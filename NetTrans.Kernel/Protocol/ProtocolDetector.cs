namespace NetTrans.Kernel.Protocol;

/// <summary>
/// URL-based protocol detection: the first hop of the registry's probe.
/// Magnet and ed2k are scheme-driven; m3u8 / mpd / torrent are extension
/// hints (a body-level sniff happens later, in the HTTP driver, via
/// <see cref="ResourceProbe.SuggestedProtocol"/>).
/// </summary>
public static class ProtocolDetector
{
    public static DownloadProtocol Detect(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return DownloadProtocol.Http;

        string trimmed = url.Trim();
        if (trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return DownloadProtocol.BitTorrent;
        if (trimmed.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase)) return DownloadProtocol.Ed2k;

        string path = trimmed;
        int cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".m3u8" or ".m3u") return DownloadProtocol.Hls;
        if (extension == ".mpd") return DownloadProtocol.Dash;
        if (extension == ".torrent") return DownloadProtocol.BitTorrent;
        if (extension is ".meta4" or ".metalink") return DownloadProtocol.Metalink;

        if (trimmed.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase)) return DownloadProtocol.Ftp;

        return DownloadProtocol.Http;
    }
}
