using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NetTrans.Interop;
using NetTrans.Kernel;
using NetTrans.Services;
using NetTrans.ViewModels;
using NetTrans.Views.Controls;

namespace NetTrans.Views;

/// <summary>
/// The inspector frame: 概览 / 分块 / 连接 / 日志 over the currently selected
/// task, plus the blue 服务器上有更新版本 notice when the server holds a newer
/// build.
/// </summary>
public sealed partial class InspectorShell : UserControl
{
    private readonly List<SegmentItem> _tabs = new()
    {
        new SegmentItem("info", "概览"),
        new SegmentItem("blocks", "分块"),
        new SegmentItem("conn", "连接"),
        new SegmentItem("log", "日志"),
    };

    private ShellViewModel? _viewModel;
    private DownloadItemViewModel? _item;
    private WindowChrome? _chrome;
    private string _tab = "info";

    /// <summary>Raised by the close button in the inspector's nav bar.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised by the 已吸附 / 已分离 chip.</summary>
    public event EventHandler? AttachToggleRequested;

    public ShellViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(_viewModel, value)) return;
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnShellPropertyChanged;

            _viewModel = value;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnShellPropertyChanged;

            BindItem(_viewModel?.Current);
        }
    }

    public InspectorShell()
    {
        InitializeComponent();
        Tabs.Items = _tabs;
        Tabs.SelectedId = _tab;
        SizeChanged += (_, e) =>
        {
            RootScale.CenterX = e.NewSize.Width / 2;
            RootScale.CenterY = e.NewSize.Height / 2;
        };
    }

    public void AttachChrome(WindowChrome chrome) => _chrome = chrome;

    /// <summary>Reflects the current dock state on the 已吸附 / 已分离 chip.</summary>
    public void SetDocked(bool docked)
    {
        AttachLabel.Text = docked ? "已吸附" : "已分离";
        AttachGlyph.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        AttachChip.Background = ThemeBrushes.Get(docked ? "BlueWash12Brush" : "FillBrush");
        AttachChip.Foreground = ThemeBrushes.Get(docked ? "BlueBrush" : "Label2Brush");
        ToolTipService.SetToolTip(AttachChip, docked ? "点击分离" : "点击吸附到主窗口");
    }

    /// <summary>Plays `snapIn` (.982 -> 1.006 -> 1) after the frame settles flush.</summary>
    public void PlaySnapIn() => Animations.SnapIn(RootScale).Begin();

    // ── nav ───────────────────────────────────────────────────────────────
    private void OnNavPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsWithinInteractive(e.OriginalSource as DependencyObject)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _chrome?.BeginDrag();
        e.Handled = true;
    }

    private static bool IsWithinInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBox or ComboBox) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnAttachClick(object sender, RoutedEventArgs e) => AttachToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnTabChanged(object? sender, string tab)
    {
        _tab = tab;
        OverviewTab.Visibility = tab == "info" ? Visibility.Visible : Visibility.Collapsed;
        BlocksTab.Visibility = tab == "blocks" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsTab.Visibility = tab == "conn" ? Visibility.Visible : Visibility.Collapsed;
        LogTab.Visibility = tab == "log" ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    // ── data ──────────────────────────────────────────────────────────────
    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Current)) BindItem(_viewModel?.Current);
    }

    private void BindItem(DownloadItemViewModel? item)
    {
        if (_item is not null) _item.PropertyChanged -= OnItemPropertyChanged;
        _item = item;
        if (_item is not null) _item.PropertyChanged += OnItemPropertyChanged;

        Refresh();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        bool hasItem = _item is not null;
        EmptyState.Visibility = hasItem ? Visibility.Collapsed : Visibility.Visible;

        if (!hasItem)
        {
            NewVersionNotice.Visibility = Visibility.Collapsed;
            OverviewTab.Visibility = Visibility.Collapsed;
            BlocksTab.Visibility = Visibility.Collapsed;
            ConnectionsTab.Visibility = Visibility.Collapsed;
            LogTab.Visibility = Visibility.Collapsed;
            return;
        }

        var item = _item!;

        NewVersionNotice.Visibility = item.HasNewerVersion ? Visibility.Visible : Visibility.Collapsed;
        NewVersionSubtitle.Text = item.NewVersionSubtitle;

        if (_tab == "info") RefreshOverview(item);
        else if (_tab == "blocks") RefreshBlocks(item);
        else if (_tab == "conn") RefreshConnections(item);
        else RefreshLog(item);

        OverviewTab.Visibility = _tab == "info" ? Visibility.Visible : Visibility.Collapsed;
        BlocksTab.Visibility = _tab == "blocks" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsTab.Visibility = _tab == "conn" ? Visibility.Visible : Visibility.Collapsed;
        LogTab.Visibility = _tab == "log" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshOverview(DownloadItemViewModel item)
    {
        Ring.Fraction = item.Fraction;
        Ring.Percent = item.PercentText;
        Ring.Subtitle = item.RingSubtitle;
        Ring.ArcBrush = item.RingBrush;
        RingCaption.Text = item.RingCaption;

        StatusRow.Value = item.StatusText;
        StatusRow.IsError = item.IsError;
        HostRow.Value = item.Host;
        ConnectionsRow.Value = item.ConnectionsText;
        SpeedRow.Value = item.SpeedText;

        // BT tasks trade the 校验 row for 节点 / 种子, exactly as the prototype does.
        ChecksumRow.Label = item.IsBitTorrent ? "节点 / 种子" : "校验";
        ChecksumRow.Value = item.IsBitTorrent ? item.PeersSeedsText : item.ChecksumText;

        AddedRow.Value = item.AddedAt;
        SavePathRow.Value = item.SavePath;

        PriorityHigh.IsChecked = item.Priority == TaskPriority.High;
        PriorityNormal.IsChecked = item.Priority == TaskPriority.Normal;
        PriorityLow.IsChecked = item.Priority == TaskPriority.Low;

        SelectLimit(item.SpeedLimit);
        UrlText.Text = item.Url;
    }

    private void RefreshBlocks(DownloadItemViewModel item)
    {
        BlocksHeader.Text = $"分块 · {item.Blocks.Length}";
        Blocks.SetBlocks(item.Blocks);
        Session.SetSamples(item.SpeedHistory);

        AverageRow.Value = item.AverageSpeedText;
        PeakRow.Value = item.PeakSpeedText;
        RetryRow.Value = item.RetriesText;
    }

    private void RefreshConnections(DownloadItemViewModel item)
    {
        ConnectionsHeader.Text = $"连接 · {item.ConnectionSpeeds.Length}";
        Connections.SetConnections(item.ConnectionSpeeds);

        TorrentGroup.Visibility = item.IsBitTorrent ? Visibility.Visible : Visibility.Collapsed;
        if (!item.IsBitTorrent) return;

        PeersRow.Value = item.PeersText;
        SeedsRow.Value = item.SeedsText;
        RatioRow.Value = item.RatioText;
        UploadRow.Value = item.UploadText;
    }

    private void RefreshLog(DownloadItemViewModel item)
    {
        // The engine appends to the log; re-rendering unchanged rows every tick
        // would reset scrolling and flicker.
        if (LogList.Children.Count == item.Log.Count) return;
        LogList.Children.Clear();

        foreach (var entry in item.Log)
        {
            var row = new Grid { Padding = new Thickness(11, 5, 11, 5), ColumnSpacing = 9 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = entry.Time,
                Style = (Style)Application.Current.Resources["MetaDimTextStyle"],
            });

            var message = new TextBlock
            {
                Text = entry.Message,
                Style = (Style)Application.Current.Resources["MetaTextStyle"],
                LineHeight = 16.2,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrushes.Get(entry.IsError ? "RedBrush" : "Label2Brush"),
            };
            Grid.SetColumn(message, 1);
            row.Children.Add(message);

            LogList.Children.Add(row);
        }
    }

    private void OnPriorityClick(object sender, RoutedEventArgs e)
    {
        if (_item is null || sender is not ToggleButton button || button.Tag is not string tag) return;
        _viewModel?.Manager.SetPriority(_item.Id, Enum.Parse<TaskPriority>(tag));
        RefreshOverview(_item);
    }

    private void OnLimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_item is null || LimitBox.SelectedItem is not string limit) return;
        _viewModel?.Manager.SetSpeedLimit(_item.Id, limit);
    }

    private void SelectLimit(string limit)
    {
        for (int i = 0; i < LimitBox.Items.Count; i++)
        {
            if (LimitBox.Items[i] as string != limit) continue;
            LimitBox.SelectedIndex = i;
            return;
        }

        LimitBox.SelectedIndex = 0;
    }

    private void OnRedownloadClick(object sender, RoutedEventArgs e)
    {
        if (_item is not null) _viewModel?.RedownloadCommand.Execute(_item);
    }
}
