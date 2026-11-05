using System.Text.RegularExpressions;
using NetTrans.Kernel.Protocol;
using NetTrans.Kernel.Protocol.Drivers;

namespace NetTrans.Kernel.Sites;

/// <summary>
/// 通用页面直链兜底（P1-8）：认领所有无已知文件扩展名的 http(s) URL，抓取 HTML
/// 扫描 &lt;a href&gt; / &lt;video src&gt; / &lt;source src&gt;，只保留流媒体引用
/// （.m3u8/.mpd）与常见直链文件，流媒体排前（第 0 条默认选用）。找不到候选或内容
/// 不是 HTML 时放弃认领（返回 null），由框架交给下一个认领者或协议探测——
/// 直链行为不变。新站点出现时仍只需编写对应站点的提取器，本类只兜底。
/// </summary>
public sealed class GenericPageSite : ISiteExtractor
{
    public string Name => "通用页面";

    // 协议驱动已直接处理的资源 + 常见直链文件：这些 URL 不当作页面认领
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u8", ".mpd", ".torrent",
        ".pdf", ".zip", ".rar", ".7z", ".tar", ".gz",
        ".exe", ".msi", ".dmg", ".pkg", ".iso", ".apk",
        ".mp4", ".mkv", ".avi", ".mov", ".webm",
        ".mp3", ".flac", ".wav", ".ogg", ".m4a",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".epub",
    };

    public bool CanHandle(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path = url;
        int cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];
        return !FileExtensions.Contains(Path.GetExtension(path));
    }

    public async Task<SiteMedia?> ExtractAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        string html;
        try
        {
            html = await DriverHelpers.GetTextAsync(url, context, cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            context.Log?.Invoke($"通用页面：抓取失败，放弃认领（{DriverHelpers.Brief(error.Message)}）");
            return null;
        }

        if (!LooksLikeHtml(html))
        {
            context.Log?.Invoke("通用页面：内容不是 HTML，放弃认领");
            return null;
        }

        var title = "";
        if (TitleRegex.Match(html) is { Success: true } match)
        {
            title = match.Groups["title"].Value.Trim();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var streaming = new List<SiteStream>();
        var files = new List<SiteStream>();

        foreach (Match reference in RefRegex.Matches(html))
        {
            var raw = reference.Groups["ref"].Value.Trim();
            if (raw.Length == 0 || raw.StartsWith('#') ||
                raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string absolute;
            try
            {
                absolute = new Uri(new Uri(url), raw).AbsoluteUri;
            }
            catch
            {
                continue;
            }

            if (!seen.Add(absolute)) continue;

            string clean = absolute;
            int cut = clean.IndexOfAny(['?', '#']);
            if (cut >= 0) clean = clean[..cut];
            var ext = Path.GetExtension(clean);
            if (ext is ".m3u8" or ".mpd")
            {
                streaming.Add(new SiteStream(absolute, ext == ".m3u8" ? "m3u8 流" : "mpd 流"));
            }
            else if (ext.Length > 1 && FileExtensions.Contains(ext))
            {
                files.Add(new SiteStream(absolute, $"直链（{ext}）"));
            }
            // 其余（导航 / 锚点 / 无扩展名资源）一律跳过
        }

        var streams = streaming.Concat(files).ToList();
        if (streams.Count == 0)
        {
            context.Log?.Invoke("通用页面：未发现可下载候选，放弃认领");
            return null;
        }

        context.Log?.Invoke($"通用页面：{streaming.Count} 个流引用 / {files.Count} 个直链文件");
        return new SiteMedia(Name, title, streams);
    }

    // <a href> / <video src> / <source src>（<audio> 里的 <source> 一并覆盖）
    private static readonly Regex RefRegex = new(
        @"<(?:a|video|source|audio)\b[^>]*?\b(?:href|src)\s*=\s*(?:""(?<ref>[^""]*)""|'(?<ref>[^']*)'|(?<ref>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleRegex = new(
        @"<title[^>]*>(?<title>.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static bool LooksLikeHtml(string text) =>
        text.TrimStart().StartsWith('<') &&
        (text.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("<body", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("<a ", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("<video", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("<source", StringComparison.OrdinalIgnoreCase));
}
