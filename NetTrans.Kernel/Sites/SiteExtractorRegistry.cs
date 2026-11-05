namespace NetTrans.Kernel.Sites;

/// <summary>
/// 站点提取器表：一个流行站点一个实现（一文件一站点），按注册顺序取第一个
/// CanHandle 命中的提取器。新增站点 = 写一个 ISiteExtractor 并注册，框架不动。
/// </summary>
public sealed class SiteExtractorRegistry
{
    private readonly List<ISiteExtractor> _sites = new();

    public void Register(ISiteExtractor site) => _sites.Add(site);

    /// <summary>第一个声称能处理该 URL 的提取器；无人认领返回 null。</summary>
    public ISiteExtractor? Find(string url)
    {
        foreach (var site in _sites)
            if (site.CanHandle(url)) return site;
        return null;
    }

    /// <summary>所有声称能处理该 URL 的提取器（注册序）：放弃认领（ExtractAsync
    /// 返回 null）时框架应依次尝试下一个，全部放弃才回退协议探测。</summary>
    public IEnumerable<ISiteExtractor> FindAll(string url) => _sites.Where(s => s.CanHandle(url));
}
