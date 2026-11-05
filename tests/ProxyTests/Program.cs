using System.Text;
using NetTrans.Kernel.Net;

namespace ProxyTests;

/// <summary>
/// SOCKS5 RFC1929 username/password authentication (Socks5Connector) and proxy
/// string parsing (ProxyInfo.Parse) tests. Hand-rolled TcpListener mocks, zero
/// NuGet dependencies, same assertion style as NetworkTests.
/// </summary>
internal sealed class Program : TestKit
{
    private static int _passed, _failed;
    private static readonly List<string> _failures = new();

    private static async Task<int> Main()
    {
        Console.WriteLine("ProxyTests：SOCKS5 RFC1929 认证 + ProxyInfo.Parse");
        Console.WriteLine(new string('=', 64));

        await RunAsync("1. RFC1929：正确凭据完成 CONNECT + 双向数据", () => AuthRoundtripAsync());
        await RunAsync("2. RFC1929：错误密码 → 认证失败异常", () => WrongPasswordAsync());
        await RunAsync("3. 无凭据 + 仅认证服务器回 0xFF → 抛出", () => NoCredsRejectedAsync());
        await RunAsync("4. 无凭据 + 服务器强选 0x02 → 抛出", () => NoCredsPickedAuthAsync());
        await RunAsync("5. 有凭据 + 服务器选 NO-AUTH(0x00) → 直接继续", () => ServerPicksNoAuthAsync());
        await RunAsync("6. 有凭据 + 服务器拒绝全部方法(0xFF) → 抛出", () => AllMethodsRejectedAsync());
        await RunAsync("7. 百分号编码凭据端到端解码", () => PercentEncodedCredsAsync());
        await RunAsync("8. ProxyInfo.Parse 解析表", () => ParseTableAsync());

        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"结果：{_passed} 通过，{_failed} 失败");
        foreach (var item in _failures) Console.WriteLine("  [FAIL] " + item);
        return _failed == 0 ? 0 : 1;
    }

    // ── 1. 正确凭据的完整握手 ─────────────────────────────────────────────

    private static async Task AuthRoundtripAsync()
    {
        var payload = Encoding.UTF8.GetBytes("AUTH-REMOTE-PAYLOAD-0123456789");
        using var socks = new AuthSocks5Server(payload, methodReply: 0x02,
            expectedUser: "alice", expectedPass: "s3cr3t:pass");

        // 密码里带冒号：UserInfo 应按第一个冒号切分，其余全是密码
        using var stream = await Socks5Connector.ConnectAsync(
            new Uri($"socks5://alice:s3cr3t:pass@127.0.0.1:{socks.Port}"),
            "auth-target.example.org", 8443, default);
        That(stream.CanRead && stream.CanWrite, "返回的流应可读可写");

        var received = new byte[payload.Length];
        await ReadExactly(stream, received);
        Equal(Encoding.UTF8.GetString(payload), Encoding.UTF8.GetString(received), "远端推送的数据应原样到达");

        var reply = new byte[payload.Length];
        Encoding.UTF8.GetBytes("CLIENT-AUTH-OK").CopyTo(reply, 0);
        await stream.WriteAsync((ReadOnlyMemory<byte>)reply);

        var finished = await Task.WhenAny(socks.HandshakeAndEcho, Task.Delay(5_000));
        That(finished == socks.HandshakeAndEcho, $"服务端握手/回读应完成：{socks.HandshakeAndEcho.Status}");

        Equal((byte)0x05, socks.Greeting?[0] ?? 0, "greeting VER 应为 0x05");
        Equal((byte)0x02, socks.Greeting?[1] ?? 0, "有凭据时 greeting 应提供 2 个方法");
        Equal((byte)0x02, socks.Greeting?[2] ?? 0, "方法表第一位应为 0x02（认证优先）");
        Equal((byte)0x00, socks.Greeting?[3] ?? 0xFF, "方法表第二位应为 0x00（免认证兜底）");

        Equal("alice", socks.SeenUser, "服务端应收到用户名 alice");
        Equal("s3cr3t:pass", socks.SeenPass, "首个冒号后的全部内容应作为密码");
        That(socks.Credentials is not null && socks.Credentials[0] == 0x01,
            "RFC1929 子协商请求应以 VER=0x01 开头（而非 0x05）");
        That(socks.Credentials is not null && socks.Credentials[1] == 5,
            "ULEN 应为 5（alice）");

        Equal("auth-target.example.org", socks.Host ?? "", "CONNECT 应携带目标域名（ATYP=3）");
        Equal(8443, socks.DestinationPort ?? -1, "CONNECT 应携带目标端口（大端两字节）");
        Equal(Encoding.UTF8.GetString(reply), Encoding.UTF8.GetString(socks.FromClient ?? Array.Empty<byte>()),
            "客户端写入的数据应原样到达远端");
    }

    // ── 2. 错误密码 ───────────────────────────────────────────────────────

    private static async Task WrongPasswordAsync()
    {
        using var socks = new AuthSocks5Server(Payload(16), methodReply: 0x02,
            expectedUser: "alice", authStatus: 0x01);

        var error = await ThrowsAsync(() => Socks5Connector.ConnectAsync(
            new Uri($"socks5://alice:bad-pass@127.0.0.1:{socks.Port}"),
            "auth-target.example.org", 443, default));
        That(error is IOException, $"应为 IOException：{error.GetType().Name}");
        That(error.Message.Contains("认证失败"), $"异常应提示认证失败：{error.Message}");

        Equal("alice", socks.SeenUser, "服务端应先收到用户名");
        Equal("bad-pass", socks.SeenPass, "服务端应收到（错误的）密码原文字节");
    }

    // ── 3. 无凭据 vs 仅认证服务器（合规回 0xFF） ──────────────────────────

    private static async Task NoCredsRejectedAsync()
    {
        using var socks = new AuthSocks5Server(Payload(16), methodReply: 0xFF);

        var error = await ThrowsAsync(() => Socks5Connector.ConnectAsync(
            new Uri($"socks5://127.0.0.1:{socks.Port}"), "auth-target.example.org", 443, default));
        That(error is IOException, $"应为 IOException：{error.GetType().Name}");
        That(error.Message.Contains("拒绝"), $"异常应提示方法被拒绝：{error.Message}");

        // 无凭据时 greeting 仍是原来的三字节 05 01 00
        Equal((byte)0x05, socks.Greeting?[0] ?? 0, "greeting VER 应为 0x05");
        Equal((byte)0x01, socks.Greeting?[1] ?? 0, "无凭据时 NMETHODS 应为 1");
        Equal((byte)0x00, socks.Greeting?[2] ?? 0xFF, "无凭据时方法表应只有 0x00");
    }

    // ── 4. 无凭据 vs 服务器强行选择用户名/密码 ────────────────────────────

    private static async Task NoCredsPickedAuthAsync()
    {
        using var socks = new AuthSocks5Server(Payload(16), methodReply: 0x02);

        var error = await ThrowsAsync(() => Socks5Connector.ConnectAsync(
            new Uri($"socks5://127.0.0.1:{socks.Port}"), "auth-target.example.org", 443, default));
        That(error is IOException, $"应为 IOException：{error.GetType().Name}");
        That(error.Message.Contains("凭据"), $"异常应提示缺少凭据：{error.Message}");
        That(socks.Credentials is null, "不应凭空编造 RFC1929 子协商");
    }

    // ── 5. 有凭据但服务器选择免认证 ───────────────────────────────────────

    private static async Task ServerPicksNoAuthAsync()
    {
        var payload = Encoding.UTF8.GetBytes("NOAUTH-CHOSEN-PAYLOAD-0123456789");
        using var socks = new AuthSocks5Server(payload, methodReply: 0x00);

        using var stream = await Socks5Connector.ConnectAsync(
            new Uri($"socks5://alice:s3cr3t@127.0.0.1:{socks.Port}"),
            "noauth-target.example.org", 8080, default);
        var received = new byte[payload.Length];
        await ReadExactly(stream, received);
        Equal(Encoding.UTF8.GetString(payload), Encoding.UTF8.GetString(received), "免认证路径数据应原样到达");

        await stream.WriteAsync((ReadOnlyMemory<byte>)new byte[payload.Length]);
        var finished = await Task.WhenAny(socks.HandshakeAndEcho, Task.Delay(5_000));
        That(finished == socks.HandshakeAndEcho, $"服务端握手/回读应完成：{socks.HandshakeAndEcho.Status}");
        That(socks.Credentials is null, "服务器选 0x00 时不应发送 RFC1929 子协商");
        Equal("noauth-target.example.org", socks.Host ?? "", "CONNECT 应携带目标域名");
        Equal(8080, socks.DestinationPort ?? -1, "CONNECT 应携带目标端口");
    }

    // ── 6. 有凭据但服务器拒绝全部方法 ─────────────────────────────────────

    private static async Task AllMethodsRejectedAsync()
    {
        using var socks = new AuthSocks5Server(Payload(16), methodReply: 0xFF);

        var error = await ThrowsAsync(() => Socks5Connector.ConnectAsync(
            new Uri($"socks5://alice:s3cr3t@127.0.0.1:{socks.Port}"),
            "auth-target.example.org", 443, default));
        That(error is IOException, $"应为 IOException：{error.GetType().Name}");
        That(error.Message.Contains("拒绝"), $"异常应提示全部方法被拒绝：{error.Message}");
        That(socks.Credentials is null, "被拒绝后不应再发送 RFC1929 子协商");
    }

    // ── 7. 百分号编码凭据 ─────────────────────────────────────────────────

    private static async Task PercentEncodedCredsAsync()
    {
        var payload = Payload(16);
        using var socks = new AuthSocks5Server(payload, methodReply: 0x02,
            expectedUser: "alice", expectedPass: "p@ss:w");

        // al%69ce → alice，p%40ss%3Aw → p@ss:w
        using var stream = await Socks5Connector.ConnectAsync(
            new Uri($"socks5://al%69ce:p%40ss%3Aw@127.0.0.1:{socks.Port}"),
            "encoded-target.example.org", 443, default);
        That(stream.CanRead && stream.CanWrite, "返回的流应可读可写");

        var received = new byte[payload.Length];
        await ReadExactly(stream, received);
        Equal(Encoding.UTF8.GetString(payload), Encoding.UTF8.GetString(received), "远端推送的数据应原样到达");
        await stream.WriteAsync((ReadOnlyMemory<byte>)payload);

        var finished = await Task.WhenAny(socks.HandshakeAndEcho, Task.Delay(5_000));
        That(finished == socks.HandshakeAndEcho, $"服务端握手/回读应完成：{socks.HandshakeAndEcho.Status}");
        That(socks.SeenUser == "alice", $"服务端应收到解码后的用户名 alice，实际 {socks.SeenUser}");
        That(socks.SeenPass == "p@ss:w", $"服务端应收到解码后的密码 p@ss:w，实际 {socks.SeenPass}");
    }

    // ── 8. ProxyInfo.Parse 表 ─────────────────────────────────────────────

    private static Task ParseTableAsync()
    {
        Equal(new ProxyInfo("socks5", "h", 1080, "u", "p"), ProxyInfo.Parse("socks5://u:p@h:1080"), "socks5 全字段");
        Equal(new ProxyInfo("socks5", "h", 1080, null, null), ProxyInfo.Parse("socks5://h"), "socks5 无端口 → 默认 1080");
        Equal(new ProxyInfo("socks5", "h", 1080, "u", null), ProxyInfo.Parse("socks5://u@h"), "socks5 仅用户名");
        Equal(new ProxyInfo("http", "h", 80, "u", "p"), ProxyInfo.Parse("http://u:p@h"), "http 无端口 → 默认 80");
        Equal(new ProxyInfo("https", "h", 443, null, null), ProxyInfo.Parse("https://h"), "https 无端口 → 默认 443");
        Equal(new ProxyInfo("http", "h", 9000, null, null), ProxyInfo.Parse("h:9000"), "裸 host:port → scheme http");
        Equal(new ProxyInfo("http", "h", 9000, null, null), ProxyInfo.Parse("  h:9000  "), "首尾空白应被忽略");
        Equal(new ProxyInfo("socks5", "h", 1080, "u", "p@x"), ProxyInfo.Parse("socks5://u:p%40x@h"), "百分号编码密码应被解码");
        Equal(new ProxyInfo("socks5", "h", 1080, "u", "p:x"), ProxyInfo.Parse("socks5://u:p%3Ax@h"), "编码冒号解码为密码的一部分");
        Equal(new ProxyInfo("socks5", "H", 1, "u", "p"), ProxyInfo.Parse("SOCKS5://u:p@H:1"), "scheme 小写化，host 保留原样");
        Equal(new ProxyInfo("socks5", "h", 1080, "u", "p"), ProxyInfo.Parse("socks5://u:p@h:1080/some/path"), "路径尾随应被忽略");

        That(ProxyInfo.Parse(null) is null, "null → null");
        That(ProxyInfo.Parse("") is null, "空串 → null");
        That(ProxyInfo.Parse("   ") is null, "纯空白 → null");
        That(ProxyInfo.Parse("::::") is null, ":::: → null");
        That(ProxyInfo.Parse("socks5://") is null, "缺主机 → null");
        That(ProxyInfo.Parse("socks5://:1080") is null, "仅端口缺主机 → null");
        That(ProxyInfo.Parse("ftp://h") is null, "不支持的 scheme → null");
        That(ProxyInfo.Parse("h:notaport") is null, "非数字端口 → null");
        That(ProxyInfo.Parse("h:99999") is null, "超范围端口 → null");
        return Task.CompletedTask;
    }

    // ── 公共构件 ──────────────────────────────────────────────────────────

    private static async Task RunAsync(string title, Func<Task> test)
    {
        Console.Write($"{title,-56}");
        try
        {
            await test();
            _passed++;
            Console.WriteLine("通过");
        }
        catch (Exception error)
        {
            _failed++;
            _failures.Add($"{title}: {error.Message}");
            Console.WriteLine($"失败：{error.Message}");
        }
    }

    private static async Task<Exception> ThrowsAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            return error;
        }
        throw new Exception("应当抛出异常");
    }

    private static async Task ReadExactly(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0) throw new IOException("流提前关闭");
            read += n;
        }
    }

    private static byte[] Payload(int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(i * 31 % 251);
        return data;
    }
}
