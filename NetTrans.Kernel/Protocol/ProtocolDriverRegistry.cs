using NetTrans.Kernel.Protocol.Drivers;
using NetTrans.Kernel.Sites;

namespace NetTrans.Kernel.Protocol;

/// <summary>
/// The driver table: site extractors first (a page URL of a supported site is
/// resolved to direct media streams, then re-probed with the matching protocol
/// driver), then protocol detection from the URL, probe with the matching
/// driver, and honour a one-hop handoff when the resource itself claims to be
/// something else (an extension-less URL serving m3u8, say).
/// </summary>
public sealed class ProtocolDriverRegistry
{
    private readonly Dictionary<DownloadProtocol, IProtocolDriver> _drivers = new();
    private readonly SiteExtractorRegistry _sites = new();
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

        _sites.Register(new BilibiliSite());
        _sites.Register(new TwitterSite());
        _sites.Register(new YouTubeSite());
        // 兜底提取器必须最后注册：CanHandle 声称一切 http(s) URL，注册顺序保证内置站点优先
        if (_options.EnableYtDlp) _sites.Register(new YtDlpSite(_options.YtDlpRunner));
    }

    public void Register(IProtocolDriver driver) => _drivers[driver.Protocol] = driver;

    /// <summary>Append a site extractor (app-level sites / tests); first CanHandle wins.</summary>
    public void RegisterSite(ISiteExtractor site) => _sites.Register(site);

    /// <summary>Site extraction → protocol probe → (optional) re-probe under a body sniff. Never throws
    /// for probe failures that mean "cannot transfer": those surface as a <see cref="TransferPlan.Unsupported"/>.</summary>
    public async Task<ResourceProbe> ProbeAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers,
        LogSink? log,
        CancellationToken cancellation)
    {
        var context = new DriverContext
        {
            Options = _options,
            Http = _http,
            Headers = headers ?? _options.DefaultHeaders,
            Log = log,
        };

        var siteProbe = await ProbeWithSiteAsync(url, context, cancellation);
        if (siteProbe is not null) return siteProbe;

        var protocol = ProtocolDetector.Detect(url);
        ResourceProbe probe = UnknownResource(url, protocol);

        for (int hop = 0; hop < 2; hop++)
        {
            if (!_drivers.TryGetValue(protocol, out var driver)) break;

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

    // ── 站点层：页面 URL → 直链流 → 复用协议驱动探测 ─────────────────────

    private async Task<ResourceProbe?> ProbeWithSiteAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        var site = _sites.Find(url);
        if (site is null) return null;

        SiteMedia? media;
        try
        {
            media = await site.ExtractAsync(url, context, cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            context.Log?.Invoke($"站点解析（{site.Name}）失败，回退协议探测（{DriverHelpers.Brief(error.Message)}）");
            return null;
        }

        if (media is null) return null; // 提取器放弃认领

        context.Log?.Invoke($"站点解析（{site.Name}）：{media.Title} · {media.Streams.Count} 条候选流");

        foreach (var stream in media.Streams)
        {
            ResourceProbe? probe;
            try
            {
                probe = await ProbeStreamAsync(media, stream, context, cancellation);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                context.Log?.Invoke($"直链探测失败（{stream.Label}）：{DriverHelpers.Brief(error.Message)}");
                probe = null;
            }

            if (probe is not null) return probe;
            context.Log?.Invoke($"跳过不可用的流（{stream.Label}），尝试下一条");
        }

        return DriverHelpers.Unsupported(url, DownloadProtocol.Http,
            media.Streams.Count > 0
                ? $"{site.Name}：已解析出 {media.Streams.Count} 条流但均不可下载"
                : $"{site.Name}：页面没有可下载的流");
    }

    /// <summary>Probe one direct stream with the matching protocol driver; null = unusable, try the next.</summary>
    private async Task<ResourceProbe?> ProbeStreamAsync(SiteMedia media, SiteStream stream, DriverContext context, CancellationToken cancellation)
    {
        var protocol = ProtocolDetector.Detect(stream.Url);
        if (!_drivers.TryGetValue(protocol, out var driver)) return null;

        var streamContext = new DriverContext
        {
            Options = context.Options,
            Http = context.Http,
            // 站点头（Referer/UA）压过任务级同名字头：站点对自己的 CDN 最有发言权
            Headers = MergeHeaders(context.Headers, stream.Headers),
            Log = context.Log,
        };

        var probe = await driver.ProbeAsync(stream.Url, streamContext, cancellation);
        if (probe.Plan is TransferPlan.Unsupported) return null;

        // 页面标题做任务名（保留探测出的扩展名）；站点必需头随探针带入传输会话
        return probe with
        {
            Name = ComposeName(media.Title, probe.Name),
            FinalUrl = stream.Url,
            ExtraHeaders = stream.Headers,
        };
    }

    private static IReadOnlyDictionary<string, string> MergeHeaders(
        IReadOnlyDictionary<string, string> baseHeaders, IReadOnlyDictionary<string, string>? extra)
    {
        if (extra is not { Count: > 0 }) return baseHeaders;
        var merged = new Dictionary<string, string>(baseHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in extra) merged[name] = value;
        return merged;
    }

    /// <summary>Page title for the task name, extension from the probed direct link.</summary>
    private static string ComposeName(string title, string probedName)
    {
        if (string.IsNullOrWhiteSpace(title)) return probedName;
        var extension = Path.GetExtension(probedName);
        return extension.Length > 0 ? title + extension : title;
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
