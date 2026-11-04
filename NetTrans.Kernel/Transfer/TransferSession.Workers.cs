using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using NetTrans.Kernel.Net;
using NetTrans.Kernel.Protocol;

namespace NetTrans.Kernel.Transfer;

/// <summary>
/// The shared worker harness underneath every network mode: mirror failover,
/// the range worker loop, checkpoint persistence, output sinks, throttling
/// and the speed/block readouts the kernel tick polls.
/// </summary>
public sealed partial class TransferSession
{
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

                    // 流式分段（未知大小）：服务器关流（ReadAsync 返回 0）即 EOF。
                    // 把 End 收紧为实际长度，IsComplete 随之成立——此前 MaxValue
                    // 分段永远过不了完成校验，无 ContentLength 的下载在重试耗尽
                    // 后必然失败，尽管字节早已完整收下。
                    if (piece.End == long.MaxValue)
                    {
                        piece.End = piece.Start + piece.Done;
                        break;
                    }
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
