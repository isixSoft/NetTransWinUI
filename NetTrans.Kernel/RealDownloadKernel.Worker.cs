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
/// Scheduling and the per-task worker: the MaxSimultaneous queue, the
/// probe-to-task handoff, session construction and the save-path decision
/// (collision rewrite + resume into our own checkpoint).
/// </summary>
public sealed partial class RealDownloadKernel
{
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
        runtime.SpaceBlocked = false;
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
            var headers = HeadersFor(runtime, task.Url);
            var probe = await _drivers.ProbeAsync(
                task.Url, headers, (message, error) => Log(task, message, error), cts.Token);
            if (runtime.Removed)
            {
                // removed mid-probe: fall through to the settle block so the
                // slot is released and the queue wakes up — an early return
                // here used to leak a MaxSimultaneous slot forever
                outcome = new TransferOutcome(TransferOutcomeKind.Paused, null);
            }
            else
            {
                ApplyProbe(task, probe);
                if (CheckSpace(task) is { } spaceError)
                {
                    // 磁盘空间预检（P1-3）：不足不进运行态，tick 每次自动探测恢复。
                    // outcome 走 Paused：不算传输失败、不计重试，只等空间释放。
                    runtime.SpaceBlocked = true;
                    lock (_gate)
                    {
                        task.Status = DownloadStatus.Error;
                        task.ErrorMessage = spaceError;
                    }
                    Log(task, spaceError, isError: true);
                    outcome = new TransferOutcome(TransferOutcomeKind.Paused, null);
                }
                else
                {
                    session = CreateSession(task, runtime, probe, headers);
                    runtime.Session = session;
                    Log(task, DescribePlan(probe));
                    outcome = await session.RunAsync(cts.Token);
                }
            }
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
        bool obsolete = false;

        lock (_gate)
        {
            if (runtime.Session == session) runtime.Session = null;
            if (runtime.Cts == cts)
            {
                runtime.Cts = null;
                cts.Dispose();
            }
            _active--;

            if (runtime.Removed || runtime.Generation != generation)
            {
                // no longer the task's live worker (task removed, or a later
                // generation took over): task state and events belong to the
                // live worker now — but still wake the queue, since queued
                // tasks may be waiting for exactly the slot just released.
                obsolete = true;
            }
            else if (runtime.Restart && outcome.Kind != TransferOutcomeKind.Completed)
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

        if (obsolete)
        {
            Schedule();
            return;
        }

        if (raiseCompleted)
        {
            await VerifyChecksumAsync(task, runtime);
            TaskCompleted?.Invoke(task);
            CompletionNotifications.Emit(_options, task);
            await RunCompletionActionsAsync(task, runtime);
        }

        TaskUpdated?.Invoke(task);
        Schedule();
    }

    // ── 完成后动作（P0-4）：任务级优先，全局级「全部完成」收尾；runner 异常只记日志 ──

    /// <summary>
    /// TaskCompleted 之后调用：任务级动作在各自任务完成时触发；无任务级动作时
    /// 检查全局「全部完成」——没有排队/下载中的任务（暂停与失败不拦收尾）就触发
    /// 恰一次，直到又有任务排队（Add/Toggle/Redownload 重置闸门）。
    /// 校验失败会把状态翻成 Error，此处以 Status==Completed 为准，不触发动作。
    /// </summary>
    private async Task RunCompletionActionsAsync(DownloadTask task, Runtime runtime)
    {
        var runner = _options.CompletionRunner;
        if (runner is null || task.Status != DownloadStatus.Completed) return;

        if (runtime.OnComplete != AfterCompletion.None)
        {
            await FireCompletionAsync(runner, task, new CompletionEvent(
                task.Name, runtime.SavePath, runtime.OnComplete,
                ExpandCompletion(runtime.OnCompleteCommand, runtime.SavePath, task.Name),
                FromAllCompleted: false));
        }

        var global = _options.AllComplete;
        if (global == AfterCompletion.None) return;

        lock (_gate)
        {
            if (_tasks.Any(t => t.Status is DownloadStatus.Queued or DownloadStatus.Downloading)) return;
            if (_allCompleteFired) return;
            _allCompleteFired = true;
        }

        await FireCompletionAsync(runner, task, new CompletionEvent(
            task.Name, runtime.SavePath, global,
            ExpandCompletion(_options.AllCompleteCommand, runtime.SavePath, task.Name),
            FromAllCompleted: true));
    }

