namespace NetTrans.Kernel.Net;

/// <summary>One cookie from a Netscape cookies.txt file (browser / curl export).</summary>
public sealed record NetscapeCookie(
    string Domain, string Path, bool Secure, long ExpiresAt, string Name, string Value);

/// <summary>
/// Netscape HTTP Cookie File parsing and per-URL Cookie header rendering
/// (P1-1 cookie import). Domain matching follows RFC 6265: the host equals
/// the domain or is a subdomain of it; secure cookies only travel over
/// https. Expired entries are dropped; expiry 0 means a session cookie.
/// </summary>
public static class NetscapeCookies
{
    /// <summary>Parse a cookies.txt body; malformed lines are skipped silently.</summary>
    public static List<NetscapeCookie> Parse(string content, DateTimeOffset? now = null)
    {
        var at = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var cookies = new List<NetscapeCookie>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimStart();
            if (line.Length == 0) continue;
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal))
            {
                line = line["#HttpOnly_".Length..]; // httponly flag travels as a comment marker
            }
            else if (line.StartsWith('#'))
            {
                continue; // regular comment line
            }

            var fields = line.Split('\t');
            if (fields.Length < 7) continue;
            if (!long.TryParse(fields[4], out var expires)) continue;
            if (expires > 0 && expires <= at) continue; // expired; 0 = session cookie
            var name = fields[5].Trim();
            if (name.Length == 0) continue;
            cookies.Add(new NetscapeCookie(
                fields[0].Trim().TrimStart('.'),
                fields[2].Trim(),
                string.Equals(fields[3], "TRUE", StringComparison.OrdinalIgnoreCase),
                expires,
                name,
                fields[6]));
        }
        return cookies;
    }

    /// <summary>
    /// Render the cookies matching <paramref name="url"/> as a Cookie header
    /// value ("n=v; n2=v2" in file order); null when nothing matches.
    /// </summary>
    public static string? HeaderFor(IEnumerable<NetscapeCookie> cookies, Uri url, DateTimeOffset? now = null)
    {
        var at = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var host = url.Host;
        var isSecure = url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        var pairs = new List<string>();
        foreach (var cookie in cookies)
        {
            if (cookie.ExpiresAt > 0 && cookie.ExpiresAt <= at) continue;
            if (cookie.Secure && !isSecure) continue;
            if (!DomainMatches(host, cookie.Domain)) continue;
            pairs.Add($"{cookie.Name}={cookie.Value}");
        }
        return pairs.Count == 0 ? null : string.Join("; ", pairs);
    }

    private static bool DomainMatches(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
