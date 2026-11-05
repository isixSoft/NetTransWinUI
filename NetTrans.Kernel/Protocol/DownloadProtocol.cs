namespace NetTrans.Kernel.Protocol;

/// <summary>
/// The protocol surface, aligned with Ghost Downloader's network stack:
/// HTTP/HTTPS (multi-segment), FTP/FTPS, HLS (m3u8), MPEG-DASH (mpd),
/// BitTorrent (magnet / .torrent), eD2k and Metalink4 (.meta4).
/// </summary>
public enum DownloadProtocol
{
    /// <summary>Plain HTTP(S) with byte-range segmenting.</summary>
    Http,

    /// <summary>FTP / FTPS, single stream (REST ranges are unreliable across servers).</summary>
    Ftp,

    /// <summary>magnet: / .torrent — metadata parsing here; the data plane is an external-engine slot.</summary>
    BitTorrent,

    /// <summary>HLS media playlists (m3u8), unencrypted, VOD.</summary>
    Hls,

    /// <summary>MPEG-DASH manifests (mpd).</summary>
    Dash,

    /// <summary>ed2k:// links — metadata parsing here; the data plane is an external-engine slot.</summary>
    Ed2k,

    /// <summary>Metalink4 (.meta4/.metalink) — the file lists URLs, size and hash; the
    /// plan resolves to a plain ByteRanges download from the primary URL.</summary>
    Metalink,
}
