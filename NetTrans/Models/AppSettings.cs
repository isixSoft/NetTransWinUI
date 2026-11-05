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

    /// <summary>P0-5 站点提取兜底开关：开启后，内置站点不认领的 http(s) 页面交给
    /// yt-dlp --dump-single-json 解析（需已安装 yt-dlp）。默认关，避免未装二进制
    /// 时每次都走一次诚实降级路径。</summary>
    public bool EnableYtDlp { get; set; } = false;

    /// <summary>P1-6 / BT / eD2k 外部引擎命令模板。占位符：{url}（任务 URL）、
    /// {path}（解析后的落盘路径）、{name}（任务名）、{files}（--select-file
    /// 索引列表，P1-6 BT per-file 勾选时由内核自动展开）。空/空白 → null，
    /// 内核此时只认 EngineRunner 委托通路。</summary>
    public string? ExternalEngineCommand { get; set; }

    // 行为
    public bool WatchClipboard { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;

    /// <summary>Mark-of-the-Web（P2-4）：完成时写 Zone.Identifier 来源标记，可关闭。</summary>
    public bool MarkOfTheWeb { get; set; } = true;
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

    /// <summary>P2-9 多语言：auto | en | zh-CN；启动时设 PrimaryLanguageOverride，
    /// 切换生效时机与 shell 约定一致 = 下次启动。shell 专属，不映射进内核。</summary>
    public string Language { get; set; } = "auto";
}
