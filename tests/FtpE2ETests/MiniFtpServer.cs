using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FtpE2ETests;

/// <summary>
/// 最小 FTP 服务器（TcpListener 手写，端口 0 起步抢空闲端口）：只实现
/// RealDownloadKernel 的 FTP 路径用到的命令——USER/PASS/TYPE I/SIZE/PASV/
/// RETR/QUIT，回复格式与 .NET 内置 FtpWebRequest 兼容。数据端口同样用 0 号
/// 端口抢占，PASV 回复 h1,h2,h3,h4,p1,p2 格式；RETR 在数据连接上发出精确
/// 字节后关闭，再回 226。
/// </summary>
public sealed class MiniFtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly List<string> _commands = new();
    private readonly List<Socket> _sockets = new();
    private readonly object _gate = new();

    public int Port { get; }

    /// <summary>true 时 SIZE 一律回 550（模拟「大小未知」的 FTP 服务器）。</summary>
    public bool RejectSize { get; init; }

    public MiniFtpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public void AddFile(string path, byte[] data) => _files[NormalizePath(path)] = data;

    /// <summary>FtpWebRequest 发送的 SIZE/RETR 参数不带前导斜杠，键统一按此归一化。</summary>
    private static string NormalizePath(string path) => path.TrimStart('/');

    /// <summary>控制连接上是否收到过某条命令（大写动词）。</summary>
    public bool SawCommand(string verb)
    {
        lock (_gate) return _commands.Contains(verb);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptSocketAsync();
                lock (_gate) _sockets.Add(client);
                _ = Task.Run(() => HandleControlAsync(client));
            }
        }
        catch
        {
            // 服务器已停止（Dispose）
        }
    }

    private async Task HandleControlAsync(Socket control)
    {
        TcpListener? dataListener = null;
        Task<Socket>? dataAccept = null;

        try
        {
            await ReplyAsync(control, "220 NetTrans FTP test server ready");

            while (true)
            {
                string? line = await ReadLineAsync(control);
                if (line is null) return; // 客户端关闭了控制连接

                string command = line.Trim();
                int space = command.IndexOf(' ');
                string verb = (space < 0 ? command : command[..space]).ToUpperInvariant();
                string argument = space < 0 ? string.Empty : command[(space + 1)..].Trim();
                lock (_gate) _commands.Add(verb);

                switch (verb)
                {
                    case "USER":
                        await ReplyAsync(control, "331 Password required");
                        break;

                    case "PASS":
                        await ReplyAsync(control, "230 User logged in");
                        break;

                    case "TYPE":
                        await ReplyAsync(control, "200 Type set");
                        break;

                    case "OPTS":
                        // FtpWebRequest 登录后固定发「OPTS utf8 on」，回 200 即可
                        await ReplyAsync(control, "200 UTF8 mode enabled");
                        break;

                    case "PWD":
                        await ReplyAsync(control, "257 \"/\" is the current directory");
                        break;

                    case "SYST":
                        await ReplyAsync(control, "215 UNIX Type: L8");
                        break;

                    case "NOOP":
                        await ReplyAsync(control, "200 NOOP ok");
                        break;

                    case "SIZE":
                        if (RejectSize)
                        {
                            await ReplyAsync(control, "550 Could not get file size");
                        }
                        else if (_files.TryGetValue(NormalizePath(argument), out var sized))
                        {
                            await ReplyAsync(control, $"213 {sized.Length}");
                        }
                        else
                        {
                            await ReplyAsync(control, "550 File not found");
                        }
                        break;

                    case "PASV":
                        (dataListener, dataAccept) = OpenDataPort();
                        await ReplyAsync(control, $"227 Entering Passive Mode ({FormatPasv(dataListener)})");
                        break;

                    case "RETR":
                        if (!_files.TryGetValue(NormalizePath(argument), out var payload))
                        {
                            await ReplyAsync(control, "550 File not found");
                            break;
                        }
                        if (dataAccept is null) throw new IOException("RETR 之前没有 PASV");
                        await ReplyAsync(control, "150 Opening data connection");
                        Socket data = await dataAccept.WaitAsync(TimeSpan.FromSeconds(10));
                        await SendFileAsync(data, payload);
                        await ReplyAsync(control, "226 Transfer complete");
                        break;

                    case "QUIT":
                        await ReplyAsync(control, "221 Goodbye");
                        return;

                    default:
                        await ReplyAsync(control, "502 Command not implemented");
                        break;
                }
            }
        }
        catch
        {
            // 客户端中途断开或测试结束——结束本控制连接即可
        }
        finally
        {
            dataListener?.Stop();
        }
    }

    private static (TcpListener Listener, Task<Socket> Accept) OpenDataPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, listener.AcceptSocketAsync());
    }

    private static string FormatPasv(TcpListener listener)
    {
        var endpoint = (IPEndPoint)listener.LocalEndpoint; // 127.0.0.1:port
        byte[] ip = endpoint.Address.GetAddressBytes();
        int port = endpoint.Port;
        return $"{ip[0]},{ip[1]},{ip[2]},{ip[3]},{port / 256},{port % 256}";
    }

    private static async Task SendFileAsync(Socket data, byte[] payload)
    {
        try
        {
            for (int offset = 0; offset < payload.Length; )
            {
                int chunk = Math.Min(64 * 1024, payload.Length - offset);
                await data.SendAsync(payload.AsMemory(offset, chunk), SocketFlags.None);
                offset += chunk;
            }
        }
        finally
        {
            // 关闭数据连接 = 客户端读到 EOF，传输结束
            try { data.Shutdown(SocketShutdown.Both); } catch { /* 已断开 */ }
            data.Dispose();
        }
    }

    private static async Task ReplyAsync(Socket socket, string line)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await socket.SendAsync(bytes, SocketFlags.None);
    }

    private static async Task<string?> ReadLineAsync(Socket socket)
    {
        var buffer = new byte[1];
        var text = new StringBuilder();
        while (true)
        {
            int n = await socket.ReceiveAsync(buffer, SocketFlags.None);
            if (n == 0) return text.Length == 0 ? null : text.ToString();
            char c = (char)buffer[0];
            if (c == '\n') return text.ToString().TrimEnd('\r');
            text.Append(c);
            if (text.Length > 4096) throw new IOException("FTP 命令行过长");
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* 已停止 */ }
        lock (_gate)
        {
            foreach (var socket in _sockets)
            {
                try { socket.Dispose(); } catch { /* 已关闭 */ }
            }
            _sockets.Clear();
        }
    }
}
