using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using NetTrans.Kernel;
using NetTrans.ViewModels;

namespace NetTrans.Services;

/// <summary>
/// The task manager: the single seam between the download kernel and the UI.
/// It mirrors the kernel's queue into the observable collection the shell
/// binds, marshals kernel events (which arrive on kernel threads) onto the UI
/// thread, and forwards user intent back down as kernel commands. View models
/// and views see only this class -- never the kernel.
/// </summary>
public sealed class TaskManager
{
    private readonly IDownloadKernel _kernel;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<int, DownloadItemViewModel> _index = new();

    public TaskManager(IDownloadKernel kernel, DispatcherQueue dispatcher)
    {
        _kernel = kernel;
        _dispatcher = dispatcher;

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
    public ObservableCollection<DownloadItemViewModel> Tasks { get; } = new();

    public double TotalSpeed { get; private set; }
    public double UploadSpeed { get; private set; }

    /// <summary>The island's 26-bar sparkline, oldest sample first.</summary>
    public IReadOnlyList<double> SpeedHistory { get; private set; } = System.Array.Empty<double>();

    /// <summary>True while at least one task is running -- drives the toolbar's play/pause glyph.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Raised once per tick so live-only readouts can refresh without a collection change.</summary>
    public event EventHandler? Ticked;

    /// <summary>Raised when a task finishes, so the shell can drop the completion banner.</summary>
    public event EventHandler<DownloadItemViewModel>? Completed;

    // ── user intent, forwarded to the kernel ──────────────────────────────
    public DownloadItemViewModel Add(NewDownloadRequest request)
    {
        var task = _kernel.Add(request); // TaskAdded fires inline on this thread
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

    /// <summary>Runs a kernel command, then refreshes the aggregates at once -- the way the old engine recomputed after each command.</summary>
    private void Command(Action action)
    {
        action();
        Apply(_kernel.Snapshot);
    }

    // ── kernel events, marshalled onto the UI thread ──────────────────────
    private void OnTaskAdded(DownloadTask task) => Post(() => AddToView(task));

    private void OnTaskRemoved(DownloadTask task) => Post(() =>
    {
        if (_index.Remove(task.Id, out var vm)) Tasks.Remove(vm);
    });

    private void OnTaskUpdated(DownloadTask task) => Post(() =>
    {
        if (_index.TryGetValue(task.Id, out var vm)) vm.Refresh();
    });

    private void OnTaskCompleted(DownloadTask task) => Post(() =>
    {
        if (!_index.TryGetValue(task.Id, out var vm)) return;
        vm.Refresh();
        Completed?.Invoke(this, vm);
    });

    private void OnTaskMoved(DownloadTask task) => Post(SyncOrder);

    private void OnTicked(KernelSnapshot snapshot) => Post(() =>
    {
        Apply(snapshot);
        foreach (var vm in Tasks)
        {
            if (vm.IsRunning) vm.Refresh();
        }

        Ticked?.Invoke(this, EventArgs.Empty);
    });

    private void Post(Action action)
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

    private void AddToView(DownloadTask task)
    {
        var vm = new DownloadItemViewModel(task);
        _index[task.Id] = vm;
        Tasks.Add(vm);
    }

    /// <summary>Mirrors the kernel's queue order with Moves, so rows keep their identity and animation state.</summary>
    private void SyncOrder()
    {
        var desired = _kernel.Tasks;
        for (int i = 0; i < desired.Count; i++)
        {
            var vm = _index[desired[i].Id];
            int at = IndexOf(vm);
            if (at != i) Tasks.Move(at, i);
        }
    }

    private int IndexOf(DownloadItemViewModel vm)
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
