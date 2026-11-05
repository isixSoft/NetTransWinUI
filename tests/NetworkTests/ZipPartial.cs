using System.IO.Compression;
using NetTrans.Kernel;
using NetTrans.Kernel.Protocol;

namespace NetworkTests;

internal sealed partial class Program
{
    // ── 35. ZIP 部分下载（P2-5：Range 读 central directory → 按成员下载解压）──

    /// <summary>In-memory ZIP fixture: 10 members, part01–05 store + part06–10 deflate.</summary>
    private static byte[] BuildZipFixture(out Dictionary<string, byte[]> contents)
    {
        contents = new Dictionary<string, byte[]>();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int i = 1; i <= 10; i++)
            {
                byte[] data = Payload(64 * 1024 + i * 1024);
                var entry = zip.CreateEntry($"part{i:00}.bin",
                    i <= 5 ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(data);
                contents[$"part{i:00}.bin"] = data;
            }
        }
        return buffer.ToArray();
    }

    private static async Task ZipIndexParsesCentralDirectoryAsync(TestServer server)
    {
        var zip = BuildZipFixture(out var contents);
        var tail = zip[^65_536..]; // EOCD：固定 22B + 注释上限 64KB
        That(ZipIndex.TryLocateEndRecord(tail, out var eocd), "应定位到 End of Central Directory");
        Equal(10, eocd.EntryCount, "EOCD 应报告 10 个成员");

        var cd = zip[eocd.CdOffset..(eocd.CdOffset + eocd.CdSize)];
        var entries = ZipIndex.Read(cd, eocd.EntryCount);
        Equal(10, entries.Count, "central directory 应解析出 10 个成员");
        Equal("part01.bin", entries[0].Name, "第 1 个成员名");
        Equal("part10.bin", entries[^1].Name, "第 10 个成员名");
        Equal((long)contents["part01.bin"].Length, entries[0].UncompressedSize, "第 1 个成员原始尺寸");
        Equal((long)contents["part10.bin"].Length, entries[^1].UncompressedSize, "第 10 个成员原始尺寸");
        Equal(0, entries[0].Method, "part01 应为 store（未压缩）");
        Equal(8, entries[^1].Method, "part10 应为 deflate");
        That(entries.All(e => e.LocalHeaderOffset >= 0 && e.LocalHeaderOffset < zip.Length),
            "所有 local header offset 应落在文件范围内");
        await Task.CompletedTask;
    }

    private static async Task ZipMemberDownloadAsync(TestServer server)
    {
        var zip = BuildZipFixture(out var contents);
        server.Map("zip/pack.zip", server.RangeFile(zip));

        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 2 });
        var allDone = NewSignal();
        int completedCount = 0;
        kernel.TaskCompleted += _ => { if (Interlocked.Increment(ref completedCount) == 2) allDone.TrySetResult(); };

        var storeTask = kernel.AddZipMember(server.Base + "zip/pack.zip", "part01.bin", dir, TaskPriority.Normal, StartNow: true);
        var deflateTask = kernel.AddZipMember(server.Base + "zip/pack.zip", "part08.bin", dir, TaskPriority.Normal, StartNow: true);

        await WaitSignalAsync(allDone.Task, "2 个 ZIP 成员任务应完成", 60_000);
        That(storeTask.Status == DownloadStatus.Completed, "store 成员任务应完成");
        That(deflateTask.Status == DownloadStatus.Completed, "deflate 成员任务应完成");
        That(File.ReadAllBytes(Path.Combine(dir, "part01.bin")).AsSpan().SequenceEqual(contents["part01.bin"]),
            "store 成员解出内容应正确");
        That(File.ReadAllBytes(Path.Combine(dir, "part08.bin")).AsSpan().SequenceEqual(contents["part08.bin"]),
            "deflate 成员解压内容应正确");
        That(storeTask.Size < zip.Length / 3,
            $"只下载单个成员时字节数应远小于整包（{storeTask.Size} < {zip.Length / 3}）");

        // 未知成员：异步进入 Error，错误文案点名成员
        var missing = kernel.AddZipMember(server.Base + "zip/pack.zip", "nope.bin", dir, TaskPriority.Normal, StartNow: true);
        await WaitAsync(() => missing.Status == DownloadStatus.Error, "未知成员任务应进入 Error", 30_000);
        That(missing.ErrorMessage is not null && missing.ErrorMessage.Contains("nope.bin"),
            $"错误文案应点名未知成员（实际「{missing.ErrorMessage}」）");
    }

}
