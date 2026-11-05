using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using NetTrans.Kernel;
using NetTrans.Kernel.Net;
using NetTrans.Kernel.Protocol;
using NetTrans.Kernel.Protocol.Drivers;
using NetTrans.Kernel.Sites;
using NetTrans.Kernel.Transfer;

namespace NetworkTests;

internal sealed partial class Program
{
    // ── 20. 站点提取器框架（仿 yt-dlp：一站点一文件，框架不动） ───────────

    private sealed class StubSite : ISiteExtractor
    {
        private readonly Func<string, SiteMedia?> _extract;
        public StubSite(string name, Func<string, SiteMedia?> extract) { Name = name; _extract = extract; }
        public string Name { get; }
        public bool CanHandle(string url) => url.Contains("/stub-page", StringComparison.Ordinal);
        public Task<SiteMedia?> ExtractAsync(string url, DriverContext context, CancellationToken cancellation)
            => Task.FromResult(_extract(url));
    }

    private static void MapJson(TestServer server, string path, Func<HttpListenerRequest, string> render) =>
        server.Map(path, ctx => TestServer.Reply(
            ctx.Response, 200, Encoding.UTF8.GetBytes(render(ctx.Request)), "application/json"));

    private static DriverContext SiteContext(TestServer server, string siteName, List<string>? logs = null)
    {
        // 不用 using：HttpClient 的生命周期归返回的 DriverContext，测试进程退出统一回收
        var http = new HttpClient();
        return new DriverContext
        {
            Options = new KernelOptions { SiteApiBases = new Dictionary<string, string> { [siteName] = server.Base } },
            Http = http,
            Headers = new Dictionary<string, string>(),
            Log = logs is null ? null : (message, error) => logs.Add(message),
        };
    }

    private static async Task SiteFrameworkAsync(TestServer server)
    {
        var data = Payload(128 * 1024);
        server.Map("stub/media.mp4", server.RangeFile(data, contentType: "video/mp4"));
        server.Map("stub/page.html", server.RangeFile(Encoding.UTF8.GetBytes("<html>page</html>"), contentType: "text/html"));

        // 命中：站点提取器优先于协议驱动；标题命名 + 扩展名来自直链；站点头随探针携带
        var registry = new ProtocolDriverRegistry(new KernelOptions(), ProbeHttp);
        registry.RegisterSite(new StubSite("Stub", _ => new SiteMedia(
            "Stub", "框架测试标题",
            new[]
            {
                new SiteStream(server.Base + "stub/media.mp4", "MP4 视频",
                    Headers: new Dictionary<string, string> { ["Referer"] = "https://stub.example/" }),
            })));

        var hit = await registry.ProbeAsync(server.Base + "stub-page/hit", null, null, CancellationToken.None);
        var hitPlan = AssertPlan<TransferPlan.ByteRanges>(hit, "站点命中应产出 ByteRanges 计划");
        Equal(server.Base + "stub/media.mp4", hitPlan.Url, "计划应指向站点解析出的直链");
        Equal("框架测试标题.mp4", hit.Name, "任务名应为 页面标题 + 直链扩展名");
        That(hit.ExtraHeaders is not null && hit.ExtraHeaders["Referer"] == "https://stub.example/",
            "探针应携带站点必需头（ExtraHeaders）");
        Equal(server.Base + "stub/media.mp4", hit.FinalUrl, "FinalUrl 应为直链");

        // 提取器抛异常 → 回退协议驱动探测页面本身
        var registry2 = new ProtocolDriverRegistry(new KernelOptions(), ProbeHttp);
        registry2.RegisterSite(new StubSite("Stub", _ => throw new HttpRequestException("site exploded")));
        var fallback = await registry2.ProbeAsync(server.Base + "stub/page.html", null, null, CancellationToken.None);
        var fallbackPlan = AssertPlan<TransferPlan.ByteRanges>(fallback, "提取器失败应回退为协议探测");
        Equal(server.Base + "stub/page.html", fallbackPlan.Url, "回退计划应指向原始 URL");
        Equal(DownloadProtocol.Http, fallback.Protocol, "回退应由 HTTP 驱动接手");

        // 提取器返回 null（放弃认领）→ 同样回退
        var registry3 = new ProtocolDriverRegistry(new KernelOptions(), ProbeHttp);
        registry3.RegisterSite(new StubSite("Stub", _ => null));
        var declined = await registry3.ProbeAsync(server.Base + "stub/page.html", null, null, CancellationToken.None);
        AssertPlan<TransferPlan.ByteRanges>(declined, "放弃认领也应回退为协议探测");
    }

