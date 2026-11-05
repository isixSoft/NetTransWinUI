namespace NetTrans.Models;

/// <summary>
/// Everything the 设置 sheet exposes, plus the shell toggles that live in the
/// tray menu. Persisted next to the executable in portable mode -- the sheet's
/// own footnote promises no registry writes.
/// </summary>
public sealed class AppSettings
{
    // 下载
    public string DefaultSavePath { get; set; } = @"D:\Downloads";
    public bool FoldersByCategory { get; set; } = true;

    /// <summary>文件名模板（P2-3），如 "{站点}/{标题}.{ext}"；空 = 默认命名。</summary>
    public string NameTemplate { get; set; } = "";

    public int MaxSimultaneousDownloads { get; set; } = 3;

    /// <summary>Global cap as shown in the dropdown ("不限", "4 MB/s", ...).</summary>
    public string GlobalSpeedLimit { get; set; } = "不限";
    public bool UncappedAtNight { get; set; }

    /// <summary>命名限速档位（P2-2）；空列表 = 使用 SpeedProfileService.Defaults 出厂档位。</summary>
    public List<SpeedProfile> SpeedProfiles { get; set; } = new();

    /// <summary>当前生效的档位名（「全速」= 不限）。</summary>
    public string ActiveSpeedProfile { get; set; } = "全速";

    // 队列与计划
    public string OffPeakStart { get; set; } = "23:00";
    public string OffPeakEnd { get; set; } = "07:00";
    public string RetryPolicy { get; set; } = "3 次";

    /// <summary>无操作 | 退出程序 | 休眠 | 关机</summary>
    public string WhenAllComplete { get; set; } = "无操作";

    // 行为
    public bool WatchClipboard { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
    public bool VerifyChecksums { get; set; } = true;
    public bool ScanOnCompletion { get; set; } = true;
    public bool EdgeHide { get; set; } = true;
    public string BossKey { get; set; } = "Ctrl + Alt + H";

    // shell state the tray menu and 显示与排序 menu remember
    public bool ShowIsland { get; set; } = true;
    public bool ShowInspector { get; set; } = true;
    public bool DenseRows { get; set; }
    public string SortKey { get; set; } = "added";
    public string SortDirection { get; set; } = "asc";
    public string Theme { get; set; } = "auto"; // light | dark | auto
}
