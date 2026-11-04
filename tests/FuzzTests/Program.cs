using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using NetTrans.Kernel;
using NetTrans.Kernel.Transfer;

namespace FuzzTests;

/// <summary>
/// NetTrans.Kernel 故障注入模糊测试（RealDownloadKernel + 本地 TestServer），四个方向：
///   1. 断连 fuzz：随机断连 / 写一半截断 / 虚假 ContentLength / 慢响应 / 5xx，种子 12345，200 轮
///   2. 乱序/延迟 fuzz：慢响应 + 钉死的偶发 500，种子 777，100 轮（重试应全部扛住 → 必 Completed）
///   3. 损坏 checkpoint fuzz：限速下载到中段 → 暂停 → 四类损坏写入 .nettrans.json → 恢复，种子 999，60 轮
///   4. 随机种子压轴：不可预测种子混合上述故障 100 轮，失败轮打印种子；同种子重跑必须结果一致
/// 合法终态：Completed（文件必须与源站字节精确）或 Error（重试耗尽）；全程绝不崩溃、绝不挂死。
/// </summary>
internal static class Program
{
    private const int KiB = 1024;
    private const int MiB = 1024 * KiB;

    private static int _passed, _failed;
    private static readonly List<string> _failures = new();
    private static readonly List<string> _unhandled = new();
    private static readonly string _root =
        Path.Combine(Path.GetTempPath(), "nettrans-fuzz-" + Guid.NewGuid().ToString("N")[..10]);

    private static async Task<int> Main()
    {
        // 进程级哨兵：内核任何未捕获/未观察异常都记下来，作为「绝不崩溃」的断言依据
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            lock (_unhandled) _unhandled.Add(e.ExceptionObject?.ToString() ?? "(null)");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            lock (_unhandled) _unhandled.Add(e.Exception.ToString());
        };

        Console.WriteLine("NetTrans.Kernel 故障注入模糊测试（RealDownloadKernel）");
        Console.WriteLine(new string('=', 64));

        using var server = new TestServer();
        Console.WriteLine($"测试服务器：{server.Base}");
        Console.WriteLine();

