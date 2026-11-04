namespace NetTrans.Kernel;

/// <summary>What the 新建下载 sheet hands the kernel.</summary>
/// <param name="Headers">Per-task request headers (cookies / referer, Ghost-style); null = defaults.</param>
/// <param name="ExpectedHash">
/// 期望指纹（发布页/SHA256SUMS 里抄来的十六进制）：32 位按 MD5、40 位 SHA-1、
/// 64 位 SHA-256 自动判别；下载完成后比对，不匹配任务进入 Error。null = 不校验。
/// </param>
public sealed record NewDownloadRequest(
    string Url,
    string SaveTo,
    string Category,
    int Connections,
    TaskPriority Priority,
    bool StartNow,
    string? ScheduledAt = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? ExpectedHash = null);
