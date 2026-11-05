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
/// The 900 ms tick (speed/block accounting and the kernel snapshot), speed
/// limits (per-task + shared global bucket, night-exempt window), checksum
/// verification and checkpoint persistence/cleanup.
/// </summary>
public sealed partial class RealDownloadKernel
{
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

        // 队列定时启停（P0-3）：每个 tick 顺带评估一次；锁在方法内部短暂持有
        EvaluateQueueSchedules();
    }

    // ── 队列定时启停（P0-3） ──────────────────────────────────────────────

    private sealed record QueueSchedule(string? Start, string? Stop);

    private readonly Dictionary<string, QueueSchedule> _queueSchedules = new(StringComparer.Ordinal);

    private DateTime Now() => _options.Clock?.Invoke() ?? DateTime.Now;

    /// <summary>
    /// 设置一条队列的定时启停窗口（HH:mm 24 小时制，可跨午夜）。start/stop 都
    /// 为 null = 取消调度。有效窗口：start 缺省视为 00:00，stop 缺省视为 24:00，
    /// start &gt; stop 即跨午夜（如 23:00→07:00 表示 [23:00, 次日 07:00)）。
    /// 窗口内：队列内暂停任务批量开始；窗口外：队列内排队/下载中任务批量暂停。
    /// 不属于任何已调度队列的任务一概不受影响。
    /// </summary>
    public void SetQueueSchedule(string queue, string? start, string? stop)
    {
        if (string.IsNullOrWhiteSpace(queue)) throw new ArgumentException("队列名不能为空", nameof(queue));
        ParseHm(start); // 先验证再入库：坏 HH:mm 直接拒绝
        ParseHm(stop);
        lock (_gate)
        {
            if (start is null && stop is null) _queueSchedules.Remove(queue);
            else _queueSchedules[queue] = new QueueSchedule(start, stop);
        }
    }

    /// <summary>
    /// 评估所有队列调度（900ms tick 每次顺带调用；测试经注入时钟直接调用保证
    /// 确定性）。状态迁移在锁内完成，批量开始后经 Schedule() 按并发槽调度。
    /// </summary>
    public void EvaluateQueueSchedules()
    {
        List<(DownloadTask Task, bool Start)>? changes = null;
        lock (_gate)
        {
            if (_queueSchedules.Count == 0) return;
            var now = Now().TimeOfDay;

            foreach (var (queue, schedule) in _queueSchedules)
            {
                bool inWindow = InWindow(now, schedule);
                foreach (var task in _tasks)
                {
                    if (!string.Equals(task.Queue, queue, StringComparison.Ordinal)) continue;

                    if (inWindow && task.Status == DownloadStatus.Paused)
                    {
                        task.Status = DownloadStatus.Queued;
                        task.ErrorMessage = null;
                        _allCompleteFired = false; // 又有工作进入队列，收尾动作可再次触发
                        (changes ??= new()).Add((task, true));
                    }
                    else if (!inWindow && task.Status is DownloadStatus.Queued or DownloadStatus.Downloading)
                    {
                        var runtime = _runtimes[task.Id];
                        task.Status = DownloadStatus.Paused;
                        task.Speed = 0;
                        task.Connections = 0;
                        task.ConnectionSpeeds = Array.Empty<double>();
                        runtime.Cts?.Cancel();
                        (changes ??= new()).Add((task, false));
                    }
                }
            }
        }

        if (changes is null) return;
        foreach (var (task, start) in changes)
        {
            Log(task, start ? "队列调度：到达开始时间，批量开始" : "队列调度：到达停止时间，批量暂停");
            TaskUpdated?.Invoke(task);
        }
        Schedule();
    }

    /// <summary>跨午夜窗口判定：[start, stop) 语义，start &gt; stop 视为跨午夜。</summary>
    private static bool InWindow(TimeSpan now, QueueSchedule schedule)
    {
        var start = ParseHm(schedule.Start) ?? TimeSpan.Zero;
        var end = ParseHm(schedule.Stop) ?? TimeSpan.FromDays(1);
        return start <= end ? now >= start && now < end : now >= start || now < end;
    }

    private static TimeSpan? ParseHm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!TimeSpan.TryParseExact(value, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var time) ||
            time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            throw new FormatException($"时间须为 HH:mm（24 小时制，<24:00）：{value}");
        return time;
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

}
