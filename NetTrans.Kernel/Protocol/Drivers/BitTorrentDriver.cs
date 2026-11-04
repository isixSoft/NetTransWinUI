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
                bytes = await DriverHelpers.GetBytesAsync(url, context, cancellation);
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
                $"种子元数据已解析（{trackers} 个 tracker），数据面需外部 BT 引擎"),
            Trackers: TrackerList(torrent));
    }

    /// <summary>magnet 的 tr= tracker 列表，逐个脱敏后去重。</summary>
    private static IReadOnlyList<string>? MagnetTrackers(string url)
    {
        var query = url[(url.IndexOf('?') + 1)..];
        var trackers = new List<string>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !parts[0].Equals("tr", StringComparison.OrdinalIgnoreCase)) continue;
            var announce = Uri.UnescapeDataString(parts[1]);
            var masked = SensitiveUrls.Mask(announce);
            if (masked.Length > 0 && !trackers.Contains(masked)) trackers.Add(masked);
        }
        return trackers.Count > 0 ? trackers : null;
    }

    /// <summary>.torrent 的 announce + announce-list，逐个脱敏后去重。</summary>
    private static IReadOnlyList<string>? TrackerList(Dictionary<string, object?>? torrent)
    {
        if (torrent is null) return null;
        var trackers = new List<string>();

        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var masked = SensitiveUrls.Mask(raw);
            if (!trackers.Contains(masked)) trackers.Add(masked);
        }

        if (torrent.GetValueOrDefault("announce-list") is List<object?> groups)
        {
            foreach (var group in groups.OfType<List<object?>>())
                foreach (var item in group)
                    Add(Bencode.Text(item));
        }
        Add(Bencode.Text(torrent.GetValueOrDefault("announce")));
        return trackers.Count > 0 ? trackers : null;
    }

    private static ResourceProbe ProbeMagnet(string url, DriverContext context)
    {
        string? hash = null;
        string? name = null;
        long size = -1;

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
            }
        }

        hash ??= "unknown";
        name ??= $"bt-{hash[..Math.Min(8, hash.Length)]}";
        var trackerList = MagnetTrackers(url);
        context.Log?.Invoke($"magnet：infohash {hash} · {trackerList?.Count ?? 0} 个 tracker");
        return new ResourceProbe(
            name, size, AcceptRanges: false, ETag: null, FinalUrl: url,
            DownloadProtocol.BitTorrent,
            new TransferPlan.ExternalEngine("BitTorrent / libtorrent",
                $"infohash {hash} · {trackerList?.Count ?? 0} 个 tracker · DHT/PEX 数据面需外部 BT 引擎"),
            Trackers: trackerList);
    }

    private static int TrackerCount(Dictionary<string, object?>? torrent)
    {
        if (torrent is null) return 0;
        if (torrent.GetValueOrDefault("announce-list") is List<object?> groups) return groups.Count;
        return torrent.ContainsKey("announce") ? 1 : 0;
    }
}
