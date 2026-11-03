using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NetTrans.Services;
using NetTrans.ViewModels;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace NetTrans.Views.Controls;

/// <summary>The point in the frame where a context menu was asked for.</summary>
public sealed record RowContextRequest(DownloadItemViewModel Item, Point Position);

/// <summary>One `.row`. Rows are recycled by the repeater, so every visual is driven off <see cref="Item"/>.</summary>
public sealed partial class TaskRow : UserControl
{
    public static readonly DependencyProperty IsDenseProperty =
        DependencyProperty.Register(nameof(IsDense), typeof(bool), typeof(TaskRow),
            new PropertyMetadata(false, (d, _) => ((TaskRow)d).ApplyDensity()));

    public static readonly DependencyProperty ShowSeparatorProperty =
        DependencyProperty.Register(nameof(ShowSeparator), typeof(bool), typeof(TaskRow),
            new PropertyMetadata(true, (d, _) => ((TaskRow)d).ApplySeparator()));

    public event EventHandler<(DownloadItemViewModel Item, bool Additive)>? RowInvoked;
    public event EventHandler<DownloadItemViewModel>? ToggleRequested;
    public event EventHandler<DownloadItemViewModel>? RemoveRequested;
    public event EventHandler<RowContextRequest>? ContextRequested;

    private DownloadItemViewModel? _item;
    private bool _isPointerOver;
    private double _animatedFraction = -1;

    public DownloadItemViewModel? Item
    {
        get => _item;
        private set
        {
            if (ReferenceEquals(_item, value)) return;
            if (_item is not null) _item.PropertyChanged -= OnItemPropertyChanged;
            _item = value;
            if (_item is not null) _item.PropertyChanged += OnItemPropertyChanged;

            Bindings.Update();
            ApplyState();
        }
    }

    public bool IsDense
    {
        get => (bool)GetValue(IsDenseProperty);
        set => SetValue(IsDenseProperty, value);
    }

    public bool ShowSeparator
    {
        get => (bool)GetValue(ShowSeparatorProperty);
        set => SetValue(ShowSeparatorProperty, value);
    }

    public TaskRow()
    {
        InitializeComponent();
        DataContextChanged += (_, args) => Item = args.NewValue as DownloadItemViewModel;
        ApplyDensity();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyState();

    private void ApplyState()
    {
        if (Item is null) return;

        DoneBadge.Visibility = Item.IsDone ? Visibility.Visible : Visibility.Collapsed;
        TrailingText.Visibility = Item.IsDone ? Visibility.Collapsed : Visibility.Visible;
        TrailingText.Foreground = ThemeBrushes.Get(Item.IsError ? "RedBrush" : "Label2Brush");

        // The pause/resume action is absent on finished rows; only 删除 remains.
        ToggleButton.Visibility = Item.IsDone ? Visibility.Collapsed : Visibility.Visible;

        ApplyBackground();
        ApplyProgress(animate: true);
    }

    private void ApplyBackground() =>
        Surface.Background = Item?.IsSelected == true
            ? ThemeBrushes.Get("RowSelectedBrush")
            : _isPointerOver
                ? ThemeBrushes.Get("RowHoverBrush")
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private void ApplyProgress(bool animate)
    {
        if (Item is null || Track.ActualWidth <= 0) return;

        double target = Track.ActualWidth * Item.Fraction;
        if (!animate)
        {
            _animatedFraction = Item.Fraction;
            TrackFill.Width = target;
            return;
        }

        // The item raises several notifications per engine tick; only the ones
        // that actually move the bar should start a new .5s transition.
        if (Math.Abs(_animatedFraction - Item.Fraction) < 0.0001) return;
        _animatedFraction = Item.Fraction;

        // CSS: transition: width .5s linear.
        var frames = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        frames.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)),
            Value = target,
        });

        Storyboard.SetTarget(frames, TrackFill);
        Storyboard.SetTargetProperty(frames, "Width");

        var storyboard = new Storyboard();
        storyboard.Children.Add(frames);
        storyboard.Begin();
    }

    /// <summary>`.card.dense`: 22px tile, no sub line, 2px track, separator pulled in to 43px.</summary>
    private void ApplyDensity()
    {
        bool dense = IsDense;

        RowContent.Padding = new Thickness(12, dense ? 5 : 9, 12, dense ? 5 : 9);
        RowContent.ColumnSpacing = dense ? 9 : 11;

        Tile.Width = dense ? 22 : 36;
        Tile.Height = dense ? 22 : 36;
        Tile.CornerRadius = new CornerRadius(dense ? 6 : 9);
        TileGlyph.IconSize = dense ? 13 : 19;

        NameText.FontSize = dense ? 13 : 14.5;
        SubText.Visibility = dense ? Visibility.Collapsed : Visibility.Visible;

        Track.Height = dense ? 2 : 3;
        Track.Margin = new Thickness(0, dense ? 4 : 6, 0, 0);

        ToggleButton.Width = dense ? 46 : 64;
        ToggleGlyph.IconSize = dense ? 13 : 17;
        ToggleLabel.FontSize = dense ? 11 : 12.5;
        RemoveGlyph.IconSize = dense ? 13 : 17;
        RemoveLabel.FontSize = dense ? 11 : 12.5;

        // `.card.dense .badge`: the 完成 badge shrinks with the row.
        DoneBadge.Padding = dense ? new Thickness(6, 1, 6, 1) : new Thickness(7, 2, 7, 2);
        DoneBadgeText.FontSize = dense ? 10 : 11;

        ApplySeparator();
        ApplyProgress(animate: false);
    }

    private void ApplySeparator()
    {
        Separator.Visibility = ShowSeparator ? Visibility.Visible : Visibility.Collapsed;
        Separator.Margin = new Thickness(IsDense ? 43 : 59, 0, 0, 0);
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // `.row { overflow: hidden }` -- the swipe strip must not spill out of the card.
        Surface.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        ApplyProgress(animate: false);
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => ApplyProgress(animate: false);

    private void OnSwipeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // translateX(101%): parked just past its own width.
        if (!_isPointerOver) SwipeOffset.X = e.NewSize.Width;
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        ApplyBackground();
        Animations.Slide(SwipeOffset, "X", 0, 240).Begin();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        ApplyBackground();
        Animations.Slide(SwipeOffset, "X", Swipe.ActualWidth, 240).Begin();
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Item is null) return;
        RowInvoked?.Invoke(this, (Item, IsCtrlDown()));
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Item is null) return;
        RowInvoked?.Invoke(this, (Item, false));
        ContextRequested?.Invoke(this, new RowContextRequest(Item, e.GetPosition(null)));
        e.Handled = true;
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (Item is not null) ToggleRequested?.Invoke(this, Item);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Item is not null) RemoveRequested?.Invoke(this, Item);
    }

    /// <summary>Ctrl-click extends the selection, matching the handoff's Cmd/Ctrl behaviour.</summary>
    private static bool IsCtrlDown() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
}
