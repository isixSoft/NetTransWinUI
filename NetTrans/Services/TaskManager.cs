using NetTrans.Kernel;
using NetTrans.ViewModels;

namespace NetTrans.Services;

/// <summary>
/// The UI-side task manager: binds the kernel-side generic manager to
/// <see cref="DownloadItemViewModel"/>. All mirroring and marshalling logic
/// lives in <c>NetTrans.Kernel.TaskManager&lt;TItem&gt;</c>; this shell only
/// supplies the WinUI view-model wiring, so it stays the single seam the shell
/// binds to.
/// </summary>
public sealed class TaskManager : NetTrans.Kernel.TaskManager<DownloadItemViewModel>
{
    public TaskManager(IDownloadKernel kernel, IDispatcher dispatcher)
        : base(kernel, dispatcher,
            task => new DownloadItemViewModel(task),
            vm => vm.Refresh(),
            vm => vm.IsRunning)
    {
    }
}
