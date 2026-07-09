using System.Diagnostics;

namespace GogDownloader.Core.Content;

/// <summary>
/// Token bucket sdílený všemi spojeními. `bytesPerSecond <= 0` limit vypíná.
/// </summary>
public sealed class RateLimiter(long bytesPerSecond)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastTicks = Stopwatch.GetTimestamp();

    /// <summary>Kbelík startuje plný, jinak by i první čtení čekalo celou sekundu.</summary>
    private double _tokens = bytesPerSecond > 0 ? bytesPerSecond : 0;

    public bool Enabled => bytesPerSecond > 0;

    public async ValueTask ConsumeAsync(int bytes, CancellationToken ct)
    {
        if (!Enabled) return;

        await _gate.WaitAsync(ct);
        try
        {
            // Kbelík musí pojmout i požadavek větší než sekundový rozpočet, jinak
            // by se na něj čekalo věčně.
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
