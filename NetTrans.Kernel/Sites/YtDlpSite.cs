using System.Diagnostics;
using System.Text.Json;
using NetTrans.Kernel.Protocol;

namespace NetTrans.Kernel.Sites;

/// <summary>
/// yt-dlp 兜底站点提取器（外部解析引擎）：内置站点不认领的 http(s) 页面交给
/// <c>yt-dlp --dump-single-json</c> 解析为 <see cref="SiteMedia"/>，直链仍由
/// NetTrans 的协议驱动探测与传输——借鉴 Varia / Persepolis 的组合策略。
/// CanHandle 声称一切 http(s)，注册表必须把本类放在最后（内置站点优先）；
/// 二进制不可用时 ExtractAsync 返回 null（诚实降级），坏输出抛 FormatException
/// （框架记日志后同样回退协议探测）。
/// </summary>
public sealed class YtDlpSite : ISiteExtractor
{
    public const string SiteName = "yt-dlp";

    private readonly Func<string, CancellationToken, Task<string?>> _runner;

    /// <param name="runner">yt-dlp 运行槽：给定页面 URL 返回 dump-json 的 stdout；
    /// null = 二进制不可用。默认从 PATH 运行 yt-dlp（测试经 KernelOptions.YtDlpRunner 注入）。</param>
    public YtDlpSite(Func<string, CancellationToken, Task<string?>>? runner = null)
        => _runner = runner ?? RunYtDlpAsync;

    public string Name => SiteName;

    /// <summary>兜底认领：一切绝对 http(s) URL（注册顺序保证内置站点优先）。</summary>
    public bool CanHandle(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public async Task<SiteMedia?> ExtractAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        string? json = await _runner(url, cancellation);
        if (json is null) return null; // yt-dlp 不可用 → 框架回退协议探测

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new FormatException($"yt-dlp 输出不是有效 JSON（{error.Message}）");
        }

        using (document)
        {
            var root = document.RootElement;
            var streams = new List<SiteStream>();

            if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
            {
                // 无混流器（不做 ffmpeg）：只收渐进式与纯音频，video-only/storyboard 排除
                var ranked = new List<(SiteStream Stream, int Height, long Size)>();
                foreach (var format in formats.EnumerateArray())
                {
                    var stream = FormatToStream(format);
                    if (stream is not null) ranked.Add((stream, HeightOf(format), SizeOf(format)));
                }

                ranked.Sort((a, b) =>
                {
                    int byHeight = b.Height.CompareTo(a.Height); // height 降序，空靠后
                    return byHeight != 0 ? byHeight : b.Size.CompareTo(a.Size);
                });
                streams.AddRange(ranked.Select(item => item.Stream));
            }

            if (root.TryGetProperty("subtitles", out var subtitles) && subtitles.ValueKind == JsonValueKind.Object)
            {
                foreach (var language in subtitles.EnumerateObject())
                {
                    if (language.Value.ValueKind != JsonValueKind.Array) continue;
                    foreach (var entry in language.Value.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object ||
                            !entry.TryGetProperty("url", out var subUrl) ||
                            subUrl.ValueKind != JsonValueKind.String ||
                            subUrl.GetString() is not { Length: > 0 } subLink) continue;

                        string ext = entry.TryGetProperty("ext", out var subExt) &&
                                     subExt.ValueKind == JsonValueKind.String
                            ? subExt.GetString() ?? "vtt"
                            : "vtt";
                        streams.Add(new SiteStream(subLink, $"字幕 {language.Name} ({ext})"));
                        break; // 每语言取第一条（vtt/srt 等价，够用）
                    }
                }
            }

            if (streams.Count == 0) return null; // 没有可下载的流（含 playlist 等形态）→ 框架回退

            string title = root.TryGetProperty("title", out var titleElement) &&
                           titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString() ?? SiteName
                : SiteName;
            return new SiteMedia(SiteName, title, streams);
        }
    }

    /// <summary>一个 format → 一条流；null = 排除（无 url / 需混流的 video-only / storyboard）。</summary>
    private static SiteStream? FormatToStream(JsonElement format)
    {
        if (format.ValueKind != JsonValueKind.Object ||
            !format.TryGetProperty("url", out var urlElement) ||
            urlElement.ValueKind != JsonValueKind.String ||
            urlElement.GetString() is not { Length: > 0 } url) return null;

        string vcodec = StringOrNull(format, "vcodec") ?? "none";
        string acodec = StringOrNull(format, "acodec") ?? "none";
        if (acodec == "none") return null; // video-only（需混流）或 storyboard：NetTrans 不做混流器

        string ext = StringOrNull(format, "ext") ?? "bin";
        int? height = HeightOf(format) is { } h && h > 0 ? h : null;
        bool audioOnly = vcodec == "none";

        string label = audioOnly
            ? $"音频 {ext}"
            : height is { } quality
                ? $"{quality}p {ext}"
                : StringOrNull(format, "format_note") ?? StringOrNull(format, "format_id") ?? $"视频 {ext}";

        return new SiteStream(url, label, SizeOf(format), HeadersOf(format));
    }

    private static string? StringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int HeightOf(JsonElement format) =>
        format.TryGetProperty("height", out var height) &&
        height.ValueKind == JsonValueKind.Number &&
        height.TryGetInt32(out var value) ? value : 0;

    /// <summary>声明大小：filesize 优先，filesize_approx 兜底（yt-dlp 常缺精确值）。</summary>
    private static long SizeOf(JsonElement format)
    {
        if (format.TryGetProperty("filesize", out var exact) &&
            exact.ValueKind == JsonValueKind.Number && exact.TryGetInt64(out var size)) return size;
        if (format.TryGetProperty("filesize_approx", out var approx) &&
            approx.ValueKind == JsonValueKind.Number && approx.TryGetInt64(out var estimate)) return estimate;
        return 0;
    }

    /// <summary>http_headers → 站点 CDN 头：只取 Referer / User-Agent（其余头由任务/全局策略决定）。</summary>
    private static IReadOnlyDictionary<string, string>? HeadersOf(JsonElement format)
    {
        if (!format.TryGetProperty("http_headers", out var headers) || headers.ValueKind != JsonValueKind.Object)
            return null;

        Dictionary<string, string>? result = null;
        foreach (var header in headers.EnumerateObject())
        {
            if (header.Value.ValueKind != JsonValueKind.String) continue;
            if (header.NameEquals("Referer") || header.NameEquals("User-Agent"))
                (result ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[header.Name] =
                    header.Value.GetString()!;
        }
        return result;
    }

    // ── 默认 runner：从 PATH 运行 yt-dlp（净解析，不下载字节） ─────────────

    private static async Task<string?> RunYtDlpAsync(string url, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo("yt-dlp")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--dump-single-json");
        start.ArgumentList.Add("--no-playlist");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(url);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch
        {
            return null; // 未安装等启动失败 → 诚实降级（框架回退协议探测）
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            // 暂停/移除不能把 yt-dlp 进程留在后台
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        string errorText = await stderr;
        if (process.ExitCode != 0)
        {
            var brief = errorText.Split('\n', 2)[0].TrimEnd('\r').Trim();
            throw new FormatException(brief.Length > 0
                ? $"yt-dlp 退出码 {process.ExitCode}：{brief}"
                : $"yt-dlp 退出码 {process.ExitCode}");
        }

        return await stdout;
    }
}
