using System.Text;
using System.Text.Json;
using NetTrans.Kernel.Protocol;
using NetTrans.Kernel.Protocol.Drivers;

namespace NetTrans.Kernel.Sites;

/// <summary>
/// 站点提取器的共用管道：端点覆写、头合并、JSON 取回、短链跟随。
/// 站点无关——站点自己的端点、参数与解析逻辑都留在各自文件里。
/// </summary>
internal static class SiteHttp
{
    /// <summary>站点 API 基址：先取 <see cref="KernelOptions.SiteApiBases"/> 覆写（测试 fixture / 反代加速），否则用站点默认。</summary>
    public static string ResolveBase(DriverContext context, string siteName, string defaultBase)
    {
        var base_ = context.Options.SiteApiBases?.TryGetValue(siteName, out var overridden) == true &&
                    !string.IsNullOrWhiteSpace(overridden)
            ? overridden.Trim()
            : defaultBase;
        return base_.EndsWith('/') ? base_ : base_ + "/";
    }

    /// <summary>任务/默认头打底，站点必需头补齐（同名字头以站点为准）。</summary>
    public static IReadOnlyDictionary<string, string> MergeHeaders(
        IReadOnlyDictionary<string, string> headers, params (string Name, string Value)[] extra)
    {
        var merged = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in extra)
            if (value.Length > 0) merged[name] = value;
        return merged;
    }

    public static async Task<JsonDocument> GetJsonAsync(
        string url, DriverContext context,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null) DriverHelpers.AddHeaders(message, headers);
        using var response = await context.Http.SendAsync(message, cancellation);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
    }

    public static async Task<JsonDocument> PostJsonAsync(
        string url, string body, DriverContext context,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (headers is not null) DriverHelpers.AddHeaders(message, headers);
        using var response = await context.Http.SendAsync(message, cancellation);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
    }

    /// <summary>跟随重定向链，返回最终 URL（b23.tv / youtu.be 一类短链用）。</summary>
    public static async Task<string> FinalUrlAsync(
        string url, DriverContext context,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null) DriverHelpers.AddHeaders(message, headers);
        using var response = await context.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation);
        return response.RequestMessage?.RequestUri?.ToString() ?? url;
    }
}
