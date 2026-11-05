using System.Text.Json;
using System.Text.RegularExpressions;
using NetTrans.Kernel.Protocol;

namespace NetTrans.Kernel.Sites;

/// <summary>
/// X / Twitter 视频推文：走网页嵌入同款公开 syndication 端点
/// （cdn.syndication.twimg.com/tweet-result，无需鉴权）。视频取最高码率
/// mp4 变体，m3u8 变体作为 HLS 驱动的兜底流。端点若变，只改本文件。
/// </summary>
public sealed class TwitterSite : ISiteExtractor
{
    public string Name => "X / Twitter";

    private const string ApiBase = "https://cdn.syndication.twimg.com/";

    private static readonly Regex StatusRegex =
        new(@"^/[A-Za-z0-9_]{1,20}/status(?:es)?/(\d+)", RegexOptions.Compiled);

    public bool CanHandle(string url) =>
        SiteUrls.HostMatches(url, "x.com", "twitter.com") && StatusRegex.IsMatch(SiteUrls.PathOf(url));

    public async Task<SiteMedia?> ExtractAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        var apiBase = SiteHttp.ResolveBase(context, Name, ApiBase);
        var id = StatusRegex.Match(SiteUrls.PathOf(url)).Groups[1].Value;

        // token 只是缓存键（官方嵌入脚本用 id 派生的伪随机数），数值即可
        var token = (long)(long.Parse(id) / 1e15 * Random.Shared.NextDouble() * 1e15);
        using var json = await SiteHttp.GetJsonAsync(
            $"{apiBase}tweet-result?id={id}&token={token}&lang=zh-cn", context, null, cancellation);

        var root = json.RootElement;
        if (!root.TryGetProperty("mediaDetails", out var media) ||
            media.ValueKind != JsonValueKind.Array || media.GetArrayLength() == 0)
        {
            throw new FormatException("推文里没有视频/动图（纯文字或纯图片）");
        }

        var first = media[0];
        var type = first.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (type is not ("video" or "animated_gif"))
            throw new FormatException($"推文媒体不是视频（{type}）");

        if (!first.TryGetProperty("video_info", out var videoInfo) ||
            !videoInfo.TryGetProperty("variants", out var variants) ||
            variants.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("video_info.variants 缺失（站点结构可能已变化）");
        }

        var mp4 = new List<(long Bitrate, string Url)>();
        string? hls = null;
        foreach (var variant in variants.EnumerateArray())
        {
            var variantUrl = variant.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(variantUrl)) continue;

            var contentType = variant.TryGetProperty("content_type", out var c) ? c.GetString() : null;
            if (contentType == "video/mp4")
                mp4.Add((variant.TryGetProperty("bitrate", out var b) && b.TryGetInt64(out var bitrate)
                    ? bitrate : 0, variantUrl));
            else if (contentType == "application/x-mpegurl")
                hls = variantUrl;
        }

        if (mp4.Count == 0 && hls is null)
            throw new FormatException("推文视频没有可用的流变体");

        mp4.Sort((a, b) => b.Bitrate.CompareTo(a.Bitrate));

        var streams = new List<SiteStream>();
        foreach (var (_, variantUrl) in mp4)
            streams.Add(new SiteStream(variantUrl, "MP4 视频"));
        if (hls is not null) streams.Add(new SiteStream(hls, "HLS（兜底）"));

        return new SiteMedia(Name, Title(root), streams);
    }

    private static string Title(JsonElement root)
    {
        var author = root.TryGetProperty("user", out var user) &&
                     user.TryGetProperty("screen_name", out var screenName)
            ? screenName.GetString() : null;
        var text = root.TryGetProperty("text", out var textElement)
            ? textElement.GetString() : null;
        text = text?.Split('\n', 2)[0].Trim();

        var title = string.IsNullOrWhiteSpace(author) ? (text ?? "推文视频") : $"@{author}：{text}";
        return title.Length > 80 ? title[..80] : title;
    }
}
