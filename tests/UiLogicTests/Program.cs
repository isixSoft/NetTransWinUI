// Headless tests for the UI/kernel seam logic, runnable on plain net8.0:
//   1. the generic task manager's event mirroring, marshalling and derived
//      aggregates (against a programmable FakeKernel + a counting dispatcher),
//   2. the app's FormatHelpers rendering (linked from NetTrans/Services),
//   3. the kernel's speed-limit parsing (reached by reflection because
//      RealDownloadKernel.ParseLimit is private and the file is off-limits).
// Style: main + Check(name, condition, detail) + summary; exit 1 on failure.

using System.Reflection;
using NetTrans.Kernel;
using NetTrans.Services;

int passed = 0, failed = 0;

void Check(string name, bool condition, string detail = "")
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}{(detail.Length > 0 ? $"  [{detail}]" : "")}");
    }
}

void Section(string title) => Console.WriteLine($"\n== {title} ==");

// ── 1. the task manager seam ──────────────────────────────────────────────

Section("TaskManager · constructor seeds the mirror");
{
    var kernel = new FakeKernel();
    var t1 = kernel.MakeTask("a.zip", DownloadStatus.Downloading);
    var t2 = kernel.MakeTask("b.mp4", DownloadStatus.Paused);
    kernel.SnapshotValue = new KernelSnapshot(512, 64, IsRunning: true, new[] { 9.0, 8.0 });

    var dispatcher = new TestDispatcher();
    var manager = NewManager(kernel, dispatcher);

    Check("tasks are mirrored from the kernel queue in order",
        manager.Tasks.Count == 2
        && ReferenceEquals(manager.Tasks[0].Task, t1)
        && ReferenceEquals(manager.Tasks[1].Task, t2),
        $"count={manager.Tasks.Count}");
    Check("snapshot aggregates are applied at construction",
        manager.TotalSpeed == 512 && manager.UploadSpeed == 64
        && manager.IsRunning
        && manager.SpeedHistory is [9, 8],
        $"speed={manager.TotalSpeed} up={manager.UploadSpeed} running={manager.IsRunning}");
}

Section("TaskManager · TaskAdded / Add()");
{
    var kernel = new FakeKernel();
    kernel.MakeTask("a.zip", DownloadStatus.Downloading);
    kernel.MakeTask("b.mp4", DownloadStatus.Paused);
    var manager = NewManager(kernel, new TestDispatcher());

    var t3 = kernel.Add(Req("c.zip")); // kernel fires TaskAdded inline
    Check("TaskAdded appends a view item for the new task",
        manager.Tasks.Count == 3 && ReferenceEquals(manager.Tasks[2].Task, t3),
        $"count={manager.Tasks.Count}");

    var vm = manager.Add(Req("d.zip"));
    Check("Add() returns the wrapper and the mirror already holds it",
        vm.Task.Name == "d.zip" && manager.Tasks.Count == 4
        && ReferenceEquals(manager.Tasks[3], vm),
        $"count={manager.Tasks.Count} id={vm.Task.Id}");
}

Section("TaskManager · TaskRemoved");
{
    var kernel = new FakeKernel();
    var t1 = kernel.MakeTask("a.zip", DownloadStatus.Downloading);
    kernel.MakeTask("b.mp4", DownloadStatus.Paused);
    var manager = NewManager(kernel, new TestDispatcher());

    kernel.RaiseTaskRemoved(t1);
    Check("TaskRemoved drops the mapped item",
        manager.Tasks.Count == 1 && manager.Tasks.All(vm => vm.Task.Id != t1.Id),
        $"count={manager.Tasks.Count}");

    kernel.RaiseTaskRemoved(kernel.MakeTask("ghost.zip", DownloadStatus.Queued, track: false));
    Check("removal of an unknown task is ignored",
        manager.Tasks.Count == 1,
        $"count={manager.Tasks.Count}");
}

