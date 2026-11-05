using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EndToEndTests;

/// <summary>
/// 最小 HTTP 测试服务器（HttpListener）：路由表 + 可注入故障的字节载荷端点。
/// 内建 HEAD / Range(206) 支持，覆盖端到端下载的全部需要。
/// （与 NetworkTests 的同名服务器同源，按本工程命名空间独立一份。）
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
                    // 客户端中途断开是被测行为之一
                }
                catch (Exception error)
                {
                    try { Reply(ctx.Response, 500, Encoding.UTF8.GetBytes(error.Message)); }
                    catch { /* 响应已不可写 */ }
                }
            });
        }
    }

    /// <summary>按原始 URL（未解码、去查询串）取路由键。</summary>
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

    public static void ServeHead(HttpListenerContext ctx, byte[] data, string? etag = null, string? contentType = null)
    {
        if (etag is not null) ctx.Response.Headers["ETag"] = etag;
        if (contentType is not null) ctx.Response.ContentType = contentType;
        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength64 = data.Length;
        ctx.Response.Close();
    }

    /// <summary>按 Range 头回 206 分片，否则回 200 全量。</summary>
    public static void ServeRange(HttpListenerContext ctx, byte[] data, string? etag = null, string? contentType = null)
    {
        var response = ctx.Response;
        if (etag is not null) response.Headers["ETag"] = etag;
        if (contentType is not null) response.ContentType = contentType;
        response.Headers["Accept-Ranges"] = "bytes";

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
        string? contentType = null,
        Func<int, HttpListenerContext, bool>? onGet = null)
    {
        int gets = 0;
        return ctx =>
        {
            if (ctx.Request.HttpMethod == "HEAD")
            {
                ServeHead(ctx, data, etag, contentType);
                return;
            }

            int n = Interlocked.Increment(ref gets);
            if (onGet is not null && onGet(n, ctx)) return;
            ServeRange(ctx, data, etag, contentType);
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
