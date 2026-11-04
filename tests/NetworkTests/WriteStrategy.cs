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
    // ── 19. 写入策略：小文件内存缓冲 / 大文件聚合直写 ─────────────────────

    private static async Task MemorySinkAsync(TestServer server)
    {
        // 4MB 恰在默认阈值上（≤ 阈值即内存缓冲）：完成前文件不存在，
        // 完成时一次性落盘，全程无断点；目录由落盘动作自建
        var data = Payload(4 * 1024 * 1024);
        server.Map("sink/small.bin", server.RangeFile(data, etag: "\"sink-small-1\""));
        var dir = Path.Combine(TempDir(), "nested", "deeper"); // 目录不存在：落盘时应自建

        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 4, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "sink/small.bin", dir, "", 4, TaskPriority.Normal, StartNow: true));
        kernel.SetSpeedLimit(task.Id, "256 KB/s");

        var path = TargetPath(dir, task);
        await WaitAsync(() => task.Done > 0, "下载应有进度", 15_000);
        That(!File.Exists(path), "小文件完成前不应在盘上出现（内存缓冲无中间态）");
        That(!File.Exists(path + ".nettrans.json"), "小文件不应写断点");
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "任务应完成", 90_000);

        Equal(Hash(data), Hash(File.ReadAllBytes(path)), "内存缓冲落盘字节应完整");
        That(!File.Exists(path + ".nettrans.json"), "小文件全程不应有断点文件");
        That(task.Log.Any(l => l.Message.Contains("内存缓冲")), "日志应记录小文件内存缓冲策略");

        // 暂停：进度随内存缓冲一起丢弃（无文件、无断点），恢复后从头重下并完成
        var dir2 = TempDir();
        var task2 = kernel.Add(new NewDownloadRequest(server.Base + "sink/small.bin", dir2, "", 2, TaskPriority.Normal, StartNow: true));
        kernel.SetSpeedLimit(task2.Id, "128 KB/s");
        await WaitAsync(() => task2.Done > 0, "暂停前下载应有进度", 15_000);
        kernel.Toggle(task2.Id);
        await WaitAsync(() => task2.Status == DownloadStatus.Paused, "任务应暂停", 15_000);
        That(!File.Exists(TargetPath(dir2, task2)), "暂停后不应留下半截文件（进度丢弃）");
        That(!File.Exists(TargetPath(dir2, task2) + ".nettrans.json"), "暂停后不应留下断点");
        // Toggle 同步置 Paused，session 收尾日志晚于状态翻转：轮询等待
        await WaitAsync(() => task2.Log.Any(l => l.Message.Contains("进度不落盘")),
            "暂停日志应说明内存缓冲进度不落盘", 10_000);

        kernel.Toggle(task2.Id);
        await WaitAsync(() => task2.Status == DownloadStatus.Completed, "恢复后应从头重下并完成", 90_000);
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(dir2, task2))), "恢复重下字节应完整");
    }

    private static Task DirectSinkUnitAsync()
    {
        var path = Path.Combine(TempDir(), "direct.bin");
        const long size = 2 * 1024 * 1024;
        var chunk = new byte[64 * 1024];
        for (int i = 0; i < chunk.Length; i++) chunk[i] = (byte)(i % 251);

        using (var sink = new DirectSink(path, size))
        {
            // 乱序：先写第 2 块（断续起点），再补第 1 块，然后连续聚合第 3、4 块
            sink.Write(64 * 1024, chunk, chunk.Length);
            sink.Write(0, chunk, chunk.Length);
            sink.Write(128 * 1024, chunk, chunk.Length);
            sink.Write(192 * 1024, chunk, chunk.Length);
            // 跨 staging 阈值的大块应跳过暂存直接落盘
            var big = new byte[600 * 1024];
            Array.Fill(big, (byte)0xAB);
            sink.Write(1024 * 1024, big, big.Length);
            sink.Complete();
        }

        var onDisk = File.ReadAllBytes(path);
        Equal(size, (long)onDisk.Length, "预分配尺寸应保持");
        That(onDisk.AsSpan(0, 64 * 1024).ToArray().SequenceEqual(chunk), "块 1（乱序补写）应精确落位");
        That(onDisk.AsSpan(64 * 1024, 64 * 1024).ToArray().SequenceEqual(chunk), "块 2（断续起点）应精确落位");
        That(onDisk.AsSpan(128 * 1024, 128 * 1024).ToArray().SequenceEqual(chunk.Concat(chunk).ToArray()),
            "块 3+4（连续聚合）应精确落位");
        That(onDisk.AsSpan((int)(1024 * 1024), 600 * 1024).ToArray().All(b => b == 0xAB), "跨阈值大块直写应精确落位");
        That(onDisk.AsSpan(128 * 1024 + 128 * 1024, 64 * 1024).ToArray().All(b => b == 0), "未写区域应保持 0 填充");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 真·流式响应：全程无 ContentLength（HttpListener 自动 chunked），HEAD 与
    /// GET 都不给长度。内核应把「服务器关流」当作 EOF 完成任务，而不是误判
    /// 「连接在完成前中断」后全量重试三遍再失败。
    /// </summary>
    private static async Task TrueStreamingAsync(TestServer server)
    {
        var data = Payload(3 * 1024 * 1024);
        server.Map("stream/live.bin", ctx =>
        {
            if (ctx.Request.HttpMethod == "HEAD")
            {
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.Close();
                return;
            }
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.OutputStream.Write(data);
            ctx.Response.OutputStream.Close();
            ctx.Response.Close();
        });
        var dir = TempDir();

        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(server.Base + "stream/live.bin", dir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => task.Status == DownloadStatus.Completed, "无 ContentLength 的流式下载应完成", 60_000);
        Equal(Hash(data), Hash(File.ReadAllBytes(TargetPath(dir, task))), "流式下载字节应完整");
        Equal(data.Length, task.Size, "完成后 Size 应收紧为实际长度");
    }

    /// <summary>手工 bencode 一个最小种子：announce + announce-list + info{name,length}。</summary>
    private static byte[] BuildTorrent(string announce, string announce2)
    {
        static byte[] S(string text) => Encoding.ASCII.GetBytes($"{text.Length}:{text}");
        using var ms = new MemoryStream();
        void W(ReadOnlySpan<byte> bytes) => ms.Write(bytes);
        W("d"u8);
        W(S("announce")); W(S(announce));
        W(S("announce-list")); W("ll"u8); W(S(announce)); W("e"u8); W("l"u8); W(S(announce2)); W("ee"u8);
        W(S("info")); W("d"u8);
        W(S("length")); W("i1024e"u8);
        W(S("name")); W(S("pt.torrent.iso"));
        W("e"u8);
        W("e"u8);
        return ms.ToArray();
    }

}
