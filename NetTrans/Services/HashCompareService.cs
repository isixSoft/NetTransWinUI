using System.Security.Cryptography;

namespace NetTrans.Services;

/// <summary>比对结果（P2-7 校验窗）：算法、期望值与实际值都展示。</summary>
public sealed record HashMatch(bool Matches, string Algorithm, string Expected, string Actual);

/// <summary>
/// 哈希展示与手动比对（P2-7）：检查器/完成窗计算文件的 MD5/SHA-1/SHA-256
/// 三值；用户粘贴期望哈希即时比对——按长度识别算法（32/40/64 hex），输入
/// 清洗大小写与空白，无法识别返回 null（界面如实提示，不猜测）。
/// </summary>
public static class HashCompareService
{
    /// <summary>流式计算文件三种哈希（逐算法独立重读，统一小写展示）。</summary>
    public static (string Md5, string Sha1, string Sha256) Compute(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        string md5 = Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        string sha1 = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        string sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return (md5, sha1, sha256);
    }

    /// <summary>粘贴哈希 vs 实际哈希；长度或字符无法识别 → null。</summary>
    public static HashMatch? Verify(string? pasted, string actual)
    {
        string expected = Clean(pasted);
        string algorithm = expected.Length switch
        {
            32 => "MD5",
            40 => "SHA-1",
            64 => "SHA-256",
            _ => "",
        };
        if (algorithm.Length == 0 || !expected.All(Uri.IsHexDigit)) return null;

        string actualClean = Clean(actual).ToLowerInvariant();
        return new HashMatch(
            string.Equals(expected, actualClean, StringComparison.OrdinalIgnoreCase),
            algorithm,
            expected.ToLowerInvariant(),
            actualClean);
    }

    private static string Clean(string? value) =>
        new((value ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
}
