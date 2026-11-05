using System.Buffers.Binary;
using System.Text;

namespace NetTrans.Kernel.Protocol;

/// <summary>
/// 最小 ZIP central directory 解析器（P2-5 ZIP 部分下载）。只支持经典
/// ZIP（EOCD + central directory entries）；Zip64 大文件不做——部分下载
/// 面向的是「挑几个成员」的普通压缩包。字段布局见 PKWARE APPNOTE。
/// </summary>
public static class ZipIndex
{
    /// <summary>EOCD 定位结果；offset/size 相对整个 ZIP 文件。</summary>
    public readonly record struct ZipEndRecord(int CdOffset, int CdSize, int EntryCount);

    /// <summary>Central directory 里的一个成员。</summary>
    public sealed record ZipEntry(
        string Name,
        ushort Method,
        long CompressedSize,
        long UncompressedSize,
        long Crc32,
        long LocalHeaderOffset);

    private const uint EndSignature = 0x06054b50;   // "PK\x05\x06"
    private const uint EntrySignature = 0x02014b50; // "PK\x01\x02"
    private const int EndFixedLength = 22;

    /// <summary>
    /// 在文件尾部切片中定位 EOCD（从后往前扫，容忍最长 64KB 的 ZIP 注释
    /// 与巧合的伪签名——伪签名会被注释长度一致性检查排除）。
    /// </summary>
    public static bool TryLocateEndRecord(byte[] tail, out ZipEndRecord record)
    {
        record = default;
        for (int i = tail.Length - EndFixedLength; i >= 0 && i >= tail.Length - EndFixedLength - 65_536; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != EndSignature) continue;

            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20));
            if (i + EndFixedLength + commentLen != tail.Length) continue; // EOCD 必须顶着切片末尾

            record = new ZipEndRecord(
                (int)BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 12)),
                BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 10)));
            return true;
        }
        return false;
    }

    /// <summary>解析 central directory 连续区间，返回 <paramref name="entryCount"/> 个成员。</summary>
    public static IReadOnlyList<ZipEntry> Read(ReadOnlySpan<byte> centralDirectory, int entryCount)
    {
        var entries = new List<ZipEntry>(entryCount);
        int pos = 0;
        for (int i = 0; i < entryCount; i++)
        {
            if (pos + 46 > centralDirectory.Length ||
                BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.Slice(pos)) != EntrySignature)
                break;

            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory.Slice(pos + 10));
            long crc = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.Slice(pos + 16));
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.Slice(pos + 20));
            long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.Slice(pos + 24));
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory.Slice(pos + 28));
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory.Slice(pos + 30));
            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory.Slice(pos + 32));
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.Slice(pos + 42));
            string name = Encoding.UTF8.GetString(centralDirectory.Slice(pos + 46, nameLen));

            entries.Add(new ZipEntry(name, method, compressed, uncompressed, crc, localOffset));
            pos += 46 + nameLen + extraLen + commentLen;
        }
        return entries;
    }
}
