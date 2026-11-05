namespace NetTrans.Kernel.Protocol.Drivers;

/// <summary>
/// eD2k link driver: ed2k://|file|NAME|SIZE|HASH|/ parses into a probe.
/// Like BT, the data plane (Kelpie on Ghost's side) is an external-engine
/// slot — the plan reports that honestly.
/// </summary>
public sealed class Ed2kDriver : IProtocolDriver
{
    public DownloadProtocol Protocol => DownloadProtocol.Ed2k;

    public Task<ResourceProbe> ProbeAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        var parts = url.Split('|');
        if (parts.Length >= 6 &&
            parts[0].StartsWith("ed2k:", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("file", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(parts[3], out var size))
        {
            string name = Uri.UnescapeDataString(parts[2]);
            string hash = parts[4];
            context.Log?.Invoke($"eD2k 元数据：{name} · {size} 字节 · hash {hash}");
            return Task.FromResult(new ResourceProbe(
                name, size, AcceptRanges: false, ETag: null, FinalUrl: url,
                DownloadProtocol.Ed2k,
                new TransferPlan.ExternalEngine("eD2k / Kelpie", $"hash {hash} · 数据面需外部 eD2k 引擎")));
        }

        return Task.FromResult(DriverHelpers.Unsupported(url, DownloadProtocol.Ed2k, "无法识别的 eD2k 链接"));
    }
}
