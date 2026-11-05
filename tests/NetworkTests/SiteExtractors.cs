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
}
