namespace NetTrans.Kernel;

/// <summary>
/// 批量导入解析（P1-7）：每行一条；空行与 # 开头的注释行跳过，行首尾空白容忍，
/// CRLF/LF 均可。来源不限——txt 文件内容或剪贴板多行文本走同一条路径。
/// </summary>
public static class BatchImport
{
    public static IReadOnlyList<string> ParseLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var urls = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            urls.Add(line);
        }

        return urls;
    }
}
