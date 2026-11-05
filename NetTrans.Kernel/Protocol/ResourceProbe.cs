namespace NetTrans.Kernel.Protocol;

/// <summary>
/// What a driver learned about a resource before any byte moves: identity
/// (name / size / etag) plus the plan for transferring it.
/// </summary>
/// <param name="Name">File name for the task; the kernel sanitizes it.</param>
/// <param name="Size">Bytes, or -1 when the server does not tell.</param>
/// <param name="AcceptRanges">True when byte-range requests are honoured.</param>
/// <param name="ETag">Entity tag, when present — part of the resume identity check.</param>
/// <param name="FinalUrl">URL after redirect handling (the probe target itself for now).</param>
/// <param name="Protocol">The protocol that produced this probe.</param>
/// <param name="Plan">How to transfer it.</param>
/// <param name="SuggestedProtocol">Set when the content itself says it is another protocol
/// (a body sniffed as m3u8/mpd) so the registry can hand the probe to that driver.</param>
/// <param name="Trackers">BT/PT tracker 展示列表（已过 SensitiveUrls.Mask 脱敏），非 BT 为 null。</param>
/// <param name="ExtraHeaders">站点直链所需的附加头（Referer/UA 等）；内核并入传输会话，
/// 压过默认与任务级同名字头——站点最懂自己的 CDN（与 yt-dlp 同策略）。</param>
public sealed record ResourceProbe(
    string Name,
    long Size,
    bool AcceptRanges,
    string? ETag,
    string FinalUrl,
    DownloadProtocol Protocol,
    TransferPlan Plan,
    DownloadProtocol? SuggestedProtocol = null,
    IReadOnlyList<string>? Trackers = null,
    IReadOnlyDictionary<string, string>? ExtraHeaders = null);
