namespace NetTrans.Kernel.Net;

/// <summary>
/// Parsed proxy endpoint. <see cref="Parse"/> accepts
/// "socks5://[user:pass@]host[:port]" (default port 1080),
/// "http://"/"https://[user:pass@]host[:port]" (default ports 80/443) and the
/// bare "host:port" form (treated as http). The userinfo is percent-decoded
/// once; the password is everything after the first colon, so it may itself
/// contain colons. Returns null when the string is empty, uses an unsupported
/// scheme, or has no usable host.
/// </summary>
public sealed record ProxyInfo(string Scheme, string Host, int Port, string? Username, string? Password)
{
    public static ProxyInfo? Parse(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return null;

        var value = proxy.Trim();
        var scheme = "http";

        int schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            scheme = value[..schemeEnd].ToLowerInvariant();
            value = value[(schemeEnd + 3)..];
        }

        if (scheme is not ("socks5" or "http" or "https")) return null;

        string? username = null, password = null;
        int at = value.LastIndexOf('@');
        if (at >= 0)
        {
            var userinfo = value[..at];
            value = value[(at + 1)..];
            int colon = userinfo.IndexOf(':');
            username = Uri.UnescapeDataString(colon < 0 ? userinfo : userinfo[..colon]);
            if (colon >= 0) password = Uri.UnescapeDataString(userinfo[(colon + 1)..]);
        }

        // The authority ends at the first path/query/fragment character.
        int tail = value.IndexOfAny(['/', '?', '#']);
        if (tail >= 0) value = value[..tail];

        int port = scheme switch { "socks5" => 1080, "https" => 443, _ => 80 };
        int portStart = value.LastIndexOf(':');
        if (portStart >= 0)
        {
            var portText = value[(portStart + 1)..];
            if (portText.Length == 0 || !int.TryParse(portText, out port) || port is < 1 or > 65535)
                return null;
            value = value[..portStart];
        }

        // Missing host (this also rejects bare IPv6 literals, which the kernel
        // never accepted for proxy strings).
        if (value.Length == 0 || value.Contains(':')) return null;

        return new ProxyInfo(scheme, value, port, username, password);
    }
}
