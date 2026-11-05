namespace NetTrans.Kernel;

/// <summary>What the 新建下载 sheet hands the kernel.</summary>
/// <param name="Headers">Per-task request headers (cookies / referer, Ghost-style); null = defaults.</param>
/// <param name="ExpectedHash">
/// 期望指纹（发布页/SHA256SUMS 里抄来的十六进制）：32 位按 MD5、40 位 SHA-1、
/// 64 位 SHA-256 自动判别；下载完成后比对，不匹配任务进入 Error。null = 不校验。
/// </param>
/// <param name="SeedRatio">PT 做种目标分享率（如 1.5）；null = 未指定（模板占位符替换为 0）。</param>
/// <param name="SeedHours">PT 做种目标小时数；null = 未指定。</param>
/// <param name="OnComplete">任务级「完成后动作」；None = 无（全局 AllComplete 仍可能收尾触发）。</param>
/// <param name="OnCompleteCommand">OnComplete = RunCommand 的命令模板（{path} {name} 占位符）。</param>
/// <param name="Queue">队列分组名（P0-3）；空 = 不属于任何命名队列，定时调度不触碰。</param>
/// <param name="CookieFile">Netscape cookies.txt 路径（P1-1）：按域匹配注入 Cookie 头；
/// 显式 Headers["Cookie"] 优先，全局 KernelOptions.CookieFile 兜底。null = 不导入。</param>
/// <param name="SelectedFiles">BT per-file 选择（P1-6）：种子 info.files 的 1-based 索引
/// （aria2 --select-file 语义，按勾选顺序透传）；null/空 = 不选择（{files} 展开为空）。</param>
/// <param name="NameOverride">任务初始文件名（P2-5 ZIP 成员）：空 = URL 文件名；
/// Kind/Category 由该名推导，使任务一开始就展示目标成员。</param>
/// <param name="SourcePage">来源页面 URL（P2-8 过期链接刷新）：批量抓取/页面提取
/// 创建的任务自动带上；直链 403/410 时内核从该页重新提取新直链并重试一次。</param>
public sealed record NewDownloadRequest(
    string Url,
    string SaveTo,
    string Category,
    int Connections,
    TaskPriority Priority,
    bool StartNow,
    string? ScheduledAt = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? ExpectedHash = null,
    double? SeedRatio = null,
    int? SeedHours = null,
    AfterCompletion OnComplete = AfterCompletion.None,
    string? OnCompleteCommand = null,
    string Queue = "",
    string? CookieFile = null,
    IReadOnlyList<int>? SelectedFiles = null,
    string? NameOverride = null,
    string? SourcePage = null);
