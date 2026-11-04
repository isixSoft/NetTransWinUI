using NetTrans.Kernel;
using NetTrans.Models;
using NetTrans.Services;

namespace SetupTests;

/// <summary>
/// 设置页 → 内核选项映射测试（KernelOptionsFactory），外加 JsonSettingsStore
/// 的保存/读取往返。KernelOptionsFactory / AppSettings / JsonSettingsStore 均
/// 为纯逻辑文件，通过 Compile Include 链接进本工程——测的即是应用真实源码。
/// 每个 Check 独立计数，全部跑完后再汇总，退出码 1 表示存在失败。
/// </summary>
internal static class Program
{
    private static int _passed, _failed;
    private static readonly List<string> _failures = new();

    private static int Main()
    {
        Console.WriteLine("设置页 → 内核选项映射测试");
        Console.WriteLine(new string('=', 64));

        Run("1.  null settings → 内核全部默认值", NullSettingsGivesKernelDefaults);
        Run("2.  默认 AppSettings → 内核默认值", DefaultSettingsGiveKernelDefaults);
        Run("3.  限速解析：\"不限\"/空/垃圾文本 → null", SpeedLimitUnlimitedTexts);
        Run("4.  限速解析：单位换算（KB/MB/GB/B、小数、大小写）", SpeedLimitUnitParsing);
        Run("5.  重试策略：\"N 次\" → RetryCount，垃圾回退默认", RetryPolicyMapping);
        Run("6.  MaxSimultaneous 钳制（下限 1）", MaxSimultaneousClamp);
        Run("7.  JsonSettingsStore 往返 + 工厂映射", SettingsStoreRoundTrip);
        Run("8.  shell 专属字段不泄漏进内核选项", ShellFieldsNotMapped);
        Run("9.  保存路径 + 分类文件夹映射", SavePathMapping);
        Run("10. 夜间不限速设置映射", NightLimitMapping);
        Run("11. OffPeak 窗口判断（跨午夜/边界/垃圾）", OffPeakWindowLogic);
        Run("12. 本地签名库路径映射", HashDatabaseMapping);

        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"结果：{_passed} 通过，{_failed} 失败");
        foreach (var item in _failures) Console.WriteLine("  [FAIL] " + item);
        return _failed == 0 ? 0 : 1;
    }

    // ── 1. null settings ──────────────────────────────────────────────────

    private static void NullSettingsGivesKernelDefaults()
    {
        var options = KernelOptionsFactory.Create(null);
        Equal("NetTrans/1.0", options.UserAgent, "UserAgent 保持内核默认");
        Equal(3, options.MaxSimultaneous, "MaxSimultaneous 保持内核默认");
        Equal(3, options.RetryCount, "RetryCount 保持内核默认");
        Equal((long?)null, options.GlobalSpeedLimit, "GlobalSpeedLimit 保持 null（不限）");
        Equal(8, options.ConnectionsPerTask, "ConnectionsPerTask 保持内核默认");
        Equal(".nettrans.json", options.CheckpointSuffix, "CheckpointSuffix 保持内核默认");
        That(options.Proxy is null, "Proxy 保持 null（直连）");
    }

    // ── 2. 默认 AppSettings ───────────────────────────────────────────────

    private static void DefaultSettingsGiveKernelDefaults()
    {
        // 默认值：MaxSimultaneousDownloads=3、GlobalSpeedLimit="不限"、RetryPolicy="3 次"
        var options = KernelOptionsFactory.Create(new AppSettings());
        Equal(3, options.MaxSimultaneous, "默认并发 3 → MaxSimultaneous 3");
        Equal((long?)null, options.GlobalSpeedLimit, "默认 \"不限\" → GlobalSpeedLimit null");
        Equal(3, options.RetryCount, "默认 \"3 次\" → RetryCount 3");
    }

    // ── 3. 限速：不限 / 空 / 垃圾 ─────────────────────────────────────────

    private static void SpeedLimitUnlimitedTexts()
    {
        Equal((long?)null, Create("不限").GlobalSpeedLimit, "\"不限\" → null");
        Equal((long?)null, Create("").GlobalSpeedLimit, "空串 → null");
        Equal((long?)null, Create("   ").GlobalSpeedLimit, "纯空白 → null");
        Equal((long?)null, Create("abc").GlobalSpeedLimit, "\"abc\" → null");
        Equal((long?)null, Create("4").GlobalSpeedLimit, "只有数字没有单位 → null");
        Equal((long?)null, Create("MB/s").GlobalSpeedLimit, "只有单位没有数字 → null");
        Equal((long?)null, Create("4 MB/s extra").GlobalSpeedLimit, "单位后带尾随垃圾 → null");
        Equal((long?)null, Create("12 PB/s").GlobalSpeedLimit, "不认识的单位 PB/s → null");
    }

    // ── 4. 限速：单位换算 ─────────────────────────────────────────────────

    private static void SpeedLimitUnitParsing()
    {
        Equal(4194304L, Create("4 MB/s").GlobalSpeedLimit, "\"4 MB/s\" → 4×1024² = 4194304");
        Equal(524288L, Create("512 KB/s").GlobalSpeedLimit, "\"512 KB/s\" → 512×1024 = 524288");
        Equal(2147483648L, Create("2 GB/s").GlobalSpeedLimit, "\"2 GB/s\" → 2×1024³ = 2147483648");
        Equal(1572864L, Create("1.5 MB/s").GlobalSpeedLimit, "\"1.5 MB/s\" → 1572864（支持小数）");
        Equal(1048576L, Create("1 mb/s").GlobalSpeedLimit, "\"1 mb/s\" → 大小写不敏感 → 1048576");
        Equal(100L, Create("100 B/s").GlobalSpeedLimit, "\"100 B/s\" → 100");
        Equal(3221225472L, Create("3GB/S").GlobalSpeedLimit, "\"3GB/S\" → 无空格 + 大写单位 → 3×1024³");
        Equal((long?)null, Create("1.2.3 MB/s").GlobalSpeedLimit, "畸形数字 1.2.3 → null");
    }

    // ── 5. 重试策略 ───────────────────────────────────────────────────────

    private static void RetryPolicyMapping()
    {
        Equal(3, Create(retry: "3 次").RetryCount, "\"3 次\" → 3");
        Equal(5, Create(retry: "5 次").RetryCount, "\"5 次\" → 5");
        Equal(10, Create(retry: "10 次").RetryCount, "\"10 次\" → 10（取前导整数）");
        Equal(3, Create(retry: "abc").RetryCount, "垃圾文本 → 内核默认 3");
        Equal(3, Create(retry: "").RetryCount, "空串 → 内核默认 3");
        Equal(7, Create(retry: "  7 次").RetryCount, "前导空白后 \"7 次\" → 7");
    }

    // ── 6. MaxSimultaneous 钳制 ───────────────────────────────────────────

    private static void MaxSimultaneousClamp()
    {
        Equal(1, Create(max: 0).MaxSimultaneous, "0 → 钳制为 1");
        Equal(1, Create(max: -2).MaxSimultaneous, "负数 → 钳制为 1");
        Equal(7, Create(max: 7).MaxSimultaneous, "7 → 原样保留");
    }

    // ── 7. JsonSettingsStore 往返 ─────────────────────────────────────────

    private static void SettingsStoreRoundTrip()
    {
        // 便携模式：沙箱里可执行目录可写，settings 落在测试输出目录旁。
        ISettingsStore store = new JsonSettingsStore();
        var saved = new AppSettings { MaxSimultaneousDownloads = 2, GlobalSpeedLimit = "1 MB/s" };
        store.Save(saved);

        var loaded = store.Load();
        Equal(2, loaded.MaxSimultaneousDownloads, "往返后 MaxSimultaneousDownloads == 2");
        Equal("1 MB/s", loaded.GlobalSpeedLimit, "往返后 GlobalSpeedLimit == \"1 MB/s\"");

        var options = KernelOptionsFactory.Create(loaded);
        Equal(2, options.MaxSimultaneous, "工厂映射：MaxSimultaneous == 2");
        Equal(1048576L, options.GlobalSpeedLimit, "工厂映射：\"1 MB/s\" → 1048576");
    }

    // ── 8. shell 专属字段不映射 ───────────────────────────────────────────

    private static void ShellFieldsNotMapped()
    {
        // 设置页里真正的 shell 专属字段（主题/老板键/岛屿显隐/排序等）不应有
        // 任何内核对应物；改遍它们，内核其余默认值必须纹丝不动。
        // （DefaultSavePath / OffPeak 窗口自阶段 4 起已映射进内核，见测试 9/10。）
        var settings = new AppSettings
        {
            Theme = "dark",
            BossKey = "Ctrl + Shift + Z",
            ShowIsland = false,
            ShowInspector = false,
            DenseRows = true,
            SortKey = "size",
            SortDirection = "desc",
            WatchClipboard = false,
            WhenAllComplete = "关机",
        };
        var options = KernelOptionsFactory.Create(settings);
        Equal("NetTrans/1.0", options.UserAgent, "UserAgent 不受 shell 字段影响");
        Equal(8, options.ConnectionsPerTask, "ConnectionsPerTask 不受 shell 字段影响");
        Equal(".nettrans.json", options.CheckpointSuffix, "CheckpointSuffix 不受 shell 字段影响");
        That(options.Proxy is null, "Proxy 不受 shell 字段影响");
        That(options.DefaultHeaders.Count == 0, "DefaultHeaders 保持为空");
        That(options.MirrorResolver is null, "MirrorResolver 保持 null");
        That(options.HttpHandlerFactory is null, "HttpHandlerFactory 保持 null");
    }

    // ── 9. 保存路径 + 分类文件夹映射 ──────────────────────────────────────

    private static void SavePathMapping()
    {
        var settings = new AppSettings { DefaultSavePath = @"D:\Downloads", FoldersByCategory = true };
        var options = KernelOptionsFactory.Create(settings);
        Equal(@"D:\Downloads", options.DefaultSavePath, "DefaultSavePath 原样映射进内核");
        That(options.FoldersByCategory, "FoldersByCategory=true → 内核开启分类子目录");

        settings.FoldersByCategory = false;
        That(!KernelOptionsFactory.Create(settings).FoldersByCategory, "FoldersByCategory=false → 内核关闭分类子目录");

        // 空白路径 → null（内核保留旧行为：写进程工作目录）；带空白 → Trim
        Equal((string?)null, KernelOptionsFactory.Create(new AppSettings { DefaultSavePath = "" }).DefaultSavePath,
            "空字符串 DefaultSavePath → null");
        Equal((string?)null, KernelOptionsFactory.Create(new AppSettings { DefaultSavePath = "   " }).DefaultSavePath,
            "纯空白 DefaultSavePath → null");
        Equal(@"E:\DL", KernelOptionsFactory.Create(new AppSettings { DefaultSavePath = " E:\\DL " }).DefaultSavePath,
            "带空白的 DefaultSavePath → Trim 后映射");
    }

    // ── 10. 夜间不限速设置映射 ────────────────────────────────────────────

    private static void NightLimitMapping()
    {
        var settings = new AppSettings { UncappedAtNight = true, OffPeakStart = "23:30", OffPeakEnd = "06:15" };
        var options = KernelOptionsFactory.Create(settings);
        That(options.UncappedAtNight, "UncappedAtNight=true → 内核开启夜间豁免");
        Equal("23:30", options.OffPeakStart, "OffPeakStart 原样传递（解析留在内核侧）");
        Equal("06:15", options.OffPeakEnd, "OffPeakEnd 原样传递");

        settings.UncappedAtNight = false;
        That(!KernelOptionsFactory.Create(settings).UncappedAtNight, "UncappedAtNight=false → 内核关闭夜间豁免");

        // 默认 AppSettings：豁免默认关、窗口默认 23:00–07:00
        var defaults = KernelOptionsFactory.Create(new AppSettings());
        That(!defaults.UncappedAtNight, "默认 UncappedAtNight=false");
        Equal("23:00", defaults.OffPeakStart, "默认 OffPeakStart=23:00");
        Equal("07:00", defaults.OffPeakEnd, "默认 OffPeakEnd=07:00");
    }

    // ── 11. OffPeak 窗口判断 ──────────────────────────────────────────────

    private static void OffPeakWindowLogic()
    {
        // 跨午夜窗口 23:00→07:00：午夜两侧都算「夜间」
        That(RealDownloadKernel.InOffPeakWindow(At("23:00"), "23:00", "07:00"),
            "跨午夜窗口：23:00 起点闭区间 → 窗口内");
        That(RealDownloadKernel.InOffPeakWindow(At("23:30"), "23:00", "07:00"), "跨午夜窗口：23:30 → 窗口内");
        That(RealDownloadKernel.InOffPeakWindow(At("00:00"), "23:00", "07:00"), "跨午夜窗口：午夜 00:00 → 窗口内");
        That(RealDownloadKernel.InOffPeakWindow(At("06:59"), "23:00", "07:00"), "跨午夜窗口：06:59 → 窗口内");
        That(!RealDownloadKernel.InOffPeakWindow(At("07:00"), "23:00", "07:00"), "跨午夜窗口：07:00 终点开区间 → 窗口外");
        That(!RealDownloadKernel.InOffPeakWindow(At("12:00"), "23:00", "07:00"), "跨午夜窗口：正午 → 窗口外");
        That(!RealDownloadKernel.InOffPeakWindow(At("22:59"), "23:00", "07:00"), "跨午夜窗口：22:59 → 窗口外");

        // 正向窗口 01:00→05:00
        That(RealDownloadKernel.InOffPeakWindow(At("02:00"), "01:00", "05:00"), "正向窗口：02:00 → 窗口内");
        That(RealDownloadKernel.InOffPeakWindow(At("04:59"), "01:00", "05:00"), "正向窗口：04:59 → 窗口内");
        That(!RealDownloadKernel.InOffPeakWindow(At("05:00"), "01:00", "05:00"), "正向窗口：05:00 → 窗口外");
        That(!RealDownloadKernel.InOffPeakWindow(At("00:59"), "01:00", "05:00"), "正向窗口：00:59 → 窗口外");

        // 单位数小时："7:00" 合法
        That(RealDownloadKernel.InOffPeakWindow(At("06:30"), "23:00", "7:00"), "单位数小时 \"7:00\" 可解析");

        // 零长窗口与垃圾文本一律「无豁免」
        That(!RealDownloadKernel.InOffPeakWindow(At("08:00"), "08:00", "08:00"), "零长窗口 → 窗口外");
        That(!RealDownloadKernel.InOffPeakWindow(At("03:00"), "25:00", "07:00"), "小时越界 25:00 → 窗口外");
        That(!RealDownloadKernel.InOffPeakWindow(At("03:00"), "23:00", "07:60"), "分钟越界 07:60 → 窗口外");
        That(!RealDownloadKernel.InOffPeakWindow(At("03:00"), "abc", "07:00"), "垃圾起点 → 窗口外");
        That(!RealDownloadKernel.InOffPeakWindow(At("03:00"), "23:00", null), "null 终点 → 窗口外");
        That(!RealDownloadKernel.InOffPeakWindow(At("03:00"), " ", "07:00"), "空白起点 → 窗口外");
    }

    private static TimeSpan At(string hhmm)
    {
        var parts = hhmm.Split(':');
        return new TimeSpan(int.Parse(parts[0]), int.Parse(parts[1]), 0);
    }

    // ── 12. 本地签名库路径映射 ────────────────────────────────────────────

    private static void HashDatabaseMapping()
    {
        // 签名库跟着下载根目录走：DefaultSavePath 非空 → <目录>/.nettrans-checksums.json
        var options = KernelOptionsFactory.Create(new AppSettings { DefaultSavePath = @"D:\Downloads" });
        Equal(Path.Combine("D:\\Downloads", ".nettrans-checksums.json"), options.HashDatabasePath,
            "签名库应落在下载根目录下");

        // 未配置下载目录 → 不落盘（只保留进程内历史）
        Equal((string?)null, KernelOptionsFactory.Create(new AppSettings { DefaultSavePath = "" }).HashDatabasePath,
            "无 DefaultSavePath → 签名库不落盘");
        // AppSettings 默认值就是 D:\Downloads → 签名库跟着默认目录走
        Equal(Path.Combine("D:\\Downloads", ".nettrans-checksums.json"),
            KernelOptionsFactory.Create(new AppSettings()).HashDatabasePath,
            "默认设置下签名库应跟随默认下载目录");
    }

    // ── 基础设施 ──────────────────────────────────────────────────────────

    private static KernelOptions Create(
        string? limit = null,
        string? retry = null,
        int max = 3)
    {
        var settings = new AppSettings();
        if (limit is not null) settings.GlobalSpeedLimit = limit;
        if (retry is not null) settings.RetryPolicy = retry;
        settings.MaxSimultaneousDownloads = max;
        return KernelOptionsFactory.Create(settings);
    }

    private static void Run(string name, Action body)
    {
        int before = _failed;
        try { body(); }
        catch (Exception error)
        {
            _failed++;
            _failures.Add($"{name} :: {error.Message}");
            Console.WriteLine($"[FAIL] {name}");
            Console.WriteLine($"       {error.Message}");
            return;
        }
        if (_failed != before)
        {
            Console.WriteLine($"[FAIL] {name}（见上方逐条失败）");
        }
        else
        {
            Console.WriteLine($"[PASS] {name}");
        }
    }

    private static void That(bool condition, string what)
    {
        if (condition) { _passed++; return; }
        _failed++;
        _failures.Add(what);
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual)) { _passed++; return; }
        _failed++;
        _failures.Add($"{what}：期望 [{expected}]，实际 [{actual}]");
    }
}
