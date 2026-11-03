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
        ThemeBrushes.SetTheme(Current.RequestedTheme);

        // The download kernel lives in its own UI-free assembly; TaskManager is
        // the only seam between it and the observable UI tree.
        IDownloadKernel kernel = UseRealKernel
            ? new RealDownloadKernel(new KernelOptions())
            : new SimulatedDownloadKernel();
        var taskManager = new TaskManager(kernel, new DispatcherQueueDispatcher(DispatcherQueue.GetForCurrentThread()));

        ISettingsStore settingsStore = new JsonSettingsStore();
        IClipboardWatcher clipboardWatcher = new ClipboardWatcher();

        var shellViewModel = new ShellViewModel(taskManager, clipboardWatcher, settingsStore);

        _shell = new ShellHost(shellViewModel);
        _shell.Start();

        MainAppWindow = _shell.MainWindow;
    }
}
