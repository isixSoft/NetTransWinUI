using System.Net.Sockets;
using System.Text;

namespace NetTrans.Kernel.Net;

/// <summary>
/// Minimal SOCKS5 CONNECT (RFC 1928) with optional RFC 1929 username/password
/// subnegotiation, used through SocketsHttpHandler.ConnectCallback so
/// KernelOptions.Proxy can say socks5://[user:pass@]host[:port].
/// Credentials are taken from the proxy URI userinfo ("user:pass",
/// percent-decoded); when they are present the greeting offers auth first and
/// NO-AUTH as a fallback. Domain-name addressing is used for every destination,
/// which covers both host names and literal IPs at a small resolution cost on
/// the proxy.
/// </summary>
public static class Socks5Connector
{
    private const byte Version = 0x05;
    private const byte MethodNoAuth = 0x00;
    private const byte MethodUserPass = 0x02;
    private const byte UserPassVersion = 0x01;

    public static async Task<Stream> ConnectAsync(
        Uri proxy, string destinationHost, int destinationPort, CancellationToken cancellation)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(proxy.Host, proxy.Port <= 0 ? 1080 : proxy.Port, cancellation);
            var stream = tcp.GetStream();

            var (user, pass) = SplitCredentials(proxy);
            if (user is null)
            {
                // greeting: VER=5, 1 method, NO-AUTH
                await stream.WriteAsync((ReadOnlyMemory<byte>)new byte[] { Version, 0x01, MethodNoAuth }, cancellation);
            }
            else
            {
                // greeting: VER=5, 2 methods, username/password first, NO-AUTH fallback
                await stream.WriteAsync((ReadOnlyMemory<byte>)new byte[] { Version, 0x02, MethodUserPass, MethodNoAuth }, cancellation);
            }

            var method = await ReadExactlyAsync(stream, 2, cancellation);
            if (method[0] != Version)
                throw new IOException($"SOCKS5 代理返回了不支持的版本（0x{method[0]:x2}）");
            switch (method[1])
            {
                case MethodNoAuth:
                    // server picked anonymous access; skip the subnegotiation
                    break;
                case MethodUserPass when user is null:
                    throw new IOException("SOCKS5 代理要求用户名/密码认证，但代理地址未提供凭据");
                case MethodUserPass:
                    await AuthenticateAsync(stream, user, pass, cancellation);
                    break;
                case 0xFF:
                    throw new IOException("SOCKS5 代理拒绝了本端提供的全部认证方法（0xFF）");
                default:
                    throw new IOException($"SOCKS5 代理选择了不受支持的认证方法（0x{method[1]:x2}）");
            }

            var host = Encoding.UTF8.GetBytes(destinationHost);
            if (host.Length > 255) throw new IOException("目标主机名超过 SOCKS5 长度限制");

            var request = new byte[7 + host.Length];
            request[0] = 0x05;
            request[1] = 0x01; // CONNECT
            request[2] = 0x00;
            request[3] = 0x03; // domain name
            request[4] = (byte)host.Length;
            host.CopyTo(request, 5);
            request[^2] = (byte)(destinationPort >> 8);
            request[^1] = (byte)destinationPort;
            await stream.WriteAsync((ReadOnlyMemory<byte>)request, cancellation);

            var head = await ReadExactlyAsync(stream, 4, cancellation);
            if (head[1] != 0x00) throw new IOException($"SOCKS5 代理返回错误码 0x{head[1]:x2}");

            int extra = head[3] switch
            {
                0x01 => 4 + 2, // IPv4 + port
                0x04 => 16 + 2, // IPv6 + port
                0x03 => (await ReadExactlyAsync(stream, 1, cancellation))[0] + 2,
                _ => throw new IOException("SOCKS5 代理返回未知地址类型"),
            };
            await ReadExactlyAsync(stream, extra, cancellation);

            // The stream keeps the socket alive from here; the TcpClient wrapper is dropped.
            return stream;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// RFC 1929 subnegotiation: VER=1, ULEN, UNAME, PLEN, PASSWD (UTF-8);
    /// the server answers VER STATUS, where only STATUS 0 means success.
    /// </summary>
    private static async Task AuthenticateAsync(Stream stream, string user, string pass, CancellationToken cancellation)
    {
        var name = Encoding.UTF8.GetBytes(user);
        var secret = Encoding.UTF8.GetBytes(pass);
        if (name.Length > 255 || secret.Length > 255)
            throw new IOException("SOCKS5 认证的用户名或密码超过 255 字节限制");

        var request = new byte[3 + name.Length + secret.Length];
        request[0] = UserPassVersion;
        request[1] = (byte)name.Length;
        name.CopyTo(request, 2);
        request[2 + name.Length] = (byte)secret.Length;
        secret.CopyTo(request, 3 + name.Length);
        await stream.WriteAsync((ReadOnlyMemory<byte>)request, cancellation);

        var status = await ReadExactlyAsync(stream, 2, cancellation);
        if (status[1] != 0x00)
            throw new IOException($"SOCKS5 代理认证失败（状态 0x{status[1]:x2}），请检查用户名或密码");
    }

    /// <summary>
    /// UserInfo carries "user:pass" (password optional); the password is
    /// everything after the first colon, so it may itself contain colons.
    /// Percent escapes are decoded here, one level.
    /// </summary>
    private static (string? User, string Pass) SplitCredentials(Uri proxy)
    {
        var info = proxy.UserInfo;
        if (string.IsNullOrEmpty(info)) return (null, string.Empty);
        int sep = info.IndexOf(':');
        return sep < 0
            ? (Uri.UnescapeDataString(info), string.Empty)
            : (Uri.UnescapeDataString(info[..sep]), Uri.UnescapeDataString(info[(sep + 1)..]));
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken cancellation)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellation);
            if (n == 0) throw new IOException("SOCKS5 代理提前关闭了连接");
            read += n;
        }

        return buffer;
    }
}
