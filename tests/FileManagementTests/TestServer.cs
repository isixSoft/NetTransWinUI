using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FileManagementTests;

/// <summary>
/// Minimal HTTP file server for kernel tests: serves one deterministic payload
/// (same bytes under every path) with ETag + byte-range support so the kernel's
/// probe, multi-segment transfer and resume all see a well-behaved origin.
/// Hand-rolled over TcpListener — zero package dependencies.
/// </summary>
public sealed class TestServer : IDisposable
{
    /// <summary>Deterministic payload (LCG), stable across runs and instances.</summary>
    public static byte[] Bytes { get; } = CreatePayload(8 * 1024 * 1024);

    public const string ETag = "\"nettrans-test-payload\"";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = new();

    public string BaseUrl { get; }

    public TestServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{port}";
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (_stop.IsCancellationRequested)
            {
                return;
            }

            var connection = client;
            lock (_connections) _connections.Add(Task.Run(() => ServeAsync(connection, _stop.Token)));
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        using var _ = client;
        await using var stream = client.GetStream();

        while (!stop.IsCancellationRequested)
        {
            string? request = await ReadRequestAsync(stream, stop);
            if (request is null) return; // client closed or malformed

            var lines = request.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return;
            string method = parts[0];
            bool ranged = lines.Any(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));

            if (method == "HEAD")
            {
                await WriteAsync(stream,
                    "HTTP/1.1 200 OK\r\n" +
                    $"Content-Length: {Bytes.Length}\r\n" +
                    $"ETag: {ETag}\r\n" +
                    "Accept-Ranges: bytes\r\n" +
                    "Content-Type: application/octet-stream\r\n\r\n", stop);
                continue;
            }

            if (method != "GET") return;

            if (ranged && TryParseRange(request, out long from, out long to))
            {
                to = Math.Min(to, Bytes.Length - 1);
                int length = (int)(to - from + 1);
                await WriteAsync(stream,
                    "HTTP/1.1 206 Partial Content\r\n" +
                    $"Content-Length: {length}\r\n" +
                    $"Content-Range: bytes {from}-{to}/{Bytes.Length}\r\n" +
                    $"ETag: {ETag}\r\n" +
                    "Accept-Ranges: bytes\r\n" +
                    "Content-Type: application/octet-stream\r\n\r\n", stop);
                await WriteBodyAsync(stream, from, length, stop);
            }
            else
            {
                await WriteAsync(stream,
                    "HTTP/1.1 200 OK\r\n" +
                    $"Content-Length: {Bytes.Length}\r\n" +
                    $"ETag: {ETag}\r\n" +
                    "Accept-Ranges: bytes\r\n" +
                    "Content-Type: application/octet-stream\r\n\r\n", stop);
                await WriteBodyAsync(stream, 0, Bytes.Length, stop);
            }
        }
    }

    private static async Task WriteAsync(NetworkStream stream, string text, CancellationToken stop) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), stop);

    private static async Task WriteBodyAsync(NetworkStream stream, long from, int length, CancellationToken stop)
    {
        var chunk = new byte[64 * 1024];
        long position = from;
        while (length > 0)
        {
            int n = Math.Min(chunk.Length, length);
            Array.Copy(Bytes, position, chunk, 0, n);
            await stream.WriteAsync(chunk.AsMemory(0, n), stop);
            position += n;
            length -= n;
        }
    }

    /// <summary>Reads one request's head; null when the client went away.</summary>
    private static async Task<string?> ReadRequestAsync(NetworkStream stream, CancellationToken stop)
    {
        var buffer = new byte[16 * 1024];
        int total = 0;
        while (total < buffer.Length)
        {
            if (total >= 4 && buffer[total - 4] == (byte)'\r' && buffer[total - 3] == (byte)'\n' &&
                buffer[total - 2] == (byte)'\r' && buffer[total - 1] == (byte)'\n')
            {
                break;
            }

            int n = await stream.ReadAsync(buffer.AsMemory(total), stop);
            if (n == 0) return total > 0 ? Encoding.ASCII.GetString(buffer, 0, total) : null;
            total += n;
        }

        return Encoding.ASCII.GetString(buffer, 0, total);
    }

    /// <summary>"bytes=123-" and "bytes=123-456" both land here; anything else is treated as a full GET.</summary>
    private static bool TryParseRange(string request, out long from, out long to)
    {
        from = to = 0;
        var line = request.Split("\r\n")
            .FirstOrDefault(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
        if (line is null) return false;

        string value = line["Range:".Length..].Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;

        var span = value["bytes=".Length..].Split('-');
        if (span.Length < 1 || !long.TryParse(span[0], out from)) return false;
        to = span.Length > 1 && long.TryParse(span[1], out long parsed) ? parsed : Bytes.Length - 1;
        return from >= 0 && from < Bytes.Length && to >= from;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { /* already down */ }
        lock (_connections)
        {
            foreach (var connection in _connections)
            {
                try { connection.Wait(TimeSpan.FromSeconds(2)); } catch { /* teardown noise */ }
            }
        }
        _stop.Dispose();
    }

    private static byte[] CreatePayload(int length)
    {
        var bytes = new byte[length];
        uint state = 0x9E3779B9;
        for (int i = 0; i < bytes.Length; i++)
        {
            state = state * 1664525u + 1013904223u;
            bytes[i] = (byte)(state >> 24);
        }
        return bytes;
    }
}
