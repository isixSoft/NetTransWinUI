using Microsoft.UI.Xaml;
using NetTrans.Kernel;
using NetTrans.Services;
using NetTrans.ViewModels;
using NetTrans.Shell;

namespace NetTrans;

public partial class App : Application
{
    private ShellHost? _shell;

    /// <summary>
    /// 内核模式开关：Real = 真实协议内核（HTTP 多分段 / FTP / HLS / DASH / BT·eD2k 元数据，
    /// 断点续传、限速、镜像源、SOCKS5）；Simulation = 演示内核（种子数据、固定节奏）。
    /// 改这一行即可切换，UI 与 TaskManager 对两者无感知。
    /// </summary>
    private const bool UseRealKernel = true;

    public static Window? MainAppWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // P2-9 多语言：必须在任何 XAML 参与布局之前（包括 ThemeBrushes
        // 读取 Theme）设置 PrimaryLanguageOverride，这样 Resources.resw
        // 被 MRT Core 正确加载。
        ISettingsStore settingsStore = new JsonSettingsStore();
        var bootstrap = settingsStore.Load();
        L10n.Apply(bootstrap.Language);

        ThemeBrushes.SetTheme(Current.RequestedTheme);
        IDownloadKernel kernel = UseRealKernel
            ? new RealDownloadKernel(KernelOptionsFactory.Create(settingsStore.Load()))
            : new SimulatedDownloadKernel();
        var taskManager = new TaskManager(kernel, new DispatcherQueueDispatcher(DispatcherQueue.GetForCurrentThread()));

        IClipboardWatcher clipboardWatcher = new ClipboardWatcher();

        var shellViewModel = new ShellViewModel(taskManager, clipboardWatcher, settingsStore);

        _shell = new ShellHost(shellViewModel);
        _shell.Start();

        MainAppWindow = _shell.MainWindow;
    }
}
