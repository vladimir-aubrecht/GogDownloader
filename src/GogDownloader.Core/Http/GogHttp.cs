using System.Net;
using System.Net.Http.Headers;
using GogDownloader.Core.Auth;

namespace GogDownloader.Core.Http;

public sealed record GogHttpOptions
{
    /// <summary>
    /// Concurrent connections per host. GOG rate-limits by request rate, and a game of thousands
    /// of tiny files makes thousands of requests, so more than a few connections trips it and
    /// stalls. Five is the sensible ceiling; the user can force more with --connections.
    /// </summary>
    public int MaxConnections { get; init; } = 5;

    /// <summary>Download speed cap in bytes per second. 0 means unlimited.</summary>
    public long MaxBytesPerSecond { get; init; }

    public int MaxRetries { get; init; } = 5;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Retries a request on transient failures. Honours Retry-After (both 429 and 503),
/// otherwise exponential backoff with jitter.
/// </summary>
public sealed class RetryHandler(int maxRetries) : DelegatingHandler
{
    private static readonly Random Jitter = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, ct);
                if (!IsTransient(response.StatusCode) || attempt >= maxRetries)
                    return response;
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                // connection dropped — fall through to the backoff below
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxRetries)
            {
                // timeout, not a user cancellation
            }

            var delay = response is not null
                ? RetryAfter(response) ?? Backoff(attempt)
                : Backoff(attempt);

            response?.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout
             or HttpStatusCode.TooManyRequests
             or HttpStatusCode.InternalServerError
             or HttpStatusCode.BadGateway
             or HttpStatusCode.ServiceUnavailable
             or HttpStatusCode.GatewayTimeout;

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is { } delta) return delta;
        if (ra?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return wait;
        }
        return null;
    }

    private static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Pow(2, attempt);
        lock (Jitter) seconds += Jitter.NextDouble();
        return TimeSpan.FromSeconds(Math.Min(seconds, 60));
    }
}

/// <summary>Adds the Bearer token and refreshes it once on a 401.</summary>
public sealed class AuthHandler(TokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        token = await tokens.GetAccessTokenAsync(ct, forceRefresh: true);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }
}

public static class GogHttpFactory
{
    public static HttpClient Create(GogHttpOptions options, TokenProvider? tokens = null)
    {
        HttpMessageHandler handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = options.MaxConnections,

            // Deliberately off. Depot chunks and manifests are already compressed and are
            // inflated by hand; metadata is plain JSON the server sends uncompressed when not
            // asked. Requesting transport compression made the CDN gzip the already-compressed
            // chunks and return a Content-Length that did not match the body, so specific files
            // truncated the same way every run ("response ended prematurely, N bytes expected").
            AutomaticDecompression = DecompressionMethods.None,

            PooledConnectionLifetime = TimeSpan.FromMinutes(10),

            // A game of thousands of tiny files makes one request per file. Over HTTP/1.1 each is
            // a request-response on a pooled connection the CDN keeps closing, which crawls and
            // truncates; HTTP/2 multiplexes them all over one connection. Fastly supports it, and
            // this is the difference between minutes and an hour for such games.
            EnableMultipleHttp2Connections = true,
        };

        handler = new RetryHandler(options.MaxRetries) { InnerHandler = handler };
        if (tokens is not null)
            handler = new AuthHandler(tokens) { InnerHandler = handler };

        var client = new HttpClient(handler)
        {
            Timeout = options.Timeout,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GogDownloader/0.1");
        return client;
    }
}
