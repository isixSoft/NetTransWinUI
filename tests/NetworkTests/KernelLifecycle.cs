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
    // ── 9. Remove 中途取消 ────────────────────────────────────────────────

    private static async Task RemoveMidwayAsync(TestServer server)
    {
        var data = Payload(8 * 1024 * 1024);
        server.Map("cancel/slow.bin", server.RangeFile(data));
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 4, MaxSimultaneous = 1 });
        var removed = NewSignal();
        kernel.TaskRemoved += _ => removed.TrySetResult();

        var task = kernel.Add(new NewDownloadRequest(server.Base + "cancel/slow.bin", dir, "", 4, TaskPriority.Normal, StartNow: true));
        kernel.SetSpeedLimit(task.Id, "128 KB/s"); // 拖慢下载，保证中途取消窗口
        await WaitAsync(() => task.Done > 0, "取消前下载应有进度", 15_000);

        kernel.Remove(new[] { task.Id });
        var finished = await Task.WhenAny(removed.Task, Task.Delay(5_000));
        That(finished == removed.Task, "Remove 应触发 TaskRemoved 事件");
        That(!kernel.Tasks.Any(t => t.Id == task.Id), "任务应从任务列表移除");

        // 句柄释放判据：目标文件最终可被再次删除（或已被内核清理）
        var path = TargetPath(dir, task);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var released = false;
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(path)) { released = true; break; }
            try { File.Delete(path); released = true; break; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(100);
        }
        That(released, "下载中 Remove 后文件句柄应释放（File.Delete 成功或文件已被清理）");
    }

    // ── 10. Redownload ────────────────────────────────────────────────────

    private static async Task RedownloadAsync(TestServer server)
    {
        var data = Payload(256 * 1024);
        server.Map("redo/data.bin", server.RangeFile(data));
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 });
        int completions = 0;
        kernel.TaskCompleted += _ => Interlocked.Increment(ref completions);

        var task = kernel.Add(new NewDownloadRequest(server.Base + "redo/data.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "第一次下载应完成", 60_000);
        var path = TargetPath(dir, task);
        Equal(Hash(data), Hash(File.ReadAllBytes(path)), "第一次下载字节应正确");

        kernel.Redownload(task.Id);
        Equal(0L, task.Done, "Redownload 后 Done 应归零");
        That(task.Status is DownloadStatus.Queued or DownloadStatus.Downloading,
            $"Redownload 后应重新排队，实际 {task.Status}");
        await WaitAsync(() => task.Status == DownloadStatus.Completed && Volatile.Read(ref completions) >= 2,
            "重新下载应再次完成", 60_000);

        Equal(task.Size, task.Done, "重新下载完成后 Done 应再次到满");
        Equal(Hash(data), Hash(File.ReadAllBytes(path)), "重新下载字节应正确");
        That(Volatile.Read(ref completions) >= 2, "TaskCompleted 应触发两次");
        That(task.Log.Any(l => l.Message.Contains("重新开始")), "日志应出现\"重新开始\"（按新版本重新开始下载）");
    }

    // ── 11. 暂停/恢复边界 ─────────────────────────────────────────────────

    private static async Task PauseResumeEdgesAsync(TestServer server)
    {
        var data = Payload(4 * 1024 * 1024);
        server.Map("pause/edge.bin", server.RangeFile(data, etag: "\"pause-edge-1\""));
        var dir = TempDir();

        // 11a：暂停打在探测/启动的任意时刻也要安全——StartNow:true 后立刻暂停，
        // 此时 worker 可能还在探测，也可能刚开始传。
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "pause/edge.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        kernel.Toggle(task.Id); // 立刻暂停
        Equal(DownloadStatus.Paused, task.Status, "启动后立刻暂停：状态应为 Paused");
        That(task.Speed == 0, "暂停后速度应归零");

        kernel.Toggle(task.Id); // 恢复
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "探测期暂停过的任务恢复后应完成", 60_000);
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(dir, task))), "探测期暂停/恢复后字节应完整");
        That(!task.Log.Any(l => l.IsError), $"探测期暂停不应产生失败日志：{string.Join(" | ", task.Log.Where(l => l.IsError).Select(l => l.Message))}");
        That(!File.Exists(TargetPath(dir, task) + ".nettrans.json"), "完成后 checkpoint 应清除");
    }

    // ── 12. AddTask 坏 URL ────────────────────────────────────────────────

    private static async Task BadUrlAsync(TestServer server)
    {
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 4 });
        var good = Payload(64 * 1024);
        server.Map("badurl/good.bin", server.RangeFile(good, etag: "\"badurl-good\""));
        var goodDir = TempDir();

        // 各种坏 URL：任务必须优雅进入 Error（带失败信息），绝不崩内核或挂死
        foreach (var url in new[] { "not-a-url", "", "http://", "http://nonexistent.invalid/x.bin", "ed2k://|file|name|abc|HASH|/" })
        {
            var task = kernel.Add(new NewDownloadRequest(url, TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status is DownloadStatus.Error or DownloadStatus.Completed,
                $"坏 URL 「{(url.Length == 0 ? "<空>" : url)}」应到达终态", 30_000);
            Equal(DownloadStatus.Error, task.Status, $"坏 URL 「{url}」应为 Error");
            That(!string.IsNullOrWhiteSpace(task.ErrorMessage), $"坏 URL 「{url}」应带错误信息");
            That(task.Log.Any(l => l.IsError), $"坏 URL 「{url}」日志应有失败行");
        }

        // 坏 URL 风暴之后内核必须仍然健康：好任务照常完成
        var healthy = kernel.Add(new NewDownloadRequest(server.Base + "badurl/good.bin", goodDir, "", 1, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => healthy.Status == DownloadStatus.Completed, "坏 URL 之后内核应照常工作", 30_000);
        Equal(Hash(good), Hash(File.ReadAllBytes(TargetPath(goodDir, healthy))), "好任务字节应完整");
    }

    // ── 13. Remove 竞态风暴 ───────────────────────────────────────────────

    private static async Task RemoveRaceStormAsync(TestServer server)
    {
        var data = Payload(8 * 1024 * 1024);
        server.Map("race/storm.bin", server.RangeFile(data, etag: "\"race-1\""));
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 });

        var task = kernel.Add(new NewDownloadRequest(server.Base + "race/storm.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        kernel.SetSpeedLimit(task.Id, "128 KB/s");
        await WaitAsync(() => task.Done > 0, "竞态前下载应有进度", 30_000);

        // 对同一任务狂轰乱炸：Remove 之后立刻重建，反复制造 Add/Remove/Toggle/Redownload
        // 的真实重叠窗口——全部操作都在内核锁内，任何一路都不允许抛异常或把内核打挂
        var storm = Task.Run(() =>
        {
            for (int i = 0; i < 60; i++)
            {
                if (!kernel.Tasks.Any(t => t.Id == task.Id))
                {
                    task = kernel.Add(new NewDownloadRequest(server.Base + "race/storm.bin", TempDir(), "", 2, TaskPriority.Normal, StartNow: true));
                    kernel.SetSpeedLimit(task.Id, "128 KB/s");
                    continue;
                }

                kernel.Remove(new[] { task.Id });          // 幂等：不存在即跳过
                kernel.Toggle(task.Id);                    // 不存在即跳过
                if (i % 10 == 0) kernel.Redownload(task.Id);
                kernel.SetPriority(task.Id, TaskPriority.High);
                kernel.SetSpeedLimit(task.Id, i % 2 == 0 ? "不限" : "256 KB/s");
                kernel.MoveToFront(task.Id);
                kernel.MoveToBack(task.Id);
            }
        });
        kernel.ToggleAll();
        // 所有等待都是上限而非预期耗时（成功即提前返回）；慢沙箱 / TCP TIME_WAIT
        // 积压时给足余量，避免与被测内核无关的环境抖动造成偶发超时。
        await storm.WaitAsync(TimeSpan.FromSeconds(120));
        kernel.Remove(kernel.Tasks.Select(t => t.Id).ToArray()); // 双重清理 + 覆盖未知 id

        That(!kernel.Tasks.Any(t => t.Id == task.Id), "风暴后任务应不在任务列表");
        await WaitAsync(() => !kernel.Tasks.Any(), "风暴后任务列表应清空", 30_000);

        // 内核必须仍然健康：加一个新任务并完成
        var healthyDir = TempDir();
        var healthy = kernel.Add(new NewDownloadRequest(server.Base + "race/storm.bin", healthyDir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => healthy.Status == DownloadStatus.Completed, "竞态风暴后内核应照常完成新任务", 180_000);
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(healthyDir, healthy))), "风暴后新任务字节应完整");

        // 从未启动的排队任务：Remove 干净利落、不产生文件
        var queued = kernel.Add(new NewDownloadRequest(server.Base + "race/storm.bin", TempDir(), "", 2, TaskPriority.Normal, StartNow: false));
        kernel.Remove(new[] { queued.Id });
        That(!kernel.Tasks.Any(t => t.Id == queued.Id), "未启动任务应直接移除");
    }

    // ── 14. Range 续传边界 ────────────────────────────────────────────────

    private static async Task RangeResumeHeaderAsync(TestServer server)
    {
        // 断点续传是大文件直写（DirectSink）语义：载荷必须超过小文件内存缓冲阈值（默认 4MB）
        var data = Payload(6 * 1024 * 1024);
        var rangeHeaders = new List<string>();
        var gate = new object();
        server.Map("range/resume.bin", server.RangeFile(data, etag: "\"range-resume-1\"", onGet: (_, ctx) =>
        {
            var range = ctx.Request.Headers["Range"];
            if (range is not null) lock (gate) rangeHeaders.Add(range);
            return false;
        }));
        var dir = TempDir();

        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "range/resume.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        kernel.SetSpeedLimit(task.Id, "256 KB/s");
        await WaitAsync(() => task.Done >= 512 * 1024, "限速下载到 512 KB", 30_000);
        kernel.Toggle(task.Id); // 暂停 → checkpoint
        await WaitAsync(() => File.Exists(TargetPath(dir, task) + ".nettrans.json"), "暂停后 checkpoint 应落盘", 15_000);

        kernel.Toggle(task.Id); // 恢复 → 续传 GET 必须带中段 Range
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "恢复后任务应完成", 60_000);
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(dir, task))), "续传后字节应完整");

        lock (gate)
        {
            That(rangeHeaders.Any(h => h.StartsWith("bytes=") && !h.StartsWith("bytes=0-")),
                $"恢复后应发出非零起点的 Range 续传头，实际捕获：{string.Join(" ; ", rangeHeaders.Take(8))}");
        }
    }

    private static async Task RangeBetrayalAsync(TestServer server)
    {
        // 探测时宣称支持 Range（HEAD 带 Accept-Ranges），GET 却无视 Range
        // 回 200 全量：多分段会话必须明确失败（不静默把 200 正文写错位置）
        var data = Payload(4 * 1024 * 1024);
        server.Map("range/betray.bin", server.RangeFile(data, etag: "\"betray-1\"", onGet: (_, ctx) =>
        {
            if (ctx.Request.Headers["Range"] is null) return false; // 首次 GET 正常放行
            ctx.Response.StatusCode = 200;                          // 续传 GET：背叛 Range
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data);
            ctx.Response.Close();
            return true;
        }));
        var dir = TempDir();

        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1, RetryCount = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "range/betray.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => task.Status is DownloadStatus.Error or DownloadStatus.Completed,
            "Range 背叛任务应到达终态", 60_000);
        Equal(DownloadStatus.Error, task.Status, "多分段下服务器背叛 Range 应明确 Error");
        That(task.ErrorMessage!.Contains("断点续传"), $"错误信息应提及断点续传，实际「{task.ErrorMessage}」");
        That(task.Log.Any(l => l.Message.Contains("服务器不支持断点续传")), "日志应记录「服务器不支持断点续传（Range 被忽略）」");
    }

}
