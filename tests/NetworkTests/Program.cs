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
/// NetTrans.Kernel 网络层测试（10 个方向，18 条独立 Check）。
/// 断言全部基于内核源码与 BCL 的真实行为；确认的内核问题以"已知失败"标注，不强行 PASS。
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
        await RunAsync("2b. HTTP 命名：filename 带引号转义", () => NamingQuotedEscapeAsync(server), knownBug: true);
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
        await RunAsync("7b. HLS：master 相对变体 URL（按 RFC8216 应可用）", () => HlsRelativeVariantAsync(server), knownBug: true);
        await RunAsync("8.  SOCKS5：RFC1928 握手 + CONNECT + 可读写流", Socks5RoundtripAsync);
        await RunAsync("9.  Remove 中途取消：TaskRemoved + 文件句柄释放", () => RemoveMidwayAsync(server));
        await RunAsync("10. Redownload：Done 归零重新下载 + 日志\"重新开始\"", () => RedownloadAsync(server));

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
