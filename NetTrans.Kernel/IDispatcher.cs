namespace NetTrans.Kernel;

/// <summary>
/// The threading seam between the kernel and its host. Kernel events fire on
/// kernel threads; hosts that care about thread affinity (WinUI) implement
/// this to hop onto their own context, and tests substitute a synchronous or
/// queued fake. The task manager posts every kernel event through it.
/// </summary>
public interface IDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the host's context, now or later.</summary>
    void Post(Action action);
}
