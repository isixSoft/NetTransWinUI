using NetTrans.Kernel;

namespace FtpE2ETests;

/// <summary>
/// NetTrans.Kernel FTP / SOCKS5 端到端测试：自建最小 FTP 服务器与最小 SOCKS5
/// 中继代理，走 RealDownloadKernel 的两条「真传输」路径，共 5 个方向：
///   1. FTP 全流程：SIZE 探测 → PASV 单流 RETR → 字节精确 +「FTP 传输就绪」
///   2. FTP 大小未知：SIZE 被拒 → 未知大小流式下载仍完成
///   3. SOCKS5 认证代理：6 连接并发多分段经代理中继到本地 HTTP 源站
///   4. SOCKS5 匿名代理：无 userinfo 问候（NO-AUTH）+ 字节精确中继
///   5. SOCKS5 错误密码：代理拒绝认证 → 任务 Error 且不挂死
/// </summary>
internal static class Program
{
    private const int MiB = 1024 * 1024;

    private static int _passed, _failed;
    private static readonly List<string> _failures = new();
    private static readonly List<string> _roots = new();

    private static async Task<int> Main()
    {
        Console.WriteLine("NetTrans.Kernel FTP / SOCKS5 端到端测试");
        Console.WriteLine(new string('=', 64));
        Console.WriteLine();

        await RunAsync("1. FTP 全流程：SIZE 探测 → RETR 单流 → 字节精确", () => FtpFullFlowAsync());
        await RunAsync("2. FTP 大小未知：SIZE 被拒 → 流式下载完成", () => FtpUnknownSizeAsync());
        await RunAsync("3. SOCKS5 认证代理：6 连接并发多分段经代理", () => Socks5AuthMultiSegmentAsync());
        await RunAsync("4. SOCKS5 匿名代理：NO-AUTH 问候 + 中继", () => Socks5NoAuthAsync());
        await RunAsync("5. SOCKS5 错误密码：拒绝认证 → Error 不挂死", () => Socks5WrongPasswordAsync());

        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"结果：{_passed} 通过，{_failed} 失败");
        foreach (var item in _failures) Console.WriteLine("  [FAIL] " + item);