Section("TaskManager · TaskUpdated / TaskCompleted");
{
    var kernel = new FakeKernel();
    var t1 = kernel.MakeTask("a.zip", DownloadStatus.Paused);
    var t2 = kernel.MakeTask("b.mp4", DownloadStatus.Queued);
    var dispatcher = new TestDispatcher();
    var manager = NewManager(kernel, dispatcher);

    kernel.RaiseTaskUpdated(t1);
    Check("TaskUpdated refreshes exactly the mapped item",
        manager.Tasks[0].Refreshes == 1 && manager.Tasks[1].Refreshes == 0
        && dispatcher.Posted == 1,
        $"r1={manager.Tasks[0].Refreshes} r2={manager.Tasks[1].Refreshes}");

    var completed = new List<TestItem>();
    manager.Completed += (_, vm) => completed.Add(vm);

    t2.Status = DownloadStatus.Completed;
    kernel.RaiseTaskCompleted(t2);
    Check("TaskCompleted refreshes the item and raises Completed with it",
        completed.Count == 1 && ReferenceEquals(completed[0], manager.Tasks[1])
        && manager.Tasks[1].Refreshes == 1,
        $"completed={completed.Count} refreshes={manager.Tasks[1].Refreshes}");

    kernel.RaiseTaskCompleted(kernel.MakeTask("ghost.zip", DownloadStatus.Completed, track: false));
    Check("completion of an unknown task is ignored",
        completed.Count == 1,
        $"completed={completed.Count}");
}

Section("TaskManager · TaskMoved reorders with Moves");
{
    var kernel = new FakeKernel();
    kernel.MakeTask("a.zip", DownloadStatus.Queued);
    kernel.MakeTask("b.mp4", DownloadStatus.Queued);
    kernel.MakeTask("c.zip", DownloadStatus.Queued);
    var manager = NewManager(kernel, new TestDispatcher());

    kernel.Reorder(3, 1, 2);
    kernel.RaiseTaskMoved();
    Check("the mirror matches the kernel's queue order",
        manager.Tasks.Select(vm => vm.Task.Id).SequenceEqual([3, 1, 2]),
        $"order={string.Join(',', manager.Tasks.Select(vm => vm.Task.Id))}");
}

Section("TaskManager · Ticked applies aggregates and refreshes live rows");
{
    var kernel = new FakeKernel();
    kernel.MakeTask("live.zip", DownloadStatus.Downloading);
    kernel.MakeTask("idle.mp4", DownloadStatus.Paused);
    var dispatcher = new TestDispatcher();
    var manager = NewManager(kernel, dispatcher);

    int ticks = 0;
    manager.Ticked += (_, _) => ticks++;
    kernel.RaiseTicked(new KernelSnapshot(2048, 256, IsRunning: true, new[] { 1.0, 2.0, 3.0 }));

    Check("the snapshot aggregates land on the manager",
        manager.TotalSpeed == 2048 && manager.UploadSpeed == 256 && manager.IsRunning
        && manager.SpeedHistory is [1, 2, 3],
        $"speed={manager.TotalSpeed} up={manager.UploadSpeed} running={manager.IsRunning}");
    Check("only the running rows are refreshed on a tick",
        manager.Tasks[0].Refreshes == 1 && manager.Tasks[1].Refreshes == 0,
        $"live={manager.Tasks[0].Refreshes} idle={manager.Tasks[1].Refreshes}");
    Check("the tick event fires exactly once per kernel tick",
        ticks == 1 && dispatcher.Posted == 1,
        $"ticks={ticks} posted={dispatcher.Posted}");
}

Section("TaskManager · kernel events are marshalled through the dispatcher");
{
    var kernel = new FakeKernel();
    var dispatcher = new TestDispatcher { RunInline = false }; // kernel thread
    var manager = NewManager(kernel, dispatcher);

    var t1 = kernel.MakeTask("a.zip", DownloadStatus.Downloading);
    kernel.RaiseTaskAdded(t1);
    Check("a posted event does not touch the mirror before the flush",
        manager.Tasks.Count == 0 && dispatcher.Pending == 1,
        $"count={manager.Tasks.Count} pending={dispatcher.Pending}");

    dispatcher.Flush();
    Check("the flush applies the event on the host context",
        manager.Tasks.Count == 1 && ReferenceEquals(manager.Tasks[0].Task, t1),
        $"count={manager.Tasks.Count}");

    kernel.RaiseTaskRemoved(t1);
    Check("a posted removal waits for the next flush",
        manager.Tasks.Count == 1 && dispatcher.Pending == 1,
        $"count={manager.Tasks.Count}");

    kernel.RaiseTicked(new KernelSnapshot(4096, 0, IsRunning: false, Array.Empty<double>()));
    Check("a posted tick leaves the aggregates stale until the flush",
        manager.Tasks.Count == 1 && manager.TotalSpeed == 0 && dispatcher.Pending == 2,
        $"speed={manager.TotalSpeed} pending={dispatcher.Pending}");

    dispatcher.Flush();
    Check("after the flush the mirror and aggregates are current",
        manager.Tasks.Count == 0 && manager.TotalSpeed == 4096,
        $"count={manager.Tasks.Count} speed={manager.TotalSpeed}");
    Check("every kernel event went through exactly one Post",
        dispatcher.Posted == 3,
        $"posted={dispatcher.Posted}");
}

