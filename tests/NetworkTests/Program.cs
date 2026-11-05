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

/// <summary>
/// NetTrans.Kernel 网络层测试（21 个方向，含站点提取器框架与 yt-dlp 兜底）。断言全部基于内核源码与 BCL 的真实行为；
/// 确认的内核问题以"已知失败"标注，不强行 PASS。
/// （历史注：2b filename 引号转义与 7b HLS 相对变体 URL 曾是已知内核缺陷，均已修复。）
/// </summary>
internal sealed partial class Program : TestKit
{
    private static readonly HttpClient ProbeHttp = new();
    private static readonly ProtocolDriverRegistry Registry = new(new KernelOptions(), ProbeHttp);

    private static int _passed, _failed, _knownFailed;
    private static readonly List<string> _failures = new();
    private static readonly List<string> _knownFailures = new();

    private static async Task<int> Main()
    {
        Console.WriteLine("NetTrans.Kernel 网络层测试");
        Console.WriteLine(new string('=', 64));

        using var server = new TestServer();
        Console.WriteLine($"测试服务器：{server.Base}");
        Console.WriteLine();

        await RunAsync("1a. 协议检测：URL 大写扩展名 .M3U8/.MPD/.TORRENT", () => DetectionUpperCaseAsync());
        await RunAsync("1b. 协议检测：fragment/端口/magnet 无 dn/ed2k 字段不全/裸域名", () => DetectionEdgesAsync());
        await RunAsync("2a. HTTP 命名：Content-Disposition filename*（RFC5987 百分号编码）", () => NamingStarAsync(server));
        await RunAsync("2b. HTTP 命名：filename 带引号转义", () => NamingQuotedEscapeAsync(server));
        await RunAsync("2c. HTTP 命名：无 disposition 时取 URL 末段", () => NamingUrlSegmentAsync(server));
        await RunAsync("2d. HTTP 命名：URL 以 / 结尾时的回退名", () => NamingTrailingSlashAsync(server));
        await RunAsync("3.  HTTP 重试：某次 GET 返回 500 后恢复，任务最终完成", () => RetryOn500Async(server));
        await RunAsync("4.  镜像中途失效：镜像断开连接后降级，下载仍完成", () => MirrorDropsAsync(server));
        await RunAsync("5a. 限速：全局 A 生效于单任务 B>A", () => GlobalLimitWinsAsync(server));
        await RunAsync("5b. 限速：SetSpeedLimit(\"不限\") 中途解除立即生效", () => UnlimitMidwayAsync(server));
        await RunAsync("5c. 限速：SpeedLimiter.SetLimit(0) 立即解除", LimiterUnitAsync);
        await RunAsync("6a. DASH：SegmentTimeline（t/d/r）计划", () => DashSegmentTimelineAsync(server));
        await RunAsync("6b. DASH：SegmentList（Initialization+SegmentURL）计划", () => DashSegmentListAsync(server));
        await RunAsync("6c. DASH：SegmentBase 回退为 ByteRanges 计划", () => DashSegmentBaseAsync(server));
        await RunAsync("7a. HLS：../ 相对路径/查询串/绝对 URL 分段 + 同码率取第一个变体", () => HlsEdgesAsync(server));
        await RunAsync("7b. HLS：master 相对变体 URL（按 RFC8216 应可用）", () => HlsRelativeVariantAsync(server));
        await RunAsync("8.  SOCKS5：RFC1928 握手 + CONNECT + 可读写流", Socks5RoundtripAsync);
        await RunAsync("9.  Remove 中途取消：TaskRemoved + 文件句柄释放", () => RemoveMidwayAsync(server));
        await RunAsync("10. Redownload：Done 归零重新下载 + 日志\"重新开始\"", () => RedownloadAsync(server));
        await RunAsync("11. 暂停/恢复边界：探测期暂停 + 反复暂停恢复", () => PauseResumeEdgesAsync(server));
        await RunAsync("12. AddTask 坏 URL：优雅失败不崩内核", () => BadUrlAsync(server));
        await RunAsync("13. Remove 竞态风暴：并发 Toggle/Remove/Redownload 不崩", () => RemoveRaceStormAsync(server));
        await RunAsync("14a. Range 续传：恢复请求应携带中段 Range 头", () => RangeResumeHeaderAsync(server));
        await RunAsync("14b. Range 背叛：GET 忽略 Range → 明确失败不静默错字节", () => RangeBetrayalAsync(server));
        await RunAsync("15a. MirrorSourceSet：去重/轮转/失败降级（单元）", MirrorSourceSetUnitAsync);
        await RunAsync("15b. MirrorSourceSet.ValidateAsync：筛选与取消传播（单元）", MirrorValidateUnitAsync);
        await RunAsync("15c. SpeedLimiter 分块精度 + 中途改限速（单元）", LimiterAccuracyUnitAsync);
        await RunAsync("16a. SegmentTable.Split 边界切分（单元）", SegmentSplitUnitAsync);
        await RunAsync("16b. checkpoint 往返与 Restore 校验（单元）", CheckpointRoundTripUnitAsync);
        await RunAsync("16c. ChunkedFileWriter 乱序直写 + 并发（单元）", ChunkedWriterUnitAsync);
        await RunAsync("17a. Checksums：算法识别 + 校验清单解析（单元）", ChecksumsUnitAsync);
        await RunAsync("17b. 期望哈希校验：MD5/SHA-256 匹配与不匹配", () => ExpectedHashAsync(server));
        await RunAsync("17c. 本地签名库：历史指纹记录/一致/变化 + 持久化", () => HashDatabaseAsync(server));
        await RunAsync("18a. SensitiveUrls：passkey/token 脱敏（单元）", SensitiveUrlsUnitAsync);
        await RunAsync("18b. PT magnet：展示脱敏 + tracker 列表 + 原始 URL 保留", () => PtMagnetAsync(server));
        await RunAsync("18c. PT 种子：tracker 脱敏 + 做种占位符渲染", () => PtTorrentSeedAsync(server));
        await RunAsync("19a. 小文件内存缓冲：完成前不落盘/无断点/暂停丢弃/目录自建", () => MemorySinkAsync(server));
        await RunAsync("19b. DirectSink 聚合：写合并正确性 + 乱序断续（单元）", DirectSinkUnitAsync);
        await RunAsync("19c. 真流式：无 ContentLength 的 HTTP 响应应完成", () => TrueStreamingAsync(server));
        await RunAsync("20a. 站点框架：站点提取器优先于协议驱动 + 失败/放弃回退", () => SiteFrameworkAsync(server));
        await RunAsync("20b. Bilibili 内核级：view+playurl 提取 → 下载完成（Referer/UA 必须在传）", () => BilibiliKernelAsync(server));
        await RunAsync("20c. Bilibili 提取器单元：分P选择/画质标签/错误路径", () => BilibiliExtractorUnitAsync(server));
        await RunAsync("20d. X/Twitter：变体排序与 registry 选择", () => TwitterAsync(server));
        await RunAsync("20e. YouTube：直链格式选择/加密格式拒绝/404 顺延下一流", () => YouTubeAsync(server));
        await RunAsync("21a. yt-dlp 映射单元：formats 过滤/排序/字幕/流头", YtDlpMappingUnitAsync);
        await RunAsync("21b. yt-dlp 注册表：兜底命中最优流/默认关闭/缺二进制回退", () => YtDlpRegistryAsync(server));
        await RunAsync("21c. yt-dlp 坏输出与空 formats：异常回退/放弃认领", () => YtDlpBadOutputAsync(server));
        await RunAsync("22a. 完成后动作：任务级 runner 恰一次 + 事件字段", () => CompletionActionTaskAsync(server));
        await RunAsync("22b. 完成后动作：全局「全部完成」才收尾触发", () => CompletionActionAllAsync(server));
        await RunAsync("22c. 完成后动作：异常 runner 不崩内核 + 命令展开", () => CompletionActionErrorAsync(server));
        await RunAsync("23a. 队列调度：到点批量开始/并发槽语义/队列外不受影响", () => QueueScheduleStartAsync(server));
        await RunAsync("23b. 队列调度：跨午夜窗口边界（22:59/23:00/06:59/07:00）", () => QueueScheduleMidnightAsync(server));
        await RunAsync("23c. 队列调度：仅停止时间窗口 + 非法 HH:mm 拒绝", () => QueueScheduleStopOnlyAsync(server));
        await RunAsync("24a. 控制API：POST/GET/DELETE 任务 + 404", () => ControlApiBasicsAsync(server));
        await RunAsync("24b. 控制API：仅环回放行 + 非环回 403", () => ControlApiTrustAsync(server));
        await RunAsync("24c. 控制API：端口回退 + pause/resume/409", () => ControlApiPortAndToggleAsync(server));

        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"结果：{_passed} 通过，{_knownFailed} 已知失败（内核问题），{_failed} 失败");
        foreach (var item in _knownFailures) Console.WriteLine("  [已知失败] " + item);
        foreach (var item in _failures) Console.WriteLine("  [FAIL] " + item);
        return _failed == 0 ? 0 : 1;
    }
    // ── 基础设施 ──────────────────────────────────────────────────────────

    private static async Task RunAsync(string name, Func<Task> body, bool knownBug = false)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await body();
            Interlocked.Increment(ref _passed);
            Console.WriteLine($"[PASS] {name}（{sw.Elapsed.TotalSeconds:0.0}s）");
        }
        catch (Exception error)
        {
            if (knownBug)
            {
                Interlocked.Increment(ref _knownFailed);
                _knownFailures.Add($"{name} :: {error.Message}");
                Console.WriteLine($"[已知失败] {name}（{sw.Elapsed.TotalSeconds:0.0}s）");
                Console.WriteLine($"           {error.Message}");
            }
            else
            {
                Interlocked.Increment(ref _failed);
                _failures.Add($"{name} :: {error.Message}");
                Console.WriteLine($"[FAIL] {name}（{sw.Elapsed.TotalSeconds:0.0}s）");
                Console.WriteLine($"       {error.Message}");
            }
        }
    }

    private static T AssertPlan<T>(ResourceProbe probe, string what) where T : TransferPlan
    {
        if (probe.Plan is not T plan) throw new Exception($"{what}：实际 {probe.Plan.GetType().Name}（{probe.Plan}）");
        return plan;
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
        var dir = Path.Combine(Path.GetTempPath(), "nettrans-net-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // 与 RealDownloadKernel.TargetPath 对应：SaveTo 无扩展名 → SaveTo/Sanitize(Name)
    private static string TargetPath(string dir, DownloadTask task) => Path.Combine(dir, task.Name);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitSignalAsync(Task signal, string what, int timeoutMs)
    {
        var finished = await Task.WhenAny(signal, Task.Delay(timeoutMs));
        if (finished != signal) throw new Exception($"等待超时（{timeoutMs} ms）：{what}");
        if (signal.IsFaulted) throw signal.Exception!.GetBaseException();
    }

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

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0) throw new IOException("流提前结束");
            read += n;
        }
    }
}