        CleanupRoots();
        return _failed == 0 ? 0 : 1;
    }

    // ── 1. FTP 全流程 ─────────────────────────────────────────────────────

    private static async Task FtpFullFlowAsync()
    {
        var data = Payload(2 * MiB);
        using var ftp = new MiniFtpServer();
        ftp.AddFile("/files/big.bin", data);
        var dir = TempDir("ftp");

        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 4, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(
            $"ftp://127.0.0.1:{ftp.Port}/files/big.bin", dir, "soft", 4, TaskPriority.Normal, StartNow: true));
        await WaitCompletedAsync(kernel, task, "FTP 任务");

        var path = Path.Combine(dir, "big.bin");
        That(task.Status == DownloadStatus.Completed, "任务状态应为 Completed");
        That(task.Log.Any(l => l.Message.Contains("FTP 探测完成")), "日志应包含 SIZE 探测成功（FTP 探测完成：N 字节）");
        That(task.Log.Any(l => l.Message.Contains("FTP 传输就绪")), "日志应包含「FTP 传输就绪」");
        That(File.Exists(path), "目标文件应落在保存目录");
        That(File.ReadAllBytes(path).AsSpan().SequenceEqual(data), "文件字节应与 FTP 源完全一致");
        That(task.Size == data.Length && task.Done == task.Size, $"Size/Done 应到满（实际 {task.Done}/{task.Size}）");
        That(task.Blocks.Length == 96 && task.Blocks.All(b => b == 1), "完成后 96 块应全部点亮");
        That(task.Log.Any(l => l.Message.StartsWith("SHA-256 ")), "日志应出现 SHA-256 校验行");
        That(ftp.SawCommand("USER") && ftp.SawCommand("PASS"), "FTP 服务器应收到 USER/PASS 登录");
        That(ftp.SawCommand("SIZE") && ftp.SawCommand("PASV") && ftp.SawCommand("RETR"),
            "FTP 服务器应收到 SIZE/PASV/RETR 命令");
    }

    // ── 2. FTP 大小未知 ───────────────────────────────────────────────────

    private static async Task FtpUnknownSizeAsync()
    {
        var data = Payload(1 * MiB, seed: 5);
        using var ftp = new MiniFtpServer { RejectSize = true };
        ftp.AddFile("/files/unknown.bin", data);
        var dir = TempDir("ftp-unknown");

        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 4, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(
            $"ftp://127.0.0.1:{ftp.Port}/files/unknown.bin", dir, "soft", 4, TaskPriority.Normal, StartNow: true));
        await WaitCompletedAsync(kernel, task, "FTP 未知大小任务");

        var path = Path.Combine(dir, "unknown.bin");
        That(task.Status == DownloadStatus.Completed, "任务状态应为 Completed");
        That(task.Log.Any(l => l.Message.Contains("FTP 大小探测失败")), "日志应记录 SIZE 探测失败");
        That(task.Log.Any(l => l.Message.Contains("未报告文件大小")), "日志应说明按未知大小流式下载");
        That(task.Log.Any(l => l.Message.Contains("FTP 传输就绪")), "日志应包含「FTP 传输就绪」");
        That(File.Exists(path), "目标文件应落在保存目录");
        That(File.ReadAllBytes(path).AsSpan().SequenceEqual(data), "流式下载的字节应与 FTP 源完全一致");
        That(task.Size == data.Length && task.Done == task.Size,
            $"完成后 Size 应回填实际字节数（实际 {task.Size}，Done {task.Done}）");
        That(task.Blocks.Length == 96 && task.Blocks.All(b => b == 1), "完成后 96 块应全部点亮");
        That(ftp.SawCommand("SIZE") && ftp.SawCommand("RETR"), "FTP 服务器仍应收到 SIZE 与 RETR");
    }

    // ── 3. SOCKS5 认证代理：多分段并发 ────────────────────────────────────

    private static async Task Socks5AuthMultiSegmentAsync()
    {
        var data = Payload(6 * MiB, seed: 3);
        using var server = new TestServer();
        server.Map("e2e/socks-multi.bin", server.RangeFile(data, etag: "\"socks-multi-1\""));
        using var proxy = new Socks5TestProxy(requireAuth: true, validUser: "user", validPassword: "pass");
        var dir = TempDir("socks-auth");

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            Proxy = $"socks5://user:pass@127.0.0.1:{proxy.Port}",
            ConnectionsPerTask = 6,
            MaxSimultaneous = 1,
        });
        var task = kernel.Add(new NewDownloadRequest(
            $"{server.Base}e2e/socks-multi.bin", dir, "soft", 6, TaskPriority.Normal, StartNow: true));
        await WaitCompletedAsync(kernel, task, "SOCKS5 多分段任务");

        var path = Path.Combine(dir, "socks-multi.bin");
        That(task.Status == DownloadStatus.Completed, "任务状态应为 Completed");
        That(task.Log.Any(l => l.Message.Contains("HTTP 多分段就绪")), "日志应报告多分段计划");
        That(File.Exists(path), "目标文件应落在保存目录");
        That(File.ReadAllBytes(path).AsSpan().SequenceEqual(data), "经代理下载的文件字节应与源站完全一致");
        That(task.Size == data.Length && task.Done == task.Size, $"Size/Done 应到满（实际 {task.Done}/{task.Size}）");
        That(task.Blocks.Length == 96 && task.Blocks.All(b => b == 1), "完成后 96 块应全部点亮");

        That(proxy.Auths.Length > 0, "代理应记录到至少一次 RFC1929 认证");
        That(proxy.Auths.All(a => a.Ok && a.User == "user" && a.Password == "pass"),
            $"全部认证应以正确凭据通过（实际 {Describe(proxy.Auths)}）");
        That(proxy.Connects.Length >= 4, $"多分段应经代理开出多路 CONNECT（实际 {proxy.Connects.Length}）");
        That(proxy.Connects.All(c => c.Host == "127.0.0.1" && c.Port == server.Port),
            "每条 CONNECT 都应指向本地 TestServer 端口");
    }

    // ── 4. SOCKS5 匿名代理 ────────────────────────────────────────────────

    private static async Task Socks5NoAuthAsync()
    {
        var data = Payload(1 * MiB, seed: 9);
        using var server = new TestServer();
        server.Map("e2e/socks-anon.bin", server.RangeFile(data));
        using var proxy = new Socks5TestProxy(requireAuth: false);
        var dir = TempDir("socks-anon");

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            Proxy = $"socks5://127.0.0.1:{proxy.Port}",
            ConnectionsPerTask = 3,
            MaxSimultaneous = 1,
        });
        var task = kernel.Add(new NewDownloadRequest(
            $"{server.Base}e2e/socks-anon.bin", dir, "soft", 3, TaskPriority.Normal, StartNow: true));
        await WaitCompletedAsync(kernel, task, "SOCKS5 匿名任务");

        var path = Path.Combine(dir, "socks-anon.bin");
        That(task.Status == DownloadStatus.Completed, "任务状态应为 Completed");
        That(File.ReadAllBytes(path).AsSpan().SequenceEqual(data), "经匿名代理下载的字节应与源站完全一致");
        That(proxy.Auths.Length == 0, "匿名代理不应触发 RFC1929 子协商");
        That(proxy.Connects.Length >= 1, "代理应记录到 CONNECT");
        That(proxy.Connects.All(c => c.Host == "127.0.0.1" && c.Port == server.Port),
            "CONNECT 应指向本地 TestServer 端口");
    }

    // ── 5. SOCKS5 错误密码 ────────────────────────────────────────────────

    private static async Task Socks5WrongPasswordAsync()
    {
        var data = Payload(64 * 1024, seed: 13);
        using var server = new TestServer();
        server.Map("e2e/socks-denied.bin", server.RangeFile(data));
        using var proxy = new Socks5TestProxy(requireAuth: true, validUser: "user", validPassword: "pass");
        var dir = TempDir("socks-badpass");

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            Proxy = $"socks5://user:wrongpass@127.0.0.1:{proxy.Port}",
            ConnectionsPerTask = 4,
            MaxSimultaneous = 1,
            RetryCount = 0, // 认证失败应直接判死，不拖重试
        });
        var task = kernel.Add(new NewDownloadRequest(
            $"{server.Base}e2e/socks-denied.bin", dir, "soft", 4, TaskPriority.Normal, StartNow: true));
        await WaitUntilAsync(
            () => task.Status is DownloadStatus.Error or DownloadStatus.Completed,
            TimeSpan.FromSeconds(30), "任务应在认证被拒后快速终态（不挂死）", kernel);

        That(task.Status == DownloadStatus.Error, "任务应进入 Error 而不是完成");
        That(task.ErrorMessage is not null, "任务应携带失败信息");
        That(task.Log.Any(l => l.IsError && l.Message.Contains("下载失败")), "日志应有错误级「下载失败」行");
        That(task.Log.Any(l => l.Message.Contains("HEAD 探测失败")), "日志应记录探测阶段已失败");
        That(!File.Exists(Path.Combine(dir, "socks-denied.bin")), "失败任务不应产出文件");

        That(proxy.Auths.Any(a => !a.Ok && a.User == "user" && a.Password == "wrongpass"),
            "代理应记录到错误密码的认证尝试并被拒");
        That(proxy.Connects.Length == 0, "认证未通过时不应有任何 CONNECT 到达");
    }

    // ── 基础设施 ──────────────────────────────────────────────────────────

    private static async Task RunAsync(string name, Func<Task> body)
    {
        try
        {
            await body();
            Interlocked.Increment(ref _passed);
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref _failed);
            _failures.Add($"{name} :: {error.Message}");
            Console.WriteLine($"[FAIL] {name}");
            Console.WriteLine($"       {error.Message}");
        }
    }

    private static void That(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }

    private static string Describe(Socks5TestProxy.AuthRecord[] records) =>
        records.Length == 0 ? "无记录" : string.Join("；", records.Select(r => $"{r.User}/{r.Password} ok={r.Ok}"));

    /// <summary>Deterministic payload; different seeds produce different bytes.</summary>
    private static byte[] Payload(int size, byte seed = 0)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)((i * 31 + seed) % 251);
        return data;
    }

    private static string TempDir(string label)
    {
        var root = Path.Combine(Path.GetTempPath(), "nettrans-ftp-e2e-" + Guid.NewGuid().ToString("N")[..10]);
        _roots.Add(root);
        var dir = Path.Combine(root, label);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupRoots()
    {
        foreach (var root in _roots)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                    break;
                }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { break; }
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what, RealDownloadKernel? kernel = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        if (kernel is not null) DumpKernel(kernel);
        throw new Exception($"等待超时（{timeout.TotalSeconds:0}s）：{what}");
    }

    private static void DumpKernel(RealDownloadKernel kernel)
    {
        foreach (var task in kernel.Tasks)
        {
            Console.WriteLine($"  ---- task #{task.Id} {task.Name} 状态 {task.Status} Done={task.Done} ----");
            foreach (var entry in task.Log) Console.WriteLine($"  [{entry.Time}] {entry.Message}");
        }
    }

    private static async Task WaitCompletedAsync(RealDownloadKernel kernel, DownloadTask task, string label)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var current = kernel.Tasks.FirstOrDefault(t => t.Id == task.Id);
            if (current is { Status: DownloadStatus.Completed }) return;
            if (current is { Status: DownloadStatus.Error }) break; // 提前失败：立刻报告而不是等满超时
            await Task.Delay(100);
        }

        var stuck = kernel.Tasks.FirstOrDefault(t => t.Id == task.Id);
        Console.WriteLine($"  ---- task #{task.Id} 日志（状态 {stuck?.Status}，错误 {stuck?.ErrorMessage}）----");
        foreach (var entry in stuck?.Log ?? Enumerable.Empty<LogEntry>()) Console.WriteLine($"  [{entry.Time}] {entry.Message}");
        throw new Exception($"等待失败：{label} 未完成（状态 {stuck?.Status}）");
    }
}
