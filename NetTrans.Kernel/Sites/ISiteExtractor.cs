using NetTrans.Kernel.Protocol;

namespace NetTrans.Kernel.Sites;

/// <summary>站点为一条内容提供的单条流：直链 URL + 画质标签 + CDN 所需头。</summary>
/// <param name="Url">绝对直链（媒体文件 / m3u8 / mpd），由协议驱动接手探测与传输。</param>
/// <param name="Label">画质标签（"720P 高清" / "MP4 视频"…），仅用于日志与展示。</param>
/// <param name="Size">声明大小（字节）；可空——直链探测会再确认。</param>
/// <param name="Headers">该 CDN 需要的请求头（Referer/UA 等）；探测与传输会话都会携带。</param>
public sealed record SiteStream(
    string Url,
    string Label,
    long? Size = null,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>站点提取结果：页面标题 + 全部候选流（最优在前，第 0 条默认选用）。</summary>
public sealed record SiteMedia(string Site, string Title, IReadOnlyList<SiteStream> Streams);

/// <summary>
/// 一个被支持的站点：认领页面 URL 并解析出直链流。一站点一文件——站点改版
/// 只更新对应提取器，框架 / 协议驱动 / 内核零改动。提取器不传输字节；
/// 框架会用匹配的协议驱动（HTTP/HLS/DASH）对直链再探测并生成传输计划。
/// </summary>
public interface ISiteExtractor
{
    /// <summary>站点名（日志与 <see cref="KernelOptions.SiteApiBases"/> 覆写键）。</summary>
    string Name { get; }

    /// <summary>该 URL 是否属于本站（纯判别，无 I/O）。</summary>
    bool CanHandle(string url);

    /// <summary>
    /// 解析页面为直链流（最优在前）。返回 null = 放弃认领（框架回退协议探测）；
    /// 抛异常 = 站点解析失败（框架记日志后同样回退协议探测）。
    /// </summary>
    Task<SiteMedia?> ExtractAsync(string url, DriverContext context, CancellationToken cancellation);

    /// <summary>
    /// 站点批量抓取（P2-6）：列表页/主页一次解析出多条任务草稿，每条
    /// <see cref="SiteMedia"/> 是一个独立媒体（其第 0 条流即下载目标）。
    /// 返回 null = 不支持批量（默认），框架尝试下一个认领者。
    /// </summary>
    Task<IReadOnlyList<SiteMedia>?> ExtractDraftsAsync(string url, DriverContext context, CancellationToken cancellation)
        => Task.FromResult<IReadOnlyList<SiteMedia>?>(null);
}