Section("TaskManager · commands forward intent and re-apply the snapshot");
{
    var kernel = new FakeKernel();
    kernel.MakeTask("a.zip", DownloadStatus.Downloading);
    var manager = NewManager(kernel, new TestDispatcher());

    kernel.SnapshotValue = new KernelSnapshot(7, 0, IsRunning: true, Array.Empty<double>());
    manager.Toggle(1);
    manager.Remove(new[] { 1, 2 });
    manager.ToggleAll();
    manager.MoveToFront(1);
    manager.MoveToBack(2);
    manager.Redownload(1);
    manager.SetPriority(1, TaskPriority.High);
    manager.SetSpeedLimit(1, "1 MB/s");

    Check("each command reaches the kernel with its arguments",
        string.Join('|', kernel.Calls)
        == "Toggle:1|Remove:1,2|ToggleAll|MoveToFront:1|MoveToBack:2|Redownload:1|SetPriority:1:High|SetSpeedLimit:1:1 MB/s",
        $"calls={string.Join('|', kernel.Calls)}");
    Check("a command re-applies the snapshot immediately, without a tick",
        manager.TotalSpeed == 7 && manager.IsRunning,
        $"speed={manager.TotalSpeed}");
}

// ── 2. FormatHelpers (linked straight from NetTrans/Services) ─────────────

Section("FormatHelpers · Bytes");
{
    Check("0 renders as \"0 MB\"", FormatHelpers.Bytes(0) == "0 MB", FormatHelpers.Bytes(0));
    Check("a half MB rounds down to \"0 MB\"", FormatHelpers.Bytes(512 * 1024) == "0 MB",
        FormatHelpers.Bytes(512 * 1024));
    Check("small values render as whole MB", FormatHelpers.Bytes(2 * 1024 * 1024) == "2 MB",
        FormatHelpers.Bytes(2 * 1024 * 1024));
    Check("just under 1 GB still renders in MB",
        FormatHelpers.Bytes(1024L * 1024 * 1024 - 1) == "1024 MB",
        FormatHelpers.Bytes(1024L * 1024 * 1024 - 1));
    Check("1 GB exactly switches to two decimals",
        FormatHelpers.Bytes(1024L * 1024 * 1024) == "1.00 GB",
        FormatHelpers.Bytes(1024L * 1024 * 1024));
    Check("fractional GB keeps two decimals", FormatHelpers.Bytes(1.5 * 1024 * 1024 * 1024) == "1.50 GB",
        FormatHelpers.Bytes(1.5 * 1024 * 1024 * 1024));
    Check("TB-scale sizes render as GB (no TB unit)",
        FormatHelpers.Bytes(2.25 * 1024L * 1024 * 1024 * 1024) == "2304.00 GB",
        FormatHelpers.Bytes(2.25 * 1024L * 1024 * 1024 * 1024));
}

Section("FormatHelpers · Speed / SpeedOrDash / SpeedParts");
{
    Check("zero speed renders empty", FormatHelpers.Speed(0) == "", FormatHelpers.Speed(0));
    Check("negative speed renders empty", FormatHelpers.Speed(-5) == "", FormatHelpers.Speed(-5));
    Check("sub-MB speeds render as whole KB/s",
        FormatHelpers.Speed(100 * 1024) == "100 KB/s", FormatHelpers.Speed(100 * 1024));
    Check("half a MB/s still renders in KB/s",
        FormatHelpers.Speed(0.5 * 1024 * 1024) == "512 KB/s", FormatHelpers.Speed(0.5 * 1024 * 1024));
    Check("1 MB/s exactly switches to one decimal",
        FormatHelpers.Speed(1024 * 1024) == "1.0 MB/s", FormatHelpers.Speed(1024 * 1024));
    Check("fractional MB/s keeps one decimal",
        FormatHelpers.Speed(1.26 * 1024 * 1024) == "1.3 MB/s", FormatHelpers.Speed(1.26 * 1024 * 1024));
    Check("SpeedOrDash renders an em dash at zero",
        FormatHelpers.SpeedOrDash(0) == "—", FormatHelpers.SpeedOrDash(0));
    Check("SpeedOrDash forwards real speeds",
        FormatHelpers.SpeedOrDash(100 * 1024) == "100 KB/s", FormatHelpers.SpeedOrDash(100 * 1024));
    Check("SpeedParts splits value and unit below 1 MB/s",
        FormatHelpers.SpeedParts(999 * 1024) == ("999", "KB/s"),
        $"{FormatHelpers.SpeedParts(999 * 1024).Item1}/{FormatHelpers.SpeedParts(999 * 1024).Item2}");
    Check("SpeedParts splits value and unit above 1 MB/s",
        FormatHelpers.SpeedParts(1.5 * 1024 * 1024) == ("1.5", "MB/s"),
        $"{FormatHelpers.SpeedParts(1.5 * 1024 * 1024).Item1}/{FormatHelpers.SpeedParts(1.5 * 1024 * 1024).Item2}");
}

