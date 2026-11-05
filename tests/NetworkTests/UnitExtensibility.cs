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
    // ── 15. 单元扩容：MirrorSourceSet / SpeedLimiter ──────────────────────

    private static async Task MirrorSourceSetUnitAsync()
    {
        // 去重：主源与镜像相同、镜像彼此相同都只保留一份
        var set = new MirrorSourceSet("p", new[] { "p", "a", "a", "b" });
        Equal(3, set.Count, "主源 + 去重后的镜像应为 3 个源");

        // 轮转：Next 按 主源→镜像 依次循环
        var sequence = string.Join(",", Enumerable.Range(0, 6).Select(_ => set.Next()));
        Equal("p,a,b,p,a,b", sequence, "Next 应按 p→a→b 轮转");

        // 失败降级：报告失败的源移到队尾，仍在轮转中（不删除）
        set.ReportFailure("p");
        var after = string.Join(",", Enumerable.Range(0, 3).Select(_ => set.Next()));
        Equal("a,b,p", after, "ReportFailure 应把失败源移到队尾");

        // 单源集合的失败报告是无操作
        var single = new MirrorSourceSet("only", null);
        single.ReportFailure("only");
        Equal("only", single.Next(), "单源 ReportFailure 后轮转不变");

        await Task.CompletedTask;
    }

    private static async Task MirrorValidateUnitAsync()
    {
        // mock 探测：URL → (size, etag)；"boom" 直接抛异常（坏镜像）
        var catalogue = new Dictionary<string, (long Size, string? ETag)>
        {
            ["ok-1"] = (1000, "e1"),
            ["ok-2"] = (1000, null),     // 无 etag：身份未知但大小一致 → 保留
            ["wrong-size"] = (999, "e1"),
            ["wrong-etag"] = (1000, "e2"),
        };

        var valid = await MirrorSourceSet.ValidateAsync(
            new[] { "ok-1", "wrong-size", "ok-2", "wrong-etag", "boom" },
            size: 1000, etag: "e1", cancellation: default,
            probe: (url, _) => url == "boom"
                ? throw new InvalidOperationException("mirror exploded")
                : Task.FromResult(catalogue.TryGetValue(url, out var hit) ? hit : (0L, (string?)null)));

        Equal(string.Join(",", valid), "ok-1,ok-2", "ValidateAsync 应保留大小一致且 etag 相容的镜像、跳过坏镜像");

        // 外部取消必须传播而不是被吞掉（探测收到的是联动令牌，应随外部取消）
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new CancellationToken[1];
        await AssertThrowsAsync<OperationCanceledException>(
            () => MirrorSourceSet.ValidateAsync(new[] { "x" }, 1000, "e1",
                probe: (_, token) => { cancelled[0] = token; throw new OperationCanceledException(token); },
                cts.Token),
            "取消应传播为 OperationCanceledException");
        That(cancelled[0].IsCancellationRequested, "探测收到的联动令牌应已被外部取消带动");

        // 探测自身超时（6s 上限）：假探测挂起 → 被超时打断 → 镜像被跳过
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var none = await MirrorSourceSet.ValidateAsync(new[] { "slow" }, 1000, "e1",
            probe: async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token); // 6s 探测超时将其打断
                return (0L, (string?)null);
            },
            cancellation: default);
        sw.Stop();
        Equal(0, none.Count, "挂起的镜像探测应被超时跳过");
        That(sw.Elapsed < TimeSpan.FromSeconds(10), $"镜像探测超时上限应生效（实际 {sw.Elapsed.TotalSeconds:0.0}s）");
    }

    private static async Task LimiterAccuracyUnitAsync()
    {
        // 分块消费的精度：256 KB/s 下消费 8×32 KB ≈ 1s
        var limiter = new SpeedLimiter(256 * 1024);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var chunk = new byte[32 * 1024];
        for (int i = 0; i < 8; i++) await limiter.WaitAsync(chunk.Length);
        sw.Stop();
        That(sw.Elapsed.TotalSeconds >= 0.75,
            $"256 KB/s 下 8×32 KB 应 ≥0.75s（实际 {sw.Elapsed.TotalSeconds:0.00}s）");
        That(sw.Elapsed.TotalSeconds <= 4.0,
            $"限速节奏不应异常缓慢（实际 {sw.Elapsed.TotalSeconds:0.00}s）");

        // 中途改限速立即生效：512 KB/s 用 256 KB（~0.5s）→ 降到 64 KB/s 再用 128 KB（~2s）
        limiter.SetLimit(512 * 1024);
        for (int i = 0; i < 8; i++) await limiter.WaitAsync(chunk.Length);
        limiter.SetLimit(64 * 1024);
        sw.Restart();
        for (int i = 0; i < 4; i++) await limiter.WaitAsync(chunk.Length);
        sw.Stop();
        That(sw.Elapsed.TotalSeconds >= 1.6,
            $"降到 64 KB/s 后 4×32 KB 应 ≥1.6s（实际 {sw.Elapsed.TotalSeconds:0.00}s）");
        That(sw.Elapsed.TotalSeconds <= 7.0,
            $"降速后节奏不应异常缓慢（实际 {sw.Elapsed.TotalSeconds:0.00}s）");
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> body, string what) where TException : Exception
    {
        try
        {
            await body();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception error)
        {
            throw new Exception($"{what}：抛出了 {error.GetType().Name}（{error.Message}）而非 {typeof(TException).Name}");
        }
        throw new Exception($"{what}：未抛出 {typeof(TException).Name}");
    }

    private static void AssertThrows<TException>(Action body, string what) where TException : Exception
    {
        try
        {
            body();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception error)
        {
            throw new Exception($"{what}：抛出了 {error.GetType().Name}（{error.Message}）而非 {typeof(TException).Name}");
        }
        throw new Exception($"{what}：未抛出 {typeof(TException).Name}");
    }

    // ── 16. 单元扩容：SegmentTable / checkpoint / ChunkedFileWriter ───────

    private static Task SegmentSplitUnitAsync()
    {
        const long MiB = 1024 * 1024;

        // 大小未知（内核探测失败时传 -1）→ 单个流式分段，永不算完成
        var stream = SegmentTable.Split("u", -1, null, 8);
        Equal(1, stream.Segments.Length, "未知大小应只有 1 个流式分段");
        Equal(long.MaxValue, stream.Segments[0].End, "流式分段 End 应为 long.MaxValue");
        That(!stream.Segments[0].IsComplete, "流式分段不应算完成");
        Equal(-1L, stream.Size, "未知大小时 Size=-1");

        // 过切分 ×4 + 1 MiB 下限：3 MiB / 8 连接 → 3 段各 1 MiB（下限生效）
        var floored = SegmentTable.Split("f", 3 * MiB, "e", 8);
        Equal(3, floored.Segments.Length, "3 MiB @ 8 连接应受 1 MiB 下限切成 3 段");
        for (int i = 0; i < floored.Segments.Length; i++)
            Equal((i * MiB, (i + 1) * MiB), (floored.Segments[i].Start, floored.Segments[i].End),
                $"下限分段 {i} 应恰好是第 {i} 个 1 MiB");

        // 过切分：10 MiB / 2 连接 → 8 段，总长守恒
        var over = SegmentTable.Split("o", 10 * MiB, null, 2);
        Equal(8, over.Segments.Length, "10 MiB @ 2 连接应过切分为 8 段");
        Equal(10 * MiB, over.Segments.Sum(s => s.End - s.Start), "过切分后总长应守恒");
        That(over.Segments.All(s => s.End - s.Start >= MiB), "过切分段不得低于 1 MiB 下限");

        // 不整除：5 MiB+1 / 1 连接 → 4 段，前 extra 段多 1 字节，接缝连续
        var uneven = SegmentTable.Split("n", 5 * MiB + 1, null, 1);
        Equal(4, uneven.Segments.Length, "5 MiB+1 @ 1 连接应切成 4 段");
        for (int i = 1; i < uneven.Segments.Length; i++)
            Equal(uneven.Segments[i - 1].End, uneven.Segments[i].Start, $"第 {i} 段起点应紧接上一段终点");
        Equal(5 * MiB + 1, uneven.Segments[^1].End, "末段终点应等于总大小");

        // connections 钳制：0 与 64 之外不存在；64 也封顶 64 段
        Equal(4, SegmentTable.Split("c", 64 * MiB, null, 0).Segments.Length, "connections=0 钳制为 1 → 4 段（×4 过切分）");
        Equal(64, SegmentTable.Split("c", 64 * MiB, null, 100).Segments.Length, "connections=100 钳制为 64 → 封顶 64 段");

        return Task.CompletedTask;
    }

    private static Task CheckpointRoundTripUnitAsync()
    {
        const long MiB = 1024 * 1024;

        // 往返：Split → 部分完成 → ToCheckpoint → Restore，字段与进度不丢
        var table = SegmentTable.Split("rt", 8 * MiB, "etag-1", 4);
        table.Segments[0].Done = table.Segments[0].End - table.Segments[0].Start; // 第 1 段完成
        table.Segments[1].Done = 123456;
        var checkpoint = table.ToCheckpoint();
        Equal("ranges", checkpoint.Kind, "checkpoint Kind 应为 ranges");

        var restored = SegmentTable.Restore(checkpoint);
        That(restored is not null, "合法 checkpoint 应可 Restore");
        Equal(table.Size, restored!.Size, "往返后 Size 应不变");
        Equal(table.ETag, restored.ETag, "往返后 ETag 应不变");
        Equal("rt", restored.Url, "往返后 Url 应不变");
        Equal(table.Segments.Length, restored.Segments.Length, "往返后分段数应不变");
        for (int i = 0; i < table.Segments.Length; i++)
        {
            Equal(table.Segments[i].Start, restored.Segments[i].Start, $"段 {i} Start 往返");
            Equal(table.Segments[i].End, restored.Segments[i].End, $"段 {i} End 往返");
            Equal(table.Segments[i].Done, restored.Segments[i].Done, $"段 {i} Done 往返");
        }
        Equal(table.DoneBytes, restored.DoneBytes, "往返后 DoneBytes 应不变");

        // 校验拒绝：Kind 错 / 空数组 / 长度不齐 → null（上层会静默重下）
        That(SegmentTable.Restore(new TransferCheckpoint { Kind = "segments", Start = [0], End = [1], Done = [0] }) is null,
            "Kind=segments 的 checkpoint 不应被 Restore 为 ranges 表");
        That(SegmentTable.Restore(new TransferCheckpoint { Kind = "ranges", Start = [], End = [], Done = [] }) is null,
            "空数组 checkpoint 应拒绝");
        That(SegmentTable.Restore(new TransferCheckpoint { Kind = "ranges", Start = [0, 1], End = [1], Done = [0, 0] }) is null,
            "数组长度不齐应拒绝");

        // Done 越界 clamp：负数 → 0，超过段长 → 段长
        var clamped = SegmentTable.Restore(new TransferCheckpoint
        {
            Kind = "ranges", Url = "u", Size = 2 * MiB,
            Start = [0, MiB], End = [MiB, 2 * MiB], Done = [-5, 2 * MiB],
        });
        That(clamped is not null, "越界 checkpoint 仍应可 Restore");
        Equal(0L, clamped!.Segments[0].Done, "负 Done 应 clamp 为 0");
        Equal(MiB, clamped.Segments[1].Done, "超段长 Done 应 clamp 为段长");
        That(clamped.Segments[1].IsComplete, "clamp 到段长后该段应视为完成");

        return Task.CompletedTask;
    }

    private static Task ChunkedWriterUnitAsync()
    {
        const long MiB = 1024 * 1024;
        var path = Path.Combine(TempDir(), "chunked.bin");

        // 预分配：writer 一建好文件就是全尺寸，未写区域读出 0
        using (var writer = new ChunkedFileWriter(path, 4 * MiB))
            writer.Write(2 * MiB, new byte[] { 1, 2, 3, 4 }, 4);
        Equal(4 * MiB, new FileInfo(path).Length, "预分配应立即把文件设为全尺寸");
        var onDisk = File.ReadAllBytes(path);
        That(onDisk.AsSpan(0, (int)(2 * MiB)).ToArray().All(b => b == 0), "未写前半应是 0 填充");
        That(onDisk.AsSpan((int)(2 * MiB), 4).ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4 }),
            "乱序直写应精确落在指定偏移");
        That(onDisk.AsSpan((int)(2 * MiB) + 4).ToArray().All(b => b == 0), "写入点之后应仍是 0 填充");

        // 多线程乱序并发直写：8 个连接各写自己的 1 MiB 条带，字节精确拼装
        var strip = Payload((int)MiB);
        var concurrent = Path.Combine(TempDir(), "chunked-concurrent.bin");
        using (var writer = new ChunkedFileWriter(concurrent, 8 * MiB))
        {
            var lanes = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                var buffer = new byte[64 * 1024];
                long at = i * MiB;
                for (int copied = 0; copied < MiB; copied += buffer.Length)
                {
                    Array.Copy(strip, copied, buffer, 0, buffer.Length);
                    writer.Write(at + copied, buffer, buffer.Length);
                }
            })).ToArray();
            Task.WaitAll(lanes.ToArray(), TimeSpan.FromSeconds(30));
            writer.Flush();
        }
        var assembled = File.ReadAllBytes(concurrent);
        Equal(8 * MiB, assembled.Length, "并发直写后文件长度应正确");
        That(assembled.SequenceEqual(Enumerable.Range(0, 8).SelectMany(_ => strip)), "8 条带乱序并发写入后应字节精确复原");

        return Task.CompletedTask;
    }

}
