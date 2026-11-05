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

internal sealed partial class Program
{
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

    // ── 26. Cookie 导入（P1-1）：Netscape 解析 + 按域注入 ───────────────────

    private static Task CookieParseUnitAsync()
    {
        var soon = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var stale = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var content = string.Join('\n',
            "# Netscape HTTP Cookie File（注释行应跳过）",
            "",
            $".example.com\tTRUE\t/\tFALSE\t{soon}\tsessionid\tabc123",
            $"#HttpOnly_.example.com\tTRUE\t/\tTRUE\t{soon}\thttponly_tok\th!!j",
            $"other.net\tFALSE\t/\tFALSE\t{soon}\tother\tx",
            $"127.0.0.1\tFALSE\t/\tFALSE\t{stale}\texpired\tgone",
            $"127.0.0.1\tFALSE\t/\tFALSE\t0\tsession\ts1",
            "这行不是 cookie 数据",
            $"127.0.0.1\tFALSE\t/\tFALSE\t{soon}\tplain\tv v");

        var cookies = NetscapeCookies.Parse(content);
        Equal(5, cookies.Count, $"应解析出 5 条有效 cookie（实际 {cookies.Count}）");

        // RFC 6265：secure cookie 仅随 https 发送。子域断言走 https（两条都该在），
        // 裸域断言走 http（secure 的 httponly_tok 必须被剔除，只剩 sessionid）。
        var header = NetscapeCookies.HeaderFor(cookies, new Uri("https://www.example.com/v/file.bin"));
        That(header is not null && header.Contains("sessionid=abc123") && header.Contains("httponly_tok=h!!j"),
            $"example.com 子域 https 请求应带两条 .example.com cookie，#HttpOnly_ 行必须可用（实际「{header}」）");
        That(!header!.Contains("other="), "其他域 cookie 不得注入");

        Equal("sessionid=abc123", NetscapeCookies.HeaderFor(cookies, new Uri("http://example.com/")),
            "裸域也应匹配 .example.com 的 domain cookie，且 http 不得携带 secure cookie");

        var local = NetscapeCookies.HeaderFor(cookies, new Uri("http://127.0.0.1/x/file.bin"));
        That(local is not null && local.Contains("session=s1") && local.Contains("plain=v v"),
            $"本地域应保留 session(0) 与普通 cookie（实际「{local}」）");
        That(!local!.Contains("expired=gone"), "过期 cookie 必须剔除");

        Equal(null, NetscapeCookies.HeaderFor(new List<NetscapeCookie>(), new Uri("http://example.com/")),
            "空集 → null");
        Equal(0, NetscapeCookies.Parse("   \n# 只有一行注释\n").Count, "空内容 → 0 条");
        return Task.CompletedTask;
    }

    private static async Task CookieInjectAsync(TestServer server)
    {
        var data = Payload(24 * 1024);
        var file = server.RangeFile(data, etag: "\"cookie-1\"");
        string? seenCookie = null;
        server.Map("cookie/file.bin", ctx =>
        {
            seenCookie = ctx.Request.Headers["Cookie"];
            file(ctx);
        });

        var soon = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var taskCookiePath = Path.Combine(TempDir(), "cookies.txt");
        File.WriteAllText(taskCookiePath, string.Join('\n',
            "# Netscape HTTP Cookie File",
            $"127.0.0.1\tFALSE\t/\tFALSE\t{soon}\tsessionid\tabc123",
            $"example.com\tFALSE\t/\tFALSE\t{soon}\twrongdom\tno"));
        var globalCookiePath = Path.Combine(TempDir(), "global-cookies.txt");
        File.WriteAllText(globalCookiePath,
            $"127.0.0.1\tFALSE\t/\tFALSE\t{soon}\tglobal\tyes");

        // 任务级 CookieFile：请求按域携带 Cookie 头（只含本域 cookie）
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "cookie/file.bin", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true, CookieFile: taskCookiePath));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "带 cookie 文件的任务应完成", 30_000);
            Equal("sessionid=abc123", seenCookie, $"请求头应携带按域匹配的 Cookie（实际「{seenCookie}」）");
        }

        // 无 cookie 文件：不发 Cookie 头
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "cookie/file.bin", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "无 cookie 任务应完成", 30_000);
            That(string.IsNullOrEmpty(seenCookie), $"无 cookie 文件时不应携带 Cookie 头（实际「{seenCookie}」）");
        }

        // 任务显式 Cookie 头优先于 cookie 文件
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "cookie/file.bin", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true, CookieFile: taskCookiePath,
                Headers: new Dictionary<string, string> { ["Cookie"] = "manual=1" }));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "显式头任务应完成", 30_000);
            Equal("manual=1", seenCookie, "任务显式 Cookie 头应优先于 cookie 文件");
        }

        // 全局 CookieFile 注入；任务级文件优先于全局文件
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1, CookieFile = globalCookiePath }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "cookie/file.bin", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "全局 cookie 任务应完成", 30_000);
            Equal("global=yes", seenCookie, "全局 CookieFile 应按域注入");
        }
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1, CookieFile = globalCookiePath }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "cookie/file.bin", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true, CookieFile: taskCookiePath));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "任务级+全局共存任务应完成", 30_000);
            Equal("sessionid=abc123", seenCookie, "任务级 cookie 文件应优先于全局");
        }

        // cookie 文件缺失/损坏：静默降级，任务照常完成
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "cookie/file.bin", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true, CookieFile: Path.Combine(TempDir(), "missing.txt")));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "cookie 文件缺失任务应照常完成", 30_000);
        }
    }
}
