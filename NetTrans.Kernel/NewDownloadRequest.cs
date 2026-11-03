namespace NetTrans.Kernel;

/// <summary>What the 新建下载 sheet hands the kernel.</summary>
public sealed record NewDownloadRequest(
    string Url,
    string SaveTo,
    string Category,
    int Connections,
    TaskPriority Priority,
    bool StartNow,
    string? ScheduledAt = null);
