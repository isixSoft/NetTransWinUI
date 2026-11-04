using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace NetTrans.Kernel.Protocol.Drivers;

/// <summary>
/// MPEG-DASH (mpd) driver, deliberately minimal but real: picks the first
/// period, prefers a video adaptation set, picks the highest-bandwidth
/// representation, and materializes SegmentTemplate (duration or
/// SegmentTimeline, $Number$/$Time$/$RepresentationID$/$Bandwidth$) and
/// SegmentList manifests into an ordered segment plan. A SegmentBase (single
/// indexed file) falls back to a byte-range plan on the base URL.
/// </summary>
public sealed class DashDriver : IProtocolDriver
{
    public DownloadProtocol Protocol => DownloadProtocol.Dash;

    public async Task<ResourceProbe> ProbeAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        XDocument document;
        try
        {
            var text = await DriverHelpers.GetTextAsync(url, context, cancellation);
            document = XDocument.Parse(text);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return DriverHelpers.Unsupported(url, DownloadProtocol.Dash, $"无法读取 MPD 清单（{DriverHelpers.Brief(error.Message)}）");
        }

        var mpd = document.Root;
        if (mpd is null || mpd.Name.LocalName != "MPD")
            return DriverHelpers.Unsupported(url, DownloadProtocol.Dash, "不是有效的 MPD 清单");

        var period = mpd.Elements().FirstOrDefault(e => e.Name.LocalName == "Period");
        if (period is null)
            return DriverHelpers.Unsupported(url, DownloadProtocol.Dash, "MPD 中没有 Period");

        var adaptation = period.Elements().Where(e => e.Name.LocalName == "AdaptationSet")
            .FirstOrDefault(a => MediaType(a).StartsWith("video", StringComparison.Ordinal))
            ?? period.Elements().FirstOrDefault(e => e.Name.LocalName == "AdaptationSet");
        if (adaptation is null)
            return DriverHelpers.Unsupported(url, DownloadProtocol.Dash, "MPD 中没有 AdaptationSet");

