using System.Text.Json;
using System.Text.RegularExpressions;
using NetTrans.Kernel.Protocol;

namespace NetTrans.Kernel.Sites;

/// <summary>
/// YouTube 页面：Innertube player API（ANDROID 客户端档案）提取。只提供
/// 「渐进式」格式（音视频合一）且带明文 url 的条目——自适应流要么缺音轨
/// （内核无混流器）要么需要 JS 解签，不装懂。全部加密时如实失败；
/// hlsManifestUrl 存在时追加 HLS 兜底流。YouTube 改版只需更新本文件。
/// </summary>
public sealed class YouTubeSite : ISiteExtractor
{
    public string Name => "YouTube";

    private const string ApiBase = "https://www.youtube.com/";

    private static readonly Regex VideoIdRegex =
        new(@"(?:v=|/shorts/|youtu\.be/)([0-9A-Za-z_-]{11})", RegexOptions.Compiled);

    public bool CanHandle(string url) =>
        SiteUrls.HostMatches(url, "youtube.com", "youtu.be") && VideoIdRegex.IsMatch(url);

    public async Task<SiteMedia?> ExtractAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        var apiBase = SiteHttp.ResolveBase(context, Name, ApiBase);
        var videoId = VideoIdRegex.Match(url).Groups[1].Value;

        var body = JsonSerializer.Serialize(new
        {
            videoId,
            context = new
            {
                client = new
                {
                    clientName = "ANDROID",
                    clientVersion = "19.09.37",
                    androidSdkVersion = 30,
                    hl = "en",
                },
            },
        });

        using var json = await SiteHttp.PostJsonAsync(
            $"{apiBase}youtubei/v1/player", body, context, null, cancellation);
        var root = json.RootElement;

        if (root.TryGetProperty("playabilityStatus", out var status) &&
            status.TryGetProperty("status", out var statusValue) &&
            statusValue.GetString() is { } state && state != "OK")
        {
            throw new FormatException($"YouTube 拒绝了请求（{state}）");
        }

        var title = root.TryGetProperty("videoDetails", out var details) &&
                    details.TryGetProperty("title", out var titleElement)
            ? titleElement.GetString() : null;

        var streams = new List<SiteStream>();
        if (root.TryGetProperty("streamingData", out var streamingData))
        {
            if (streamingData.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
            {
                var progressive = new List<(long Bitrate, string Url, string Label)>();
                foreach (var format in formats.EnumerateArray())
                {
                    // 没有 url = signatureCipher 加密格式，需要 JS 解签，跳过
                    if (!format.TryGetProperty("url", out var formatUrl) ||
                        formatUrl.GetString() is not { Length: > 0 } streamUrl)
                    {
                        continue;
                    }

                    var bitrate = format.TryGetProperty("bitrate", out var b) && b.TryGetInt64(out var value)
                        ? value : 0;
                    var label = format.TryGetProperty("qualityLabel", out var ql) ? ql.GetString() : null;
                    progressive.Add((bitrate, streamUrl, label ?? "渐进式"));
                }

                progressive.Sort((a, b) => b.Bitrate.CompareTo(a.Bitrate));
                streams.AddRange(progressive.Select(f => new SiteStream(f.Url, f.Label)));
            }

            if (streamingData.TryGetProperty("hlsManifestUrl", out var hls) &&
                hls.GetString() is { Length: > 0 } hlsUrl)
            {
                streams.Add(new SiteStream(hlsUrl, "HLS 清单（兜底）"));
            }
        }

        if (streams.Count == 0)
            throw new FormatException("没有可用直链（格式已加密，需要 JS 解签或混流器）— 更新 YouTubeSite 适配");

        return new SiteMedia(Name, string.IsNullOrWhiteSpace(title) ? "YouTube 视频" : title!, streams);
    }
}
