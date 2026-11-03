using NetTrans.Kernel.Protocol.Drivers;

namespace NetTrans.Kernel.Protocol;

/// <summary>
/// The driver table: detects the protocol from a URL, probes with the matching
/// driver, and honours a one-hop handoff when the resource itself claims to be
/// something else (an extension-less URL serving m3u8, say).
/// </summary>
public sealed class ProtocolDriverRegistry
{
    private readonly Dictionary<DownloadProtocol, IProtocolDriver> _drivers = new();
    private readonly KernelOptions _options;
    private readonly HttpClient _http;

    public ProtocolDriverRegistry(KernelOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
        Register(new HttpDriver());
        Register(new FtpDriver());
        Register(new HlsDriver());
        Register(new DashDriver());
        Register(new BitTorrentDriver());
        Register(new Ed2kDriver());
    }

    public void Register(IProtocolDriver driver) => _drivers[driver.Protocol] = driver;

    /// <summary>Detect → probe → (optional) re-probe under a body sniff. Never throws for probe failures
    /// that mean "cannot transfer": those surface as a <see cref="TransferPlan.Unsupported"/>.</summary>
    public async Task<ResourceProbe> ProbeAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers,
        LogSink? log,
        CancellationToken cancellation)
    {
        var protocol = ProtocolDetector.Detect(url);
        ResourceProbe probe = UnknownResource(url, protocol);

        for (int hop = 0; hop < 2; hop++)
        {
            if (!_drivers.TryGetValue(protocol, out var driver)) break;

            var context = new DriverContext
            {
                Options = _options,
                Http = _http,
                Headers = headers ?? _options.DefaultHeaders,
                Log = log,
            };

            probe = await driver.ProbeAsync(url, context, cancellation);

            if (probe.SuggestedProtocol is { } suggested && suggested != probe.Protocol)
            {
                protocol = suggested;
                continue;
            }

            break;
        }

        return probe;
    }

    private static ResourceProbe UnknownResource(string url, DownloadProtocol protocol)
    {
        string name = "";
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) name = Path.GetFileName(uri.LocalPath);
        }
        catch
        {
            // keep the empty name; the kernel falls back to its own guess
        }

        return new ResourceProbe(
            name, -1, false, null, url, protocol,
            new TransferPlan.Unsupported($"暂不支持该协议（{protocol}）"));
    }
}
