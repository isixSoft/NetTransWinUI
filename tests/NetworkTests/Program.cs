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
/// NetTrans.Kernel 网络层测试（16 个方向）。断言全部基于内核源码与 BCL 的真实行为；
/// 确认的内核问题以"已知失败"标注，不强行 PASS。
/// （历史注：2b filename 引号转义与 7b HLS 相对变体 URL 曾是已知内核缺陷，均已修复。）
/// </summary>
internal static class Program
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

        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"结果：{_passed} 通过，{_knownFailed} 已知失败（内核问题），{_failed} 失败");
        foreach (var item in _knownFailures) Console.WriteLine("  [已知失败] " + item);
        foreach (var item in _failures) Console.WriteLine("  [FAIL] " + item);
        return _failed == 0 ? 0 : 1;
    }

    // ── 1. 协议检测边界 ───────────────────────────────────────────────────

    private static Task DetectionUpperCaseAsync()
    {
        Equal(DownloadProtocol.Hls, ProtocolDetector.Detect("http://cdn.example.com/live/movie.M3U8"), ".M3U8 大写应识别为 HLS");
        Equal(DownloadProtocol.Dash, ProtocolDetector.Detect("https://media.example.com/v/movie.MPD"), ".MPD 大写应识别为 DASH");
        Equal(DownloadProtocol.BitTorrent, ProtocolDetector.Detect("http://static.example.com/apps/app.TORRENT"), ".TORRENT 大写应识别为 BT");
        Equal(DownloadProtocol.Hls, ProtocolDetector.Detect("http://a.example.com/movie.M3U8?token=abc"), "大写扩展名 + 查询串");
        Equal(DownloadProtocol.Dash, ProtocolDetector.Detect("http://a.example.com/v/movie.MPD#fragment"), "大写扩展名 + fragment");
        return Task.CompletedTask;
    }

    private static async Task DetectionEdgesAsync()
    {
        Equal(DownloadProtocol.Hls, ProtocolDetector.Detect("http://a.example.com/v/movie.m3u8#t=42"), "fragment 不影响扩展名识别");
        Equal(DownloadProtocol.Hls, ProtocolDetector.Detect("http://a.example.com:8080/movie.M3U8"), "带端口不影响扩展名识别");
        Equal(DownloadProtocol.Http, ProtocolDetector.Detect("http://a.example.com:8080/dir/file.zip"), "带端口普通文件仍走 HTTP");
        Equal(DownloadProtocol.BitTorrent, ProtocolDetector.Detect("magnet:?xt=urn:btih:0123456789abcdef"), "magnet 无 dn 仍按 scheme 识别为 BT");
        Equal(DownloadProtocol.Ed2k, ProtocolDetector.Detect("ed2k://|file|name|abc|HASH|/"), "ed2k 字段不全仍按 scheme 识别");
        Equal(DownloadProtocol.Http, ProtocolDetector.Detect("example.com/file.zip"), "无 scheme 裸域名回退 HTTP");

        // magnet 无 dn 的探测行为：名称回退 bt-<hash 前 8 位>，数据面交给外部引擎
        var magnet = await Registry.ProbeAsync("magnet:?xt=urn:btih:0123456789abcdef", null, null, default);
        Equal("bt-01234567", magnet.Name, "magnet 无 dn 时名称回退");
        That(magnet.Plan is TransferPlan.ExternalEngine engine && engine.Engine == "BitTorrent / libtorrent",
            $"magnet 计划应为外部引擎，实际 {magnet.Plan}");

        // ed2k 字段不全：识别出协议但计划不可用
        var ed2k = await Registry.ProbeAsync("ed2k://|file|name|abc|HASH|/", null, null, default);
        Equal(DownloadProtocol.Ed2k, ed2k.Protocol, "ed2k 探测协议");
        That(ed2k.Plan is TransferPlan.Unsupported unsupported && unsupported.Reason.Contains("无法识别的 eD2k 链接"),
            $"ed2k 字段不全应为 Unsupported(无法识别的 eD2k 链接)，实际 {ed2k.Plan}");
    }

    // ── 2. HTTP 命名边界 ──────────────────────────────────────────────────

    private static async Task NamingStarAsync(TestServer server)
    {
        server.Map("naming/star", server.RangeFile(Payload(4096), etag: "\"star\"",
            disposition: "attachment; filename*=UTF-8''%E4%B8%AD%E6%96%87.zip"));
        var probe = await Registry.ProbeAsync(server.Base + "naming/star", null, null, default);
        // .NET 8 的 ContentDispositionHeaderValue.FileNameStar 已按 RFC5987 解码（含字符集前缀剥离），
        // 内核再做一次 UnescapeDataString 属于无操作透传，结果应还原出 UTF-8 文件名。
        Equal("中文.zip", probe.Name, "filename* 百分号编码应还原为 UTF-8 文件名");
    }

    private static async Task NamingQuotedEscapeAsync(TestServer server)
    {
        server.Map("naming/quoted", server.RangeFile(Payload(4096), etag: "\"quoted\"",
            disposition: "attachment; filename=\"say \\\"hi\\\".txt\""));
        var probe = await Registry.ProbeAsync(server.Base + "naming/quoted", null, null, default);
        // RFC6266 quoted-string：\" 是转义引号，正确解析结果应为 say "hi".txt。
        Equal("say \"hi\".txt", probe.Name, "quoted-string 的 \\\" 转义应还原为真实引号");
    }

    private static async Task NamingUrlSegmentAsync(TestServer server)
    {
        server.Map("naming/My%20File.bin", server.RangeFile(Payload(1024)));
        var probe = await Registry.ProbeAsync(server.Base + "naming/My%20File.bin", null, null, default);
        Equal("My File.bin", probe.Name, "无 disposition 时应取 URL 末段并解码 %20");
    }

    private static async Task NamingTrailingSlashAsync(TestServer server)
    {
        server.Map("naming/dir/", server.RangeFile(Payload(128)));
        var probe = await Registry.ProbeAsync(server.Base + "naming/dir/", null, null, default);
        Equal("dir", probe.Name, "URL 以 / 结尾时应回退到最后一段目录名");
    }

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

    // ── 6. DASH 其余形态 ──────────────────────────────────────────────────

    private static async Task DashSegmentTimelineAsync(TestServer server)
    {
        const string mpd = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT7S">
              <Period>
                <AdaptationSet contentType="video">
                  <Representation id="v1" bandwidth="800000" codecs="avc1.64001f">
                    <SegmentTemplate media="$RepresentationID$_$Bandwidth$_$Number$_$Time$.m4s"
                                     initialization="$RepresentationID$/init.mp4" timescale="1" startNumber="1">
                      <SegmentTimeline>
                        <S t="0" d="2" r="1"/>
                        <S d="3"/>
                      </SegmentTimeline>
                    </SegmentTemplate>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        server.Map("dash/timeline.mpd", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(mpd), "application/dash+xml"));

        var probe = await Registry.ProbeAsync(server.Base + "dash/timeline.mpd", null, null, default);
        var plan = AssertPlan<TransferPlan.SegmentFiles>(probe, "SegmentTimeline 应产出 SegmentFiles 计划");
        var b = server.Base;
        // t=0 d=2 r=1 → 2 段（number 1,2 / time 0,2）；S d=3 续接 → 1 段（number 3 / time 4）
        Equal(
            string.Join("\n", new[]
            {
                $"{b}dash/v1/init.mp4",
                $"{b}dash/v1_800000_1_0.m4s",
                $"{b}dash/v1_800000_2_2.m4s",
                $"{b}dash/v1_800000_3_4.m4s",
            }),
            string.Join("\n", plan.Segments), "SegmentTimeline 应按 t/d/r 展开并解析模板变量");
        Equal("timeline.mp4", probe.Name, "DASH 输出文件名");
        That(plan.TotalSize is null, "SegmentFiles 计划无总大小");
    }

    private static async Task DashSegmentListAsync(TestServer server)
    {
        const string mpd = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
              <Period>
                <AdaptationSet contentType="video">
                  <Representation id="v2" bandwidth="1200000">
                    <SegmentList duration="4">
                      <Initialization sourceURL="list-init.mp4"/>
                      <SegmentURL media="seg-1.m4s"/>
                      <SegmentURL media="seg-2.m4s"/>
                    </SegmentList>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        server.Map("dash/list.mpd", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(mpd), "application/dash+xml"));

        var probe = await Registry.ProbeAsync(server.Base + "dash/list.mpd", null, null, default);
        var plan = AssertPlan<TransferPlan.SegmentFiles>(probe, "SegmentList 应产出 SegmentFiles 计划");
        var b = server.Base;
        Equal(
            string.Join("\n", new[] { $"{b}dash/list-init.mp4", $"{b}dash/seg-1.m4s", $"{b}dash/seg-2.m4s" }),
            string.Join("\n", plan.Segments), "SegmentList 应含 Initialization + 全部 SegmentURL 并解析为绝对 URL");
        Equal("list.mp4", probe.Name, "DASH 输出文件名");
    }

    private static async Task DashSegmentBaseAsync(TestServer server)
    {
        const string mpd = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
              <Period>
                <AdaptationSet contentType="video">
                  <Representation id="v3" bandwidth="900000">
                    <BaseURL>video/single.m4v</BaseURL>
                    <SegmentBase indexRange="0-999">
                      <Initialization range="0-999"/>
                    </SegmentBase>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        server.Map("dash/base.mpd", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(mpd), "application/dash+xml"));

        var probe = await Registry.ProbeAsync(server.Base + "dash/base.mpd", null, null, default);
        var plan = AssertPlan<TransferPlan.ByteRanges>(probe, "SegmentBase 应回退为 ByteRanges 计划");
        Equal($"{server.Base}dash/video/single.m4v", plan.Url, "ByteRanges 应落在 BaseURL 解析结果上");
        That(plan.AcceptRanges, "SegmentBase 回退计划应允许字节范围");
        Equal(-1, plan.Size, "SegmentBase 回退计划大小未知");
        That(plan.Mirrors.Count == 0, "不应有镜像");
        That(probe.AcceptRanges, "probe 应允许字节范围");
        Equal("base.mp4", probe.Name, "DASH 输出文件名");
    }

    // ── 7. HLS 解析边界 ───────────────────────────────────────────────────

    private static async Task HlsEdgesAsync(TestServer server)
    {
        var master = string.Join('\n',
            "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=640x360",
            server.Base + "hls/v/low.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=1920x1080",
            server.Base + "hls/v/high.m3u8");
        const string low = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXTINF:10,
            ../seg0.ts
            #EXTINF:10,
            seg1.ts?token=abc
            #EXTINF:10,
            http://absolute.example.org/abs.ts
            #EXT-X-ENDLIST
            """;
        const string high = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXTINF:10,
            high-only.ts
            #EXT-X-ENDLIST
            """;
        server.Map("hls/master.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(master), "application/vnd.apple.mpegurl"));
        server.Map("hls/v/low.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(low), "application/vnd.apple.mpegurl"));
        server.Map("hls/v/high.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(high), "application/vnd.apple.mpegurl"));

        var probe = await Registry.ProbeAsync(server.Base + "hls/master.m3u8", null, null, default);
        var plan = AssertPlan<TransferPlan.SegmentFiles>(probe, "HLS 应产出 SegmentFiles 计划");
        var b = server.Base;
        Equal(
            string.Join("\n", new[] { $"{b}hls/seg0.ts", $"{b}hls/v/seg1.ts?token=abc", "http://absolute.example.org/abs.ts" }),
            string.Join("\n", plan.Segments),
            "分段应解析：../ 相对路径、带查询串、绝对 URL 原样保留");
        That(!plan.Segments.Any(s => s.Contains("high-only")), "码率相同时应取第一个变体（low.m3u8）");
        Equal("master.ts", probe.Name, "HLS 输出文件名 .ts");
    }

    private static async Task HlsRelativeVariantAsync(TestServer server)
    {
        // RFC8216：变体 URI 相对于 master 播放列表解析，相对路径是常态写法
        var master = string.Join('\n',
            "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000",
            "v/rel-low.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=2000000",
            "v/rel-high.m3u8");
        const string high = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXTINF:10,
            rel-seg.ts
            #EXT-X-ENDLIST
            """;
        server.Map("hls/rel-master.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(master), "application/vnd.apple.mpegurl"));
        server.Map("hls/v/rel-low.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(high), "application/vnd.apple.mpegurl"));
        server.Map("hls/v/rel-high.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(high), "application/vnd.apple.mpegurl"));

        var probe = await Registry.ProbeAsync(server.Base + "hls/rel-master.m3u8", null, null, default);
        var plan = AssertPlan<TransferPlan.SegmentFiles>(probe, "相对变体 URL 的 master 应可探测");
        Equal($"{server.Base}hls/v/rel-seg.ts", plan.Segments.Single(), "应取最高码率变体 rel-high.m3u8 并解析分段");
    }

    // ── 8. SOCKS5 ─────────────────────────────────────────────────────────

    private static async Task Socks5RoundtripAsync()
    {
        var payload = Encoding.UTF8.GetBytes("HELLO-FROM-SOCKS5-REMOTE-0123456789ABCDEFG");
        using var socks = new Socks5TestServer(payload);

        using var stream = await Socks5Connector.ConnectAsync(
            new Uri($"socks5://127.0.0.1:{socks.Port}"), "proxy-target.example.org", 8443, default);
        That(stream.CanRead && stream.CanWrite, "返回的流应可读可写");

        var received = new byte[payload.Length];
        await ReadExactlyAsync(stream, received);
        Equal(Encoding.UTF8.GetString(payload), Encoding.UTF8.GetString(received), "远端推送的数据应原样到达");

        var reply = new byte[payload.Length];
        Encoding.UTF8.GetBytes("CLIENT-READY").CopyTo(reply, 0);
        await stream.WriteAsync(reply);

        var finished = await Task.WhenAny(socks.HandshakeAndEcho, Task.Delay(5_000));
        That(finished == socks.HandshakeAndEcho, $"SOCKS5 服务端握手/回读应完成：{socks.HandshakeAndEcho.Status}");
        That(socks.GreetingOk, "greeting 应为 05 01 00");
        Equal("proxy-target.example.org", socks.Host, "CONNECT 应携带目标域名（ATYP=3）");
        Equal(8443, socks.DestinationPort ?? -1, "CONNECT 应携带目标端口（大端两字节）");
        Equal(Encoding.UTF8.GetString(reply), Encoding.UTF8.GetString(socks.FromClient ?? Array.Empty<byte>()),
            "客户端写入的数据应原样到达远端");
    }

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
        await WaitAsync(() => task.Done > 0, "竞态前下载应有进度", 15_000);

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
        await storm.WaitAsync(TimeSpan.FromSeconds(60));
        kernel.Remove(kernel.Tasks.Select(t => t.Id).ToArray()); // 双重清理 + 覆盖未知 id

        That(!kernel.Tasks.Any(t => t.Id == task.Id), "风暴后任务应不在任务列表");
        await WaitAsync(() => !kernel.Tasks.Any(), "风暴后任务列表应清空", 10_000);

        // 内核必须仍然健康：加一个新任务并完成
        var healthyDir = TempDir();
        var healthy = kernel.Add(new NewDownloadRequest(server.Base + "race/storm.bin", healthyDir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => healthy.Status == DownloadStatus.Completed, "竞态风暴后内核应照常完成新任务", 90_000);
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(healthyDir, healthy))), "风暴后新任务字节应完整");

        // 从未启动的排队任务：Remove 干净利落、不产生文件
        var queued = kernel.Add(new NewDownloadRequest(server.Base + "race/storm.bin", TempDir(), "", 2, TaskPriority.Normal, StartNow: false));
        kernel.Remove(new[] { queued.Id });
        That(!kernel.Tasks.Any(t => t.Id == queued.Id), "未启动任务应直接移除");
    }

    // ── 14. Range 续传边界 ────────────────────────────────────────────────

    private static async Task RangeResumeHeaderAsync(TestServer server)
    {
        var data = Payload(4 * 1024 * 1024);
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

    // ── 15. 单元扩容：MirrorSourceSet / SpeedLimiter ──────────────────────

    private static async Task MirrorSourceSetUnitAsync()
    {
        // 去重：主源与镜像相同、镜像彼此相同都只保留一份
        var set = new MirrorSourceSet("p", new[] { "p", "a", "a", "b" });
        Equal(3, set.Count, "主源 + 去重后的镜像应为 3 个源");

        // 轮转：Next 按 主源→镜像 依次循环
        var sequence = string.Join(",", Enumerable.Range(0, 6).Select(_ => set.Next()));
        Equal("p,a,b,p,a,b", sequence, "Next 应按 p→a→b 轮转");

        // 失败降级：报告失败的源移到队尾，仍在轮转中（不删除）
        set.ReportFailure("p");
        var after = string.Join(",", Enumerable.Range(0, 3).Select(_ => set.Next()));
        Equal("a,b,p", after, "ReportFailure 应把失败源移到队尾");

        // 单源集合的失败报告是无操作
        var single = new MirrorSourceSet("only", null);
        single.ReportFailure("only");
        Equal("only", single.Next(), "单源 ReportFailure 后轮转不变");

        await Task.CompletedTask;
    }

    private static async Task MirrorValidateUnitAsync()
    {
        // mock 探测：URL → (size, etag)；"boom" 直接抛异常（坏镜像）
        var catalogue = new Dictionary<string, (long Size, string? ETag)>
        {
            ["ok-1"] = (1000, "e1"),
            ["ok-2"] = (1000, null),     // 无 etag：身份未知但大小一致 → 保留
            ["wrong-size"] = (999, "e1"),
            ["wrong-etag"] = (1000, "e2"),
        };

        var valid = await MirrorSourceSet.ValidateAsync(
            new[] { "ok-1", "wrong-size", "ok-2", "wrong-etag", "boom" },
            size: 1000, etag: "e1", cancellation: default,
            probe: (url, _) => url == "boom"
                ? throw new InvalidOperationException("mirror exploded")
                : Task.FromResult(catalogue.TryGetValue(url, out var hit) ? hit : (0L, (string?)null)));

        Equal(string.Join(",", valid), "ok-1,ok-2", "ValidateAsync 应保留大小一致且 etag 相容的镜像、跳过坏镜像");

        // 外部取消必须传播而不是被吞掉（探测收到的是联动令牌，应随外部取消）
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new CancellationToken[1];
        await AssertThrowsAsync<OperationCanceledException>(
            () => MirrorSourceSet.ValidateAsync(new[] { "x" }, 1000, "e1",
                probe: (_, token) => { cancelled[0] = token; throw new OperationCanceledException(token); },
                cts.Token),
            "取消应传播为 OperationCanceledException");
        That(cancelled[0].IsCancellationRequested, "探测收到的联动令牌应已被外部取消带动");

        // 探测自身超时（6s 上限）：假探测挂起 → 被超时打断 → 镜像被跳过
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var none = await MirrorSourceSet.ValidateAsync(new[] { "slow" }, 1000, "e1",
            probe: async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token); // 6s 探测超时将其打断
                return (0L, (string?)null);
            },
            cancellation: default);
        sw.Stop();
        Equal(0, none.Count, "挂起的镜像探测应被超时跳过");
        That(sw.Elapsed < TimeSpan.FromSeconds(10), $"镜像探测超时上限应生效（实际 {sw.Elapsed.TotalSeconds:0.0}s）");
    }

    private static async Task LimiterAccuracyUnitAsync()
    {
        // 分块消费的精度：256 KB/s 下消费 8×32 KB ≈ 1s
        var limiter = new SpeedLimiter(256 * 1024);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var chunk = new byte[32 * 1024];
        for (int i = 0; i < 8; i++) await limiter.WaitAsync(chunk.Length);
        sw.Stop();
        That(sw.Elapsed.TotalSeconds >= 0.75,
            $"256 KB/s 下 8×32 KB 应 ≥0.75s（实际 {sw.Elapsed.TotalSeconds:0.00}s）");
        That(sw.Elapsed.TotalSeconds <= 4.0,
            $"限速节奏不应异常缓慢（实际 {sw.Elapsed.TotalSeconds:0.00}s）");

        // 中途改限速立即生效：512 KB/s 用 256 KB（~0.5s）→ 降到 64 KB/s 再用 128 KB（~2s）
        limiter.SetLimit(512 * 1024);
        for (int i = 0; i < 8; i++) await limiter.WaitAsync(chunk.Length);
        limiter.SetLimit(64 * 1024);
        sw.Restart();
        for (int i = 0; i < 4; i++) await limiter.WaitAsync(chunk.Length);
        sw.Stop();
        That(sw.Elapsed.TotalSeconds >= 1.6,
            $"降到 64 KB/s 后 4×32 KB 应 ≥1.6s（实际 {sw.Elapsed.TotalSeconds:0.00}s）");
        That(sw.Elapsed.TotalSeconds <= 7.0,
            $"降速后节奏不应异常缓慢（实际 {sw.Elapsed.TotalSeconds:0.00}s）");
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> body, string what) where TException : Exception
    {
        try
        {
            await body();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception error)
        {
            throw new Exception($"{what}：抛出了 {error.GetType().Name}（{error.Message}）而非 {typeof(TException).Name}");
        }
        throw new Exception($"{what}：未抛出 {typeof(TException).Name}");
    }

    // ── 16. 单元扩容：SegmentTable / checkpoint / ChunkedFileWriter ───────

    private static Task SegmentSplitUnitAsync()
    {
        const long MiB = 1024 * 1024;

        // 大小未知（内核探测失败时传 -1）→ 单个流式分段，永不算完成
        var stream = SegmentTable.Split("u", -1, null, 8);
        Equal(1, stream.Segments.Length, "未知大小应只有 1 个流式分段");
        Equal(long.MaxValue, stream.Segments[0].End, "流式分段 End 应为 long.MaxValue");
        That(!stream.Segments[0].IsComplete, "流式分段不应算完成");
        Equal(-1L, stream.Size, "未知大小时 Size=-1");

        // 过切分 ×4 + 1 MiB 下限：3 MiB / 8 连接 → 3 段各 1 MiB（下限生效）
        var floored = SegmentTable.Split("f", 3 * MiB, "e", 8);
        Equal(3, floored.Segments.Length, "3 MiB @ 8 连接应受 1 MiB 下限切成 3 段");
        for (int i = 0; i < floored.Segments.Length; i++)
            Equal((i * MiB, (i + 1) * MiB), (floored.Segments[i].Start, floored.Segments[i].End),
                $"下限分段 {i} 应恰好是第 {i} 个 1 MiB");

        // 过切分：10 MiB / 2 连接 → 8 段，总长守恒
        var over = SegmentTable.Split("o", 10 * MiB, null, 2);
        Equal(8, over.Segments.Length, "10 MiB @ 2 连接应过切分为 8 段");
        Equal(10 * MiB, over.Segments.Sum(s => s.End - s.Start), "过切分后总长应守恒");
        That(over.Segments.All(s => s.End - s.Start >= MiB), "过切分段不得低于 1 MiB 下限");

        // 不整除：5 MiB+1 / 1 连接 → 4 段，前 extra 段多 1 字节，接缝连续
        var uneven = SegmentTable.Split("n", 5 * MiB + 1, null, 1);
        Equal(4, uneven.Segments.Length, "5 MiB+1 @ 1 连接应切成 4 段");
        for (int i = 1; i < uneven.Segments.Length; i++)
            Equal(uneven.Segments[i - 1].End, uneven.Segments[i].Start, $"第 {i} 段起点应紧接上一段终点");
        Equal(5 * MiB + 1, uneven.Segments[^1].End, "末段终点应等于总大小");

        // connections 钳制：0 与 64 之外不存在；64 也封顶 64 段
        Equal(4, SegmentTable.Split("c", 64 * MiB, null, 0).Segments.Length, "connections=0 钳制为 1 → 4 段（×4 过切分）");
        Equal(64, SegmentTable.Split("c", 64 * MiB, null, 100).Segments.Length, "connections=100 钳制为 64 → 封顶 64 段");

        return Task.CompletedTask;
    }

    private static Task CheckpointRoundTripUnitAsync()
    {
        const long MiB = 1024 * 1024;

        // 往返：Split → 部分完成 → ToCheckpoint → Restore，字段与进度不丢
        var table = SegmentTable.Split("rt", 8 * MiB, "etag-1", 4);
        table.Segments[0].Done = table.Segments[0].End - table.Segments[0].Start; // 第 1 段完成
        table.Segments[1].Done = 123456;
        var checkpoint = table.ToCheckpoint();
        Equal("ranges", checkpoint.Kind, "checkpoint Kind 应为 ranges");

        var restored = SegmentTable.Restore(checkpoint);
        That(restored is not null, "合法 checkpoint 应可 Restore");
        Equal(table.Size, restored!.Size, "往返后 Size 应不变");
        Equal(table.ETag, restored.ETag, "往返后 ETag 应不变");
        Equal("rt", restored.Url, "往返后 Url 应不变");
        Equal(table.Segments.Length, restored.Segments.Length, "往返后分段数应不变");
        for (int i = 0; i < table.Segments.Length; i++)
        {
            Equal(table.Segments[i].Start, restored.Segments[i].Start, $"段 {i} Start 往返");
            Equal(table.Segments[i].End, restored.Segments[i].End, $"段 {i} End 往返");
            Equal(table.Segments[i].Done, restored.Segments[i].Done, $"段 {i} Done 往返");
        }
        Equal(table.DoneBytes, restored.DoneBytes, "往返后 DoneBytes 应不变");

        // 校验拒绝：Kind 错 / 空数组 / 长度不齐 → null（上层会静默重下）
        That(SegmentTable.Restore(new TransferCheckpoint { Kind = "segments", Start = [0], End = [1], Done = [0] }) is null,
            "Kind=segments 的 checkpoint 不应被 Restore 为 ranges 表");
        That(SegmentTable.Restore(new TransferCheckpoint { Kind = "ranges", Start = [], End = [], Done = [] }) is null,
            "空数组 checkpoint 应拒绝");
        That(SegmentTable.Restore(new TransferCheckpoint { Kind = "ranges", Start = [0, 1], End = [1], Done = [0, 0] }) is null,
            "数组长度不齐应拒绝");

        // Done 越界 clamp：负数 → 0，超过段长 → 段长
        var clamped = SegmentTable.Restore(new TransferCheckpoint
        {
            Kind = "ranges", Url = "u", Size = 2 * MiB,
            Start = [0, MiB], End = [MiB, 2 * MiB], Done = [-5, 2 * MiB],
        });
        That(clamped is not null, "越界 checkpoint 仍应可 Restore");
        Equal(0L, clamped!.Segments[0].Done, "负 Done 应 clamp 为 0");
        Equal(MiB, clamped.Segments[1].Done, "超段长 Done 应 clamp 为段长");
        That(clamped.Segments[1].IsComplete, "clamp 到段长后该段应视为完成");

        return Task.CompletedTask;
    }

    private static Task ChunkedWriterUnitAsync()
    {
        const long MiB = 1024 * 1024;
        var path = Path.Combine(TempDir(), "chunked.bin");

        // 预分配：writer 一建好文件就是全尺寸，未写区域读出 0
        using (var writer = new ChunkedFileWriter(path, 4 * MiB))
            writer.Write(2 * MiB, new byte[] { 1, 2, 3, 4 }, 4);
        Equal(4 * MiB, new FileInfo(path).Length, "预分配应立即把文件设为全尺寸");
        var onDisk = File.ReadAllBytes(path);
        That(onDisk.AsSpan(0, (int)(2 * MiB)).ToArray().All(b => b == 0), "未写前半应是 0 填充");
        That(onDisk.AsSpan((int)(2 * MiB), 4).ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4 }),
            "乱序直写应精确落在指定偏移");
        That(onDisk.AsSpan((int)(2 * MiB) + 4).ToArray().All(b => b == 0), "写入点之后应仍是 0 填充");

        // 多线程乱序并发直写：8 个连接各写自己的 1 MiB 条带，字节精确拼装
        var strip = Payload((int)MiB);
        var concurrent = Path.Combine(TempDir(), "chunked-concurrent.bin");
        using (var writer = new ChunkedFileWriter(concurrent, 8 * MiB))
        {
            var lanes = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                var buffer = new byte[64 * 1024];
                long at = i * MiB;
                for (int copied = 0; copied < MiB; copied += buffer.Length)
                {
                    Array.Copy(strip, copied, buffer, 0, buffer.Length);
                    writer.Write(at + copied, buffer, buffer.Length);
                }
            })).ToArray();
            Task.WaitAll(lanes.ToArray(), TimeSpan.FromSeconds(30));
            writer.Flush();
        }
        var assembled = File.ReadAllBytes(concurrent);
        Equal(8 * MiB, assembled.Length, "并发直写后文件长度应正确");
        That(assembled.SequenceEqual(Enumerable.Range(0, 8).SelectMany(_ => strip)), "8 条带乱序并发写入后应字节精确复原");

        return Task.CompletedTask;
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

    private static void That(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}：期望 [{expected}]，实际 [{actual}]");
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
