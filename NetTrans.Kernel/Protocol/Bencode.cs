namespace NetTrans.Kernel.Protocol;

/// <summary>
/// Minimal bencode decoder, enough for .torrent metadata: the info dict's
/// name / length / files. Binary fields (pieces, hashes) are not needed and
/// are decoded lossily as UTF-8 — never round-tripped.
/// </summary>
public static class Bencode
{
    public static Dictionary<string, object?>? DecodeTorrent(byte[] data)
    {
        try
        {
            int pos = 0;
            return Decode(data, ref pos) as Dictionary<string, object?>;
        }
        catch
        {
            return null;
        }
    }

    public static string? Text(object? value) => value as string;

    public static long? Number(object? value) => value as long?;

    private static object? Decode(byte[] data, ref int pos)
    {
        return data[pos] switch
        {
            (byte)'i' => DecodeInteger(data, ref pos),
            (byte)'l' => DecodeList(data, ref pos),
            (byte)'d' => DecodeDictionary(data, ref pos),
            _ => DecodeBytes(data, ref pos),
        };
    }

    private static long DecodeInteger(byte[] data, ref int pos)
    {
        pos++; // 'i'
        int end = Array.IndexOf(data, (byte)'e', pos);
        if (end < 0) throw new FormatException("unterminated integer");
        var value = long.Parse(System.Text.Encoding.ASCII.GetString(data, pos, end - pos));
        pos = end + 1;
        return value;
    }

    private static List<object?> DecodeList(byte[] data, ref int pos)
    {
        pos++; // 'l'
        var list = new List<object?>();
        while (data[pos] != (byte)'e') list.Add(Decode(data, ref pos));
        pos++;
        return list;
    }

    private static Dictionary<string, object?> DecodeDictionary(byte[] data, ref int pos)
    {
        pos++; // 'd'
        var dict = new Dictionary<string, object?>();
        while (data[pos] != (byte)'e')
        {
            var key = DecodeBytes(data, ref pos);
            dict[key] = Decode(data, ref pos);
        }
        pos++;
        return dict;
    }

    private static string DecodeBytes(byte[] data, ref int pos)
    {
        int colon = Array.IndexOf(data, (byte)':', pos);
        if (colon < 0) throw new FormatException("unterminated string length");
        int length = int.Parse(System.Text.Encoding.ASCII.GetString(data, pos, colon - pos));
        pos = colon + 1;
        var value = System.Text.Encoding.UTF8.GetString(data, pos, length);
        pos += length;
        return value;
    }
}
