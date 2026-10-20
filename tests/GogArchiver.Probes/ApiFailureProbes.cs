using System.Net;
using GogArchiver.Core;
using GogArchiver.Core.Api;
using GogArchiver.Core.Http;
using GogArchiver.Core.Library;

namespace GogArchiver.Probes;

public static class ApiFailureProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Build discovery distinguishes missing depots from API failures");
        foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.InternalServerError,
                     HttpStatusCode.TooManyRequests, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden,
                     HttpStatusCode.BadRequest })
        {
            var calls = 0;
            using var http = new HttpClient(new Handler(_ => { calls++; return new(status); }));
            HttpStatusCode? failure = null;
            try { await new GogApiClient(http).GetLatestBuildAsync(1, GamePlatform.Windows); }
            catch (HttpRequestException e) { failure = e.StatusCode; }
            Probe.Check($"HTTP {(int)status} is an error, not a reason to choose a legacy depot or installer",
                failure == status && calls == 1);
        }

        foreach (var first in new[] { HttpStatusCode.OK, HttpStatusCode.NotFound })
        {
            var calls = 0;
            using var http = new HttpClient(new Handler(_ => ++calls == 1
                ? Reply(first, "{\"items\":[]}") : Reply(HttpStatusCode.OK, BuildJson)));
            Probe.Check($"{first}: an absent generation 2 depot still falls back to generation 1",
                (await new GogApiClient(http).GetLatestBuildAsync(1, GamePlatform.Windows))?.BuildId == "build" && calls == 2);
        }
        using (var http = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound))))
            Probe.Check("missing depots in both generations return no build",
                await new GogApiClient(http).GetLatestBuildAsync(1, GamePlatform.Windows) is null);

        var attempts = 0;
        using (var http = new HttpClient(new RetryHandler(1) { InnerHandler = new Handler(_ =>
                   ++attempts == 1 ? new(HttpStatusCode.ServiceUnavailable) : Reply(HttpStatusCode.OK, BuildJson)) }))
            Probe.Check("the HTTP retry handler recovers a temporary build outage before selecting a depot",
                (await new GogApiClient(http).GetLatestBuildAsync(1, GamePlatform.Windows))?.BuildId == "build" && attempts == 2);

        using (var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/user/data/games" => Reply(HttpStatusCode.OK, "{\"owned\":[1]}"),
            "/products/1" => Reply(HttpStatusCode.OK,
                "{\"id\":1,\"title\":\"Game\",\"slug\":\"game\",\"downloads\":{\"installers\":[{\"os\":\"windows\",\"language\":\"en\",\"total_size\":4}]}}"),
            _ => new(HttpStatusCode.ServiceUnavailable),
        })))
        {
            var snapshot = await new LibrarySync(new(http), new(http, http, new(0)), 1).RunAsync();
            Probe.Check("sync records a build outage as a failure instead of publishing an installer-only game",
                snapshot.Games.Count == 0 && snapshot.Failures.ContainsKey(1));
        }
    }

    private const string BuildJson = "{\"items\":[{\"build_id\":\"build\",\"generation\":1,\"branch\":null}]}";
    private static HttpResponseMessage Reply(HttpStatusCode status, string json) => new(status)
    { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply(request));
    }
}
