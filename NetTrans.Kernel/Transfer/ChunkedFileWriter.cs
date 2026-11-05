namespace NetTrans.Kernel.Transfer;

/// <summary>
/// One output file written from many connections: every write carries its own
/// offset and lands directly at that position — Ghost Downloader's
/// "direct-write at file offsets, no merge step" model. The file is
/// preallocated up front so segments never need shifting.
/// </summary>
public sealed class ChunkedFileWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly object _gate = new();

    public ChunkedFileWriter(string path, long? preallocate)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (preallocate is > 0) _stream.SetLength(preallocate.Value);
    }

    public void Write(long offset, byte[] buffer, int count) => Write(offset, buffer, 0, count);

    public void Write(long offset, byte[] buffer, int offsetInBuffer, int count)
    {
        lock (_gate)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.Write(buffer, offsetInBuffer, count);
        }
    }

    public void Flush()
    {
        lock (_gate) _stream.Flush();
    }

    public void Dispose() => _stream.Dispose();
}
