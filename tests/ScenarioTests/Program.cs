using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using EndToEndTests;
using NetTrans.Kernel;

namespace ScenarioTests;

/// <summary>
/// 场景化端到端测试：以「用户的一天」为线索，把调度、断点、限速、
/// 校验、签名库、PT 引擎下发等特性串成完整使用流程，验证它们协同工作。
/// </summary>
internal static class Program
{
    private static int _passed, _failed;
    private static readonly List<string> Failures = new();
    private static readonly object EventLock = new();

    private static async Task<int> Main()
    {
        using var server = new TestServer();

        await Run("场景1. 新用户首日：并发上限/队列补位/分类落盘/签名库", () => FirstDayAsync(server));
        await Run("场景2. 网络抖动一天：断连→重试→续传→字节精确", () => FlakyNetworkAsync(server));
        await Run("场景3. PT 用户日常：magnet 引擎下发/脱敏/签名入库", () => PtUserAsync(server));
        await Run("场景4. 混跑 + 全局限速：跨任务共享一个总闸", () => MixedTrafficAsync(server));
        await Run("场景5. 坏文件处置：校验失败→换源重下→签名入库", () => BadFileRecoveryAsync(server));
        await Run("场景6. 进程重启：断点存活→新实例续传→零重复流量", () => RestartResumeAsync(server));

        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"结果：{_passed} 通过，{_failed} 失败");
        foreach (var failure in Failures) Console.WriteLine($"  [FAIL] {failure}");
        return _failed == 0 ? 0 : 1;
    }

    // ── 场景 1：新用户首日 ────────────────────────────────────────────────
    // 五个视频任务排队，并发上限 2；完成的自动补位；文件按分类落盘；
    // 完成即签名入库。跑完这一套，「下载器开箱可用」才算成立。

    private static async Task FirstDayAsync(TestServer server)
    {
        var root = TempDir();
        var files = Enumerable.Range(1, 5)
            .Select(i => (name: $"movie-{i}.bin", data: Payload(200 * 1024 + i * 4096)))
            .ToList();
        foreach (var (name, data) in files) server.Map($"day1/{name}", server.RangeFile(data, etag: $"\"{name}\""));

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 2,
            ConnectionsPerTask = 3,
            DefaultSavePath = root,
            FoldersByCategory = true,
            HashDatabasePath = Path.Combine(root, "hashes.json"),
        });

        var tasks = files.Select((f, i) => kernel.Add(new NewDownloadRequest(
                server.Base + $"day1/{f.name}", "", "video", 3,
                i < 2 ? TaskPriority.High : TaskPriority.Normal, StartNow: true)))
            .ToList();

        // 并发天花板：整个生命周期内同时在跑的 never 超过 2
        int maxRunning = 0;
        await WaitAsync(() =>
        {
            int running = tasks.Count(t => t.Status == DownloadStatus.Downloading);
            maxRunning = Math.Max(maxRunning, running);
            return tasks.All(t => t.Status is DownloadStatus.Completed or DownloadStatus.Error);
        }, "五个任务都应到达终态", 60_000);

        Equal(5, tasks.Count(t => t.Status == DownloadStatus.Completed), "五个任务全部完成");
        That(maxRunning is > 0 and <= 2, $"并发峰值应 ≤ 2（实际 {maxRunning}）");

        // 分类落盘：video 分类 → <默认目录>/video/<文件名>
        foreach (var (name, data) in files)
        {
            var path = Path.Combine(root, "video", name);
            That(File.Exists(path), $"文件应按分类落盘：video/{name}");
            That(File.ReadAllBytes(path).SequenceEqual(data), $"字节精确：{name}");
        }

        // 签名库：五个 URL 全部入库
        var db = File.ReadAllText(Path.Combine(root, "hashes.json"));
        foreach (var (name, _) in files)
            That(db.Contains(server.Base + $"day1/{name}"), $"签名库应记录 {name}");
    }

    // ── 场景 2：网络抖动一天 ──────────────────────────────────────────────
    // 第一次响应被腰斩、第二次直接 500——真实移动网络的日常。
    // 内核要自己扛住：重试、续传、最终字节精确。

    private static async Task FlakyNetworkAsync(TestServer server)
    {
        var data = Payload(1536 * 1024);
        int gets = 0;
        server.Map("flaky/file.bin", server.RangeFile(data, etag: "\"flaky\"", onGet: (n, ctx) =>
        {
            lock (EventLock) gets = Math.Max(gets, n);
            if (n == 1)
            {
                // 响应到一半把连接掐了
                var response = ctx.Response;
                response.StatusCode = 200;
                response.Headers["Accept-Ranges"] = "bytes";
                response.ContentType = "application/octet-stream";
                response.ContentLength64 = data.Length;
                response.OutputStream.Write(data, 0, data.Length / 2);
                try { response.OutputStream.Close(); } catch { /* 已断 */ }
                return true;
            }
            if (n == 2)
            {
                TestServer.Reply(ctx.Response, 500, Array.Empty<byte>());
                return true;
            }
            return false;
        }));

        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "flaky/file.bin", TempDir(), "", 1,
            TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "抖动网络下任务最终应完成", 60_000);

        var saved = Path.Combine(task.SavePath, "file.bin");
        That(File.Exists(saved), $"文件应存在（{saved}）");
        That(File.ReadAllBytes(saved).SequenceEqual(data), "跨两次断连后字节仍精确");
        lock (EventLock)
            That(gets >= 3, $"服务器应看到多次尝试（实际 {gets} 次 GET）");
    }

    // ── 场景 3：PT 用户日常 ───────────────────────────────────────────────
    // magnet 带私有 passkey：引擎必须拿到原文，日志/展示必须看不到；
    // 做种参数进命令模板；引擎产出的文件照常过校验并进签名库。

    private static async Task PtUserAsync(TestServer server)
    {
        const string secret = "DAY3-SECRET-passkey";
        var magnet = "magnet:?xt=urn:btih:aaaabbbbccccddddeeeeffff0000111122223333&dn=pt.day3.iso" +
                     $"&tr=https%3a%2f%2fpt.example%2fannounce%3fpasskey%3d{Uri.EscapeDataString(secret)}";
        var root = TempDir();
        var dbPath = Path.Combine(root, "hashes.json");

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            DefaultSavePath = root,
            FoldersByCategory = true,
            HashDatabasePath = dbPath,
            ExternalEngineCommand = "printf '%s' '{url}' > '{path}'",
        });
        var task = kernel.Add(new NewDownloadRequest(magnet, "", "bt", 1, TaskPriority.Normal,
            StartNow: true, SeedRatio: 2.0, SeedHours: 72));
        await WaitAsync(() => task.Status is DownloadStatus.Completed or DownloadStatus.Error,
            "引擎命令任务应有终态", 60_000);
        Equal(DownloadStatus.Completed, task.Status, $"引擎产出文件应完成（{task.ErrorMessage}）");

        // 数据面：引擎拿到真实 passkey
        var produced = Path.Combine(root, "bt", "pt.day3.iso");
        That(File.Exists(produced), $"bt 分类下应有引擎产出：{produced}");
        That(File.ReadAllText(produced).Contains(secret), "引擎必须拿到含真实 passkey 的 magnet 原文");

        // 展示面：任何 UI 可见字段与日志都不得出现 passkey
        Equal(magnet, task.Url, "原始 magnet 只留给引擎下发");
        That(task.DisplayUrl is not null && task.DisplayUrl.Contains("***") && !task.DisplayUrl.Contains(secret),
            "展示链接必须脱敏");
        That(task.Trackers is { Count: 1 } && task.Trackers[0].Contains("***"), "tracker 应脱敏");
        That(task.Log.All(l => !l.Message.Contains(secret)), $"全部日志不得含 passkey（{string.Join(" | ", task.Log.Select(l => l.Message))}）");

        // 引擎产出照常过完整性链路：签名入库
        var db = File.ReadAllText(dbPath);
        That(db.Contains("aaaabbbbccccddddeeeeffff0000111122223333".ToLowerInvariant()) ||
             db.Contains("urn:btih") || db.Contains("magnet:"), "签名库应记录该 magnet 任务");
        Equal("SHA-256 已校验", task.Checksum, "引擎产出文件应完成 SHA-256 自检");
    }

    // ── 场景 4：混跑 + 全局限速 ───────────────────────────────────────────
    // 两个多分段任务同时跑，全局总闸 150 KB/s。修过的 bug 是「各自 150」
    // 合计 300——这里用总时长抓回归：600KB/150KBps ≈ 4s，共享桶才对。

    private static async Task MixedTrafficAsync(TestServer server)
    {
        var a = Payload(300 * 1024);
        var b = Payload(300 * 1024, 0x5A);
        server.Map("mix/a.bin", server.RangeFile(a, etag: "\"mix-a\""));
        server.Map("mix/b.bin", server.RangeFile(b, etag: "\"mix-b\""));

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 4,
            MaxSimultaneous = 2,
            GlobalSpeedLimit = 150 * 1024,
        });
        var watch = Stopwatch.StartNew();
        var taskA = kernel.Add(new NewDownloadRequest(server.Base + "mix/a.bin", TempDir(), "", 4, TaskPriority.Normal, StartNow: true));
        var taskB = kernel.Add(new NewDownloadRequest(server.Base + "mix/b.bin", TempDir(), "", 4, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => taskA.Status == DownloadStatus.Completed && taskB.Status == DownloadStatus.Completed,
            "两个任务都应完成", 90_000);
        watch.Stop();

        That(watch.ElapsedMilliseconds >= 3000,
            $"共享限速下 600KB @150KB/s 应 ≥3s（实际 {watch.ElapsedMilliseconds}ms——若过快说明各任务独立限速）");
        That(File.ReadAllBytes(Path.Combine(taskA.SavePath, "a.bin")).SequenceEqual(a), "A 字节精确");
        That(File.ReadAllBytes(Path.Combine(taskB.SavePath, "b.bin")).SequenceEqual(b), "B 字节精确");
    }

    // ── 场景 5：坏文件处置 ───────────────────────────────────────────────
    // 发布页抄来的哈希和实收不符：任务必须失败而不是把坏文件当完成；
    // 用户换源重下（服务器内容已修正）→ 完成 + 校验通过 + 签名入库。

    private static async Task BadFileRecoveryAsync(TestServer server)
    {
        var bad = Payload(256 * 1024);
        var good = Payload(256 * 1024, 0xA5);
        server.Map("day5/app.bin", server.RangeFile(bad, etag: "\"day5-bad\""));
        var goodHash = Convert.ToHexString(SHA256.HashData(good)).ToLowerInvariant();

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 2,
            MaxSimultaneous = 1,
            HashDatabasePath = Path.Combine(TempDir(), "hashes.json"),
        });

        var task = kernel.Add(new NewDownloadRequest(server.Base + "day5/app.bin", TempDir(), "", 2,
            TaskPriority.Normal, StartNow: true, ExpectedHash: goodHash));
        await WaitAsync(() => task.Status == DownloadStatus.Error, "坏文件必须判 Error", 60_000);
        That(task.ErrorMessage!.Contains("校验失败"), "错误信息应说明校验失败");
        Equal("SHA-256 校验失败", task.Checksum, "Checksum 应标注失败");

        // 换源修正后重下（Redownload 复用同一任务对象）
        server.Map("day5/app.bin", server.RangeFile(good, etag: "\"day5-good\""));
        kernel.Redownload(task.Id);
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "修正后重下应完成", 60_000);
        That(File.ReadAllBytes(Path.Combine(task.SavePath, "app.bin")).SequenceEqual(good), "重下文件字节精确");
        // Status 先于校验翻转（内核完成即报 Completed，Checksum 稍后标注）：等待校验信号
        await WaitAsync(() => task.Checksum == "SHA-256 已校验", "重下应校验通过", 15_000);
        That(task.Log.Any(l => l.Message.Contains("已记录签名")), "重下完成应记录签名");
    }

    // ── 场景 6：进程重启 ─────────────────────────────────────────────────
    // 下载过半「进程退出」（Dispose 保留 checkpoint），新实例接手：
    // 必须续传而不是从零开始，且最终字节精确。

    private static async Task RestartResumeAsync(TestServer server)
    {
        // 断点续传是大文件直写（DirectSink）语义：载荷必须超过小文件内存缓冲阈值（默认 4MB）
        var data = Payload(6 * 1024 * 1024);
        var resumedStarts = new List<long>();
        server.Map("restart/big.bin", server.RangeFile(data, etag: "\"restart\"", onGet: (n, ctx) =>
        {
            var range = ctx.Request.Headers["Range"];
            if (range is not null && long.TryParse(range["bytes=".Length..].Split('-')[0], out var start) && start > 0)
                lock (resumedStarts) resumedStarts.Add(start);
            return false;
        }));

        var saveDir = TempDir();
        var options = new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 };

        long firstDone;
        using (var kernel1 = new RealDownloadKernel(options))
        {
            var task = kernel1.Add(new NewDownloadRequest(server.Base + "restart/big.bin", saveDir, "", 1,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Done > 512 * 1024, "第一阶段应下载过 512KB", 60_000);
            firstDone = task.Done;
        } // 进程退出：checkpoint 已周期性落盘

        That(firstDone > 0, "退出前应有实际进度");

        using (var kernel2 = new RealDownloadKernel(options))
        {
            var task2 = kernel2.Add(new NewDownloadRequest(server.Base + "restart/big.bin", saveDir, "", 1,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task2.Status == DownloadStatus.Completed, "重启后任务应完成", 90_000);
            That(File.ReadAllBytes(Path.Combine(task2.SavePath, "big.bin")).SequenceEqual(data), "重启续传后字节精确");
            lock (resumedStarts)
                That(resumedStarts.Count > 0 && resumedStarts.Min() > 0,
                    $"应有从 >0 偏移续传的请求（实际 {resumedStarts.Count} 个续传请求）");
        }
    }

    // ── 基础设施 ──────────────────────────────────────────────────────────

    private static async Task Run(string name, Func<Task> body)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await body();
            Interlocked.Increment(ref _passed);
            Console.WriteLine($"[PASS] {name}（{watch.Elapsed.TotalSeconds:0.0}s）");
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref _failed);
            Failures.Add($"{name} :: {error.Message}");
            Console.WriteLine($"[FAIL] {name}（{watch.Elapsed.TotalSeconds:0.0}s）");
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string what, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"等待超时：{what}");
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nettrans-scenario-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>确定性载荷：同 size/seed 恒定，便于字节精确断言。</summary>
    private static byte[] Payload(int size, int seed = 1)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(i * 31 + seed * 7);
        return data;
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}：期望 {expected}，实际 {actual}");
    }

    private static void That(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }
}
