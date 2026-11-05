using System.Collections.ObjectModel;

namespace NetTrans.Kernel;

/// <summary>
/// The task manager: the single seam between the download kernel and the UI.
/// It mirrors the kernel's queue into the observable collection the shell
/// binds, marshals kernel events (which arrive on kernel threads) through the
/// dispatcher, and forwards user intent back down as kernel commands. View
/// models and views see only this class -- never the kernel.
///
/// Generic over the host's per-task view wrapper, so the mirroring logic here
/// stays UI-free and testable headlessly: the host supplies how to wrap a
/// DownloadTask, how to refresh one, and which wrapped tasks are running.
/// </summary>
/// <typeparam name="TItem">The host's per-task view wrapper (e.g. a view model).</typeparam>
public class TaskManager<TItem>
    where TItem : class
{
    private readonly IDownloadKernel _kernel;
    private readonly IDispatcher _dispatcher;
    private readonly Func<DownloadTask, TItem> _createItem;
    private readonly Action<TItem> _refreshItem;
    private readonly Func<TItem, bool> _isRunning;
    private readonly Dictionary<int, TItem> _index = new();

    /// <summary>
    /// Items that were running as of the last tick -- the delta used to repaint
    /// rows whose running state flipped between ticks even when the kernel did
    /// not raise a per-task event for the transition.
    /// </summary>
    private readonly HashSet<TItem> _wasRunning = new();

    public TaskManager(IDownloadKernel kernel, IDispatcher dispatcher,
        Func<DownloadTask, TItem> createItem, Action<TItem> refreshItem,
        Func<TItem, bool> isRunning)
    {
        _kernel = kernel;
        _dispatcher = dispatcher;
        _createItem = createItem;
        _refreshItem = refreshItem;
        _isRunning = isRunning;

        kernel.TaskAdded += OnTaskAdded;
        kernel.TaskRemoved += OnTaskRemoved;
        kernel.TaskUpdated += OnTaskUpdated;
        kernel.TaskCompleted += OnTaskCompleted;
        kernel.TaskMoved += OnTaskMoved;
        kernel.Ticked += OnTicked;

        foreach (var task in kernel.Tasks) AddToView(task);
        Apply(kernel.Snapshot);
    }

    /// <summary>The kernel queue as the shell binds it: same order, view-model wrappers.</summary>
    public ObservableCollection<TItem> Tasks { get; } = new();

    public double TotalSpeed { get; private set; }
    public double UploadSpeed { get; private set; }

    /// <summary>The island's 26-bar sparkline, oldest sample first.</summary>
    public IReadOnlyList<double> SpeedHistory { get; private set; } = System.Array.Empty<double>();

    /// <summary>True while at least one task is running -- drives the toolbar's play/pause glyph.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Raised once per tick so live-only readouts can refresh without a collection change.</summary>
    public event EventHandler? Ticked;

    /// <summary>Raised when a task finishes, so the shell can drop the completion banner.</summary>
    public event EventHandler<TItem>? Completed;

    // ── user intent, forwarded to the kernel ──────────────────────────────
    public TItem Add(NewDownloadRequest request)
    {
        var task = _kernel.Add(request); // raises TaskAdded, which is marshalled through the dispatcher
        // The posted TaskAdded handler may not have run yet (deferred
        // dispatcher, non-UI caller), so establish the mirror entry here and
        // let the posted handler skip tasks the mirror already holds.
        if (!_index.ContainsKey(task.Id)) AddToView(task);
        return _index[task.Id];
    }

    public void Toggle(int id) => Command(() => _kernel.Toggle(id));
    public void Remove(IEnumerable<int> ids) => Command(() => _kernel.Remove(ids));
    public void ToggleAll() => Command(_kernel.ToggleAll);
    public void MoveToFront(int id) => Command(() => _kernel.MoveToFront(id));
    public void MoveToBack(int id) => Command(() => _kernel.MoveToBack(id));
    public void Redownload(int id) => Command(() => _kernel.Redownload(id));
    public void SetPriority(int id, TaskPriority priority) => Command(() => _kernel.SetPriority(id, priority));
    public void SetSpeedLimit(int id, string limit) => Command(() => _kernel.SetSpeedLimit(id, limit));

    /// <summary>
    /// Global cap passthrough for the 设置 sheet's 限速档位 switch (P2-2) -- views
    /// never touch the kernel directly, per the seam rule above.
    /// </summary>
    public void SetGlobalSpeedLimit(long? bytesPerSecond) => Command(() => _kernel.SetGlobalSpeedLimit(bytesPerSecond));

    /// <summary>Runs a kernel command, then refreshes the aggregates at once -- the way the old engine recomputed after each command.</summary>
    private void Command(Action action)
    {
        action();
        Apply(_kernel.Snapshot);
    }

    // ── kernel events, marshalled through the dispatcher ──────────────────
    private void OnTaskAdded(DownloadTask task) => Post(() =>
    {
        if (!_index.ContainsKey(task.Id)) AddToView(task); // Add() may have mirrored it already
    });

    private void OnTaskRemoved(DownloadTask task) => Post(() =>
    {
        if (_index.Remove(task.Id, out var vm))
        {
            _wasRunning.Remove(vm);
            Tasks.Remove(vm);
        }
    });

    private void OnTaskUpdated(DownloadTask task) => Post(() =>
    {
        if (_index.TryGetValue(task.Id, out var vm)) _refreshItem(vm);
    });

    private void OnTaskCompleted(DownloadTask task) => Post(() =>
    {
        if (!_index.TryGetValue(task.Id, out var vm)) return;
        _refreshItem(vm);
        Completed?.Invoke(this, vm);
    });

    private void OnTaskMoved(DownloadTask task) => Post(SyncOrder);

    private void OnTicked(KernelSnapshot snapshot) => Post(() =>
    {
        Apply(snapshot);
        foreach (var vm in Tasks)
        {
            // Live rows refresh every tick. A row whose running state flipped
            // since the last tick also refreshes now: without this, the tick
            // that observes a transition would skip it (the row would keep its
            // previous presentation until some other event repainted it).
            bool running = _isRunning(vm);
            if (running || _wasRunning.Contains(vm)) _refreshItem(vm);
            if (running) _wasRunning.Add(vm); else _wasRunning.Remove(vm);
        }

        Ticked?.Invoke(this, EventArgs.Empty);
    });

    private void Post(Action action) => _dispatcher.Post(action);

    private void AddToView(DownloadTask task)
    {
        var vm = _createItem(task);
        _index[task.Id] = vm;
        Tasks.Add(vm);
    }

    /// <summary>Mirrors the kernel's queue order with Moves, so rows keep their identity and animation state.</summary>
    private void SyncOrder()
    {
        var desired = _kernel.Tasks;
        for (int i = 0; i < desired.Count; i++)
        {
            var task = desired[i];
            if (!_index.TryGetValue(task.Id, out var vm))
            {
                // A task whose TaskAdded event is still in flight (or was never
                // marshalled): materialize it at its desired position instead
                // of throwing.
                vm = _createItem(task);
                _index[task.Id] = vm;
                Tasks.Insert(i, vm);
            }

            int at = IndexOf(vm);
            if (at != i) Tasks.Move(at, i);
        }
    }

    private int IndexOf(TItem vm)
    {
        for (int i = 0; i < Tasks.Count; i++)
        {
            if (ReferenceEquals(Tasks[i], vm)) return i;
        }

        return -1;
    }

    private void Apply(KernelSnapshot snapshot)
    {
        TotalSpeed = snapshot.TotalSpeed;
        UploadSpeed = snapshot.UploadSpeed;
        SpeedHistory = snapshot.SpeedHistory;
        IsRunning = snapshot.IsRunning;
    }
}
