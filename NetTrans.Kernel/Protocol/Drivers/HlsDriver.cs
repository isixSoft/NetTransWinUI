using System.Text;
using System.Text.RegularExpressions;

namespace NetTrans.Kernel.Protocol.Drivers;

/// <summary>
/// HLS (m3u8) driver: fetches the playlist, walks master → highest-bandwidth
/// variant, rejects live and encrypted streams honestly, and plans the
/// segment list (init segment / EXT-X-MAP included first). Segments are
/// fetched whole and committed in order — the output is a concatenated TS
/// stream (remuxing to mp4 would need an external muxer, same as Ghost's
/// N_m3u8DL-RE pipeline).
/// </summary>
public sealed class HlsDriver : IProtocolDriver
{
    public DownloadProtocol Protocol => DownloadProtocol.Hls;

    public async Task<ResourceProbe> ProbeAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        string text;
        M3u8Playlist playlist;
        try
        {
            text = await DriverHelpers.GetTextAsync(url, context, cancellation);
            playlist = M3u8Playlist.Parse(text);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return DriverHelpers.Unsupported(url, DownloadProtocol.Hls, $"无法读取播放列表（{DriverHelpers.Brief(error.Message)}）");
        }

        // segment URIs are resolved against the playlist they came from
        string playlistUrl = url;

        if (playlist.Variants.Count > 0)
        {
            var best = playlist.Variants.MaxBy(v => v.Bandwidth);
            context.Log?.Invoke($"主播放列表：选择最高码率变体（{best.Bandwidth} bps）");
            playlistUrl = DriverHelpers.ResolveUrl(best.Url, playlistUrl);
            try
            {
                text = await DriverHelpers.GetTextAsync(playlistUrl, context, cancellation);
                playlist = M3u8Playlist.Parse(text);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return DriverHelpers.Unsupported(url, DownloadProtocol.Hls, $"无法读取变体播放列表（{DriverHelpers.Brief(error.Message)}）");
            }
        }

        if (!playlist.HasEndList)
            return DriverHelpers.Unsupported(url, DownloadProtocol.Hls, "直播流（无 EXT-X-ENDLIST）暂不支持");

        if (playlist.KeyMethod is not (null or "NONE"))
            return DriverHelpers.Unsupported(url, DownloadProtocol.Hls, $"加密 HLS（{playlist.KeyMethod}）暂不支持");

        var segments = new List<string>(playlist.Segments.Count + 1);
        if (playlist.InitSegment is { } init) segments.Add(DriverHelpers.ResolveUrl(init, playlistUrl));
        segments.AddRange(playlist.Segments.Select(segment => DriverHelpers.ResolveUrl(segment, playlistUrl)));

        if (segments.Count == 0)
            return DriverHelpers.Unsupported(url, DownloadProtocol.Hls, "播放列表中没有可下载的分段");

        context.Log?.Invoke($"HLS：{segments.Count} 个分段，输出为拼接 TS 流（未重封装）");
        return new ResourceProbe(
            DriverHelpers.BaseName(url) + ".ts", -1, AcceptRanges: false, ETag: null, FinalUrl: url,
            DownloadProtocol.Hls,
            new TransferPlan.SegmentFiles(segments, null));
    }
}

/// <summary>The slice of M3U8 syntax the kernel needs; everything else is ignored.</summary>
internal sealed class M3u8Playlist
{
    public List<(long Bandwidth, string Url)> Variants { get; } = new();
    public List<string> Segments { get; } = new();
    public string? InitSegment { get; private set; }
    public string? KeyMethod { get; private set; }
    public bool HasEndList { get; private set; }

    public static M3u8Playlist Parse(string text)
    {
        var lines = text.Split('\n');
        if (!lines.Any(l => l.TrimStart().StartsWith("#EXTM3U")))
            throw new FormatException("不是有效的 M3U8 播放列表");

        var playlist = new M3u8Playlist();
        long? pendingVariantBandwidth = null;
        bool pendingSegment = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                pendingVariantBandwidth = Attribute(line, "BANDWIDTH") is { } value &&
                                          long.TryParse(value, out var parsedBandwidth) ? parsedBandwidth : 0;
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase))
            {
                playlist.KeyMethod = Attribute(line, "METHOD");
                continue;
            }

            if (line.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase))
            {
                if (Attribute(line, "URI") is { } map) playlist.InitSegment = map;
                continue;
            }

            if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
            {
                playlist.HasEndList = true;
                continue;
            }

            if (line.StartsWith('#'))
            {
                // EXTINF and other tags: the next non-tag line is a segment
                if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)) pendingSegment = true;
                continue;
            }

            if (pendingVariantBandwidth is { } bandwidth)
            {
                playlist.Variants.Add((bandwidth, line));
                pendingVariantBandwidth = null;
            }
            else if (pendingSegment || playlist.Variants.Count == 0)
            {
                playlist.Segments.Add(line);
                pendingSegment = false;
            }
        }

        return playlist;
    }

    private static string? Attribute(string line, string name)
    {
        // ATTRIBUTE=value pairs, values may be quoted
        foreach (Match match in AttributeRegex.Matches(line))
        {
            if (match.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var value = match.Groups["value"].Value;
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private static readonly Regex AttributeRegex =
        new(@"(?<name>[A-Z0-9-]+)=(""(?<value>[^""]*)""|(?<value>[^,]*))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
