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

internal static partial class Program
{
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

}
