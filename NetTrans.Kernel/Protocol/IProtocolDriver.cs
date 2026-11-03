namespace NetTrans.Kernel.Protocol;

/// <summary>
/// One protocol implementation: turn a URL into a <see cref="ResourceProbe"/>.
/// Drivers never transfer bytes — they hand back a <see cref="TransferPlan"/>
/// for the transfer layer.
/// </summary>
public interface IProtocolDriver
{
    DownloadProtocol Protocol { get; }

    Task<ResourceProbe> ProbeAsync(string url, DriverContext context, CancellationToken cancellation);
}

/// <summary>Kernel log sink: message plus an error flag that defaults to false at the call site.</summary>
public delegate void LogSink(string message, bool isError = false);

/// <summary>What a driver may touch while probing: the shared HTTP client, options and a log sink.</summary>
public sealed class DriverContext
{
    public required KernelOptions Options { get; init; }
    public required HttpClient Http { get; init; }

    /// <summary>Merged headers (options defaults + per-task) — cookies/referer for playlists and mirrors.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Wired by the kernel to the owning task's 日志 tab; callable from any thread.</summary>
    public LogSink? Log { get; init; }
}
