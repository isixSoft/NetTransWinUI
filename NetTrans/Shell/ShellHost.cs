using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NetTrans.Interop;
using NetTrans.Services;
using NetTrans.ViewModels;
using NetTrans.Views;
using NetTrans.Views.Controls;
using Windows.Graphics;

namespace NetTrans.Shell;

/// <summary>
/// Owns the three frames the design calls for -- the task window, the inspector
/// window and the island -- and everything that is a property of the shell
/// rather than of one frame: magnetic docking, the island following the task
/// frame, 贴边隐藏 and the 老板键.
/// </summary>
public sealed class ShellHost : IDisposable
{
    private const double FrameWidth = 536;
    private const double FrameHeight = 680;
    private const double IslandGap = 14;
    private const double IslandCollapsedWidth = 152;
    private const double IslandCollapsedHeight = 37;
    private const double IslandExpandedWidth = 300;
    private const double IslandExpandedHeight = 52;

    /// <summary>Only 36px of the frame stays on screen once it is edge-hidden.</summary>
    private const double EdgePeek = 36;

    private const int BossHotKeyId = 0xB055;
    private const uint VkH = 0x48;

    private readonly ShellViewModel _viewModel;

    private Window _mainWindow = null!;
    private Window _inspectorWindow = null!;
    private Window _islandWindow = null!;

    private WindowChrome _mainChrome = null!;
    private WindowChrome _inspectorChrome = null!;
    private WindowChrome _islandChrome = null!;

    private MainShell _mainShell = null!;
    private InspectorShell _inspectorShell = null!;
    private IslandControl _island = null!;

    private DockManager _dock = null!;
    private DispatcherTimer? _edgeTimer;
    private bool _edgeHidden;

    public ShellHost(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
    }

    public Window MainWindow => _mainWindow;

    public void Start()
    {
        BuildMainFrame();
        BuildInspectorFrame();
        BuildIsland();

        _dock = new DockManager(_mainChrome, _inspectorChrome);
        _dock.DockChanged += OnDockChanged;
        _dock.Snapped += (_, _) => _inspectorShell.PlaySnapIn();
        _dock.MainMoved += (_, _) => PositionIsland();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Manager.Ticked += (_, _) => RefreshIsland();

        PlaceFrames();
        ApplyInspectorVisibility();
        ApplyIslandVisibility();
        OnDockChanged(this, EventArgs.Empty);

        _mainChrome.HotKeyPressed += OnHotKey;
        _mainChrome.RegisterHotKey(BossHotKeyId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, VkH);

        _mainWindow.Activate();
        if (_viewModel.EdgeHide) ScheduleEdgeHide();
    }

