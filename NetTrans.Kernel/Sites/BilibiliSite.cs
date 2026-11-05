using System.Text.Json;
using System.Text.RegularExpressions;
using NetTrans.Kernel.Protocol;

namespace NetTrans.Kernel.Sites;

/// <summary>
/// Bilibili 视频页（www.bilibili.com/video/BV…、b23.tv 短链）。view 接口取
/// 标题与 cid（支持 ?p= 分P），playurl 接口取 durl 渐进式直链——音画合一，
/// 平台内核没有混流器，不用 DASH 分轨。接口与 CDN 都要求站点 Referer。
/// 接口改版只需更新本文件。
/// </summary>
public sealed class BilibiliSite : ISiteExtractor
{
    public string Name => "Bilibili";

    private const string ApiBase = "https://api.bilibili.com/";
    private const string PageReferer = "https://www.bilibili.com/";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private static readonly Regex BvRegex = new("BV[0-9A-Za-z]{10}", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> SiteHeaders = new Dictionary<string, string>
    {
        ["Referer"] = PageReferer,
        ["User-Agent"] = UserAgent,
    };

    public bool CanHandle(string url) => SiteUrls.HostMatches(url, "bilibili.com", "b23.tv");

    public async Task<SiteMedia?> ExtractAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        var apiBase = SiteHttp.ResolveBase(context, Name, ApiBase);
        var headers = SiteHttp.MergeHeaders(context.Headers, ("Referer", PageReferer), ("User-Agent", UserAgent));

        // b23.tv 短链：先跟随跳转拿到正式页面
        var pageUrl = SiteUrls.HostMatches(url, "b23.tv")
            ? await SiteHttp.FinalUrlAsync(url, context, headers, cancellation)
            : url;

        var bvid = BvRegex.Match(pageUrl).Value;
        if (bvid.Length == 0) bvid = SiteUrls.Query(pageUrl, "bvid") ?? "";
        if (bvid.Length == 0) throw new FormatException("页面 URL 中没有 BV 号");

        int page = int.TryParse(SiteUrls.Query(pageUrl, "p"), out var parsed) && parsed > 0 ? parsed : 1;

        using var view = await SiteHttp.GetJsonAsync(
            $"{apiBase}x/web-interface/view?bvid={Uri.EscapeDataString(bvid)}", context, headers, cancellation);
        var (title, cid) = ViewInfo(view, page, context.Log);

        using var play = await SiteHttp.GetJsonAsync(
            $"{apiBase}x/player/playurl?bvid={Uri.EscapeDataString(bvid)}&cid={cid}&qn=64&platform=html5&high_quality=1",
            context, headers, cancellation);

        return new SiteMedia(Name, title, new[] { PlayStream(play) });
    }

    private static (string Title, long Cid) ViewInfo(JsonDocument view, int page, LogSink? log)
    {
        var root = view.RootElement;
        if (root.TryGetProperty("code", out var code) && code.GetInt64() != 0)
        {
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new FormatException($"view 接口返回 {code.GetInt64()}（{message ?? "未知错误"}）");
        }

        var data = root.GetProperty("data");
        var title = data.GetProperty("title").GetString() ?? "Bilibili 视频";
        long cid = data.TryGetProperty("cid", out var rootCid) ? rootCid.GetInt64() : 0;

        if (data.TryGetProperty("pages", out var pages) &&
            pages.ValueKind == JsonValueKind.Array && pages.GetArrayLength() > 0)
        {
            if (pages.GetArrayLength() > 1)
                log?.Invoke($"Bilibili：{pages.GetArrayLength()} 个分P，取第 {page} 个");
            var chosen = pages[Math.Clamp(page, 1, pages.GetArrayLength()) - 1];
            if (chosen.TryGetProperty("cid", out var pageCid)) cid = pageCid.GetInt64();
        }

        if (cid == 0) throw new FormatException("view 接口没有返回 cid（站点结构可能已变化）");
        return (title, cid);
    }

    private SiteStream PlayStream(JsonDocument play)
    {
        var root = play.RootElement;
        if (root.TryGetProperty("code", out var code) && code.GetInt64() != 0)
        {
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new FormatException($"playurl 接口返回 {code.GetInt64()}（{message ?? "未知错误"}）");
        }

        var data = root.GetProperty("data");
        if (data.TryGetProperty("durl", out var durl) &&
            durl.ValueKind == JsonValueKind.Array && durl.GetArrayLength() > 0)
        {
            var first = durl[0];
            var url = first.GetProperty("url").GetString();
            if (string.IsNullOrEmpty(url)) throw new FormatException("durl 直链为空");

            long? size = first.TryGetProperty("size", out var sizeElement) &&
                         sizeElement.TryGetInt64(out var parsed) && parsed > 0 ? parsed : null;
            int qn = data.TryGetProperty("quality", out var quality) && quality.TryGetInt32(out var q) ? q : 0;
            return new SiteStream(url, QualityLabel(qn), size, SiteHeaders);
        }

        throw new FormatException("playurl 没有 durl 直链（fnval 已改 DASH 或需要登录）— 更新 BilibiliSite 适配");
    }

    private static string QualityLabel(int qn) => qn switch
    {
        127 => "8K 超高清", 126 => "杜比全景声", 125 => "HDR 真彩", 120 => "4K 超清",
        116 => "1080P 60帧", 112 => "1080P 高码率", 80 => "1080P 高清", 74 => "720P 60帧",
        64 => "720P 高清", 32 => "480P 清晰", 16 => "360P 流畅", 15 => "360P 流畅",
        _ => "默认画质",
    };
}
