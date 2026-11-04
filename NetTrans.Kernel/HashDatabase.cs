using System.Text.Json;

namespace NetTrans.Kernel;

/// <summary>
/// 本地签名库（「每个下载过的文件都留指纹」）：
/// 完成的下载以 URL 为键记录算法+指纹+大小+时间，JSON 落盘；
/// 再次下载同源时内核据此自动比对，内容被篡改/换源立刻可见。
/// 损坏的库文件降级为空库；写盘失败不影响下载主流程。
/// </summary>
public sealed class HashDatabase
{
    /// <summary>一条历史指纹记录。</summary>
    public sealed record Entry(string Url, string FileName, long Size, string Algorithm, string Hash, string CompletedAt);

    private readonly object _gate = new();
    private readonly string _path;                                  // 空 = 只进内存，不落盘
    private Dictionary<string, Entry> _entries;                     // key: url 原样

    public HashDatabase(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path) ? string.Empty : path;
        _entries = Load(_path);
    }

    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>查同 URL 的历史指纹；没有返回 null。</summary>
    public Entry? Find(string url)
    {
        lock (_gate) return _entries.TryGetValue(url, out var hit) ? hit : null;
    }

    /// <summary>记录（或覆盖）一条指纹并立刻落盘。</summary>
    public void Record(Entry entry)
    {
        lock (_gate)
        {
            _entries[entry.Url] = entry;
            SaveLocked();
        }
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (path.Length == 0 || !File.Exists(path)) return new Dictionary<string, Entry>();
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path))
                   ?? new Dictionary<string, Entry>();
        }
        catch
        {
            // 半写/损坏 JSON：签名库宁缺毋滥，降级为空库重建
            return new Dictionary<string, Entry>();
        }
    }

    private void SaveLocked()
    {
        if (_path.Length == 0) return;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonIndented));
        }
        catch
        {
            // 签名库持久化失败只丢指纹记录，不能拖垮下载主流程
        }
    }

    private static readonly JsonSerializerOptions JsonIndented = new() { WriteIndented = true };
}
