using System.Diagnostics;

namespace GogDownloader.Core.Content;

/// <summary>
/// A token bucket shared by all connections. `bytesPerSecond <= 0` disables the limit.
/// </summary>
public sealed class RateLimiter(long bytesPerSecond)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastTicks = Stopwatch.GetTimestamp();

    /// <summary>The bucket starts full, otherwise even the first read would wait a whole second.</summary>
    private double _tokens = bytesPerSecond > 0 ? bytesPerSecond : 0;

    public bool Enabled => bytesPerSecond > 0;

    public async ValueTask ConsumeAsync(int bytes, CancellationToken ct)
    {
        if (!Enabled) return;

        await _gate.WaitAsync(ct);
        try
        {
            // The bucket must hold a request larger than the per-second budget, otherwise
            // it would be waited on forever.
            var capacity = Math.Max(bytesPerSecond, bytes);

            while (true)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
                _lastTicks = now;
                _tokens = Math.Min(capacity, _tokens + elapsed * bytesPerSecond);

                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }

                var deficitSeconds = (bytes - _tokens) / bytesPerSecond;
                await Task.Delay(TimeSpan.FromSeconds(deficitSeconds), ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
