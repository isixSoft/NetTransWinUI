using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.Kernel;
using NetTrans.Services;
using NetTrans.ViewModels;
using NetTrans.Views.Controls;

namespace NetTrans.Views.Sheets;

/// <summary>批量下载: crawl a page, then pick which of the found resources to queue.</summary>
public sealed partial class BatchSheet : UserControl
{
    private static readonly (string Name, string Size)[] Found =
    {
        ("chapter-01-intro.pdf", "4.2 MB"),
        ("chapter-02-tokens.pdf", "6.8 MB"),
        ("chapter-03-motion.pdf", "11.4 MB"),
        ("assets-bundle.zip", "128 MB"),
        ("cover-artwork.png", "2.1 MB"),
        ("errata-notes.pdf", "0.4 MB"),
    };

    private readonly ShellViewModel _viewModel;
    private readonly List<CheckRow> _rows = new();
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };

    public BatchSheet(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        _scanTimer.Tick += (_, _) =>
        {
            _scanTimer.Stop();
            ShowResults();
        };
    }

    private void OnScanClick(object sender, RoutedEventArgs e)
    {
        FormStage.Visibility = Visibility.Collapsed;
        ScanStage.Visibility = Visibility.Visible;
        ScanButton.IsEnabled = false;
        _scanTimer.Start();
    }

    private void ShowResults()
    {
        ScanStage.Visibility = Visibility.Collapsed;
        ListStage.Visibility = Visibility.Visible;

        for (int i = 0; i < Found.Length; i++)
        {
            var row = new CheckRow(Found[i].Name, Found[i].Size, isChecked: i is 0 or 1 or 3, showSeparator: i > 0);
            row.Toggled += (_, _) => UpdateCount();
            _rows.Add(row);
            FoundList.Children.Add(row);
        }

        Host.IsRightEnabled = true;
        UpdateCount();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.SetChecked(true);
        UpdateCount();
    }

    private void UpdateCount()
    {
        int selected = _rows.Count(r => r.IsChecked);
        FoundHeader.Text = $"找到 {Found.Length} 个资源 · 已选 {selected}";
        Host.RightLabel = $"添加 {selected}";
        Host.IsRightEnabled = selected > 0;
    }

    private void OnCancelled(object? sender, EventArgs e)
    {
        _scanTimer.Stop();
        _viewModel.ActiveSheet = null;
    }

    private void OnConfirmed(object? sender, EventArgs e)
    {
        var picked = _rows.Where(r => r.IsChecked).ToList();
        if (picked.Count == 0) return;

        string page = PageBox.Text.TrimEnd('/');
        foreach (var row in picked)
        {
            _viewModel.Manager.Add(new NewDownloadRequest(
                $"{page}/{row.Label}",
                _viewModel.Settings.DefaultSavePath,
                "doc",
                8,
                TaskPriority.Normal,
                StartNow: true));
        }

        _viewModel.Say($"已添加 {picked.Count} 个任务");
        _viewModel.ActiveSheet = null;
    }
}
