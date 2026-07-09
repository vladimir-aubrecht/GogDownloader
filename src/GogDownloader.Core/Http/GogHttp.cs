using System.Net;
using System.Net.Http.Headers;
using GogDownloader.Core.Auth;

namespace GogDownloader.Core.Http;

public sealed record GogHttpOptions
{
    /// <summary>Kolik souběžných spojení na jeden host. GOG rate-limituje, nepřehánět.</summary>
    public int MaxConnections { get; init; } = 8;

    /// <summary>Strop rychlosti stahování v bajtech za sekundu. 0 = bez omezení.</summary>
    public long MaxBytesPerSecond { get; init; }

    public int MaxRetries { get; init; } = 5;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Opakuje požadavek při přechodných chybách. Respektuje Retry-After (429 i 503),
/// jinak exponenciální backoff s jitterem.
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
                // spadlo spojení — spadneme do backoffu níž
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxRetries)
            {
                // timeout, ne zrušení uživatelem
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

/// <summary>Přidá Bearer token a při 401 ho jednou obnoví.</summary>
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
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        handler = new RetryHandler(options.MaxRetries) { InnerHandler = handler };
        if (tokens is not null)
            handler = new AuthHandler(tokens) { InnerHandler = handler };

        var client = new HttpClient(handler) { Timeout = options.Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GogDownloader/0.1");
        return client;
    }
}
