using Microsoft.UI.Dispatching;
using NetTrans.Kernel;

namespace NetTrans.Services;

/// <summary>
/// Bridges the WinUI DispatcherQueue to the kernel's IDispatcher seam: work
/// posted from kernel threads runs inline when already on the UI thread and is
/// enqueued otherwise -- the exact behaviour the task manager used to inline.
/// </summary>
public sealed class DispatcherQueueDispatcher : IDispatcher
{
    private readonly DispatcherQueue _dispatcher;

    public DispatcherQueueDispatcher(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public void Post(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }
}
