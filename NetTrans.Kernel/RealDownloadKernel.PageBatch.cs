using NetTrans.Kernel.Protocol;
using NetTrans.Kernel.Sites;

namespace NetTrans.Kernel;

/// <summary>
/// 站点批量抓取（P2-6）：列表页/主页 URL 一次解析出多条任务草稿并全部入队。
/// 批量解析交给站点提取器框架（GenericPageSite 兜底：页面上每个候选各成
/// 一条草稿）；只有实现 ExtractDraftsAsync 的提取器参与，其余认领者跳过。
/// </summary>
public sealed partial class RealDownloadKernel
{
    public async Task<IReadOnlyList<DownloadTask>> AddPageBatchAsync(
        string url, string saveTo, TaskPriority priority = TaskPriority.Normal, bool StartNow = false)
    {
        var drafts = await _drivers.ExtractDraftsAsync(url, null, null, CancellationToken.None);

        var tasks = new List<DownloadTask>(drafts.Count);
        foreach (var draft in drafts)
        {
            var stream = draft.Streams[0]; // 草稿的最优（第 0 条）流即下载目标
            tasks.Add(Add(new NewDownloadRequest(stream.Url, saveTo, "", 2, priority, StartNow,
                Headers: stream.Headers, SourcePage: url)));
        }
        return tasks;
    }
}
