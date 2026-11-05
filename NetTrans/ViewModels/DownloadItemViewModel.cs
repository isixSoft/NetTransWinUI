using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NetTrans.Kernel;
using NetTrans.Services;

namespace NetTrans.ViewModels;

/// <summary>
/// One task, in the shape the list row and the inspector bind to. Every derived
/// string here is the handoff's own expression for that string -- see Row() and
/// Inspector() in mini-ios2.jsx.
/// </summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    /// <summary>The kernel task this row mirrors. Owned by the kernel; read-only here.</summary>
    public DownloadTask Model { get; }

    public DownloadItemViewModel(DownloadTask model)
    {
        Model = model;
        _done = model.Done;
        _speed = model.Speed;
        _status = model.Status;
        _connections = model.Connections;
        _priority = model.Priority;
        _speedLimit = model.SpeedLimit;
    }

    public int Id => Model.Id;
    public string Name => Model.Name;
    public string Host => Model.Host;
    public FileKind Kind => Model.Kind;
    public long Size => Model.Size;
    public string Category => Model.Category;
    public string Url => Model.Url;
    public string SavePath => Model.SavePath;
    public string AddedAt => Model.AddedAt;
    public int Retries => Model.Retries;
    public int[] Blocks => Model.Blocks;
    public double[] ConnectionSpeeds => Model.ConnectionSpeeds;
    public double[] SpeedHistory => Model.SpeedHistory;
    public IReadOnlyList<LogEntry> Log => Model.Log;

    [ObservableProperty] private long _done;
    [ObservableProperty] private double _speed;
    [ObservableProperty] private DownloadStatus _status;
    [ObservableProperty] private int _connections;
    [ObservableProperty] private TaskPriority _priority;
    [ObservableProperty] private string _speedLimit;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Set when the server holds a newer build; the kernel clears it on redownload.</summary>
    public NewVersionInfo? NewerVersion => Model.NewerVersion;

    public string? Checksum => Model.Checksum;

    public string? ErrorMessage => Model.ErrorMessage;

    // ── derived: state ────────────────────────────────────────────────────
    public bool IsDone => Status == DownloadStatus.Completed;
    public bool IsRunning => Status == DownloadStatus.Downloading;
    public bool IsError => Status == DownloadStatus.Error;
    public bool IsQueued => Status == DownloadStatus.Queued;
    public bool IsPaused => Status == DownloadStatus.Paused;
    public bool IsBitTorrent => Model.Peers is not null;
    public bool HasNewerVersion => Model.NewerVersion is not null;
    public bool IsHighPriority => Priority == TaskPriority.High;

    /// <summary>STATE_CN in the handoff.</summary>
    public string StatusText => Status switch
    {
        DownloadStatus.Downloading => "下载中",
        DownloadStatus.Paused => "已暂停",
        DownloadStatus.Completed => "已完成",
        DownloadStatus.Error => "出错",
        _ => "排队中",
    };

    public double Percent => Size <= 0 ? 0 : Math.Clamp(Done / (double)Size * 100.0, 0, 100);
    public double Fraction => Size <= 0 ? 0 : Math.Clamp(Done / (double)Size, 0, 1);

    // ── derived: row ──────────────────────────────────────────────────────
    public string SubText => Status switch
    {
        DownloadStatus.Completed => Checksum is { Length: > 0 } c
            ? $"{FormatHelpers.Bytes(Size)} · {c}"
            : FormatHelpers.Bytes(Size),
        DownloadStatus.Error => $"{ErrorMessage} · 已重试 {Retries} 次",
        DownloadStatus.Queued => "排队中，等待空闲通道",
        DownloadStatus.Paused => $"已暂停 · {FormatHelpers.Bytes(Done)} / {FormatHelpers.Bytes(Size)}",
        _ => $"{FormatHelpers.Speed(Speed)} · {FormatHelpers.Eta(Size - Done, Speed)}",
    };

    /// <summary>The row's trailing readout: 完成 badge, 失败, an em dash while queued, or the percentage.</summary>
    public string TrailingText => Status switch
    {
        DownloadStatus.Completed => "完成",
        DownloadStatus.Error => "失败",
        DownloadStatus.Queued => "—",
        _ => $"{Percent:0}%",
    };

    /// <summary>The progress track is hidden for finished and not-yet-started tasks.</summary>
    public bool ShowProgress => Status is not (DownloadStatus.Completed or DownloadStatus.Queued);

    /// <summary>Paused rows draw the fill in --gray instead of --blue.</summary>
    public Brush ProgressBrush => Resource(IsPaused ? "GrayBrush" : "BlueBrush");

    public Brush SubBrush => Resource(IsError ? "RedBrush" : "Label2Brush");

    public Brush TileBrush => new SolidColorBrush(ParseColor(Model.Tint));

    public Geometry KindGlyph => Glyph(Kind switch
    {
        FileKind.Disc => "IconDisc",
        FileKind.Film => "IconFilm",
        FileKind.Zip => "IconZip",
        FileKind.Music => "IconMusic",
        _ => "IconDoc",
    });

    /// <summary>The hover action's label: 暂停 while running, 重试 after a failure, otherwise 继续.</summary>
    public string ToggleLabel => Status switch
    {
        DownloadStatus.Downloading => "暂停",
        DownloadStatus.Error => "重试",
        _ => "继续",
    };

    public Geometry ToggleGlyph => Glyph(IsRunning ? "IconPauseFill" : "IconPlayFill");

    // ── derived: inspector ────────────────────────────────────────────────
    public Brush RingBrush => Resource(Status switch
    {
        DownloadStatus.Error => "RedBrush",
        DownloadStatus.Completed => "GreenBrush",
        DownloadStatus.Downloading => "BlueBrush",
        _ => "GrayBrush",
    });

    /// <summary>Ring centre subtitle: live speed, falling back to the state name when stalled.</summary>
    public string RingSubtitle => FormatHelpers.Speed(Speed) is { Length: > 0 } s ? s : StatusText;

    public string RingCaption =>
        $"{FormatHelpers.Bytes(Done)} / {FormatHelpers.Bytes(Size)} · " +
        (IsDone ? "已完成" : FormatHelpers.Eta(Size - Done, Speed));

    public string PercentText => $"{Percent:0}";
    public string ConnectionsText => Connections > 0 ? Connections.ToString() : "—";
    public string SpeedText => FormatHelpers.SpeedOrDash(Speed);
    public string ChecksumText => Checksum is { Length: > 0 } c ? c : "未启用";
    public string PeersSeedsText => $"{Model.Peers} / {Model.Seeds}";
    public string PeersText => Model.Peers?.ToString() ?? "—";
    public string SeedsText => Model.Seeds?.ToString() ?? "—";
    public string RatioText => Model.Ratio?.ToString("0.00") ?? "—";
    public string UploadText => FormatHelpers.SpeedOrDash(Model.UploadSpeed);
    public string RetriesText => $"{Retries} 次";
    public string AverageSpeedText => SpeedHistory.Length == 0
        ? FormatHelpers.SpeedOrDash(Speed * 0.82)
        : FormatHelpers.SpeedOrDash(SpeedHistory.Average());
    public string PeakSpeedText => FormatHelpers.SpeedOrDash(Math.Max(Model.PeakSpeed, Speed));

    public string NewVersionSubtitle => Model.NewerVersion is { } n
        ? $"{n.Version} · {FormatHelpers.Bytes(n.Size)} · 发布于 {n.Published}"
        : "";

    /// <summary>Called by the task manager after the kernel mutates the task, to refresh every derived readout at once.</summary>
    public void Refresh()
    {
        Done = Model.Done;
        Speed = Model.Speed;
        Status = Model.Status;
        Connections = Model.Connections;
        Priority = Model.Priority;
        SpeedLimit = Model.SpeedLimit;

        // the real kernel refines these after its probe
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(Size));

        OnPropertyChanged(nameof(Blocks));
        OnPropertyChanged(nameof(ConnectionSpeeds));
        OnPropertyChanged(nameof(SpeedHistory));
        OnPropertyChanged(nameof(Log));
        OnPropertyChanged(nameof(AverageSpeedText));
        OnPropertyChanged(nameof(PeakSpeedText));
        OnPropertyChanged(nameof(NewerVersion));
        OnPropertyChanged(nameof(HasNewerVersion));
        OnPropertyChanged(nameof(NewVersionSubtitle));
        OnPropertyChanged(nameof(ChecksumText));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(SubText));
    }

    partial void OnDoneChanged(long value) => RaiseDerived();
    partial void OnSpeedChanged(double value) => RaiseDerived();
    partial void OnConnectionsChanged(int value) => RaiseDerived();

    partial void OnStatusChanged(DownloadStatus value)
    {
        RaiseDerived();
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ProgressBrush));
        OnPropertyChanged(nameof(SubBrush));
        OnPropertyChanged(nameof(RingBrush));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(ToggleGlyph));
    }

    partial void OnPriorityChanged(TaskPriority value) => OnPropertyChanged(nameof(IsHighPriority));

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(SubText));
        OnPropertyChanged(nameof(TrailingText));
        OnPropertyChanged(nameof(RingSubtitle));
        OnPropertyChanged(nameof(RingCaption));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(ConnectionsText));
        OnPropertyChanged(nameof(SpeedText));
    }

    private static Brush Resource(string key) => ThemeBrushes.Get(key);

    private static Geometry Glyph(string key) => (Geometry)Application.Current.Resources[key];

    private static Windows.UI.Color ParseColor(string hex)
    {
        string h = hex.TrimStart('#');
        byte r = Convert.ToByte(h.Substring(0, 2), 16);
        byte g = Convert.ToByte(h.Substring(2, 2), 16);
        byte b = Convert.ToByte(h.Substring(4, 2), 16);
        return Windows.UI.Color.FromArgb(0xFF, r, g, b);
    }
}
