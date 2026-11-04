using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NetTrans.Kernel;

/// <summary>校验文件解析出的一条指纹。</summary>
/// <param name="FileName">清单内的文件名（/ 分隔，剥去 ./ 与 * 前缀）。</param>
/// <param name="Algorithm">"MD5" / "SHA-1" / "SHA-256"。</param>
/// <param name="Hash">大写十六进制指纹。</param>
public sealed record ChecksumEntry(string FileName, string Algorithm, string Hash);

/// <summary>
/// 校验和工具（Ubuntu SHA256SUMS 模式）：
/// ① 十六进制串自动判别算法（32=MD5、40=SHA-1、64=SHA-256）；
/// ② 解析校验清单文件（GNU coreutils「&lt;hash&gt; [*]文件名」与 BSD「MD5 (文件) = &lt;hash&gt;」两种行格式）；
/// ③ 按算法计算文件指纹。
/// 全部纯函数，便于单测。
/// </summary>
public static partial class Checksums
{
    /// <summary>按长度+字符集判别算法；不认识返回 null。</summary>
    public static string? DetectAlgorithm(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var text = hex.Trim();
        var isHex = text.All(Uri.IsHexDigit);
        return text.Length switch
        {
            32 when isHex => "MD5",
            40 when isHex => "SHA-1",
            64 when isHex => "SHA-256",
            _ => null,
        };
    }

    /// <summary>计算文件指纹，返回大写十六进制；algorithm 不认识时按 SHA-256 处理。</summary>
    public static string Compute(string algorithm, string path) => algorithm.ToUpperInvariant() switch
    {
        "MD5" => Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))),
        "SHA-1" => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path))),
        _ => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
    };

    /// <summary>
    /// 解析校验清单内容（SHA256SUMS / MD5SUMS / sha256sum.txt …）。
    /// 忽略空行与 # 注释；无法识别的行跳过；同文件名后行覆盖前行。
    /// </summary>
    public static List<ChecksumEntry> ParseChecksumFile(string text)
    {
        var byName = new Dictionary<string, ChecksumEntry>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            // BSD 风格：MD5 (filename) = hex
            var bsd = BsdLine().Match(line);
            if (bsd.Success)
            {
                var algorithm = bsd.Groups[1].Value.ToUpperInvariant() switch
                {
                    "MD5" => "MD5",
                    "SHA1" => "SHA-1",
                    _ => "SHA-256",
                };
                byName[NormalizeName(bsd.Groups[2].Value)] =
                    new ChecksumEntry(NormalizeName(bsd.Groups[2].Value), algorithm, bsd.Groups[3].Value.ToUpperInvariant());
                continue;
            }

            // GNU 风格：<hash> [*]<filename>
            var gnu = GnuLine().Match(line);
            if (gnu.Success && DetectAlgorithm(gnu.Groups[1].Value) is { } detected)
            {
                byName[NormalizeName(gnu.Groups[2].Value)] =
                    new ChecksumEntry(NormalizeName(gnu.Groups[2].Value), detected, gnu.Groups[1].Value.ToUpperInvariant());
            }
        }
        return byName.Values.ToList();
    }

    /// <summary>剥 * 二进制标记、./ 前缀，统一 / 分隔 —— 与清单文件常见写法对齐。</summary>
    private static string NormalizeName(string name)
    {
        var text = name.Trim().TrimStart('*');
        while (text.StartsWith("./")) text = text[2..];
        return text.Replace('\\', '/');
    }

    [GeneratedRegex(@"^(MD5|SHA1|SHA256)\s*\((.+)\)\s*=\s*([0-9A-Fa-f]+)\s*$")]
    private static partial Regex BsdLine();

    [GeneratedRegex(@"^([0-9A-Fa-f]{32}|[0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})\s+\*?(.+)$")]
    private static partial Regex GnuLine();
}
