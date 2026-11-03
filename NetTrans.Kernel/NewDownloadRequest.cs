namespace NetTrans.Kernel;

/// <summary>What the 新建下载 sheet hands the kernel.</summary>
/// <param name="Headers">Per-task request headers (cookies / referer, Ghost-style); null = defaults.</param>
public sealed record NewDownloadRequest(
    string Url,
    string SaveTo,
    string Category,
    int Connections,
    TaskPriority Priority,
    bool StartNow,
    string? ScheduledAt = null,
    IReadOnlyDictionary<string, string>? Headers = null);
