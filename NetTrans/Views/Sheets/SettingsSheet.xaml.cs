using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.Services;
using NetTrans.ViewModels;

namespace NetTrans.Views.Sheets;

/// <summary>设置: the three groups from the handoff, backed by AppSettings.</summary>
public sealed partial class SettingsSheet : UserControl
{
    private readonly ShellViewModel _viewModel;
    private readonly SpeedProfileService _speedProfiles;
    private bool _loading = true;

    public SettingsSheet(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        var settings = viewModel.Settings;
        _speedProfiles = new SpeedProfileService(settings);

        SavePathRow.Value = settings.DefaultSavePath;
        ScheduleRow.Value = $"{settings.OffPeakStart} – {settings.OffPeakEnd}";
        BossKeyRow.Value = settings.BossKey;

        FoldersSwitch.IsOn = settings.FoldersByCategory;
        NightSwitch.IsOn = settings.UncappedAtNight;
        ClipboardSwitch.IsOn = settings.WatchClipboard;
        NotifySwitch.IsOn = settings.NotifyOnCompletion;
        MotwSwitch.IsOn = settings.MarkOfTheWeb;
        VerifySwitch.IsOn = settings.VerifyChecksums;
        ScanSwitch.IsOn = settings.ScanOnCompletion;
        EdgeSwitch.IsOn = viewModel.EdgeHide;
        IslandSwitch.IsOn = viewModel.ShowIsland;

        NameTemplateBox.Text = settings.NameTemplate;

        Select(ConcurrencyBox, settings.MaxSimultaneousDownloads.ToString());
        Select(GlobalLimitBox, settings.GlobalSpeedLimit);
        ProfileBox.ItemsSource = _speedProfiles.Profiles.Select(p => p.Name).ToList();
        Select(ProfileBox, settings.ActiveSpeedProfile);
        Select(RetryBox, settings.RetryPolicy);
        Select(AfterBox, settings.WhenAllComplete);

        // P2-9 语言下拉：显示名本地化 + 值为语言码（auto/en/zh-CN）
        LanguageBox.ItemsSource = new[]
        {
            new KeyValuePair<string, string>(L10n.Get("LangAuto"), "auto"),
            new KeyValuePair<string, string>(L10n.Get("LangEn"), "en"),
            new KeyValuePair<string, string>(L10n.Get("LangZhCN"), "zh-CN"),
        };
        SelectByValue(LanguageBox, settings.Language);

        _loading = false;
    }

    private static void Select(ComboBox box, string value)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] as string != value) continue;
            box.SelectedIndex = i;
            return;
        }

        box.SelectedIndex = 0;
    }

    /// <summary>For LanguageBox whose items are KeyValuePair (display, lang-code).</summary>
    private static void SelectByValue(ComboBox box, string value)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is KeyValuePair<string, string> kv && kv.Value == value)
            {
                box.SelectedIndex = i; return;
            }
        }
        box.SelectedIndex = 0;
    }

    private void OnFoldersToggled(object? sender, bool value) => Update(s => s.FoldersByCategory = value);

    private void OnNightToggled(object? sender, bool value) => Update(s => s.UncappedAtNight = value);

    private void OnNotifyToggled(object? sender, bool value) => Update(s => s.NotifyOnCompletion = value);

    private void OnMotwToggled(object? sender, bool value) => Update(s => s.MarkOfTheWeb = value);

    private void OnVerifyToggled(object? sender, bool value) => Update(s => s.VerifyChecksums = value);

    private void OnScanToggled(object? sender, bool value) => Update(s => s.ScanOnCompletion = value);

    private void OnClipboardToggled(object? sender, bool value)
    {
        Update(s => s.WatchClipboard = value);
        _viewModel.Say(value ? L10n.Get("NotifyClipboardStarted") : L10n.Get("NotifyClipboardStopped"));
    }

    private void OnEdgeToggled(object? sender, bool value)
    {
        if (_loading) return;
        _viewModel.EdgeHide = value;
    }

    private void OnIslandToggled(object? sender, bool value)
    {
        if (_loading) return;
        _viewModel.ShowIsland = value;
    }

    private void OnConcurrencyChanged(object sender, SelectionChangedEventArgs e) =>
        Update(s => s.MaxSimultaneousDownloads = int.TryParse(ConcurrencyBox.SelectedItem as string, out int n) ? n : 3);

    private void OnGlobalLimitChanged(object sender, SelectionChangedEventArgs e) =>
        Update(s => s.GlobalSpeedLimit = GlobalLimitBox.SelectedItem as string ?? "不限");

    /// <summary>限速档位（P2-2）: switch applies the cap to the live kernel and records the name.</summary>
    private void OnProfileChanged(object sender, SelectionChangedEventArgs e) =>
        Update(_ => _speedProfiles.Switch(ProfileBox.SelectedItem as string ?? "全速",
            _viewModel.Manager.SetGlobalSpeedLimit));

    private void OnNameTemplateChanged(object sender, TextChangedEventArgs e) =>
        Update(s => s.NameTemplate = NameTemplateBox.Text);

    private void OnRetryChanged(object sender, SelectionChangedEventArgs e) =>
        Update(s => s.RetryPolicy = RetryBox.SelectedItem as string ?? "3 次");

    private void OnAfterChanged(object sender, SelectionChangedEventArgs e) =>
        Update(s => s.WhenAllComplete = AfterBox.SelectedItem as string ?? "无操作");

    /// <summary>P2-9 语言切换：值存为 auto/en/zh-CN，下次启动生效。</summary>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Update(s => s.Language = (LanguageBox.SelectedItem as KeyValuePair<string, string>?)?.Value ?? "auto");
    }

    private void OnOptimizeClick(object sender, RoutedEventArgs e) =>
        _viewModel.Say(L10n.Get("NotifyConcurrencyOpt"));

    private void OnClosed(object? sender, EventArgs e) => _viewModel.ActiveSheet = null;

    private void Update(Action<Models.AppSettings> change)
    {
        if (_loading) return;
        change(_viewModel.Settings);
        _viewModel.Persist();
    }
}
