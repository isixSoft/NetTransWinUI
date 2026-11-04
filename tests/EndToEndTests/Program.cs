using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using EndToEndTests;
using NetTrans.Kernel;

namespace EndToEndTests;

/// <summary>
/// NetTrans.Kernel 端到端测试：走真实的 添加 → 探测 → 多协议传输 → SHA-256 校验
/// 全流程（本地 TestServer 提供源站），共 7 个方向：
///   1. HTTP 多分段：2 MiB 字节精确 + 96 块全亮 + SHA-256 日志 + checkpoint 清除
///   2. 断点续传（同一内核）：限速 → 暂停 → checkpoint → 恢复 → 字节精确
///   3. HLS 端到端：媒体播放列表 4 分段拼接，video.ts 命名
///   4. DASH 端到端：SegmentTemplate init + 4 分段，video.mp4 命名
///   5. 服务器不支持 Range：单流宽松回退
///   6. 优先级调度：高优先级插队 + 同优先级 FIFO
///   7. 模拟内核回归：种子任务 / 命令 / tick
///   8. 默认保存路径回退：SaveTo 为空 → DefaultSavePath + 分类子目录
///   9. 夜间不限速：OffPeak 窗口内全局限速被豁免
/// </summary>
internal static class Program
{
    private const int MiB = 1024 * 1024;

    private static int _passed, _failed;
    private static readonly List<string> _failures = new();
    private static readonly List<string> _roots = new();

    private static async Task<int> Main()
    {
        Console.WriteLine("NetTrans.Kernel 端到端测试");
        Console.WriteLine(new string('=', 64));

        using var server = new TestServer();
        Console.WriteLine($"测试服务器：{server.Base}");
        Console.WriteLine();

        await RunAsync("1. 多分段 2 MiB：字节精确 + 96 块全亮 + SHA-256 + checkpoint 清除", () => MultiSegmentAsync(server));
        await RunAsync("2. 断点续传（同一内核）：限速 → 暂停 → checkpoint → 恢复", () => ResumeSameKernelAsync(server));
        await RunAsync("3. HLS 端到端：4 分段拼接 + video.ts 命名", () => HlsEndToEndAsync(server));
        await RunAsync("4. DASH 端到端：SegmentTemplate init + 4 分段 + video.mp4 命名", () => DashEndToEndAsync(server));
        await RunAsync("5. 无 Range 服务器：单流宽松回退", () => NoRangeFallbackAsync(server));
        await RunAsync("6. 优先级调度：高优先级插队 + 同优先级 FIFO", () => PrioritySchedulingAsync(server));
        await RunAsync("7. 模拟内核回归：种子 8 任务 / 命令 / tick", () => SimulatedKernelAsync());
        await RunAsync("8. 默认保存路径回退：SaveTo 空 → DefaultSavePath + 分类子目录", () => DefaultSavePathAsync(server));
        await RunAsync("9. 夜间不限速：OffPeak 窗口内全局限速豁免", () => NightUncappedAsync(server));

        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"结果：{_passed} 通过，{_failed} 失败");
        foreach (var item in _failures) Console.WriteLine("  [FAIL] " + item);

