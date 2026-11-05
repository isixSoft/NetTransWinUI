namespace NetTrans.Kernel.Transfer;

/// <summary>
/// Persisted resume state (断点续传), written beside the target file as JSON
/// (path + <see cref="KernelOptions.CheckpointSuffix"/>). Two shapes share the
/// record: "ranges" uses Start/End/Done; "segments" (HLS/DASH) uses Committed
/// — byte lengths of the ordered, already-written segment prefix — plus a
/// Fingerprint of the playlist so a changed manifest invalidates the resume.
/// </summary>
public sealed class TransferCheckpoint
{
    public string Kind { get; set; } = "ranges";
    public string Url { get; set; } = "";
    public string? ETag { get; set; }
    public long Size { get; set; } = -1;
    public long[] Start { get; set; } = [];
    public long[] End { get; set; } = [];
    public long[] Done { get; set; } = [];
    public long[] Committed { get; set; } = [];
    public string? Fingerprint { get; set; }
}
