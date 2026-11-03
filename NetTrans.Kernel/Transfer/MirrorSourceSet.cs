namespace NetTrans.Kernel.Transfer;

/// <summary>
/// The set of URLs serving one identical resource: the primary plus validated
/// mirrors. Segment fetches pick sources round-robin and demote a source
/// after a failure — which is what makes GitHub / HuggingFace-style mirror
/// acceleration transparent to the rest of the kernel.
/// </summary>
public sealed class MirrorSourceSet
{
    private readonly object _gate = new();
    private readonly List<string> _sources;
    private int _cursor;

    public MirrorSourceSet(string primary, IReadOnlyList<string>? mirrors)
    {
        _sources = [primary];
        if (mirrors is { Count: > 0 })
        {
            foreach (var mirror in mirrors)
            {
                if (mirror != primary && !_sources.Contains(mirror)) _sources.Add(mirror);
            }
        }
    }

    public int Count
    {
        get { lock (_gate) return _sources.Count; }
    }

    /// <summary>Next source, round-robin, so concurrent segments spread across mirrors.</summary>
    public string Next()
    {
        lock (_gate)
        {
            var source = _sources[_cursor % _sources.Count];
            _cursor++;
            return source;
        }
    }

    /// <summary>Move a failing source to the back of the rotation.</summary>
    public void ReportFailure(string source)
    {
        lock (_gate)
        {
            if (_sources.Count < 2 || !_sources.Remove(source)) return;
            _sources.Add(source);
        }
    }

    /// <summary>
    /// Probe candidates against the primary's identity: keep the ones serving
    /// the same size with an agreeing etag. One unusable mirror is skipped,
    /// never fatal.
    /// </summary>
    public static async Task<List<string>> ValidateAsync(
        IReadOnlyList<string> candidates,
        long size,
        string? etag,
        Func<string, CancellationToken, Task<(long Size, string? ETag)>> probe,
        CancellationToken cancellation)
    {
        var valid = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                var (candidateSize, candidateEtag) = await probe(candidate, timeout.Token);
                if (candidateSize == size && (etag is null || candidateEtag is null || candidateEtag == etag))
                {
                    valid.Add(candidate);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // mirror unusable — skip
            }
        }

        return valid;
    }
}