Section("FormatHelpers · Eta");
{
    Check("a stalled download reports 计算中", FormatHelpers.Eta(1000, 0) == "计算中");
    Check("zero remaining reports 计算中", FormatHelpers.Eta(0, 100) == "计算中");
    Check("negative remaining reports 计算中", FormatHelpers.Eta(-100, 5) == "计算中");
    Check("thirty seconds renders seconds only", FormatHelpers.Eta(30_000, 1000) == "剩余 30 秒",
        FormatHelpers.Eta(30_000, 1000));
    Check("ninety seconds renders minutes and seconds",
        FormatHelpers.Eta(90_000, 1000) == "剩余 1 分 30 秒", FormatHelpers.Eta(90_000, 1000));
    Check("exactly 99 minutes is still rendered",
        FormatHelpers.Eta(5940_000, 1000) == "剩余 99 分 0 秒", FormatHelpers.Eta(5940_000, 1000));
    Check("past 99 minutes falls back to 计算中",
        FormatHelpers.Eta(5941_000, 1000) == "计算中", FormatHelpers.Eta(5941_000, 1000));
}

// ── 3. the kernel's speed-limit parsing (via reflection) ──────────────────

Section("Kernel limit parsing · RealDownloadKernel.ParseLimit");
{
    var parse = typeof(RealDownloadKernel).GetMethod("ParseLimit", BindingFlags.NonPublic | BindingFlags.Static);
    Check("ParseLimit is reachable for testing (private static in RealDownloadKernel)",
        parse is not null && parse.GetParameters().Length == 1,
        "the parallel kernel refactor may have moved or renamed it");

    if (parse is not null)
    {
        long? Limit(string? text)
        {
            try
            {
                return (long?)parse.Invoke(null, new object?[] { text });
            }
            catch (Exception)
            {
                return null;
            }
        }

        Check("\"不限\" parses to null (unlimited)", Limit("不限") is null);
        Check("null parses to null", Limit(null) is null);
        Check("empty text parses to null", Limit("") is null);
        Check("whitespace parses to null", Limit("  ") is null);
        Check("\"1 MB/s\" parses to 1048576 B/s", Limit("1 MB/s") == 1048576,
            Limit("1 MB/s")?.ToString() ?? "null");
        Check("a missing space is accepted", Limit("1MB/s") == 1048576, Limit("1MB/s")?.ToString() ?? "null");
        Check("KB/s multiplies by 1024", Limit("512 KB/s") == 524288, Limit("512 KB/s")?.ToString() ?? "null");
        Check("B/s passes through", Limit("100 B/s") == 100, Limit("100 B/s")?.ToString() ?? "null");
        Check("fractional values truncate to long", Limit("1.5 MB/s") == 1572864,
            Limit("1.5 MB/s")?.ToString() ?? "null");
        Check("GB/s multiplies by 1024^3", Limit("2 GB/s") == 2147483648,
            Limit("2 GB/s")?.ToString() ?? "null");
        Check("units are case-insensitive", Limit("1 mb/s") == 1048576, Limit("1 mb/s")?.ToString() ?? "null");
        Check("non-numeric text parses to null", Limit("abc") is null);
        Check("unknown units parse to null", Limit("1 TB/s") is null);
        Check("negative caps parse to null", Limit("-1 MB/s") is null);
        Check("a bare number without a unit parses to null", Limit("1 MB") is null);
    }
}

Console.WriteLine($"\n== summary: {passed} passed, {failed} failed ==");
return failed == 0 ? 0 : 1;

// ── helpers ───────────────────────────────────────────────────────────────

static TaskManager<TestItem> NewManager(FakeKernel kernel, TestDispatcher dispatcher) =>
    new(kernel, dispatcher, task => new TestItem(task), vm => vm.Refresh(), vm => vm.IsRunning);

static NewDownloadRequest Req(string name) =>
    new($"https://example.com/{name}", @"D:\Downloads", "soft", Connections: 8,
        TaskPriority.Normal, StartNow: true);

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