        CleanupRoots();
        return _failed == 0 ? 0 : 1;
    }

    // ── 1. HTTP 多分段 ────────────────────────────────────────────────────

    private static async Task MultiSegmentAsync(TestServer server)
    {
        var data = Payload(2 * MiB);
        server.Map("e2e/multi.bin", server.RangeFile(data, etag: "\"e2e-multi-1\""));
        var dir = TempDir("multi");

        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 4, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "e2e/multi.bin", dir, "soft", 4, TaskPriority.Normal, StartNow: true));
        await WaitCompletedAsync(kernel, task, "多分段任务");

        var path = Path.Combine(dir, "multi.bin");
        That(task.Status == DownloadStatus.Completed, "任务状态应为 Completed");
        That(task.Log.Any(l => l.Message.Contains("HTTP 多分段就绪")), "日志应报告多分段计划（支持断点续传）");
        That(File.ReadAllBytes(path).AsSpan().SequenceEqual(data), "文件字节应与源站完全一致");
        That(task.Blocks.Length == 96 && task.Blocks.All(b => b == 1), "完成后 96 块应全部点亮");
        That(task.Size == data.Length && task.Done == task.Size, $"Size/Done 应到满（实际 {task.Done}/{task.Size}）");
        That(task.Log.Any(l => l.Message.StartsWith("SHA-256 ")), "日志应出现 SHA-256 校验行");
        That(!File.Exists(path + ".nettrans.json"), "完成后 checkpoint 应被清除");
    }

    // ── 2. 断点续传（同一内核） ───────────────────────────────────────────

    private static async Task ResumeSameKernelAsync(TestServer server)
    {
        var data = Payload(2 * MiB, seed: 7);
        server.Map("e2e/resume.bin", server.RangeFile(data, etag: "\"e2e-resume-1\""));
        var dir = TempDir("resume");

        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "e2e/resume.bin", dir, "soft", 2, TaskPriority.Normal, StartNow: false));
        kernel.SetSpeedLimit(task.Id, "256 KB/s");
        kernel.Toggle(task.Id); // 开始下载（限速）

        await WaitUntilAsync(() => task.Done >= 512 * 1024, TimeSpan.FromSeconds(30), "限速下载到 512 KB");
        kernel.Toggle(task.Id); // 暂停

        var checkpoint = Path.Combine(dir, "resume.bin.nettrans.json");
        await WaitUntilAsync(() => File.Exists(checkpoint), TimeSpan.FromSeconds(15), "暂停后 checkpoint 落盘");
        That(File.Exists(Path.Combine(dir, "resume.bin")), "暂停后目标文件（半截）应在盘");

        kernel.Toggle(task.Id); // 从断点恢复
        await WaitCompletedAsync(kernel, task, "恢复任务");

        That(task.Status == DownloadStatus.Completed, "恢复后任务应为 Completed");
        That(task.Log.Any(l => l.Message.Contains("已从断点恢复")), "日志应包含「已从断点恢复」");
        That(File.ReadAllBytes(Path.Combine(dir, "resume.bin")).AsSpan().SequenceEqual(data), "恢复后文件字节应完整");
        That(Directory.GetFiles(dir).Length == 1, "续传应写回原文件，不应产生防碰撞副本");
        That(!File.Exists(checkpoint), "完成后 checkpoint 应被清除");
    }

    // ── 3. HLS 端到端 ─────────────────────────────────────────────────────

    private static async Task HlsEndToEndAsync(TestServer server)
    {
        var segments = new[] { Payload(256 * 1024, 1), Payload(128 * 1024, 2), Payload(384 * 1024, 3), Payload(64 * 1024, 4) };
        var playlist = string.Join('\n',
            "#EXTM3U",
            "#EXT-X-TARGETDURATION:10",
            "#EXTINF:10,",
            "seg-1.ts",
            "#EXTINF:10,",
            "seg-2.ts",
            "#EXTINF:10,",
            "seg-3.ts",
            "#EXTINF:10,",
            "seg-4.ts",
            "#EXT-X-ENDLIST");
        server.Map("e2e/video.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(playlist), "application/vnd.apple.mpegurl"));
        for (int i = 1; i <= segments.Length; i++)
        {
            var segment = segments[i - 1];
            server.Map($"e2e/seg-{i}.ts", ctx => TestServer.Reply(ctx.Response, 200, segment, "video/mp2t"));
        }

        var dir = TempDir("hls");
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 4, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "e2e/video.m3u8", dir, "video", 4, TaskPriority.Normal, StartNow: true));
        await WaitCompletedAsync(kernel, task, "HLS 任务");

        var expected = segments.SelectMany(s => s).ToArray();
        var path = Path.Combine(dir, "video.ts");
        That(task.Name == "video.ts", $"HLS 应命名 video.ts（实际 {task.Name}）");
        That(task.Status == DownloadStatus.Completed, "任务状态应为 Completed");
        That(task.Log.Any(l => l.Message.Contains("分段协议就绪（4 个分段）")), "日志应报告 4 个分段");
        That(File.ReadAllBytes(path).AsSpan().SequenceEqual(expected), "HLS 分段应按顺序拼接且字节精确");
        That(task.Blocks.Length == 96 && task.Blocks.All(b => b == 1), "完成后 96 块应全部点亮");
        That(task.Done == task.Size && task.Size == expected.Length, $"Size/Done 应等于拼接长度（实际 {task.Done}/{task.Size}）");
    }

    // ── 4. DASH 端到端 ────────────────────────────────────────────────────

    private static async Task DashEndToEndAsync(TestServer server)
    {
        var init = Payload(64 * 1024, 11);
        var segments = new[] { Payload(128 * 1024, 12), Payload(128 * 1024, 13), Payload(128 * 1024, 14), Payload(128 * 1024, 15) };
        const string mpd = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT16S">
              <Period>
                <AdaptationSet contentType="video">
                  <Representation id="v1" bandwidth="800000">
                    <SegmentTemplate media="seg-$Number$.m4s" initialization="init.mp4" duration="4" startNumber="1" timescale="1"/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        server.Map("e2e-dash/video.mpd", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(mpd), "application/dash+xml"));
        server.Map("e2e-dash/init.mp4", ctx => TestServer.Reply(ctx.Response, 200, init, "video/mp4"));
        for (int i = 1; i <= segments.Length; i++)
        {
            var segment = segments[i - 1];
            server.Map($"e2e-dash/seg-{i}.m4s", ctx => TestServer.Reply(ctx.Response, 200, segment, "video/iso.segment"));
        }

        var dir = TempDir("dash");
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 4, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "e2e-dash/video.mpd", dir, "video", 4, TaskPriority.Normal, StartNow: true));
        await WaitCompletedAsync(kernel, task, "DASH 任务");

        var expected = new[] { init }.Concat(segments).SelectMany(s => s).ToArray();
        var path = Path.Combine(dir, "video.mp4");
        That(task.Name == "video.mp4", $"DASH 应命名 video.mp4（实际 {task.Name}）");
        That(task.Status == DownloadStatus.Completed, "任务状态应为 Completed");
        That(task.Log.Any(l => l.Message.Contains("分段协议就绪（5 个分段）")), "日志应报告 init + 4 分段共 5 个");
        That(File.ReadAllBytes(path).AsSpan().SequenceEqual(expected), "DASH 应按 init→seg1..4 拼接且字节精确");
    }

    // ── 5. 无 Range 服务器 ────────────────────────────────────────────────

    private static async Task NoRangeFallbackAsync(TestServer server)
    {
        var data = Payload(1 * MiB, seed: 21);
        server.Map("e2e/norange.bin", ctx =>
        {
            var response = ctx.Response;
            if (ctx.Request.HttpMethod == "HEAD")
            {
                response.StatusCode = 200;
                response.ContentLength64 = data.Length;
                response.Close();
                return Task.CompletedTask;
            }

            // 忽略 Range 头：一律 200 + 全量正文，且不带 Accept-Ranges
            response.StatusCode = 200;
            response.ContentLength64 = data.Length;
            response.OutputStream.Write(data);
            response.Close();
            return Task.CompletedTask;
        });

        var dir = TempDir("norange");
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 4, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "e2e/norange.bin", dir, "soft", 4, TaskPriority.Normal, StartNow: true));
        await WaitCompletedAsync(kernel, task, "无 Range 任务");

        That(task.Status == DownloadStatus.Completed, "任务状态应为 Completed");
        That(task.Log.Any(l => l.Message.Contains("HTTP 单流")), "日志应回退为「HTTP 单流（服务器不支持分段或大小未知）」");
        That(task.Size == data.Length, $"Size 应来自 HEAD（实际 {task.Size}）");
        That(File.ReadAllBytes(Path.Combine(dir, "norange.bin")).AsSpan().SequenceEqual(data), "单流下载字节应完整");
    }

    // ── 6. 优先级调度 ─────────────────────────────────────────────────────

    private static async Task PrioritySchedulingAsync(TestServer server)
    {
        var payload = Payload(2 * MiB, seed: 31);
        server.Map("e2e/prio/a.bin", server.RangeFile(payload, etag: "\"prio-a\""));
        server.Map("e2e/prio/b.bin", server.RangeFile(payload, etag: "\"prio-b\""));
        server.Map("e2e/prio/c.bin", server.RangeFile(payload, etag: "\"prio-c\""));
        server.Map("e2e/prio/d.bin", server.RangeFile(payload, etag: "\"prio-d\""));
        var dir = TempDir("prio");

        // 本地传输极快：给每个任务限速，把 Downloading/Queued 的状态窗口拉长到秒级，
        // 调度顺序才能被可靠观察（2 MiB @ 128 KB/s ≈ 16s，@ 1 MB/s ≈ 2s）。
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 });
        var a = kernel.Add(new NewDownloadRequest(server.Base + "e2e/prio/a.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        kernel.SetSpeedLimit(a.Id, "128 KB/s");
        await WaitUntilAsync(() => a.Status == DownloadStatus.Downloading && a.Done > 256 * 1024, TimeSpan.FromSeconds(30), "A 限速下载到中段", kernel);

        var b = kernel.Add(new NewDownloadRequest(server.Base + "e2e/prio/b.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        kernel.SetSpeedLimit(b.Id, "1 MB/s");
        var c = kernel.Add(new NewDownloadRequest(server.Base + "e2e/prio/c.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        kernel.SetSpeedLimit(c.Id, "1 MB/s");
        var d = kernel.Add(new NewDownloadRequest(server.Base + "e2e/prio/d.bin", dir, "", 2, TaskPriority.High, StartNow: true));
        kernel.SetSpeedLimit(d.Id, "1 MB/s");
        await WaitUntilAsync(() => b.Status == DownloadStatus.Queued && c.Status == DownloadStatus.Queued && d.Status == DownloadStatus.Queued,
            TimeSpan.FromSeconds(5), "B/C/D 在唯一槽位被占时排队", kernel);
        That(a.Status == DownloadStatus.Downloading, "A 应继续占用槽位");

        kernel.Toggle(a.Id); // 暂停 A → 槽位释放
        await WaitUntilAsync(() => d.Status == DownloadStatus.Downloading, TimeSpan.FromSeconds(15), "高优先级 D 插队开始", kernel);
        That(a.Status == DownloadStatus.Paused, "A 应处于暂停");
        That(b.Status == DownloadStatus.Queued && c.Status == DownloadStatus.Queued, "Normal 的 B/C 仍应排队");

        kernel.Toggle(a.Id); // A 恢复 → 重新排队（Normal、先加入）
        await WaitUntilAsync(() => a.Status == DownloadStatus.Queued, TimeSpan.FromSeconds(5), "A 恢复后进入队列", kernel);

        kernel.Toggle(d.Id); // 暂停 D → 槽位再次释放
        await WaitUntilAsync(() => a.Status == DownloadStatus.Downloading, TimeSpan.FromSeconds(15), "同优先级 FIFO：A 先于 B/C", kernel);
        That(b.Status == DownloadStatus.Queued && c.Status == DownloadStatus.Queued, "B/C 仍应排队");
    }

    // ── 7. 模拟内核回归 ───────────────────────────────────────────────────

    private static async Task SimulatedKernelAsync()
    {
        int ticks = 0;
        using var sim = new SimulatedDownloadKernel();
        sim.Ticked += _ => Interlocked.Increment(ref ticks);

        var tasks = sim.Tasks;
        That(tasks.Count == 8, $"种子应为 8 个任务（实际 {tasks.Count}）");
        That(tasks.Select(t => t.Id).OrderBy(id => id).SequenceEqual(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "种子任务 id 应为 1..8");
        That(tasks.Count(t => t.Status == DownloadStatus.Downloading) == 3
            && tasks.Count(t => t.Status == DownloadStatus.Completed) == 2
            && tasks.Any(t => t.Status == DownloadStatus.Paused)
            && tasks.Any(t => t.Status == DownloadStatus.Error)
            && tasks.Any(t => t.Status == DownloadStatus.Queued),
            "种子状态分布应为 3 下载 / 2 完成 / 1 暂停 / 1 失败 / 1 排队");
        That(tasks.First(t => t.Id == 2).Blocks.All(b => b == 1), "已完成种子任务的 96 块应全亮");

        var t1 = tasks.First(t => t.Id == 1);
        sim.Toggle(1);
        That(t1.Status == DownloadStatus.Paused && t1.Speed == 0, "Toggle(1) 应暂停下载中的任务");

        var t4 = tasks.First(t => t.Id == 4);
        sim.Toggle(4);
        That(t4.Status == DownloadStatus.Downloading, "Toggle(4) 应恢复暂停的任务");

        sim.SetPriority(8, TaskPriority.High);
        That(tasks.First(t => t.Id == 8).Priority == TaskPriority.High, "SetPriority 应生效");

        sim.SetSpeedLimit(5, "1 MB/s");
        That(tasks.First(t => t.Id == 5).SpeedLimit == "1 MB/s", "SetSpeedLimit 应生效");

        sim.MoveToFront(8);
        That(sim.Tasks[0].Id == 8, "MoveToFront 应把任务 8 移到队首");
        sim.MoveToBack(1);
        That(sim.Tasks[^1].Id == 1, "MoveToBack 应把任务 1 移到队尾");

        long done3 = tasks.First(t => t.Id == 3).Done;
        await WaitUntilAsync(() => Volatile.Read(ref ticks) >= 3, TimeSpan.FromSeconds(10), "模拟内核 tick 至少 3 次");
        That(tasks.First(t => t.Id == 3).Done > done3, "tick 应推进下载中的任务进度");

        sim.ToggleAll();
        That(sim.Tasks.Where(t => t.Status != DownloadStatus.Completed).All(t => t.Status == DownloadStatus.Paused),
            "第一次 ToggleAll 应全部暂停（完成项除外）");
        sim.ToggleAll();
        That(sim.Tasks.Where(t => t.Status != DownloadStatus.Completed).All(t => t.Status == DownloadStatus.Downloading),
            "第二次 ToggleAll 应全部恢复");
        That(sim.Snapshot.IsRunning, "有任务下载时快照 IsRunning 应为 true");
    }

    // ── 8. 默认保存路径回退 ───────────────────────────────────────────────

    private static async Task DefaultSavePathAsync(TestServer server)
    {
        var soft = Payload(512 * 1024, seed: 41);
        var video = Payload(512 * 1024, seed: 42);
        server.Map("e2e/default/notes.bin", server.RangeFile(soft, etag: "\"e2e-default-1\""));
        server.Map("e2e/default/clip.mp4", server.RangeFile(video, etag: "\"e2e-default-2\""));

        // SaveTo 传空：内核必须回退 DefaultSavePath；「分类文件夹」开启时
        // 再追加分类子目录（.bin → doc，显式 "video" → video）。
        var root = TempDir("default-root");
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 2,
            MaxSimultaneous = 2,
            DefaultSavePath = root,
            FoldersByCategory = true,
        });

        var docTask = kernel.Add(new NewDownloadRequest(server.Base + "e2e/default/notes.bin", "", "", 2, TaskPriority.Normal, StartNow: true));
        var videoTask = kernel.Add(new NewDownloadRequest(server.Base + "e2e/default/clip.mp4", "", "video", 2, TaskPriority.Normal, StartNow: true));
        await WaitCompletedAsync(kernel, docTask, "默认路径 doc 任务");
        await WaitCompletedAsync(kernel, videoTask, "默认路径 video 任务");

        That(docTask.Status == DownloadStatus.Completed && videoTask.Status == DownloadStatus.Completed,
            "两个默认路径任务都应 Completed");
        var docPath = Path.Combine(root, "doc", "notes.bin");
        var videoPath = Path.Combine(root, "video", "clip.mp4");
        That(File.Exists(docPath), "doc 任务应落盘 DefaultSavePath/doc/notes.bin（分类子目录自动创建）");
        That(File.Exists(videoPath), "显式分类 video 应落盘 DefaultSavePath/video/clip.mp4");
        That(File.ReadAllBytes(docPath).AsSpan().SequenceEqual(soft), "doc 任务文件字节应精确");
        That(File.ReadAllBytes(videoPath).AsSpan().SequenceEqual(video), "video 任务文件字节应精确");

        // 关闭分类文件夹：同样的空 SaveTo 应直接落在 DefaultSavePath 根
        var flatRoot = TempDir("default-flat");
        using var flatKernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 2,
            MaxSimultaneous = 1,
            DefaultSavePath = flatRoot,
            FoldersByCategory = false,
        });
        var flatTask = flatKernel.Add(new NewDownloadRequest(server.Base + "e2e/default/notes.bin", "", "", 2, TaskPriority.Normal, StartNow: true));
        await WaitCompletedAsync(flatKernel, flatTask, "默认平铺路径任务");
        var flatPath = Path.Combine(flatRoot, "notes.bin");
        That(flatTask.Status == DownloadStatus.Completed && File.Exists(flatPath),
            "FoldersByCategory=false 时应直接落盘 DefaultSavePath/notes.bin");
        That(File.ReadAllBytes(flatPath).AsSpan().SequenceEqual(soft), "平铺路径文件字节应精确");
    }

    // ── 9. 夜间不限速 ─────────────────────────────────────────────────────

    private static async Task NightUncappedAsync(TestServer server)
    {
        var data = Payload(1 * MiB, seed: 51);
        server.Map("e2e/night.bin", server.RangeFile(data, etag: "\"e2e-night-1\""));
        var dir = TempDir("night");

        // 全局限速 128 KB/s，但夜间豁免窗口覆盖「现在」前后各 1 小时
        //（跨午夜场景也成立）：1 MiB 若仍被限速需 ~8s，豁免后应在远短于此
        // 的时间内完成。
        var options = new KernelOptions
        {
            ConnectionsPerTask = 4,
            MaxSimultaneous = 1,
            GlobalSpeedLimit = 128 * 1024,
            UncappedAtNight = true,
            OffPeakStart = DateTime.Now.AddHours(-1).ToString("HH:mm"),
            OffPeakEnd = DateTime.Now.AddHours(+1).ToString("HH:mm"),
        };
        That(RealDownloadKernel.InOffPeakWindow(DateTime.Now.TimeOfDay, options.OffPeakStart, options.OffPeakEnd),
            "测试自检：当前时刻应落在动态 OffPeak 窗口内");

        using var kernel = new RealDownloadKernel(options);
        var task = kernel.Add(new NewDownloadRequest(server.Base + "e2e/night.bin", dir, "soft", 4, TaskPriority.Normal, StartNow: true));
        var clock = Stopwatch.StartNew();
        await WaitCompletedAsync(kernel, task, "夜间豁免任务");
        clock.Stop();

        That(task.Status == DownloadStatus.Completed, "任务状态应为 Completed");
        That(File.ReadAllBytes(Path.Combine(dir, "night.bin")).AsSpan().SequenceEqual(data), "文件字节应精确");
        That(clock.Elapsed < TimeSpan.FromSeconds(5),
            $"夜间窗口内 1 MiB 应远快于 128 KB/s 限速（实际 {clock.Elapsed.TotalSeconds:0.0}s，未豁免需 ≥ 8s）");
    }

    // ── 基础设施 ──────────────────────────────────────────────────────────

    private static async Task RunAsync(string name, Func<Task> body)
    {
        try
        {
            await body();
            Interlocked.Increment(ref _passed);
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref _failed);
            _failures.Add($"{name} :: {error.Message}");
            Console.WriteLine($"[FAIL] {name}");
            Console.WriteLine($"       {error.Message}");
        }
    }

    private static void That(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }

    /// <summary>Deterministic payload; different seeds produce different bytes.</summary>
    private static byte[] Payload(int size, byte seed = 0)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)((i * 31 + seed) % 251);
        return data;
    }

    private static string TempDir(string label)
    {
        var root = Path.Combine(Path.GetTempPath(), "nettrans-e2e-" + Guid.NewGuid().ToString("N")[..10]);
        _roots.Add(root);
        var dir = Path.Combine(root, label);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupRoots()
    {
        foreach (var root in _roots)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                    break;
                }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { break; }
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what, RealDownloadKernel? kernel = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        if (kernel is not null) DumpKernel(kernel);
        throw new Exception($"等待超时（{timeout.TotalSeconds:0}s）：{what}");
    }

    private static void DumpKernel(RealDownloadKernel kernel)
    {
        foreach (var task in kernel.Tasks)
        {
            Console.WriteLine($"  ---- task #{task.Id} {task.Name} 状态 {task.Status} Done={task.Done} ----");
            foreach (var entry in task.Log) Console.WriteLine($"  [{entry.Time}] {entry.Message}");
        }
    }

    private static async Task WaitCompletedAsync(RealDownloadKernel kernel, DownloadTask task, string label)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var current = kernel.Tasks.FirstOrDefault(t => t.Id == task.Id);
            if (current is { Status: DownloadStatus.Completed }) return;
            await Task.Delay(100);
        }

        var stuck = kernel.Tasks.FirstOrDefault(t => t.Id == task.Id);
        Console.WriteLine($"  ---- task #{task.Id} 日志（状态 {stuck?.Status}）----");
        foreach (var entry in stuck?.Log ?? Enumerable.Empty<LogEntry>()) Console.WriteLine($"  [{entry.Time}] {entry.Message}");
        throw new Exception($"等待超时：{label} 未完成");
    }
}
