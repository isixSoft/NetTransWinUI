using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NetTrans.Interop;
using NetTrans.Services;
using NetTrans.ViewModels;
using NetTrans.Views.Controls;
using NetTrans.Views.Sheets;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace NetTrans.Views;

/// <summary>
/// The task frame. Owns the nav bar, the segmented control, the folded list,
/// the seven-icon toolbar and every in-frame overlay (sheets, popovers, toast,
/// completion banner, drop target).
/// </summary>
public sealed partial class MainShell : UserControl
{
    private readonly List<SegmentItem> _tabs = new()
    {
        new SegmentItem("all", "全部"),
        new SegmentItem("active", "进行中 0"),
        new SegmentItem("done", "已完成 0"),
    };

    private ShellViewModel? _viewModel;
    private WindowChrome? _chrome;
    private PopoverControl? _popover;
    private FrameworkElement? _sheet;

    /// <summary>Raised when the red traffic light is clicked.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the yellow traffic light is clicked.</summary>
    public event EventHandler? MinimizeRequested;

    public ShellViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(_viewModel, value)) return;
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

            _viewModel = value;

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                Tabs.SelectedId = _viewModel.Tab;
            }

            Bindings.Update();
            SyncTabs();
            SyncToggleAllGlyph();
            SyncViewMenuAct();
        }
    }

    public MainShell()
    {
        InitializeComponent();
        Tabs.Items = _tabs;
        Toast.Opacity = 0;
    }

    /// <summary>Handed the frame's chrome so the nav bar can start an OS drag.</summary>
    public void AttachChrome(WindowChrome chrome) => _chrome = chrome;

    // ── nav bar ───────────────────────────────────────────────────────────
    private void OnNavPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // `.frame .nav { cursor: grab }` -- but not over the buttons sitting in it.
        if (IsWithinInteractive(e.OriginalSource as DependencyObject)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _chrome?.BeginDrag();
        e.Handled = true;
    }

    private static bool IsWithinInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBox) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void OnCloseDotPressed(object sender, PointerRoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnMinimizeDotPressed(object sender, PointerRoutedEventArgs e)
    {
        MinimizeRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>
    /// The frame is a fixed 536x680, so there is nothing to zoom to. The green
    /// dot opens and closes the inspector frame instead -- the only thing that
    /// changes the shell's footprint.
    /// </summary>
    private void OnZoomDotPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.ShowInspector = !ViewModel.ShowInspector;
        e.Handled = true;
    }

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleSearchCommand.Execute(null);
        if (ViewModel?.IsSearchOpen == true) DispatcherQueue.TryEnqueue(() => NavSearch.Focus(FocusState.Programmatic));
    }

    private void OnSearchLostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { Query.Length: 0, IsSearchOpen: true }) ViewModel.IsSearchOpen = false;
    }

    private void OnTabChanged(object? sender, string tab)
    {
        if (ViewModel is not null) ViewModel.Tab = tab;
    }

    // ── list ──────────────────────────────────────────────────────────────
    private void OnRowPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not TaskRow row) return;

        row.IsDense = ViewModel?.DenseRows == true;
        row.ShowSeparator = args.Index > 0;

        // Repeater elements are recycled, so subscribe once per instance.
        if (row.Tag is not null) return;
        row.Tag = "wired";

        row.RowInvoked += (_, e) => ViewModel?.Select(e.Item.Id, e.Additive);
        row.ToggleRequested += (_, item) => ViewModel?.ToggleTaskCommand.Execute(item);
        row.RemoveRequested += (_, item) => ViewModel?.RemoveTaskCommand.Execute(item);
        row.RowContextRequested += (_, request) => ShowContextMenu(request);
    }

    private void RefreshRowDensity()
    {
        int count = Rows.ItemsSourceView?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            if (Rows.TryGetElement(i) is TaskRow row)
            {
                row.IsDense = ViewModel?.DenseRows == true;
                row.ShowSeparator = i > 0;
            }
        }
    }

    // ── menus ─────────────────────────────────────────────────────────────
    private void OnAddMenuClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        ShowPopover(new[]
        {
            new PopoverItem("新建下载…", Glyph("IconPlus"), Invoke: () => ViewModel.ActiveSheet = "add"),
            new PopoverItem("批量下载…", Glyph("IconLayers"), Invoke: () => ViewModel.ActiveSheet = "batch"),
            new PopoverItem("打开种子 / 磁力链…", Glyph("IconMagnet"), Invoke: () => ViewModel.ActiveSheet = "torrent"),
            new PopoverItem("视频嗅探…", Glyph("IconFilm"), Invoke: () => ViewModel.ActiveSheet = "sniff"),
        }, new Point(536 - 222 - 12, 40), width: 222);
    }

    private void OnViewMenuClick(object sender, RoutedEventArgs e) => ShowViewMenu(page: "view");

    private void ShowViewMenu(string page)
    {
        if (ViewModel is null) return;

        var items = new List<PopoverItem>();

        if (page == "view")
        {
            items.Add(new PopoverItem("大图", Glyph("IconRows"), IsChecked: !ViewModel.DenseRows,
                Invoke: () => ViewModel.DenseRows = false));
            items.Add(new PopoverItem("小图", Glyph("IconGrid"), IsChecked: ViewModel.DenseRows,
                Invoke: () => ViewModel.DenseRows = true));

            bool first = true;
            foreach (var (key, label) in new[]
                     {
                         ("added", "加入时间"), ("name", "名称"), ("size", "大小"),
                         ("progress", "进度"), ("speed", "速度"),
                     })
            {
                bool active = ViewModel.SortKey == key;
                var arrow = active
                    ? Glyph(ViewModel.SortDirection == "asc" ? "IconArrowUp" : "IconArrowDown")
                    : null;

                items.Add(new PopoverItem(label, arrow, SeparatorBefore: first,
                    Invoke: () => { ViewModel.SetSortCommand.Execute(key); ShowViewMenu("view"); },
                    KeepOpen: true));
                first = false;
            }

            items.Add(new PopoverItem("分类筛选", Glyph("IconChevron"), SeparatorBefore: true,
                Invoke: () => ShowViewMenu("cat"), KeepOpen: true));
        }
        else
        {
            items.Add(new PopoverItem("‹ 返回", Invoke: () => ShowViewMenu("view"), KeepOpen: true));

            bool first = true;
            foreach (var (id, label) in ShellViewModel.Categories)
            {
                items.Add(new PopoverItem(label, IsChecked: ViewModel.Category == id, SeparatorBefore: first,
                    Invoke: () => ViewModel.Category = id));
                first = false;
            }
        }

        ShowPopover(items, new Point(536 - 242, 46), width: 226);
    }

    /// <summary>
    /// The handoff's tray menu. The prototype hangs it off a desktop tray pill;
    /// here the island owns it, since that is the always-visible element, and
    /// the menu is drawn inside the task frame so it has room.
    /// </summary>
    public void ShowTrayMenu()
    {
        if (ViewModel is null) return;

        ShowPopover(new[]
        {
            new PopoverItem("显示主窗口", Glyph("IconWindow"), Invoke: () =>
            {
                ViewModel.EdgeHide = false;
                ViewModel.ShowIsland = true;
            }),
            new PopoverItem(ViewModel.EdgeHide ? "取消贴边" : "贴边隐藏", Glyph("IconPin"),
                Invoke: () => ViewModel.ToggleEdgeHideCommand.Execute(null)),
            new PopoverItem("隐藏悬浮窗", Glyph("IconEye"),
                Invoke: () => ViewModel.ToggleIslandCommand.Execute(null)),
            new PopoverItem(ViewModel.IsRunning ? "全部暂停" : "全部开始", SeparatorBefore: true,
                Invoke: () => ViewModel.ToggleAllCommand.Execute(null)),
            new PopoverItem("速度限制…", Invoke: () => ViewModel.ActiveSheet = "prefs"),
            new PopoverItem("老板键：全部隐藏", Glyph("IconPower"), SeparatorBefore: true,
                Invoke: () => ViewModel.ToggleBossModeCommand.Execute(null)),
            new PopoverItem("退出", IsDestructive: true, SeparatorBefore: true,
                Invoke: () => CloseRequested?.Invoke(this, EventArgs.Empty)),
        }, new Point(14, 52), width: 226);
    }

    private void ShowContextMenu(RowContextRequest request)
    {
        if (ViewModel is null) return;

        var item = request.Item;
        var items = new List<PopoverItem>();

        if (item.IsDone) items.Add(new PopoverItem("打开文件", Glyph("IconOpen")));
        items.Add(new PopoverItem("在文件夹中显示", Glyph("IconFolder"),
            Invoke: () => ViewModel.OpenFolderCommand.Execute(null)));

        if (!item.IsDone)
        {
            items.Add(new PopoverItem(item.ToggleLabel, item.ToggleGlyph, SeparatorBefore: true,
                Invoke: () => ViewModel.ToggleTaskCommand.Execute(item)));
        }

        items.Add(new PopoverItem("重新下载", Glyph("IconRedo"), SeparatorBefore: item.IsDone,
            Invoke: () => ViewModel.RedownloadCommand.Execute(item)));
        items.Add(new PopoverItem("详细信息", Glyph("IconInfo"),
            Invoke: () => ViewModel.ShowInspector = true));

        if (!item.IsDone)
        {
            items.Add(new PopoverItem("移到队首", Glyph("IconUp"), SeparatorBefore: true,
                Invoke: () => ViewModel.MoveToFrontCommand.Execute(item)));
            items.Add(new PopoverItem("移到队尾", Glyph("IconDown"),
                Invoke: () => ViewModel.MoveToBackCommand.Execute(item)));
        }

        items.Add(new PopoverItem("校验 SHA-256", Glyph("IconShield"), SeparatorBefore: item.IsDone,
            Invoke: () => ViewModel.Say($"已开始校验“{item.Name}”")));
        items.Add(new PopoverItem("拷贝链接", Glyph("IconCopy"), SeparatorBefore: true, Invoke: () => CopyLink(item)));
        items.Add(new PopoverItem("重命名", Glyph("IconRename")));
        items.Add(new PopoverItem("删除", Glyph("IconTrash"), IsDestructive: true, SeparatorBefore: true,
            Invoke: () => ViewModel.RemoveTaskCommand.Execute(item)));

        var position = new Point(
            Math.Min(request.Position.X, ActualWidth - 244),
            Math.Min(request.Position.Y, ActualHeight - 380));

        ShowPopover(items, position, width: 232);
    }

    private void CopyLink(DownloadItemViewModel item)
    {
        var package = new DataPackage();
        package.SetText(item.Url);
        Clipboard.SetContent(package);
        ViewModel?.Say("已拷贝链接");
    }

    private void ShowPopover(IEnumerable<PopoverItem> items, Point position, double width)
    {
        DismissPopover();

        _popover = new PopoverControl();
        _popover.Dismissed += (_, _) => DismissPopover();

        OverlayHost.IsHitTestVisible = true;
        OverlayHost.Children.Add(_popover);
        _popover.Show(items, position, width);
    }

    private void DismissPopover()
    {
        if (_popover is null) return;
        OverlayHost.Children.Remove(_popover);
        _popover = null;
        OverlayHost.IsHitTestVisible = _sheet is not null;
    }

    // ── sheets ────────────────────────────────────────────────────────────
    private void ShowSheet(string? name)
    {
        if (_sheet is not null)
        {
            OverlayHost.Children.Remove(_sheet);
            _sheet = null;
        }

        if (ViewModel is null || name is null)
        {
            OverlayHost.IsHitTestVisible = _popover is not null;
            return;
        }

        FrameworkElement sheet = name switch
        {
            "add" => new AddSheet(ViewModel),
            "batch" => new BatchSheet(ViewModel),
            "torrent" => new TorrentSheet(ViewModel),
            "sniff" => new SniffSheet(ViewModel),
            _ => new SettingsSheet(ViewModel),
        };

        _sheet = sheet;
        OverlayHost.IsHitTestVisible = true;
        OverlayHost.Children.Add(sheet);
    }

    // ── drag and drop ─────────────────────────────────────────────────────
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = false;
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (ViewModel is null) return;

        string? url = null;
        if (e.DataView.Contains(StandardDataFormats.WebLink)) url = (await e.DataView.GetWebLinkAsync()).ToString();
        else if (e.DataView.Contains(StandardDataFormats.Text)) url = await e.DataView.GetTextAsync();

        if (string.IsNullOrWhiteSpace(url))
        {
            ViewModel.Say("拖入的内容不是链接");
            return;
        }

        ViewModel.PendingUrl = url.Trim();
        ViewModel.ActiveSheet = "add";
    }

    // ── view-model reactions ──────────────────────────────────────────────
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.Tab):
                Tabs.SelectedId = ViewModel?.Tab;
                break;

            case nameof(ShellViewModel.TabActiveLabel):
            case nameof(ShellViewModel.TabDoneLabel):
                SyncTabs();
                break;

            case nameof(ShellViewModel.IsRunning):
                SyncToggleAllGlyph();
                break;

            case nameof(ShellViewModel.HasCategoryFilter):
                SyncViewMenuAct();
                break;

            case nameof(ShellViewModel.DenseRows):
                RefreshRowDensity();
                break;

            case nameof(ShellViewModel.IsListExpanded):
                Animations.Slide(FoldGlyphRotation, "Angle", ViewModel?.IsListExpanded == true ? 180 : 0, 200).Begin();
                break;

            case nameof(ShellViewModel.Toast):
                SyncToast();
                break;

            case nameof(ShellViewModel.Banner):
                SyncBanner();
                break;

            case nameof(ShellViewModel.ActiveSheet):
                ShowSheet(ViewModel?.ActiveSheet);
                break;
        }
    }

    private void SyncTabs()
    {
        if (ViewModel is null) return;
        _tabs[1].Label = ViewModel.TabActiveLabel;
        _tabs[2].Label = ViewModel.TabDoneLabel;
    }

    private void SyncToggleAllGlyph() =>
        ToggleAllGlyph.Data = Glyph(ViewModel?.IsRunning == true ? "IconPauseFill" : "IconPlayFill");

    /// <summary>`nav__b.act`: the 显示与排序 button goes white-on-blue while a category filter is on.</summary>
    private void SyncViewMenuAct() =>
        ViewMenuButton.Style = (Style)Application.Current.Resources[
            ViewModel?.HasCategoryFilter == true ? "NavButtonActiveStyle" : "NavButtonStyle"];

    private void SyncToast()
    {
        string? message = ViewModel?.Toast;

        if (message is null)
        {
            var fadeOut = Animations.Fade(Toast, 0, 200);
            fadeOut.Completed += (_, _) =>
            {
                if (ViewModel?.Toast is null) Toast.Visibility = Visibility.Collapsed;
            };
            fadeOut.Begin();
            return;
        }

        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        ToastScale.ScaleX = 0.94;
        ToastScale.ScaleY = 0.94;
        Animations.Slide(ToastScale, "ScaleX", 1, 200).Begin();
        Animations.Slide(ToastScale, "ScaleY", 1, 200).Begin();
        Animations.Fade(Toast, 1, 200).Begin();
    }

    private void SyncBanner()
    {
        var task = ViewModel?.Banner;

        if (task is null)
        {
            Banner.Visibility = Visibility.Collapsed;
            return;
        }

        BannerSubtitle.Text = $"{task.Name} · {FormatHelpers.Bytes(task.Size)}";
        Banner.Visibility = Visibility.Visible;

        // `drop`: slides down 16px while fading in.
        BannerOffset.Y = -16;
        Banner.Opacity = 0;
        Animations.Slide(BannerOffset, "Y", 0, 340).Begin();
        Animations.Fade(Banner, 1, 340).Begin();
    }

    private void OnBannerOpenClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Banner is { } task) ViewModel.Say($"已打开“{task.Name}”");
        if (ViewModel is not null) ViewModel.Banner = null;
    }

    private static Geometry Glyph(string key) => (Geometry)Application.Current.Resources[key];
}
