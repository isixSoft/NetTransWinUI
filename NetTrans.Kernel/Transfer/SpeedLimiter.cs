using System.Diagnostics;

namespace NetTrans.Kernel.Transfer;

/// <summary>
/// Token-bucket speed cap, one per task. The global cap lives in a
/// kernel-wide shared limiter (see <see cref="TransferRequest.GlobalLimiter"/>)
/// so N concurrent tasks together stay under the global value; waits chain
/// global-then-task. Waits are sliced into short delays so a live SetLimit
/// takes effect fast. A null/zero cap means unlimited and never delays.
/// </summary>
public sealed class SpeedLimiter
{
    private const long BucketFloor = 64 * 1024; // burst allowance when the cap is tiny

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _limit;
    private double _tokens;
    private double _lastRefill;

    public SpeedLimiter(long? bytesPerSecond = null)
    {
        _limit = Math.Max(0, bytesPerSecond ?? 0);
        _lastRefill = _clock.Elapsed.TotalSeconds;
    }

    /// <summary>bytes/s, or null/0 for unlimited. Safe to call mid-transfer.</summary>
    public void SetLimit(long? bytesPerSecond)
    {
        lock (_gate)
        {
            _limit = Math.Max(0, bytesPerSecond ?? 0);
        }
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken cancellation = default)
    {
        while (true)
        {
            int delayMs;
            lock (_gate)
            {
                Refill();
                if (_limit <= 0)
                {
                    _tokens = 0;
                    return;
                }

                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }

                // short of tokens: wait exactly as long as the shortfall needs,
                // then re-check (tokens are only consumed on grant, never per retry)
                delayMs = (int)Math.Ceiling((bytes - _tokens) * 1000.0 / _limit);
            }

            // cap the slice so limit changes and cancellation stay responsive
            await Task.Delay(Math.Clamp(delayMs, 1, 250), cancellation);
        }
    }

    private void Refill()
    {
        var now = _clock.Elapsed.TotalSeconds;
        _tokens += (now - _lastRefill) * _limit;
        _lastRefill = now;
        _tokens = Math.Min(_tokens, Math.Max(BucketFloor, _limit));
    }
}
