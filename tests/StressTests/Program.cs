using System.Diagnostics;
using System.Net;
using NetTrans.Kernel;

namespace StressTests;

/// <summary>
/// NetTrans.Kernel 压力/边界测试：64 连接并发、排队调度、限速精度、内存峰值，共 4 个方向：
///   1. 64 连接并发大文件：2 任务 × 32 连接 × 32 MiB。
///      内核把单任务连接数钳制在 1..32（RealDownloadKernel.StartLocked 的 Math.Clamp(…, 1, 32)），
///      HttpClient MaxConnectionsPerServer = 64，所以「64 并发连接」用 2 任务 × 32 连接实现。
///      又因 SegmentTable.Split 以 1 MiB 为最小分片（pieces ≤ size/1MiB），16 MiB 只能切 16 片、
///      只会起 16 条活连接；单任务 32 MiB 才能真跑满 32 连接（pieces = min(64, 32×4, 32) = 32）。
///   2. 多任务排队压力：8 任务 × 4 连接 × 4 MiB，MaxSimultaneous = 3，观察排队调度不越界。
///   3. 限速精度统计：task.Speed 是 0.9s tick 上的 EMA 平滑（0.65×旧 + 0.35×新），不可直接采信；
///      统计一律用「时间窗内 Done 增量 / 时间窗」自算。且 Done 只在 0.9s 的内核 tick 上刷新，
///      1s 窗只会落在 1~2 次 tick 更新之间（窗口速率在 0.9×~1.8× 目标间跳动，纯量化噪声），
///      所以测速窗取 2s（含 2~3 次 tick），并跳过前 2 个窗（SpeedLimiter 令牌桶冷启动的缓冲突发）。
///   4. 内存峰值/泄漏观察：同一 kernel 连续 10 轮 × 8 任务 × 1 MiB，每轮强制 GC 后记录
///      WorkingSet64 / 托管堆 / Gen0-2 集合数；内核自带缓存与连接池，阈值只作宽松回归报警。
///
/// 内核疑点（发现于首轮测试，均已修复，此处保留历史记录）：
///   ① 已修复：TransferSession.RangeWorkerAsync 曾在首片 Start+Done == 0 时不带 Range 头
///      → 服务器回 200 全量正文，首片把整个资源拉完，其余分片再各自拉自己的区间
///      → 实际网络传输量 ≈ 2×文件大小。现在有界分段始终携带 Range，且读取按分段
///      所需字节截断（读满即断），服务器侧实发 ≈ 逻辑大小（测试 1/3 的服务器字节计数可验证）。
///      task.Done 也不再冲过 Size。限速统计仍用「窗口均值」（Σ窗内 Done 增量 / Σ窗时长），
///      免疫 tick 量化噪声。
///   ② 已修复：GlobalSpeedLimit 现在由内核级共享 token 桶执行
///      （RealDownloadKernel._globalLimiter → TransferRequest.GlobalLimiter → TransferSession.ThrottleAsync），
///      N 任务合计不超过全局值（组 B 断言「合计 ≈ 全局 ±25%」）。
/// </summary>
internal static class Program
{
    private const int MiB = 1024 * 1024;

    private static int _passed, _failed;
    private static readonly List<string> _failures = new();
    private static readonly List<string> _roots = new();

    private static async Task<int> Main()
    {
        Console.WriteLine("NetTrans.Kernel 压力/边界测试");
        Console.WriteLine(new string('=', 64));

        using var server = new TestServer();
        Console.WriteLine($"测试服务器：{server.Base}");
        Console.WriteLine();

        await RunAsync("1. 64 连接并发大文件：2 任务 × 32 连接 × 32 MiB（MaxSimultaneous=2）",
            () => SixtyFourConnectionsAsync(server));
        await RunAsync("2. 多任务排队压力：8 任务 × 4 连接 × 4 MiB（MaxSimultaneous=3）",
            () => QueuePressureAsync(server));
        await RunAsync("3. 限速精度统计：单任务 256 KB/s + 全局 512 KB/s 双任务并发",
            () => SpeedLimitPrecisionAsync(server));
        await RunAsync("4. 内存峰值/泄漏观察：同一内核 10 轮 × 8 任务 × 1 MiB",
            () => MemoryObservationAsync(server));

        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"结果：{_passed} 通过，{_failed} 失败");
        foreach (var item in _failures) Console.WriteLine("  [FAIL] " + item);