        var representation = adaptation.Elements().Where(e => e.Name.LocalName == "Representation")
            .OrderByDescending(r => long.TryParse((string?)r.Attribute("bandwidth"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0)
            .FirstOrDefault();
        if (representation is null)
            return DriverHelpers.Unsupported(url, DownloadProtocol.Dash, "AdaptationSet 中没有 Representation");

        string representationId = (string?)representation.Attribute("id") ?? "representation";
        long bandwidth = long.TryParse((string?)representation.Attribute("bandwidth"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

        string baseUrl = BaseUrlOf(representation) ?? BaseUrlOf(adaptation) ?? BaseUrlOf(period) ?? BaseUrlOf(mpd) ?? url;
        baseUrl = DriverHelpers.ResolveUrl(baseUrl, url);

        var plan = BuildPlan(mpd, period, adaptation, representation, baseUrl, representationId, bandwidth, url, context);
        return plan;
    }

    private ResourceProbe BuildPlan(
        XElement mpd, XElement period, XElement adaptation, XElement representation,
        string baseUrl, string representationId, long bandwidth, string url, DriverContext context)
    {
        var template = ElementOf(representation, "SegmentTemplate")
                       ?? ElementOf(adaptation, "SegmentTemplate")
                       ?? ElementOf(period, "SegmentTemplate");

        if (template is not null)
        {
            var media = (string?)template.Attribute("media");
            if (media is null)
                return DriverHelpers.Unsupported(url, DownloadProtocol.Dash, "SegmentTemplate 缺少 media 模板");

            var initialization = (string?)template.Attribute("initialization") ?? "";
            long timescale = long.TryParse((string?)template.Attribute("timescale"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts) && ts > 0 ? ts : 1;
            long startNumber = long.TryParse((string?)template.Attribute("startNumber"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var sn) ? sn : 1;

            var timeline = ElementOf(template, "SegmentTimeline");
            var segmentUrls = new List<string>();
            long number = 0;

            if (timeline is not null)
            {
                long time = 0;
                foreach (var s in timeline.Elements().Where(e => e.Name.LocalName == "S"))
                {
                    long at = long.TryParse((string?)s.Attribute("t"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : time;
                    long duration = long.TryParse((string?)s.Attribute("d"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 0;
                    long repeat = long.TryParse((string?)s.Attribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0;
                    if (duration <= 0) continue;

                    for (long i = 0; i <= repeat; i++)
                    {
                        segmentUrls.Add(DriverHelpers.ResolveUrl(
                            DriverHelpers.SubstituteTemplate(media, representationId, bandwidth, startNumber + number, at), baseUrl));
                        at += duration;
                        number++;
                    }

                    time = at;
                }
            }
            else if (long.TryParse((string?)template.Attribute("duration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var perSegment) && perSegment > 0)
            {
                long count = 0;
                var presentation = (string?)mpd.Attribute("mediaPresentationDuration");
                if (presentation is not null)
                {
                    try
                    {
                        count = (long)Math.Ceiling(XmlConvert.ToTimeSpan(presentation).TotalSeconds * timescale / perSegment);
                    }
                    catch (FormatException)
                    {
                        count = 0;
                    }
                }

                if (count <= 0)
                    return DriverHelpers.Unsupported(url, DownloadProtocol.Dash, "无法从 MPD 确定分段数量（mediaPresentationDuration 缺失）");

                for (long i = 0; i < count; i++)
                {
                    segmentUrls.Add(DriverHelpers.ResolveUrl(
                        DriverHelpers.SubstituteTemplate(media, representationId, bandwidth, startNumber + i, i * perSegment), baseUrl));
                }

                number = count;
            }
            else
            {
                return DriverHelpers.Unsupported(url, DownloadProtocol.Dash, "SegmentTemplate 缺少 duration / SegmentTimeline");
            }

            if (segmentUrls.Count == 0)
                return DriverHelpers.Unsupported(url, DownloadProtocol.Dash, "DASH 分段列表为空");

            var segments = new List<string>(segmentUrls.Count + 1);
            if (initialization.Length > 0)
                segments.Add(DriverHelpers.ResolveUrl(
                    DriverHelpers.SubstituteTemplate(initialization, representationId, bandwidth, startNumber, 0), baseUrl));
            segments.AddRange(segmentUrls);

            context.Log?.Invoke($"DASH：选择 {representationId}（{bandwidth} bps），{segmentUrls.Count} 个分段");
            return new ResourceProbe(
                DriverHelpers.BaseName(url) + ".mp4", -1, AcceptRanges: false, ETag: null, FinalUrl: url,
                DownloadProtocol.Dash,
                new TransferPlan.SegmentFiles(segments, null));
        }

        var list = ElementOf(representation, "SegmentList")
                   ?? ElementOf(adaptation, "SegmentList")
                   ?? ElementOf(period, "SegmentList");

        if (list is not null)
        {
            var segments = new List<string>();
            var initialization = (string?)ElementOf(list, "Initialization")?.Attribute("sourceURL");
            if (initialization is { Length: > 0 }) segments.Add(DriverHelpers.ResolveUrl(initialization, baseUrl));

            foreach (var segment in list.Elements().Where(e => e.Name.LocalName == "SegmentURL"))
            {
                var media = (string?)segment.Attribute("media") ?? (string?)segment.Attribute("sourceURL");
                if (media is { Length: > 0 }) segments.Add(DriverHelpers.ResolveUrl(media, baseUrl));
            }

            if (segments.Count == 0)
                return DriverHelpers.Unsupported(url, DownloadProtocol.Dash, "SegmentList 为空");

            context.Log?.Invoke($"DASH SegmentList：{segments.Count} 个分段");
            return new ResourceProbe(
                DriverHelpers.BaseName(url) + ".mp4", -1, AcceptRanges: false, ETag: null, FinalUrl: url,
                DownloadProtocol.Dash,
                new TransferPlan.SegmentFiles(segments, null));
        }

        // SegmentBase (single indexed file): the whole resource over byte ranges
        context.Log?.Invoke("DASH：按单文件（SegmentBase）字节范围下载");
        return new ResourceProbe(
            DriverHelpers.BaseName(url) + ".mp4", -1, AcceptRanges: true, ETag: null, FinalUrl: url,
            DownloadProtocol.Dash,
            new TransferPlan.ByteRanges(baseUrl, -1, AcceptRanges: true, Array.Empty<string>()));
    }

    private static string MediaType(XElement adaptation) =>
        (string?)adaptation.Attribute("contentType") ?? (string?)adaptation.Attribute("mimeType") ?? "";

    private static XElement? ElementOf(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? BaseUrlOf(XElement element)
    {
        var value = element.Elements().FirstOrDefault(e => e.Name.LocalName == "BaseURL")?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
