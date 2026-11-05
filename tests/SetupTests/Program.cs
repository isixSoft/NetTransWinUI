using System.Reflection;
using System.Xml.Linq;
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
        Run("13. 系统通知：开关开→sink 收到标题/正文；关→不发；null/异常不崩", NotificationContract);
        Run("14. 系统通知开关映射：NotifyOnCompletion → NotifyOnComplete", NotifyOnCompleteMapping);
        Run("15. 限速档位：默认档位/Resolve/Switch 即时下发/未知档位不动内核/往返持久化", SpeedProfilesContract);
        Run("16. 文件名模板：占位符渲染/非法字符/未知占位符/空模板回退/工厂映射", FileNameTemplateContract);
        Run("17. Mark-of-the-Web 开关映射：MarkOfTheWeb → 内核", MarkOfTheWebMapping);
        Run("18. 哈希展示比对：三算法已知向量/长度识别算法/清洗匹配/两值展示/非法输入 null", HashCompareContract);
        Run("19. 启动恢复档位：非默认档位下发/全速·空·未知档位不动内核", SpeedProfileRestoreContract);
        Run("20. 多语言 resw：en 与 zh-CN 键集合一致（P2-9）", L10nParity);
        Run("21. 多语言 resw：7 个工具栏 tooltip 键 + 设置页文案键齐全且值非空（P2-9）", L10nRequiredKeys);
        Run("22. Language 设置：默认 auto + 往返持久化（P2-9）", LanguageFieldContract);
        Run("23. 外部引擎命令模板：原样透传进 KernelOptions（圈22）", ExternalEngineCommandMapping);
        Run("24. yt-dlp 兜底开关：EnableYtDlp → 内核（圈22）", EnableYtDlpMapping);
        Run("25. 完成后动作：WhenAllComplete 下拉值 → AfterCompletion enum（圈22）", AfterCompletionMapping);

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
            Language = "en",
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

    // ── 13/14. 系统通知服务契约（P1-4：接口 + 设置开关；AppNotification 接线归 UI 侧）──

    private sealed class RecordingSink : INotificationSink
    {
        public List<(string Title, string Body)> Calls { get; } = new();
        public bool Throw { get; set; }

        public void Notify(string title, string body)
        {
            if (Throw) throw new InvalidOperationException("sink 故意抛异常");
            Calls.Add((title, body));
        }
    }

    private static DownloadTask NewTask(string name) => new()
    {
        Id = 1,
        Name = name,
        Host = "example.org",
        Kind = FileKind.Zip,
        Size = 100,
        Category = "soft",
    };

    private static void NotificationContract()
    {
        var sink = new RecordingSink();
        var options = new KernelOptions { NotificationSink = sink, NotifyOnComplete = true };
        CompletionNotifications.Emit(options, NewTask("setup.exe"));
        Equal(1, sink.Calls.Count, "开关开 + sink → 恰一次通知");
        Equal("下载完成", sink.Calls[0].Title, "通知标题");
        That(sink.Calls[0].Body.Contains("setup.exe"), "正文应包含任务名");

        options = new KernelOptions { NotificationSink = sink, NotifyOnComplete = false };
        CompletionNotifications.Emit(options, NewTask("setup.exe"));
        Equal(1, sink.Calls.Count, "开关关 → 不发通知");

        // sink 未注入（headless 默认）与 sink 抛异常都不得影响内核
        CompletionNotifications.Emit(new KernelOptions { NotifyOnComplete = true }, NewTask("x.iso"));
        That(true, "sink null → 不崩");
        var throwing = new RecordingSink { Throw = true };
        CompletionNotifications.Emit(
            new KernelOptions { NotificationSink = throwing, NotifyOnComplete = true }, NewTask("x.iso"));
        That(true, "sink 抛异常 → 吞掉不崩");
    }

    private static void NotifyOnCompleteMapping()
    {
        That(KernelOptionsFactory.Create(new AppSettings()).NotifyOnComplete, "默认设置 → 通知开");
        That(!KernelOptionsFactory.Create(new AppSettings { NotifyOnCompletion = false }).NotifyOnComplete,
            "NotifyOnCompletion=false → 内核通知关");
        That(KernelOptionsFactory.Create(new AppSettings { NotifyOnCompletion = true }).NotifyOnComplete,
            "NotifyOnCompletion=true → 内核通知开");
    }

    // ── 15. 限速档位 profile（P2-2） ──────────────────────────────────────

    private static void SpeedProfilesContract()
    {
        // 出厂默认档位：全速（不限）+ 浏览优先（1 MiB/s），对标 Motrix 多 profile
        var defaults = SpeedProfileService.Defaults;
        Equal(2, defaults.Count, "默认档位应有 2 个");
        Equal("全速", defaults[0].Name, "默认档位 1 名称");
        Equal((long?)null, defaults[0].BytesPerSecond, "「全速」→ null（不限）");
        Equal("浏览优先", defaults[1].Name, "默认档位 2 名称");
        Equal(1048576L, defaults[1].BytesPerSecond, "「浏览优先」→ 1 MiB/s");

        // Resolve：档位名 → 内核字节数
        var settings = new AppSettings();
        var service = new SpeedProfileService(settings);
        Equal((long?)null, service.Resolve("全速"), "Resolve(全速) → null");
        Equal(1048576L, service.Resolve("浏览优先"), "Resolve(浏览优先) → 1048576");
        Equal((long?)null, service.Resolve("不存在的档位"), "Resolve(未知档位) → null 兜底");

        // Switch：经委托即时下发内核，并记录当前档位
        long? applied = 42L;
        That(service.Switch("浏览优先", v => applied = v), "Switch(浏览优先) 应成功");
        Equal(1048576L, applied, "Switch 应把 1048576 下发给内核委托");
        Equal("浏览优先", settings.ActiveSpeedProfile, "切换后 ActiveSpeedProfile 记录档位名");

        That(service.Switch("全速", v => applied = v), "Switch(全速) 应成功");
        Equal((long?)null, applied, "Switch(全速) 应下发 null（解除限速）");
        Equal("全速", settings.ActiveSpeedProfile, "切回全速后 ActiveSpeedProfile 更新");

        // 未知档位：返回 false，不触碰内核、不改状态
        applied = 42L;
        That(!service.Switch("不存在", v => applied = v), "未知档位 Switch 应失败");
        Equal(42L, applied, "未知档位不得触碰内核委托");
        Equal("全速", settings.ActiveSpeedProfile, "失败切换不改 ActiveSpeedProfile");

        // 自定义档位优先：settings.SpeedProfiles 非空时覆盖默认列表
        settings.SpeedProfiles.Add(new SpeedProfile("自定义档", 2097152L));
        Equal(1, service.Profiles.Count, "自定义列表非空 → Profiles 用自定义列表");
        Equal("自定义档", service.Profiles[0].Name, "自定义档位名");
        Equal(2097152L, service.Resolve("自定义档"), "Resolve(自定义档) → 2097152");

        // 往返：档位列表 + 当前档位随设置持久化
        ISettingsStore store = new JsonSettingsStore();
        var saved = new AppSettings
        {
            SpeedProfiles = { new SpeedProfile("夜间小水管", 262144L) },
            ActiveSpeedProfile = "夜间小水管",
        };
        store.Save(saved);
        var loaded = store.Load();
        Equal(1, loaded.SpeedProfiles.Count, "档位列表应随设置持久化");
        Equal("夜间小水管", loaded.SpeedProfiles[0].Name, "往返后档位名");
        Equal(262144L, loaded.SpeedProfiles[0].BytesPerSecond, "往返后档位限速值");
        Equal("夜间小水管", loaded.ActiveSpeedProfile, "往返后当前档位");
    }

    // ── 19. 启动恢复档位（设置页三件套接线） ──────────────────────────────

    private static void SpeedProfileRestoreContract()
    {
        // 上次选了「浏览优先」：启动恢复应把 1 MiB/s 下发内核
        var settings = new AppSettings { ActiveSpeedProfile = "浏览优先" };
        var service = new SpeedProfileService(settings);
        long? applied = 42L;
        That(service.Restore(v => applied = v), "非默认档位启动恢复应成功");
        Equal(1048576L, applied, "恢复应把档位字节值下发给内核委托");
        Equal("浏览优先", settings.ActiveSpeedProfile, "恢复不改当前档位名");

        // 默认「全速」：不下发——内核保持 KernelOptionsFactory 映射的旧全局限速值
        var plain = new SpeedProfileService(new AppSettings());
        applied = 42L;
        That(!plain.Restore(v => applied = v), "默认全速不应下发内核");
        Equal(42L, applied, "全速启动恢复不得触碰内核委托");

        // 空档位名：同样不下发
        var blank = new SpeedProfileService(new AppSettings { ActiveSpeedProfile = "" });
        applied = 42L;
        That(!blank.Restore(v => applied = v), "空档位名不应下发内核");
        Equal(42L, applied, "空档位名不得触碰内核委托");

        // 记录的档位已不存在（如自定义列表被清空）：Switch 失败路径，状态不动
        var staleSettings = new AppSettings { ActiveSpeedProfile = "已删档位" };
        applied = 42L;
        That(!new SpeedProfileService(staleSettings).Restore(v => applied = v), "未知档位启动恢复应失败");
        Equal(42L, applied, "未知档位不得触碰内核委托");
        Equal("已删档位", staleSettings.ActiveSpeedProfile, "失败的恢复不改当前档位名");
    }

    // ── 16. 文件名模板（P2-3） ────────────────────────────────────────────

    private static void FileNameTemplateContract()
    {
        // 空模板 → null（内核回退默认命名）
        Equal((string?)null, FileNameTemplate.Render(null, "video.mp4", "example.org"), "null 模板 → null");
        Equal((string?)null, FileNameTemplate.Render("", "video.mp4", "example.org"), "空串模板 → null");
        Equal((string?)null, FileNameTemplate.Render("   ", "video.mp4", "example.org"), "空白模板 → null");

        // 占位符：{站点}=主机、{标题}=去扩展名、{ext}=扩展名（不含点）
        Equal("video.mp4", FileNameTemplate.Render("{标题}.{ext}", "video.mp4", "example.org"), "单段模板渲染");
        Equal("example.org-video.mp4", FileNameTemplate.Render("{站点}-{标题}.{ext}", "video.mp4", "example.org"), "{站点}-{标题}.{ext}");
        Equal(Path.Combine("example.org", "video.mp4"),
            FileNameTemplate.Render("{站点}/{标题}.{ext}", "video.mp4", "example.org"), "'/' 应作为子目录分隔");
        Equal(Path.Combine("example.org", "README"),
            FileNameTemplate.Render("{站点}/{标题}", "README", "example.org"), "无扩展名：{标题} 保持原名、{ext} 不出现");

        // 非法字符：Windows 非法字符在段内替换为 '_'
        Equal("127.0.0.1_8080", FileNameTemplate.Render("{站点}", "a.mp4", "127.0.0.1:8080"), "主机中的冒号应替换为下划线");
        Equal("doc_v1.bin", FileNameTemplate.Render("{标题}?v1.{ext}", "doc.bin", "example.org"), "模板自带 '?' 应替换为 '_'");

        // 未识别的占位符原样保留（{} 是合法文件名字符）
        Equal("h-{未知}.mp4", FileNameTemplate.Render("{站点}-{未知}.{ext}", "a.mp4", "h"), "未知占位符应原样保留");

        // 全空段（如 "///"）→ 渲染为空 → null 回退
        Equal((string?)null, FileNameTemplate.Render("///", "a.mp4", "h"), "全空段模板 → null");

        // 工厂映射：AppSettings.NameTemplate 原样进内核
        Equal("", KernelOptionsFactory.Create(new AppSettings()).NameTemplate, "默认无模板");
        Equal("{站点}/{标题}.{ext}",
            KernelOptionsFactory.Create(new AppSettings { NameTemplate = "{站点}/{标题}.{ext}" }).NameTemplate,
            "NameTemplate 原样映射");
    }

    // ── 17. Mark-of-the-Web 开关映射（P2-4） ──────────────────────────────

    private static void MarkOfTheWebMapping()
    {
        That(KernelOptionsFactory.Create(new AppSettings()).MarkOfTheWeb, "默认设置 → 来源标记开");
        That(!KernelOptionsFactory.Create(new AppSettings { MarkOfTheWeb = false }).MarkOfTheWeb,
            "MarkOfTheWeb=false → 内核来源标记关");
        That(KernelOptionsFactory.Create(new AppSettings { MarkOfTheWeb = true }).MarkOfTheWeb,
            "MarkOfTheWeb=true → 内核来源标记开");
    }

    // ── 18. 哈希展示与手动比对（P2-7） ────────────────────────────────────

    private static void HashCompareContract()
    {
        // 已知向量（NIST："abc"）
        var file = Path.Combine(Path.GetTempPath(), "hash-vec-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "abc");
        try
        {
            var (md5, sha1, sha256) = HashCompareService.Compute(file);
            Equal("900150983cd24fb0d6963f7d28e17f72", md5, "MD5 已知向量");
            Equal("a9993e364706816aba3e25717850c26c9cd0d89d", sha1, "SHA-1 已知向量");
            Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", sha256, "SHA-256 已知向量");

            // 粘贴匹配：大小写与空白清洗
            var match = HashCompareService.Verify(
                "  BA7816BF8F01CFEA414140DE5DAE2223 B00361A396177A9CB410FF61F20015AD ", sha256);
            That(match is { Matches: true }, "正确 SHA-256（大小写/空白清洗后）应匹配");
            Equal("SHA-256", match!.Algorithm, "64 位 hex → SHA-256");
            Equal(sha256, match.Actual, "结果应展示实际值（统一小写）");

            // 不匹配：两值都展示
            var mismatch = HashCompareService.Verify(
                "0000000000000000000000000000000000000000000000000000000000000000", sha256);
            That(mismatch is { Matches: false }, "错误哈希应不匹配");
            Equal("0000000000000000000000000000000000000000000000000000000000000000",
                mismatch!.Expected, "不匹配时应展示期望值");
            Equal(sha256, mismatch.Actual, "不匹配时也应展示实际值");

            // 按长度识别算法
            Equal("MD5", HashCompareService.Verify(md5, md5)?.Algorithm, "32 位 hex → MD5");
            Equal("SHA-1", HashCompareService.Verify(sha1, sha1)?.Algorithm, "40 位 hex → SHA-1");

            // 无法识别 → null
            That(HashCompareService.Verify("xyz", sha256) is null, "非十六进制 → null");
            That(HashCompareService.Verify("1234", sha256) is null, "长度无法识别 → null");
            That(HashCompareService.Verify("", sha256) is null, "空粘贴 → null");
            That(HashCompareService.Verify(null, sha256) is null, "null 粘贴 → null");
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ── 20/21/22. 多语言资源（P2-9） ──────────────────────────────────────

    /// <summary>主界面七图标工具栏里 4 个静态 tooltip（x:Uid 直取 resw）。</summary>
    private static readonly string[] L10nToolbarKeys =
    {
        "ToolbarAdd.TooltipService.ToolTip",
        "ToolbarOpenFolder.TooltipService.ToolTip",
        "ToolbarSniff.TooltipService.ToolTip",
        "ToolbarSettings.TooltipService.ToolTip",
    };

    /// <summary>动态 3 个 tooltip 由 ShellViewModel 经 L10n 取这两组键。</summary>
    private static readonly string[] L10nTooltipKeys =
    {
        "TooltipToggleAllStart",
        "TooltipToggleAllPause",
        "TooltipRemove",
        "TooltipRemoveCount",
        "TooltipInspectorOpen",
        "TooltipInspectorClose",
    };

    /// <summary>设置页纯展示文案（标题/分组/行标签/占位符/说明/按钮）。</summary>
    private static readonly string[] L10nSettingsKeys =
    {
        "SettingsSheet.Title",
        "SettingsSheet.RightLabel",
        "SettingsGroupDownload.Text",
        "SettingsGroupQueue.Text",
        "SettingsGroupBehavior.Text",
        "SettingsRowSavePath.Label",
        "SettingsRowFolders.Label",
        "SettingsRowNameTemplate.Label",
        "SettingsNameTemplate.PlaceholderText",
        "SettingsRowConcurrency.Label",
        "SettingsRowGlobalLimit.Label",
        "SettingsRowProfile.Label",
        "SettingsRowNight.Label",
        "SettingsRowSchedule.Label",
        "SettingsRowRetry.Label",
        "SettingsRowAfter.Label",
        "SettingsRowLanguage.Label",
        "SettingsNoteLanguage.Text",
        "SettingsRowClipboard.Label",
        "SettingsRowNotify.Label",
        "SettingsRowMotw.Label",
        "SettingsRowVerify.Label",
        "SettingsRowScan.Label",
        "SettingsRowEdge.Label",
        "SettingsRowIsland.Label",
        "SettingsRowBossKey.Label",
        "SettingsNoteBossKey.Text",
        "SettingsNotePortable.Text",
        "SettingsOptimize.Content",
        "LangAuto",
        "LangEn",
        "LangZhCN",
        "NotifyClipboardStarted",
        "NotifyClipboardStopped",
        "NotifyConcurrencyOpt",
        "SettingsGroupEngines.Text",
        "SettingsRowYtDlp.Label",
        "SettingsRowEngineCmd.Label",
        "SettingsEngineCmd.PlaceholderText",
        "SettingsNoteEngine.Text",
    };

    private static string[] AllL10nKeys =>
        L10nToolbarKeys.Concat(L10nTooltipKeys).Concat(L10nSettingsKeys).ToArray();

    /// <summary>resw 即普通 XML，LINQ to XML 直解析——测试工程保持零 NuGet。</summary>
    private static Dictionary<string, string> LoadResw(string lang)
    {
        var root = FindRepoRoot()
            ?? throw new InvalidOperationException("沿 BaseDirectory 上溯找不到仓库根（NetTrans/NetTrans.csproj）");
        var file = Path.Combine(root.FullName, "NetTrans", "Strings", lang, "Resources.resw");
        That(File.Exists(file), $"{lang} Resources.resw 应存在：{file}");
        var doc = XDocument.Load(file);
        return doc.Root!.Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")?.Value ?? "");
    }

    private static DirectoryInfo? FindRepoRoot()
    {
        // Binaries may live outside the repo (--artifacts-path /tmp/...), so the
        // seed path is baked in at build time via AssemblyMetadata("RepoRoot").
        var seed = Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
        if (seed is null) return null;
        var dir = new DirectoryInfo(seed);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NetTrans", "NetTrans.csproj"))) return dir;
            dir = dir.Parent;
        }
        return null;
    }

    private static void L10nParity()
    {
        var en = LoadResw("en");
        var zh = LoadResw("zh-CN");
        var missingInZh = en.Keys.Except(zh.Keys).ToList();
        var missingInEn = zh.Keys.Except(en.Keys).ToList();
        Equal(0, missingInZh.Count, $"zh-CN 相对 en 缺少的键：{string.Join(", ", missingInZh)}");
        Equal(0, missingInEn.Count, $"en 相对 zh-CN 缺少的键：{string.Join(", ", missingInEn)}");
    }

    private static void L10nRequiredKeys()
    {
        var en = LoadResw("en");
        var zh = LoadResw("zh-CN");
        foreach (var key in AllL10nKeys)
        {
            That(en.TryGetValue(key, out var enVal) && enVal.Trim().Length > 0, $"en 应含非空键 {key}");
            That(zh.TryGetValue(key, out var zhVal) && zhVal.Trim().Length > 0, $"zh-CN 应含非空键 {key}");
        }

        // 计数 tooltip 的占位符契约：两语言都必须带 {0}
        if (en.TryGetValue("TooltipRemoveCount", out var enCount) && zh.TryGetValue("TooltipRemoveCount", out var zhCount))
        {
            That(enCount.Contains("{0}"), $"en TooltipRemoveCount 应含 {{0}} 占位符，实际 [{enCount}]");
            That(zhCount.Contains("{0}"), $"zh-CN TooltipRemoveCount 应含 {{0}} 占位符，实际 [{zhCount}]");
        }
    }

    private static void LanguageFieldContract()
    {
        Equal("auto", new AppSettings().Language, "默认语言 auto（跟随系统）");

        ISettingsStore store = new JsonSettingsStore();
        store.Save(new AppSettings { Language = "zh-CN" });
        Equal("zh-CN", store.Load().Language, "Language 应随设置持久化");

        store.Save(new AppSettings { Language = "en" });
        Equal("en", store.Load().Language, "Language 往返 en");
    }

    // ── 23/24/25. 圈22：ExternalEngineCommand / EnableYtDlp / AfterCompletion ──

    private static void ExternalEngineCommandMapping()
    {
        // 命令模板字符串原样透传进内核占位符不变，外壳在 TransferSession.Engines.cs 执行
        var settings = new AppSettings { ExternalEngineCommand = "aria2c --select-file {files} -x 16 -s 16 -o '{path}' '{url}'" };
        var options = KernelOptionsFactory.Create(settings);
        Equal("aria2c --select-file {files} -x 16 -s 16 -o '{path}' '{url}'", options.ExternalEngineCommand,
            "ExternalEngineCommand 应原样透传");

        // 默认空 → null（内核走 EngineRunner 委托通路）
        That(KernelOptionsFactory.Create(new AppSettings()).ExternalEngineCommand is null,
            "默认未设置命令模板 → null");

        // 空白 → null（与其他字符串字段一致）
        That(KernelOptionsFactory.Create(new AppSettings { ExternalEngineCommand = "   " }).ExternalEngineCommand is null,
            "空白命令模板 → null");
    }

    private static void EnableYtDlpMapping()
    {
        That(!KernelOptionsFactory.Create(new AppSettings()).EnableYtDlp, "默认 EnableYtDlp=false");
        That(KernelOptionsFactory.Create(new AppSettings { EnableYtDlp = true }).EnableYtDlp,
            "EnableYtDlp=true → 内核开关开");
    }

    private static void AfterCompletionMapping()
    {
        // 下拉值 → AfterCompletion enum
        Equal(AfterCompletion.None,
            KernelOptionsFactory.Create(new AppSettings { WhenAllComplete = "无操作" }).AllComplete,
            "\"无操作\" → None");
        Equal(AfterCompletion.ExitApp,
            KernelOptionsFactory.Create(new AppSettings { WhenAllComplete = "退出程序" }).AllComplete,
            "\"退出程序\" → ExitApp");
        Equal(AfterCompletion.Hibernate,
            KernelOptionsFactory.Create(new AppSettings { WhenAllComplete = "休眠" }).AllComplete,
            "\"休眠\" → Hibernate");
        Equal(AfterCompletion.Shutdown,
            KernelOptionsFactory.Create(new AppSettings { WhenAllComplete = "关机" }).AllComplete,
            "\"关机\" → Shutdown");

        // 非法/空值 → 兜底 None（不触发任何动作）
        Equal(AfterCompletion.None,
            KernelOptionsFactory.Create(new AppSettings { WhenAllComplete = "" }).AllComplete,
            "空 WhenAllComplete → None");
        Equal(AfterCompletion.None,
            KernelOptionsFactory.Create(new AppSettings { WhenAllComplete = "garbage" }).AllComplete,
            "未知值 → None");

        // 默认 AppSettings.WhenAllComplete = "无操作"
        Equal(AfterCompletion.None,
            KernelOptionsFactory.Create(new AppSettings()).AllComplete,
            "默认设置 → None");
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
