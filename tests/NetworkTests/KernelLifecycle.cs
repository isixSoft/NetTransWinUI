using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using NetTrans.Kernel;
using NetTrans.Kernel.Net;
using NetTrans.Kernel.Protocol;
using NetTrans.Kernel.Protocol.Drivers;
using NetTrans.Kernel.Transfer;
namespace NetworkTests;

internal sealed partial class Program
{
    // ── 9. Remove 中途取消 ────────────────────────────────────────────────

    private static async Task RemoveMidwayAsync(TestServer server)
    {
        var data = Payload(8 * 1024 * 1024);
        server.Map("cancel/slow.bin", server.RangeFile(data));
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 4, MaxSimultaneous = 1 });
        var removed = NewSignal();
        kernel.TaskRemoved += _ => removed.TrySetResult();

        var task = kernel.Add(new NewDownloadRequest(server.Base + "cancel/slow.bin", dir, "", 4, TaskPriority.Normal, StartNow: true));
        kernel.SetSpeedLimit(task.Id, "128 KB/s"); // 拖慢下载，保证中途取消窗口
        await WaitAsync(() => task.Done > 0, "取消前下载应有进度", 15_000);

        kernel.Remove(new[] { task.Id });
        var finished = await Task.WhenAny(removed.Task, Task.Delay(5_000));
        That(finished == removed.Task, "Remove 应触发 TaskRemoved 事件");
        That(!kernel.Tasks.Any(t => t.Id == task.Id), "任务应从任务列表移除");

        // 句柄释放判据：目标文件最终可被再次删除（或已被内核清理）
        var path = TargetPath(dir, task);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var released = false;
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(path)) { released = true; break; }
            try { File.Delete(path); released = true; break; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(100);
        }
        That(released, "下载中 Remove 后文件句柄应释放（File.Delete 成功或文件已被清理）");
    }

    // ── 10. Redownload ────────────────────────────────────────────────────

    private static async Task RedownloadAsync(TestServer server)
    {
        var data = Payload(256 * 1024);
        server.Map("redo/data.bin", server.RangeFile(data));
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 });
        int completions = 0;
        kernel.TaskCompleted += _ => Interlocked.Increment(ref completions);

        var task = kernel.Add(new NewDownloadRequest(server.Base + "redo/data.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "第一次下载应完成", 60_000);
        var path = TargetPath(dir, task);
        Equal(Hash(data), Hash(File.ReadAllBytes(path)), "第一次下载字节应正确");

        kernel.Redownload(task.Id);
        Equal(0L, task.Done, "Redownload 后 Done 应归零");
        That(task.Status is DownloadStatus.Queued or DownloadStatus.Downloading,
            $"Redownload 后应重新排队，实际 {task.Status}");
        await WaitAsync(() => task.Status == DownloadStatus.Completed && Volatile.Read(ref completions) >= 2,
            "重新下载应再次完成", 60_000);

        Equal(task.Size, task.Done, "重新下载完成后 Done 应再次到满");
        Equal(Hash(data), Hash(File.ReadAllBytes(path)), "重新下载字节应正确");
        That(Volatile.Read(ref completions) >= 2, "TaskCompleted 应触发两次");
        That(task.Log.Any(l => l.Message.Contains("重新开始")), "日志应出现\"重新开始\"（按新版本重新开始下载）");
    }

    // ── 11. 暂停/恢复边界 ─────────────────────────────────────────────────

    private static async Task PauseResumeEdgesAsync(TestServer server)
    {
        var data = Payload(4 * 1024 * 1024);
        server.Map("pause/edge.bin", server.RangeFile(data, etag: "\"pause-edge-1\""));
        var dir = TempDir();

        // 11a：暂停打在探测/启动的任意时刻也要安全——StartNow:true 后立刻暂停，
        // 此时 worker 可能还在探测，也可能刚开始传。
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "pause/edge.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        kernel.Toggle(task.Id); // 立刻暂停
        Equal(DownloadStatus.Paused, task.Status, "启动后立刻暂停：状态应为 Paused");
        That(task.Speed == 0, "暂停后速度应归零");

        kernel.Toggle(task.Id); // 恢复
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "探测期暂停过的任务恢复后应完成", 60_000);
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(dir, task))), "探测期暂停/恢复后字节应完整");
        That(!task.Log.Any(l => l.IsError), $"探测期暂停不应产生失败日志：{string.Join(" | ", task.Log.Where(l => l.IsError).Select(l => l.Message))}");
        That(!File.Exists(TargetPath(dir, task) + ".nettrans.json"), "完成后 checkpoint 应清除");
    }

    // ── 12. AddTask 坏 URL ────────────────────────────────────────────────

    private static async Task BadUrlAsync(TestServer server)
    {
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 4 });
        var good = Payload(64 * 1024);
        server.Map("badurl/good.bin", server.RangeFile(good, etag: "\"badurl-good\""));
        var goodDir = TempDir();

        // 各种坏 URL：任务必须优雅进入 Error（带失败信息），绝不崩内核或挂死
        foreach (var url in new[] { "not-a-url", "", "http://", "http://nonexistent.invalid/x.bin", "ed2k://|file|name|abc|HASH|/" })
        {
            var task = kernel.Add(new NewDownloadRequest(url, TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status is DownloadStatus.Error or DownloadStatus.Completed,
                $"坏 URL 「{(url.Length == 0 ? "<空>" : url)}」应到达终态", 30_000);
            Equal(DownloadStatus.Error, task.Status, $"坏 URL 「{url}」应为 Error");
            That(!string.IsNullOrWhiteSpace(task.ErrorMessage), $"坏 URL 「{url}」应带错误信息");
            That(task.Log.Any(l => l.IsError), $"坏 URL 「{url}」日志应有失败行");
        }

        // 坏 URL 风暴之后内核必须仍然健康：好任务照常完成
        var healthy = kernel.Add(new NewDownloadRequest(server.Base + "badurl/good.bin", goodDir, "", 1, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => healthy.Status == DownloadStatus.Completed, "坏 URL 之后内核应照常工作", 30_000);
        Equal(Hash(good), Hash(File.ReadAllBytes(TargetPath(goodDir, healthy))), "好任务字节应完整");
    }

    // ── 13. Remove 竞态风暴 ───────────────────────────────────────────────

    private static async Task RemoveRaceStormAsync(TestServer server)
    {
        var data = Payload(8 * 1024 * 1024);
        server.Map("race/storm.bin", server.RangeFile(data, etag: "\"race-1\""));
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 });

        var task = kernel.Add(new NewDownloadRequest(server.Base + "race/storm.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        kernel.SetSpeedLimit(task.Id, "128 KB/s");
        await WaitAsync(() => task.Done > 0, "竞态前下载应有进度", 30_000);

        // 对同一任务狂轰乱炸：Remove 之后立刻重建，反复制造 Add/Remove/Toggle/Redownload
        // 的真实重叠窗口——全部操作都在内核锁内，任何一路都不允许抛异常或把内核打挂
        var storm = Task.Run(() =>
        {
            for (int i = 0; i < 60; i++)
            {
                if (!kernel.Tasks.Any(t => t.Id == task.Id))
                {
                    task = kernel.Add(new NewDownloadRequest(server.Base + "race/storm.bin", TempDir(), "", 2, TaskPriority.Normal, StartNow: true));
                    kernel.SetSpeedLimit(task.Id, "128 KB/s");
                    continue;
                }

                kernel.Remove(new[] { task.Id });          // 幂等：不存在即跳过
                kernel.Toggle(task.Id);                    // 不存在即跳过
                if (i % 10 == 0) kernel.Redownload(task.Id);
                kernel.SetPriority(task.Id, TaskPriority.High);
                kernel.SetSpeedLimit(task.Id, i % 2 == 0 ? "不限" : "256 KB/s");
                kernel.MoveToFront(task.Id);
                kernel.MoveToBack(task.Id);
            }
        });
        kernel.ToggleAll();
        // 所有等待都是上限而非预期耗时（成功即提前返回）；慢沙箱 / TCP TIME_WAIT
        // 积压时给足余量，避免与被测内核无关的环境抖动造成偶发超时。
        await storm.WaitAsync(TimeSpan.FromSeconds(120));
        kernel.Remove(kernel.Tasks.Select(t => t.Id).ToArray()); // 双重清理 + 覆盖未知 id

        That(!kernel.Tasks.Any(t => t.Id == task.Id), "风暴后任务应不在任务列表");
        await WaitAsync(() => !kernel.Tasks.Any(), "风暴后任务列表应清空", 30_000);

        // 内核必须仍然健康：加一个新任务并完成
        var healthyDir = TempDir();
        var healthy = kernel.Add(new NewDownloadRequest(server.Base + "race/storm.bin", healthyDir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => healthy.Status == DownloadStatus.Completed, "竞态风暴后内核应照常完成新任务", 180_000);
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(healthyDir, healthy))), "风暴后新任务字节应完整");

        // 从未启动的排队任务：Remove 干净利落、不产生文件
        var queued = kernel.Add(new NewDownloadRequest(server.Base + "race/storm.bin", TempDir(), "", 2, TaskPriority.Normal, StartNow: false));
        kernel.Remove(new[] { queued.Id });
        That(!kernel.Tasks.Any(t => t.Id == queued.Id), "未启动任务应直接移除");
    }

    // ── 14. Range 续传边界 ────────────────────────────────────────────────

    private static async Task RangeResumeHeaderAsync(TestServer server)
    {
        // 断点续传是大文件直写（DirectSink）语义：载荷必须超过小文件内存缓冲阈值（默认 4MB）
        var data = Payload(6 * 1024 * 1024);
        var rangeHeaders = new List<string>();
        var gate = new object();
        server.Map("range/resume.bin", server.RangeFile(data, etag: "\"range-resume-1\"", onGet: (_, ctx) =>
        {
            var range = ctx.Request.Headers["Range"];
            if (range is not null) lock (gate) rangeHeaders.Add(range);
            return false;
        }));
        var dir = TempDir();

        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "range/resume.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        kernel.SetSpeedLimit(task.Id, "256 KB/s");
        await WaitAsync(() => task.Done >= 512 * 1024, "限速下载到 512 KB", 30_000);
        kernel.Toggle(task.Id); // 暂停 → checkpoint
        await WaitAsync(() => File.Exists(TargetPath(dir, task) + ".nettrans.json"), "暂停后 checkpoint 应落盘", 15_000);

        kernel.Toggle(task.Id); // 恢复 → 续传 GET 必须带中段 Range
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "恢复后任务应完成", 60_000);
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(dir, task))), "续传后字节应完整");

        lock (gate)
        {
            That(rangeHeaders.Any(h => h.StartsWith("bytes=") && !h.StartsWith("bytes=0-")),
                $"恢复后应发出非零起点的 Range 续传头，实际捕获：{string.Join(" ; ", rangeHeaders.Take(8))}");
        }
    }

    private static async Task RangeBetrayalAsync(TestServer server)
    {
        // 探测时宣称支持 Range（HEAD 带 Accept-Ranges），GET 却无视 Range
        // 回 200 全量：多分段会话必须明确失败（不静默把 200 正文写错位置）
        var data = Payload(4 * 1024 * 1024);
        server.Map("range/betray.bin", server.RangeFile(data, etag: "\"betray-1\"", onGet: (_, ctx) =>
        {
            if (ctx.Request.Headers["Range"] is null) return false; // 首次 GET 正常放行
            ctx.Response.StatusCode = 200;                          // 续传 GET：背叛 Range
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data);
            ctx.Response.Close();
            return true;
        }));
        var dir = TempDir();

        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1, RetryCount = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "range/betray.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => task.Status is DownloadStatus.Error or DownloadStatus.Completed,
            "Range 背叛任务应到达终态", 60_000);
        Equal(DownloadStatus.Error, task.Status, "多分段下服务器背叛 Range 应明确 Error");
        That(task.ErrorMessage!.Contains("断点续传"), $"错误信息应提及断点续传，实际「{task.ErrorMessage}」");
        That(task.Log.Any(l => l.Message.Contains("服务器不支持断点续传")), "日志应记录「服务器不支持断点续传（Range 被忽略）」");
    }

    // ── 22. 完成后动作（P0-4：任务级 + 全局「全部完成」收尾，runner 注入） ──
    // 内核不真关机：动作语义由宿主经 KernelOptions.CompletionRunner 注入，
    // 测试收集 CompletionEvent 断言「恰一次」与字段；校验失败（Error）不触发。

    private static async Task CompletionActionTaskAsync(TestServer server)
    {
        var data = Payload(64 * 1024);
        server.Map("done/a.zip", server.RangeFile(data));
        server.Map("done/b.zip", server.RangeFile(data));

        var events = new ConcurrentQueue<CompletionEvent>();
        var options = new KernelOptions
        {
            MaxSimultaneous = 1,
            CompletionRunner = (e, _) => { events.Enqueue(e); return Task.CompletedTask; },
        };
        using var kernel = new RealDownloadKernel(options);
        var dir = TempDir();
        var a = kernel.Add(new NewDownloadRequest(server.Base + "done/a.zip", dir, "", 2, TaskPriority.Normal,
            StartNow: true, OnComplete: AfterCompletion.Notify));
        var b = kernel.Add(new NewDownloadRequest(server.Base + "done/b.zip", dir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => a.Status == DownloadStatus.Completed && b.Status == DownloadStatus.Completed,
            "两个任务都应完成", 60_000);
        await WaitAsync(() => events.Count == 1, "任务级动作应恰好触发一次", 10_000);
        await Task.Delay(300);
        Equal(1, events.Count, "只有配置了动作的 A 触发，且不重复");
        var completed = events.Single();
        Equal(AfterCompletion.Notify, completed.Action, "动作种类应来自任务配置");
        Equal(false, completed.FromAllCompleted, "任务级事件不是全完成事件");
        Equal(a.Name, completed.TaskName, "事件应携带任务名");
        That(completed.SavePath is not null && File.Exists(completed.SavePath), "事件应携带已落盘的产物路径");
    }

    private static async Task CompletionActionAllAsync(TestServer server)
    {
        var data = Payload(64 * 1024);
        var stillBusy = new ManualResetEventSlim(false);
        server.Map("done/g1.zip", server.RangeFile(data));
        server.Map("done/g2.zip", server.RangeFile(data, onGet: (_, _) =>
        {
            stillBusy.Wait(15_000); // 门闩：钉住 g2 在下载中，前半段断言才确定
            return false;
        }));

        var events = new ConcurrentQueue<CompletionEvent>();
        var options = new KernelOptions
        {
            MaxSimultaneous = 2,
            AllComplete = AfterCompletion.Shutdown,
            CompletionRunner = (e, _) => { events.Enqueue(e); return Task.CompletedTask; },
        };
        using var kernel = new RealDownloadKernel(options);
        var dir = TempDir();
        var g1 = kernel.Add(new NewDownloadRequest(server.Base + "done/g1.zip", dir, "", 2, TaskPriority.Normal, StartNow: true));
        var g2 = kernel.Add(new NewDownloadRequest(server.Base + "done/g2.zip", dir, "", 2, TaskPriority.Normal, StartNow: true));

        // g1 完成时 g2 仍在下载（门闩保证）→ 不应收尾
        await WaitAsync(() => g1.Status == DownloadStatus.Completed, "g1 应完成", 60_000);
        That(g2.Status == DownloadStatus.Downloading, "前置：g2 应仍处下载中（门闩）");
        That(events.Count == 0, "还有下载中任务时不应触发全局动作");

        // 放行 g2 → 全部完成 → 恰好触发一次
        stillBusy.Set();
        await WaitAsync(() => g2.Status == DownloadStatus.Completed, "g2 应完成", 60_000);
        await WaitAsync(() => events.Count == 1, "全部完成后应恰好触发一次", 10_000);
        await Task.Delay(300);
        Equal(1, events.Count, "全局动作不随单个任务触发，收尾恰一次");
        var completed = events.Single();
        Equal(AfterCompletion.Shutdown, completed.Action, "动作种类应来自全局配置");
        Equal(true, completed.FromAllCompleted, "应为「全部完成」事件");
        Equal(g2.Name, completed.TaskName, "事件应携带收尾完成的任务名");
    }

    private static async Task CompletionActionErrorAsync(TestServer server)
    {
        var data = Payload(64 * 1024);
        server.Map("done/c1.zip", server.RangeFile(data));
        server.Map("done/c2.zip", server.RangeFile(data));

        var events = new ConcurrentQueue<CompletionEvent>();
        var options = new KernelOptions
        {
            CompletionRunner = (e, _) => { events.Enqueue(e); throw new InvalidOperationException("动作炸了"); },
        };
        using var kernel = new RealDownloadKernel(options);
        var dir = TempDir();
        var c1 = kernel.Add(new NewDownloadRequest(server.Base + "done/c1.zip", dir, "", 2, TaskPriority.Normal,
            StartNow: true, OnComplete: AfterCompletion.RunCommand, OnCompleteCommand: "{name} <- {path}"));
        await WaitAsync(() => c1.Status == DownloadStatus.Completed, "runner 抛异常不应影响任务完成", 60_000);
        await WaitAsync(() => events.Count == 1, "异常 runner 仍应被调用一次", 10_000);
        var completed = events.Single();
        Equal(AfterCompletion.RunCommand, completed.Action, "RunCommand 动作应原样触发");
        That(completed.Command == $"{c1.Name} <- {completed.SavePath}",
            $"RunCommand 模板应展开 {{name}}/{{path}}：{completed.Command}");
        That(LogContains(c1, "完成后动作执行失败"), "runner 异常应记入任务日志");

        // 内核仍可用：第二个任务（无动作）正常完成，且不再触发
        var c2 = kernel.Add(new NewDownloadRequest(server.Base + "done/c2.zip", dir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => c2.Status == DownloadStatus.Completed, "内核应继续正常工作", 60_000);
        await Task.Delay(300);
        Equal(1, events.Count, "c2 无动作，不应再触发");
    }

    // ── 23. 队列分组与定时启停（P0-3：可注入时钟，跨午夜窗口） ───────────

    private static async Task QueueScheduleStartAsync(TestServer server)
    {
        var data = Payload(64 * 1024);
        server.Map("queue/a1.zip", server.RangeFile(data));
        server.Map("queue/a2.zip", server.RangeFile(data));
        server.Map("queue/a3.zip", server.RangeFile(data));
        server.Map("queue/out.zip", server.RangeFile(data));

        var clock = new DateTime(2026, 11, 5, 7, 59, 0);
        var options = new KernelOptions { MaxSimultaneous = 2, Clock = () => clock };
        using var kernel = new RealDownloadKernel(options);
        var dir = TempDir();
        kernel.SetQueueSchedule("夜间队", start: "08:00", stop: null);
        var a1 = kernel.Add(new NewDownloadRequest(server.Base + "queue/a1.zip", dir, "", 2, TaskPriority.Normal, StartNow: false, Queue: "夜间队"));
        var a2 = kernel.Add(new NewDownloadRequest(server.Base + "queue/a2.zip", dir, "", 2, TaskPriority.Normal, StartNow: false, Queue: "夜间队"));
        var a3 = kernel.Add(new NewDownloadRequest(server.Base + "queue/a3.zip", dir, "", 2, TaskPriority.Normal, StartNow: false, Queue: "夜间队"));
        var outside = kernel.Add(new NewDownloadRequest(server.Base + "queue/out.zip", dir, "", 2, TaskPriority.Normal, StartNow: false));

        kernel.EvaluateQueueSchedules(); // 07:59 未到点
        That(new[] { a1, a2, a3, outside }.All(t => t.Status == DownloadStatus.Paused), "未到点不应启动任何任务");

        clock = new DateTime(2026, 11, 5, 8, 0, 0);
        kernel.EvaluateQueueSchedules(); // 到点：A 内批量开始，并发槽语义不变
        await WaitAsync(() => a1.Status == DownloadStatus.Downloading && a2.Status == DownloadStatus.Downloading,
            "A 内任务应批量进入运行态", 30_000);
        Equal(DownloadStatus.Queued, a3.Status, "第三个任务按并发槽排队（语义不变）");
        Equal(DownloadStatus.Paused, outside.Status, "队列外任务不受调度影响");
        That(LogContains(a1, "批量开始"), "应有批量开始日志");
    }

    private static async Task QueueScheduleMidnightAsync(TestServer server)
    {
        var data = Payload(64 * 1024);
        var gateA = new ManualResetEventSlim(false);
        var gateOut = new ManualResetEventSlim(false);
        server.Map("mid/a1.zip", server.RangeFile(data, onGet: (_, _) => { gateA.Wait(20_000); return false; }));
        server.Map("mid/a2.zip", server.RangeFile(data));
        server.Map("mid/out.zip", server.RangeFile(data, onGet: (_, _) => { gateOut.Wait(20_000); return false; }));

        var clock = new DateTime(2026, 11, 5, 22, 59, 0);
        var options = new KernelOptions { MaxSimultaneous = 4, Clock = () => clock };
        using var kernel = new RealDownloadKernel(options);
        var dir = TempDir();
        kernel.SetQueueSchedule("跨夜队", start: "23:00", stop: "07:00"); // 跨午夜窗口 [23:00, 07:00)
        var a1 = kernel.Add(new NewDownloadRequest(server.Base + "mid/a1.zip", dir, "", 2, TaskPriority.Normal, StartNow: false, Queue: "跨夜队"));
        var a2 = kernel.Add(new NewDownloadRequest(server.Base + "mid/a2.zip", dir, "", 2, TaskPriority.Normal, StartNow: false, Queue: "跨夜队"));
        var outside = kernel.Add(new NewDownloadRequest(server.Base + "mid/out.zip", dir, "", 2, TaskPriority.Normal, StartNow: true));

        // 22:59：窗口外（22:59 ≥ 07:00 且 < 23:00）→ 队列任务保持暂停；队列外任务照常下载
        kernel.EvaluateQueueSchedules();
        That(a1.Status == DownloadStatus.Paused && a2.Status == DownloadStatus.Paused, "22:59 在窗口外不应启动");
        await WaitAsync(() => outside.Status == DownloadStatus.Downloading, "队列外任务应照常下载", 30_000);

        // 23:00：入窗 → 队列任务批量开始
        clock = new DateTime(2026, 11, 5, 23, 0, 0);
        kernel.EvaluateQueueSchedules();
        await WaitAsync(() => a1.Status == DownloadStatus.Downloading, "23:00 入窗应启动（门闩钉住）", 30_000);
        That(a2.Status != DownloadStatus.Paused, "a2 也应被批量开始（可能已完成）");

        // 06:59：仍在窗口内（跨午夜）→ 不打断
        clock = new DateTime(2026, 11, 6, 6, 59, 0);
        kernel.EvaluateQueueSchedules();
        Equal(DownloadStatus.Downloading, a1.Status, "06:59 仍在窗口内不应暂停");

        // 07:00：出窗 → 队列任务批量暂停；队列外下载中的任务不受影响
        clock = new DateTime(2026, 11, 6, 7, 0, 0);
        kernel.EvaluateQueueSchedules();
        Equal(DownloadStatus.Paused, a1.Status, "07:00 出窗应批量暂停");
        That(LogContains(a1, "批量暂停"), "应有批量暂停日志");
        Equal(DownloadStatus.Downloading, outside.Status, "队列外下载中的任务不受停止窗口影响");

        gateA.Set();
        gateOut.Set();
    }

    private static async Task QueueScheduleStopOnlyAsync(TestServer server)
    {
        var data = Payload(64 * 1024);
        var gate = new ManualResetEventSlim(false);
        server.Map("stop/a1.zip", server.RangeFile(data, onGet: (_, _) => { gate.Wait(20_000); return false; }));
        server.Map("stop/a2.zip", server.RangeFile(data));

        var clock = new DateTime(2026, 11, 5, 10, 0, 0);
        var options = new KernelOptions { MaxSimultaneous = 4, Clock = () => clock };
        using var kernel = new RealDownloadKernel(options);
        var dir = TempDir();
        // 仅设停止时间 → 有效窗口 [00:00, 22:00)，窗口内照常运行
        kernel.SetQueueSchedule("下班队", start: null, stop: "22:00");
        var a1 = kernel.Add(new NewDownloadRequest(server.Base + "stop/a1.zip", dir, "", 2, TaskPriority.Normal, StartNow: false, Queue: "下班队"));
        var a2 = kernel.Add(new NewDownloadRequest(server.Base + "stop/a2.zip", dir, "", 2, TaskPriority.Normal, StartNow: false, Queue: "下班队"));

        kernel.EvaluateQueueSchedules(); // 10:00 在窗口内 → 批量开始
        await WaitAsync(() => a1.Status == DownloadStatus.Downloading, "窗口内暂停任务应批量开始", 30_000);

        clock = new DateTime(2026, 11, 5, 22, 0, 0);
        kernel.EvaluateQueueSchedules(); // 22:00 出窗 → 下载中的任务批量暂停
        Equal(DownloadStatus.Paused, a1.Status, "到停止时间应暂停下载中的任务");

        // 非法调度直接拒绝，不落库
        AssertThrows<FormatException>(() => kernel.SetQueueSchedule("坏队", "25:00", null), "小时应 <24");
        AssertThrows<FormatException>(() => kernel.SetQueueSchedule("坏队", "abc", null), "须为 HH:mm");
        AssertThrows<FormatException>(() => kernel.SetQueueSchedule("坏队", "08:60", null), "分钟应 <60");

        gate.Set();
    }

    // ── 29. 批量导入（P1-7：txt 每行一条 / # 注释；剪贴板同路径） ─────────

    private static async Task BatchImportFileAsync(TestServer server)
    {
        var data = Payload(64 * 1024);
        server.Map("batch/one.bin", server.RangeFile(data));
        server.Map("batch/two.bin", server.RangeFile(data));
        server.Map("batch/three.bin", server.RangeFile(data));

        var urls = new[] { server.Base + "batch/one.bin", server.Base + "batch/two.bin", server.Base + "batch/three.bin" };
        var file = Path.Combine(TempDir(), "batch.txt");
        File.WriteAllText(file, string.Join('\n', urls));
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 });
        int doneCount = 0;
        var allDone = NewSignal();
        kernel.TaskCompleted += _ => { if (Interlocked.Increment(ref doneCount) == 3) allDone.TrySetResult(); };

        var tasks = kernel.AddBatch(File.ReadAllText(file), dir, 1, TaskPriority.Normal, StartNow: true, Queue: "夜间");

        Equal(3, tasks.Count, "3 行文本应产生 3 个任务");
        That(tasks.All(t => t.Queue == "夜间"), "全部任务应归指定队列");
        for (int i = 0; i < 3; i++) Equal(urls[i], tasks[i].Url, $"第 {i + 1} 行 URL 应按序入队");

        await WaitSignalAsync(allDone.Task, "3 个批量任务应全部完成", 60_000);
        That(tasks.All(t => t.Status == DownloadStatus.Completed), "批量任务应真实下载完成");
    }

    private static async Task BatchImportEdgesAsync(TestServer server)
    {
        server.Map("batch/e1.bin", server.RangeFile(Payload(1024)));
        server.Map("batch/e2.bin", server.RangeFile(Payload(1024)));

        // 空行 / 注释行 / 纯空白行跳过；CRLF 与前导制表符容忍
        var text = "# 顶部注释\n" +
                   server.Base + "batch/e1.bin\n" +
                   "\n   \n# 另一条注释\n" +
                   server.Base + "batch/e2.bin\r\n" +
                   "\t" + server.Base + "batch/e1.bin\n";
        Equal(string.Join("\n",
                server.Base + "batch/e1.bin",
                server.Base + "batch/e2.bin",
                server.Base + "batch/e1.bin"),
            string.Join("\n", BatchImport.ParseLines(text)), "解析应只留有效行并按序输出");

        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 });
        var tasks = kernel.AddBatch(text, dir, 1, TaskPriority.Normal, StartNow: false);
        Equal(3, tasks.Count, "AddBatch 应按解析结果建任务");

        Equal(0, kernel.AddBatch("", dir, 1, TaskPriority.Normal, StartNow: false).Count, "空文本 → 0 任务");
        Equal(0, kernel.AddBatch("# 只有注释\n\n", dir, 1, TaskPriority.Normal, StartNow: false).Count, "纯注释/空行 → 0 任务");
    }

}
