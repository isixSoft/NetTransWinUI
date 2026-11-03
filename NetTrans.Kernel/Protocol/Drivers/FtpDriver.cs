using System.Net;

namespace NetTrans.Kernel.Protocol.Drivers;

/// <summary>
/// FTP / FTPS probe and plan. Size comes from GetFileSize; the transfer is a
/// single binary stream — FTP REST resume semantics vary too much between
/// servers to promise byte ranges (Ghost Downloader's aioftp path makes the
/// same trade).
/// </summary>
public sealed class FtpDriver : IProtocolDriver
{
    public DownloadProtocol Protocol => DownloadProtocol.Ftp;

    public Task<ResourceProbe> ProbeAsync(string url, DriverContext context, CancellationToken cancellation)
    {
        return Task.Run(() =>
        {
            var uri = new Uri(url);
            bool secure = uri.Scheme == "ftps";
            long size = TryGetSize(uri, secure, context);
            string name = DriverHelpers.GuessName(url);

            if (size > 0)
                context.Log?.Invoke($"FTP{(secure ? "S" : string.Empty)} 探测完成：{size} 字节");
            else
                context.Log?.Invoke("FTP 服务器未报告文件大小，将按未知大小流式下载");

            return new ResourceProbe(
                name, size, AcceptRanges: false, ETag: null, FinalUrl: url,
                DownloadProtocol.Ftp,
                new TransferPlan.FtpFile(url, size));
        }, cancellation);
    }

    private long TryGetSize(Uri uri, bool secure, DriverContext context)
    {
        try
        {
#pragma warning disable SYSLIB0014
            var request = (FtpWebRequest)WebRequest.Create(SchemeSafeUri(uri));
            request.Method = WebRequestMethods.Ftp.GetFileSize;
            request.UseBinary = true;
            request.UsePassive = true;
            request.EnableSsl = secure;
            request.Timeout = 15000;
            if (context.Options.Proxy is { } proxy && proxy.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                request.Proxy = new System.Net.WebProxy(proxy);
#pragma warning restore SYSLIB0014

            using var response = (System.Net.FtpWebResponse)request.GetResponse();
            return response.ContentLength;
        }
        catch (Exception error)
        {
            context.Log?.Invoke($"FTP 大小探测失败（{DriverHelpers.Brief(error.Message)}）");
            return -1;
        }
    }

    /// <summary>FtpWebRequest registers only ftp://; ftps rides on it with EnableSsl.</summary>
    private static Uri SchemeSafeUri(Uri uri)
    {
        if (uri.Scheme != "ftps") return uri;
        var builder = new UriBuilder(uri) { Scheme = "ftp" };
        return builder.Uri;
    }
}
