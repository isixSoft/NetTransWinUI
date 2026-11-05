using System.Security.Cryptography;
using System.Text;
using NetTrans.Kernel;
using NetTrans.Kernel.Protocol;

namespace ExternalEngineTests;

/// <summary>
/// 外部引擎插槽（BT / eD2k 数据面）测试：magnet / ed2k 探测离线解析为
/// ExternalEngine 计划，断言 EngineRunner 与命令模板两条执行通路、
/// 失败通路与未配置时的原样错误文案。
/// 断言全部基于内核真实行为；Windows 下 shell 类用例跳过（沙箱为 Linux，正常执行）。
/// </summary>
internal sealed class Program : TestKit
{
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef";
    private const string MagnetName = "bt-01234567";

    // TransferSession 外部引擎分支的原始文案（未配置引擎时必须逐字保持）
    private const string HonestSlotError =
        "需要外部引擎（BitTorrent / libtorrent）—— 引擎插槽未接入：infohash 0123456789abcdef · 0 个 tracker · DHT/PEX 数据面需外部 BT 引擎";

    private static int _passed, _failed;
    private static readonly List<string> _failures = new();

    private static async Task<int> Main()
    {
        Console.WriteLine("NetTrans.Kernel 外部引擎插槽测试");
        Console.WriteLine(new string('=', 64));

        await RunAsync("1.  EngineRunner：magnet 像字节传输一样完成", RunnerCompletesMagnetAsync);
        await RunAsync("2.  EngineRunner 与命令同时配置时 runner 优先", RunnerWinsOverCommandAsync);
        await RunAsync("3.  EngineRunner 返回 false → Error", RunnerFalseFailsAsync);
        await RunAsync("4.  EngineRunner 报告完成但无文件 → Error", RunnerNoFileFailsAsync);

        if (!OperatingSystem.IsWindows())
        {
            await RunAsync("5.  命令模板：cp fixture 到 {path}", CommandCopiesFixtureAsync);
            await RunAsync("6.  命令模板：{name}/{url} 占位符替换", CommandSubstitutionAsync);
            await RunAsync("7.  命令模板：exit 3 → Error + 退出码 3", CommandExit3Async);
            await RunAsync("8.  命令模板：退出码 0 但无文件 → Error", CommandNoFileAsync);
            await RunAsync("9.  eD2k 计划经命令模板完成", Ed2kCommandCompletesAsync);
            await RunAsync("11. {files}：双文件种子勾选第 1 个 → --select-file=1", FilesPlaceholderSelectsFirstFileAsync);
            await RunAsync("12. {files}：未勾选 → 空展开", FilesPlaceholderEmptyWithoutSelectionAsync);
        }
        else
        {
            Console.WriteLine("[SKIP] 5-9、11-12 shell 用例（Windows 宿主）");
        }

        await RunAsync("10. 未配置引擎：保持原槽位错误文案", NoEngineHonestErrorAsync);
        await RunAsync("13. SelectedFiles：透传 runner 计划", SelectedFilesReachRunnerPlanAsync);

        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"结果：{_passed} 通过，{_failed} 失败");
        foreach (var item in _failures) Console.WriteLine("  [FAIL] " + item);
        return _failed == 0 ? 0 : 1;
    }

    // ── 1. EngineRunner 成功通路 ──────────────────────────────────────────

    private static async Task RunnerCompletesMagnetAsync()
    {
        var data = Payload(4096);
        string? runnerEngine = null;
        string? runnerPath = null;
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            EngineRunner = (plan, path, _) =>
            {
                runnerEngine = plan.Engine;
                runnerPath = path;
                File.WriteAllBytes(path, data);
                return Task.FromResult(true);
            },
        });
        int completions = 0;
        kernel.TaskCompleted += _ => Interlocked.Increment(ref completions);

        var task = kernel.Add(new NewDownloadRequest(Magnet, dir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitTerminalAsync(task);
        await WaitAsync(() => Volatile.Read(ref completions) >= 1, "TaskCompleted 应触发", 5_000);

        var path = Path.Combine(dir, MagnetName);
        Equal(DownloadStatus.Completed, task.Status, "状态应为 Completed");
        Equal(MagnetName, task.Name, "magnet 探测应命名任务");
        That(File.Exists(path), "目标文件应存在");
        That(runnerPath == path, $"EngineRunner 收到的 savePath 应为 {path}，实际 {runnerPath}");
        Equal("BitTorrent / libtorrent", runnerEngine, "EngineRunner 收到的计划引擎名");
        Equal(Hash(data), Hash(File.ReadAllBytes(path)), "文件字节应与 runner 写入一致");
        Equal((long)data.Length, task.Size, "Size 应为文件长度");
        Equal(task.Size, task.Done, "Done 应等于 Size");
        That(task.Blocks.Length == 96 && task.Blocks.All(b => b == 1), "96 格 Blocks 应全部为 1");
        That(!Directory.EnumerateFiles(dir, "*" + new KernelOptions().CheckpointSuffix).Any(),
            "完成不应留下断点文件");
        That(task.Checksum is not null, "完成应经过 SHA-256 校验（与字节传输一致）");
        That(LogSnapshot(task).Any(m => m.Contains("外部引擎接管：BitTorrent / libtorrent")),
            "日志应记录引擎接管");
    }

    // ── 2. 优先级：runner 覆盖命令模板 ────────────────────────────────────

    private static async Task RunnerWinsOverCommandAsync()
    {
        var runnerData = Payload(2048);
        var commandData = Payload(4096);
        var fixture = WriteFixture(commandData);
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            EngineRunner = (_, path, _) =>
            {
                File.WriteAllBytes(path, runnerData);
                return Task.FromResult(true);
            },
            ExternalEngineCommand = $"cp '{fixture}' \"{{path}}\"",
        });

        var task = kernel.Add(new NewDownloadRequest(Magnet, dir, "", 1, TaskPriority.Normal, StartNow: true));
        await WaitTerminalAsync(task);

        Equal(DownloadStatus.Completed, task.Status, "状态应为 Completed");
        Equal(Hash(runnerData), Hash(File.ReadAllBytes(Path.Combine(dir, MagnetName))),
            "两者同时配置时文件应来自 runner（runner 优先）");
    }

    // ── 3-4. runner 失败通路 ──────────────────────────────────────────────

    private static async Task RunnerFalseFailsAsync()
    {
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            EngineRunner = (_, _, _) => Task.FromResult(false),
        });
        var task = kernel.Add(new NewDownloadRequest(Magnet, dir, "", 1, TaskPriority.Normal, StartNow: true));
        await WaitTerminalAsync(task);

        Equal(DownloadStatus.Error, task.Status, "runner 返回 false 应为 Error");
        Equal("外部引擎运行失败", task.ErrorMessage, "ErrorMessage 应为外部引擎运行失败");
        That(task.Retries >= 1, "失败应计入 Retries");
    }

    private static async Task RunnerNoFileFailsAsync()
    {
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            EngineRunner = (_, _, _) => Task.FromResult(true), // 不写文件
        });
        var task = kernel.Add(new NewDownloadRequest(Magnet, dir, "", 1, TaskPriority.Normal, StartNow: true));
        await WaitTerminalAsync(task);

        Equal(DownloadStatus.Error, task.Status, "报告完成但无文件应为 Error");
        That((task.ErrorMessage ?? "").Contains("外部引擎未产出文件"),
            $"ErrorMessage 应包含“外部引擎未产出文件”，实际 {task.ErrorMessage}");
    }

    // ── 5-8. 命令模板通路（/bin/sh -c；Windows 跳过） ────────────────────

    private static async Task CommandCopiesFixtureAsync()
    {
        var data = Payload(8192);
        var fixture = WriteFixture(data);
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            ExternalEngineCommand = $"cp '{fixture}' \"{{path}}\"",
        });
        var task = kernel.Add(new NewDownloadRequest(Magnet, dir, "", 1, TaskPriority.Normal, StartNow: true));
        await WaitTerminalAsync(task);

        var path = Path.Combine(dir, MagnetName);
        Equal(DownloadStatus.Completed, task.Status, "退出码 0 + 文件存在应为 Completed");
        Equal(Hash(data), Hash(File.ReadAllBytes(path)), "文件字节应与 fixture 一致");
        Equal((long)data.Length, task.Size, "Size 应为文件长度");
        That(task.Blocks.Length == 96 && task.Blocks.All(b => b == 1), "Blocks 应全部为 1");
    }

    private static async Task CommandSubstitutionAsync()
    {
        var data = Payload(1024);
        var fixture = WriteFixture(data);
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            ExternalEngineCommand =
                $"cp '{fixture}' \"{{path}}\" && printf '%s|%s' \"{{name}}\" \"{{url}}\" > \"{{path}}.name\"",
        });
        var task = kernel.Add(new NewDownloadRequest(Magnet, dir, "", 1, TaskPriority.Normal, StartNow: true));
        await WaitTerminalAsync(task);

        var path = Path.Combine(dir, MagnetName);
        Equal(DownloadStatus.Completed, task.Status, "命令成功应为 Completed");
        Equal(Hash(data), Hash(File.ReadAllBytes(path)), "主文件字节应与 fixture 一致");
        Equal($"{MagnetName}|{Magnet}", File.ReadAllText(path + ".name"),
            "{name}/{url} 应替换为任务名与任务 URL");
    }

    private static async Task CommandExit3Async()
    {
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            ExternalEngineCommand = "echo boom-stderr >&2; exit 3",
        });
        var task = kernel.Add(new NewDownloadRequest(Magnet, dir, "", 1, TaskPriority.Normal, StartNow: true));
        await WaitTerminalAsync(task);

        Equal(DownloadStatus.Error, task.Status, "非零退出码应为 Error");
        var log = LogSnapshot(task);
        That(log.Any(m => m.Contains("外部引擎退出码 3")),
            $"日志应包含“外部引擎退出码 3”，实际：{string.Join(" | ", log)}");
        That(log.Any(m => m.Contains("boom-stderr")), "stderr 首行应进入日志");
        That((task.ErrorMessage ?? "").Contains("退出码 3"),
            $"ErrorMessage 应包含退出码 3，实际 {task.ErrorMessage}");
        That(task.Retries >= 1, "失败应计入 Retries");
    }

    private static async Task CommandNoFileAsync()
    {
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            ExternalEngineCommand = "exit 0",
        });
        var task = kernel.Add(new NewDownloadRequest(Magnet, dir, "", 1, TaskPriority.Normal, StartNow: true));
        await WaitTerminalAsync(task);

        Equal(DownloadStatus.Error, task.Status, "退出码 0 但无文件应为 Error");
        That((task.ErrorMessage ?? "").Contains("外部引擎未产出文件"),
            $"ErrorMessage 应包含“外部引擎未产出文件”，实际 {task.ErrorMessage}");
    }

    // ── 9. eD2k 计划 ──────────────────────────────────────────────────────

    private static async Task Ed2kCommandCompletesAsync()
    {
        var data = Payload(4096);
        var fixture = WriteFixture(data);
        const string ed2k = "ed2k://|file|ubuntu.iso|4096|0123456789ABCDEF0123456789ABCDEF|/";
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            ExternalEngineCommand = $"cp '{fixture}' \"{{path}}\"",
        });
        var task = kernel.Add(new NewDownloadRequest(ed2k, dir, "", 1, TaskPriority.Normal, StartNow: true));
        await WaitTerminalAsync(task);

        Equal(DownloadStatus.Completed, task.Status, "eD2k 计划经命令模板应完成");
        Equal("ubuntu.iso", task.Name, "eD2k 探测应命名任务");
        Equal(Hash(data), Hash(File.ReadAllBytes(Path.Combine(dir, "ubuntu.iso"))), "文件字节应与 fixture 一致");
        Equal((long)data.Length, task.Size, "Size 应与探测大小一致");
        That(LogSnapshot(task).Any(m => m.Contains("外部引擎接管：eD2k / Kelpie")), "日志应记录 eD2k 引擎接管");
    }

    // ── 10. 未配置引擎：诚实错误 ──────────────────────────────────────────

    private static async Task NoEngineHonestErrorAsync()
    {
        // 先独立探测，得到计划里的引擎名与详情，核对硬编码文案逐字一致
        string probedText;
        using (var http = new HttpClient())
        {
            var registry = new ProtocolDriverRegistry(new KernelOptions(), http);
            var probe = await registry.ProbeAsync(Magnet, null, null, default);
            That(probe.Plan is TransferPlan.ExternalEngine, $"magnet 计划应为 ExternalEngine，实际 {probe.Plan}");
            var engine = (TransferPlan.ExternalEngine)probe.Plan;
            probedText = $"需要外部引擎（{engine.Engine}）—— 引擎插槽未接入：{engine.Detail}";
        }

        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(Magnet, dir, "", 1, TaskPriority.Normal, StartNow: true));
        await WaitTerminalAsync(task);

        Equal(DownloadStatus.Error, task.Status, "未配置引擎应为 Error");
        Equal(HonestSlotError, probedText, "硬编码文案应与探测构造的原文案逐字一致");
        Equal(HonestSlotError, task.ErrorMessage, "应保持原有槽位未接入文案");
        That(LogSnapshot(task).Any(m => m.Contains("下载失败：" + HonestSlotError)), "失败文案应进入任务日志");
    }

    // ── 11-13. BT per-file 选择：{files} 占位符（P1-6）───────────────────

    private static async Task FilesPlaceholderSelectsFirstFileAsync()
    {
        var torrent = WriteTwoFileTorrent();
        // fixture 先解析核对：确保种子确为双文件（a.txt=100 / b.txt=200）
        using (var http = new HttpClient())
        {
            var registry = new ProtocolDriverRegistry(new KernelOptions(), http);
            var probe = await registry.ProbeAsync(torrent, null, null, default);
            Equal("dual", probe.Name, "双文件种子应解析出 info.name");
            Equal(300L, probe.Size, "双文件大小之和应为 300");
        }

        var data = Payload(1024);
        var fixture = WriteFixture(data);
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            ExternalEngineCommand = $"printf '%s' \"{{files}}\" > \"{{path}}.sel\" && cp '{fixture}' \"{{path}}\"",
        });
        var task = kernel.Add(new NewDownloadRequest(torrent, dir, "", 1, TaskPriority.Normal, StartNow: true,
            SelectedFiles: new[] { 1 }));
        await WaitTerminalAsync(task);

        Equal(DownloadStatus.Completed, task.Status, "勾选文件的 BT 任务应完成");
        Equal("--select-file=1", File.ReadAllText(Path.Combine(dir, "dual.sel")),
            "{files} 应展开为 aria2 语义的 --select-file 索引（勾选第 1 个文件）");
    }

    private static async Task FilesPlaceholderEmptyWithoutSelectionAsync()
    {
        var torrent = WriteTwoFileTorrent();
        var data = Payload(512);
        var fixture = WriteFixture(data);
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            ExternalEngineCommand = $"printf '%s' \"{{files}}\" > \"{{path}}.sel\" && cp '{fixture}' \"{{path}}\"",
        });
        var task = kernel.Add(new NewDownloadRequest(torrent, dir, "", 1, TaskPriority.Normal, StartNow: true));
        await WaitTerminalAsync(task);

        Equal(DownloadStatus.Completed, task.Status, "未勾选文件的 BT 任务应完成");
        Equal(string.Empty, File.ReadAllText(Path.Combine(dir, "dual.sel")),
            "未勾选时 {files} 应展开为空（模板自行决定是否携带参数）");
    }

    private static async Task SelectedFilesReachRunnerPlanAsync()
    {
        var torrent = WriteTwoFileTorrent();
        var data = Payload(2048);
        IReadOnlyList<int>? planSelection = null;
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            EngineRunner = (plan, path, _) =>
            {
                planSelection = ((TransferPlan.ExternalEngine)plan).SelectedFiles;
                File.WriteAllBytes(path, data);
                return Task.FromResult(true);
            },
        });
        var task = kernel.Add(new NewDownloadRequest(torrent, dir, "", 1, TaskPriority.Normal, StartNow: true,
            SelectedFiles: new[] { 2, 1 }));
        await WaitTerminalAsync(task);

        Equal(DownloadStatus.Completed, task.Status, "runner 通路应完成");
        That(planSelection is not null, "runner 收到的计划应携带 SelectedFiles");
        Equal(2, planSelection!.Count, "选择数量应透传");
        Equal(2, planSelection[0], "索引按用户勾选顺序透传");
        Equal(1, planSelection[1], "索引按用户勾选顺序透传");
    }

    // ── 基础设施 ──────────────────────────────────────────────────────────

    private static async Task RunAsync(string name, Func<Task> body)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await body();
            Interlocked.Increment(ref _passed);
            Console.WriteLine($"[PASS] {name}（{sw.Elapsed.TotalSeconds:0.0}s）");
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref _failed);
            _failures.Add($"{name} :: {error.Message}");
            Console.WriteLine($"[FAIL] {name}（{sw.Elapsed.TotalSeconds:0.0}s）");
            Console.WriteLine($"       {error.Message}");
        }
    }

    private static byte[] Payload(int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(i * 31 % 251);
        return data;
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nettrans-ext-engine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteFixture(byte[] data)
    {
        var path = Path.Combine(TempDir(), "fixture.bin");
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>手工 bencode 双文件种子：info.name=dual，a.txt=100 / b.txt=200。</summary>
    private static string WriteTwoFileTorrent()
    {
        var fileA = BDict(("length", BInt(100)), ("path", BList(BString("a.txt"))));
        var fileB = BDict(("length", BInt(200)), ("path", BList(BString("b.txt"))));
        var info = BDict(("files", BList(fileA, fileB)), ("name", BString("dual")));
        var torrent = BDict(("announce", BString("http://tracker.example.org/announce")), ("info", info));
        var path = Path.Combine(TempDir(), "dual.torrent");
        File.WriteAllBytes(path, torrent);
        return path;
    }

    private static byte[] BString(string s)
    {
        var text = Encoding.UTF8.GetBytes(s);
        return Encoding.ASCII.GetBytes($"{text.Length}:").Concat(text).ToArray();
    }

    private static byte[] BInt(long value) => Encoding.ASCII.GetBytes($"i{value}e");

    private static byte[] BList(params byte[][] items) =>
        items.Aggregate(Encoding.ASCII.GetBytes("l"), (acc, item) => acc.Concat(item).ToArray())
             .Concat(Encoding.ASCII.GetBytes("e")).ToArray();

    private static byte[] BDict(params (string Key, byte[] Value)[] pairs) =>
        pairs.Aggregate(Encoding.ASCII.GetBytes("d"), (acc, pair) => acc.Concat(BString(pair.Key)).Concat(pair.Value).ToArray())
             .Concat(Encoding.ASCII.GetBytes("e")).ToArray();

    private static async Task WaitTerminalAsync(DownloadTask task, int timeoutMs = 15_000) =>
        await WaitAsync(
            () => task.Status is DownloadStatus.Completed or DownloadStatus.Error,
            $"任务应在 {timeoutMs} ms 内到达终态（实际 {task.Status}）", timeoutMs);

    private static async Task WaitAsync(Func<bool> condition, string what, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new Exception($"等待超时（{timeoutMs} ms）：{what}");
    }

    /// <summary>任务日志非线程安全集合的快照；与其他线程的 Add 竞争时短暂重试。</summary>
    private static string[] LogSnapshot(DownloadTask task)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return task.Log.Select(l => l.Message).ToArray();
            }
            catch when (attempt < 100)
            {
                Thread.Sleep(25);
            }
        }
    }
}