    // ── frames ────────────────────────────────────────────────────────────
    private void BuildMainFrame()
    {
        _mainShell = new MainShell { ViewModel = _viewModel };
        _mainWindow = new Window { Title = "NetTrans", Content = _mainShell };

        _mainChrome = new WindowChrome(_mainWindow);
        _mainChrome.MakeFrameless();
        _mainChrome.SetContentSize(FrameWidth, FrameHeight);

        _mainShell.AttachChrome(_mainChrome);
        _mainShell.CloseRequested += (_, _) => Application.Current.Exit();
        _mainShell.MinimizeRequested += (_, _) =>
        {
            if (_mainWindow.AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
        };

        _mainShell.PointerEntered += (_, _) => SlideIn();
        _mainShell.PointerExited += (_, _) => ScheduleEdgeHide();
    }

    private void BuildInspectorFrame()
    {
        _inspectorShell = new InspectorShell { ViewModel = _viewModel };
        _inspectorWindow = new Window { Title = "详细信息", Content = _inspectorShell };

        _inspectorChrome = new WindowChrome(_inspectorWindow);
        _inspectorChrome.MakeFrameless();
        _inspectorChrome.MakeUtilityWindow();
        _inspectorChrome.SetContentSize(FrameWidth, FrameHeight);

        _inspectorShell.AttachChrome(_inspectorChrome);
        _inspectorShell.CloseRequested += (_, _) => _viewModel.ShowInspector = false;
        _inspectorShell.AttachToggleRequested += (_, _) =>
        {
            if (_dock.Dock is null) _dock.Attach();
            else _dock.Detach();
        };
    }

    private void BuildIsland()
    {
        _island = new IslandControl();
        _islandWindow = new Window { Title = "NetTrans 悬浮窗", Content = _island };

        _islandChrome = new WindowChrome(_islandWindow);
        _islandChrome.MakeFrameless(resizable: false, keepShadow: false);
        _islandChrome.MakeUtilityWindow(noActivate: true, topMost: true);
        _islandChrome.SetContentSize(IslandCollapsedWidth, IslandCollapsedHeight, cornerRadius: IslandCollapsedHeight / 2);

        _island.ExpandedChanged += (_, expanded) =>
        {
            double width = expanded ? IslandExpandedWidth : IslandCollapsedWidth;
            double height = expanded ? IslandExpandedHeight : IslandCollapsedHeight;
            _islandChrome.SetContentSize(width, height, cornerRadius: expanded ? 20 : height / 2);
            PositionIsland();
            RefreshIsland();
        };

        _island.MenuRequested += (_, _) =>
        {
            _mainWindow.Activate();
            _mainShell.ShowTrayMenu();
        };
    }

    /// <summary>Centres the docked pair on the work area, leaving room for the island above it.</summary>
    private void PlaceFrames()
    {
        var area = DisplayArea.GetFromWindowId(_mainWindow.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = _mainChrome.Scale;

        int frameWidth = (int)Math.Round(FrameWidth * scale);
        int frameHeight = (int)Math.Round(FrameHeight * scale);
        int islandRoom = (int)Math.Round((IslandExpandedHeight + IslandGap) * scale);

        int x = area.X + Math.Max(14, (area.Width - frameWidth * 2) / 2);
        int y = area.Y + Math.Max(islandRoom, (area.Height - frameHeight) / 2);

        _mainChrome.MoveTo(x, y);
        _inspectorChrome.MoveTo(x + frameWidth, y);
        PositionIsland();
    }

    private void PositionIsland()
    {
        if (!_viewModel.ShowIsland) return;

        var main = _mainChrome.BoundsPx;
        var island = _islandChrome.BoundsPx;
        double scale = _mainChrome.Scale;

        int x = main.X + (main.Width - island.Width) / 2;
        int y = main.Y - island.Height - (int)Math.Round(IslandGap * scale);
        _islandChrome.MoveTo(x, y);
    }

    private void RefreshIsland()
    {
        if (!_viewModel.ShowIsland) return;

        _island.Update(
            _viewModel.OverallFraction,
            _viewModel.TotalSpeedValue,
            _viewModel.TotalSpeedUnit,
            _viewModel.IslandSubtitle,
            _viewModel.SpeedHistory);
    }

    // ── docking ───────────────────────────────────────────────────────────
    private void OnDockChanged(object? sender, EventArgs e)
    {
        var dock = _dock.Dock;

        // Bonded frames square off the two corners along the shared edge.
        _mainChrome.ApplyCorners(_viewModel.ShowInspector ? dock : null);
        _inspectorChrome.ApplyCorners(_viewModel.ShowInspector ? Opposite(dock) : null);
        _inspectorShell.SetDocked(dock is not null);
    }

    private static DockSide? Opposite(DockSide? side) => side switch
    {
        DockSide.Right => DockSide.Left,
        DockSide.Left => DockSide.Right,
        DockSide.Bottom => DockSide.Top,
        DockSide.Top => DockSide.Bottom,
        _ => null,
    };

    // ── shell toggles ─────────────────────────────────────────────────────
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.ShowInspector):
                ApplyInspectorVisibility();
                OnDockChanged(this, EventArgs.Empty);
                break;

            case nameof(ShellViewModel.ShowIsland):
                ApplyIslandVisibility();
                break;

            case nameof(ShellViewModel.EdgeHide):
                if (_viewModel.EdgeHide) ScheduleEdgeHide();
                else SlideIn();
                break;

            case nameof(ShellViewModel.BossMode):
                ApplyBossMode();
                break;
        }
    }

    private void ApplyInspectorVisibility()
    {
        if (_viewModel.ShowInspector)
        {
            _inspectorWindow.AppWindow.Show(activateWindow: false);
            _dock.Reflow();
        }
        else
        {
            _inspectorWindow.AppWindow.Hide();
        }
    }

    private void ApplyIslandVisibility()
    {
        if (_viewModel.ShowIsland)
        {
            _islandWindow.AppWindow.Show(activateWindow: false);
            PositionIsland();
            RefreshIsland();
        }
        else
        {
            _islandWindow.AppWindow.Hide();
        }
    }

    /// <summary>Ctrl+Alt+H: everything disappears, downloads keep running.</summary>
    private void OnHotKey(object? sender, int id)
    {
        if (id != BossHotKeyId) return;
        _mainWindow.DispatcherQueue.TryEnqueue(() => _viewModel.BossMode = !_viewModel.BossMode);
    }

    private void ApplyBossMode()
    {
        if (_viewModel.BossMode)
        {
            _mainWindow.AppWindow.Hide();
            _inspectorWindow.AppWindow.Hide();
            _islandWindow.AppWindow.Hide();
            return;
        }

        _mainWindow.AppWindow.Show();
        ApplyInspectorVisibility();
        ApplyIslandVisibility();
    }

    // ── 贴边隐藏 ──────────────────────────────────────────────────────────
    private void ScheduleEdgeHide()
    {
        if (!_viewModel.EdgeHide || _edgeHidden || _viewModel.BossMode) return;

        _edgeTimer?.Stop();
        _edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _edgeTimer.Tick += (_, _) =>
        {
            // PointerExited also fires while the pointer moves between child
            // elements, so confirm against the real cursor before sliding away.
            if (IsPointerOverShell()) return;
            _edgeTimer?.Stop();
            SlideOut();
        };
        _edgeTimer.Start();
    }

    private bool IsPointerOverShell()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return false;

        return Contains(_mainChrome.BoundsPx, cursor)
            || (_viewModel.ShowInspector && Contains(_inspectorChrome.BoundsPx, cursor))
            || (_viewModel.ShowIsland && Contains(_islandChrome.BoundsPx, cursor));
    }

    private static bool Contains(RectInt32 rect, NativeMethods.POINT point) =>
        point.X >= rect.X && point.X < rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y < rect.Y + rect.Height;

    private void SlideOut()
    {
        if (_edgeHidden || !_viewModel.EdgeHide) return;

        var area = DisplayArea.GetFromWindowId(_mainWindow.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var bounds = _mainChrome.BoundsPx;
        int target = area.X + area.Width - (int)Math.Round(EdgePeek * _mainChrome.Scale);

        _edgeHidden = true;
        AnimateMainTo(target, bounds.Y);
    }

    private void SlideIn()
    {
        _edgeTimer?.Stop();
        if (!_edgeHidden) return;

        var area = DisplayArea.GetFromWindowId(_mainWindow.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var bounds = _mainChrome.BoundsPx;
        int target = area.X + area.Width - bounds.Width;

        _edgeHidden = false;
        AnimateMainTo(target, bounds.Y);
    }

    /// <summary>.34s slide on the shared bezier, with the inspector and island following.</summary>
    private void AnimateMainTo(int targetX, int targetY)
    {
        var from = _mainChrome.BoundsPx;
        int dx = targetX - from.X;
        int dy = targetY - from.Y;
        if (dx == 0 && dy == 0) return;

        long started = Environment.TickCount64;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

        timer.Tick += (_, _) =>
        {
            double t = Math.Clamp((Environment.TickCount64 - started) / 340.0, 0, 1);
            double eased = Easing.Standard(t);
            _mainChrome.MoveTo(from.X + (int)Math.Round(dx * eased), from.Y + (int)Math.Round(dy * eased));
            _dock.Reflow();
            PositionIsland();

            if (t >= 1) timer.Stop();
        };

        timer.Start();
    }

    public void Dispose()
    {
        _edgeTimer?.Stop();
        _mainChrome.UnregisterHotKey(BossHotKeyId);
        _dock.Dispose();
        _islandChrome.Dispose();
        _inspectorChrome.Dispose();
        _mainChrome.Dispose();
    }
}
