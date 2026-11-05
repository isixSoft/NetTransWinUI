using System.Net;

namespace NetTrans.Kernel;

/// <summary>nettrans://add 链接解析出的参数（P0-2 浏览器接管入口）。</summary>
/// <param name="Url">要下载的直链（http/https/ftp）。</param>
/// <param name="Sha256">期望 SHA-256（64 位十六进制，已归一小写）；null = 未提供。</param>
/// <param name="Token">握手 token，须与 LocalControlApi.Token 一致才被接受。</param>
/// <param name="SaveTo">保存目录（to 参数）；null = 未提供（API 层必填，UI 侧可预填默认目录）。</param>
public sealed record NettransAdd(string Url, string? Sha256, string Token, string? SaveTo);

/// <summary>
/// <c>nettrans://add?url=…&amp;sha256=…&amp;token=…&amp;to=…</c> 链接解析。
/// 协议关联的注册本体在 OS/UI 侧（注册表/应用清单）；本类只保证链接形态与
/// 参数校验：token 防止恶意页面伪造 nettrans:// 调用塞进任意下载。
/// </summary>
public static class NettransLink
{
    public static NettransAdd? Parse(string link, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(link))
        {
            error = "空链接";
            return null;
        }

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("nettrans", StringComparison.OrdinalIgnoreCase))
        {
            error = "须为 nettrans:// 链接";
            return null;
        }

        if (!uri.Host.Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            error = $"未知动作（{uri.Host}）";
            return null;
        }

        string? url = null, sha256 = null, token = null, saveTo = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = WebUtility.UrlDecode(eq < 0 ? pair : pair[..eq]);
            string value = WebUtility.UrlDecode(eq < 0 ? string.Empty : pair[(eq + 1)..]);
            if (key.Equals("url", StringComparison.OrdinalIgnoreCase)) url = value;
            else if (key.Equals("sha256", StringComparison.OrdinalIgnoreCase)) sha256 = value;
            else if (key.Equals("token", StringComparison.OrdinalIgnoreCase)) token = value;
            else if (key.Equals("to", StringComparison.OrdinalIgnoreCase)) saveTo = value;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "缺少 url 参数";
            return null;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps &&
             target.Scheme != Uri.UriSchemeFtp))
        {
            error = "url 须为 http(s)/ftp 直链";
            return null;
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            error = "缺少 token 参数";
            return null;
        }
        if (!string.IsNullOrEmpty(sha256) &&
            (sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c))))
        {
            error = "sha256 须为 64 位十六进制";
            return null;
        }

        return new NettransAdd(url, string.IsNullOrEmpty(sha256) ? null : sha256.ToLowerInvariant(), token,
            string.IsNullOrWhiteSpace(saveTo) ? null : saveTo);
    }
}
