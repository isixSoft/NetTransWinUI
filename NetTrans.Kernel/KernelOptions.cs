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

    /// <summary>Checkpoints (断点续传) are written next to the target file with this suffix.</summary>
    public string CheckpointSuffix { get; set; } = ".nettrans.json";
}