    private static async Task BilibiliKernelAsync(TestServer server)
    {
        var data = Payload(256 * 1024);
        string? seenReferer = null, seenUa = null;
        server.Map("bili/media.mp4", server.RangeFile(data, contentType: "video/mp4",
            onGet: (_, ctx) =>
            {
                seenReferer = ctx.Request.Headers["Referer"];
                seenUa = ctx.Request.Headers["User-Agent"];
                return false; // 校验后仍走正常 Range 服务
            }));

        // 真实站点时域名是 api.bilibili.com，经 KernelOptions.SiteApiBases 覆写指向本地 fixture
        MapJson(server, "x/web-interface/view", req => req.QueryString["bvid"] switch
        {
            "BV1xx411c7mD" => """{"code":0,"message":"0","data":{"bvid":"BV1xx411c7mD","title":"【测试】站点提取器标题","cid":111,"pages":[{"cid":111,"page":1},{"cid":222,"page":2}]}}""",
            "BVNODURL0000" => """{"code":0,"message":"0","data":{"bvid":"BVNODURL0000","title":"no-durl","cid":333,"pages":[{"cid":333,"page":1}]}}""",
            _ => """{"code":-404,"message":"啥都木有"}""",
        });
        MapJson(server, "x/player/playurl", req =>
        {
            if (req.QueryString["cid"] == "333")
                return """{"code":0,"message":"0","data":{"quality":64,"durl":[]}}""";
            // 注：插值 raw string 里 JSON 结尾的连续 } 需用空格隔开（CS9007），JSON 中空白合法
            return $$"""{"code":0,"message":"0","data":{"quality":64,"format":"mp4","durl":[{"url":"{{server.Base}}bili/media.mp4","size":{{data.Length}},"length":100000}] } }""";
        });

        var options = new KernelOptions
        {
            ConnectionsPerTask = 2,
            MaxSimultaneous = 1,
            SiteApiBases = new Dictionary<string, string> { ["Bilibili"] = server.Base },
        };
        using var kernel = new RealDownloadKernel(options);
        var dir = TempDir();
        var task = kernel.Add(new NewDownloadRequest(
            "https://www.bilibili.com/video/BV1xx411c7mD/", dir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "Bilibili 页面任务应完成", 60_000);

        That(task.Log.Any(l => l.Message.Contains("站点解析（Bilibili）")),
            $"日志应有站点解析行：{string.Join(" | ", task.Log.Select(l => l.Message))}");
        Equal("【测试】站点提取器标题.mp4", task.Name, "任务名应为 页面标题 + mp4");
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(dir, task))), "站点直链下载字节应完整");
        Equal("https://www.bilibili.com/", seenReferer, "直链 GET 必须带站点 Referer（否则 CDN 403）");
        That(seenUa is not null && seenUa.StartsWith("Mozilla/5.0", StringComparison.Ordinal), "直链 GET 必须带浏览器 UA");
    }

    private static async Task BilibiliExtractorUnitAsync(TestServer server)
    {
        var logs = new List<string>();
        using var http = new HttpClient();
        var context = new DriverContext
        {
            Options = new KernelOptions { SiteApiBases = new Dictionary<string, string> { ["Bilibili"] = server.Base } },
            Http = http,
            Headers = new Dictionary<string, string>(),
            Log = (message, error) => logs.Add(message),
        };
        var site = new BilibiliSite();

        // ?p=2 → 用 pages[1].cid；画质标签按 qn=64 → 720P 高清；流头必须带 Referer
        var media = await site.ExtractAsync("https://www.bilibili.com/video/BV1xx411c7mD?p=2", context, CancellationToken.None);
        Equal("【测试】站点提取器标题", media!.Title, "标题应来自 view 接口");
        Equal(1, media.Streams.Count, "html5 playurl 应返回一条直链流");
        Equal(server.Base + "bili/media.mp4", media.Streams[0].Url, "直链应来自 playurl fixture");
        Equal("720P 高清", media.Streams[0].Label, "qn=64 应映射为 720P 高清");
        That(media.Streams[0].Headers!["Referer"] == "https://www.bilibili.com/", "流头必须带站点 Referer");
        That(logs.Any(l => l.Contains("2 个分P")), $"多P 应有日志：{string.Join(" | ", logs)}");
        That(logs.Any(l => l.Contains("取第 2 个")), "p=2 应选第 2 个分P");

        // 错误路径：BV 号缺失 / view code!=0 / durl 缺失
        await AssertThrowsAsync<FormatException>(
            () => site.ExtractAsync("https://www.bilibili.com/video/av123456", context, CancellationToken.None),
            "没有 BV 号应报错");
        await AssertThrowsAsync<FormatException>(
            () => site.ExtractAsync("https://www.bilibili.com/video/BVERR0000000", context, CancellationToken.None),
            "view code=-404 应抛出接口信息");
        await AssertThrowsAsync<FormatException>(
            () => site.ExtractAsync("https://www.bilibili.com/video/BVNODURL0000", context, CancellationToken.None),
            "playurl 无 durl 应报错");
    }

    private static async Task TwitterAsync(TestServer server)
    {
        MapJson(server, "tweet-result", req => req.QueryString["id"] == "999"
            ? """{"text":"纯文字","user":{"screen_name":"tester"},"mediaDetails":[]}"""
            : $$"""
                {"text":"测试推文内容\n第二行","user":{"screen_name":"tester"},"mediaDetails":[{"type":"video",
                  "video_info":{"duration_millis":61000,"variants":[
                    {"bitrate":320000,"content_type":"video/mp4","url":"{{server.Base}}tw/low.mp4"},
                    {"bitrate":832000,"content_type":"video/mp4","url":"{{server.Base}}tw/high.mp4"},
                    {"bitrate":0,"content_type":"application/x-mpegurl","url":"{{server.Base}}tw/playlist.m3u8"}] } }] }
                """);

        var high = Payload(64 * 1024);
        server.Map("tw/high.mp4", server.RangeFile(high, contentType: "video/mp4"));

        var registry = new ProtocolDriverRegistry(new KernelOptions
        {
            SiteApiBases = new Dictionary<string, string> { ["X / Twitter"] = server.Base },
        }, ProbeHttp);

        var probe = await registry.ProbeAsync(
            "https://x.com/tester/status/1234567890123456789?s=20", null, null, CancellationToken.None);
        var plan = AssertPlan<TransferPlan.ByteRanges>(probe, "推文视频应产出 ByteRanges 计划");
        Equal(server.Base + "tw/high.mp4", plan.Url, "应选最高码率 mp4 变体");
        That(probe.Name.StartsWith("@tester：", StringComparison.Ordinal) && probe.Name.EndsWith(".mp4", StringComparison.Ordinal),
            $"任务名应为 作者：正文.mp4，实际 {probe.Name}");

        // 单元：纯文字/图片推文如实报错
        var context = SiteContext(server, "X / Twitter");
        await AssertThrowsAsync<FormatException>(
            () => new TwitterSite().ExtractAsync("https://x.com/tester/status/999", context, CancellationToken.None),
            "无视频推文应报错");
    }

    private static async Task YouTubeAsync(TestServer server)
    {
        var ok720 = server.Base + "yt/ok720.mp4";
        var ok360 = server.Base + "yt/ok360.mp4";
        var miss720 = server.Base + "yt/miss720.mp4";
        var ok360b = server.Base + "yt/ok360b.mp4";

        string PlayerJson(string f720, string f360) => $$"""
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"title":"YouTube 测试视频","author":"Tester"},
             "streamingData":{
               "formats":[
                 {"itag":137,"mimeType":"video/mp4; codecs=\"avc1.640028\"","bitrate":2000000,"qualityLabel":"1080p","signatureCipher":"s=encrypted"},
                 {"itag":22,"mimeType":"video/mp4; codecs=\"avc1.64001F, mp4a.40.2\"","bitrate":1200000,"qualityLabel":"720p","url":"{{f720}}"},
                 {"itag":18,"mimeType":"video/mp4; codecs=\"avc1.42001E, mp4a.40.2\"","bitrate":396000,"qualityLabel":"360p","url":"{{f360}}"}],
               "hlsManifestUrl":"{{server.Base}}yt/master.m3u8"} }
            """;

        server.Map("youtubei/v1/player", ctx =>
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var videoId = Regex.Match(reader.ReadToEnd(), "\"videoId\":\"([^\"]+)\"").Groups[1].Value;
            var json = videoId switch
            {
                "fallvid2222" => PlayerJson(miss720, ok360b),
                "ciphered000" => """{"playabilityStatus":{"status":"OK"},"videoDetails":{"title":"ciphered"},"streamingData":{"formats":[{"itag":137,"mimeType":"video/mp4","bitrate":2000000,"signatureCipher":"s=x"}]}}""",
                _ => PlayerJson(ok720, ok360),
            };
            TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(json), "application/json");
            return Task.CompletedTask;
        });

        var ok720Data = Payload(64 * 1024);
        var ok360Data = Payload(32 * 1024);
        server.Map("yt/ok720.mp4", server.RangeFile(ok720Data, contentType: "video/mp4"));
        server.Map("yt/ok360.mp4", server.RangeFile(ok360Data, contentType: "video/mp4"));
        server.Map("yt/ok360b.mp4", server.RangeFile(ok360Data, contentType: "video/mp4"));
        // yt/miss720.mp4 故意不映射 → 404

        var registry = new ProtocolDriverRegistry(new KernelOptions
        {
            SiteApiBases = new Dictionary<string, string> { ["YouTube"] = server.Base },
        }, ProbeHttp);

        var hit = await registry.ProbeAsync("https://www.youtube.com/watch?v=okvid111111", null, null, CancellationToken.None);
        var hitPlan = AssertPlan<TransferPlan.ByteRanges>(hit, "YouTube 渐进式格式应产出 ByteRanges 计划");
        Equal(ok720, hitPlan.Url, "应选码率最高的带明文 url 格式（跳过 signatureCipher 的 1080p）");
        Equal("YouTube 测试视频.mp4", hit.Name, "标题命名");

        // 提取器单元：流排序 + HLS 兜底
        var context = SiteContext(server, "YouTube");
        var media = await new YouTubeSite().ExtractAsync("https://www.youtube.com/watch?v=okvid111111", context, CancellationToken.None);
        Equal(3, media!.Streams.Count, "应有 2 条渐进式 + 1 条 HLS 兜底");
        That(media.Streams[0].Url == ok720 && media.Streams[1].Url == ok360, "渐进式应按码率降序");
        That(media.Streams[2].Url.EndsWith("yt/master.m3u8", StringComparison.Ordinal), "HLS 清单应为兜底流");

        // 最佳流 404 → 顺延下一条流
        var fell = await registry.ProbeAsync("https://youtu.be/fallvid2222", null, null, CancellationToken.None);
        var fellPlan = AssertPlan<TransferPlan.ByteRanges>(fell, "最佳流失败应顺延");
        Equal(ok360b, fellPlan.Url, "720p 流 404 后应改用 360p 流");

        // 全加密 → 提取器如实报错（框架回退协议探测由 20a 覆盖）
        await AssertThrowsAsync<FormatException>(
            () => new YouTubeSite().ExtractAsync("https://www.youtube.com/watch?v=ciphered000", context, CancellationToken.None),
            "仅加密格式应报「解签」错误");
    }

    // ── 21. yt-dlp 兜底提取器（外部解析引擎：-J dump-json → SiteMedia） ──

    /// <summary>yt-dlp --dump-single-json 的 fixture：覆盖渐进式/video-only/纯音频/storyboard/无 url 格式与多语言字幕。</summary>
    private static string YtDlpFixture(string baseUrl) => $$"""
        {
          "id": "fixture123",
          "title": "yt-dlp 测试标题",
          "_type": "video",
          "webpage_url": "https://video.example/watch/abc",
          "formats": [
            { "format_id": "sb0", "ext": "mhtml", "vcodec": "none", "acodec": "none", "format_note": "storyboard", "url": "https://sb.example/0.jpg" },
            { "format_id": "248", "ext": "mp4", "height": 1080, "vcodec": "avc1.640028", "acodec": "none", "format_note": "1080p video-only", "url": "https://vonly.example/248.mp4" },
            { "format_id": "140", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "filesize": 5200000, "url": "https://audio.example/140.m4a" },
            { "format_id": "137", "ext": "mp4", "height": 1080, "vcodec": "avc1.640028", "acodec": "mp4a.40.2", "filesize_approx": 250000000, "url": "{{baseUrl}}ytd/best.mp4", "http_headers": { "Referer": "https://video.example/", "User-Agent": "Mozilla/5.0 fixture" } },
            { "format_id": "22", "ext": "mp4", "height": 720, "vcodec": "avc1.64001F", "acodec": "mp4a.40.2", "filesize": 100000000, "url": "https://cdn.example/22.mp4" },
            { "format_id": "nourl", "ext": "mp4", "height": 2160, "vcodec": "avc1", "acodec": "mp4a" }
          ],
          "subtitles": {
            "zh-Hans": [ { "ext": "vtt", "url": "https://subs.example/zh.vtt" } ],
            "en": [ { "ext": "srt", "url": "https://subs.example/en.srt" }, { "ext": "vtt", "url": "https://subs.example/en.vtt" } ]
          }
        }
        """;

    private static async Task YtDlpMappingUnitAsync()
    {
        var context = new DriverContext
        {
            Options = new KernelOptions(),
            Http = new HttpClient(),
            Headers = new Dictionary<string, string>(),
        };
        var site = new YtDlpSite((_, _) => Task.FromResult<string?>(YtDlpFixture("https://fixture.example/")));

        // 映射：formats → Streams（排序/标签/大小/流头）+ 字幕条目
        var media = await site.ExtractAsync("https://video.example/watch/abc", context, CancellationToken.None);
        Equal("yt-dlp 测试标题", media!.Title, "标题应取自 dump-json 的 title");
        Equal(5, media.Streams.Count, "应为 2 渐进式 + 1 纯音频 + 2 字幕（video-only/storyboard/无 url 被排除）");
        Equal("https://fixture.example/ytd/best.mp4", media.Streams[0].Url, "1080p 渐进式应排最前（height 降序）");
        Equal("1080p mp4", media.Streams[0].Label, "画质标签 = height + ext");
        Equal(250000000L, media.Streams[0].Size, "filesize_approx 应映射为声明大小");
        Equal("720p mp4", media.Streams[1].Label, "第二应为 720p 渐进式");
        Equal("音频 m4a", media.Streams[2].Label, "纯音频排在视频后（height 为空靠后）");
        Equal(5200000L, media.Streams[2].Size, "纯音频的 filesize 应映射");
        Equal("字幕 zh-Hans (vtt)", media.Streams[3].Label, "字幕流按语言标注");
        Equal("字幕 en (srt)", media.Streams[4].Label, "每语言取第一条字幕（srt 在前）");
        That(media.Streams[0].Headers!["Referer"] == "https://video.example/", "http_headers 的 Referer 应映射为流头");
        That(media.Streams[0].Headers!["User-Agent"] == "Mozilla/5.0 fixture", "http_headers 的 UA 应映射为流头");
        That(media.Streams[3].Headers is null, "字幕流不需要 CDN 头");

        // CanHandle：兜底认领一切 http(s)，拒绝 ftp/magnet/裸串
        That(site.CanHandle("https://any.example/page"), "https 应认领");
        That(site.CanHandle("http://any.example/page"), "http 应认领");
        That(!site.CanHandle("ftp://host/file.zip"), "ftp 不认领");
        That(!site.CanHandle("magnet:?xt=urn:btih:abc"), "magnet 不认领");
        That(!site.CanHandle("not a url"), "裸串不认领");

        // 二进制缺失（runner 返回 null）→ 放弃认领，框架回退协议探测
        var missing = await new YtDlpSite((_, _) => Task.FromResult<string?>(null))
            .ExtractAsync("https://video.example/watch/abc", context, CancellationToken.None);
        That(missing is null, "yt-dlp 不可用应返回 null（诚实降级）");
    }

    private static async Task YtDlpRegistryAsync(TestServer server)
    {
        var best = Payload(96 * 1024);
        server.Map("ytd/best.mp4", server.RangeFile(best, contentType: "video/mp4"));
        server.Map("ytd/page.html", server.RangeFile(Encoding.UTF8.GetBytes("<html>page</html>"), contentType: "text/html"));

        // 启用：runner 注入 fixture → 兜底命中 → 最优渐进式流 + 标题命名 + 站点头
        var registry = new ProtocolDriverRegistry(new KernelOptions
        {
            EnableYtDlp = true,
            YtDlpRunner = (_, _) => Task.FromResult<string?>(YtDlpFixture(server.Base)),
        }, ProbeHttp);

        var hit = await registry.ProbeAsync("https://video.example/watch/abc", null, null, CancellationToken.None);
        var hitPlan = AssertPlan<TransferPlan.ByteRanges>(hit, "yt-dlp 命中应产出 ByteRanges 计划");
        Equal(server.Base + "ytd/best.mp4", hitPlan.Url, "应选 1080p 渐进式直链");
        Equal("yt-dlp 测试标题.mp4", hit.Name, "任务名 = 页面标题 + 直链扩展名");
        That(hit.ExtraHeaders is not null && hit.ExtraHeaders["Referer"] == "https://video.example/",
            "探针应携带 http_headers 映射的 Referer（传输时随 GET 发送，该机制由 20b 内核级验证）");

        // 未启用（默认）：兜底不注册，runner 不被调用，直接协议探测
        bool runnerCalled = false;
        var offRegistry = new ProtocolDriverRegistry(new KernelOptions
        {
            YtDlpRunner = (_, _) => { runnerCalled = true; return Task.FromResult<string?>(YtDlpFixture(server.Base)); },
        }, ProbeHttp);
        var off = await offRegistry.ProbeAsync(server.Base + "ytd/page.html", null, null, CancellationToken.None);
        AssertPlan<TransferPlan.ByteRanges>(off, "未启用 yt-dlp 应直接协议探测");
        That(!runnerCalled, "EnableYtDlp=false 时 runner 不应被调用");

        // 二进制缺失：runner 返回 null → 回退协议探测原 URL，不抛未处理异常
        var missingRegistry = new ProtocolDriverRegistry(new KernelOptions
        {
            EnableYtDlp = true,
            YtDlpRunner = (_, _) => Task.FromResult<string?>(null),
        }, ProbeHttp);
        var fell = await missingRegistry.ProbeAsync(server.Base + "ytd/page.html", null, null, CancellationToken.None);
        var fellPlan = AssertPlan<TransferPlan.ByteRanges>(fell, "yt-dlp 缺失应回退协议探测");
        Equal(server.Base + "ytd/page.html", fellPlan.Url, "回退计划应指向原始 URL");
    }

    private static async Task YtDlpBadOutputAsync(TestServer server)
    {
        server.Map("ytd2/page.html", server.RangeFile(Encoding.UTF8.GetBytes("<html>page</html>"), contentType: "text/html"));
        var context = new DriverContext
        {
            Options = new KernelOptions(),
            Http = new HttpClient(),
            Headers = new Dictionary<string, string>(),
        };

        // 坏 JSON：抛 FormatException（框架记日志后回退）
        var badSite = new YtDlpSite((_, _) => Task.FromResult<string?>("<html>not json</html>"));
        await AssertThrowsAsync<FormatException>(
            () => badSite.ExtractAsync("https://video.example/watch/abc", context, CancellationToken.None),
            "坏 JSON 应抛 FormatException");

        // 空 formats：无可用流 → 放弃认领
        var emptySite = new YtDlpSite((_, _) => Task.FromResult<string?>("""{ "id": "x", "title": "t", "formats": [] }"""));
        var empty = await emptySite.ExtractAsync("https://video.example/watch/abc", context, CancellationToken.None);
        That(empty is null, "无可用流应放弃认领（回退协议探测）");

        // 框架级：坏 JSON → 回退协议探测原 URL
        var registry = new ProtocolDriverRegistry(new KernelOptions
        {
            EnableYtDlp = true,
            YtDlpRunner = (_, _) => Task.FromResult<string?>("{oops"),
        }, ProbeHttp);
        var fell = await registry.ProbeAsync(server.Base + "ytd2/page.html", null, null, CancellationToken.None);
        var fellPlan = AssertPlan<TransferPlan.ByteRanges>(fell, "坏 JSON 应回退协议探测");
        Equal(server.Base + "ytd2/page.html", fellPlan.Url, "回退计划应指向原始 URL");
    }

    // ── 30. 通用页面直链兜底（P1-8：<a>/<video>/<source> 过滤） ───────────

    private static async Task GenericPageExtractAsync(TestServer server)
    {
        const string html = """
            <html><head><title>发布页</title></head><body>
            <a href="/page/about">关于</a>
            <a href="#top">回到顶部</a>
            <a href="mailto:hi@example.org">联系</a>
            <a href="files/report.pdf">下载报告</a>
            <a href="files/report.pdf">重复链接</a>
            <video controls><source src="stream/index.m3u8" type="application/vnd.apple.mpegurl"></video>
            </body></html>
            """;
        server.Map("page/release", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(html), "text/html"));
        server.Map("page/binary", ctx => TestServer.Reply(ctx.Response, 200, Payload(256), "application/octet-stream"));

        using var http = new HttpClient();
        var context = new DriverContext
        {
            Options = new KernelOptions(),
            Http = http,
            Headers = new Dictionary<string, string>(),
        };
        var site = new GenericPageSite();

        That(site.CanHandle(server.Base + "page/release"), "无文件扩展名的 http(s) 页面应认领");
        That(!site.CanHandle(server.Base + "page/files/report.pdf"), "常见直链扩展名不应当页面认领");

        var media = await site.ExtractAsync(server.Base + "page/release", context, CancellationToken.None);
        That(media is not null, "HTML 页面应解析出候选");
        Equal("发布页", media!.Title, "标题取 <title>");
        Equal(2, media.Streams.Count, "导航/锚点/mailto 应过滤，pdf 去重 → 两类候选");
        That(media.Streams[0].Url == server.Base + "page/stream/index.m3u8",
            $"流媒体候选应排前且解析为绝对 URL，实际 {media.Streams[0].Url}");
        That(media.Streams[1].Url == server.Base + "page/files/report.pdf",
            $"pdf 直链候选第二，实际 {media.Streams[1].Url}");

        That(await site.ExtractAsync(server.Base + "page/binary", context, CancellationToken.None) is null,
            "非 HTML 内容应放弃认领（回退协议探测）");
    }

    private static async Task GenericPageKernelAsync(TestServer server)
    {
        const string html = """
            <html><head><title>双候选页</title></head><body>
            <a href="files/manual.pdf">手册</a>
            <video><source src="stream/index.m3u8" type="application/vnd.apple.mpegurl"></video>
            </body></html>
            """;
        const string playlist = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXTINF:10,
            seg0.ts
            #EXT-X-ENDLIST
            """;
        server.Map("page/dual", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(html), "text/html"));
        server.Map("page/stream/index.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(playlist), "application/vnd.apple.mpegurl"));

        var registry = new ProtocolDriverRegistry(new KernelOptions(), ProbeHttp);
        var probe = await registry.ProbeAsync(server.Base + "page/dual", null, null, default);
        var plan = AssertPlan<TransferPlan.SegmentFiles>(probe, "页面兜底应选中 m3u8 最优流并可传输");
        Equal($"{server.Base}page/stream/seg0.ts", plan.Segments.Single(), "m3u8 分段应解析为绝对 URL");
        Equal("双候选页.ts", probe.Name, "任务名 = 页面标题 + 最优流扩展名");
    }

    // ── 36. 站点批量抓取（P2-6：列表页一次解析出多条任务草稿） ────────────

    private static async Task PageBatchDraftsAsync(TestServer server)
    {
        var pdf = Payload(2048);
        var mp3 = Payload(4096);
        var zip = Payload(8192);
        const string html = """
            <html><head><title>资源页</title></head><body>
            <a href="files/doc.pdf">文档</a>
            <a href="#top">锚点</a>
            <a href="mailto:hi@example.org">邮件</a>
            <a href="data:text/plain,hi">data</a>
            <a href="media/song.mp3">歌曲</a>
            <a href="media/clip.zip">压缩包</a>
            </body></html>
            """;
        server.Map("page/batch", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(html), "text/html"));
        server.Map("page/files/doc.pdf", server.RangeFile(pdf, contentType: "application/pdf"));
        server.Map("page/media/song.mp3", server.RangeFile(mp3, contentType: "audio/mpeg"));
        server.Map("page/media/clip.zip", server.RangeFile(zip, contentType: "application/zip"));

        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 3 });
        var allDone = NewSignal();
        int done = 0;
        kernel.TaskCompleted += _ => { if (Interlocked.Increment(ref done) == 3) allDone.TrySetResult(); };

        var tasks = await kernel.AddPageBatchAsync(server.Base + "page/batch", dir, TaskPriority.Normal, StartNow: true);

        Equal(3, tasks.Count, "页面应解析出 3 条媒体草稿（锚点/data:/mailto 跳过）");
        That(tasks[0].Url.EndsWith("doc.pdf"), "第 1 条草稿应是 doc.pdf（按页面出现序）");
        That(tasks[1].Url.EndsWith("song.mp3"), "第 2 条草稿应是 song.mp3");
        That(tasks[2].Url.EndsWith("clip.zip"), "第 3 条草稿应是 clip.zip");
        await WaitSignalAsync(allDone.Task, "3 个批量任务应全部完成", 60_000);
        That(tasks.All(t => t.Status == DownloadStatus.Completed), "批量任务状态应全为 Completed");
        Equal(Hash(pdf), Hash(File.ReadAllBytes(Path.Combine(dir, "doc.pdf"))), "pdf 落盘字节应完整");
        Equal(Hash(mp3), Hash(File.ReadAllBytes(Path.Combine(dir, "song.mp3"))), "mp3 落盘字节应完整");
        Equal(Hash(zip), Hash(File.ReadAllBytes(Path.Combine(dir, "clip.zip"))), "zip 落盘字节应完整");
        Equal(3, kernel.Tasks.Count, "内核应恰好只有批量入队的 3 个任务");
    }

    // ── 37. 过期链接刷新（P2-8：403/410 → 从来源页重提直链并重试） ─────────

    private static async Task ExpiredLinkRefreshAsync(TestServer server)
    {
        var data = Payload(64 * 1024);
        server.Map("refresh/file-v2.zip", server.RangeFile(data));
        int pageHits = 0;
        server.Map("refresh/page", ctx =>
        {
            int hit = Interlocked.Increment(ref pageHits);
            var html = hit <= 1
                ? """<html><head><title>刷新页</title></head><body><a href="file-v1.zip">文件</a></body></html>"""
                : """<html><head><title>刷新页</title></head><body><a href="file-v2.zip">文件</a></body></html>""";
            TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(html), "text/html");
        });
        server.Map("refresh/file-v1.zip", ctx =>
        {
            ctx.Response.StatusCode = 403; // 旧直链已过期
            ctx.Response.ContentLength64 = 0;
            ctx.Response.Close();
        });

        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 });
        var completed = NewSignal();
        kernel.TaskCompleted += _ => completed.TrySetResult();

        var tasks = await kernel.AddPageBatchAsync(server.Base + "refresh/page", dir, TaskPriority.Normal, StartNow: true);
        Equal(1, tasks.Count, "来源页应有 1 条草稿");
        await WaitSignalAsync(completed.Task, "403 后应自动刷新直链并完成", 60_000);
        Equal(DownloadStatus.Completed, tasks[0].Status, "任务最终应 Completed");
        That(tasks[0].Url.EndsWith("file-v2.zip"), "任务 URL 应已换成刷新后的直链");
        Equal(Hash(data), Hash(File.ReadAllBytes(Path.Combine(dir, "file-v2.zip"))), "刷新后新直链内容应完整");
        That(tasks[0].Log.Any(l => l.Message.Contains("刷新")), "日志应记录直链刷新");
    }
}