        CleanupRoots();
        return _failed == 0 ? 0 : 1;
    }

    // ── 1. 64 连接并发大文件 ──────────────────────────────────────────────

    private static async Task SixtyFourConnectionsAsync(TestServer server)
    {
        // 2 任务 × 32 连接 = 64 并发连接（单任务被内核钳在 32，HttpClient 连接池上限 64）。
        // 每任务 32 MiB：见类注释 —— 16 MiB 会因 1 MiB 分片下限退化成 16 条活连接。
        var dataA = Payload(32 * MiB, seed: 101);
        var dataB = Payload(32 * MiB, seed: 202);
        var (handlerA, servedA) = CountedRangeFile(server, dataA, "\"stress-a\"");
        var (handlerB, servedB) = CountedRangeFile(server, dataB, "\"stress-b\"");
        server.Map("stress/big-a.bin", handlerA);
        server.Map("stress/big-b.bin", handlerB);

        var dir = TempDir("sixtyfour");
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 32, MaxSimultaneous = 2 });

        var a = kernel.Add(new NewDownloadRequest(server.Base + "stress/big-a.bin", dir, "soft", 32, TaskPriority.Normal, StartNow: true));
        var b = kernel.Add(new NewDownloadRequest(server.Base + "stress/big-b.bin", dir, "soft", 32, TaskPriority.Normal, StartNow: true));

        // 轮询到双终态（超时 3 分钟：64 并发在沙盒里可能慢）；顺带记录活连接 / 并发下载数峰值。
        // task.Connections / PeakSpeed 只在 0.9s tick 上刷新，本地回环太快时可能采样不到 —— 只打印不断言。
        var clock = Stopwatch.StartNew();
        int maxLiveA = 0, maxLiveB = 0, maxConcurrent = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            maxLiveA = Math.Max(maxLiveA, a.Connections);
            maxLiveB = Math.Max(maxLiveB, b.Connections);
            int running = (a.Status == DownloadStatus.Downloading ? 1 : 0) + (b.Status == DownloadStatus.Downloading ? 1 : 0);
            maxConcurrent = Math.Max(maxConcurrent, running);
            if (a.Status is DownloadStatus.Completed or DownloadStatus.Error &&
                b.Status is DownloadStatus.Completed or DownloadStatus.Error) break;
            await Task.Delay(50);
        }
        clock.Stop();

        That(a.Status == DownloadStatus.Completed, $"任务 A 应 Completed（实际 {a.Status} {a.ErrorMessage}）");
        That(b.Status == DownloadStatus.Completed, $"任务 B 应 Completed（实际 {b.Status} {b.ErrorMessage}）");
        That(a.Size == dataA.Length && a.Done == dataA.Length, $"A Size/Done 应到满（实际 {a.Done}/{a.Size}）");
        That(b.Size == dataB.Length && b.Done == dataB.Length, $"B Size/Done 应到满（实际 {b.Done}/{b.Size}）");
        That(File.ReadAllBytes(Path.Combine(dir, "big-a.bin")).AsSpan().SequenceEqual(dataA), "big-a.bin 字节应与源站完全一致");
        That(File.ReadAllBytes(Path.Combine(dir, "big-b.bin")).AsSpan().SequenceEqual(dataB), "big-b.bin 字节应与源站完全一致");
        That(a.Blocks.Length == 96 && a.Blocks.All(x => x == 1), "A 完成后 96 块应全部点亮");
        That(b.Blocks.Length == 96 && b.Blocks.All(x => x == 1), "B 完成后 96 块应全部点亮");

        double aggregate = 2L * 32 * MiB / Math.Max(0.001, clock.Elapsed.TotalSeconds);
        Console.WriteLine($"       总耗时 {clock.Elapsed.TotalSeconds:0.0}s · 逻辑聚合吞吐 {FmtRate(aggregate)}（64 MiB）");
        Console.WriteLine($"       服务器实发：A {servedA() / (double)MiB:0.0} MiB / B {servedB() / (double)MiB:0.0} MiB（逻辑各 32 MiB）—— 首片无 Range 拉全量导致 ~2× 放大（内核疑点①）");
        Console.WriteLine($"       并发下载数峰值 {maxConcurrent}/2 · 活连接峰值 A={maxLiveA} B={maxLiveB}（tick 未采样到则为 0）");
        Console.WriteLine($"       峰值速度(EMA)：A {FmtRate(a.PeakSpeed)} · B {FmtRate(b.PeakSpeed)}");
    }

    // ── 2. 多任务排队压力 ─────────────────────────────────────────────────

    private static async Task QueuePressureAsync(TestServer server)
    {
        var payloads = Enumerable.Range(0, 8).Select(i => Payload(4 * MiB, (byte)(i + 1))).ToArray();
        for (int i = 0; i < 8; i++)
            server.Map($"stress/q{i}.bin", server.RangeFile(payloads[i], etag: $"\"q{i}\""));

        var dir = TempDir("queue");
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 4, MaxSimultaneous = 3 });

        var tasks = new List<DownloadTask>();
        for (int i = 0; i < 8; i++)
            tasks.Add(kernel.Add(new NewDownloadRequest(server.Base + $"stress/q{i}.bin", dir, "soft", 4, TaskPriority.Normal, StartNow: true)));

        // Add 内部的 Schedule 是同步的：8 个任务加完的瞬间必然恰好 3 个下载、5 个排队
        //（worker 要走完 HEAD 探测 + 网络往返才可能到终态，微秒级的 Add 循环不可能被反超）。
        That(tasks.Count(t => t.Status == DownloadStatus.Downloading) == 3,
            $"加入 8 任务后应恰好 3 个进入下载（实际 {tasks.Count(t => t.Status == DownloadStatus.Downloading)}）");
        That(tasks.Count(t => t.Status == DownloadStatus.Queued) == 5,
            $"加入 8 任务后其余 5 个应排队（实际 {tasks.Count(t => t.Status == DownloadStatus.Queued)}）");

        int maxRunning = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            maxRunning = Math.Max(maxRunning, tasks.Count(t => t.Status == DownloadStatus.Downloading));
            if (tasks.All(t => t.Status is DownloadStatus.Completed or DownloadStatus.Error)) break;
            await Task.Delay(20);
        }
        That(maxRunning <= 3, $"运行中任务数任何时刻不应超过 MaxSimultaneous=3（观测峰值 {maxRunning}）");

        for (int i = 0; i < 8; i++)
        {
            var task = tasks[i];
            That(task.Status == DownloadStatus.Completed, $"q{i}.bin 应 Completed（实际 {task.Status} {task.ErrorMessage}）");
            That(task.Size == payloads[i].Length && task.Done == payloads[i].Length,
                $"q{i}.bin Size/Done 应到满（实际 {task.Done}/{task.Size}）");
            That(File.ReadAllBytes(Path.Combine(dir, $"q{i}.bin")).AsSpan().SequenceEqual(payloads[i]),
                $"q{i}.bin 字节应与源站完全一致");
            That(task.Blocks.Length == 96 && task.Blocks.All(x => x == 1), $"q{i}.bin 完成后 96 块应全部点亮");
        }

        Console.WriteLine($"       8 任务 × 4 MiB 全部完成 · 同时下载数峰值 {maxRunning}/3（排队调度未越界）");
    }

    // ── 3. 限速精度统计 ───────────────────────────────────────────────────

    private static async Task SpeedLimitPrecisionAsync(TestServer server)
    {
        const long PerTaskTarget = 256 * 1024; // "256 KB/s" → ParseLimit = 262144 B/s
        const long GlobalTarget = 512 * 1024;  // GlobalSpeedLimit = 524288 B/s
        const double WarmupSeconds = 2.0;      // 跳过令牌桶冷启动的缓冲突发
        const double WindowSeconds = 2.0;      // Done 只有 0.9s tick 分辨率 → 1s 窗噪声过大，取 2s（见类注释）

        // ── 组 A：单任务 per-task 限速 256 KB/s，6 MiB —— 首片放大后经限速桶约 11 MiB ≈ 44s ──
        var data = Payload(6 * MiB, seed: 61);
        var (limHandler, servedLim) = CountedRangeFile(server, data, "\"lim\"");
        server.Map("stress/lim.bin", limHandler);
        var dirA = TempDir("limit-a");
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 4, MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "stress/lim.bin", dirA, "soft", 4, TaskPriority.Normal, StartNow: false));
            kernel.SetSpeedLimit(task.Id, "256 KB/s");
            kernel.Toggle(task.Id); // 从暂停启动，限速从第一字节就生效

            var points = ToPoints(await SampleAsync(task, null, TimeSpan.FromSeconds(90)), p => p.A);
            That(task.Status == DownloadStatus.Completed, $"限速任务应 Completed（实际 {task.Status} {task.ErrorMessage}）");
            That(task.Size == data.Length && task.Done == data.Length, $"限速任务 Size/Done 应到满（实际 {task.Done}/{task.Size}）");

            var (mean, rates) = WindowStats(points, WarmupSeconds, WindowSeconds);
            That(rates.Count >= 5, $"测速窗应 ≥ 5 个（实际 {rates.Count}，总进度 {task.Done} 字节）");
            double spanMean = (double)points.Max(p => p.Done) / Math.Max(0.001, points[^1].T);

            Console.WriteLine($"       [A] 单任务 256 KB/s · {rates.Count} 个 {WindowSeconds:0}s 窗（KB/s）：{string.Join(" ", rates.Select(r => $"{r / 1024:0}"))}");
            Console.WriteLine($"       [A] 窗口均值 {FmtRate(mean)}（偏差 {Pct(mean / PerTaskTarget)}） · 最大窗 {FmtRate(rates.Max())}（偏差 {Pct(rates.Max() / PerTaskTarget)}）");
            Console.WriteLine($"       [A] 服务器实发 {servedLim() / (double)MiB:0.0} MiB（逻辑 6 MiB，内核疑点①） · 原始 Done 峰值折算 {FmtRate(spanMean)} · 峰值速度(EMA) {FmtRate(task.PeakSpeed)}");

            That(mean >= PerTaskTarget * 0.75 && mean <= PerTaskTarget * 1.25,
                $"限速窗口均值应落在目标 ±25% 内（实际 {FmtRate(mean)}，目标 {FmtRate(PerTaskTarget)}）");
            That(rates.All(r => r <= PerTaskTarget * 1.5),
                $"每个测速窗都不应超过目标 +50%（最大窗 {FmtRate(rates.Max())}，上限 {FmtRate(PerTaskTarget * 1.5)}）");
            That(File.ReadAllBytes(Path.Combine(dirA, "lim.bin")).AsSpan().SequenceEqual(data), "限速下载字节应精确");
        }

        // ── 组 B：全局限速 512 KB/s × 2 任务并发 × 8 MiB ──
        var dataB1 = Payload(8 * MiB, seed: 71);
        var dataB2 = Payload(8 * MiB, seed: 72);
        var (gHandlerA, servedGA) = CountedRangeFile(server, dataB1, "\"g-a\"");
        var (gHandlerB, servedGB) = CountedRangeFile(server, dataB2, "\"g-b\"");
        server.Map("stress/g-a.bin", gHandlerA);
        server.Map("stress/g-b.bin", gHandlerB);
        var dirB = TempDir("limit-b");
        using (var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 4,
            MaxSimultaneous = 2,
            GlobalSpeedLimit = GlobalTarget,
        }))
        {
            var a = kernel.Add(new NewDownloadRequest(server.Base + "stress/g-a.bin", dirB, "soft", 4, TaskPriority.Normal, StartNow: true));
            var b = kernel.Add(new NewDownloadRequest(server.Base + "stress/g-b.bin", dirB, "soft", 4, TaskPriority.Normal, StartNow: true));

            var raw = await SampleAsync(a, b, TimeSpan.FromSeconds(120));
            That(a.Status == DownloadStatus.Completed, $"全局限速任务 A 应 Completed（实际 {a.Status} {a.ErrorMessage}）");
            That(b.Status == DownloadStatus.Completed, $"全局限速任务 B 应 Completed（实际 {b.Status} {b.ErrorMessage}）");

            // 只统计到「第一个任务完成」为止：之后合计速率自然掉半，会污染窗口
            int cut = raw.FindIndex(p => p.SA is DownloadStatus.Completed or DownloadStatus.Error
                                      || p.SB is DownloadStatus.Completed or DownloadStatus.Error);
            That(cut > 0, "至少应观测到一个任务到达终态");
            var window = raw.Take(cut + 1).ToList();
            var (mean, rates) = WindowStats(ToPoints(window, p => p.A + p.B), WarmupSeconds, WindowSeconds);
            double meanA = WindowStats(ToPoints(window, p => p.A), WarmupSeconds, WindowSeconds).Mean;
            double meanB = WindowStats(ToPoints(window, p => p.B), WarmupSeconds, WindowSeconds).Mean;

            Console.WriteLine($"       [B] 全局 512 KB/s × 2 任务并发 · 单任务窗口均值 A {FmtRate(meanA)} / B {FmtRate(meanB)} · 合计 {FmtRate(mean)}");
            Console.WriteLine($"       [B] {rates.Count} 个 {WindowSeconds:0}s 窗合计速率（KB/s）：{string.Join(" ", rates.Select(r => $"{r / 1024:0}"))}");
            Console.WriteLine($"       [B] 服务器实发：A {servedGA() / (double)MiB:0.0} MiB / B {servedGB() / (double)MiB:0.0} MiB（逻辑各 8 MiB，疑点①修复后应 ≈ 逻辑大小）");

            That(rates.Count >= 5, $"全局限速测速窗应 ≥ 5 个（实际 {rates.Count}）");
            // 共享全局桶语义（内核已修复疑点②）：2 任务合计 ≈ 全局值（±25%），
            // 单任务不超过全局 × 1.3 且有真实吞吐（均分下各 ≈ 全局 × 0.5）。
            That(mean <= GlobalTarget * 1.3 && mean >= GlobalTarget * 0.75,
                $"两任务合计速率应 ≈ 全局值（实际 {FmtRate(mean)}，目标 {FmtRate(GlobalTarget)}±25%）");
            That(meanA <= GlobalTarget * 1.3 && meanB <= GlobalTarget * 1.3,
                $"每任务速率不应超过全局限速 × 1.3（A {FmtRate(meanA)} / B {FmtRate(meanB)}，上限 {FmtRate(GlobalTarget * 1.3)}）");
            That(meanA >= GlobalTarget * 0.3 && meanB >= GlobalTarget * 0.3,
                $"每任务应有真实吞吐（A {FmtRate(meanA)} / B {FmtRate(meanB)}，下限 {FmtRate(GlobalTarget * 0.3)}）");
            That(File.ReadAllBytes(Path.Combine(dirB, "g-a.bin")).AsSpan().SequenceEqual(dataB1), "全局限速 A 字节应精确");
            That(File.ReadAllBytes(Path.Combine(dirB, "g-b.bin")).AsSpan().SequenceEqual(dataB2), "全局限速 B 字节应精确");
        }
    }

    // ── 4. 内存峰值/泄漏观察 ──────────────────────────────────────────────

    private static async Task MemoryObservationAsync(TestServer server)
    {
        // 80 条路由共用同一份 1 MiB payload + 同一个处理器：服务器与被测内核同进程，
        // 服务器内存也计入 WorkingSet64，故让服务器侧占用恒定、不随轮数增长。
        // 1 MiB 文件在 1 MiB 分片下限内只能切 1 片（每任务 1 条活连接），内存观察不依赖连接数。
        var payload = Payload(MiB, seed: 99);
        var shared = server.RangeFile(payload, etag: "\"mem\"");
        for (int r = 0; r < 10; r++)
            for (int j = 0; j < 8; j++)
                server.Map($"stress/mem/r{r}t{j}.bin", shared);

        var dir = TempDir("memory");
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 4, MaxSimultaneous = 8 });

        var rows = new List<(double Ws, double Managed, int Gen0, int Gen1, int Gen2)>();
        for (int r = 0; r < 10; r++)
        {
            var round = new List<DownloadTask>();
            for (int j = 0; j < 8; j++)
                round.Add(kernel.Add(new NewDownloadRequest(server.Base + $"stress/mem/r{r}t{j}.bin", dir, "soft", 4, TaskPriority.Normal, StartNow: true)));

            await WaitAllTerminalAsync(round, TimeSpan.FromSeconds(60), $"内存观察第 {r} 轮");

            foreach (var t in round)
                That(t.Status == DownloadStatus.Completed && t.Done == payload.Length,
                    $"内存轮次任务 #{t.Id} 应 Completed 且字节数到满（实际 {t.Status} {t.Done}/{t.Size} {t.ErrorMessage}）");
            for (int j = 0; j < 8; j++)
                That(File.ReadAllBytes(Path.Combine(dir, $"r{r}t{j}.bin")).AsSpan().SequenceEqual(payload),
                    $"r{r}t{j}.bin 字节应精确");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var proc = Process.GetCurrentProcess();
            double ws = proc.WorkingSet64 / (double)MiB;
            double managed = GC.GetTotalMemory(forceFullCollection: true) / (double)MiB;
            rows.Add((ws, managed, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)));
            Console.WriteLine($"       第 {r,2} 轮：WorkingSet {ws,7:0.0} MB · 托管堆 {managed,7:0.0} MB · Gen0/1/2 = {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
        }

        double avgFirst2 = rows.Take(2).Average(x => x.Ws);
        double avgLast3 = rows.TakeLast(3).Average(x => x.Ws);
        Console.WriteLine($"       前 2 轮 WorkingSet 均值 {avgFirst2:0.0} MB · 末 3 轮均值 {avgLast3:0.0} MB · 回归报警阈值 {avgFirst2 + 30:0.0} MB（+30 MB，宽松阈值防误报）");
        That(avgLast3 <= avgFirst2 + 30,
            $"末 3 轮 WorkingSet 均值 {avgLast3:0.0} MB 超过前 2 轮均值 {avgFirst2:0.0} MB + 30 MB —— 疑似泄漏，证据见上方逐轮数字");
    }

    // ── 采样与测速统计 ────────────────────────────────────────────────────

    private readonly record struct Sample(double T, long Done);

    private readonly record struct Sample2(double T, long A, long B, DownloadStatus SA, DownloadStatus SB);

    /// <summary>每 200ms 采一次（时间, DoneA, DoneB, 状态），两个任务都到终态（或超时抛错）为止。</summary>
    private static async Task<List<Sample2>> SampleAsync(DownloadTask a, DownloadTask? b, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        var points = new List<Sample2>();
        while (true)
        {
            points.Add(new Sample2(
                clock.Elapsed.TotalSeconds, a.Done, b?.Done ?? 0, a.Status, b?.Status ?? DownloadStatus.Completed));
            bool aDone = a.Status is DownloadStatus.Completed or DownloadStatus.Error or DownloadStatus.Paused;
            bool bDone = b is null || b.Status is DownloadStatus.Completed or DownloadStatus.Error or DownloadStatus.Paused;
            if (aDone && bDone) return points;
            if (clock.Elapsed.TotalSeconds > timeout.TotalSeconds)
            {
                DumpTasks(a, b);
                throw new Exception(
                    $"采样超时（{timeout.TotalSeconds:0}s）：A={a.Status} {a.Done}/{a.Size}，B={b?.Status} {b?.Done}/{b?.Size}");
            }
            await Task.Delay(200);
        }
    }

    private static List<Sample> ToPoints(List<Sample2> points, Func<Sample2, long> selector) =>
        points.Select(p => new Sample(p.T, selector(p))).ToList();

    /// <summary>
    /// 测速统计：跳过 warmup 秒（令牌桶冷启动 + 探测延迟），之后按 windowSeconds 逐窗算
    /// 「Done 增量 / 实际时长」；均值 = Σ窗内增量 / Σ窗时长（窗口均值）。
    /// 测速一律用 Done 增量自算，不采信 EMA 平滑的 task.Speed。
    /// 只统计到 Done 峰值为止的样本并掐掉尾部停滞段：多分段下传输中的 Done 原始值会先冲过
    /// Size（内核疑点①的重复传输），完成瞬间被夹回 Size —— 端点均值会被夹回截断失真，
    /// 窗口均值不受影响。
    /// </summary>
    private static (double Mean, List<double> Rates) WindowStats(List<Sample> points, double warmupSeconds, double windowSeconds)
    {
        if (points.All(p => p.Done <= 0)) throw new Exception("采样期间无任何下载进度");
        long peak = points.Max(p => p.Done);
        int last = points.FindLastIndex(p => p.Done == peak);
        while (last > 1 && points[last].Done == points[last - 1].Done) last--;
        var curve = points.Take(last + 1).ToList();

        double start = curve.First(p => p.Done > 0).T + warmupSeconds;
        double end = curve[^1].T;
        var rates = new List<double>();
        double sumDelta = 0, sumDt = 0;
        for (double at = start; at + windowSeconds <= end; at += windowSeconds)
        {
            var (ta, da) = Nearest(curve, at);
            var (tb, db) = Nearest(curve, at + windowSeconds);
            double dt = tb - ta;
            if (dt > 0 && db >= da)
            {
                rates.Add((db - da) / dt);
                sumDelta += db - da;
                sumDt += dt;
            }
        }

        double mean = sumDt > 0 ? sumDelta / sumDt : 0;
        return (mean, rates);
    }

    /// <summary>取时间戳最接近 target 的采样点（采样节奏 200ms，边界误差 ≤ 0.1s）。</summary>
    private static Sample Nearest(List<Sample> points, double target)
    {
        var best = points[0];
        double bestDiff = double.MaxValue;
        foreach (var p in points)
        {
            double diff = Math.Abs(p.T - target);
            if (diff < bestDiff) { bestDiff = diff; best = p; }
        }
        return best;
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

    /// <summary>
    /// 给 RangeFile 端点包一层服务器侧发送字节计数（按请求 Range 解析推算将发送的字节数），
    /// 用于核对内核实际拉取的网络字节量（对照内核疑点①「首片无 Range 拉全量」的 ~2× 放大）。
    /// </summary>
    private static (Action<HttpListenerContext> Handler, Func<long> Served) CountedRangeFile(TestServer server, byte[] data, string etag)
    {
        long served = 0;
        var inner = server.RangeFile(data, etag);
        return (ctx =>
        {
            if (ctx.Request.HttpMethod != "HEAD")
            {
                long lo = 0, hi = data.Length - 1;
                var range = ctx.Request.Headers["Range"];
                if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    var spec = range["bytes=".Length..];
                    int dash = spec.IndexOf('-');
                    if (dash >= 0 && long.TryParse(spec[..dash].Trim(), out var parsed) && parsed >= 0 && parsed < data.Length)
                    {
                        lo = parsed;
                        var right = spec[(dash + 1)..].Trim();
                        hi = right.Length > 0 && long.TryParse(right, out var parsedEnd)
                            ? Math.Min(parsedEnd, data.Length - 1)
                            : data.Length - 1;
                    }
                }
                if (lo <= hi) Interlocked.Add(ref served, hi - lo + 1);
            }
            inner(ctx);
        }, () => Interlocked.Read(ref served));
    }

    private static async Task WaitAllTerminalAsync(IReadOnlyList<DownloadTask> tasks, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (tasks.All(t => t.Status is DownloadStatus.Completed or DownloadStatus.Error)) return;
            await Task.Delay(50);
        }

        DumpTasks(tasks.Cast<DownloadTask?>().ToArray());
        throw new Exception(
            $"等待超时（{timeout.TotalSeconds:0}s）：{what}（完成 {tasks.Count(t => t.Status == DownloadStatus.Completed)}/{tasks.Count}）");
    }

    private static void DumpTasks(params DownloadTask?[] tasks)
    {
        foreach (var task in tasks)
        {
            if (task is null) continue;
            Console.WriteLine($"  ---- task #{task.Id} {task.Name} 状态 {task.Status} Done={task.Done}/{task.Size} ----");
            foreach (var entry in task.Log.TakeLast(6)) Console.WriteLine($"  [{entry.Time}] {entry.Message}");
        }
    }

    /// <summary>Deterministic payload; different seeds produce different bytes.</summary>
    private static byte[] Payload(int size, byte seed = 0)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)((i * 31 + seed) % 251);
        return data;
    }

    private static string TempDir(string label)
    {
        var root = Path.Combine(Path.GetTempPath(), "nettrans-stress-" + Guid.NewGuid().ToString("N")[..10]);
        _roots.Add(root);
        var dir = Path.Combine(root, label);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupRoots()
    {
        foreach (var root in _roots)
        {
            // kernel.Dispose() 后句柄释放有短暂延迟，多试几次
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                    break;
                }
                catch (IOException) { Thread.Sleep(400); }
                catch (UnauthorizedAccessException) { break; }
            }
        }
    }

    private static string FmtRate(double bytesPerSecond) => bytesPerSecond >= MiB
        ? $"{bytesPerSecond / MiB:0.00} MB/s"
        : $"{bytesPerSecond / 1024:0.0} KB/s";

    /// <summary>相对目标的偏差百分比（ratio = 实际/目标）：正数带 +，负数带 -，接近 0 显示 ±。</summary>
    private static string Pct(double ratio)
    {
        double p = Math.Round((ratio - 1) * 100, 1);
        return (p > 0 ? "+" : p < 0 ? "" : "±") + p.ToString("0.0") + "%";
    }
}
