namespace NetTrans.Kernel.Transfer;

/// <summary>
/// Where transfer bytes land. Two strategies behind one interface, chosen per
/// task by <see cref="TransferSession"/>: small known-size files buffer fully
/// in memory and materialize once at completion (<see cref="MemorySink"/>),
/// everything else writes through the preallocated file with per-segment
/// aggregation (<see cref="DirectSink"/>). The transfer hot paths only ever
/// see this interface.
/// </summary>
public interface IFileSink : IDisposable
{
    /// <summary>Place <paramref name="count"/> bytes of <paramref name="buffer"/> at <paramref name="offset"/> of the target file.</summary>
    void Write(long offset, byte[] buffer, int count);

    /// <summary>Push buffered bytes to their durable home (no-op while everything still lives in memory).</summary>
    void Flush();

    /// <summary>
    /// The transfer finished: finalize the file exactly once. Memory sinks
    /// materialize here — before this call the file does not exist on disk.
    /// </summary>
    void Complete();
}

/// <summary>
/// Small-file strategy: the whole body accumulates in memory while workers
/// stream in — out-of-order piece writes land by offset in one dense buffer —
/// and the file appears on disk only at <see cref="Complete"/>. No
/// preallocation, no half-written intermediate state, and no checkpoint
/// (there is no partial file to resume from).
/// </summary>
public sealed class MemorySink : IFileSink
{
    private readonly string _path;
    private byte[] _buffer;
    private long _highWater;

    public MemorySink(string path, long expectedSize)
    {
        _path = path;
        _buffer = new byte[Math.Max(0, expectedSize)];
    }

    public void Write(long offset, byte[] buffer, int count)
    {
        if (count <= 0) return;
        var end = offset + count;
        if (end > int.MaxValue) throw new IOException("小文件内存缓冲超出限制（阈值设置过大）");

        lock (_buffer)
        {
            if (end > _buffer.Length) Array.Resize(ref _buffer, (int)Math.Min(end + 1024 * 1024, int.MaxValue - 1));
            Buffer.BlockCopy(buffer, 0, _buffer, (int)offset, count);
            if (end > _highWater) _highWater = end;
        }
    }

    public void Flush()
    {
        // 内存即真相：没有盘上的中间态可冲
    }

    public void Complete()
    {
        lock (_buffer)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.Write(_buffer, 0, (int)_highWater);
        }
    }

    public void Dispose()
    {
        // 暂停/失败：放弃缓冲，盘上不留任何东西
        _buffer = [];
    }
}

/// <summary>
/// Large-file strategy: the preallocated <see cref="ChunkedFileWriter"/> plus
/// write coalescing. Workers hand over 64 KB chunks of one piece; chunks
/// adjacent to the staging buffer accumulate and hit the disk in ≤512 KB
/// seeks instead of one write per chunk. An offset that breaks continuity
/// flushes the staging buffer first, and runs larger than the staging buffer
/// bypass it entirely — out-of-order piece writes stay byte-exact.
/// </summary>
public sealed class DirectSink : IFileSink
{
    private const int StageThreshold = 512 * 1024;

    private readonly ChunkedFileWriter _writer;
    private readonly byte[] _staging = new byte[StageThreshold];
    private readonly object _gate = new();
    private long _stagingOffset;
    private int _stagingLength;

    public DirectSink(string path, long? preallocate) => _writer = new ChunkedFileWriter(path, preallocate);

    public void Write(long offset, byte[] buffer, int count)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            int at = 0;
            while (at < count)
            {
                bool contiguous = _stagingLength > 0 && offset + at == _stagingOffset + _stagingLength;
                if (!contiguous)
                {
                    FlushStaging();
                    // 超过 staging 容量的整块跳过暂存直接落盘，省一次内存拷贝
                    if (count - at >= _staging.Length)
                    {
                        _writer.Write(offset + at, buffer, at, count - at);
                        break;
                    }
                    _stagingOffset = offset + at;
                }

                int take = Math.Min(count - at, _staging.Length - _stagingLength);
                Buffer.BlockCopy(buffer, at, _staging, _stagingLength, take);
                _stagingLength += take;
                at += take;
                if (_stagingLength == _staging.Length) FlushStaging();
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            FlushStaging();
            _writer.Flush();
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            FlushStaging();
            _writer.Flush();
        }
    }

    public void Dispose() => _writer.Dispose();

    private void FlushStaging()
    {
        if (_stagingLength <= 0)
        {
            _stagingLength = 0;
            return;
        }
        _writer.Write(_stagingOffset, _staging, 0, _stagingLength);
        _stagingLength = 0;
    }
}
