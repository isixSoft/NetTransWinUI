using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using NetTrans.Kernel;
using NetTrans.Kernel.Net;
using NetTrans.Kernel.Protocol;
using NetTrans.Kernel.Protocol.Drivers;
using NetTrans.Kernel.Transfer;
namespace NetworkTests;

internal sealed partial class Program
{
    // ── 3. HTTP 重试 ──────────────────────────────────────────────────────

    private static async Task RetryOn500Async(TestServer server)
    {
        var data = Payload(4 * 1024 * 1024);
        server.Map("retry/flaky.bin", server.RangeFile(data, etag: "\"flaky-1\"", onGet: (n, ctx) =>
        {
            if (n != 2) return false; // 仅第 2 次 GET 注入 500，之后恢复
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentLength64 = 0;
            ctx.Response.Close();
            return true;
        }));

        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 });
        var completed = NewSignal();
        kernel.TaskCompleted += _ => completed.TrySetResult();
        var task = kernel.Add(new NewDownloadRequest(server.Base + "retry/flaky.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));

        await WaitSignalAsync(completed.Task, "任务应在 60s 内完成（500 后重试）", 60_000);
        Equal(DownloadStatus.Completed, task.Status, "任务状态应为 Completed");
        var path = TargetPath(dir, task);
        Equal(Hash(data), Hash(File.ReadAllBytes(path)), "重试后文件字节应完整");
        That(task.Log.Any(l => l.Message.Contains("重试")), "日志应记录到重试（连接中断，第 1 次重试）");
    }

    // ── 4. 镜像中途失效 ───────────────────────────────────────────────────

    private static async Task MirrorDropsAsync(TestServer server)
    {
        var data = Payload(4 * 1024 * 1024);
        server.Map("mirror/main.bin", server.RangeFile(data, etag: "\"mirror-1\""));

        int mirrorDrops = 0;
        server.Map("mirror/copy.bin", ctx =>
        {
            if (ctx.Request.HttpMethod == "HEAD")
            {
                TestServer.ServeHead(ctx, data, etag: "\"mirror-1\"");
                return;
            }

            if (Interlocked.Increment(ref mirrorDrops) <= 2)
            {
                // 模拟中途失效：发 206 响应头 + 一小段正文后强制断开连接
                var response = ctx.Response;
                long start = 0;
                var range = ctx.Request.Headers["Range"];
                if (range is not null && range.StartsWith("bytes=") && long.TryParse(range[6..].Split('-')[0], out var s))
                    start = Math.Clamp(s, 0, data.Length - 1);
                response.StatusCode = 206;
                response.Headers["Content-Range"] = $"bytes {start}-{data.Length - 1}/{data.Length}";
                response.ContentLength64 = data.Length - start;
                response.OutputStream.Write(data, (int)start, Math.Min(16 * 1024, (int)(data.Length - start)));
                try { response.OutputStream.Flush(); } catch { /* 断开在即 */ }
                response.Abort();
                return;
            }

            TestServer.ServeRange(ctx, data, etag: "\"mirror-1\"");
        });

        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 4,
            MaxSimultaneous = 1,
            MirrorResolver = (_, _) => Task.FromResult<IReadOnlyList<string>>(new[] { server.Base + "mirror/copy.bin" }),
        });
        var completed = NewSignal();
        kernel.TaskCompleted += _ => completed.TrySetResult();
        var task = kernel.Add(new NewDownloadRequest(server.Base + "mirror/main.bin", dir, "", 4, TaskPriority.Normal, StartNow: true));

        await WaitSignalAsync(completed.Task, "镜像断开后任务仍应完成", 90_000);
        Equal(DownloadStatus.Completed, task.Status, "任务状态应为 Completed");
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(dir, task))), "镜像失效降级后字节应完整");
        That(Interlocked.CompareExchange(ref mirrorDrops, 0, 0) >= 1, "镜像源应实际被请求并被断开");
        That(task.Log.Any(l => l.Message.Contains("已启用 1 个镜像源")), "日志应记录镜像启用");
    }

    // ── 5. 限速 ───────────────────────────────────────────────────────────

    private static async Task GlobalLimitWinsAsync(TestServer server)
    {
        var data = Payload(4 * 1024 * 1024);
        server.Map("limit/global.bin", server.RangeFile(data));
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            GlobalSpeedLimit = 1024 * 1024, // A = 1 MiB/s
            ConnectionsPerTask = 1,
            MaxSimultaneous = 1,
        });
        var completed = NewSignal();
        kernel.TaskCompleted += _ => completed.TrySetResult();
        var sw = Stopwatch.StartNew();
        var task = kernel.Add(new NewDownloadRequest(server.Base + "limit/global.bin", dir, "", 1, TaskPriority.Normal, StartNow: true));
        kernel.SetSpeedLimit(task.Id, "4 MB/s"); // B = 4 MiB/s > A：实际应按全局 A 节奏

        await WaitSignalAsync(completed.Task, "任务应完成", 60_000);
        sw.Stop();
        Equal(DownloadStatus.Completed, task.Status, "任务状态应为 Completed");
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(dir, task))), "限速下字节应完整");
        That(sw.Elapsed.TotalSeconds >= 3.2,
            $"4 MiB @ 全局 1 MiB/s 应耗时 ≥3.2s（单任务 4 MB/s 不应生效），实际 {sw.Elapsed.TotalSeconds:0.00}s");
        That(sw.Elapsed.TotalSeconds <= 30, $"限速节奏不应异常缓慢，实际 {sw.Elapsed.TotalSeconds:0.00}s");
    }

    private static async Task UnlimitMidwayAsync(TestServer server)
    {
        var data = Payload(2 * 1024 * 1024);
        server.Map("limit/unlim.bin", server.RangeFile(data));
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 });
        var completed = NewSignal();
        kernel.TaskCompleted += _ => completed.TrySetResult();

        var task = kernel.Add(new NewDownloadRequest(server.Base + "limit/unlim.bin", dir, "", 1, TaskPriority.Normal, StartNow: false));
        kernel.SetSpeedLimit(task.Id, "512 KB/s");
        kernel.Toggle(task.Id);

        var sw = Stopwatch.StartNew();
        await WaitAsync(() => task.Done > 0, "限速阶段应出现进度", 10_000);
        var limitedElapsed = sw.Elapsed.TotalSeconds;
        That(limitedElapsed >= 0.4, $"解除前限速阶段应已持续 ≥0.4s，实际 {limitedElapsed:0.00}s");
        kernel.SetSpeedLimit(task.Id, "不限"); // ParseLimit → null → 全局也未设 → 解除限速
        await WaitSignalAsync(completed.Task, "解除限速后任务应完成", 30_000);
        sw.Stop();

        Equal(DownloadStatus.Completed, task.Status, "任务状态应为 Completed");
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(dir, task))), "字节应完整");
        That(sw.Elapsed.TotalSeconds < 3.0,
            $"中途解除后 2 MiB 总耗时应远小于全程 512 KB/s（≥4s），实际 {sw.Elapsed.TotalSeconds:0.00}s");
    }

    private static async Task LimiterUnitAsync()
    {
        var limiter = new SpeedLimiter(512 * 1024);
        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync(512 * 1024); // 512 KiB @ 512 KiB/s ≈ 1s
        sw.Stop();
        That(sw.Elapsed.TotalMilliseconds >= 800,
            $"限速 512 KiB/s 下取 512 KiB 应 ≥800ms，实际 {sw.Elapsed.TotalMilliseconds:0}ms");

        limiter.SetLimit(0); // 解除（0/null 同义），应立即放行
        sw.Restart();
        await limiter.WaitAsync(512 * 1024);
        sw.Stop();
        That(sw.Elapsed.TotalMilliseconds < 100,
            $"SetLimit(0) 后应立即放行，实际 {sw.Elapsed.TotalMilliseconds:0}ms");
    }

    // ── 27. 磁盘空间预检（P1-3） ──────────────────────────────────────────

    private static async Task DiskSpaceBlockedAsync(TestServer server)
    {
        var data = Payload(64 * 1024);
        server.Map("space/blocked.bin", server.RangeFile(data, etag: "\"space-1\""));
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 1,
            MaxSimultaneous = 1,
            FreeSpaceProbe = _ => 1024, // 注入：仅剩 1 KiB，而资源 64 KiB
        });

        var task = kernel.Add(new NewDownloadRequest(server.Base + "space/blocked.bin", dir, "", 1,
            TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => task.Status == DownloadStatus.Error, "空间不足任务应进入 Error（不进运行态下载）", 30_000);
        That(task.ErrorMessage is not null && task.ErrorMessage.Contains("需要") && task.ErrorMessage.Contains("剩余"),
            $"错误文案应含「需要/剩余」（实际「{task.ErrorMessage}」）");
        That(task.Size == data.Length, $"错误前探测出的资源大小应落在任务上（实际 {task.Size}）");
        That(!File.Exists(TargetPath(dir, task)), "空间不足不应写出目标文件");
    }

    private static async Task DiskSpaceRecoveryAsync(TestServer server)
    {
        var data = Payload(64 * 1024);
        server.Map("space/recover.bin", server.RangeFile(data, etag: "\"space-2\""));
        long free = 1024; // 闭包：先不足，后释放
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 1,
            MaxSimultaneous = 1,
            FreeSpaceProbe = _ => free,
        });

        var task = kernel.Add(new NewDownloadRequest(server.Base + "space/recover.bin", dir, "", 1,
            TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => task.Status == DownloadStatus.Error, "空间不足应先进入 Error", 30_000);

        free = data.Length * 4; // 模拟释放空间；900ms tick 应自动重新排队
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "释放空间后应自动恢复并完成", 30_000);
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(dir, task))), "恢复下载的字节应完整");
        That(task.ErrorMessage is null, "自动恢复后错误文案应清空");
    }

}
