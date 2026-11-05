namespace NetTrans.Kernel;

/// <summary>
/// 文件名模板（P2-3）：完成前按模板重命名，如 "{站点}/{标题}.{ext}"。
/// 占位符：{站点}=主机名、{标题}=去扩展名的文件名、{ext}=扩展名（不含点）。
/// "/" 作为子目录分隔符，每段内的 Windows 非法字符替换为 "_"（冲突策略
/// 仍由内核既有碰撞命名处理）。未识别的 {占位符} 原样保留。模板空白或
/// 渲染为空 → null，调用方回退默认命名。
/// </summary>
public static class FileNameTemplate
{
    public static string? Render(string? template, string fileName, string host)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        string title = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName).TrimStart('.');

        var segments = new List<string>();
        foreach (string raw in template.Split('/'))
        {
            string segment = raw
                .Replace("{站点}", host)
                .Replace("{标题}", title)
                .Replace("{ext}", ext);
            segment = SanitizeSegment(segment);
            if (segment.Length > 0) segments.Add(segment);
        }

        return segments.Count > 0 ? Path.Combine(segments.ToArray()) : null;
    }

    // Explicit Windows-invalid set instead of Path.GetInvalidFileNameChars():
    // on Unix that API only returns control chars, and the rendered name must
    // be Windows-safe regardless of where the kernel runs. '/' is handled as
    // the segment separator and never reaches here.
    private static readonly char[] InvalidChars = ['"', '<', '>', '|', ':', '*', '?', '\\', '\0'];

    private static string SanitizeSegment(string segment)
    {
        return new string(segment.Select(c => InvalidChars.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