        await RunAsync("1. 断连 fuzz：随机断连/截断/虚假长度/慢响应/5xx，种子 12345", () => DisconnectFuzzAsync(server));
        await RunAsync("2. 乱序/延迟 fuzz：慢响应 + 偶发 500，种子 777（应全部扛住）", () => DelayFuzzAsync(server));
        await RunAsync("3. 损坏 checkpoint fuzz：暂停 → 破坏 → 恢复，种子 999", () => CheckpointFuzzAsync(server));
        await RunAsync("4. 随机种子压轴：混合故障 + 同种子重跑确定性", () => RandomSeedFuzzAsync(server));

        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"结果：{_passed} 通过，{_failed} 失败");
        foreach (var item in _failures) Console.WriteLine("  [FAIL] " + item);

        lock (_unhandled)
        {
            Console.WriteLine(_unhandled.Count == 0
                ? "全程无未捕获异常"
                : $"未捕获异常 {_unhandled.Count} 次：\n    " + string.Join("\n    ", _unhandled.Take(5)));
        }

        CleanupRoots();
        return _failed == 0 ? 0 : 1;
    }

    // ── 1. 断连 fuzz ──────────────────────────────────────────────────────

    private static async Task DisconnectFuzzAsync(TestServer server)
    {
        const int masterSeed = 12345;
        const int rounds = 200;
        var stats = new Stats();
        var watch = Stopwatch.StartNew();

        await RunRoundsAsync(degree: 3, Enumerable.Range(0, rounds), async round =>
        {
            int seed = Seed(masterSeed, round);
            try
            {
                var (status, gets, faults) = await MixedFaultRoundAsync(server, round, seed);
                lock (stats) { stats.Record(status); stats.AddTraffic(gets, faults); }
            }
            catch (Exception error)
            {
                lock (stats) stats.Fail($"第 {round} 轮（seed={seed}）::{error.Message}");
            }
        });

        stats.AssertNoFailures($"断连 fuzz {rounds} 轮");
        AssertNoUnhandled("断连 fuzz");
        Console.WriteLine($"  断连 fuzz：实际 {rounds} 轮 · Completed {stats.Completed} · Error {stats.Error}" +
                          $" · 故障 GET {stats.Faults}/{stats.Gets} · {watch.Elapsed.TotalSeconds:0.0}s");
    }

    // ── 2. 乱序 / 延迟 fuzz ───────────────────────────────────────────────

    private static async Task DelayFuzzAsync(TestServer server)
    {
        const int masterSeed = 777;
        const int rounds = 100;
        var stats = new Stats();
        var watch = Stopwatch.StartNew();

        await RunRoundsAsync(degree: 3, Enumerable.Range(0, rounds), async round =>
        {
            int seed = Seed(masterSeed, round);
            try
            {
                var rng = new Random(seed);
                int size = 256 * KiB + rng.Next(512 * KiB - 256 * KiB + 1);
                var data = new byte[size];
                rng.NextBytes(data);
                string etag = $"\"fuzz2-{round}\"";

                // 注入策略：约 2/3 的 GET 先慢 50~150ms 再正常回；另由种子定 k∈0..2 个 500
                // 钉在前 k 个 GET 上——k ≤ RetryCount(3)，重试必定扛住 → 本测试要求全部 Completed
                int pinned = rng.Next(3);
                var injector = new FaultInjector((uint)seed, data, etag, 650, new[] { FaultInjector.Delay }, pinned);
                string route = $"f2/r{round}.bin";
                server.Map(route, server.RangeFile(data, etag, contentType: null, injector.Handle));
                var dir = TempDir($"t2/r{round}");
                string target = Path.Combine(dir, $"r{round}.bin");

                using var kernel = new RealDownloadKernel(new KernelOptions
                {
                    ConnectionsPerTask = 4,
                    MaxSimultaneous = 1,
                    RetryCount = 3,
                });
                var task = kernel.Add(new NewDownloadRequest(
                    server.Base + route, dir, "soft", 4, TaskPriority.Normal, StartNow: true));

                var status = await WaitTerminalAsync(task, TimeSpan.FromSeconds(45), $"延迟轮 {round}");
                That(status == DownloadStatus.Completed,
                    $"轮 {round}：慢响应+偶发 500 应被重试扛住（实际 {status}：{task.ErrorMessage}）");
                That(File.ReadAllBytes(target).AsSpan().SequenceEqual(data), $"轮 {round}：文件必须字节精确");
                lock (stats) { stats.Record(status); stats.AddTraffic(injector.Gets, injector.Faults); }
            }
            catch (Exception error)
            {
                lock (stats) stats.Fail($"第 {round} 轮（seed={seed}）::{error.Message}");
            }
        });

        stats.AssertNoFailures($"乱序/延迟 fuzz {rounds} 轮");
        AssertNoUnhandled("乱序/延迟 fuzz");
        Console.WriteLine($"  乱序/延迟 fuzz：实际 {rounds} 轮 · Completed {stats.Completed} · Error {stats.Error}" +
                          $" · 故障 GET {stats.Faults}/{stats.Gets} · {watch.Elapsed.TotalSeconds:0.0}s");
    }

    // ── 3. 损坏 checkpoint fuzz ───────────────────────────────────────────

    private static async Task CheckpointFuzzAsync(TestServer server)
    {
        const int masterSeed = 999;
        const int rounds = 60;
        var stats = new Stats();
        var watch = Stopwatch.StartNew();

        await RunRoundsAsync(degree: 3, Enumerable.Range(0, rounds), async round =>
        {
            int seed = Seed(masterSeed, round);
            try
            {
                await CheckpointRoundAsync(server, round, seed, stats);
            }
            catch (Exception error)
            {
                lock (stats) stats.Fail($"第 {round} 轮（seed={seed}）::{error.Message}");
            }
        });

        stats.AssertNoFailures($"损坏 checkpoint fuzz {rounds} 轮");
        AssertNoUnhandled("损坏 checkpoint fuzz");
        Console.WriteLine($"  损坏 checkpoint fuzz：实际 {rounds} 轮 · Completed {stats.Completed} · Error {stats.Error}" +
                          $" · {watch.Elapsed.TotalSeconds:0.0}s");
    }

    private static async Task CheckpointRoundAsync(TestServer server, int round, int seed, Stats stats)
    {
        var rng = new Random(seed);
        var data = new byte[1 * MiB];
        rng.NextBytes(data);
        string etag = $"\"fuzz3-{round}\"";
        string route = $"f3/r{round}.bin";
        server.Map(route, server.RangeFile(data, etag));
        var dir = TempDir($"t3/r{round}");
        string target = Path.Combine(dir, $"r{round}.bin");
        string checkpoint = target + ".nettrans.json";

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 4,
            MaxSimultaneous = 1,
            RetryCount = 3,
        });
        // 先限速再启动：内核的 task.Done 只在 0.9s tick 刷新，必须留出暂停窗口
        var task = kernel.Add(new NewDownloadRequest(
            server.Base + route, dir, "soft", 4, TaskPriority.Normal, StartNow: false));
        kernel.SetSpeedLimit(task.Id, "512 KB/s");
        kernel.Toggle(task.Id); // 开始

        // 1 MiB @ 512 KB/s ≈ 2.1s 下载完；首个 tick（0.9s）时 Done ≈ 475 KB，窗口充足
        await WaitUntilAsync(() => task.Done >= 192 * KiB, TimeSpan.FromSeconds(20), $"轮 {round}：限速下载到 192 KB", task);
        kernel.Toggle(task.Id); // 暂停
        await WaitUntilAsync(
            () => task.Status == DownloadStatus.Paused && File.Exists(checkpoint) &&
                  task.Log.Any(l => l.Message.Contains("断点已保存")),
            TimeSpan.FromSeconds(15), $"轮 {round}：暂停后 checkpoint 落盘", task);

        // 四类损坏轮换：0 全随机字节 / 1 截断 JSON / 2 合法 JSON 缺字段 / 3 字段类型错误。
        // 内核必须静默作废 → 走防碰撞 NextFreePath 或原地重下，绝不抛异常崩任务。
        CorruptCheckpoint(rng, round % 4, checkpoint, server.Base + route);

        kernel.SetSpeedLimit(task.Id, "16 MB/s"); // 恢复后放开速度，压缩重下耗时
        kernel.Toggle(task.Id); // 恢复

        var status = await WaitTerminalAsync(task, TimeSpan.FromSeconds(30), $"轮 {round}：损坏 checkpoint 恢复后的终态");
        That(status == DownloadStatus.Completed,
            $"轮 {round}：损坏 checkpoint 后应重新下载至 Completed（实际 {status}：{task.ErrorMessage}）");
        await WaitUntilAsync(() => task.Checksum is not null, TimeSpan.FromSeconds(10), $"轮 {round}：SHA-256 校验完成", task);

        // 成品可能在原路径（断点作废后原地重下）或防碰撞副本「r{n} (1).bin」，字节必须精确
        bool exact = Directory.EnumerateFiles(dir)
            .Where(f => !f.EndsWith(".nettrans.json", StringComparison.Ordinal))
            .Any(f => File.ReadAllBytes(f).AsSpan().SequenceEqual(data));
        That(exact, $"轮 {round}：目录中应存在与源站字节精确一致的成品文件");

        lock (stats) stats.Record(status);
    }

    /// <summary>按 variant 把 checkpoint 文件写成四类损坏内容之一。</summary>
    private static void CorruptCheckpoint(Random rng, int variant, string path, string url)
    {
        string jsonUrl = JsonSerializer.Serialize(url);
        switch (variant)
        {
            case 0: // 全随机字节：连 UTF-8 / JSON 都不是
                var bytes = new byte[64 + rng.Next(64)];
                rng.NextBytes(bytes);
                File.WriteAllBytes(path, bytes);
                return;
            case 1: // 截断 JSON：一枚真实 checkpoint 从 2/3 处斩断
            {
                string good = JsonSerializer.Serialize(new TransferCheckpoint
                {
                    Kind = "ranges",
                    Url = url,
                    Size = 1 * MiB,
                    Start = new long[] { 0 },
                    End = new long[] { 1 * MiB },
                    Done = new long[] { 192 * KiB },
                });
                File.WriteAllText(path, good[..(good.Length * 2 / 3)]);
                return;
            }
            case 2: // 合法 JSON 缺字段：缺 Size/Start/End/Done，断点必须作废
                File.WriteAllText(path, "{\"Kind\":\"ranges\",\"Url\":" + jsonUrl + "}");
                return;
            default: // 字段类型错误：Size 写成字符串
                File.WriteAllText(path,
                    "{\"Kind\":\"ranges\",\"Url\":" + jsonUrl + ",\"Size\":\"not-a-number\",\"Start\":[0]}");
                return;
        }
    }

    // ── 4. 随机种子压轴 ───────────────────────────────────────────────────

    private static async Task RandomSeedFuzzAsync(TestServer server)
    {
        const int rounds = 100;
        int masterSeed = new Random().Next(1, int.MaxValue); // 压轴轮种子不可预测，打印以便复现
        Console.WriteLine($"  压轴 fuzz masterSeed = {masterSeed}");
        var stats = new Stats();
        var watch = Stopwatch.StartNew();
        var outcomes = new Dictionary<int, DownloadStatus>();

        await RunRoundsAsync(degree: 3, Enumerable.Range(0, rounds), async round =>
        {
            int seed = Seed(masterSeed, round);
            try
            {
                var (status, gets, faults) = await MixedFaultRoundAsync(server, round, seed);
                lock (stats) { stats.Record(status); stats.AddTraffic(gets, faults); outcomes[round] = status; }
            }
            catch (Exception error)
            {
                lock (stats) stats.Fail($"第 {round} 轮（seed={seed}）::{error.Message}");
            }
        });

        // 确定性复核：截断/虚假长度故障的结局取决于 TCP 重置前到达的字节数（物理竞态），
        // 同种子也无法钉死；因此确定性检查使用物理确定的故障子集——硬断连（0 字节到达）、
        // 5xx（不改 Done）、慢响应（正常完成）——同种子连跑两次，结局必须一致。
        var replaySeeds = Enumerable.Range(0, 10).Select(i => Seed(masterSeed, 100000 + i)).ToList();
        var first = new Dictionary<int, DownloadStatus>();
        await RunRoundsAsync(degree: 3, replaySeeds, async seed =>
        {
            var status = await DeterministicRoundAsync(server, seed, run: 1);
            lock (first) first[seed] = status;
        });
        var second = new Dictionary<int, DownloadStatus>();
        await RunRoundsAsync(degree: 3, replaySeeds, async seed =>
        {
            var status = await DeterministicRoundAsync(server, seed, run: 2);
            lock (second) second[seed] = status;
        });

        var mismatches = replaySeeds
            .Where(seed => first.TryGetValue(seed, out var s1) && second.TryGetValue(seed, out var s2) && s1 != s2)
            .Select(seed => $"seed {seed} 首跑 {first[seed]} / 重跑 {second[seed]}")
            .ToList();
        That(mismatches.Count == 0, "同种子重跑结果必须一致：" + string.Join("；", mismatches));
        AssertNoUnhandled("压轴 fuzz");
        Console.WriteLine($"  压轴 fuzz：实际 {rounds}+{2 * replaySeeds.Count} 轮（确定性复核 {replaySeeds.Count} 轮×2）" +
                          $" · Completed {stats.Completed} · Error {stats.Error}" +
                          $" · 故障 GET {stats.Faults}/{stats.Gets} · {watch.Elapsed.TotalSeconds:0.0}s");
    }

    /// <summary>
    /// 确定性故障轮（同种子必同结局）：只注入硬断连 / 5xx / 慢响应——三者都不产生
    /// 「部分字节已到达」的歧义状态，重试序列完全由 (种子, GET 序号) 决定。
    /// </summary>
    private static async Task<DownloadStatus> DeterministicRoundAsync(TestServer server, int seed, int run)
    {
        var rng = new Random(seed);
        int conns = 3 + rng.Next(4);
        int size = 256 * KiB + rng.Next(1 * MiB - 256 * KiB + 1);
        var data = new byte[size];
        rng.NextBytes(data);
        string etag = $"\"fuzzdet-{run}-{seed}\"";

        // 高故障率 + 三种确定模式：大部分轮要走满重试链（Completed 或 Error 都可出现）
        var injector = new FaultInjector((uint)seed, data, etag, 500 + rng.Next(500),
            new[] { FaultInjector.Abort, FaultInjector.ServerError, FaultInjector.Delay });
        string route = $"det{run}/s{seed}.bin";
        server.Map(route, server.RangeFile(data, etag, contentType: null, injector.Handle));
        var dir = TempDir($"det/r{run}/s{seed}");
        string target = Path.Combine(dir, $"s{seed}.bin");

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = conns,
            MaxSimultaneous = 1,
            RetryCount = 2 + rng.Next(3), // 2~4
        });
        var task = kernel.Add(new NewDownloadRequest(
            server.Base + route, dir, "soft", conns, TaskPriority.Normal, StartNow: true));

        var status = await WaitTerminalAsync(task, TimeSpan.FromSeconds(45), $"确定性轮 {seed}");
        That(status is DownloadStatus.Completed or DownloadStatus.Error, "终态必须是 Completed 或 Error");
        if (status == DownloadStatus.Completed)
            That(File.ReadAllBytes(target).AsSpan().SequenceEqual(data), $"确定性轮 {seed}：Completed 文件必须字节精确");
        return status;
    }

    // ── 混合故障轮（方向 1 / 4 共用） ─────────────────────────────────────

    /// <summary>一轮混合故障：随机载荷 + 随机强度/模式 + 随机内核选项，返回终态。</summary>
    private static async Task<(DownloadStatus Status, int Gets, int Faults)> MixedFaultRoundAsync(
        TestServer server, int round, int seed)
    {
        var rng = new Random(seed);
        int conns = 3 + rng.Next(4); // 3~6
        int size = 256 * KiB + rng.Next(1 * MiB - 256 * KiB + 1);
        var data = new byte[size];
        rng.NextBytes(data);
        string etag = $"\"fuzz-{round}-{seed}\"";

        // 故障强度分档（‰）：无 / 轻 / 中 / 猛——猛档大量轮会重试耗尽后 Error，同样合法
        int permille;
        int[] modes;
        int tier = rng.Next(100);
        if (tier < 30) { permille = 0; modes = Array.Empty<int>(); }
        else if (tier < 65) { permille = 150 + rng.Next(200); modes = PickModes(rng, 2 + rng.Next(2)); }
        else if (tier < 88) { permille = 400 + rng.Next(250); modes = PickModes(rng, 3 + rng.Next(2)); }
        else { permille = 850 + rng.Next(150); modes = PickModes(rng, 3 + rng.Next(3)); }

        var injector = new FaultInjector((uint)seed, data, etag, permille, modes);
        string route = $"fx/r{round}-{seed}.bin";
        server.Map(route, server.RangeFile(data, etag, contentType: null, injector.Handle));
        var dir = TempDir($"mix/r{round}-{seed}");
        string target = Path.Combine(dir, $"r{round}-{seed}.bin");

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = conns,
            MaxSimultaneous = 1 + rng.Next(2), // 1~2
            RetryCount = 2 + rng.Next(3),      // 2~4
        });
        var task = kernel.Add(new NewDownloadRequest(
            server.Base + route, dir, "soft", conns, TaskPriority.Normal, StartNow: true));

        var status = await WaitTerminalAsync(task, TimeSpan.FromSeconds(45), $"混合故障轮 {round}");
        That(status is DownloadStatus.Completed or DownloadStatus.Error, "终态必须是 Completed 或 Error");
        if (status == DownloadStatus.Completed)
        {
            // 注意：不在这里断言「完成后 checkpoint 清除」——周期 checkpoint 定时器与完成
            // 路径的 DeleteCheckpoint 存在竞态（已作为内核疑点上报，见测试报告）。
            That(File.ReadAllBytes(target).AsSpan().SequenceEqual(data), $"轮 {round}：Completed 文件必须字节精确");
        }

        return (status, injector.Gets, injector.Faults);
    }

    /// <summary>从五种故障模式里洗牌取 count 个。</summary>
    private static int[] PickModes(Random rng, int count)
    {
        int[] all =
        {
            FaultInjector.Abort, FaultInjector.Truncate, FaultInjector.FalseLength,
            FaultInjector.Delay, FaultInjector.ServerError,
        };
        for (int i = all.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (all[i], all[j]) = (all[j], all[i]);
        }
        return all[..count];
    }

    // ── 故障注入器 ────────────────────────────────────────────────────────

    /// <summary>
    /// 单轮故障注入器。每个 GET 的处置是 (轮种子, GET 序号) 的纯函数——与请求到达的
    /// 线程时序无关，同一种子得到同一故障序列（可复现、可重跑对比）。
    /// HEAD 探测不经过本注入器（RangeFile 对 HEAD 直接放行），故障只打在传输 GET 上。
    /// </summary>
    private sealed class FaultInjector
    {
        public const int Abort = 0;        // 硬断连：一个字节都不回
        public const int Truncate = 1;     // 按正确 ContentLength 写一部分后断连
        public const int FalseLength = 2;  // ContentLength 虚报更长，写完声称的一部分再断连
        public const int Delay = 3;        // 慢响应 50~200ms 后正常回
        public const int ServerError = 4;  // 500 / 503

        private readonly uint _seed;
        private readonly byte[] _data;
        private readonly string? _etag;
        private readonly int _permille;           // 每 GET 注入概率（千分比）
        private readonly int[] _modes;
        private readonly int _pinnedServerErrors; // 钉死在第 1..k 个 GET 上的 5xx 数（延迟场景用）
        private int _gets, _faults;

        public FaultInjector(uint seed, byte[] data, string etag, int permille, int[] modes, int pinnedServerErrors = 0)
        {
            _seed = seed;
            _data = data;
            _etag = etag;
            _permille = permille;
            _modes = modes;
            _pinnedServerErrors = pinnedServerErrors;
        }

        public int Gets => _gets;
        public int Faults => _faults;

        /// <summary>RangeFile 的 onGet 回调；返回 true 表示本请求已由故障注入自行处置。</summary>
        public bool Handle(int n, HttpListenerContext ctx)
        {
            Interlocked.Increment(ref _gets);

            // 延迟场景：前 k 个 GET 必吃 5xx（k ≤ 重试预算 → 任务必 Completed）
            if (n <= _pinnedServerErrors)
            {
                Interlocked.Increment(ref _faults);
                TestServer.Reply(ctx.Response, n % 2 == 0 ? 503 : 500, Encoding.UTF8.GetBytes("injected 5xx"));
                return true;
            }

            uint h = Mix(_seed, (uint)n);
            if (_modes.Length == 0 || h % 1000u >= (uint)_permille) return false; // 本 GET 不注入

            Interlocked.Increment(ref _faults);
            int mode = _modes[(int)((h >> 22) % (uint)_modes.Length)];
            switch (mode)
            {
                case Abort:
                    ctx.Response.Abort(); // 硬断连，连响应头都不给
                    return true;
                case ServerError:
                    TestServer.Reply(ctx.Response, (h & 1) == 0 ? 500 : 503, Encoding.UTF8.GetBytes("injected 5xx"));
                    return true;
                case Delay:
                    Thread.Sleep(50 + (int)(h % 151)); // 50~200ms
                    return false;                      // 放行给正常 ServeRange
                default:
                    ServeCorrupted(ctx, overClaim: mode == FalseLength, h);
                    return true;
            }
        }

        /// <summary>按请求的 Range 切片，回「承诺更多」或「写一半」的正文后硬断连（绝不 Close）。</summary>
        private void ServeCorrupted(HttpListenerContext ctx, bool overClaim, uint h)
        {
            // 与 ServeRange 相同的 Range 解析：无 Range 头时按全量切片
            long start = 0, end = _data.Length - 1;
            bool ranged = false;
            var range = ctx.Request.Headers["Range"];
            if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal) && _data.Length > 0)
            {
                var spec = range["bytes=".Length..];
                int dash = spec.IndexOf('-');
                if (dash >= 0 &&
                    long.TryParse(spec[..dash].Trim(), out var parsedStart) &&
                    parsedStart >= 0 && parsedStart < _data.Length)
                {
                    start = parsedStart;
                    var right = spec[(dash + 1)..].Trim();
                    end = right.Length > 0 && long.TryParse(right, out var parsedEnd)
                        ? Math.Min(parsedEnd, _data.Length - 1)
                        : _data.Length - 1;
                    ranged = start <= end;
                }
            }
            if (!ranged) { start = 0; end = _data.Length - 1; }

            long length = end - start + 1;
            var response = ctx.Response;
            if (_etag is not null) response.Headers["ETag"] = _etag;
            response.StatusCode = ranged ? 206 : 200;
            response.Headers["Accept-Ranges"] = "bytes";
            if (ranged) response.Headers["Content-Range"] = $"bytes {start}-{end}/{_data.Length}";
            response.ContentType = "application/octet-stream";
            // 虚假长度：多报 1B~64KB，客户端读完切片后仍在等待 → 断连即报错
            response.ContentLength64 = overClaim ? length + 1 + h % 65536 : length;

            int take = overClaim
                ? (int)length
                : (int)Math.Max(1, length * (25 + h % 50) / 100); // 截断点 25%~74%
            response.OutputStream.Write(_data, (int)start, take);
            ctx.Response.Abort();
        }

        /// <summary>SplitMix 风格的 32 位混合：稳定、分布均匀的小函数。</summary>
        public static uint Mix(uint a, uint b)
        {
            uint x = a ^ (b * 0x9E3779B9u);
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }
    }

    // ── 基础设施 ──────────────────────────────────────────────────────────

    /// <summary>轮种子 = 主种子与轮号的纯函数；打印 (masterSeed, 轮号) 即可复现。</summary>
    private static int Seed(int masterSeed, int round) =>
        (int)(FaultInjector.Mix((uint)masterSeed, (uint)round) & 0x7FFFFFFF);

    /// <summary>并行跑轮，degree 控制同时在下载的轮数（每轮独立内核，互不干扰）。</summary>
    private static async Task RunRoundsAsync(int degree, IEnumerable<int> rounds, Func<int, Task> body)
    {
        using var gate = new SemaphoreSlim(degree, degree);
        var tasks = rounds.Select(async round =>
        {
            await gate.WaitAsync();
            try { await body(round); }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
    }

    private static async Task RunAsync(string name, Func<Task> body)
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
            _failures.Add($"{name} :: {error.Message}");
            Console.WriteLine($"[FAIL] {name}");
            Console.WriteLine($"       {error.Message}");
        }
    }

    private static void That(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }

    private static void AssertNoUnhandled(string label)
    {
        lock (_unhandled)
        {
            That(_unhandled.Count == 0,
                $"{label}：进程出现未捕获异常：\n    " + string.Join("\n    ", _unhandled.Take(3)));
        }
    }

    private sealed class Stats
    {
        public int Completed, Error, Gets, Faults;
        public readonly List<string> FailureLines = new();

        public void Record(DownloadStatus status)
        {
            if (status == DownloadStatus.Completed) Completed++;
            else if (status == DownloadStatus.Error) Error++;
        }

        public void AddTraffic(int gets, int faults)
        {
            Gets += gets;
            Faults += faults;
        }

        public void Fail(string line)
        {
            FailureLines.Add(line);
            Console.WriteLine("    [轮失败] " + line);
        }

        public void AssertNoFailures(string label)
        {
            That(FailureLines.Count == 0,
                $"{label}：{FailureLines.Count} 轮失败\n    " + string.Join("\n    ", FailureLines.Take(10)));
        }
    }

    private static string TempDir(string label)
    {
        var dir = Path.Combine(_root, label);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupRoots()
    {
        // kernel.Dispose() 后文件句柄释放有短暂延迟，删除要重试
        for (int attempt = 0; attempt < 6 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException) { Thread.Sleep(400); }
            catch (UnauthorizedAccessException) { break; }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what, DownloadTask? task = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(40);
        }

        if (task is not null) DumpTask(task);
        throw new Exception($"等待超时（{timeout.TotalSeconds:0}s）：{what}");
    }

    private static async Task<DownloadStatus> WaitTerminalAsync(DownloadTask task, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = task.Status;
            if (status is DownloadStatus.Completed or DownloadStatus.Error) return status;
            await Task.Delay(50);
        }

        DumpTask(task);
        throw new Exception($"等待超时（{timeout.TotalSeconds:0}s）：{what}");
    }

    private static void DumpTask(DownloadTask task)
    {
        Console.WriteLine($"  ---- task #{task.Id} {task.Name} 状态 {task.Status} Done={task.Done}/{task.Size} ----");
        foreach (var entry in task.Log.TakeLast(12)) Console.WriteLine($"  [{entry.Time}] {entry.Message}");
    }
}
