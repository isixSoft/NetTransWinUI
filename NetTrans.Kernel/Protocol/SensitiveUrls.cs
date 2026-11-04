namespace NetTrans.Kernel.Protocol;

/// <summary>
/// PT 场景的敏感 URL 脱敏：tracker/magnet 链接里的 passkey、uid、token 等
/// 私有参数在展示层（任务列表、日志、inspector）必须遮蔽，但原始 URL 仍
/// 原样保留在 task.Url 供外部引擎下发。展示与数据面分离，这是 PT 用户
/// 截图/贴日志不翻车的底线。
/// </summary>
public static partial class SensitiveUrls
{
    /// <summary>视为私密凭据的查询参数名（小写比对）。</summary>
    private static readonly string[] PrivateKeys =
    {
        "passkey", "uid", "auth", "authkey", "authkeyid", "token", "key", "secret", "pwd", "password",
    };

    /// <summary>
    /// 返回脱敏后的展示串：敏感参数值替换为 ***。
    /// magnet 链接会逐个 decode tr= 值（announce 里还嵌一层查询串）后遮蔽，
    /// 便于直接阅读；普通 http(s) URL 只处理自身查询串。
    /// </summary>
    public static string Mask(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            var queryIndex = url.IndexOf('?');
            if (queryIndex < 0) return url;

            var head = url[..(queryIndex + 1)];
            var masked = url[(queryIndex + 1)..]
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair =>
                {
                    var eq = pair.IndexOf('=');
                    if (eq <= 0) return pair;
                    var key = Uri.UnescapeDataString(pair[..eq]);
                    var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                    // tr= 的值是 announce URL，里面还嵌一层查询串 → 解码后递归遮蔽；
                    // 展示层直接输出解码结果，可读性优先（原始 magnet 在 task.Url 不动）
                    var safeValue = key.Equals("tr", StringComparison.OrdinalIgnoreCase)
                        ? MaskQuery(value)
                        : value;
                    return $"{key}={safeValue}";
                });
            return head + string.Join("&", masked);
        }

        return MaskQuery(url);
    }

    /// <summary>遮蔽单个 URL 自身查询串中的敏感参数值；无查询串原样返回。</summary>
    private static string MaskQuery(string url)
    {
        var queryIndex = url.IndexOf('?');
        if (queryIndex < 0) return url;

        var head = url[..(queryIndex + 1)];
        var masked = url[(queryIndex + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) return pair;
                var key = pair[..eq];
                var value = pair[(eq + 1)..];
                var safeValue = IsPrivate(key) ? "***" : MaskEmbedded(value);
                return $"{key}={safeValue}";
            });
        return head + string.Join("&", masked);
    }

    /// <summary>
    /// 值里嵌着 percent-encode 的 URL（shell 命令、拼接日志里最常见）：
    /// 解一层码继续遮蔽，展示用解码结果——magnet 嵌进命令行也逃不掉。
    /// </summary>
    private static string MaskEmbedded(string value)
    {
        if (!value.Contains('%')) return value;
        var decoded = Uri.UnescapeDataString(value);
        return decoded.Contains('?') ? MaskQuery(decoded) : value;
    }

    /// <summary>键名归一后比对：passkey=、PassKey=、authkeyid= 都算私密。</summary>
    private static bool IsPrivate(string key)
    {
        key = key.Trim().ToLowerInvariant();
        return PrivateKeys.Any(k => k == key);
    }
}