    /// <summary>runner 抛异常只记任务日志——完成后动作不能反过来弄脏任务状态。</summary>
    private async Task FireCompletionAsync(
        Func<CompletionEvent, CancellationToken, Task> runner, DownloadTask task, CompletionEvent completion)
    {
        try
        {
            await runner(completion, CancellationToken.None);
        }
        catch (Exception error)
        {
            Log(task, $"完成后动作执行失败（{Brief(error.Message)}）", isError: true);
        }
    }

    private static string? ExpandCompletion(string? template, string? savePath, string name) =>
        template is null ? null : template
            .Replace("{path}", savePath ?? string.Empty)
            .Replace("{name}", name);

    private static string Brief(string message) => message.Split('\n', 2)[0].TrimEnd('\r').Trim();

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
            if (task.ExpectedHash is null && probe.ExpectedHash is { Length: > 0 } hash)
            {
                task.ExpectedHash = hash;
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
            Headers = WithSiteHeaders(headers, probe.ExtraHeaders),
            Checkpoint = LoadCheckpoint(savePath),
            CheckpointChanged = checkpoint => PersistCheckpoint(savePath, checkpoint),
            Log = (message, error) => Log(task, message, error),
            EngineRunner = _options.EngineRunner,
            ExternalEngineCommand = _options.ExternalEngineCommand,
            TaskUrl = task.Url,
            TaskName = task.Name,
            SeedRatio = task.SeedRatio,
            SeedHours = task.SeedHours,
            SelectedFiles = task.SelectedFiles,
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

    private IReadOnlyDictionary<string, string> HeadersFor(Runtime runtime, string url)
    {
        var merged = new Dictionary<string, string>(_options.DefaultHeaders);
        if (runtime.Headers is { Count: > 0 } extra)
        {
            foreach (var (name, value) in extra) merged[name] = value;
        }

        // Cookie 导入（P1-1）：任务级文件优先于全局，显式 Cookie 头最优先；
        // 文件缺失/损坏静默降级——cookie 只是助力，不能挡住下载本身。
        var cookieFile = runtime.CookieFile ?? _options.CookieFile;
        bool hasExplicitCookie = merged.Keys.Any(
            k => k.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        if (cookieFile is not null && !hasExplicitCookie)
        {
            try
            {
                var header = NetscapeCookies.HeaderFor(
                    NetscapeCookies.Parse(File.ReadAllText(cookieFile)), new Uri(url));
                if (header is not null) merged["Cookie"] = header;
            }
            catch
            {
                // 静默：无有效 cookie 就按无 cookie 下载
            }
        }

        return merged;
    }

    // ── 磁盘空间预检（P1-3） ─────────────────────────────────────────────

    /// <summary>
    /// 资源大小已知且剩余空间不足时返回错误文案（含「需要/剩余」）；
    /// 大小未知（流式）无法估算，跳过预检。
    /// </summary>
    private string? CheckSpace(DownloadTask task)
    {
        if (task.Size <= 0) return null;
        long free = FreeBytes(TargetDirectory(task));
        return free < task.Size
            ? $"磁盘空间不足：需要 {task.Size} 字节，剩余 {free} 字节，等待空间释放后自动重试"
            : null;
    }

    /// <summary>保存目录（探测命名后）；空间预检与自动恢复共用。</summary>
    private string TargetDirectory(DownloadTask task) =>
        Path.GetDirectoryName(TargetPath(task)) ?? ".";

    /// <summary>剩余空间探测：注入槽优先；探测失败视为充裕，不拦下载。</summary>
    private long FreeBytes(string directory)
    {
        try
        {
            return _options.FreeSpaceProbe?.Invoke(directory) ?? RealFreeBytes(directory);
        }
        catch
        {
            return long.MaxValue;
        }
    }

    private static long RealFreeBytes(string directory)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        return root is null ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
    }

    /// <summary>站点直链必需头压过任务级同名字头：站点提取器最懂自己的 CDN
    /// （缺 Referer 直接 403，与 yt-dlp 同策略）。</summary>
    private static IReadOnlyDictionary<string, string> WithSiteHeaders(
        IReadOnlyDictionary<string, string> headers, IReadOnlyDictionary<string, string>? siteHeaders)
    {
        if (siteHeaders is not { Count: > 0 }) return headers;
        var merged = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in siteHeaders) merged[name] = value;
        return merged;
    }

}
