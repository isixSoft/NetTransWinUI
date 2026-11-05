using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetworkTests;

/// <summary>
/// 最小 HTTP 测试服务器（HttpListener）：路由表 + 可注入故障的字节载荷端点。
/// 内建 HEAD / Range(206) 支持，覆盖探测与多分段下载的全部需要。
/// </summary>
public sealed class TestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, Func<HttpListenerContext, Task>> _routes = new(StringComparer.Ordinal);

    public int Port { get; }
    public string Base => $"http://127.0.0.1:{Port}/";

    public TestServer()
    {
        // 先抢一个空闲端口再交给 HttpListener（它不支持 0 号端口）
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public void Map(string path, Action<HttpListenerContext> handler) =>
        Map(path, ctx => { handler(ctx); return Task.CompletedTask; });

    public void Map(string path, Func<HttpListenerContext, Task> handler) =>
        _routes[path.TrimStart('/')] = handler;

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                if (!_listener.IsListening) return;
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_routes.TryGetValue(RequestPath(ctx.Request), out var handler))
                        await handler(ctx);
                    else
                        Reply(ctx.Response, 404, Array.Empty<byte>());
                }
                catch (HttpListenerException)
                {
                    // 客户端中途断开（镜像失效场景）正是被测行为
                }
                catch (Exception error)
                {
                    try { Reply(ctx.Response, 500, Encoding.UTF8.GetBytes(error.Message)); }
                    catch { /* 响应已不可写 */ }
                }
            });
        }
    }

    /// <summary>按原始 URL（未解码、去查询串）取路由键，保证 %20 等编码路径可匹配。</summary>
    public static string RequestPath(HttpListenerRequest request)
    {
        var raw = request.RawUrl ?? "/";
        int cut = raw.IndexOf('?');
        if (cut >= 0) raw = raw[..cut];
        return raw.TrimStart('/');
    }

    public static void Reply(HttpListenerResponse response, int status, byte[] body, string? contentType = null)
    {
        response.StatusCode = status;
        if (contentType is not null) response.ContentType = contentType;
        response.ContentLength64 = body.Length;
        if (body.Length > 0) response.OutputStream.Write(body);
        response.Close();
    }

    // ── 文件型端点的公共构件 ─────────────────────────────────────────────

    public static void SetCommonHeaders(HttpListenerResponse response, string? etag, string? disposition, string? contentType)
    {
        if (etag is not null) response.Headers["ETag"] = etag;
        if (disposition is not null) response.Headers["Content-Disposition"] = disposition;
        if (contentType is not null) response.ContentType = contentType;
        response.Headers["Accept-Ranges"] = "bytes";
    }

    public static void ServeHead(HttpListenerContext ctx, byte[] data, string? etag = null, string? disposition = null, string? contentType = null)
    {
        SetCommonHeaders(ctx.Response, etag, disposition, contentType);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength64 = data.Length;
        ctx.Response.Close();
    }

    /// <summary>按 Range 头回 206 分片，否则回 200 全量。</summary>
    public static void ServeRange(HttpListenerContext ctx, byte[] data, string? etag = null, string? disposition = null, string? contentType = null)
    {
        var response = ctx.Response;
        SetCommonHeaders(response, etag, disposition, contentType);

        long start = 0, end = data.Length - 1;
        bool ranged = false;
        var range = ctx.Request.Headers["Range"];
        if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal) && data.Length > 0)
        {
            var spec = range["bytes=".Length..];
            int dash = spec.IndexOf('-');
            if (dash >= 0 &&
                long.TryParse(spec[..dash].Trim(), out var parsedStart) &&
                parsedStart >= 0 && parsedStart < data.Length)
            {
                start = parsedStart;
                var right = spec[(dash + 1)..].Trim();
                end = right.Length > 0 && long.TryParse(right, out var parsedEnd)
                    ? Math.Min(parsedEnd, data.Length - 1)
                    : data.Length - 1;
                ranged = start <= end;
            }
        }

        if (ranged)
        {
            response.StatusCode = 206;
            response.Headers["Content-Range"] = $"bytes {start}-{end}/{data.Length}";
            response.ContentLength64 = end - start + 1;
            response.OutputStream.Write(data, (int)start, (int)(end - start + 1));
        }
        else
        {
            response.StatusCode = 200;
            response.ContentLength64 = data.Length;
            response.OutputStream.Write(data);
        }
        response.Close();
    }

    /// <summary>
    /// HEAD + Range 全支持的静态文件端点。onGet 收到本路由 1 起数的 GET 计数，
    /// 返回 true 表示故障注入已自行处理该请求。
    /// </summary>
    public Action<HttpListenerContext> RangeFile(
        byte[] data,
        string? etag = null,
        string? disposition = null,
        string? contentType = null,
        Func<int, HttpListenerContext, bool>? onGet = null)
    {
        int gets = 0;
        return ctx =>
        {
            if (ctx.Request.HttpMethod == "HEAD")
            {
                ServeHead(ctx, data, etag, disposition, contentType);
                return;
            }

            int n = Interlocked.Increment(ref gets);
            if (onGet is not null && onGet(n, ctx)) return;
            ServeRange(ctx, data, etag, disposition, contentType);
        };
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // 已停止
        }
    }
}

/// <summary>
/// 最小 RFC1928（NO-AUTH）SOCKS5 CONNECT 应答器：校验 greeting（05 01 00）、
/// 回 05 00，解析 CONNECT 的域名 + 端口，回成功应答（IPv4 绑定地址），
/// 然后由"远端"先推一段载荷、再回读客户端写入的字节，作为流可读写的判据。
/// </summary>
public sealed class Socks5TestServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _payloadFromRemote;

    public int Port { get; }
    public bool GreetingOk { get; private set; }
    public string? Host { get; private set; }
    public int? DestinationPort { get; private set; }
    public byte[]? FromClient { get; private set; }
    public Task HandshakeAndEcho => _done.Task;

    public Socks5TestServer(byte[] payloadFromRemote)
    {
        _payloadFromRemote = payloadFromRemote;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync();
            client.NoDelay = true;
            var stream = client.GetStream();

            var greeting = await ReadExactlyAsync(stream, 3);
            GreetingOk = greeting[0] == 0x05 && greeting[1] == 0x01 && greeting[2] == 0x00;
            await stream.WriteAsync((ReadOnlyMemory<byte>)new byte[] { 0x05, 0x00 });

            var head = await ReadExactlyAsync(stream, 4);
            if (head[0] != 0x05 || head[1] != 0x01 || head[3] != 0x03)
                throw new IOException($"CONNECT 请求异常：VER={head[0]} CMD={head[1]} ATYP={head[3]}");

            int length = (await ReadExactlyAsync(stream, 1))[0];
            Host = Encoding.UTF8.GetString(await ReadExactlyAsync(stream, length));
            var portBytes = await ReadExactlyAsync(stream, 2);
            DestinationPort = (portBytes[0] << 8) | portBytes[1];

            // 成功应答：VER REP RSV ATYP=IPv4 + 127.0.0.1 + 8080
            await stream.WriteAsync((ReadOnlyMemory<byte>)new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0x1F, 0x90 });

            await stream.WriteAsync(_payloadFromRemote);
            FromClient = await ReadExactlyAsync(stream, _payloadFromRemote.Length);
            _done.TrySetResult();
        }
        catch (Exception error)
        {
            _done.TrySetException(error);
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read));
            if (n == 0) throw new IOException("客户端提前关闭连接");
            read += n;
        }
        return buffer;
    }

    public void Dispose() => _listener.Stop();
}
