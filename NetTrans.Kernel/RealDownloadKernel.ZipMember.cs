using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using NetTrans.Kernel.Protocol;
using NetTrans.Kernel.Transfer;

namespace NetTrans.Kernel;

/// <summary>
/// ZIP 部分下载（P2-5）：不走探测驱动/TransferSession——任务创建时已知
/// 目标成员。Range 读取 central directory 定位成员，再 Range 拉成员的
/// local header + 数据段，按 method（0=store / 8=deflate）解压落盘。整包
/// 从不落地，只拉 EOCD / CD / 成员三小段。成员较小，进度以完成一次性
/// 推进；checkpoint/断点续传与磁盘空间预检不适用于本路径。
/// </summary>
public sealed partial class RealDownloadKernel
{
    public DownloadTask AddZipMember(
        string url, string entryName, string saveTo,
        TaskPriority priority = TaskPriority.Normal, bool StartNow = false)
    {
        // 先以暂停态入队、标记成员后再放行：StartNow 的 worker 否则可能先
        // 读不到成员标记而走普通 HTTP 全包下载。标记完把任务置 Queued 交给
        // 调度器（BuildTask 对 StartNow:false 造的是 Paused，不会被调度）。
        // NameOverride 让任务从入队起就展示成员名（Kind/Category 由 BuildTask
        // 按成员名推导）。
        var task = Add(new NewDownloadRequest(url, saveTo, "", 1, priority, StartNow: false,
            NameOverride: Path.GetFileName(entryName)));

        lock (_gate)
        {
            _runtimes[task.Id].ZipEntryName = entryName;
            if (StartNow) task.Status = DownloadStatus.Queued;
        }

        if (StartNow)
        {
            Log(task, "已加入队列，等待空闲通道");
            Schedule();
        }
        return task;
    }

    private async Task<TransferOutcome> TransferZipMemberAsync(
        DownloadTask task, Runtime runtime, string entryName, CancellationToken cancellation)
    {
        long total = await HeadLengthAsync(task, runtime, cancellation);

        // 1. EOCD：文件尾部 64KB 切片
        long tailStart = Math.Max(0, total - 65_536);
        byte[] tail = await GetRangeAsync(task, runtime, tailStart, total - 1, cancellation);
        if (!ZipIndex.TryLocateEndRecord(tail, out var eocd))
            throw new InvalidDataException("不是有效的 ZIP 文件（未找到 End of Central Directory）");

        // 2. central directory
        byte[] cd = await GetRangeAsync(task, runtime, eocd.CdOffset, eocd.CdOffset + eocd.CdSize - 1, cancellation);
        var entry = ZipIndex.Read(cd, eocd.EntryCount).FirstOrDefault(e => e.Name == entryName);
        if (entry is null)
            throw new InvalidOperationException($"ZIP 中不存在成员「{entryName}」（共 {eocd.EntryCount} 个成员）");

        // 3. local header：真实 name/extra 长度可能与 central directory 不同
        byte[] header = await GetRangeAsync(task, runtime, entry.LocalHeaderOffset, entry.LocalHeaderOffset + 29, cancellation);
        if (header.Length < 30 || header[0] != 0x50 || header[1] != 0x4B || header[2] != 0x03 || header[3] != 0x04)
            throw new InvalidDataException($"成员 {entryName} 的 local header 无效");
        int nameLen = BitConverter.ToUInt16(header, 26);
        int extraLen = BitConverter.ToUInt16(header, 28);

        // 4. 成员数据段
        long dataStart = entry.LocalHeaderOffset + 30 + nameLen + extraLen;
        byte[] data = await GetRangeAsync(task, runtime, dataStart, dataStart + entry.CompressedSize - 1, cancellation);

        // 5. 解压落盘（冲突沿用 Sanitize 后直接写：成员名在包内唯一）
        string saveTo = !string.IsNullOrWhiteSpace(task.SavePath) ? task.SavePath : DefaultDirectoryFor(task);
        string target = Path.Combine(saveTo, Sanitize(task.Name));
        switch (entry.Method)
        {
            case 0:
                File.WriteAllBytes(target, data);
                break;
            case 8:
                using (var source = new MemoryStream(data))
                using (var deflate = new DeflateStream(source, CompressionMode.Decompress))
                using (var output = File.Create(target))
                {
                    deflate.CopyTo(output);
                }
                break;
            default:
                throw new InvalidOperationException($"不支持的压缩方法 {entry.Method}（仅 store/deflate）");
        }

        lock (_gate)
        {
            task.Size = entry.CompressedSize; // 计入下载字节数（远小于整包）
            task.Done = entry.CompressedSize;
        }
        Log(task, $"ZIP 成员已解出：{entryName}（压缩 {entry.CompressedSize} → 原始 {entry.UncompressedSize} 字节）");
        return new TransferOutcome(TransferOutcomeKind.Completed, null);
    }

    private async Task<long> HeadLengthAsync(DownloadTask task, Runtime runtime, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Head, task.Url);
        foreach (var (name, value) in HeadersFor(runtime, task.Url))
            message.Headers.TryAddWithoutValidation(name, value);
        using var response = await _http.SendAsync(message, cancellation);
        response.EnsureSuccessStatusCode();
        return response.Content.Headers.ContentLength
            ?? throw new InvalidOperationException("服务器未报告 ZIP 大小，无法定位 central directory");
    }

    private async Task<byte[]> GetRangeAsync(
        DownloadTask task, Runtime runtime, long start, long endInclusive, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, task.Url);
        message.Headers.Range = new RangeHeaderValue(start, endInclusive);
        foreach (var (name, value) in HeadersFor(runtime, task.Url))
            message.Headers.TryAddWithoutValidation(name, value);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellation);
    }
}
