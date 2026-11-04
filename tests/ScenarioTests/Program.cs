using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndToEndTests;
using NetTrans.Kernel;
using NetTrans.Kernel.Protocol;
using NetTrans.Kernel.Protocol.Drivers;
using NetTrans.Kernel.Transfer;

namespace ScenarioTests;

/// <summary>
/// 场景化端到端测试：以「用户的一天」为线索，把调度、断点、限速、
/// 校验、签名库、PT 引擎下发等特性串成完整使用流程，验证它们协同工作。
/// </summary>
internal static class Program
{
    private static int _passed, _failed;
    private static readonly List<string> Failures = new();
    private static readonly object EventLock = new();

    private static async Task<int> Main()
    {
        using var server = new TestServer();

        await Run("场景1. 新用户首日：并发上限/队列补位/分类落盘/签名库", () => FirstDayAsync(server));
        await Run("场景2. 网络抖动一天：断连→重试→续传→字节精确", () => FlakyNetworkAsync(server));
        await Run("场景3. PT 用户日常：magnet 引擎下发/脱敏/签名入库", () => PtUserAsync(server));
        await Run("场景4. 混跑 + 全局限速：跨任务共享一个总闸", () => MixedTrafficAsync(server));
        await Run("场景5. 坏文件处置：校验失败→换源重下→签名入库", () => BadFileRecoveryAsync(server));
        await Run("场景6. 进程重启：断点存活→新实例续传→零重复流量", () => RestartResumeAsync(server));
        await Run("场景7. 队列指挥室：插队/全停全续/下载中重下/移除任务", () => QueueControlAsync(server));
        await Run("场景8. 暂停续传：手动暂停落断点→恢复→零重复流量", () => PauseResumeAsync(server));
        await Run("场景9. 链接动物园：ed2k/magnet/torrent/坏链/引擎故障", () => LinkZooAsync(server));
        await Run("场景10. 命名与校验：防碰撞/响应头命名/多算法/镜像", () => NamingChecksumMirrorAsync(server));

        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"结果：{_passed} 通过，{_failed} 失败");
        foreach (var failure in Failures) Console.WriteLine($"  [FAIL] {failure}");
        return _failed == 0 ? 0 : 1;
    }

    // ── 场景 1：新用户首日 ────────────────────────────────────────────────
    // 五个视频任务排队，并发上限 2；完成的自动补位；文件按分类落盘；
    // 完成即签名入库。跑完这一套，「下载器开箱可用」才算成立。

    private static async Task FirstDayAsync(TestServer server)
    {
        var root = TempDir();
        var files = Enumerable.Range(1, 5)
            .Select(i => (name: $"movie-{i}.bin", data: Payload(200 * 1024 + i * 4096)))
            .ToList();
        foreach (var (name, data) in files) server.Map($"day1/{name}", server.RangeFile(data, etag: $"\"{name}\""));

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 2,
            ConnectionsPerTask = 3,
            DefaultSavePath = root,
            FoldersByCategory = true,
            HashDatabasePath = Path.Combine(root, "hashes.json"),
        });

        var tasks = files.Select((f, i) => kernel.Add(new NewDownloadRequest(
                server.Base + $"day1/{f.name}", "", "video", 3,
                i < 2 ? TaskPriority.High : TaskPriority.Normal, StartNow: true)))
            .ToList();

        // 并发天花板：整个生命周期内同时在跑的 never 超过 2
        int maxRunning = 0;
        await WaitAsync(() =>
        {
            int running = tasks.Count(t => t.Status == DownloadStatus.Downloading);
            maxRunning = Math.Max(maxRunning, running);
            return tasks.All(t => t.Status is DownloadStatus.Completed or DownloadStatus.Error);
        }, "五个任务都应到达终态", 60_000);

        Equal(5, tasks.Count(t => t.Status == DownloadStatus.Completed), "五个任务全部完成");
        That(maxRunning is > 0 and <= 2, $"并发峰值应 ≤ 2（实际 {maxRunning}）");

        // 分类落盘：video 分类 → <默认目录>/video/<文件名>
        foreach (var (name, data) in files)
        {
            var path = Path.Combine(root, "video", name);
            That(File.Exists(path), $"文件应按分类落盘：video/{name}");
            That(File.ReadAllBytes(path).SequenceEqual(data), $"字节精确：{name}");
        }

        // 签名库：五个 URL 全部入库
        var db = File.ReadAllText(Path.Combine(root, "hashes.json"));
        foreach (var (name, _) in files)
            That(db.Contains(server.Base + $"day1/{name}"), $"签名库应记录 {name}");
    }

    // ── 场景 2：网络抖动一天 ──────────────────────────────────────────────
    // 第一次响应被腰斩、第二次直接 500——真实移动网络的日常。
    // 内核要自己扛住：重试、续传、最终字节精确。

    private static async Task FlakyNetworkAsync(TestServer server)
    {
        var data = Payload(1536 * 1024);
        int gets = 0;
        server.Map("flaky/file.bin", server.RangeFile(data, etag: "\"flaky\"", onGet: (n, ctx) =>
        {
            lock (EventLock) gets = Math.Max(gets, n);
            if (n == 1)
            {
                // 响应到一半把连接掐了
                var response = ctx.Response;
                response.StatusCode = 200;
                response.Headers["Accept-Ranges"] = "bytes";
                response.ContentType = "application/octet-stream";
                response.ContentLength64 = data.Length;
                response.OutputStream.Write(data, 0, data.Length / 2);
                try { response.OutputStream.Close(); } catch { /* 已断 */ }
                return true;
            }
            if (n == 2)
            {
                TestServer.Reply(ctx.Response, 500, Array.Empty<byte>());
                return true;
            }
            return false;
        }));

        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "flaky/file.bin", TempDir(), "", 1,
            TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "抖动网络下任务最终应完成", 60_000);

        var saved = Path.Combine(task.SavePath, "file.bin");
        That(File.Exists(saved), $"文件应存在（{saved}）");
        That(File.ReadAllBytes(saved).SequenceEqual(data), "跨两次断连后字节仍精确");
        lock (EventLock)
            That(gets >= 3, $"服务器应看到多次尝试（实际 {gets} 次 GET）");
    }

    // ── 场景 3：PT 用户日常 ───────────────────────────────────────────────
    // magnet 带私有 passkey：引擎必须拿到原文，日志/展示必须看不到；
    // 做种参数进命令模板；引擎产出的文件照常过校验并进签名库。

    private static async Task PtUserAsync(TestServer server)
    {
        const string secret = "DAY3-SECRET-passkey";
        var magnet = "magnet:?xt=urn:btih:aaaabbbbccccddddeeeeffff0000111122223333&dn=pt.day3.iso" +
                     $"&tr=https%3a%2f%2fpt.example%2fannounce%3fpasskey%3d{Uri.EscapeDataString(secret)}";
        var root = TempDir();
        var dbPath = Path.Combine(root, "hashes.json");

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            DefaultSavePath = root,
            FoldersByCategory = true,
            HashDatabasePath = dbPath,
            ExternalEngineCommand = "printf '%s' '{url}' > '{path}'",
        });
        var task = kernel.Add(new NewDownloadRequest(magnet, "", "bt", 1, TaskPriority.Normal,
            StartNow: true, SeedRatio: 2.0, SeedHours: 72));
        await WaitAsync(() => task.Status is DownloadStatus.Completed or DownloadStatus.Error,
            "引擎命令任务应有终态", 60_000);
        Equal(DownloadStatus.Completed, task.Status, $"引擎产出文件应完成（{task.ErrorMessage}）");

        // 数据面：引擎拿到真实 passkey
        var produced = Path.Combine(root, "bt", "pt.day3.iso");
        That(File.Exists(produced), $"bt 分类下应有引擎产出：{produced}");
        That(File.ReadAllText(produced).Contains(secret), "引擎必须拿到含真实 passkey 的 magnet 原文");

        // 展示面：任何 UI 可见字段与日志都不得出现 passkey
        Equal(magnet, task.Url, "原始 magnet 只留给引擎下发");
        That(task.DisplayUrl is not null && task.DisplayUrl.Contains("***") && !task.DisplayUrl.Contains(secret),
            "展示链接必须脱敏");
        That(task.Trackers is { Count: 1 } && task.Trackers[0].Contains("***"), "tracker 应脱敏");
        That(task.Log.All(l => !l.Message.Contains(secret)), $"全部日志不得含 passkey（{string.Join(" | ", task.Log.Select(l => l.Message))}）");

        // 引擎产出照常过完整性链路：签名入库
        var db = File.ReadAllText(dbPath);
        That(db.Contains("aaaabbbbccccddddeeeeffff0000111122223333".ToLowerInvariant()) ||
             db.Contains("urn:btih") || db.Contains("magnet:"), "签名库应记录该 magnet 任务");
        Equal("SHA-256 已校验", task.Checksum, "引擎产出文件应完成 SHA-256 自检");
    }

    // ── 场景 4：混跑 + 全局限速 ───────────────────────────────────────────
    // 两个多分段任务同时跑，全局总闸 150 KB/s。修过的 bug 是「各自 150」
    // 合计 300——这里用总时长抓回归：600KB/150KBps ≈ 4s，共享桶才对。

    private static async Task MixedTrafficAsync(TestServer server)
    {
        var a = Payload(300 * 1024);
        var b = Payload(300 * 1024, 0x5A);
        server.Map("mix/a.bin", server.RangeFile(a, etag: "\"mix-a\""));
        server.Map("mix/b.bin", server.RangeFile(b, etag: "\"mix-b\""));

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 4,
            MaxSimultaneous = 2,
            GlobalSpeedLimit = 150 * 1024,
        });
        var watch = Stopwatch.StartNew();
        var taskA = kernel.Add(new NewDownloadRequest(server.Base + "mix/a.bin", TempDir(), "", 4, TaskPriority.Normal, StartNow: true));
        var taskB = kernel.Add(new NewDownloadRequest(server.Base + "mix/b.bin", TempDir(), "", 4, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => taskA.Status == DownloadStatus.Completed && taskB.Status == DownloadStatus.Completed,
            "两个任务都应完成", 90_000);
        watch.Stop();

        That(watch.ElapsedMilliseconds >= 3000,
            $"共享限速下 600KB @150KB/s 应 ≥3s（实际 {watch.ElapsedMilliseconds}ms——若过快说明各任务独立限速）");
        That(File.ReadAllBytes(Path.Combine(taskA.SavePath, "a.bin")).SequenceEqual(a), "A 字节精确");
        That(File.ReadAllBytes(Path.Combine(taskB.SavePath, "b.bin")).SequenceEqual(b), "B 字节精确");
    }

    // ── 场景 5：坏文件处置 ───────────────────────────────────────────────
    // 发布页抄来的哈希和实收不符：任务必须失败而不是把坏文件当完成；
    // 用户换源重下（服务器内容已修正）→ 完成 + 校验通过 + 签名入库。

    private static async Task BadFileRecoveryAsync(TestServer server)
    {
        var bad = Payload(256 * 1024);
        var good = Payload(256 * 1024, 0xA5);
        server.Map("day5/app.bin", server.RangeFile(bad, etag: "\"day5-bad\""));
        var goodHash = Convert.ToHexString(SHA256.HashData(good)).ToLowerInvariant();

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 2,
            MaxSimultaneous = 1,
            HashDatabasePath = Path.Combine(TempDir(), "hashes.json"),
        });

        var task = kernel.Add(new NewDownloadRequest(server.Base + "day5/app.bin", TempDir(), "", 2,
            TaskPriority.Normal, StartNow: true, ExpectedHash: goodHash));
        await WaitAsync(() => task.Status == DownloadStatus.Error, "坏文件必须判 Error", 60_000);
        That(task.ErrorMessage!.Contains("校验失败"), "错误信息应说明校验失败");
        Equal("SHA-256 校验失败", task.Checksum, "Checksum 应标注失败");

        // 换源修正后重下（Redownload 复用同一任务对象）
        server.Map("day5/app.bin", server.RangeFile(good, etag: "\"day5-good\""));
        kernel.Redownload(task.Id);
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "修正后重下应完成", 60_000);
        That(File.ReadAllBytes(Path.Combine(task.SavePath, "app.bin")).SequenceEqual(good), "重下文件字节精确");
        // Status 先于校验翻转（内核完成即报 Completed，Checksum 稍后标注）：等待校验信号
        await WaitAsync(() => task.Checksum == "SHA-256 已校验", "重下应校验通过", 15_000);
        That(task.Log.Any(l => l.Message.Contains("已记录签名")), "重下完成应记录签名");
    }

    // ── 场景 6：进程重启 ─────────────────────────────────────────────────
    // 下载过半「进程退出」（Dispose 保留 checkpoint），新实例接手：
    // 必须续传而不是从零开始，且最终字节精确。

    private static async Task RestartResumeAsync(TestServer server)
    {
        // 断点续传是大文件直写（DirectSink）语义：载荷必须超过小文件内存缓冲阈值（默认 4MB）
        var data = Payload(6 * 1024 * 1024);
        var resumedStarts = new List<long>();
        server.Map("restart/big.bin", server.RangeFile(data, etag: "\"restart\"", onGet: (n, ctx) =>
        {
            var range = ctx.Request.Headers["Range"];
            if (range is not null && long.TryParse(range["bytes=".Length..].Split('-')[0], out var start) && start > 0)
                lock (resumedStarts) resumedStarts.Add(start);
            return false;
        }));

        var saveDir = TempDir();
        var options = new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 };

        long firstDone;
        using (var kernel1 = new RealDownloadKernel(options))
        {
            var task = kernel1.Add(new NewDownloadRequest(server.Base + "restart/big.bin", saveDir, "", 1,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Done > 512 * 1024, "第一阶段应下载过 512KB", 60_000);
            firstDone = task.Done;
        } // 进程退出：checkpoint 已周期性落盘

        That(firstDone > 0, "退出前应有实际进度");

        using (var kernel2 = new RealDownloadKernel(options))
        {
            var task2 = kernel2.Add(new NewDownloadRequest(server.Base + "restart/big.bin", saveDir, "", 1,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task2.Status == DownloadStatus.Completed, "重启后任务应完成", 90_000);
            That(File.ReadAllBytes(Path.Combine(task2.SavePath, "big.bin")).SequenceEqual(data), "重启续传后字节精确");
            lock (resumedStarts)
                That(resumedStarts.Count > 0 && resumedStarts.Min() > 0,
                    $"应有从 >0 偏移续传的请求（实际 {resumedStarts.Count} 个续传请求）");
        }
    }

    // ── 场景 7：队列指挥室 ───────────────────────────────────────────────
    // 真实用户对队列做的每个动作：插队、全停全续、下载中重下、
    // 下载中移除、限速文本与优先级调整。并发天花板 1 + 全局限速让
    // 时序可预期：A 先跑，C 插到 B 前，B 靠高优先级接棒。

    private static async Task QueueControlAsync(TestServer server)
    {
        var payloads = new Dictionary<string, byte[]>();
        foreach (var name in new[] { "a.bin", "b.bin", "c.bin", "d.bin" })
        {
            var data = Payload(320 * 1024, name[0]);
            payloads[name] = data;
            server.Map($"day7/{name}", server.RangeFile(data, etag: $"\"{name}\""));
        }

        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 1,
            ConnectionsPerTask = 2,
            GlobalSpeedLimit = 80 * 1024,
        });
        var completionOrder = new List<string>();
        var moved = new List<int>();
        var removed = new List<int>();
        kernel.TaskCompleted += t => { lock (EventLock) completionOrder.Add(t.Name); };
        kernel.TaskMoved += t => { lock (EventLock) moved.Add(t.Id); };
        kernel.TaskRemoved += t => { lock (EventLock) removed.Add(t.Id); };

        var a = kernel.Add(new NewDownloadRequest(server.Base + "day7/a.bin", TempDir(), "", 2, TaskPriority.Normal, StartNow: true));
        var b = kernel.Add(new NewDownloadRequest(server.Base + "day7/b.bin", TempDir(), "", 2, TaskPriority.Normal, StartNow: true));
        var c = kernel.Add(new NewDownloadRequest(server.Base + "day7/c.bin", TempDir(), "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => a.Status == DownloadStatus.Downloading && b.Status == DownloadStatus.Queued,
            "A 应先获得通道", 30_000);

        // 插队：C 抢到 B 前；对队首/运行中任务 MoveToFront 是无操作
        kernel.MoveToFront(c.Id);
        kernel.MoveToFront(a.Id);
        kernel.MoveToBack(c.Id);   // C 不在队尾 → 实际移动
        kernel.MoveToFront(c.Id);  // 移回队首
        lock (EventLock)
            That(moved.Contains(c.Id), "MoveToFront/MoveToBack 应触发 TaskMoved");

        // 限速文本 Zoo：全部单位 + 非法输入 + 不限 + 纯数字（解析在 ApplyLimitLocked 内执行）
        foreach (var limit in new[] { "512 B/s", "1 KB/s", "1.5 MB/s", "2 GB/s", "不限", "junk", "100" })
            kernel.SetSpeedLimit(b.Id, limit);
        Equal("100", b.SpeedLimit, "限速文本应原样记录");
        kernel.SetSpeedLimit(9999, "1 KB/s"); // 未知 id → 无操作

        // 「夜间不限速」窗口判定：跨午夜 / 窗口内 / 窗口外 / 相等 / 非法输入
        That(RealDownloadKernel.InOffPeakWindow(new(23, 30, 0), "23:00", "07:00"), "跨午夜窗口：23:30 应在窗口内");
        That(RealDownloadKernel.InOffPeakWindow(new(3, 0, 0), "23:00", "07:00"), "跨午夜窗口：03:00 应在窗口内");
        That(!RealDownloadKernel.InOffPeakWindow(new(12, 0, 0), "23:00", "07:00"), "跨午夜窗口：正午应在窗口外");
        That(RealDownloadKernel.InOffPeakWindow(new(1, 30, 0), "01:00", "02:00"), "普通窗口：01:30 应在窗口内");
        That(!RealDownloadKernel.InOffPeakWindow(new(2, 30, 0), "01:00", "02:00"), "普通窗口：02:30 应在窗口外");
        That(!RealDownloadKernel.InOffPeakWindow(new(5, 0, 0), "05:00", "05:00"), "零长度窗口应视为无窗口");
        That(!RealDownloadKernel.InOffPeakWindow(new(5, 0, 0), "bad", "07:00"), "非法起点应视为无窗口");
        That(!RealDownloadKernel.InOffPeakWindow(new(5, 0, 0), null, "07:00"), "空起点应视为无窗口");
        That(!RealDownloadKernel.InOffPeakWindow(new(5, 0, 0), "25:00", "07:00"), "越界小时应视为无窗口");
        That(!RealDownloadKernel.InOffPeakWindow(new(5, 0, 0), "12:60", "07:00"), "越界分钟应视为无窗口");

        await WaitAsync(() => a.Status == DownloadStatus.Completed &&
                             c.Status == DownloadStatus.Downloading && b.Status == DownloadStatus.Queued,
            "C 应插到 B 前接棒", 90_000);

        kernel.ToggleAll(); // 全暂停（下载中→暂停、排队→暂停、已完成跳过）
        await WaitAsync(() => c.Status == DownloadStatus.Paused && b.Status == DownloadStatus.Paused,
            "全部任务应暂停", 15_000);
        kernel.ToggleAll(); // 全恢复
        await WaitAsync(() => c.Status == DownloadStatus.Downloading, "恢复后 C 应继续", 30_000);
        await WaitAsync(() => c.Status == DownloadStatus.Completed && b.Status == DownloadStatus.Downloading,
            "B 应接着跑", 90_000);

        // B 跑起来后再调优先级（Normal→High 生效，再次同值是无操作）
        kernel.SetPriority(b.Id, TaskPriority.High);
        kernel.SetPriority(b.Id, TaskPriority.High);
        Equal(TaskPriority.High, b.Priority, "优先级应生效");

        // 下载中重下：走 Restart 标记，任务原地重来
        kernel.Redownload(b.Id);
        await WaitAsync(() => b.Status == DownloadStatus.Completed, "下载中重下应完成", 120_000);
        That(File.ReadAllBytes(Path.Combine(b.SavePath, "b.bin")).SequenceEqual(payloads["b.bin"]), "重下字节精确");

        // 已完成任务 Toggle / 未知 id Toggle：无操作；已完成任务的 Redownload → 原地重下
        kernel.Toggle(a.Id);
        Equal(DownloadStatus.Completed, a.Status, "已完成任务不应被 Toggle 改状态");
        kernel.Toggle(9999);
        Equal(3, kernel.Tasks.Count, "未知 id 的 Toggle 不应改变任务数");
        kernel.SetPriority(a.Id, TaskPriority.High); // 已完成任务调优先级 → 只记字段
        kernel.Redownload(a.Id);
        await WaitAsync(() => a.Status == DownloadStatus.Completed, "已完成任务重下应完成", 120_000);
        That(File.ReadAllBytes(Path.Combine(a.SavePath, "a.bin")).SequenceEqual(payloads["a.bin"]), "A 重下字节精确");

        lock (EventLock)
            That(completionOrder.IndexOf(a.Name) == 0 &&
                 completionOrder.IndexOf(c.Name) < completionOrder.IndexOf(b.Name),
                 $"完成顺序应为 A→C→B（实际 {string.Join(",", completionOrder)}）");

        // 下载中移除：任务消失、事件触发、内核回空闲
        var d = kernel.Add(new NewDownloadRequest(server.Base + "day7/d.bin", TempDir(), "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => d.Status == DownloadStatus.Downloading && d.Done > 0, "D 应获得通道", 120_000);
        kernel.Remove(new[] { d.Id });
        await WaitAsync(() => removed.Contains(d.Id), "移除应触发 TaskRemoved", 15_000);
        That(!kernel.Tasks.Any(t => t.Id == d.Id), "移除后任务应消失");
        await WaitAsync(() => !kernel.Snapshot.IsRunning, "移除后内核应回空闲", 15_000);

        // 收尾断言：三次完整下载全部字节精确
        foreach (var (name, data) in payloads.Where(p => p.Key != "d.bin"))
        {
            var task = new[] { a, b, c }.First(t => t.Name == name);
            That(File.ReadAllBytes(Path.Combine(task.SavePath, name)).SequenceEqual(data), $"字节精确：{name}");
        }
    }

    // ── 场景 8：暂停续传（不关进程） ─────────────────────────────────────
    // 用户点「暂停」：DirectSink 落断点、任务停在暂停态；再点「开始」：
    // 从断点续传而不是从零开始。限速 512KB/s 让前 1.5MB 稳定落在
    // 断点定时器（2s）之后，暂停时断点必然已在磁盘上。

    private static async Task PauseResumeAsync(TestServer server)
    {
        var data = Payload(6 * 1024 * 1024);
        var resumedStarts = new List<long>();
        server.Map("day8/big.bin", server.RangeFile(data, etag: "\"day8\"", onGet: (n, ctx) =>
        {
            var range = ctx.Request.Headers["Range"];
            if (range is not null && long.TryParse(range["bytes=".Length..].Split('-')[0], out var start) && start > 0)
                lock (EventLock) resumedStarts.Add(start);
            return false;
        }));

        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 1,
            MaxSimultaneous = 1,
            GlobalSpeedLimit = 512 * 1024,
        });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "day8/big.bin", dir, "", 1,
            TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => task.Done > 1536 * 1024, "第一阶段应下载过 1.5MB", 60_000);
        That(kernel.Snapshot.IsRunning, "运行中快照应报告 IsRunning");
        That(kernel.Snapshot.TotalSpeed > 0, "运行中应有实际速度");
        That(kernel.Snapshot.SpeedHistory.Length > 0, "速度历史应有样本");
        That(kernel.Snapshot.UploadSpeed == 0, "纯 HTTP 下载不应有上传速度");

        kernel.Toggle(task.Id);
        await WaitAsync(() => task.Status == DownloadStatus.Paused, "暂停应生效", 15_000);
        var checkpoint = Path.Combine(dir, "big.bin.nettrans.json");
        That(File.Exists(checkpoint), $"暂停应落盘断点（{checkpoint}）");

        kernel.Toggle(task.Id);
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "恢复后应完成", 120_000);
        That(File.ReadAllBytes(Path.Combine(dir, "big.bin")).SequenceEqual(data), "断点续传字节精确");
        lock (EventLock)
            That(resumedStarts.Count > 0 && resumedStarts.Min() > 0,
                $"应从 >0 偏移续传（实际 {resumedStarts.Count} 个续传请求）");
        That(!File.Exists(checkpoint), "完成应清理断点文件");
        await WaitAsync(() => !kernel.Snapshot.IsRunning, "完成后快照应停止运行标记", 10_000);
    }

    // ── 场景 9：链接动物园 ───────────────────────────────────────────────
    // 用户从论坛/发布页抄来五花八门的链接：ed2k、magnet（贫富不齐）、
    // .torrent、纯乱码、ftp 死链、扩展名伪装。内核要认得出每一种、
    // 报错报得明白、脱敏一步不漏、引擎故障如实上报。

    private static async Task LinkZooAsync(TestServer server)
    {
        // ① ed2k + 未接引擎 → 明确报错而不是装死
        using (var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 1 }))
        {
            var link = "ed2k://|file|ubuntu-24.04.iso|3145728|0123456789ABCDEF0123456789ABCDEF|/";
            var task = kernel.Add(new NewDownloadRequest(link, TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Error, "无引擎 ed2k 应报错", 30_000);
            That(task.ErrorMessage!.Contains("外部引擎"), $"错误应指向引擎插槽（{task.ErrorMessage}）");
            That(task.DisplayUrl is not null, "ed2k 展示链接应走脱敏通道");
            That(task.Name == "ubuntu-24.04.iso", $"eD2k 链接应解析出文件名（实际 {task.Name}）");
            Equal(3145728L, task.Size, "eD2k 链接应解析出大小");
        }

        // ② 畸形 ed2k → Unsupported 计划，报错来自驱动
        using (var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest("ed2k://|junk", TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Error, "畸形 ed2k 应报错", 30_000);
            That(task.ErrorMessage!.Contains("eD2k"), $"错误应来自 eD2k 驱动（{task.ErrorMessage}）");
            kernel.ToggleAll(); // 失败任务批量重试：Error → 重新排队
            await WaitAsync(() => task.Status == DownloadStatus.Error, "重试后应再次报错", 30_000);
        }

        // ③ 引擎在位：magnet（贫/富/残缺）、.torrent 全家桶
        var payload = Payload(96 * 1024, 0x77);
        var root = TempDir();
        var torrent = TorrentBytes("debian.iso", 524288,
            "http://tracker1.example/announce", "udp://tracker2.example:1337/announce");
        server.Map("day9/debian.torrent", server.RangeFile(torrent, contentType: "application/octet-stream"));

        using (var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 3,
            DefaultSavePath = root,
            FoldersByCategory = true,
            HashDatabasePath = Path.Combine(TempDir(), "hashes.json"),
            EngineRunner = (plan, path, ct) => { File.WriteAllBytes(path, payload); return Task.FromResult(true); },
        }))
        {
            // 贫 magnet：只有 xt + xl → 名字退回 bt-<hash8>，大小来自 xl
            var poor = kernel.Add(new NewDownloadRequest(
                $"magnet:?xt=urn:btih:{new string('a', 40)}&xl=98304", "", "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => poor.Status is DownloadStatus.Completed or DownloadStatus.Error, "贫 magnet 应有终态", 30_000);
            Equal(DownloadStatus.Completed, poor.Status, $"贫 magnet 应完成（{poor.Status}：{poor.ErrorMessage}）");
            Equal("bt-aaaaaaaa", poor.Name, "无 dn 的 magnet 应以 hash 前缀命名");
            Equal(98304L, poor.Size, "xl 参数应成为任务大小");
            Equal("SHA-256 已校验", poor.Checksum, "引擎产出应过 SHA-256 自检");
            That(File.Exists(Path.Combine(root, "bt", "bt-aaaaaaaa")), "产出应落在 bt 分类目录");

            // 富 magnet：dn + xl + 两种 tracker（含 passkey）→ 脱敏必须到位
            const string secret = "DAY9-SECRET-passkey";
            var rich = kernel.Add(new NewDownloadRequest(
                $"magnet:?xt=urn:btih:{new string('b', 40)}&dn=pt.day9.bin&xl=98304" +
                $"&tr=udp%3a%2f%2ftracker.example%3a1337%2fannounce" +
                $"&tr=https%3a%2f%2fpt.example%2fannounce%3fpasskey%3d{Uri.EscapeDataString(secret)}",
                "", "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => rich.Status == DownloadStatus.Completed, "富 magnet 应完成", 30_000);
            Equal("pt.day9.bin", rich.Name, "dn 参数应成为任务名");
            That(rich.Trackers is { Count: 2 }, $"应解析出 2 个 tracker（实际 {rich.Trackers?.Count}）");
            That(rich.Trackers!.Any(t => t.Contains("***")), "含 passkey 的 tracker 应脱敏");
            That(rich.Trackers.All(t => !t.Contains(secret)), "tracker 脱敏后不得残留 passkey");
            That(rich.Trackers.Any(t => t.Contains("udp://tracker.example:1337/announce")), "无凭据 tracker 应原样展示");
            That(rich.DisplayUrl is not null && !rich.DisplayUrl.Contains(secret), "展示链接应脱敏");

            // .torrent：HTTP 取回元数据 → 解析出名字/大小/tracker → 引擎产出
            var torrentTask = kernel.Add(new NewDownloadRequest(
                server.Base + "day9/debian.torrent", "", "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => torrentTask.Status is DownloadStatus.Completed or DownloadStatus.Error,
                ".torrent 任务应有终态", 30_000);
            Equal(DownloadStatus.Completed, torrentTask.Status,
                $".torrent 任务应完成（{torrentTask.Status}：{torrentTask.ErrorMessage}）");
            Equal("debian.iso", torrentTask.Name, "种子元数据应更正任务名");
            Equal(524288L, torrentTask.Size, "种子元数据应给出大小");
            That(torrentTask.Trackers is { Count: 2 }, "种子 tracker 应入库");
            That(torrentTask.Log.Any(l => l.Message.Contains("BT 元数据")), "应记录 BT 元数据日志");
            That(File.Exists(Path.Combine(root, "bt", "debian.iso")), "产出应落在 bt 分类目录");

            // 残缺 magnet：无 xt → dn 兜底命名，仍可完成
            var broken = kernel.Add(new NewDownloadRequest("magnet:?dn=only", "", "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => broken.Status == DownloadStatus.Completed, "残缺 magnet 应完成", 30_000);
            Equal("only", broken.Name, "无 xt 的 magnet 应以 dn 参数兜底命名");

            // 引擎故障三连：运行失败 / 未产出文件 / 运行异常
            using (var failKernel = new RealDownloadKernel(new KernelOptions
            {
                MaxSimultaneous = 3,
                DefaultSavePath = TempDir(),
                EngineRunner = (plan, path, ct) => Task.FromResult(false),
            }))
            {
                var fail = failKernel.Add(new NewDownloadRequest(
                    $"magnet:?xt=urn:btih:{new string('c', 40)}", "", "", 1, TaskPriority.Normal, StartNow: true));
                await WaitAsync(() => fail.Status == DownloadStatus.Error, "引擎失败应报错", 30_000);
                That(fail.ErrorMessage!.Contains("外部引擎运行失败"), $"应如实上报（{fail.ErrorMessage}）");
            }

            using (var noFileKernel = new RealDownloadKernel(new KernelOptions
            {
                MaxSimultaneous = 3,
                DefaultSavePath = TempDir(),
                EngineRunner = (plan, path, ct) => Task.FromResult(true),
            }))
            {
                var noFile = noFileKernel.Add(new NewDownloadRequest(
                    $"magnet:?xt=urn:btih:{new string('d', 40)}", "", "", 1, TaskPriority.Normal, StartNow: true));
                await WaitAsync(() => noFile.Status == DownloadStatus.Error, "引擎未产出文件应报错", 30_000);
                That(noFile.ErrorMessage!.Contains("未产出文件"), $"应如实上报（{noFile.ErrorMessage}）");
            }

            using (var throwKernel = new RealDownloadKernel(new KernelOptions
            {
                MaxSimultaneous = 3,
                DefaultSavePath = TempDir(),
                EngineRunner = (plan, path, ct) => throw new InvalidOperationException("boom"),
            }))
            {
                var thrown = throwKernel.Add(new NewDownloadRequest(
                    $"magnet:?xt=urn:btih:{new string('e', 40)}", "", "", 1, TaskPriority.Normal, StartNow: true));
                await WaitAsync(() => thrown.Status == DownloadStatus.Error, "引擎异常应报错", 30_000);
                That(thrown.ErrorMessage!.Contains("boom"), $"异常消息应透传（{thrown.ErrorMessage}）");
            }
        }

        // ④ 乱码链接：HTTP 探测失败 → 明确 Error + 未知来源
        using (var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest("not a url", TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Error, "乱码链接应报错", 30_000);
            Equal("未知来源", task.Host, "无法解析的主机应标注未知来源");
        }

        // ⑤ ftp 死链：端口 1 立即拒绝 → Error（不悬挂）
        using (var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest("ftp://127.0.0.1:1/pub/ubuntu.iso", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Error, "ftp 死链应报错", 30_000);
            That(task.ErrorMessage is not null && task.ErrorMessage.Length > 0, "应有具体错误消息");
        }

        // ⑥ 扩展名伪装：.m3u8 / .mpd 指向 404 → 走对应驱动后报错；坏种子同理
        server.Map("day9/broken.torrent", server.RangeFile(Encoding.UTF8.GetBytes("this is not bencode"), contentType: "application/octet-stream"));
        using (var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 2 }))
        {
            var m3u8 = kernel.Add(new NewDownloadRequest(server.Base + "day9/missing.m3u8", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true));
            var mpd = kernel.Add(new NewDownloadRequest(server.Base + "day9/missing.mpd", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true));
            var brokenTorrent = kernel.Add(new NewDownloadRequest(server.Base + "day9/broken.torrent", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true, ExpectedHash: "   "));
            await WaitAsync(() => m3u8.Status == DownloadStatus.Error && mpd.Status == DownloadStatus.Error &&
                                 brokenTorrent.Status == DownloadStatus.Error,
                "伪装流媒体/坏种子链接应报错", 30_000);
        }
    }

    // ── 场景 10：命名与校验的日常 ────────────────────────────────────────
    // 同名文件不覆盖（(1) 改写）、显式文件目标、Content-Disposition 与
    // filename* 命名、MD5/SHA-1/SHA-256 多算法校验、签名库「签名变化」
    // 提示、损坏/外来断点兜底、镜像源探测与降级、请求头合并、自动分类。

    private static async Task NamingChecksumMirrorAsync(TestServer server)
    {
        var root = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 2,
            MaxSimultaneous = 2,
            DefaultSavePath = root,
            FoldersByCategory = true,
            HashDatabasePath = Path.Combine(root, "hashes.json"),
            DefaultHeaders = new Dictionary<string, string> { ["Referer"] = server.Base },
            MirrorResolver = (url, ct) => Task.FromResult<IReadOnlyList<string>>(url.Contains("solo.bin")
                ? new List<string>()
                : new List<string>
                {
                    server.Base + "day10/mirror.bin",
                    server.Base + "day10/mirror-bad.bin",
                    server.Base + "day10/mirror-etag.bin",
                    "http://127.0.0.1:1/mirror-dead.bin",
                }),
        });

        // ① 防碰撞：同名再下一遍 → same.bin 之外改写为 same (1).bin
        var same1 = Payload(64 * 1024, 0x11);
        var same2 = Payload(64 * 1024, 0x22);
        server.Map("day10/one/same.bin", server.RangeFile(same1, etag: "\"same1\""));
        server.Map("day10/two/same.bin", server.RangeFile(same2, etag: "\"same2\""));
        var sameDir = TempDir();
        var first = kernel.Add(new NewDownloadRequest(server.Base + "day10/one/same.bin", sameDir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => first.Status == DownloadStatus.Completed, "首遍下载应完成", 60_000);
        var second = kernel.Add(new NewDownloadRequest(server.Base + "day10/two/same.bin", sameDir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => second.Status == DownloadStatus.Completed, "二遍下载应完成", 60_000);
        That(File.ReadAllBytes(Path.Combine(first.SavePath, "same.bin")).SequenceEqual(same1), "先到者用原名");
        That(File.ReadAllBytes(Path.Combine(second.SavePath, "same (1).bin")).SequenceEqual(same2), "后到者改写为 (1)");

        // ② 显式文件目标（SaveTo 自带扩展名）→ 原样落盘
        var explicitTarget = Path.Combine(TempDir(), "explicit.bin");
        server.Map("day10/explicit-src.bin", server.RangeFile(Payload(32 * 1024, 0x33), etag: "\"explicit\""));
        var explicitTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/explicit-src.bin", explicitTarget, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => explicitTask.Status == DownloadStatus.Completed, "显式目标应完成", 60_000);
        That(File.Exists(explicitTarget), "显式文件目标应原样使用");

        // ③ Content-Disposition：quoted filename（含空格）与 filename*（UTF-8 百分号编码）
        var cdData = Payload(48 * 1024, 0x44);
        server.Map("day10/cd.bin", ctx =>
        {
            ctx.Response.Headers["Content-Disposition"] = "attachment; filename=\"setup 1.0.exe\"";
            if (ctx.Request.HttpMethod == "HEAD") { TestServer.ServeHead(ctx, cdData, "\"cd\""); return; }
            TestServer.ServeRange(ctx, cdData, etag: "\"cd\"");
        });
        var cdTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/cd.bin", "", "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => cdTask.Status == DownloadStatus.Completed, "Content-Disposition 任务应完成", 60_000);
        Equal("setup 1.0.exe", cdTask.Name, "quoted filename 应成为任务名");
        That(File.Exists(Path.Combine(root, "doc", "setup 1.0.exe")), "应按新名字落盘（分类在添加时按 URL 名判定 → doc）");

        var starData = Payload(48 * 1024, 0x55);
        server.Map("day10/star.bin", ctx =>
        {
            ctx.Response.Headers["Content-Disposition"] = "attachment; filename*=UTF-8''%E6%8A%A5%E5%91%8A.pdf";
            if (ctx.Request.HttpMethod == "HEAD") { TestServer.ServeHead(ctx, starData, "\"star\""); return; }
            TestServer.ServeRange(ctx, starData, etag: "\"star\"");
        });
        var starTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/star.bin", "", "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => starTask.Status == DownloadStatus.Completed, "filename* 任务应完成", 60_000);
        Equal("报告.pdf", starTask.Name, "filename* 百分号编码应解码");
        That(File.Exists(Path.Combine(root, "doc", "报告.pdf")), "应按解码名落盘（pdf → doc 分类）");

        // ④ MD5 / SHA-1 校验（长度判别算法）
        var md5Data = Payload(24 * 1024, 0x66);
        server.Map("day10/md5.bin", server.RangeFile(md5Data, etag: "\"md5\""));
        var md5Task = kernel.Add(new NewDownloadRequest(server.Base + "day10/md5.bin", TempDir(), "", 2, TaskPriority.Normal,
            StartNow: true, ExpectedHash: Convert.ToHexString(MD5.HashData(md5Data))));
        await WaitAsync(() => md5Task.Status == DownloadStatus.Completed, "MD5 任务应完成", 60_000);
        await WaitAsync(() => md5Task.Checksum == "MD5 已校验", "MD5 校验应通过", 15_000);

        var sha1Data = Payload(24 * 1024, 0x88);
        server.Map("day10/sha1.bin", server.RangeFile(sha1Data, etag: "\"sha1\""));
        var sha1Task = kernel.Add(new NewDownloadRequest(server.Base + "day10/sha1.bin", TempDir(), "", 2, TaskPriority.Normal,
            StartNow: true, ExpectedHash: Convert.ToHexString(SHA1.HashData(sha1Data))));
        await WaitAsync(() => sha1Task.Status == DownloadStatus.Completed, "SHA-1 任务应完成", 60_000);
        await WaitAsync(() => sha1Task.Checksum == "SHA-1 已校验", "SHA-1 校验应通过", 15_000);

        // ⑤ 签名库「签名变化」：同 URL 内容变了 → 完成但显著标注
        var v1 = Payload(64 * 1024, 0x99);
        var v2 = Payload(64 * 1024, 0xAA);
        server.Map("day10/evolve.bin", server.RangeFile(v1, etag: "\"evolve1\""));
        var evolveDir = TempDir();
        var evolve1 = kernel.Add(new NewDownloadRequest(server.Base + "day10/evolve.bin", evolveDir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => evolve1.Status == DownloadStatus.Completed, "首版应完成", 60_000);
        await WaitAsync(() => evolve1.Checksum == "SHA-256 已校验", "首版应入库", 15_000);
        server.Map("day10/evolve.bin", server.RangeFile(v2, etag: "\"evolve2\""));
        var evolve2 = kernel.Add(new NewDownloadRequest(server.Base + "day10/evolve.bin", TempDir(), "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => evolve2.Status == DownloadStatus.Completed, "改版应完成", 60_000);
        Equal("SHA-256 已校验（签名变化）", evolve2.Checksum, "内容变化应显著标注");
        That(evolve2.Log.Any(l => l.IsError && l.Message.Contains("不一致")), "签名变化应写错误日志");

        // ⑥ 损坏断点：垃圾 JSON 兜底为「无断点」，下载照常完成
        var corruptDir = TempDir();
        var corruptSrc = Payload(48 * 1024, 0xBB);
        server.Map("day10/corrupt.bin", server.RangeFile(corruptSrc, etag: "\"corrupt\""));
        await File.WriteAllTextAsync(Path.Combine(corruptDir, "corrupt.bin.nettrans.json"), "{ not json");
        var corruptTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/corrupt.bin", corruptDir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => corruptTask.Status == DownloadStatus.Completed, "损坏断点不应阻碍下载", 60_000);
        That(File.ReadAllBytes(Path.Combine(corruptDir, "corrupt.bin")).SequenceEqual(corruptSrc), "损坏断点下字节仍精确");

        // ⑦ 外来断点：同目录遗留别人的断点 → 不得误续传
        var foreignDir = TempDir();
        var foreignSrc = Payload(48 * 1024, 0xCC);
        server.Map("day10/foreign.bin", server.RangeFile(foreignSrc, etag: "\"foreign\""));
        var foreignCp = Path.Combine(foreignDir, "foreign.bin.nettrans.json");
        await File.WriteAllTextAsync(foreignCp, JsonSerializer.Serialize(new TransferCheckpoint
        {
            Kind = "ranges",
            Url = "http://other.example/x",
            Size = 99,
            Start = new long[] { 0 },
            End = new long[] { 98 },
            Done = new long[] { 40 },
        }));
        var foreignTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/foreign.bin", foreignDir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => foreignTask.Status == DownloadStatus.Completed, "外来断点不应误续传", 60_000);
        That(File.ReadAllBytes(Path.Combine(foreignDir, "foreign.bin")).SequenceEqual(foreignSrc), "外来断点下字节仍精确");

        // ⑧ 镜像源：主源 + 好镜像 + 坏尺寸 + 坏指纹 + 死镜像 → 只收好镜像；
        //    「solo」任务的解析器返回空表 → 无镜像照样下载。
        var primary = Payload(600 * 1024, 0xDD);
        server.Map("day10/primary.bin", server.RangeFile(primary, etag: "\"mirror-set\""));
        server.Map("day10/mirror.bin", server.RangeFile(primary, etag: "\"mirror-set\""));
        server.Map("day10/mirror-bad.bin", server.RangeFile(Payload(64 * 1024, 0xEE), etag: "\"mirror-bad\""));
        server.Map("day10/mirror-etag.bin", server.RangeFile(primary, etag: "\"etag-mismatch\""));
        var mirrorTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/primary.bin", TempDir(), "", 3, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => mirrorTask.Status == DownloadStatus.Completed, "镜像任务应完成", 90_000);
        That(mirrorTask.Log.Any(l => l.Message.Contains("镜像探测：1/4")), $"应只收 1 个可用镜像（{string.Join(" | ", mirrorTask.Log.Select(l => l.Message))}）");
        That(mirrorTask.Log.Any(l => l.Message.Contains("个源")), "计划描述应报告镜像数量");
        That(File.ReadAllBytes(Path.Combine(mirrorTask.SavePath, "primary.bin")).SequenceEqual(primary), "镜像下载字节精确");

        var soloSrc = Payload(48 * 1024, 0xDF);
        server.Map("day10/solo.bin", server.RangeFile(soloSrc, etag: "\"solo\""));
        var soloTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/solo.bin", TempDir(), "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => soloTask.Status == DownloadStatus.Completed, "空镜像列表任务应完成", 60_000);
        That(soloTask.Log.All(l => !l.Message.Contains("镜像探测")), "空镜像列表不应触发镜像探测");
        That(File.ReadAllBytes(Path.Combine(soloTask.SavePath, "solo.bin")).SequenceEqual(soloSrc), "无镜像下载字节精确");

        // ⑨ 请求头合并：默认 Referer + 任务 Cookie 同时到达服务器
        var observedCookies = new List<string>();
        var headerData = Payload(32 * 1024, 0x0F);
        server.Map("day10/auth.bin", server.RangeFile(headerData, etag: "\"auth\"", onGet: (n, ctx) =>
        {
            lock (EventLock) observedCookies.Add(ctx.Request.Headers["Cookie"] ?? "");
            return false;
        }));
        var authTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/auth.bin", TempDir(), "", 1, TaskPriority.Normal,
            StartNow: true, Headers: new Dictionary<string, string> { ["Cookie"] = "session=abc" }));
        await WaitAsync(() => authTask.Status == DownloadStatus.Completed, "带头任务应完成", 60_000);
        lock (EventLock)
            That(observedCookies.Any(c => c.Contains("session=abc")), $"Cookie 应随请求到达（实际 {string.Join(",", observedCookies)}）");

        // ⑩ 自动分类：无分类任务按扩展名落 music/doc/soft 目录；8 连接的小任务
        server.Map("day10/song.flac", server.RangeFile(Payload(16 * 1024, 0x10), etag: "\"song\""));
        server.Map("day10/song.mp3", server.RangeFile(Payload(16 * 1024, 0x30), etag: "\"song-mp3\""));
        server.Map("day10/pic.jpg", server.RangeFile(Payload(16 * 1024, 0x40), etag: "\"pic\""));
        server.Map("day10/note.txt", server.RangeFile(Payload(16 * 1024, 0x50), etag: "\"note\""));
        server.Map("day10/tool.zip", server.RangeFile(Payload(16 * 1024, 0x20), etag: "\"tool\""));
        var song = kernel.Add(new NewDownloadRequest(server.Base + "day10/song.flac", "", "", 1, TaskPriority.Normal, StartNow: true));
        var songMp3 = kernel.Add(new NewDownloadRequest(server.Base + "day10/song.mp3", "", "", 8, TaskPriority.Normal, StartNow: true));
        var pic = kernel.Add(new NewDownloadRequest(server.Base + "day10/pic.jpg", "", "", 8, TaskPriority.Normal, StartNow: true));
        var note = kernel.Add(new NewDownloadRequest(server.Base + "day10/note.txt", "", "", 1, TaskPriority.Normal, StartNow: true));
        var tool = kernel.Add(new NewDownloadRequest(server.Base + "day10/tool.zip", "", "", 1, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => song.Status == DownloadStatus.Completed && tool.Status == DownloadStatus.Completed &&
                             songMp3.Status == DownloadStatus.Completed && pic.Status == DownloadStatus.Completed &&
                             note.Status == DownloadStatus.Completed,
            "自动分类任务应完成", 60_000);
        That(File.Exists(Path.Combine(root, "music", "song.flac")), "flac 应落 music 目录");
        That(File.Exists(Path.Combine(root, "music", "song.mp3")), "mp3 应落 music 目录");
        That(File.Exists(Path.Combine(root, "doc", "pic.jpg")), "jpg 应落 doc 目录");
        That(File.Exists(Path.Combine(root, "doc", "note.txt")), "txt 应落 doc 目录");
        That(File.Exists(Path.Combine(root, "soft", "tool.zip")), "zip 应落 soft 目录");
        Equal("#FF2D55", song.Tint, "音乐类型应有专属配色");

        // ⑩b 整块边界：恰好 1MB、单连接 → DirectSink 写合并整块边界
        server.Map("day10/boundary.bin", server.RangeFile(Payload(1024 * 1024, 0x60), etag: "\"boundary\""));
        var boundary = kernel.Add(new NewDownloadRequest(server.Base + "day10/boundary.bin", TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => boundary.Status == DownloadStatus.Completed, "整块边界任务应完成", 60_000);
        That(File.ReadAllBytes(Path.Combine(boundary.SavePath, "boundary.bin")).SequenceEqual(Payload(1024 * 1024, 0x60)),
            "整块边界任务字节精确");

        // ⑪ 校验清单与指纹判别：GNU/BSD 两种行格式 + 注释/空行/坏行/归一化
        var md5Hex = Convert.ToHexString(MD5.HashData(md5Data));
        var sha1Hex = Convert.ToHexString(SHA1.HashData(sha1Data));
        var sha256Hex = Convert.ToHexString(SHA256.HashData(primary));
        var manifest = Checksums.ParseChecksumFile(
            "# 注释行应跳过\n" +
            "\n" +
            $"{md5Hex} *ubuntu.iso\n" +
            $"{sha1Hex}  ./dir/report.pdf\n" +
            $"MD5 (tool\\bin\\setup.exe) = {md5Hex.ToLowerInvariant()}\n" +
            $"SHA256 (big.iso) = {sha256Hex.ToLowerInvariant()}\n" +
            $"SHA1 (nested.iso) = {sha1Hex.ToLowerInvariant()}\n" +
            "deadbeef badline.bin\n" +
            $"MD5 (././dotted.iso) = {md5Hex.ToLowerInvariant()}\n");
        Equal(6, manifest.Count, "6 条有效条目应入库，坏行跳过");
        var ubuntu = manifest.Single(e => e.FileName == "ubuntu.iso");
        Equal("MD5", ubuntu.Algorithm, "GNU 行 + 二进制标记应识别");
        Equal(md5Hex, ubuntu.Hash, "哈希字段不受 * 前缀影响");
        Equal("SHA-1", manifest.Single(e => e.FileName == "dir/report.pdf").Algorithm, "./ 前缀应归一");
        Equal("MD5", manifest.Single(e => e.FileName == "tool/bin/setup.exe").Algorithm, "BSD 反斜杠路径应归一");
        Equal("SHA-256", manifest.Single(e => e.FileName == "big.iso").Algorithm, "BSD SHA256 条目应识别");
        Equal("SHA-1", manifest.Single(e => e.FileName == "nested.iso").Algorithm, "BSD SHA1 条目应识别");
        That(manifest.All(e => e.Hash.All(char.IsAsciiHexDigit)), "无指纹的坏行不得入库");

        That(Checksums.DetectAlgorithm(null) is null, "null 应判别失败");
        That(Checksums.DetectAlgorithm(" ") is null, "空白应判别失败");
        That(Checksums.DetectAlgorithm("zzzz") is null, "非 hex 短串应判别失败");
        That(Checksums.DetectAlgorithm(new string('g', 32)) is null, "32 位非 hex 应判别失败");
        That(Checksums.DetectAlgorithm(new string('a', 48)) is null, "48 位长度应判别失败");
        Equal("MD5", Checksums.DetectAlgorithm(md5Hex), "32 位 hex → MD5");
        Equal("SHA-1", Checksums.DetectAlgorithm(sha1Hex), "40 位 hex → SHA-1");
        Equal("SHA-256", Checksums.DetectAlgorithm(sha256Hex), "64 位 hex → SHA-256");

        var probeFile = Path.Combine(TempDir(), "probe.bin");
        await File.WriteAllBytesAsync(probeFile, md5Data);
        var md5DataSha256 = Convert.ToHexString(SHA256.HashData(md5Data));
        Equal(md5Hex, Checksums.Compute("md5", probeFile), "md5 算法名（小写）");
        Equal(Convert.ToHexString(SHA1.HashData(md5Data)), Checksums.Compute("sha-1", probeFile), "sha-1 算法名");
        Equal(md5DataSha256, Checksums.Compute("sha-256", probeFile), "sha-256 算法名");
        Equal(md5DataSha256, Checksums.Compute("weird", probeFile), "未知算法按 SHA-256 兜底");

        // ⑫ 脱敏与兜底工具：边界输入一律返回安全值
        Equal("", SensitiveUrls.Mask(null), "null 链接应脱敏为空");
        Equal("", SensitiveUrls.Mask(""), "空链接应脱敏为空");
        Equal("magnet:", SensitiveUrls.Mask("magnet:"), "无查询串的 magnet 原样返回");
        Equal("magnet:?xt", SensitiveUrls.Mask("magnet:?xt"), "无值的参数不应崩溃");
        Equal("http://host/a?a=1", SensitiveUrls.Mask("http://host/a?a=1"), "无 % 的参数值原样返回");
        Equal("未命名下载", DriverHelpers.GuessName("#frag-only"), "仅片段名应退回默认名");
        Equal("a", DriverHelpers.GuessName("http://host/a#frag"), "片段应被剥掉");
        Equal("host", DriverHelpers.GuessName("http://host/"), "裸主机名应作为默认名");
        Equal("download", DriverHelpers.BaseName("http://host/.bin"), "纯扩展名应退回 download");
        Equal("http://abs.example/x", DriverHelpers.ResolveUrl("http://abs.example/x", "http://host/a/"), "绝对引用原样返回");
        Equal("http://host/a/rel.bin", DriverHelpers.ResolveUrl("rel.bin", "http://host/a/"), "相对引用应拼接");
        Equal("rel.bin", DriverHelpers.ResolveUrl("rel.bin", "::bad"), "坏基址应原样返回");
        Equal(DownloadProtocol.Http, ProtocolDetector.Detect("https://a/x"), "https 归入 http 驱动");
        Equal(DownloadProtocol.Http, ProtocolDetector.Detect(""), "空链接归入 http 驱动兜底");
        Equal(DownloadProtocol.Dash, ProtocolDetector.Detect("http://a/x.mpd"), "mpd 扩展名归入 DASH 驱动");
        Equal(DownloadProtocol.BitTorrent, ProtocolDetector.Detect("magnet:?x"), "magnet 归入 BT 驱动");
        Equal(DownloadProtocol.Ed2k, ProtocolDetector.Detect("ed2k://|file|x|1|h|/"), "ed2k 归入 eD2k 驱动");
        Equal(DownloadProtocol.Ftp, ProtocolDetector.Detect("ftp://host/x.iso"), "ftp 归入 FTP 驱动");
    }

    /// <summary>手工 bencode：{info:{name,length}, announce, announce-list}。</summary>
    private static byte[] TorrentBytes(string name, long length, params string[] announces)
    {
        static string Str(string s) => $"{Encoding.UTF8.GetByteCount(s)}:{s}";
        var info = $"d4:name{Str(name)}6:lengthi{length}ee";
        var list = "l" + string.Concat(announces.Select(a => $"l{Str(a)}e")) + "e";
        return Encoding.UTF8.GetBytes($"d4:info{info}8:announce{Str(announces[0])}13:announce-list{list}e");
    }

    // ── 基础设施 ──────────────────────────────────────────────────────────

    private static async Task Run(string name, Func<Task> body)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await body();
            Interlocked.Increment(ref _passed);
            Console.WriteLine($"[PASS] {name}（{watch.Elapsed.TotalSeconds:0.0}s）");
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref _failed);
            Failures.Add($"{name} :: {error.Message}");
            Console.WriteLine($"[FAIL] {name}（{watch.Elapsed.TotalSeconds:0.0}s）");
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string what, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"等待超时：{what}");
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nettrans-scenario-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>确定性载荷：同 size/seed 恒定，便于字节精确断言。</summary>
    private static byte[] Payload(int size, int seed = 1)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(i * 31 + seed * 7);
        return data;
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}：期望 {expected}，实际 {actual}");
    }

    private static void That(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }
}
