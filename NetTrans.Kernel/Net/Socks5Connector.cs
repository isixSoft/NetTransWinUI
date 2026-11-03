using System.Net.Sockets;
using System.Text;

namespace NetTrans.Kernel.Net;

/// <summary>
/// Minimal SOCKS5 CONNECT (RFC 1928, NO-AUTH) used through
/// SocketsHttpHandler.ConnectCallback so KernelOptions.Proxy can say socks5://.
/// Domain-name addressing is used for every destination, which covers both
/// host names and literal IPs at a small resolution cost on the proxy.
/// </summary>
public static class Socks5Connector
{
    public static async Task<Stream> ConnectAsync(
        Uri proxy, string destinationHost, int destinationPort, CancellationToken cancellation)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(proxy.Host, proxy.Port == 0 ? 1080 : proxy.Port, cancellation);
            var stream = tcp.GetStream();

            // greeting: VER=5, 1 method, NO-AUTH
            await stream.WriteAsync((ReadOnlyMemory<byte>)new byte[] { 0x05, 0x01, 0x00 }, cancellation);
            var greeting = await ReadExactlyAsync(stream, 2, cancellation);
            if (greeting[0] != 0x05 || greeting[1] != 0x00)
                throw new IOException($"SOCKS5 代理拒绝了免认证连接（0x{greeting[1]:x2}）");

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
