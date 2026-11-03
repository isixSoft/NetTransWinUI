using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ProxyTests;

/// <summary>
/// Minimal RFC1928 + RFC1929 SOCKS5 mock. Reads the greeting, replies with the
/// configured method (0x00 / 0x02 / 0xFF), optionally validates the RFC1929
/// subnegotiation against the expected user/pass and answers it with
/// VER(0x01) STATUS per the RFC, then handles CONNECT like the NetworkTests
/// mock: success reply, push a payload to the client, read the client's bytes
/// back as the stream read/write evidence.
/// </summary>
public sealed class AuthSocks5Server : IDisposable
{
    private readonly TcpListener _listener;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _payloadFromRemote;
    private readonly byte[] _methodReply;
    private readonly string? _expectedUser;
    private readonly string? _expectedPass;
    private readonly byte _authStatus;

    public int Port { get; }

    /// <summary>Full greeting as received: VER NMETHODS METHODS...</summary>
    public byte[]? Greeting { get; private set; }

    /// <summary>Raw RFC1929 request as received: VER ULEN UNAME PLEN PASSWD.</summary>
    public byte[]? Credentials { get; private set; }

    public string? SeenUser { get; private set; }
    public string? SeenPass { get; private set; }
    public string? Host { get; private set; }
    public int? DestinationPort { get; private set; }
    public byte[]? FromClient { get; private set; }
    public Task HandshakeAndEcho => _done.Task;

    /// <param name="methodReply">Second byte of the method selection reply (0x00, 0x02 or 0xFF).</param>
    /// <param name="expectedUser">When set, the mock rejects a mismatching username.</param>
    /// <param name="expectedPass">When set, the mock rejects a mismatching password.</param>
    /// <param name="authStatus">STATUS byte of the RFC1929 reply (0x00 = ok).</param>
    public AuthSocks5Server(
        byte[] payloadFromRemote,
        byte methodReply,
        string? expectedUser = null,
        string? expectedPass = null,
        byte authStatus = 0x00)
    {
        _payloadFromRemote = payloadFromRemote;
        _methodReply = new byte[] { 0x05, methodReply };
        _expectedUser = expectedUser;
        _expectedPass = expectedPass;
        _authStatus = authStatus;
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

            var greetingHead = await ReadExactlyAsync(stream, 2);
            var methods = await ReadExactlyAsync(stream, greetingHead[1]);
            Greeting = new byte[2 + methods.Length];
            Greeting[0] = greetingHead[0];
            Greeting[1] = greetingHead[1];
            methods.CopyTo(Greeting, 2);

            await stream.WriteAsync((ReadOnlyMemory<byte>)_methodReply);
            if (_methodReply[1] == 0xFF) return; // no acceptable method; client must give up

            if (_methodReply[1] == 0x02)
            {
                // RFC 1929 request: VER ULEN UNAME PLEN PASSWD
                var verLen = await ReadExactlyAsync(stream, 2);
                var user = await ReadExactlyAsync(stream, verLen[1]);
                var plen = await ReadExactlyAsync(stream, 1);
                var pass = await ReadExactlyAsync(stream, plen[0]);
                Credentials = new byte[2 + user.Length + 1 + pass.Length];
                Credentials[0] = verLen[0];
                Credentials[1] = (byte)user.Length;
                user.CopyTo(Credentials, 2);
                Credentials[2 + user.Length] = plen[0];
                pass.CopyTo(Credentials, 3 + user.Length);
                SeenUser = Encoding.UTF8.GetString(user);
                SeenPass = Encoding.UTF8.GetString(pass);
                if (_expectedUser is not null && SeenUser != _expectedUser)
                    throw new IOException($"用户名不匹配：期望 {_expectedUser}，实际 {SeenUser}");
                if (_expectedPass is not null && SeenPass != _expectedPass)
                    throw new IOException("密码不匹配");

                // RFC 1929 reply: VER(0x01) STATUS
                await stream.WriteAsync((ReadOnlyMemory<byte>)new byte[] { 0x01, _authStatus });
                if (_authStatus != 0x00) return; // auth refused; the client must fail
            }

            var head = await ReadExactlyAsync(stream, 4);
            if (head[0] != 0x05 || head[1] != 0x01 || head[3] != 0x03)
                throw new IOException($"CONNECT 请求异常：VER={head[0]} CMD={head[1]} ATYP={head[3]}");

            int length = (await ReadExactlyAsync(stream, 1))[0];
            Host = Encoding.UTF8.GetString(await ReadExactlyAsync(stream, length));
            var portBytes = await ReadExactlyAsync(stream, 2);
            DestinationPort = (portBytes[0] << 8) | portBytes[1];

            // success reply: VER REP RSV ATYP=IPv4 + 127.0.0.1 + 8080
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
