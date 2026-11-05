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
public sealed partial class RealDownloadKernel : IDownloadKernel
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
    private bool _allCompleteFired; // 「全部完成」收尾动作只触发一次，直到又有新任务排队
    private readonly SpeedLimiter _globalLimiter = new(0); // 共享全局桶：所有任务合计受全局限速约束

    private sealed class Runtime
    {
        public required DownloadTask Task { get; init; }
        public SpeedLimiter Limiter { get; } = new();
        public IReadOnlyDictionary<string, string>? Headers { get; init; }

        /// <summary>任务级 Cookie 文件（P1-1）；null 时回退 KernelOptions.CookieFile。</summary>
        public string? CookieFile { get; init; }
        public CancellationTokenSource? Cts;
        public TransferSession? Session;
        public long LastDone;
        public int Generation;
        public bool Removed;
        public bool Restart;

        /// <summary>磁盘空间预检（P1-3）：因空间不足被拦下的任务，tick 自动探测恢复。</summary>
        public bool SpaceBlocked;

        /// <summary>
        /// Save path fixed at the first start, after the probe names the file:
        /// every later start/resume of the same task writes to this exact path,
        /// so resume keeps appending to its own interrupted file and the
        /// collision rewrite is decided exactly once per task.
        /// </summary>
        public string? SavePath;

        /// <summary>任务级「完成后动作」（P0-4），来自 NewDownloadRequest；None = 无。</summary>
        public AfterCompletion OnComplete;
        public string? OnCompleteCommand;
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

    /// <summary>
    /// 批量导入（P1-7）：文本每行一条 URL（# 注释行与空行跳过，见 BatchImport），
    /// 逐条按 Add 语义入队；文件与剪贴板两种来源共用本入口。
    /// </summary>
    public IReadOnlyList<DownloadTask> AddBatch(
        string text, string saveTo, int connections, TaskPriority priority, bool StartNow, string Queue = "")
    {
        var tasks = new List<DownloadTask>();
        foreach (var url in BatchImport.ParseLines(text))
            tasks.Add(Add(new NewDownloadRequest(url, saveTo, "", connections, priority, StartNow, Queue: Queue)));
        return tasks;
    }

    public DownloadTask Add(NewDownloadRequest request)
    {
        DownloadTask task;

        lock (_gate)
        {
            task = BuildTask(_nextId++, request);
            _tasks.Add(task);
            var runtime = new Runtime
            {
                Task = task,
                Headers = request.Headers,
                CookieFile = request.CookieFile,
                OnComplete = request.OnComplete,
                OnCompleteCommand = request.OnCompleteCommand,
            };
            _runtimes[task.Id] = runtime;
            ApplyLimitLocked(runtime);
            _allCompleteFired = false; // 又有新工作，全局收尾动作可以再次触发
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
                _allCompleteFired = false; // 又有排队任务，全局收尾动作可以再次触发
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
                _allCompleteFired = false; // 重新排队即重新计「全部完成」
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

    public void SetGlobalSpeedLimit(long? bytesPerSecond)
    {
        lock (_gate)
        {
            _options.GlobalSpeedLimit = bytesPerSecond is > 0 ? bytesPerSecond : null;
            // Re-apply right away (same as the night-window flip in Tick) so a
            // mid-download profile switch is live within one limiter slice
            // (≤250ms) instead of waiting for a state change to trigger a tick.
            foreach (var runtime in _runtimes.Values) ApplyLimitLocked(runtime);
        }
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
            Queue = request.Queue,
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
            SelectedFiles = request.SelectedFiles,
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
