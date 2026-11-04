using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetTrans.Kernel.Net;
using NetTrans.Kernel.Protocol;
using NetTrans.Kernel.Transfer;

namespace NetTrans.Kernel;

/// <summary>
/// The real kernel. Protocol drivers probe the resource, a
/// <see cref="TransferSession"/> moves the bytes, and this class owns task
/// state, the priority queue, MaxSimultaneous scheduling, live speed limiting
/// and the same 900 ms event cadence as the simulated kernel — so
/// TaskManager cannot tell them apart. State lives behind one lock; events
/// fire outside it; the tick runs on a kernel-owned timer thread.
/// </summary>
public sealed class RealDownloadKernel : IDownloadKernel
{
    private const int BlockCount = 96;
    private const int IslandSamples = 26;
    private const int SessionSamples = 40;
    private const double TickSeconds = 0.9;

    private readonly KernelOptions _options;
    private readonly HttpClient _http;
    private readonly ProtocolDriverRegistry _drivers;
    private readonly HashDatabase _hashes;
    private readonly object _gate = new();
    private readonly List<DownloadTask> _tasks = new();
    private readonly Dictionary<int, Runtime> _runtimes = new();
    private readonly double[] _speedHistory = new double[IslandSamples];
    private readonly System.Threading.Timer _timer;
    private int _nextId = 100;
    private int _active;
    private bool? _lastWindowExempt;
    private readonly SpeedLimiter _globalLimiter = new(0); // 共享全局桶：所有任务合计受全局限速约束

    private sealed class Runtime
    {
        public required DownloadTask Task { get; init; }
        public SpeedLimiter Limiter { get; } = new();
        public IReadOnlyDictionary<string, string>? Headers { get; init; }
        public CancellationTokenSource? Cts;
        public TransferSession? Session;
        public long LastDone;
        public int Generation;
        public bool Removed;
        public bool Restart;

        /// <summary>
        /// Save path fixed at the first start, after the probe names the file:
        /// every later start/resume of the same task writes to this exact path,
        /// so resume keeps appending to its own interrupted file and the
        /// collision rewrite is decided exactly once per task.
        /// </summary>
        public string? SavePath;
    }

    public RealDownloadKernel(KernelOptions? options = null)
    {
        _options = options ?? new KernelOptions();
        _http = BuildClient(_options);
        _drivers = new ProtocolDriverRegistry(_options, _http);
        _hashes = new HashDatabase(_options.HashDatabasePath);
        _timer = new System.Threading.Timer(
            Tick, null, TimeSpan.FromSeconds(TickSeconds), TimeSpan.FromSeconds(TickSeconds));
    }

    private static HttpClient BuildClient(KernelOptions options)
    {
        var handler = options.HttpHandlerFactory?.Invoke() ?? BuildHandler(options);
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        }
        catch
        {
            // a malformed user agent string must not keep the kernel from starting
        }

