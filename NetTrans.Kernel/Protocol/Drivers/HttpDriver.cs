using System.Net.Http.Headers;
using NetTrans.Kernel.Transfer;

namespace NetTrans.Kernel.Protocol.Drivers;

/// <summary>
/// The workhorse: HTTP(S) probe via HEAD (GET+Range fallback for servers that
/// refuse HEAD), Content-Disposition naming, and byte-range transfer planning.
/// Also the body-sniff hop: a generic URL serving m3u8/mpd content hands the
/// probe to the HLS/DASH driver through <see cref="ResourceProbe.SuggestedProtocol"/>.
/// </summary>
public sealed class HttpDriver : IProtocolDriver
{
    public DownloadProtocol Protocol => DownloadProtocol.Http;

    public async Task<ResourceProbe> ProbeAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        ResourceHead head;
        try
        {
            head = await ProbeWithHeadAsync(url, context, cancellation);
            if (head.Size <= 0) head = await ProbeWithGetAsync(url, context, head, cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            context.Log?.Invoke($"HEAD 探测失败，改用 GET 探测（{DriverHelpers.Brief(error.Message)}）");
            head = await ProbeWithGetAsync(url, context, new ResourceHead { Name = DriverHelpers.GuessName(url) }, cancellation);
        }

        // the body says it's a playlist — hand the probe over
        if (head.Suggested is { } suggested)
        {
            return new ResourceProbe(head.Name, head.Size, head.AcceptRanges, head.ETag, url,
                DownloadProtocol.Http, new TransferPlan.Unsupported("由内容嗅探改走其他协议驱动"), suggested);
        }

        var mirrors = await ResolveMirrorsAsync(url, head, context, cancellation);
        var plan = new TransferPlan.ByteRanges(url, head.Size, head.AcceptRanges, mirrors);

        return new ResourceProbe(head.Name, head.Size, head.AcceptRanges, head.ETag, url, DownloadProtocol.Http, plan);
    }

    private async Task<ResourceHead> ProbeWithHeadAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Head, url);
        DriverHelpers.AddHeaders(message, context.Headers);
        using var response = await context.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();

        return new ResourceHead
        {
            Size = response.Content.Headers.ContentLength ?? -1,
            ETag = response.Headers.ETag?.Tag,
            AcceptRanges = response.Headers.AcceptRanges.Contains("bytes"),
            Name = FileName(response) ?? DriverHelpers.GuessName(url),
            Suggested = Sniff(response.Content.Headers.ContentType?.MediaType),
        };
    }

    private async Task<ResourceHead> ProbeWithGetAsync(string url, DriverContext context, ResourceHead fallback, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        DriverHelpers.AddHeaders(message, context.Headers);
        message.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await context.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();

        bool ranged = (int)response.StatusCode == 206;
        long size = ranged
            ? response.Content.Headers.ContentRange?.Length ?? -1
            : response.Content.Headers.ContentLength ?? -1;

        return new ResourceHead
        {
            Size = size > 0 ? size : fallback.Size,
            ETag = response.Headers.ETag?.Tag ?? fallback.ETag,
            AcceptRanges = ranged || fallback.AcceptRanges,
            Name = FileName(response) ?? fallback.Name,
            Suggested = Sniff(response.Content.Headers.ContentType?.MediaType) ?? fallback.Suggested,
        };
    }

    /// <summary>Mirror slot: probe each candidate; only identical resources join the source set.</summary>
    private async Task<IReadOnlyList<string>> ResolveMirrorsAsync(string url, ResourceHead head, DriverContext context, CancellationToken cancellation)
    {
        var resolver = context.Options.MirrorResolver;
        if (resolver is null || !head.AcceptRanges || head.Size <= 0) return Array.Empty<string>();

        try
        {
            var candidates = await resolver(url, cancellation);
            if (candidates is not { Count: > 0 }) return Array.Empty<string>();

            var valid = await MirrorSourceSet.ValidateAsync(
                candidates, head.Size, head.ETag,
                async (candidate, token) =>
                {
                    using var message = new HttpRequestMessage(HttpMethod.Head, candidate);
                    DriverHelpers.AddHeaders(message, context.Headers);
                    using var response = await context.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    return (response.Content.Headers.ContentLength ?? -1, response.Headers.ETag?.Tag);
                },
                cancellation);

            if (valid.Count > 0) context.Log?.Invoke($"镜像探测：{valid.Count}/{candidates.Count} 个候选源可用");
            return valid;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            context.Log?.Invoke($"镜像探测失败，仅用主源（{DriverHelpers.Brief(error.Message)}）");
            return Array.Empty<string>();
        }
    }

    private static string? FileName(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var raw = disposition?.FileNameStar ?? disposition?.FileName;
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            return Uri.UnescapeDataString(raw.Trim('"'));
        }
        catch
        {
            return raw.Trim('"');
        }
    }

    private static DownloadProtocol? Sniff(string? contentType) => contentType switch
    {
        "application/vnd.apple.mpegurl" or "application/x-mpegurl" => DownloadProtocol.Hls,
        "application/dash+xml" => DownloadProtocol.Dash,
        _ => null,
    };

    private sealed class ResourceHead
    {
        public long Size { get; set; } = -1;
        public string? ETag { get; set; }
        public bool AcceptRanges { get; set; }
        public string Name { get; set; } = "未命名下载";
        public DownloadProtocol? Suggested { get; set; }
    }
}
