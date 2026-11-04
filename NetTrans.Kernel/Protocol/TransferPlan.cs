namespace NetTrans.Kernel.Protocol;

/// <summary>
/// How a probed resource is to be transferred. The driver decides; the
/// transfer layer executes without knowing the protocol behind it.
/// </summary>
public abstract record TransferPlan
{
    /// <summary>
    /// One logical resource fetched over HTTP as byte ranges. When
    /// <paramref name="AcceptRanges"/> is false (or the size is unknown) the
    /// session falls back to a single streaming connection. Mirrors are
    /// optional extra sources validated to serve the identical resource.
    /// </summary>
    public sealed record ByteRanges(
        string Url,
        long Size,
        bool AcceptRanges,
        IReadOnlyList<string> Mirrors) : TransferPlan;

    /// <summary>FTP / FTPS download: a single binary stream (no byte ranges).</summary>
    public sealed record FtpFile(string Url, long Size) : TransferPlan;

    /// <summary>
    /// Discrete segment files fetched whole and committed to the output in
    /// order (HLS / DASH). The first entry may be an init segment (fMP4) or
    /// an EXT-X-MAP; URLs must already be absolute.
    /// </summary>
    public sealed record SegmentFiles(IReadOnlyList<string> Segments, long? TotalSize) : TransferPlan;

    /// <summary>
    /// The data plane belongs to a host-side engine (libtorrent / aria2 / qBittorrent …).
    /// SeedRatio/SeedHours 是 PT 做种目标（分享率 / 小时数），null = 未指定；
    /// TransferSession 会用任务参数覆盖后交给 EngineRunner / 命令模板占位符。
    /// </summary>
    public sealed record ExternalEngine(string Engine, string Detail,
        double? SeedRatio = null, int? SeedHours = null) : TransferPlan;

    /// <summary>The resource cannot be transferred by this kernel (e.g. encrypted HLS, live stream).</summary>
    public sealed record Unsupported(string Reason) : TransferPlan;
}
