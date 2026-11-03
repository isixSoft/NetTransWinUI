namespace NetTrans.Kernel.Protocol.Drivers;

/// <summary>
/// BitTorrent metadata driver: magnet:?xt=urn:btih:… and .torrent (remote or
/// local path) both parse here — name, size, trackers. Moving bytes needs a
/// real torrent engine (libtorrent ≥ 2.0 on Ghost's side), which stays an
/// explicit external-engine slot: the plan reports that honestly and the task
/// fails with a clear message instead of pretending.
/// </summary>
public sealed class BitTorrentDriver : IProtocolDriver
{
    public DownloadProtocol Protocol => DownloadProtocol.BitTorrent;

    public async Task<ResourceProbe> ProbeAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return ProbeMagnet(url, context);

        byte[] bytes;
        try
        {
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                DriverHelpers.AddHeaders(message, context.Headers);
                using var response = await context.Http.SendAsync(message, cancellation);
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync(cancellation);
            }
            else
            {
                bytes = await File.ReadAllBytesAsync(url, cancellation);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return DriverHelpers.Unsupported(url, DownloadProtocol.BitTorrent, $"无法读取种子文件（{DriverHelpers.Brief(error.Message)}）");
        }

        var torrent = Bencode.DecodeTorrent(bytes);
        var info = torrent?.GetValueOrDefault("info") as Dictionary<string, object?>;
        if (info is null)
            return DriverHelpers.Unsupported(url, DownloadProtocol.BitTorrent, "不是有效的 .torrent 元数据");

        string name = Bencode.Text(info.GetValueOrDefault("name")) ?? "bt-download";
        long? size = Bencode.Number(info.GetValueOrDefault("length"))
                     ?? (info.GetValueOrDefault("files") as List<object?>)?.Sum(file =>
                         file is Dictionary<string, object?> entry ? Bencode.Number(entry.GetValueOrDefault("length")) ?? 0 : 0);

        int trackers = TrackerCount(torrent);
        context.Log?.Invoke($"BT 元数据：{name} · {(size ?? 0) / 1048576.0:0.#} MB · {trackers} 个 tracker");
        return new ResourceProbe(
            name, size ?? -1, AcceptRanges: false, ETag: null, FinalUrl: url,
            DownloadProtocol.BitTorrent,
            new TransferPlan.ExternalEngine("BitTorrent / libtorrent",
                $"种子元数据已解析（{trackers} 个 tracker），数据面需外部 BT 引擎"));
    }

    private static ResourceProbe ProbeMagnet(string url, DriverContext context)
    {
        string? hash = null;
        string? name = null;
        long size = -1;
        int trackers = 0;

        var query = url[(url.IndexOf('?') + 1)..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;
            string key = Uri.UnescapeDataString(parts[0]);
            string value = Uri.UnescapeDataString(parts[1]);

            switch (key)
            {
                case "xt" when value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase):
                    hash = value["urn:btih:".Length..];
                    break;
                case "dn" when name is null:
                    name = value;
                    break;
                case "xl":
                    _ = long.TryParse(value, out size);
                    break;
                case "tr":
                    trackers++;
                    break;
            }
        }

        hash ??= "unknown";
        name ??= $"bt-{hash[..Math.Min(8, hash.Length)]}";
        context.Log?.Invoke($"magnet：infohash {hash} · {trackers} 个 tracker");
        return new ResourceProbe(
            name, size, AcceptRanges: false, ETag: null, FinalUrl: url,
            DownloadProtocol.BitTorrent,
            new TransferPlan.ExternalEngine("BitTorrent / libtorrent",
                $"infohash {hash} · {trackers} 个 tracker · DHT/PEX 数据面需外部 BT 引擎"));
    }

    private static int TrackerCount(Dictionary<string, object?>? torrent)
    {
        if (torrent is null) return 0;
        if (torrent.GetValueOrDefault("announce-list") is List<object?> groups) return groups.Count;
        return torrent.ContainsKey("announce") ? 1 : 0;
    }
}
