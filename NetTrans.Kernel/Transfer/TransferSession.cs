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
public sealed class TransferSession
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

    // ── external-engine mode (BT / eD2k data plane) ───────────────────────
    /// <summary>
    /// The deliberate engine slot: moving bytes for BT / eD2k needs a
    /// host-side engine, injected either as a runner delegate or as a shell
    /// command template ({url} {path} {name} {seed-ratio} {seed-time}). Success means
    /// the engine produced the file at the save path — reported exactly like a finished
    /// byte transfer; with neither slot configured the plan still fails honestly.
    /// </summary>
    private async Task<TransferOutcome> RunExternalEngineAsync(TransferPlan.ExternalEngine plan, CancellationToken cancellation)
    {
        Log($"外部引擎接管：{plan.Engine}");

        // PT 做种目标：任务参数优先，种子/驱动默认值兜底
        var seededPlan = plan with
        {
            SeedRatio = _request.SeedRatio ?? plan.SeedRatio,
            SeedHours = _request.SeedHours ?? plan.SeedHours,
        };

        if (_request.EngineRunner is { } runner)
        {
            // 引擎产物目录必须存在：命令路径（见 RunExternalEngineCommandAsync）
            // 会建目录，runner 插槽保持同一约定，不把建目录的义务甩给引擎。
            var outputDir = Path.GetDirectoryName(_request.SavePath);
            if (outputDir is { Length: > 0 }) Directory.CreateDirectory(outputDir);
            try
            {
                bool produced = await runner(seededPlan, _request.SavePath, cancellation);
                if (produced && File.Exists(_request.SavePath)) return EngineFileOutcome();
                Log(produced ? "外部引擎报告完成，但未产出文件" : "外部引擎运行失败", isError: true);
                return new TransferOutcome(TransferOutcomeKind.Failed,
                    produced ? "外部引擎未产出文件" : "外部引擎运行失败");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                Log($"外部引擎运行异常（{Brief(error.Message)}）", isError: true);
                return new TransferOutcome(TransferOutcomeKind.Failed, Brief(error.Message));
            }
        }

        return await RunExternalEngineCommandAsync(seededPlan, cancellation);
    }

    private async Task<TransferOutcome> RunExternalEngineCommandAsync(
        TransferPlan.ExternalEngine plan, CancellationToken cancellation)
    {
        // PT 做种占位符：{seed-ratio} 分享率、{seed-time} 小时数；
        // 未设置时替换为 0（aria2 语义：不限做种），模板自行决定是否携带。
        string command = (_request.ExternalEngineCommand ?? string.Empty)
            .Replace("{url}", _request.TaskUrl ?? string.Empty)
            .Replace("{path}", _request.SavePath)
            .Replace("{name}", _request.TaskName ?? string.Empty)
            .Replace("{seed-ratio}", (plan.SeedRatio ?? 0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{seed-time}", (plan.SeedHours ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        // PT 底线：magnet/tracker 里的 passkey 不进日志
        Log($"执行外部命令：{SensitiveUrls.Mask(command)}");

        // 引擎输出目录必须先存在（HTTP/FTP 驱动自写文件时会建目录，
        // 引擎接管时内核是唯一能保证目录就位的一方）
        var outputDir = Path.GetDirectoryName(_request.SavePath);
        if (outputDir is { Length: > 0 }) Directory.CreateDirectory(outputDir);

        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        start.ArgumentList.Add(command);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception error)
        {
            Log($"外部引擎启动失败（{Brief(error.Message)}）", isError: true);
            return new TransferOutcome(TransferOutcomeKind.Failed, Brief(error.Message));
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            // pause/remove must not leave the engine process behind
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { await Task.WhenAll(stdout, stderr); } catch { /* pipes are closing */ }
            throw;
        }

        string errorText = await stderr;
        _ = await stdout; // captured for completeness; only stderr feeds the failure log

        if (process.ExitCode == 0)
        {
            if (File.Exists(_request.SavePath)) return EngineFileOutcome();
            Log("外部引擎已退出，但未产出文件", isError: true);
            return new TransferOutcome(TransferOutcomeKind.Failed, "外部引擎未产出文件");
        }

        var briefError = Brief(FirstLine(errorText));
        Log(briefError.Length > 0
            ? $"外部引擎退出码 {process.ExitCode}：{briefError}"
            : $"外部引擎退出码 {process.ExitCode}", isError: true);
        return new TransferOutcome(TransferOutcomeKind.Failed, $"外部引擎退出码 {process.ExitCode}");
    }

    /// <summary>The engine produced the file: surface it like a finished byte transfer.</summary>
    private TransferOutcome EngineFileOutcome()
    {
        long length = new FileInfo(_request.SavePath).Length;
        Interlocked.Exchange(ref _total, length);
        Interlocked.Exchange(ref _done, length);
        Log($"外部引擎完成：{FormatSize(length)}");
        return new TransferOutcome(TransferOutcomeKind.Completed, null);
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].TrimEnd('\r').Trim();

    // ── byte-range mode (HTTP) ────────────────────────────────────────────
    private async Task<TransferOutcome> RunRangesAsync(TransferPlan.ByteRanges plan, CancellationToken cancellation)
    {
        _mode = ModeRanges;
        var table = PrepareRangeTable(plan);

        // a resumed table needs its backing file at the right length
        if (table.Restored && !File.Exists(_request.SavePath))
        {
            Log("本地文件缺失，断点失效，重新开始");
            table = FreshTable(plan);
        }
        else if (table.Restored && plan.Size > 0 && new FileInfo(_request.SavePath).Length != plan.Size)
        {
            Log("本地文件与断点不符，重新开始");
            table = FreshTable(plan);
        }

        _pieces = table.Segments;
        Interlocked.Exchange(ref _total, plan.Size);
        var sources = new MirrorSourceSet(plan.Url, plan.Mirrors);

        // 断点恢复时半截数据已在盘上，必须直写续传；全新任务且大小已知且
        // 不超过小文件阈值时走内存缓冲，完成时一次性落盘
        using var sink = table.Restored
            ? new DirectSink(_request.SavePath, plan.Size > 0 ? plan.Size : null)
            : CreateSink(plan.Size > 0 ? plan.Size : null);
        _memorySink = sink is MemorySink;
        if (plan.Size <= 0) Log("服务器未提供文件大小，无法断点续传");
        if (plan.Size > 0 && !plan.AcceptRanges) Log("服务器不支持分段下载，使用单连接");
        if (plan.Mirrors.Count > 0) Log($"已启用 {plan.Mirrors.Count} 个镜像源");

        using var checkpointTimer = new Timer(
            _ => SaveRangeCheckpoint(table, sink), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        return await RunWorkersAsync(
            sink, cancellation,
            onPaused: () => SaveRangeCheckpoint(table, sink),
            body: (slot, token) => RangeWorkerAsync(slot, sink, sources, token, plan.AcceptRanges));
    }

    /// <summary>Restore the checkpoint when it matches the fresh probe; split fresh otherwise.</summary>
    private SegmentTable PrepareRangeTable(TransferPlan.ByteRanges plan)
    {
        var checkpoint = _request.Checkpoint;
        if (checkpoint is not { Kind: "ranges" }) return FreshTable(plan);

        bool sizeMatch = plan.Size > 0 && checkpoint.Size == plan.Size;
        bool sizeUnknown = plan.Size <= 0 && checkpoint.Size <= 0;
        bool etagMatch = checkpoint.ETag is null || _request.ETag is null || checkpoint.ETag == _request.ETag;

        if ((!sizeMatch && !sizeUnknown) || !etagMatch)
        {
            Log("资源已变化，断点失效，重新开始");
            return FreshTable(plan);
        }

        var restored = SegmentTable.Restore(checkpoint);
        if (restored is null) return FreshTable(plan);

        restored.Restored = true;
        Log($"已从断点恢复，剩余 {FormatSize(restored.Segments.Sum(s => s.Remaining))}");
        return restored;
    }

    private SegmentTable FreshTable(TransferPlan.ByteRanges plan) =>
        SegmentTable.Split(plan.Url, plan.Size, _request.ETag,
            plan.AcceptRanges && plan.Size > 0 ? _request.Connections : 1);

    // ── segment-files mode (HLS / DASH) ───────────────────────────────────
    private async Task<TransferOutcome> RunSegmentFilesAsync(TransferPlan.SegmentFiles plan, CancellationToken cancellation)
    {
        _mode = ModeFiles;
        _files = plan.Segments.ToArray();
        _fingerprint = Fingerprint(_files);
        Interlocked.Exchange(ref _total, plan.TotalSize ?? -1);

        var committed = new List<long>();
        bool restored = false;
        if (_request.Checkpoint is { Kind: "segments" } checkpoint && checkpoint.Fingerprint == _fingerprint)
        {
            committed.AddRange(checkpoint.Committed);
            restored = committed.Count > 0;
            if (restored) Log($"已从断点恢复，已完成 {committed.Count}/{_files.Length} 个分段");
        }
        else if (_request.Checkpoint is { Kind: "segments" })
        {
            Log("播放列表已变化，断点失效，重新开始");
        }

        _committedCount = committed.Count;
        _writeOffset = committed.Sum();
        _nextSegment = committed.Count;

        // 已提交的分段数据已在盘上，恢复必须直写；全新任务按大小选 sink
        using var sink = restored
            ? new DirectSink(_request.SavePath, plan.TotalSize)
            : CreateSink(plan.TotalSize);
        _memorySink = sink is MemorySink;

        var buffers = new Dictionary<int, byte[]>();
        var buffersGate = new object();

        void SaveCheckpoint()
        {
            if (_request.CheckpointChanged is null || _memorySink) return; // 内存缓冲无中间态可续
            long[] snapshot;
            lock (buffersGate)
            {
                // 先把 staging 冲到盘上再快照 committed：恢复不得对暂存字节留空洞
                sink.Flush();
                snapshot = committed.ToArray();
            }
            if (snapshot.Length == 0) return;
            lock (_checkpointGate)
            {
                if (_checkpointClosed) return;
                if (plan.TotalSize is { } total && snapshot.Sum() >= total) return; // 完成在即：不写陈旧断点
                _request.CheckpointChanged.Invoke(new TransferCheckpoint
                {
                    Kind = "segments",
                    Url = _files.FirstOrDefault() ?? "",
                    ETag = _request.ETag,
                    Committed = snapshot,
                    Fingerprint = _fingerprint,
                });
            }
        }

        using var checkpointTimer = new Timer(
            _ => SaveCheckpoint(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        return await RunWorkersAsync(
            sink, cancellation,
            onPaused: SaveCheckpoint,
            body: (slot, token) => SegmentWorkerAsync(slot, token, sink, buffers, buffersGate, committed));
    }

    private async Task SegmentWorkerAsync(
        int slot, CancellationToken cancellation, IFileSink sink,
        Dictionary<int, byte[]> buffers, object buffersGate, List<long> committed)
    {
        while (!cancellation.IsCancellationRequested)
        {
            int index = Interlocked.Increment(ref _nextSegment) - 1;
            if (index >= _files.Length) return;

            var bytes = await FetchSegmentAsync(_files[index], slot, cancellation);
            Interlocked.Add(ref _done, bytes.LongLength);
            lock (buffersGate)
            {
                buffers[index] = bytes;
                Interlocked.Increment(ref _bufferedCount);
            }

            // commit as many in-order segments as are now ready
            lock (buffersGate)
            {
                while (buffers.TryGetValue(_committedCount, out var ready))
                {
                    buffers.Remove(_committedCount);
                    Interlocked.Decrement(ref _bufferedCount);
                    sink.Write(_writeOffset, ready, ready.Length);
                    _writeOffset += ready.Length;
                    committed.Add(ready.LongLength);
                    _committedCount++;
                }
            }
        }
    }

    private async Task<byte[]> FetchSegmentAsync(string url, int slot, CancellationToken cancellation)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ChunkSize];

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                buffer.SetLength(0);
                using var message = BuildMessage(HttpMethod.Get, url);
                using var response = await _request.Http.SendAsync(
                    message, HttpCompletionOption.ResponseHeadersRead, cancellation);
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync(cancellation);

                int n;
                while ((n = await stream.ReadAsync(chunk, cancellation)) > 0)
                {
                    if (buffer.Length + n > MaxSegmentBuffer) throw new IOException("HLS/DASH 分段超出内存缓冲限制");
                    await ThrottleAsync(n, cancellation);
                    buffer.Write(chunk, 0, n);
                    SpeedTick(slot, n);
                }

                return buffer.ToArray();
            }
            catch (Exception error) when (error is not OperationCanceledException && attempt < _request.RetryCount)
            {
                Log($"分段拉取失败，重试（{Brief(error.Message)}）");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 + attempt, 5)), cancellation);
            }
        }
    }

    // ── FTP mode ──────────────────────────────────────────────────────────
    private async Task<TransferOutcome> RunFtpAsync(TransferPlan.FtpFile plan, CancellationToken cancellation)
    {
        _mode = ModeRanges;
        Interlocked.Exchange(ref _total, plan.Size);
        _pieces = SegmentTable.Split(plan.Url, plan.Size, null, 1).Segments;
        InitSpeedSlots(1);

        using var sink = CreateSink(plan.Size > 0 ? plan.Size : null);
        _memorySink = sink is MemorySink;
        Log(plan.Url.StartsWith("ftps", StringComparison.OrdinalIgnoreCase)
            ? "FTPS · 单连接流式传输（FTP REST 跨服务器不可靠，未启用断点续传）"
            : "FTP · 单连接流式传输（FTP REST 跨服务器不可靠，未启用断点续传）");

        for (int attempt = 0; ; attempt++)
        {
            long written = 0;
            try
            {
#pragma warning disable SYSLIB0014
                var request = (FtpWebRequest)WebRequest.Create(FtpRequestUri(plan.Url));
                request.Method = WebRequestMethods.Ftp.DownloadFile;
                request.UseBinary = true;
                request.UsePassive = true;
                request.EnableSsl = plan.Url.StartsWith("ftps", StringComparison.OrdinalIgnoreCase);
                request.Timeout = 15000;
                using var response = (FtpWebResponse)request.GetResponse();
#pragma warning restore SYSLIB0014
                using var source = response.GetResponseStream()!;
                if (response.ContentLength > 0 && Interlocked.Read(ref _total) < 0)
                    Interlocked.Exchange(ref _total, response.ContentLength);

                var buffer = new byte[ChunkSize];
                int n;
                while ((n = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    await ThrottleAsync(n, cancellation);
                    sink.Write(written, buffer, n);
                    written += n;
                    Interlocked.Exchange(ref _done, written);
                    SpeedTick(0, n);
                }

                if (Interlocked.Read(ref _total) > 0 && written < Interlocked.Read(ref _total))
                    throw new IOException("FTP 连接在完成前中断");

                sink.Complete();
                return new TransferOutcome(TransferOutcomeKind.Completed, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (attempt < _request.RetryCount)
            {
                Log($"FTP 传输失败，重试（{Brief(error.Message)}）");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 + attempt, 5)), cancellation);
            }
            catch (Exception error)
            {
                return new TransferOutcome(TransferOutcomeKind.Failed, error.Message);
            }
        }
    }

    private static Uri FtpRequestUri(string url)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "ftps") return uri;

        // FtpWebRequest registers only the ftp:// scheme; ftps rides on it with EnableSsl
        var builder = new UriBuilder(uri) { Scheme = "ftp" };
        return builder.Uri;
    }

    // ── the shared worker harness ─────────────────────────────────────────
    private async Task<TransferOutcome> RunWorkersAsync(
        IFileSink sink,
        CancellationToken cancellation,
        Action? onPaused,
        Func<int, CancellationToken, Task> body)
    {
        int workers = _mode == ModeFiles
            ? Math.Min(_request.Connections, Math.Max(1, _files.Length - _committedCount))
            : Math.Min(_request.Connections, _pieces.Length);
        workers = Math.Max(1, workers);
        InitSpeedSlots(workers);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var tasks = Enumerable.Range(0, workers)
            .Select(slot => Task.Run(
                async () =>
                {
                    Interlocked.Increment(ref _liveWorkers);
                    try
                    {
                        await body(slot, linked.Token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _liveWorkers);
                        SetSpeed(slot, 0);
                    }
                },
                CancellationToken.None))
            .ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception error)
        {
            if (cancellation.IsCancellationRequested)
            {
                onPaused?.Invoke();
                Log(_memorySink ? "已暂停（小文件内存缓冲，进度不落盘）" : "已暂停，断点已保存");
                return new TransferOutcome(TransferOutcomeKind.Paused, null);
            }

            linked.Cancel();
            try { await Task.WhenAll(tasks); } catch { /* secondary failures are noise */ }
            onPaused?.Invoke();

            var failure = error is AggregateException aggregate
                ? aggregate.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) ?? error
                : error;
            return new TransferOutcome(TransferOutcomeKind.Failed, Brief(failure.Message));
        }
        finally
        {
            // 传输已定型（完成/暂停/失败）：关闭在途 checkpoint 写入，避免
            // 2s 定时器回调在内核 DeleteCheckpoint 之后又落一份陈旧断点
            lock (_checkpointGate) _checkpointClosed = true;
        }

        if (_mode == ModeRanges && _pieces.Any(p => !p.IsComplete))
            return new TransferOutcome(TransferOutcomeKind.Failed, "传输提前结束");

        if (_mode == ModeFiles && _committedCount < _files.Length)
            return new TransferOutcome(TransferOutcomeKind.Failed, "分段传输提前结束");

        sink.Complete();
        return new TransferOutcome(TransferOutcomeKind.Completed, null);
    }

    private async Task RangeWorkerAsync(int slot, IFileSink sink, MirrorSourceSet? sources, CancellationToken cancellation, bool acceptRanges)
    {
        var pieces = _pieces;
        var buffer = new byte[ChunkSize];

        while (!cancellation.IsCancellationRequested)
        {
            int index = Interlocked.Increment(ref _nextPiece) - 1;
            if (index >= pieces.Length) return;

            var piece = pieces[index];
            bool lenient = pieces.Length == 1; // single stream: a Range-ignoring server just restarts us at 0

            for (int attempt = 0; ; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (piece.IsComplete) break;
                var source = sources?.Next() ?? "";

                try
                {
                    using var message = BuildMessage(HttpMethod.Get, source);
                    if (piece.End != long.MaxValue)
                    {
                        // 有界分段始终请求自己的区间（探测时确认支持 Range，或
                        // 已有进度要续传），否则首片会被 200 全量正文撑到 2x 带宽
                        if (acceptRanges || piece.Start + piece.Done > 0)
                            message.Headers.Range = new RangeHeaderValue(piece.Start + piece.Done, piece.End - 1);
                    }
                    else if (piece.Done > 0)
                    {
                        message.Headers.Range = RangeHeaderValue.Parse($"bytes={piece.Start + piece.Done}-");
                    }

                    using var response = await _request.Http.SendAsync(
                        message, HttpCompletionOption.ResponseHeadersRead, cancellation);

                    bool ignoredRange = (int)response.StatusCode == 200 && piece.Start + piece.Done > 0;
                    if (ignoredRange && !lenient) throw new IOException("服务器不支持断点续传（Range 被忽略）");
                    if (ignoredRange)
                    {
                        Log("服务器忽略了续传请求，从头开始");
                        Interlocked.Add(ref _done, -piece.Done);
                        piece.Done = 0;
                    }

                    response.EnsureSuccessStatusCode();
                    using var stream = await response.Content.ReadAsStreamAsync(cancellation);

                    if (piece.End == long.MaxValue && Interlocked.Read(ref _total) < 0 &&
                        response.Content.Headers.ContentLength is { } length)
                    {
                        Interlocked.Exchange(ref _total, length);
                    }

                    int n;
                    while ((n = await stream.ReadAsync(buffer, cancellation)) > 0)
                    {
                        // 只消费本分段还需要的字节：服务器无视 Range 回 200 全量
                        // 时，读满分段立刻断开，绝不吃下整个正文
                        int take = piece.End == long.MaxValue
                            ? n
                            : (int)Math.Min(n, piece.End - piece.Start - piece.Done);
                        if (take <= 0) break;
                        await ThrottleAsync(take, cancellation);
                        lock (_accountGate)
                        {
                            // 写入与记账必须原子：断点快照在同一个门内先 flush 再读 Done
                            sink.Write(piece.Start + piece.Done, buffer, take);
                            piece.Done += take;
                        }
                        Interlocked.Add(ref _done, take);
                        SpeedTick(slot, take);
                        if (piece.End != long.MaxValue && piece.Done >= piece.End - piece.Start) break;
                    }

                    if (piece.IsComplete) break;
                    throw new IOException("连接在完成前中断");
                }
                catch (Exception error) when (error is not OperationCanceledException && attempt < _request.RetryCount)
                {
                    sources?.ReportFailure(source);
                    Log($"连接中断，第 {attempt + 1} 次重试（{Brief(error.Message)}）");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 + attempt, 5)), cancellation);
                }
            }
        }
    }

    // ── checkpoints ───────────────────────────────────────────────────────
    private void SaveRangeCheckpoint(SegmentTable table, IFileSink sink)
    {
        if (_request.CheckpointChanged is null || table.Size <= 0) return;
        if (_memorySink) return; // 内存缓冲无半截文件可续，断点没有意义
        lock (_accountGate)
        {
            // 先把 staging 冲到盘上再快照 Done：保证 Done ≤ 磁盘已写字节，
            // 断点恢复才不会对仍在暂存里的字节留下空洞
            sink.Flush();
            lock (_checkpointGate)
            {
                if (_checkpointClosed) return;
                if (table.DoneBytes == 0 && !table.Restored) return;
                if (table.DoneBytes >= table.Size) return; // 完成在即：不写陈旧断点
                _request.CheckpointChanged.Invoke(table.ToCheckpoint());
            }
        }
    }

    // ── output sinks ──────────────────────────────────────────────────────
    /// <summary>
    /// Choose where bytes land: known-size bodies within the small-file
    /// threshold buffer fully in memory and materialize once at completion;
    /// everything else writes through the preallocated file with aggregation.
    /// </summary>
    private IFileSink CreateSink(long? knownSize)
    {
        if (_request.SmallFileThresholdBytes > 0 && knownSize is > 0 && knownSize <= _request.SmallFileThresholdBytes)
        {
            Log($"小文件（{FormatSize(knownSize.Value)}）走内存缓冲，完成时一次性落盘");
            return new MemorySink(_request.SavePath, knownSize.Value);
        }
        return new DirectSink(_request.SavePath, knownSize > 0 ? knownSize : null);
    }

    /// <summary>
    /// Paces one read: first the shared global bucket (all tasks combined stay
    /// under the global cap), then the per-task bucket.
    /// </summary>
    private async Task ThrottleAsync(int bytes, CancellationToken cancellation)
    {
        if (_request.GlobalLimiter is { } global) await global.WaitAsync(bytes, cancellation);
        await _request.Limiter.WaitAsync(bytes, cancellation);
    }

    /// <summary>Stable identity of a playlist's segment order, so a changed manifest invalidates resume state.</summary>
    private static string Fingerprint(string[] files)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", files));
        return Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }

    // ── blocks / speeds / helpers ─────────────────────────────────────────
    /// <summary>The 96-cell 分块 grid: 1 = done, 2 = in flight, 0 = pending.</summary>
    public int[] ComputeBlocks(int cells)
    {
        var blocks = new int[cells];

        if (_mode == ModeRanges)
        {
            long size = Interlocked.CompareExchange(ref _total, 0, 0);
            if (size <= 0)
            {
                long done = Interlocked.Read(ref _done);
                int filled = (int)Math.Min(cells - 1, done / (4 * 1024 * 1024));
                for (int i = 0; i < filled; i++) blocks[i] = 1;
                if (done > 0 && filled < cells) blocks[filled] = 2;
                return blocks;
            }

            foreach (var piece in _pieces)
            {
                if (piece.Done <= 0) continue;
                long doneEnd = Math.Min(piece.Start + piece.Done, piece.End);
                int first = (int)(piece.Start * cells / size);
                int last = (int)Math.Min(cells - 1, (doneEnd - 1) * cells / size);
                for (int c = first; c <= last; c++)
                {
                    long cellStart = (long)c * size / cells;
                    long cellEnd = (long)(c + 1) * size / cells;
                    blocks[c] = Math.Max(blocks[c], cellStart >= piece.Start && cellEnd <= doneEnd ? 1 : 2);
                }
            }
        }
        else if (_mode == ModeFiles && _files.Length > 0)
        {
            int fullCells = (int)((long)_committedCount * cells / _files.Length);
            for (int i = 0; i < fullCells; i++) blocks[i] = 1;
            if (_committedCount < _files.Length && _liveWorkers > 0)
                blocks[Math.Min(fullCells, cells - 1)] = 2;
        }

        return blocks;
    }

    private void InitSpeedSlots(int workers)
    {
        lock (_speedGate)
        {
            _speeds = new double[workers];
            _windowBytes = new long[workers];
            _windowStart = new long[workers];
            for (int i = 0; i < workers; i++) _windowStart[i] = Stopwatch.GetTimestamp();
        }
    }

    private void SpeedTick(int slot, int bytes)
    {
        var now = Stopwatch.GetTimestamp();
        _windowBytes[slot] += bytes;
        double elapsed = (now - _windowStart[slot]) / (double)Stopwatch.Frequency;
        if (elapsed >= 0.5)
        {
            SetSpeed(slot, _windowBytes[slot] / elapsed);
            _windowBytes[slot] = 0;
            _windowStart[slot] = now;
        }
    }

    private void SetSpeed(int slot, double value)
    {
        lock (_speedGate)
        {
            if (slot < _speeds.Length) _speeds[slot] = value;
        }
    }

    private HttpRequestMessage BuildMessage(HttpMethod method, string url)
    {
        var message = new HttpRequestMessage(method, url);
        foreach (var (name, value) in _request.Headers)
            message.Headers.TryAddWithoutValidation(name, value);
        return message;
    }

    private void Log(string message, bool isError = false) => _request.Log?.Invoke(message, isError);

    private static string Brief(string message) => message.Length <= 60 ? message : message[..57] + "…";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };
}