        return client;
    }

    private static HttpMessageHandler BuildHandler(KernelOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None, // byte-exact progress accounting
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 64,
        };

        if (options.Proxy is not { Length: > 0 } proxy) return handler;

        // ProxyInfo.Parse validates the string first; a proxy it cannot parse
        // keeps the previous fallback behavior below (Uri/WebProxy reject it
        // exactly as they always did).
        var proxyInfo = ProxyInfo.Parse(proxy);
        if (proxyInfo?.Scheme == "socks5"
            || (proxyInfo is null && proxy.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)))
        {
            // Userinfo survives in Uri.UserInfo, so Socks5Connector picks up the
            // RFC 1929 credentials straight from it.
            var uri = new Uri(proxy);
            handler.ConnectCallback = async (context, cancellation) =>
                await Socks5Connector.ConnectAsync(
                    uri, context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellation);
            return handler;
        }

        if (proxyInfo is { Username: not null } parsed)
        {
            handler.Proxy = new System.Net.WebProxy(
                new UriBuilder(parsed.Scheme, parsed.Host, parsed.Port).Uri)
            {
                Credentials = new System.Net.NetworkCredential(
                    parsed.Username, parsed.Password ?? string.Empty),
            };
            return handler;
        }

        handler.Proxy = new System.Net.WebProxy(proxy);
        return handler;
    }

    // ── IDownloadKernel surface ───────────────────────────────────────────
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
                    _tasks.Where(t => t.Status == DownloadStatus.Downloading).Sum(t => t.Speed),
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

    public DownloadTask Add(NewDownloadRequest request)
    {
        DownloadTask task;

        lock (_gate)
        {
            task = BuildTask(_nextId++, request);
            _tasks.Add(task);
            var runtime = new Runtime { Task = task, Headers = request.Headers };
            _runtimes[task.Id] = runtime;
            ApplyLimitLocked(runtime);
        }

        TaskAdded?.Invoke(task);
        Log(task, "任务已创建");

        if (request.StartNow)
        {
            Log(task, "已加入队列，等待空闲通道");
            Schedule();
        }
        else
        {
            Log(task, "已暂停，等待手动开始");
        }

        return task;
    }

    public void Toggle(int id)
    {
        DownloadTask? task;
        bool scheduled = false;

        lock (_gate)
        {
            task = Find(id);
            if (task is null || task.Status == DownloadStatus.Completed) return;
            var runtime = _runtimes[id];

            if (task.Status == DownloadStatus.Downloading)
            {
                // flip the status now; the worker observes the cancel and winds down
                task.Status = DownloadStatus.Paused;
                task.Speed = 0;
                task.Connections = 0;
                task.ConnectionSpeeds = Array.Empty<double>();
                runtime.Cts?.Cancel();
                Log(task, "已暂停");
            }
            else
            {
                task.Status = DownloadStatus.Queued;
                task.ErrorMessage = null;
                Log(task, "已加入队列，等待空闲通道");
                scheduled = true;
            }
        }

        TaskUpdated?.Invoke(task);
        if (scheduled) Schedule();
    }

    public void Remove(IEnumerable<int> ids)
    {
        var removed = new List<(DownloadTask Task, Runtime? Runtime)>();

        lock (_gate)
        {
            foreach (int id in ids)
            {
                if (Find(id) is not { } task) continue;
                Runtime? runtime = _runtimes.TryGetValue(id, out var found) ? found : null;
                if (runtime is not null)
                {
                    _runtimes.Remove(id);
                    runtime.Removed = true;
                    runtime.Cts?.Cancel();
                }
                _tasks.Remove(task);
                removed.Add((task, runtime));
            }
        }

        foreach (var (task, runtime) in removed)
        {
            CleanupFiles(task, runtime);
            TaskRemoved?.Invoke(task);
        }
    }

    public void ToggleAll()
    {
        var affected = new List<DownloadTask>();
        bool pausing;

        lock (_gate)
        {
            pausing = _tasks.Any(t => t.Status == DownloadStatus.Downloading);

            foreach (var task in _tasks)
            {
                if (task.Status == DownloadStatus.Completed) continue;

                if (pausing)
                {
                    if (task.Status == DownloadStatus.Downloading)
                    {
                        task.Status = DownloadStatus.Paused;
                        task.Speed = 0;
                        task.Connections = 0;
                        task.ConnectionSpeeds = Array.Empty<double>();
                        if (_runtimes.TryGetValue(task.Id, out var runtime)) runtime.Cts?.Cancel();
                    }
                    else if (task.Status == DownloadStatus.Queued)
                    {
                        task.Status = DownloadStatus.Paused;
                    }
                }
                else if (task.Status is DownloadStatus.Paused or DownloadStatus.Error or DownloadStatus.Queued)
                {
                    task.Status = DownloadStatus.Queued;
                    task.ErrorMessage = null;
                }

                affected.Add(task);
            }
        }

        foreach (var task in affected) TaskUpdated?.Invoke(task);
        Schedule();
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
        DownloadTask? task;
        bool scheduled = false;

        lock (_gate)
        {
            task = Find(id);
            if (task is null) return;
            var runtime = _runtimes[id];

            task.Done = 0;
            task.Speed = 0;
            task.Checksum = null;
            task.ErrorMessage = null;
            task.NewerVersion = null;
            task.Blocks = new int[BlockCount];
            task.ConnectionSpeeds = Array.Empty<double>();
            DeleteCheckpoint(task, runtime);
            Log(task, "按新版本重新开始下载");

            if (task.Status == DownloadStatus.Downloading)
            {
                // the winding-down worker re-queues through the Restart flag
                runtime.Restart = true;
                runtime.Cts?.Cancel();
            }
            else
            {
                task.Status = DownloadStatus.Queued;
                scheduled = true;
            }
        }

        TaskUpdated?.Invoke(task);
        if (scheduled) Schedule();
    }

    public void SetPriority(int id, TaskPriority priority)
    {
        DownloadTask? task;
        bool scheduled = false;

        lock (_gate)
        {
            task = Find(id);
            if (task is null || task.Priority == priority) return;
            task.Priority = priority;
            scheduled = task.Status == DownloadStatus.Queued;
        }

        TaskUpdated?.Invoke(task);
        if (scheduled) Schedule();
    }

    public void SetSpeedLimit(int id, string limit)
    {
        DownloadTask? task;

        lock (_gate)
        {
            task = Find(id);
            if (task is null || task.SpeedLimit == limit) return;
            task.SpeedLimit = limit;
            if (_runtimes.TryGetValue(id, out var runtime)) ApplyLimitLocked(runtime);
        }

        TaskUpdated?.Invoke(task);
    }

    public void Dispose()
    {
        _timer.Dispose();

        lock (_gate)
        {
            foreach (var runtime in _runtimes.Values) runtime.Cts?.Cancel();
        }

        _http.Dispose();
    }

    // ── scheduling ────────────────────────────────────────────────────────
    private void Schedule()
    {
        List<DownloadTask>? started = null;

        lock (_gate)
        {
            while (_active < Math.Max(1, _options.MaxSimultaneous))
            {
                var next = _tasks
                    .Where(t => t.Status == DownloadStatus.Queued)
                    .OrderBy(t => t.Priority) // enum order: High < Normal < Low; stable within a priority
                    .FirstOrDefault();
                if (next is null) break;

                StartLocked(next);
                (started ??= new List<DownloadTask>()).Add(next);
            }
        }

        if (started is not null)
        {
            foreach (var task in started) TaskUpdated?.Invoke(task);
        }
    }

    private void StartLocked(DownloadTask task)
    {
        var runtime = _runtimes[task.Id];
        runtime.Generation++;
        runtime.Restart = false;
        runtime.LastDone = 0;
        task.Status = DownloadStatus.Downloading;
        task.ErrorMessage = null;
        task.Speed = 0;
        task.Connections = Math.Clamp(
            task.Connections > 0 ? task.Connections : _options.ConnectionsPerTask, 1, 32);
        _active++;

        var cts = new CancellationTokenSource();
        runtime.Cts = cts;
        int generation = runtime.Generation;
        _ = Task.Run(() => RunWorkerAsync(task, runtime, cts, generation));
    }

    // ── the per-task worker ───────────────────────────────────────────────
    private async Task RunWorkerAsync(DownloadTask task, Runtime runtime, CancellationTokenSource cts, int generation)
    {
        TransferOutcome outcome;
        TransferSession? session = null;
        try
        {
            Log(task, "正在探测资源…");
            var headers = HeadersFor(runtime);
            var probe = await _drivers.ProbeAsync(
                task.Url, headers, (message, error) => Log(task, message, error), cts.Token);
            if (runtime.Removed) return;

            ApplyProbe(task, probe);
            session = CreateSession(task, runtime, probe, headers);
            runtime.Session = session;
            Log(task, DescribePlan(probe));
            outcome = await session.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = new TransferOutcome(TransferOutcomeKind.Paused, null);
        }
        catch (Exception error)
        {
            outcome = new TransferOutcome(TransferOutcomeKind.Failed, error.Message);
        }

        bool raiseCompleted = false;

        lock (_gate)
        {
            if (runtime.Session == session) runtime.Session = null;
            if (runtime.Cts == cts)
            {
                runtime.Cts = null;
                cts.Dispose();
            }
            _active--;

            if (runtime.Removed || runtime.Generation != generation) return;

            if (runtime.Restart && outcome.Kind != TransferOutcomeKind.Completed)
            {
                runtime.Restart = false;
                task.Status = DownloadStatus.Queued;
            }
            else
            {
                switch (outcome.Kind)
                {
                    case TransferOutcomeKind.Completed when task.Status == DownloadStatus.Downloading:
                        task.Status = DownloadStatus.Completed;
                        task.Speed = 0;
                        task.Connections = 0;
                        task.ConnectionSpeeds = Array.Empty<double>();
                        task.Blocks = Enumerable.Repeat(1, BlockCount).ToArray();
                        if (task.Size <= 0 && session is not null) task.Size = Math.Max(session.DoneBytes, task.Done);
                        task.Done = task.Size > 0 ? task.Size : task.Done;
                        DeleteCheckpoint(task, runtime);
                        Log(task, "下载完成");
                        raiseCompleted = true;
                        break;

                    case TransferOutcomeKind.Paused when task.Status == DownloadStatus.Downloading:
                        task.Speed = 0;
                        task.Connections = 0;
                        task.ConnectionSpeeds = Array.Empty<double>();
                        break;

                    case TransferOutcomeKind.Failed when task.Status == DownloadStatus.Downloading:
                        task.Status = DownloadStatus.Error;
                        task.ErrorMessage = outcome.Error;
                        task.Speed = 0;
                        task.Connections = 0;
                        task.ConnectionSpeeds = Array.Empty<double>();
                        task.Retries++;
                        Log(task, $"下载失败：{outcome.Error}", isError: true);
                        break;
                }
            }
        }

        if (raiseCompleted)
        {
            await VerifyChecksumAsync(task, runtime);
            TaskCompleted?.Invoke(task);
        }

        TaskUpdated?.Invoke(task);
        Schedule();
    }

    /// <summary>Probe results land on the UI-shaped task: name, size, host.</summary>
    private void ApplyProbe(DownloadTask task, ResourceProbe probe)
    {
        bool changed = false;

        lock (_gate)
        {
            var name = Sanitize(string.IsNullOrWhiteSpace(probe.Name) ? task.Name : probe.Name);
            if (name.Length > 0 && name != task.Name)
            {
                task.Name = name;
                changed = true;
            }
            if (probe.Size > 0 && probe.Size != task.Size)
            {
                task.Size = probe.Size;
                changed = true;
            }
            var host = HostFrom(probe.FinalUrl);
            if (host.Length > 0 && host != task.Host)
            {
                task.Host = host;
                changed = true;
            }
            if (probe.Trackers is { Count: > 0 } && task.Trackers is null)
            {
                task.Trackers = probe.Trackers;
                changed = true;
            }
            if (task.Connections == 0)
            {
                task.Connections = _options.ConnectionsPerTask;
                changed = true;
            }
        }

        if (changed) TaskUpdated?.Invoke(task);
        Log(task, probe.Size > 0
            ? $"资源确认：{probe.Name} · {probe.Size} 字节 · {probe.Protocol}"
            : $"资源确认：{probe.Name} · 大小未知 · {probe.Protocol}");
    }

    private TransferSession CreateSession(
        DownloadTask task, Runtime runtime, ResourceProbe probe, IReadOnlyDictionary<string, string> headers)
    {
        var savePath = ResolveSavePath(task, runtime, probe);
        return new TransferSession(new TransferRequest
        {
            Plan = probe.Plan,
            SavePath = savePath,
            Http = _http,
            Limiter = runtime.Limiter,
            GlobalLimiter = _globalLimiter,
            ETag = probe.ETag,
            Connections = Math.Clamp(task.Connections > 0 ? task.Connections : _options.ConnectionsPerTask, 1, 32),
            RetryCount = _options.RetryCount,
            SmallFileThresholdBytes = _options.SmallFileThresholdBytes,
            Headers = headers,
            Checkpoint = LoadCheckpoint(savePath),
            CheckpointChanged = checkpoint => PersistCheckpoint(savePath, checkpoint),
            Log = (message, error) => Log(task, message, error),
            EngineRunner = _options.EngineRunner,
            ExternalEngineCommand = _options.ExternalEngineCommand,
            TaskUrl = task.Url,
            TaskName = task.Name,
            SeedRatio = task.SeedRatio,
            SeedHours = task.SeedHours,
        });
    }

    /// <summary>
    /// Decide the task's one save path at first start. A checkpoint beside the
    /// target that belongs to this very resource (same plan kind and URL)
    /// keeps the plain target path — resuming into our own interrupted file;
    /// anything else (target already on disk, or a stale/foreign checkpoint
    /// left behind) gets the "name (1).ext" rewrite so nothing on disk is
    /// silently overwritten. Later starts reuse the fixed path.
    /// </summary>
    private string ResolveSavePath(DownloadTask task, Runtime runtime, ResourceProbe probe)
    {
        // decided once, under the kernel lock: two tasks probing the same file
        // name at the same moment must not both see the slot as free
        lock (_gate)
        {
            if (runtime.SavePath is { } fixedPath) return fixedPath;

            var basePath = TargetPath(task);
            var checkpoint = LoadCheckpoint(basePath);
            if (checkpoint is not null && CheckpointBelongsTo(checkpoint, probe))
            {
                runtime.SavePath = basePath; // our own interrupted download — keep writing to it
                return basePath;
            }

            var claimed = _runtimes.Values
                .Where(other => other.SavePath is not null)
                .Select(other => Path.GetFullPath(other.SavePath!))
                .ToHashSet();
            bool isTaken(string candidate) => File.Exists(candidate) || Directory.Exists(candidate) ||
                claimed.Contains(Path.GetFullPath(candidate));

            var savePath = NextFreePath(basePath, isTaken);
            runtime.SavePath = savePath;
            return savePath;
        }
    }

    /// <summary>Whether an on-disk checkpoint describes the resource we just probed.</summary>
    private static bool CheckpointBelongsTo(TransferCheckpoint checkpoint, ResourceProbe probe) =>
        probe.Plan switch
        {
            TransferPlan.ByteRanges ranges => checkpoint.Kind == "ranges" && checkpoint.Url == ranges.Url,
            TransferPlan.SegmentFiles files => checkpoint.Kind == "segments" &&
                files.Segments.Count > 0 && checkpoint.Url == files.Segments[0],
            _ => false,
        };

    /// <summary>
    /// 防碰撞命名: never overwrite what is already on disk. A taken target is
    /// rewritten as "name (1).ext", "name (2).ext", … with the counter before
    /// the final extension ("a.tar.gz" → "a.tar (1).gz", no extension →
    /// "name (1)"). Pure over an existence check so tests can drive it.
    /// </summary>
    public static string NextFreePath(string path, Func<string, bool>? exists = null)
    {
        exists ??= candidate => File.Exists(candidate) || Directory.Exists(candidate);
        if (!exists(path)) return path;

        var name = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, $"{stem} ({i}){extension}");
            if (!exists(candidate)) return candidate;
        }
    }

    private IReadOnlyDictionary<string, string> HeadersFor(Runtime runtime)
    {
        if (runtime.Headers is not { Count: > 0 } extra) return _options.DefaultHeaders;
        var merged = new Dictionary<string, string>(_options.DefaultHeaders);
        foreach (var (name, value) in extra) merged[name] = value;
        return merged;
    }

    // ── the tick ──────────────────────────────────────────────────────────
    private void Tick(object? state)
    {
        KernelSnapshot snapshot;

        lock (_gate)
        {
            // 「夜间不限速」窗口边界：豁免状态翻转时立刻重算全部任务的限速，
            // 不必等用户手动碰一次任务。
            bool exempt = _options.UncappedAtNight &&
                InOffPeakWindow(DateTime.Now.TimeOfDay, _options.OffPeakStart, _options.OffPeakEnd);
            if (exempt != _lastWindowExempt)
            {
                _lastWindowExempt = exempt;
                foreach (var runtime in _runtimes.Values) ApplyLimitLocked(runtime);
            }

            double total = 0;

            foreach (var task in _tasks)
            {
                if (task.Status != DownloadStatus.Downloading) continue;
                if (!_runtimes.TryGetValue(task.Id, out var runtime)) continue;
                var session = runtime.Session;
                if (session is null) continue; // still probing

                long done = session.DoneBytes;
                double raw = Math.Max(0, (done - runtime.LastDone) / TickSeconds);
                runtime.LastDone = done;
                task.Done = done;
                task.Speed = Math.Max(0, task.Speed * 0.65 + raw * 0.35);
                task.PeakSpeed = Math.Max(task.PeakSpeed, task.Speed);
                task.Connections = session.LiveConnections;
                task.ConnectionSpeeds = session.ActiveSpeeds();
                task.Blocks = session.ComputeBlocks(BlockCount);
                task.SpeedHistory = Push(task.SpeedHistory, task.Speed, SessionSamples);
                total += task.Speed;
            }

            Array.Copy(_speedHistory, 1, _speedHistory, 0, _speedHistory.Length - 1);
            _speedHistory[^1] = total;

            snapshot = new KernelSnapshot(
                total,
                _tasks.Sum(t => t.UploadSpeed),
                _tasks.Any(t => t.Status == DownloadStatus.Downloading),
                (double[])_speedHistory.Clone());
        }

        Ticked?.Invoke(snapshot);
    }

    // ── limits ────────────────────────────────────────────────────────────
    private void ApplyLimitLocked(Runtime runtime)
    {
        long? global = _options.GlobalSpeedLimit is > 0 ? _options.GlobalSpeedLimit : null;
        // 「夜间不限速」：inside the off-peak window the global cap is lifted;
        // an explicitly chosen per-task limit still applies.
        if (global is not null && _options.UncappedAtNight &&
            InOffPeakWindow(DateTime.Now.TimeOfDay, _options.OffPeakStart, _options.OffPeakEnd))
        {
            global = null;
        }

        // 共享全局桶：所有任务的合计速率受全局值约束（而不是每任务各享一份）；
        // 每任务限速仍单独生效，任务实际速率 = min(共享全局, 任务限速)。
        _globalLimiter.SetLimit(global ?? 0);
        runtime.Limiter.SetLimit(ParseLimit(runtime.Task.SpeedLimit));
    }

    /// <summary>
    /// Whether <paramref name="now"/> falls inside the "HH:mm" off-peak
    /// window. Supports windows crossing midnight ("23:00"→"07:00").
    /// Unparseable bounds or a zero-length window mean "no exemption".
    /// Pure so tests can drive it.
    /// </summary>
    public static bool InOffPeakWindow(TimeSpan now, string? start, string? end)
    {
        var from = ParseClock(start);
        var to = ParseClock(end);
        if (from is not { } a || to is not { } b) return false;
        return a == b ? false
            : a < b ? now >= a && now < b
            : now >= a || now < b;
    }

    /// <summary>Strictly "H:mm" / "HH:mm" (0-23 : 0-59); anything else → null.</summary>
    private static TimeSpan? ParseClock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var h) || h is < 0 or > 23 ||
            !int.TryParse(parts[1], out var m) || m is < 0 or > 59)
        {
            return null;
        }
        return new TimeSpan(h, m, 0);
    }

    private static long? ParseLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "不限") return null;

        var match = Regex.Match(text.Trim(), @"^(\d+(?:\.\d+)?)\s*(B/s|KB/s|MB/s|GB/s)$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        double value = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "b/s" => (long)value,
            "kb/s" => (long)(value * 1024),
            "mb/s" => (long)(value * 1024 * 1024),
            "gb/s" => (long)(value * 1024 * 1024 * 1024),
            _ => null,
        };
    }

    // ── checksum / checkpoints / files ────────────────────────────────────

    /// <summary>
    /// 完成后的指纹校验（Ubuntu SHA256SUMS 模式）：
    /// ① 任务带 ExpectedHash（发布页/校验清单抄来的）→ 按长度判别算法（MD5/SHA-1/SHA-256）
    ///    并与实际指纹比对，不匹配任务转入 Error（文件不可信，不能当完成处理）；
    /// ② 指纹写入本地签名库（URL 键）：再次下载同源时与历史比对，
    ///    内容变化在 Checksum/日志里显著标注（不失败——换源/动态内容属正常业务）。
    /// </summary>
    private async Task VerifyChecksumAsync(DownloadTask task, Runtime? runtime)
    {
        try
        {
            var path = TargetPathFor(task, runtime);
            var algorithm = Checksums.DetectAlgorithm(task.ExpectedHash) ?? "SHA-256";
            var hash = await Task.Run(() => Checksums.Compute(algorithm, path));

            if (task.ExpectedHash is not null &&
                !string.Equals(hash, task.ExpectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                lock (_gate)
                {
                    task.Checksum = $"{algorithm} 校验失败";
                    task.Status = DownloadStatus.Error;
                    task.ErrorMessage =
                        $"{algorithm} 校验失败：期望 {task.ExpectedHash.Trim().ToLowerInvariant()}，实际 {hash.ToLowerInvariant()}";
                    task.Log.Add(new LogEntry(Stamp(), task.ErrorMessage, IsError: true));
                }
                return; // TaskCompleted 仍会触发，但状态已是 Error
            }

            var history = _hashes.Find(task.Url);
            var consistent = history is not null &&
                string.Equals(history.Hash, hash, StringComparison.OrdinalIgnoreCase);
            var verdict = history is null
                ? "已记录签名"
                : consistent
                    ? "与历史签名一致"
                    : $"与历史签名不一致（原 {history.Algorithm} {history.Hash[..Math.Min(16, history.Hash.Length)].ToLowerInvariant()}…）";
            _hashes.Record(new HashDatabase.Entry(task.Url, task.Name, task.Size, algorithm, hash, Stamp()));

            lock (_gate)
            {
                task.Checksum = history is null || consistent
                    ? $"{algorithm} 已校验"
                    : $"{algorithm} 已校验（签名变化）";
                task.Log.Add(new LogEntry(Stamp(), $"{algorithm} {hash[..16].ToLowerInvariant()}…（{verdict}）",
                    IsError: history is not null && !consistent));
            }
        }
        catch (Exception error)
        {
            lock (_gate) task.Log.Add(new LogEntry(Stamp(), $"校验失败：{error.Message}", IsError: true));
        }
    }

    private string CheckpointPath(string savePath) => savePath + _options.CheckpointSuffix;

    private TransferCheckpoint? LoadCheckpoint(string savePath)
    {
        try
        {
            var path = CheckpointPath(savePath);
            if (!File.Exists(path)) return null;
            var checkpoint = JsonSerializer.Deserialize<TransferCheckpoint>(File.ReadAllText(path));
            // corrupt or half-written JSON deserializes to null/garbage here and
            // must degrade to "no checkpoint" — never throw into the task start
            return checkpoint is not null && checkpoint.Kind.Length > 0 && checkpoint.Url.Length > 0
                ? checkpoint
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void PersistCheckpoint(string savePath, TransferCheckpoint checkpoint)
    {
        var path = CheckpointPath(savePath);
        var temp = path + ".tmp" + Guid.NewGuid().ToString("N");
        try
        {
            // write-then-rename: a crash mid-write leaves the old checkpoint
            // intact instead of a half JSON file that kills resume state
            File.WriteAllText(temp, JsonSerializer.Serialize(checkpoint));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* nothing else to give up */ }
        }
    }

    private void DeleteCheckpoint(DownloadTask task, Runtime? runtime)
    {
        try
        {
            var path = CheckpointPath(TargetPathFor(task, runtime));
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    private void CleanupFiles(DownloadTask task, Runtime? runtime)
    {
        DeleteCheckpoint(task, runtime);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400); // let the winding-down transfer release the handle
                var path = TargetPathFor(task, runtime);
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // leftovers after an interrupted delete are harmless
            }
        });
    }

    // ── small helpers ─────────────────────────────────────────────────────
    private DownloadTask? Find(int id) => _tasks.FirstOrDefault(t => t.Id == id);

    /// <summary>The fixed save path once the probe fixed it; the computed target before that.</summary>
    private string TargetPathFor(DownloadTask task, Runtime? runtime) => runtime?.SavePath ?? TargetPath(task);

    private void Log(DownloadTask task, string message, bool isError = false)
    {
        lock (_gate) task.Log.Add(new LogEntry(Stamp(), message, isError));
    }

    private string TargetPath(DownloadTask task)
    {
        var saveTo = task.SavePath;
        if (string.IsNullOrWhiteSpace(saveTo)) saveTo = DefaultDirectoryFor(task);
        // a target that already looks like a file (extension, not an existing dir) is used as-is
        if (Path.HasExtension(saveTo) && !Directory.Exists(saveTo)) return saveTo;
        return Path.Combine(saveTo, Sanitize(task.Name));
    }

    /// <summary>
    /// The settings-driven fallback directory: <see cref="KernelOptions.DefaultSavePath"/>,
    /// plus one category subfolder when 「分类文件夹」 is on ("D:\Downloads" +
    /// "video" → "D:\Downloads\video"). An unset DefaultSavePath keeps the
    /// legacy working-directory behavior (empty string).
    /// </summary>
    private string DefaultDirectoryFor(DownloadTask task)
    {
        var root = _options.DefaultSavePath;
        if (string.IsNullOrWhiteSpace(root)) return string.Empty;
        return _options.FoldersByCategory && task.Category.Length > 0
            ? Path.Combine(root, Sanitize(task.Category))
            : root;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "未命名下载" : clean;
    }

    private static string DescribePlan(ResourceProbe probe) => probe.Plan switch
    {
        TransferPlan.ByteRanges ranges when ranges.AcceptRanges && ranges.Size > 0 =>
            ranges.Mirrors.Count > 0
                ? $"HTTP 多分段就绪（{ranges.Mirrors.Count + 1} 个源，支持断点续传）"
                : "HTTP 多分段就绪（支持断点续传）",
        TransferPlan.ByteRanges => "HTTP 单流（服务器不支持分段或大小未知）",
        TransferPlan.FtpFile => "FTP 传输就绪",
        TransferPlan.SegmentFiles segments => $"分段协议就绪（{segments.Segments.Count} 个分段）",
        TransferPlan.ExternalEngine engine => $"外部引擎插槽：{engine.Engine}",
        TransferPlan.Unsupported unsupported => unsupported.Reason,
        _ => "传输计划未知",
    };

    private static DownloadTask BuildTask(int id, NewDownloadRequest request)
    {
        string name = FileNameFrom(request.Url);
        var kind = KindFrom(name);
        var isTorrent = ProtocolDetector.Detect(request.Url) == DownloadProtocol.BitTorrent;

        return new DownloadTask
        {
            Id = id,
            Name = name,
            Host = HostFrom(request.Url),
            Kind = kind,
            Size = 0,
            Category = request.Category.Length > 0 ? request.Category : CategoryFor(kind, isTorrent),
            Tint = TintFor(kind),
            Url = request.Url,
            SavePath = request.SaveTo,
            Priority = request.Priority,
            Connections = request.StartNow ? Math.Clamp(request.Connections, 1, 32) : 0,
            Status = request.StartNow ? DownloadStatus.Queued : DownloadStatus.Paused,
            AddedAt = "今天 " + DateTime.Now.ToString("HH:mm"),
            ExpectedHash = string.IsNullOrWhiteSpace(request.ExpectedHash) ? null : request.ExpectedHash.Trim(),
            SeedRatio = request.SeedRatio,
            SeedHours = request.SeedHours,
            // PT 底线：magnet/ed2k/torrent 链接在展示层先脱敏，原始 URL 只留给引擎
            DisplayUrl = NeedsMasking(request.Url) ? Protocol.SensitiveUrls.Mask(request.Url) : null,
        };
    }

    private static string CategoryFor(FileKind kind, bool isTorrent) => isTorrent ? "bt" : kind switch
    {
        FileKind.Film => "video",
        FileKind.Music => "music",
        FileKind.Doc => "doc",
        _ => "soft",
    };

    /// <summary>magnet / ed2k / torrent 链接展示前必须脱敏。</summary>
    private static bool NeedsMasking(string url) =>
        url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("ed2k:", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);

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
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".iso" or ".img" or ".dmg" or ".torrent" or ".exe" or ".msi" => FileKind.Disc,
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".ts" or ".flv" => FileKind.Film,
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".zst" => FileKind.Zip,
            ".flac" or ".mp3" or ".m4a" or ".wav" or ".aac" => FileKind.Music,
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
}
