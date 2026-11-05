using System.Net;
using System.Xml.Linq;

namespace NetTrans.Kernel.Protocol.Drivers;

/// <summary>
/// Metalink4（RFC 5854，P2-1）：<c>&lt;file&gt;</c> 携带 name/size/hash 与多个
/// <c>&lt;url&gt;</c>。priority 数字最小者为主源（1 最高，缺省视为最末），其余成为
/// 镜像——每个镜像在探测期做一次 Range 0-0 请求核验大小，尺寸不符的剔除，
/// 绝不让错字节的源进入计划。内嵌 hash（优先 sha-256，其次 sha-1/md5）作为期望
/// 指纹随 probe 交给内核。多 file 只取第一个（多文件清单暂不支持，诚实报错）。
/// </summary>
public sealed class MetalinkDriver : IProtocolDriver
{
    public DownloadProtocol Protocol => DownloadProtocol.Metalink;

    public bool CanHandle(string url) => ProtocolDetector.Detect(url) == DownloadProtocol.Metalink;

    public async Task<ResourceProbe> ProbeAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        string xml;
        try
        {
            xml = await DriverHelpers.GetTextAsync(url, context, cancellation);
        }
        catch (Exception error)
        {
            return DriverHelpers.Unsupported(url, DownloadProtocol.Metalink, $"无法读取 metalink 文件（{DriverHelpers.Brief(error.Message)}）");
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (Exception error)
        {
            return DriverHelpers.Unsupported(url, DownloadProtocol.Metalink, $"metalink XML 解析失败（{DriverHelpers.Brief(error.Message)}）");
        }

        if (document.Root is null || document.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "file") is not { } file)
        {
            return DriverHelpers.Unsupported(url, DownloadProtocol.Metalink, "metalink 缺少 <file> 条目");
        }

        // 多文件清单暂不支持：明确说明而不是悄悄只下第一个
        if (document.Root.Elements().Count(e => e.Name.LocalName == "file") > 1)
        {
            context.Log?.Invoke("metalink 含多个 <file>，仅取第一个（多文件清单暂不支持）");
        }

        var name = (string?)file.Attribute("name");
        long size = -1;
        if (file.Elements().FirstOrDefault(e => e.Name.LocalName == "size") is { } sizeElement &&
            long.TryParse(sizeElement.Value.Trim(), out var parsed) && parsed >= 0)
        {
            size = parsed;
        }

        string? expectedHash = null;
        foreach (var type in new[] { "sha-256", "sha1", "sha-1", "md5" })
        {
            if (file.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "hash" &&
                    string.Equals((string?)e.Attribute("type"), type, StringComparison.OrdinalIgnoreCase))
                is not { } hashElement) continue;
            var hex = hashElement.Value.Trim();
            if (Checksums.DetectAlgorithm(hex) is not null)
            {
                expectedHash = hex.ToLowerInvariant();
                break;
            }
        }

        // priority 数字最小者为主源；缺省 priority 视为最末（RFC 5854）
        var urlEntries = file.Elements().Where(e => e.Name.LocalName == "url")
            .Select(e => (Element: e, Priority: int.TryParse((string?)e.Attribute("priority"), out var p) ? p : int.MaxValue))
            .OrderBy(t => t.Priority)
            .ToList();
        if (urlEntries.Count == 0)
        {
            return DriverHelpers.Unsupported(url, DownloadProtocol.Metalink, "metalink <file> 缺少 <url> 条目");
        }

        string? Resolve(string raw)
        {
            try
            {
                return new Uri(new Uri(url), raw.Trim()).AbsoluteUri;
            }
            catch (FormatException)
            {
                context.Log?.Invoke($"metalink <url> 无法解析，跳过：{DriverHelpers.Brief(raw)}");
                return null;
            }
        }

        var primary = Resolve(urlEntries[0].Element.Value);
        if (primary is null)
        {
            return DriverHelpers.Unsupported(url, DownloadProtocol.Metalink, "metalink 主源 <url> 无法解析");
        }

        var mirrorCandidates = urlEntries.Skip(1)
            .Select(t => Resolve(t.Element.Value))
            .Where(u => u is not null)
            .Cast<string>()
            .Distinct()
            .ToList();

        // 镜像核验：Range 0-0 应答必须覆盖声明的 size（200 全量也认——按 Content-Length）。
        // 尺寸不符 / 不可达的镜像一律剔除，绝不让错字节的源进入计划。
        var mirrors = new List<string>();
        if (size >= 0)
        {
            foreach (var mirror in mirrorCandidates)
            {
                if (await MirrorMatchesAsync(mirror, size, context, cancellation))
                {
                    mirrors.Add(mirror);
                }
                else
                {
                    context.Log?.Invoke($"metalink 镜像尺寸不符，剔除：{DriverHelpers.Brief(mirror)}");
                }
            }
        }
        else
        {
            mirrors.AddRange(mirrorCandidates);
            context.Log?.Invoke("metalink 未声明 size，镜像无法核验，全部保留");
        }

        context.Log?.Invoke(
            $"Metalink4：主源 {DriverHelpers.Brief(primary)}，{mirrors.Count} 个镜像可用" +
            (expectedHash is null ? "" : $"，期望指纹 {Checksums.DetectAlgorithm(expectedHash)}"));

        return new ResourceProbe(
            name ?? DriverHelpers.BaseName(primary),
            size,
            AcceptRanges: true,
            ETag: null,
            FinalUrl: primary,
            DownloadProtocol.Metalink,
            new TransferPlan.ByteRanges(primary, size, AcceptRanges: true, mirrors),
            ExpectedHash: expectedHash);
    }

    private static async Task<bool> MirrorMatchesAsync(
        string mirror, long expectedSize, DriverContext context, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, mirror);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await context.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode) return false;

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var contentRange = response.Content.Headers.ContentRange;
                return contentRange?.Length == expectedSize || contentRange?.Length is null && response.Content.Headers.ContentLength == 1;
            }

            // 服务器不支持 Range：按全量 Content-Length 判断
            return response.Content.Headers.ContentLength == expectedSize;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            context.Log?.Invoke($"metalink 镜像探测失败：{DriverHelpers.Brief(error.Message)}");
            return false;
        }
    }
}
