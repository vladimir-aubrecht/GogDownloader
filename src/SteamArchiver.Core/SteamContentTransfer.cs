using GogArchiver.Core.Content;

namespace SteamArchiver.Core;

/// <summary>Keep user-requested pacing outside SteamKit2's network response deadlines.</summary>
internal sealed class SteamContentTransfer(long bytesPerSecond, ContentHttpContext context)
{
    private readonly RateLimiter _limiter = new(bytesPerSecond);

    public async Task<T> ChunkAsync<T>(int bytes, Func<Task<T>> transfer, CancellationToken ct)
    {
        await _limiter.ConsumeAsync(bytes, ct);
        ct.ThrowIfCancellationRequested();
        var previous = context.RequestCancellation.Value;
        context.RequestCancellation.Value = ct;
        try { return await transfer(); }
        finally { context.RequestCancellation.Value = previous; }
    }

    public async Task<T> ManifestAsync<T>(Func<Task<T>> transfer, CancellationToken ct)
    {
        long received = 0;
        var previousCounter = context.CountBytes.Value;
        var previousCancellation = context.RequestCancellation.Value;
        context.CountBytes.Value = count => Interlocked.Add(ref received, count);
        context.RequestCancellation.Value = ct;
        try { return await transfer(); }
        finally
        {
            context.CountBytes.Value = previousCounter;
            context.RequestCancellation.Value = previousCancellation;
            // Manifest wire size is only known after HTTP completes; account for retries too.
            while (received > 0)
            {
                var count = (int)Math.Min(received, int.MaxValue);
                await _limiter.ConsumeAsync(count, ct);
                received -= count;
            }
        }
    }
}
