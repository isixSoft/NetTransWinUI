using Microsoft.UI.Xaml;
using NetTrans.Kernel;
using NetTrans.Services;
using NetTrans.ViewModels;
using NetTrans.Shell;

namespace NetTrans;

public partial class App : Application
{
    private ShellHost? _shell;

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
        IDownloadKernel kernel = new SimulatedDownloadKernel();
        var taskManager = new TaskManager(kernel, DispatcherQueue.GetForCurrentThread());

        ISettingsStore settingsStore = new JsonSettingsStore();
        IClipboardWatcher clipboardWatcher = new ClipboardWatcher();

        var shellViewModel = new ShellViewModel(taskManager, clipboardWatcher, settingsStore);

        _shell = new ShellHost(shellViewModel);
        _shell.Start();

        MainAppWindow = _shell.MainWindow;
    }
}
