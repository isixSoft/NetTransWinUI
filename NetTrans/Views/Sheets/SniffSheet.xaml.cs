using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.Kernel;
using NetTrans.Services;
using NetTrans.ViewModels;
using NetTrans.Views.Controls;

namespace NetTrans.Views.Sheets;

/// <summary>视频嗅探: probe a page URL and offer the streams found on it.</summary>
public sealed partial class SniffSheet : UserControl
{
    private static readonly (string Quality, string Size, string Format)[] Sources =
    {
        ("2160p", "1.2 GB", "MP4"),
        ("1080p", "412 MB", "MP4"),
        ("720p", "186 MB", "MP4"),
        ("音轨", "38 MB", "M4A"),
    };

    private readonly ShellViewModel _viewModel;
    private readonly List<CheckRow> _rows = new();
    private readonly DispatcherTimer _probeTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    public SniffSheet(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        _probeTimer.Tick += (_, _) =>
        {
            _probeTimer.Stop();
            ShowResults();
        };
    }

    private void OnPageChanged(object sender, TextChangedEventArgs e)
    {
        // Editing the address invalidates the previous probe, as in the prototype.
        _probeTimer.Stop();
        Results.Visibility = Visibility.Collapsed;
        FoundList.Children.Clear();
        _rows.Clear();
        Host.IsRightEnabled = false;
        ProbeButton.Visibility = Visibility.Visible;
        ProbeButton.IsEnabled = true;
        ProbeButton.Content = "探测视频";
    }

    private void OnProbeClick(object sender, RoutedEventArgs e)
    {
        ProbeButton.IsEnabled = false;
        ProbeButton.Content = "探测中…";
        _probeTimer.Start();
    }

    private void ShowResults()
    {
        ProbeButton.Visibility = Visibility.Collapsed;
        Results.Visibility = Visibility.Visible;
        FoundHeader.Text = $"找到 {Sources.Length} 个源";

        for (int i = 0; i < Sources.Length; i++)
        {
            var row = new CheckRow($"{Sources[i].Quality}  {Sources[i].Format}", Sources[i].Size,
                isChecked: i == 0, showSeparator: i > 0);
            row.Toggled += (_, _) => Host.IsRightEnabled = _rows.Any(r => r.IsChecked);
            _rows.Add(row);
            FoundList.Children.Add(row);
        }

        Host.IsRightEnabled = true;
    }

    private void OnCancelled(object? sender, EventArgs e)
    {
        _probeTimer.Stop();
        _viewModel.ActiveSheet = null;
    }

    private void OnConfirmed(object? sender, EventArgs e)
    {
        var picked = _rows.Where(r => r.IsChecked).ToList();
        if (picked.Count == 0) return;

        foreach (var row in picked)
        {
            _viewModel.Manager.Add(new NewDownloadRequest(
                PageBox.Text.Trim(),
                _viewModel.Settings.DefaultSavePath,
                "video",
                8,
                TaskPriority.Normal,
                StartNow: true));
        }

        _viewModel.Say("已加入下载队列");
        _viewModel.ActiveSheet = null;
    }
}
