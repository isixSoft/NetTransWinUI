using NetTrans.Kernel.Protocol;

namespace NetTrans.Kernel;

/// <summary>
/// Everything the real kernel needs from the host app: network policy, limits,
/// and the optional plug-in slots (mirror resolver, custom HTTP handler).
/// The simulated kernel ignores this class entirely.
/// </summary>
public sealed class KernelOptions
{
    /// <summary>User-Agent sent on HTTP requests.</summary>
    public string UserAgent { get; set; } = "NetTrans/1.0";

    /// <summary>Default segment count per task ("连接数" in the 新建下载 sheet).</summary>
    public int ConnectionsPerTask { get; set; } = 8;

    /// <summary>How many tasks run at once; the rest wait in the priority queue.</summary>
    public int MaxSimultaneous { get; set; } = 3;

    /// <summary>Per-request retry budget before a segment gives the task up.</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>Global speed cap in bytes/s; null or 0 = unlimited. Re-applies to every live task immediately.</summary>
    public long? GlobalSpeedLimit { get; set; }

    /// <summary>
    /// http:// / https:// / socks5:// proxy, or null for direct connections.
    /// HTTP(S) proxies go through WebProxy; SOCKS5 through the built-in connector.
    /// </summary>
    public string? Proxy { get; set; }

    /// <summary>Headers sent with every request (cookies, referer, ...); per-task headers win.</summary>
    public IReadOnlyDictionary<string, string> DefaultHeaders { get; set; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Mirror slot: given a primary URL, return candidate mirror URLs
    /// (GitHub / HuggingFace-style acceleration). Each candidate is probed and
    /// only those serving the identical resource (same size, agreeing etag)
    /// join the source set. Null disables mirroring.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<string>>>? MirrorResolver { get; set; }

    /// <summary>
    /// Handler factory slot: return a custom <see cref="HttpMessageHandler"/> to
    /// steer TLS (fingerprint emulation à la wreq), DNS or connection pooling.
    /// .NET's SslStream cannot imitate a browser TLS handshake, so browser-grade
    /// fingerprints require an external stack injected through this slot.
    /// </summary>
    public Func<HttpMessageHandler>? HttpHandlerFactory { get; set; }

    /// <summary>
    /// Default save directory for tasks that carry no explicit target
    /// (the 新建下载 sheet's 「保存到」 left empty). Null/empty keeps the
    /// legacy behavior of writing into the process working directory.
    /// </summary>
    public string? DefaultSavePath { get; set; }

    /// <summary>
    /// When falling back to <see cref="DefaultSavePath"/>, append one
    /// subfolder per category (video/music/doc/soft/bt) — the 设置 sheet's
    /// 「分类文件夹」 toggle.
    /// </summary>
    public bool FoldersByCategory { get; set; }

    /// <summary>
    /// 「夜间不限速」：while the wall clock is inside the off-peak window
    /// (<see cref="OffPeakStart"/>…<see cref="OffPeakEnd"/>) the global speed
    /// cap is lifted; per-task limits still apply.
    /// </summary>
    public bool UncappedAtNight { get; set; }

    /// <summary>Off-peak window start, "HH:mm" (e.g. "23:00"); may cross midnight.</summary>
    public string OffPeakStart { get; set; } = "23:00";

    /// <summary>Off-peak window end, "HH:mm" (e.g. "07:00").</summary>
    public string OffPeakEnd { get; set; } = "07:00";

    /// <summary>
    /// 本地签名库文件（JSON，URL → 指纹历史）：每个完成的下载记录哈希，
    /// 再次下载同源时自动比对。null = 只进内存不落盘。
    /// </summary>
    public string? HashDatabasePath { get; set; }

    /// <summary>Checkpoints (断点续传) are written next to the target file with this suffix.</summary>
    public string CheckpointSuffix { get; set; } = ".nettrans.json";

    /// <summary>
    /// 小文件阈值（字节）：已知大小不超过它的任务全程内存缓冲，完成时一次性
    /// 落盘——无预分配、无半截中间态、不写断点。0 = 关闭（一律直写磁盘）。
    /// </summary>
    public long SmallFileThresholdBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// External-engine slot for the BT / eD2k data plane: when a probe resolves
    /// to an <see cref="TransferPlan.ExternalEngine"/> plan, this runner is
    /// invoked with the plan, the resolved save path and a cancellation token.
    /// Return true when the engine produced the file at the save path. Wins
    /// over <see cref="ExternalEngineCommand"/> when both are set.
    /// </summary>
    public Func<TransferPlan.ExternalEngine, string, CancellationToken, Task<bool>>? EngineRunner { get; set; }

    /// <summary>
    /// Shell command template for the external engine, with {url} {path} {name}
    /// placeholders (url = the task's URL, path = the resolved save path,
    /// name = the task name). Executed via "cmd /c" on Windows and
    /// "/bin/sh -c" elsewhere; exit code 0 plus the file present at the save
    /// path completes the task. <see cref="EngineRunner"/> wins when both are set.
    /// </summary>
    public string? ExternalEngineCommand { get; set; }
}
