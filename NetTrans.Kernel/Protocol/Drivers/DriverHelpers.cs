using System.Globalization;
using System.Text.RegularExpressions;

namespace NetTrans.Kernel.Protocol.Drivers;

/// <summary>Small helpers shared by every driver: name guessing, URL resolution, log-friendly errors.</summary>
public static class DriverHelpers
{
    public static string Brief(string message) => message.Length <= 60 ? message : message[..57] + "…";

    public static ResourceProbe Unsupported(string url, DownloadProtocol protocol, string reason) =>
        new(GuessName(url), -1, false, null, url, protocol,
            new TransferPlan.Unsupported(reason));

    /// <summary>Best-effort file name from a URL path (query/fragment stripped).</summary>
    public static string GuessName(string url)
    {
        string path = url;
        int cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];
        path = path.TrimEnd('/');
        int slash = path.LastIndexOf('/');
        string name = slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
        try
        {
            name = Uri.UnescapeDataString(name);
        }
        catch
        {
            // keep the raw segment
        }

        return name.Length == 0 ? "未命名下载" : name;
    }

    public static string BaseName(string url) =>
        Path.GetFileNameWithoutExtension(GuessName(url)) is { Length: > 0 } stem ? stem : "download";

    /// <summary>Resolve a (possibly relative) reference against a base URL.</summary>
    public static string ResolveUrl(string reference, string baseUrl)
    {
        if (reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return reference;
        }

        try
        {
            return new Uri(new Uri(baseUrl), reference).ToString();
        }
        catch
        {
            return reference;
        }
    }

    /// <summary>DASH $…$ template substitution, including $Number%05d$ formatting.</summary>
    public static string SubstituteTemplate(
        string pattern, string representationId, long bandwidth, long number, long time)
    {
        var result = pattern
            .Replace("$RepresentationID$", representationId)
            .Replace("$Bandwidth$", bandwidth.ToString(CultureInfo.InvariantCulture))
            .Replace("$Time$", time.ToString(CultureInfo.InvariantCulture));

        result = Regex.Replace(
            result, @"\$(?:Number|Time)%0?(\d+)d\$",
            match => number.ToString("D" + match.Groups[1].Value, CultureInfo.InvariantCulture));

        return result.Replace("$Number$", number.ToString(CultureInfo.InvariantCulture));
    }

    public static void AddHeaders(HttpRequestMessage message, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
            message.Headers.TryAddWithoutValidation(name, value);
    }
}
