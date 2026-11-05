// Test doubles for the seam tests: a programmable IDownloadKernel, the per-task
// item the generic manager mirrors into, and a queueable IDispatcher stand-in.
// Global-namespace types like TestKit — the top-level Program uses them bare.

using NetTrans.Kernel;
using NetTrans.Services;

// Programmable IDownloadKernel: the tests drive every event by hand, so the
// task manager's mirroring can be observed deterministically on one thread.
internal sealed class FakeKernel : IDownloadKernel
{
    private int _nextId = 1;

    public List<DownloadTask> TaskList { get; } = new();
    public KernelSnapshot SnapshotValue { get; set; } = new(0, 0, false, Array.Empty<double>());
    public List<string> Calls { get; } = new();

    public IReadOnlyList<DownloadTask> Tasks => TaskList;
    public KernelSnapshot Snapshot => SnapshotValue;

    public event Action<DownloadTask>? TaskAdded;
    public event Action<DownloadTask>? TaskRemoved;
    public event Action<DownloadTask>? TaskUpdated;
    public event Action<DownloadTask>? TaskCompleted;
    public event Action<DownloadTask>? TaskMoved;
    public event Action<KernelSnapshot>? Ticked;

    public DownloadTask MakeTask(string name, DownloadStatus status, long size = 100_000_000, bool track = true)
    {
        var task = new DownloadTask
        {
            Id = _nextId++,
            Name = name,
            Host = "example.com",
            Kind = FileKind.Zip,
            Size = size,
            Category = "soft",
            Status = status,
        };
        if (track) TaskList.Add(task);
        return task;
    }

    public DownloadTask Add(NewDownloadRequest request)
    {
        var task = MakeTask(Path.GetFileName(request.Url), DownloadStatus.Downloading);
        TaskAdded?.Invoke(task); // the real kernel fires inline on the caller's thread
        return task;
    }

    public void Toggle(int id) => Calls.Add($"Toggle:{id}");
    public void Remove(IEnumerable<int> ids) => Calls.Add($"Remove:{string.Join(',', ids)}");
    public void ToggleAll() => Calls.Add("ToggleAll");
    public void MoveToFront(int id) => Calls.Add($"MoveToFront:{id}");
    public void MoveToBack(int id) => Calls.Add($"MoveToBack:{id}");
    public void Redownload(int id) => Calls.Add($"Redownload:{id}");
    public void SetPriority(int id, TaskPriority priority) => Calls.Add($"SetPriority:{id}:{priority}");
    public void SetSpeedLimit(int id, string limit) => Calls.Add($"SetSpeedLimit:{id}:{limit}");
    public void SetGlobalSpeedLimit(long? bytesPerSecond) => Calls.Add($"SetGlobalSpeedLimit:{bytesPerSecond?.ToString() ?? "null"}");
    public void Dispose() { }

    public void RaiseTaskAdded(DownloadTask task) => TaskAdded?.Invoke(task);
    public void RaiseTaskRemoved(DownloadTask task) => TaskRemoved?.Invoke(task);
    public void RaiseTaskUpdated(DownloadTask task) => TaskUpdated?.Invoke(task);
    public void RaiseTaskCompleted(DownloadTask task) => TaskCompleted?.Invoke(task);
    public void RaiseTaskMoved() => TaskMoved?.Invoke(TaskList[0]);
    public void RaiseTicked(KernelSnapshot snapshot) => Ticked?.Invoke(snapshot);

    public void Reorder(params int[] ids)
    {
        var byId = TaskList.ToDictionary(t => t.Id);
        TaskList.Clear();
        foreach (var id in ids) TaskList.Add(byId[id]);
    }
}

// The host-side per-task wrapper the generic manager mirrors into; stands in
// for NetTrans's DownloadItemViewModel (WinUI-bound, hence not linkable).
internal sealed class TestItem
{
    public TestItem(DownloadTask task) => Task = task;

    public DownloadTask Task { get; }
    public int Refreshes { get; private set; }
    public bool IsRunning => Task.Status == DownloadStatus.Downloading;

    public void Refresh() => Refreshes++;
}

// Stands in for DispatcherQueueDispatcher: either runs posts inline (the "on
// the UI thread" case) or queues them for an explicit Flush (kernel threads).
internal sealed class TestDispatcher : IDispatcher
{
    private readonly List<Action> _queued = new();

    public bool RunInline { get; init; } = true;
    public int Posted { get; private set; }
    public int Pending => _queued.Count;

    public void Post(Action action)
    {
        Posted++;
        if (RunInline) action();
        else _queued.Add(action);
    }

    public void Flush()
    {
        var actions = new List<Action>(_queued);
        _queued.Clear();
        foreach (var action in actions) action();
    }
}
