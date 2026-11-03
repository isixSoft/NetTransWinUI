namespace NetTrans.Kernel;

/// <summary>
/// Timer-driven simulated kernel, seeded with the handoff's own SEED array and
/// ticking on the same 900ms cadence and the same growth curve
/// (kbs = max(60, kbs * (0.93 + rand * 0.15))). It owns the task list, advances
/// the running tasks every tick and reports through IDownloadKernel's events.
/// Swap it for a real multi-segment HTTP/BT driver behind the same interface.
///
/// All state is guarded by one lock; events are raised outside it, on the
/// calling thread for commands and on the timer thread for ticks.
/// </summary>
public sealed class SimulatedDownloadKernel : IDownloadKernel
{
    private const int BlockCount = 96;
    private const int IslandSamples = 26;
    private const int SessionSamples = 40;
    private const double TickSeconds = 0.9;
    private const double Mb = 1024 * 1024;
    private const double Kb = 1024;

    private readonly object _gate = new();
    private readonly List<DownloadTask> _tasks = new();
    private readonly Random _random = new();
    private readonly double[] _speedHistory = new double[IslandSamples];
    private readonly System.Threading.Timer _timer;
    private int _nextId = 100;

    public SimulatedDownloadKernel()
    {
        _tasks.AddRange(Seed());

        for (int i = 0; i < _speedHistory.Length; i++)
        {
            _speedHistory[i] = (900 + _random.NextDouble() * 900) * Kb;
        }

        _timer = new System.Threading.Timer(Tick, null, TimeSpan.FromSeconds(TickSeconds), TimeSpan.FromSeconds(TickSeconds));
    }

    public IReadOnlyList<DownloadTask> Tasks
    {
        get { lock (_gate) return _tasks.ToArray(); }
    }

