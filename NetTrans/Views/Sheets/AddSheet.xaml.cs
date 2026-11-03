using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.Kernel;
using NetTrans.Services;
using NetTrans.ViewModels;

namespace NetTrans.Views.Sheets;

/// <summary>新建下载: the handoff's AddSheet, wired to the engine.</summary>
public sealed partial class AddSheet : UserControl
{
    private readonly ShellViewModel _viewModel;

    public AddSheet(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        UrlBox.Text = viewModel.PendingUrl;
        Loaded += (_, _) => UrlBox.Focus(FocusState.Programmatic);
    }

    private void OnScheduleToggled(object? sender, bool isOn) =>
        ScheduleRow.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;

    private void OnCancelled(object? sender, EventArgs e) => _viewModel.ActiveSheet = null;

    private void OnConfirmed(object? sender, EventArgs e)
    {
        string url = UrlBox.Text.Trim();
        if (url.Length == 0 || url == "https://")
        {
            _viewModel.Say("请输入下载地址");
            return;
        }

        _viewModel.AddDownload(new NewDownloadRequest(
            url,
            _viewModel.Settings.DefaultSavePath,
            CategoryId(CategoryBox.SelectedIndex),
            int.TryParse(ConnectionsBox.SelectedItem as string, out int connections) ? connections : 8,
            PriorityBox.SelectedIndex switch { 0 => TaskPriority.High, 2 => TaskPriority.Low, _ => TaskPriority.Normal },
            StartNowSwitch.IsOn,
            ScheduleSwitch.IsOn ? "今晚 23:00" : null));

        _viewModel.PendingUrl = "https://";
        _viewModel.ActiveSheet = null;
    }

    private static string CategoryId(int index) => index switch
    {
        1 => "video",
        2 => "doc",
        3 => "music",
        _ => "soft",
    };
}
