using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using NetTrans.Kernel.Net;
using NetTrans.Kernel.Protocol;

namespace NetTrans.Kernel.Transfer;


public enum TransferOutcomeKind
{
    Completed,
    Paused,
    Failed,
}

public sealed record TransferOutcome(TransferOutcomeKind Kind, string? Error);

/// <summary>
/// One transfer job: a plan from a driver plus everything needed to execute it.
/// The kernel creates a session per start/resume and cancels the token to pause;
/// resume state round-trips through <see cref="TransferCheckpoint"/>.
/// </summary>
public sealed class TransferRequest
{
    public required TransferPlan Plan { get; init; }
    public required string SavePath { get; init; }
    public required HttpClient Http { get; init; }
    public required SpeedLimiter Limiter { get; init; }

    /// <summary>
    /// Kernel-wide shared token bucket enforcing the global speed cap across
    /// ALL tasks combined (N tasks together stay under the global value);
    /// waits are chained global-then-task. Null/zero = no global cap.
    /// </summary>
    public SpeedLimiter? GlobalLimiter { get; init; }

    /// <summary>ETag from the fresh probe — identity input for the resume check.</summary>
    public string? ETag { get; init; }

    public int Connections { get; init; } = 8;
    public int RetryCount { get; init; } = 3;

    /// <summary>
    /// 小文件阈值（字节）：已知大小不超过它的任务全程内存缓冲，完成时一次性
    /// 落盘——不预分配、无半截中间态、不写断点。0 = 关闭，一律直写磁盘。
    /// </summary>
    public long SmallFileThresholdBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Per-request headers (cookies / referer), already merged with defaults by the kernel.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Loaded checkpoint, already read from disk; validated here against the fresh probe.</summary>
    public TransferCheckpoint? Checkpoint { get; init; }

    /// <summary>Periodic + on-pause persistence sink (the kernel writes the file).</summary>
    public Action<TransferCheckpoint>? CheckpointChanged { get; init; }

    /// <summary>
    /// External-engine slot (BT / eD2k data plane): invoked with the plan, the
    /// resolved save path and the cancellation token; return true when the
    /// engine produced the file at the save path. Wins over
    /// <see cref="ExternalEngineCommand"/>.
    /// </summary>
    public Func<TransferPlan.ExternalEngine, string, CancellationToken, Task<bool>>? EngineRunner { get; init; }

    /// <summary>
    /// Shell command template for the external engine with {url} {path} {name}
    /// placeholders (task URL / resolved save path / task name). Runs via
    /// cmd /c on Windows and /bin/sh -c elsewhere; exit code 0 plus the file
    /// present at the save path completes the task.
    /// </summary>
    public string? ExternalEngineCommand { get; init; }

    /// <summary>Task URL and task name, filling the external-engine command template.</summary>
    public string? TaskUrl { get; init; }
    public string? TaskName { get; init; }

    /// <summary>PT 做种目标：分享率 / 小时数，null = 未指定；供模板 {seed-ratio} {seed-time} 使用。</summary>
    public double? SeedRatio { get; init; }
    public int? SeedHours { get; init; }

    public LogSink? Log { get; init; }
}

/// <summary>
/// Moves the bytes for one <see cref="TransferPlan"/>: byte-range segmenting
/// with a shared work queue, direct-at-offset writes, per-connection speed
/// readouts, mirror failover, a token-bucket limiter and periodic checkpoints.
/// The session is single-use: construct, <see cref="RunAsync"/>, discard.
/// </summary>
public sealed partial class TransferSession
{
    private const int ChunkSize = 64 * 1024;
    private const long MaxSegmentBuffer = 256 * 1024 * 1024;

    /// <summary>
    /// Serializes checkpoint writes against the completion path: once the
    /// transfer settles, <see cref="_checkpointClosed"/> flips under this gate
    /// so an in-flight 2s timer callback can never resurrect a stale
    /// checkpoint after the kernel has deleted it.
    /// </summary>
    private readonly object _checkpointGate = new();
    private bool _checkpointClosed;

    /// <summary>当前 sink 是否内存缓冲（小文件）：决定断点写入与暂停日志是否有意义。</summary>
    private bool _memorySink;

    /// <summary>
    /// Serializes worker "write + account" steps against checkpoint flushes:
    /// a checkpoint must snapshot <c>Done</c> only after the sink has flushed
    /// everything those bytes were accounted for, or a resumed transfer would
    /// leave holes for bytes still sitting in <see cref="DirectSink"/>'s
    /// staging buffer.
    /// </summary>
    private readonly object _accountGate = new();

    private const int ModeIdle = 0;
    private const int ModeRanges = 1;
    private const int ModeFiles = 2;

    private readonly TransferRequest _request;
    private readonly object _speedGate = new();

    private long _done;
    private long _total = -1;
    private int _mode = ModeIdle;
    private int _liveWorkers;
    private int _nextPiece;
    private int _nextSegment;

    // ranges mode
    private RangeSegment[] _pieces = [];

    // segment-files mode
    private string[] _files = [];
    private long _writeOffset;
    private int _committedCount;
    private int _bufferedCount;
    private string? _fingerprint;

    // per-connection speed slots, written by workers, read by the kernel's tick
    private double[] _speeds = [];
    private long[] _windowBytes = [];
    private long[] _windowStart = [];

    public TransferSession(TransferRequest request) => _request = request;

    /// <summary>Bytes received so far, across every connection.</summary>
    public long DoneBytes => Interlocked.Read(ref _done);

    /// <summary>Resource size, or -1 when unknown (may fill in once a response arrives).</summary>
    public long TotalBytes => Interlocked.Read(ref _total);

    /// <summary>Live connection count, as the inspector shows it.</summary>
    public int LiveConnections => Interlocked.CompareExchange(ref _liveWorkers, 0, 0);

    /// <summary>Speeds of connections with traffic, bytes/s — one entry per live connection.</summary>
    public double[] ActiveSpeeds()
    {
        lock (_speedGate)
        {
            return _speeds.Where(s => s > 0).ToArray();
        }
    }

    public async Task<TransferOutcome> RunAsync(CancellationToken cancellation)
    {
        try
        {
            return _request.Plan switch
            {
                TransferPlan.ByteRanges ranges => await RunRangesAsync(ranges, cancellation),
                TransferPlan.FtpFile ftp => await RunFtpAsync(ftp, cancellation),
                TransferPlan.SegmentFiles files => await RunSegmentFilesAsync(files, cancellation),
                TransferPlan.ExternalEngine engine when _request.EngineRunner is not null ||
                    !string.IsNullOrWhiteSpace(_request.ExternalEngineCommand)
                        => await RunExternalEngineAsync(engine, cancellation),
                TransferPlan.ExternalEngine engine => new TransferOutcome(TransferOutcomeKind.Failed,
                    $"需要外部引擎（{engine.Engine}）—— 引擎插槽未接入：{engine.Detail}"),
                TransferPlan.Unsupported unsupported => new TransferOutcome(TransferOutcomeKind.Failed, unsupported.Reason),
                _ => new TransferOutcome(TransferOutcomeKind.Failed, "未知传输计划"),
            };
        }
        catch (OperationCanceledException)
        {
            return new TransferOutcome(TransferOutcomeKind.Paused, null);
        }
    }

}
