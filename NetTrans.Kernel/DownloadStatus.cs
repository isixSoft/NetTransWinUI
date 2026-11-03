namespace NetTrans.Kernel;

/// <summary>The handoff's `state` field: run | paused | done | error | queued.</summary>
public enum DownloadStatus
{
    Downloading,
    Queued,
    Paused,
    Error,
    Completed,
}