    public KernelSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new KernelSnapshot(
                    _tasks.Sum(t => t.Speed),
                    _tasks.Sum(t => t.UploadSpeed),
                    _tasks.Any(t => t.Status == DownloadStatus.Downloading),
                    (double[])_speedHistory.Clone());
            }
        }
    }

    public event Action<DownloadTask>? TaskAdded;
    public event Action<DownloadTask>? TaskRemoved;
    public event Action<DownloadTask>? TaskUpdated;
    public event Action<DownloadTask>? TaskCompleted;
    public event Action<DownloadTask>? TaskMoved;
    public event Action<KernelSnapshot>? Ticked;

    // ── the transfer loop ─────────────────────────────────────────────────
    private void Tick(object? state)
    {
        var completed = new List<DownloadTask>();

        lock (_gate)
        {
            foreach (var task in _tasks)
            {
                if (task.Status != DownloadStatus.Downloading) continue;

                task.Speed = Math.Max(60 * Kb, task.Speed * (0.93 + _random.NextDouble() * 0.15));
                task.Done = (long)Math.Min(task.Size, task.Done + task.Speed * TickSeconds);
                task.PeakSpeed = Math.Max(task.PeakSpeed, task.Speed);
                task.SpeedHistory = Push(task.SpeedHistory, task.Speed, SessionSamples);

                if (task.Done >= task.Size)
                {
                    task.Done = task.Size;
                    task.Speed = 0;
                    task.Status = DownloadStatus.Completed;
                    task.Connections = 0;
                    task.Checksum = "SHA-256 已校验";
                    task.ConnectionSpeeds = Array.Empty<double>();
                    task.Blocks = Enumerable.Repeat(1, BlockCount).ToArray();
                    task.Log.Add(new LogEntry(Stamp(), "下载完成"));
                    completed.Add(task);
                    continue;
                }

                task.Blocks = MakeBlocks(task.Done / (double)task.Size);
                task.ConnectionSpeeds = MakeConnections(task.Connections, task.Speed);
            }

            Array.Copy(_speedHistory, 1, _speedHistory, 0, _speedHistory.Length - 1);
            _speedHistory[^1] = _tasks.Sum(t => t.Speed);
        }

        foreach (var task in completed) TaskCompleted?.Invoke(task);
        Ticked?.Invoke(Snapshot);
    }

    // ── commands ──────────────────────────────────────────────────────────
    public void Toggle(int id)
    {
        DownloadTask? changed = null;

        lock (_gate)
        {
            var task = Find(id);
            if (task is null || task.Status == DownloadStatus.Completed) return;

            if (task.Status == DownloadStatus.Downloading)
            {
                task.Status = DownloadStatus.Paused;
                task.Speed = 0;
                task.ConnectionSpeeds = Array.Empty<double>();
                task.Log.Add(new LogEntry(Stamp(), "已暂停"));
            }
            else
            {
                task.Status = DownloadStatus.Downloading;
                task.Speed = (300 + _random.NextDouble() * 700) * Kb;
                task.Connections = task.Connections > 0 ? task.Connections : 8;
                task.ErrorMessage = null;
                task.Log.Add(new LogEntry(Stamp(), $"已建立 {task.Connections} 个连接"));
            }

            changed = task;
        }

        TaskUpdated?.Invoke(changed);
    }

    public void Remove(IEnumerable<int> ids)
    {
        var removed = new List<DownloadTask>();

        lock (_gate)
        {
            foreach (int id in ids)
            {
                if (Find(id) is not { } task) continue;
                _tasks.Remove(task);
                removed.Add(task);
            }
        }

        foreach (var task in removed) TaskRemoved?.Invoke(task);
    }

    public void ToggleAll()
    {
        var changed = new List<DownloadTask>();

        lock (_gate)
        {
            bool pausing = _tasks.Any(t => t.Status == DownloadStatus.Downloading);

            foreach (var task in _tasks)
            {
                if (task.Status == DownloadStatus.Completed) continue;
                task.Status = pausing ? DownloadStatus.Paused : DownloadStatus.Downloading;
                task.Speed = pausing ? 0 : (300 + _random.NextDouble() * 800) * Kb;
                if (!pausing && task.Connections == 0) task.Connections = 8;
                changed.Add(task);
            }
        }

        foreach (var task in changed) TaskUpdated?.Invoke(task);
    }

    public void MoveToFront(int id)
    {
        DownloadTask? moved = null;

        lock (_gate)
        {
            int index = _tasks.FindIndex(t => t.Id == id);
            if (index > 0)
            {
                var task = _tasks[index];
                _tasks.RemoveAt(index);
                _tasks.Insert(0, task);
                moved = task;
            }
        }

        if (moved is not null) TaskMoved?.Invoke(moved);
    }

    public void MoveToBack(int id)
    {
        DownloadTask? moved = null;

        lock (_gate)
        {
            int index = _tasks.FindIndex(t => t.Id == id);
            if (index >= 0 && index < _tasks.Count - 1)
            {
                var task = _tasks[index];
                _tasks.RemoveAt(index);
                _tasks.Add(task);
                moved = task;
            }
        }

        if (moved is not null) TaskMoved?.Invoke(moved);
    }

    public void Redownload(int id)
    {
        DownloadTask? changed = null;

        lock (_gate)
        {
            var task = Find(id);
            if (task is null) return;

            task.Done = 0;
            task.Status = DownloadStatus.Downloading;
            task.Speed = (420 + _random.NextDouble() * 600) * Kb;
            task.Connections = task.Connections > 0 ? task.Connections : 8;
            task.Checksum = null;
            task.Blocks = MakeBlocks(0);
            task.Log.Add(new LogEntry(Stamp(), "按新版本重新开始下载"));
            task.NewerVersion = null;
            changed = task;
        }

        TaskUpdated?.Invoke(changed);
    }

    public void SetPriority(int id, TaskPriority priority)
    {
        DownloadTask? changed = null;

        lock (_gate)
        {
            var task = Find(id);
            if (task is null || task.Priority == priority) return;
            task.Priority = priority;
            changed = task;
        }

        TaskUpdated?.Invoke(changed);
    }

    public void SetSpeedLimit(int id, string limit)
    {
        DownloadTask? changed = null;

        lock (_gate)
        {
            var task = Find(id);
            if (task is null || task.SpeedLimit == limit) return;
            task.SpeedLimit = limit;
            changed = task;
        }

        TaskUpdated?.Invoke(changed);
    }

    public DownloadTask Add(NewDownloadRequest request)
    {
        DownloadTask task;

        lock (_gate)
        {
            task = BuildTask(request);
            _tasks.Add(task);
        }

        TaskAdded?.Invoke(task);
        return task;
    }

    public void Dispose() => _timer.Dispose();

    private DownloadTask? Find(int id)
    {
        return _tasks.FirstOrDefault(t => t.Id == id);
    }

    // ── the handoff's mkBlocks / mkConns ──────────────────────────────────
    private int[] MakeBlocks(double fraction)
    {
        var blocks = new int[BlockCount];
        for (int i = 0; i < BlockCount; i++)
        {
            double at = i / (double)BlockCount;
            blocks[i] = at < fraction ? 1
                : _random.NextDouble() < 0.05 && at < fraction + 0.12 ? 2
                : 0;
        }

        return blocks;
    }

    private double[] MakeConnections(int count, double speed)
    {
        if (count <= 0) return Array.Empty<double>();

        var values = new double[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = speed > 0 ? speed / count * (0.55 + _random.NextDouble() * 0.9) : 0;
        }

        return values;
    }

    private static double[] Push(double[] history, double value, int cap)
    {
        if (history.Length < cap) return history.Append(value).ToArray();
        var next = new double[cap];
        Array.Copy(history, 1, next, 0, cap - 1);
        next[^1] = value;
        return next;
    }

    private static string Stamp() => DateTime.Now.ToString("HH:mm");

    private static string FileNameFrom(string url)
    {
        string trimmed = url.Split('?')[0].TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        string name = slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
        return name.Length == 0 ? "未命名下载" : name;
    }

    private static string HostFrom(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : "未知来源";

    private static FileKind KindFrom(string name)
    {
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".iso" or ".img" or ".dmg" or ".torrent" => FileKind.Disc,
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" => FileKind.Film,
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".zst" => FileKind.Zip,
            ".flac" or ".mp3" or ".m4a" or ".wav" => FileKind.Music,
            _ => FileKind.Doc,
        };
    }

    private static string TintFor(FileKind kind) => kind switch
    {
        FileKind.Disc => "#FF9500",
        FileKind.Film => "#AF52DE",
        FileKind.Zip => "#5856D6",
        FileKind.Music => "#FF2D55",
        _ => "#0A84FF",
    };

    private DownloadTask BuildTask(NewDownloadRequest request)
    {
        string name = FileNameFrom(request.Url);
        var kind = KindFrom(name);

        var task = new DownloadTask
        {
            Id = _nextId++,
            Name = name,
            Host = HostFrom(request.Url),
            Kind = kind,
            Size = (long)((80 + _random.NextDouble() * 900) * Mb),
            Category = request.Category,
            Tint = TintFor(kind),
            Url = request.Url,
            SavePath = request.SaveTo,
            Priority = request.Priority,
            Connections = request.StartNow ? request.Connections : 0,
            Status = request.StartNow ? DownloadStatus.Downloading : DownloadStatus.Queued,
            Speed = request.StartNow ? (300 + _random.NextDouble() * 700) * Kb : 0,
            AddedAt = "今天 " + DateTime.Now.ToString("HH:mm"),
        };

        task.Blocks = MakeBlocks(0);
        task.Log.Add(new LogEntry(Stamp(), "任务已创建"));
        task.Log.Add(new LogEntry(Stamp(), request.StartNow ? $"已连接 {task.Host}" : "已加入队列，等待空闲通道"));

        return task;
    }

    // ── seed data: the handoff's SEED, MB converted to bytes ──────────────
    private IEnumerable<DownloadTask> Seed()
    {
        yield return SeedTask(1, "ubuntu-24.04.2-desktop.iso", FileKind.Disc, "#FF9500", "soft", 5940, 3742, 1180,
            DownloadStatus.Downloading, 8, "releases.ubuntu.com",
            "https://releases.ubuntu.com/24.04/ubuntu-24.04.2-desktop-amd64.iso",
            checksum: "SHA-256 待校验", priority: TaskPriority.High,
            newer: new NewVersionInfo("ubuntu-24.04.3-desktop.iso", (long)(6042 * Mb), "2 天前"));

        yield return SeedTask(3, "4k-timelapse-reel.mp4", FileKind.Film, "#AF52DE", "video", 1180, 486, 742,
            DownloadStatus.Downloading, 6, "cdn.video-host.net",
            "https://cdn.video-host.net/stream/8841/4k-timelapse-reel.mp4");

        yield return SeedTask(5, "source-sans-pack.zip", FileKind.Zip, "#5856D6", "doc", 86, 21, 214,
            DownloadStatus.Downloading, 4, "fonts.mirror.dev",
            "https://fonts.mirror.dev/packs/source-sans-3-complete.zip");

        yield return SeedTask(4, "arch-linux-2026.08.torrent", FileKind.Disc, "#8E8E93", "bt", 3210, 1104, 0,
            DownloadStatus.Paused, 0, "12 个节点", "magnet:?xt=urn:btih:9f2c1a…",
            peers: 12, seeds: 4, ratio: 0.42, upload: 86);

        yield return SeedTask(7, "logic-samples-vol3.flac", FileKind.Music, "#FF2D55", "music", 740, 96, 0,
            DownloadStatus.Error, 0, "cdn.audio-lib.net",
            "https://cdn.audio-lib.net/vol3/logic-samples-vol3.flac",
            retries: 2, error: "连接被服务器重置",
            log: new[]
            {
                new LogEntry("09:38", "任务已创建"),
                new LogEntry("09:39", "已连接 cdn.audio-lib.net"),
                new LogEntry("09:44", "连接被服务器重置", IsError: true),
                new LogEntry("09:44", "第 2 次重试失败", IsError: true),
            });

        yield return SeedTask(8, "design-system-handoff.pdf", FileKind.Doc, "#0A84FF", "doc", 62, 0, 0,
            DownloadStatus.Queued, 0, "docs.internal", "https://docs.internal/handoff/design-system.pdf",
            log: new[] { new LogEntry("09:45", "已加入队列，等待空闲通道") });

        yield return SeedTask(2, "blender-4.2-portable.7z", FileKind.Zip, "#34C759", "soft", 412, 412, 0,
            DownloadStatus.Completed, 0, "mirror.blender.org",
            "https://mirror.blender.org/release/blender-4.2.7z",
            checksum: "SHA-256 已校验",
            newer: new NewVersionInfo("blender-4.2.1-portable.7z", (long)(418 * Mb), "今天 08:12"));

        yield return SeedTask(6, "quarterly-report-q2.pdf", FileKind.Doc, "#FF3B30", "doc", 14, 14, 0,
            DownloadStatus.Completed, 0, "docs.internal", "https://docs.internal/reports/2026-q2.pdf");
    }

    private DownloadTask SeedTask(
        int id, string name, FileKind kind, string tint, string category,
        double sizeMb, double doneMb, double speedKb,
        DownloadStatus status, int connections, string host, string url,
        string? checksum = null, TaskPriority priority = TaskPriority.Normal,
        NewVersionInfo? newer = null, int retries = 0, string? error = null,
        int? peers = null, int? seeds = null, double? ratio = null, double upload = 0,
        LogEntry[]? log = null)
    {
        var task = new DownloadTask
        {
            Id = id,
            Name = name,
            Kind = kind,
            Tint = tint,
            Category = category,
            Size = (long)(sizeMb * Mb),
            Done = (long)(doneMb * Mb),
            Speed = speedKb * Kb,
            Status = status,
            Connections = connections,
            Host = host,
            Url = url,
            Checksum = checksum,
            Priority = priority,
            NewerVersion = newer,
            Retries = retries,
            ErrorMessage = error,
            Peers = peers,
            Seeds = seeds,
            Ratio = ratio,
            UploadSpeed = upload * Kb,
            AddedAt = "今天 09:41",
        };

        task.PeakSpeed = task.Speed * 1.24;
        task.Blocks = MakeBlocks(task.Size == 0 ? 0 : task.Done / (double)task.Size);
        task.ConnectionSpeeds = MakeConnections(connections, task.Speed);
        task.SpeedHistory = Enumerable.Range(0, SessionSamples)
            .Select(_ => task.Speed * (0.6 + _random.NextDouble() * 0.7))
            .ToArray();

        task.Log.AddRange(log ?? new[]
        {
            new LogEntry("09:41", "任务已创建"),
            new LogEntry("09:41", $"已连接 {host}"),
            new LogEntry("09:41", "服务器支持断点续传"),
            new LogEntry("09:42", $"已建立 {connections} 个连接"),
        });

        return task;
    }
}
