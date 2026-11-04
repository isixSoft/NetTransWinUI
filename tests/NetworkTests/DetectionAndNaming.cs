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

}
