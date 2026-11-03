namespace NetTrans.Kernel.Transfer;

/// <summary>
/// One byte-range piece of a ranged transfer: the file span [Start, End)
/// with Done bytes already written. End is <see cref="long.MaxValue"/> when
/// the server never told us the size (pure streaming).
/// </summary>
public sealed class RangeSegment
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Done { get; set; }

    public long Remaining => Math.Max(0, End - Start - Done);
    public bool IsComplete => End != long.MaxValue && Done >= End - Start;
}

/// <summary>
/// The segment layout of a ranged transfer plus its checkpoint round-trip —
/// this is what makes 断点续传 survive process restarts. Splitting over-splits
/// by 4x (capped) so a fast connection picks up the next piece instead of
/// idling, but never below the 1 MiB piece floor.
/// </summary>
public sealed class SegmentTable
{
    public const long MinPiece = 1024 * 1024;

    public required RangeSegment[] Segments { get; init; }
    public required long Size { get; init; }
    public required string? ETag { get; init; }
    public required string Url { get; init; }

    /// <summary>True when this table came from a checkpoint rather than a fresh split.</summary>
    public bool Restored { get; set; }

    public long DoneBytes => Segments.Sum(s => s.Done);

    public static SegmentTable Split(string url, long size, string? etag, int connections)
    {
        if (size <= 0)
        {
            return new SegmentTable
            {
                Segments = [new RangeSegment { Start = 0, End = long.MaxValue }],
                Size = size,
                ETag = etag,
                Url = url,
            };
        }

        connections = Math.Clamp(connections, 1, 64);
        int pieces = Math.Min(64, connections * 4);          // over-split for load balance
        pieces = (int)Math.Min(pieces, size / MinPiece);     // never below the 1 MiB floor
        pieces = Math.Max(1, pieces);

        var segments = new RangeSegment[pieces];
        long piece = size / pieces;
        long extra = size % pieces;
        long at = 0;
        for (int i = 0; i < pieces; i++)
        {
            long length = piece + (i < extra ? 1 : 0);
            segments[i] = new RangeSegment { Start = at, End = at + length };
            at += length;
        }

        return new SegmentTable { Segments = segments, Size = size, ETag = etag, Url = url };
    }

    public TransferCheckpoint ToCheckpoint() => new()
    {
        Kind = "ranges",
        Url = Url,
        ETag = ETag,
        Size = Size,
        Start = Segments.Select(s => s.Start).ToArray(),
        End = Segments.Select(s => s.End).ToArray(),
        Done = Segments.Select(s => s.Done).ToArray(),
    };

    public static SegmentTable? Restore(TransferCheckpoint checkpoint)
    {
        if (checkpoint.Kind != "ranges" ||
            checkpoint.Start.Length == 0 ||
            checkpoint.Start.Length != checkpoint.End.Length ||
            checkpoint.End.Length != checkpoint.Done.Length)
        {
            return null;
        }

        var segments = Enumerable.Range(0, checkpoint.Start.Length)
            .Select(i => new RangeSegment
            {
                Start = checkpoint.Start[i],
                End = checkpoint.End[i],
                Done = Math.Clamp(checkpoint.Done[i], 0, checkpoint.End[i] - checkpoint.Start[i]),
            })
            .ToArray();

        return new SegmentTable
        {
            Segments = segments,
            Size = checkpoint.Size,
            ETag = checkpoint.ETag,
            Url = checkpoint.Url,
        };
    }
}
