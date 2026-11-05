using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NetTrans.Kernel;

/// <summary>
/// 本地控制 API（P0-1）：最小 REST/JSON，直接跑在 TcpListener 上（自实现
/// 请求解析，避开 http.sys 的 URL ACL 预留问题，也无需管理员）。
/// 只信任环回来源：其他来源一律 403。首选端口被占时自动回退到相邻端口。
///
/// 路由（JSON 均为 camelCase）：
///   POST   /tasks             新建任务 → 201 {id}（400 = JSON 无效/缺 url）
///   GET    /tasks             任务列表 → 200 [task…]
///   GET    /tasks/{id}        单任务 → 200 / 404
///   POST   /tasks/{id}/pause  仅运行中可暂停，否则 409
///   POST   /tasks/{id}/resume 仅暂停中可恢复，否则 409
///   DELETE /tasks/{id}        移除 → 200 / 404
/// </summary>
public sealed class LocalControlApi : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IDownloadKernel _kernel;
    private readonly int _preferredPort;
    private readonly int _fallbackCount;
    private readonly ConcurrentDictionary<int, DownloadTask> _tasks = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<DownloadTask> _onAdded;
    private readonly Action<DownloadTask> _onUpdated;
    private readonly Action<DownloadTask> _onRemoved;
    private TcpListener? _listener;

    /// <summary>实际监听端口（Start 后有效）。</summary>
    public int Port { get; private set; }

    /// <summary>环回基址（Start 后有效），供调试与测试。</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    /// <summary>握手 token（nettrans:// 入口校验用；32 位十六进制，或构造时指定）。</summary>
    public string Token { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public LocalControlApi(IDownloadKernel kernel, int preferredPort, int fallbackCount = 10, string? token = null)
    {
        _kernel = kernel;
        _preferredPort = preferredPort;
        _fallbackCount = fallbackCount;
        if (token is not null) Token = token.ToLowerInvariant();
        _onAdded = t => _tasks[t.Id] = t;
        _onUpdated = t => _tasks[t.Id] = t;
        _onRemoved = t => _tasks.TryRemove(t.Id, out _);
        _kernel.TaskAdded += _onAdded;
        _kernel.TaskUpdated += _onUpdated;
        _kernel.TaskRemoved += _onRemoved;
    }

    /// <summary>来源是否可信（仅环回：127.0.0.0/8 与 ::1）。</summary>
    public static bool IsLoopbackSource(string remoteAddress) =>
        IPAddress.TryParse(remoteAddress, out var ip) && IPAddress.IsLoopback(ip);

    /// <summary>绑定并开始接受连接；端口被占时逐个尝试相邻端口。</summary>
    public void Start()
    {
        Exception? last = null;
        for (int attempt = 0; attempt <= _fallbackCount; attempt++)
        {
            int port = _preferredPort + attempt;
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
            }
            catch (Exception error)
            {
                last = error; // 端口被占 → 回退
                continue;
            }

            _listener = listener;
            Port = port;
            _ = AcceptLoopAsync(listener, _stop.Token);
            return;
        }

        throw new InvalidOperationException(
            $"端口 {_preferredPort}..{_preferredPort + _fallbackCount} 均不可用", last);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Stop();
        _kernel.TaskAdded -= _onAdded;
        _kernel.TaskUpdated -= _onUpdated;
        _kernel.TaskRemoved -= _onRemoved;
    }

    // ── 接受与解析 ─────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellation);
            }
            catch
            {
                break; // 监听已停止
            }
            _ = HandleClientAsync(client, cancellation);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellation)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                client.ReceiveTimeout = 5_000;
                var request = await ReadRequestAsync(stream, cancellation);
                if (request is null)
                {
                    await WriteAsync(stream, 400, "{\"error\":\"请求不完整\"}", cancellation);
                    return;
                }

                // 来源闸门先于一切路由：非环回一律 403
                var remote = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.ToString();
                if (!IsLoopbackSource(remote ?? string.Empty))
                {
                    await WriteAsync(stream, 403, "{\"error\":\"仅限本机访问\"}", cancellation);
                    return;
                }

                await RouteAsync(request, stream, cancellation);
            }
            catch
            {
                // 单连接的读写失败不影响服务
            }
        }
    }

    private sealed record ControlRequest(string Method, string Path, string Body);

    private static async Task<ControlRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellation)
    {
        var buffer = new byte[64 * 1024];
        int length = 0;
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            if (length >= buffer.Length) return null; // 头部超限
            int read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellation);
            if (read == 0) return null; // 连接先断了
            length += read;
            headerEnd = IndexOfHeaderEnd(buffer, length);
        }

        string head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        int contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(colon + 1)..].Trim(), out var parsed))
            {
                contentLength = parsed;
            }
        }
        if (contentLength is < 0 or > 4 * 1024 * 1024) return null; // 正文超限

        var body = new byte[contentLength];
        int bodyStart = headerEnd + 4;
        int filled = Math.Min(Math.Max(0, length - bodyStart), contentLength);
        if (filled > 0) Array.Copy(buffer, bodyStart, body, 0, filled);
        while (filled < contentLength)
        {
            int read = await stream.ReadAsync(body.AsMemory(filled, contentLength - filled), cancellation);
            if (read == 0) return null;
            filled += read;
        }

        return new ControlRequest(
            requestLine[0].ToUpperInvariant(),
            requestLine[1],
            Encoding.UTF8.GetString(body));
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n' &&
                buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n') return i;
        }
        return -1;
    }

    // ── 路由 ───────────────────────────────────────────────────────────────

    /// <summary>POST /links：nettrans://add 链接解析入队（P0-2）。token 不符一律 403。</summary>
    private async Task HandleNettransLinkAsync(string body, NetworkStream stream, CancellationToken cancellation)
    {
        string? link = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            link = document.RootElement.GetProperty("link").GetString();
        }
        catch
        {
            // JSON 坏/缺字段 → link 保持 null
        }

        if (string.IsNullOrWhiteSpace(link))
        {
            await WriteAsync(stream, 400, "{\"error\":\"缺少 link\"}", cancellation);
            return;
        }

        var parsed = NettransLink.Parse(link, out var linkError);
        if (parsed is null)
        {
            var message = JsonSerializer.Serialize(new { error = linkError ?? "链接无效" });
            await WriteAsync(stream, 400, message, cancellation);
            return;
        }

        if (!FixedTimeEquals(parsed.Token, Token))
        {
            await WriteAsync(stream, 403, "{\"error\":\"token 校验失败\"}", cancellation);
            return;
        }

        if (parsed.SaveTo is not { Length: > 0 })
        {
            await WriteAsync(stream, 400, "{\"error\":\"缺少 to 参数（保存目录）\"}", cancellation);
            return;
        }

        var task = _kernel.Add(new NewDownloadRequest(
            parsed.Url, parsed.SaveTo, "", 2, TaskPriority.Normal, StartNow: true,
            ExpectedHash: parsed.Sha256));
        await WriteAsync(stream, 201, JsonSerializer.Serialize(new { id = task.Id }, JsonOptions), cancellation);
    }

    /// <summary>token 常时比较，避免时序侧信道。</summary>
    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private async Task RouteAsync(ControlRequest request, NetworkStream stream, CancellationToken cancellation)
    {
        var segments = request.Path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (request.Method == "POST" && segments.Length == 1 && segments[0] == "tasks")
        {
            NewDownloadRequest? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<NewDownloadRequest>(request.Body, JsonOptions);
            }
            catch (JsonException)
            {
                parsed = null;
            }
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Url))
            {
                await WriteAsync(stream, 400, "{\"error\":\"缺少 url 或 JSON 无效\"}", cancellation);
                return;
            }

            var task = _kernel.Add(parsed);
            await WriteAsync(stream, 201, JsonSerializer.Serialize(new { id = task.Id }, JsonOptions), cancellation);
            return;
        }

        if (request.Method == "GET" && segments.Length == 1 && segments[0] == "tasks")
        {
            var snapshot = _tasks.Values.OrderBy(t => t.Id).ToArray();
            await WriteAsync(stream, 200, JsonSerializer.Serialize(snapshot, JsonOptions), cancellation);
            return;
        }

        if (request.Method == "POST" && segments.Length == 1 && segments[0] == "links")
        {
            await HandleNettransLinkAsync(request.Body, stream, cancellation);
            return;
        }

        if (segments.Length >= 2 && segments[0] == "tasks" && int.TryParse(segments[1], out var id))
        {
            if (segments.Length == 2 && request.Method == "GET")
            {
                if (_tasks.TryGetValue(id, out var task))
                    await WriteAsync(stream, 200, JsonSerializer.Serialize(task, JsonOptions), cancellation);
                else
                    await WriteAsync(stream, 404, "{\"error\":\"任务不存在\"}", cancellation);
                return;
            }

            if (segments.Length == 2 && request.Method == "DELETE")
            {
                if (!_tasks.ContainsKey(id))
                {
                    await WriteAsync(stream, 404, "{\"error\":\"任务不存在\"}", cancellation);
                    return;
                }
                _kernel.Remove(new[] { id });
                await WriteAsync(stream, 200, "{\"ok\":true}", cancellation);
                return;
            }

            if (segments.Length == 3 && request.Method == "POST")
            {
                if (!_tasks.TryGetValue(id, out var task))
                {
                    await WriteAsync(stream, 404, "{\"error\":\"任务不存在\"}", cancellation);
                    return;
                }

                if (segments[2] == "pause")
                {
                    if (task.Status is not (DownloadStatus.Queued or DownloadStatus.Downloading))
                    {
                        await WriteAsync(stream, 409, "{\"error\":\"任务不在运行中\"}", cancellation);
                        return;
                    }
                    _kernel.Toggle(id);
                    await WriteAsync(stream, 200, "{\"ok\":true}", cancellation);
                    return;
                }

                if (segments[2] == "resume")
                {
                    if (task.Status != DownloadStatus.Paused)
                    {
                        await WriteAsync(stream, 409, "{\"error\":\"任务不在暂停中\"}", cancellation);
                        return;
                    }
                    _kernel.Toggle(id);
                    await WriteAsync(stream, 200, "{\"ok\":true}", cancellation);
                    return;
                }
            }
        }

        await WriteAsync(stream, 404, "{\"error\":\"未知路由\"}", cancellation);
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string json, CancellationToken cancellation)
    {
        string reason = status switch
        {
            200 => "OK",
            201 => "Created",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            _ => "Error",
        };
        var body = Encoding.UTF8.GetBytes(json);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellation);
        await stream.WriteAsync(body, cancellation);
        await stream.FlushAsync(cancellation);
    }
}
