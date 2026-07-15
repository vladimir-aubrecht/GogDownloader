namespace GogDownloader.Core.Content;

/// <summary>
/// A body read that gives up when the connection goes quiet.
///
/// Chunks are fetched with <see cref="System.Net.Http.HttpCompletionOption.ResponseHeadersRead"/>,
/// and <see cref="System.Net.Http.HttpClient.Timeout"/> only covers receiving the headers under
/// that option — reading the response body is unbounded. So a CDN that sends headers and then
/// stops streaming hangs the read forever. Aborting a read that delivers nothing for a while
/// turns that hang into a transient failure the caller's retry recovers from.
/// </summary>
public static class StreamRead
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    public static ValueTask<int> WithStallTimeoutAsync(this Stream stream, Memory<byte> buffer, CancellationToken ct) =>
        stream.WithStallTimeoutAsync(buffer, StallTimeout, ct);

    public static async ValueTask<int> WithStallTimeoutAsync(
        this Stream stream, Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(timeout);

        try
        {
            return await stream.ReadAsync(buffer, stall.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The stall fired, not the user's Ctrl+C. An IOException so the retry treats it as
            // the transient network failure it is.
            throw new IOException($"The connection stalled for {timeout.TotalSeconds:F0}s.");
        }
    }
}
