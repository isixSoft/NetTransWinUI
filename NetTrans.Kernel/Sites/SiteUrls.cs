namespace NetTrans.Kernel.Sites;

/// <summary>URL 判别的纯字符串助手（不做 I/O），供各站点提取器共用。</summary>
internal static class SiteUrls
{
    /// <summary>Host 等于后缀或以 ".后缀" 结尾（www.bilibili.com ↔ bilibili.com）。</summary>
    public static bool HostMatches(string url, params string[] suffixes)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        var host = uri.Host.ToLowerInvariant();
        foreach (var suffix in suffixes)
            if (host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>URL path（无查询串）；解析失败返回 "/"。</summary>
    public static string PathOf(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? uri.AbsolutePath : "/";

    /// <summary>查询参数原始值（一次 percent-decode）；不存在返回 null。</summary>
    public static string? Query(string url, string name)
    {
        int cut = url.IndexOf('?');
        if (cut < 0) return null;
        foreach (var pair in url[(cut + 1)..].Split('&'))
        {
            int equals = pair.IndexOf('=');
            if (equals > 0 && pair[..equals].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                try { return Uri.UnescapeDataString(pair[(equals + 1)..]); }
                catch { return pair[(equals + 1)..]; }
            }
        }
        return null;
    }
}
