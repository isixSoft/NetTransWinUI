using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FtpE2ETests;

/// <summary>
/// 最小 SOCKS5 中继代理（TcpListener 手写，端口 0 起步）：RFC 1928 问候 +
/// 可选 RFC 1929 用户名/密码子协商。requireAuth=false 时问候一律回 05 00；
/// requireAuth=true 时回 05 02 并校验凭据，不匹配回 01 01 后断开。CONNECT
/// 成功回 05 00 00 01 + 绑定地址，然后把字节原样中继到请求指向的真实目标
/// （测试里即本机 TestServer）。记录每一次 CONNECT 与认证尝试供断言。
/// </summary>
public sealed class Socks5TestProxy : IDisposable
{
    public sealed record ConnectRecord(string Host, int Port);
    public sealed record AuthRecord(string User, string Password, bool Ok);

    private readonly TcpListener _listener;
    private readonly List<Socket> _clients = new();
    private readonly List<ConnectRecord> _connects = new();
    private readonly List<AuthRecord> _auths = new();
    private readonly object _gate = new();

    public int Port { get; }
    public bool RequireAuth { get; }
    public string ValidUser { get; }
    public string ValidPassword { get; }

    public Socks5TestProxy(bool requireAuth, string validUser = "", string validPassword = "")
    {
        RequireAuth = requireAuth;
        ValidUser = validUser;
        ValidPassword = validPassword;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public ConnectRecord[] Connects { get { lock (_gate) return _connects.ToArray(); } }
    public AuthRecord[] Auths { get { lock (_gate) return _auths.ToArray(); } }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptSocketAsync();
                client.NoDelay = true;
                lock (_gate) _clients.Add(client);
                _ = Task.Run(() => HandleClientAsync(client));
            }
        }
        catch
        {
            // 代理已停止（Dispose）
        }
    }

    private async Task HandleClientAsync(Socket client)
    {
        try
        {
            await HandshakeAsync(client);
        }
        catch
        {
            // 握手失败 / 客户端断开 / 中继结束——收摊即可
        }
        finally
        {
            try { client.Shutdown(SocketShutdown.Both); } catch { /* 已断开 */ }
            client.Dispose();
            lock (_gate) _clients.Remove(client);
        }
    }

    private async Task HandshakeAsync(Socket client)
    {
        // 问候：VER NMETHODS METHODS…
        var head = await ReadExactlyAsync(client, 2);
        if (head[0] != 0x05) throw new IOException($"SOCKS 版本异常：{head[0]}");
        var methods = await ReadExactlyAsync(client, head[1]);
        bool offersAuth = Array.IndexOf(methods, (byte)0x02) >= 0;

        if (RequireAuth)
        {
            if (!offersAuth)
            {
                await SendAsync(client, 0x05, 0xFF); // 没有可接受的方法
                return;
            }

            await SendAsync(client, 0x05, 0x02); // 要求 RFC 1929 子协商

            var verLen = await ReadExactlyAsync(client, 2);
            var user = await ReadExactlyAsync(client, verLen[1]);
            var passLen = await ReadExactlyAsync(client, 1);
            var pass = await ReadExactlyAsync(client, passLen[0]);
            string seenUser = Encoding.UTF8.GetString(user);
            string seenPass = Encoding.UTF8.GetString(pass);
            bool ok = seenUser == ValidUser && seenPass == ValidPassword;
            lock (_gate) _auths.Add(new AuthRecord(seenUser, seenPass, ok));

            await SendAsync(client, 0x01, ok ? (byte)0x00 : (byte)0x01);
            if (!ok) return; // 拒绝认证并断开
        }
        else
        {
            await SendAsync(client, 0x05, 0x00); // 无认证
        }

        // CONNECT 请求：VER CMD RSV ATYP ADDR PORT
        var request = await ReadExactlyAsync(client, 4);
        if (request[0] != 0x05 || request[1] != 0x01) throw new IOException("仅支持 CONNECT");
        string host;
        switch (request[3])
        {
            case 0x01:
                host = new IPAddress(await ReadExactlyAsync(client, 4)).ToString();
                break;
            case 0x03:
                int length = (await ReadExactlyAsync(client, 1))[0];
                host = Encoding.UTF8.GetString(await ReadExactlyAsync(client, length));
                break;
            case 0x04:
                host = new IPAddress(await ReadExactlyAsync(client, 16)).ToString();
                break;
            default:
                throw new IOException($"不支持的地址类型：{request[3]}");
        }
        var portBytes = await ReadExactlyAsync(client, 2);
        int port = (portBytes[0] << 8) | portBytes[1];
        lock (_gate) _connects.Add(new ConnectRecord(host, port));

        // 连接真实目标（测试里就是本机 TestServer）
        using var target = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        IPAddress address = IPAddress.TryParse(host, out var parsed)
            ? parsed
            : (await Dns.GetHostAddressesAsync(host))[0];
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await target.ConnectAsync(address, port, connectCts.Token);
        target.NoDelay = true;

        // 成功回复：VER REP RSV ATYP=IPv4 + 0.0.0.0:0
        await SendAsync(client, new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });

        // 原样双向中继
        await RelayAsync(client, target);
    }

    private static async Task RelayAsync(Socket a, Socket b)
    {
        var up = PumpAsync(a, b);
        var down = PumpAsync(b, a);
        await Task.WhenAny(up, down);
        try { a.Shutdown(SocketShutdown.Both); } catch { /* 已断开 */ }
        try { b.Shutdown(SocketShutdown.Both); } catch { /* 已断开 */ }
        try { await Task.WhenAll(up, down); } catch { /* 对端异常即结束 */ }
    }

    private static async Task PumpAsync(Socket from, Socket to)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            int n = await from.ReceiveAsync(buffer, SocketFlags.None);
            if (n == 0) return;
            await to.SendAsync(buffer.AsMemory(0, n), SocketFlags.None);
        }
    }

    private static async Task SendAsync(Socket socket, byte first, byte second)
    {
        await socket.SendAsync(new[] { first, second }, SocketFlags.None);
    }

    private static async Task SendAsync(Socket socket, byte[] bytes)
    {
        await socket.SendAsync(bytes, SocketFlags.None);
    }

    private static async Task<byte[]> ReadExactlyAsync(Socket socket, int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await socket.ReceiveAsync(buffer.AsMemory(read, count - read), SocketFlags.None);
            if (n == 0) throw new IOException("SOCKS 客户端提前关闭连接");
            read += n;
        }
        return buffer;
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* 已停止 */ }
        lock (_gate)
        {
            foreach (var socket in _clients)
            {
                try { socket.Dispose(); } catch { /* 已关闭 */ }
            }
            _clients.Clear();
        }
    }
}
