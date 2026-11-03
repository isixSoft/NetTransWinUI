namespace NetTrans.Kernel;

/// <summary>
/// The boundary between the download kernel and everything above it. The
/// UI-side task manager (NetTrans's <c>TaskManager</c>) is the only intended
/// consumer: it translates this contract into observable state on the UI
/// thread, and forwards user intent back down as commands.
///
/// Threading contract: command methods run synchronously on the caller's
/// thread and raise their events there; the transfer loop ticks on a
/// kernel-owned worker thread and raises Ticked / TaskCompleted there, so
/// subscribers must marshal onto their own context. Task instances are owned
/// by the kernel and mutated in place between events -- copy anything you need
/// to keep.
/// </summary>
public interface IDownloadKernel : IDisposable
{
    /// <summary>Every task, in queue order.</summary>
    IReadOnlyList<DownloadTask> Tasks { get; }

    /// <summary>Aggregate readouts, computed under the kernel lock.</summary>
    KernelSnapshot Snapshot { get; }

    event Action<DownloadTask>? TaskAdded;
    event Action<DownloadTask>? TaskRemoved;

    /// <summary>State changed without progress: paused, resumed, retried, priority / limit changed, 新版本 cleared.</summary>
    event Action<DownloadTask>? TaskUpdated;

    event Action<DownloadTask>? TaskCompleted;

    /// <summary>Queue order changed (置顶 / 置底).</summary>
    event Action<DownloadTask>? TaskMoved;

    /// <summary>Raised once per tick after the running tasks have advanced.</summary>
    event Action<KernelSnapshot>? Ticked;

    void Toggle(int id);
    void Remove(IEnumerable<int> ids);
    void ToggleAll();
    void MoveToFront(int id);
    void MoveToBack(int id);

    /// <summary>Restarts a task against the newer server-side build and clears its 新版本 flag.</summary>
    void Redownload(int id);

    void SetPriority(int id, TaskPriority priority);

    /// <summary>Per-task cap as shown in the dropdown ("不限", "1 MB/s", ...).</summary>
    void SetSpeedLimit(int id, string limit);

    DownloadTask Add(NewDownloadRequest request);
}
