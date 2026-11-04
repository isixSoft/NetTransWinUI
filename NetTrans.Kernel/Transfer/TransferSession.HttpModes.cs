using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using NetTrans.Kernel.Net;
using NetTrans.Kernel.Protocol;

namespace NetTrans.Kernel.Transfer;

/// <summary>
/// Network protocol modes: byte-range HTTP (multi-segment + mirrors),
/// segment-files (HLS / DASH) and plain FTP.
/// </summary>
public sealed partial class TransferSession
{
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

}
