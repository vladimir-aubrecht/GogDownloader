using System.Net;
using System.Text.Json;
using GameArchiver;
using GameArchiver.Core;

namespace GogArchiver.Probes;

internal static class PcGamingWikiSessionRefreshProbes
{
    public static async Task RunAsync(string root)
    {
        foreach (var stage in new[] { "userinfo", "cargo", "parse", "absent", "ambiguous" })
        {
            var path = Path.Combine(root, "rotation-" + stage + ".json");
            var store = new PcGamingWikiSessionStore(path); await SeedAsync(store);
            var handler = new Handler { RotateAt = stage }; using var http = new HttpClient(handler);
            using (var source = new PcGamingWikiDrmSource(http, TimeSpan.Zero, path))
                await source.GetAsync(new("steam", "10"));
            var saved = (await store.LoadAsync())!;
            using var resumed = new PcGamingWikiDrmSource(http, TimeSpan.Zero, path);
            var result = await LibraryDrm.RefreshAsync(Library(), resumed, force: true);
            Probe.Check("rotated cookies survive a new client without another login: " + stage,
                result.Error is null && result.Updated == 1 && handler.Rotations == 1 && handler.Rejected == 0
                && saved.Cookies.Any(c => c.Name == "session" && c.Value == handler.CurrentCookie));
            Probe.Check("cookie refresh retains expiry updates and additions/removals: " + stage,
                saved.Cookies.Single(c => c.Name == "session").Expires == Handler.Expiration
                && saved.Cookies.Any(c => c.Name == "newPreference") && saved.Cookies.All(c => c.Name != "oldPreference"));
        }

        var unchangedPath = Path.Combine(root, "unchanged-session.json");
        await SeedAsync(new(unchangedPath));
        File.SetLastWriteTimeUtc(unchangedPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(unchangedPath);
        using (var http = new HttpClient(new Handler { RotateAt = "unchanged" }))
        using (var source = new PcGamingWikiDrmSource(http, TimeSpan.Zero, unchangedPath))
            await source.GetAsync(new("steam", "10"));
        Probe.Check("unchanged cookies do not rewrite the session file", timestamp == File.GetLastWriteTimeUtc(unchangedPath));

        foreach (var fault in new[] { "identity", "http", "api", "shape", "cancel" })
        {
            var path = Path.Combine(root, "rejected-rotation-" + fault + ".json");
            await SeedAsync(new(path)); var before = await File.ReadAllTextAsync(path);
            using var cts = new CancellationTokenSource();
            var handler = new Handler { RotateAt = fault == "identity" ? "userinfo" : "cargo", Fault = fault, Cancel = cts.Cancel };
            using var http = new HttpClient(handler); using var source = new PcGamingWikiDrmSource(http, TimeSpan.Zero, path);
            var failed = false;
            try { failed = (await LibraryDrm.RefreshAsync(Library(), source, force: true, ct: cts.Token)).Error is not null; }
            catch (OperationCanceledException) { failed = true; }
            Probe.Check("unverified cookie updates cannot overwrite the saved session: " + fault,
                failed && before == await File.ReadAllTextAsync(path) && Directory.GetFiles(root, "*.tmp").Length == 0);
        }

        var retainedPath = Path.Combine(root, "retained-rotation.json");
        var retainedStore = new PcGamingWikiSessionStore(retainedPath); await SeedAsync(retainedStore);
        var retainedHandler = new Handler { RotateAt = "cargo", Fault = "later-parse" };
        using var retainedHttp = new HttpClient(retainedHandler);
        using (var source = new PcGamingWikiDrmSource(retainedHttp, TimeSpan.Zero, retainedPath))
            Probe.Check("a later metadata failure is still reported", (await LibraryDrm.RefreshAsync(Library(), source, force: true)).Error is not null);
        retainedHandler.Fault = null;
        using var retry = new PcGamingWikiDrmSource(retainedHttp, TimeSpan.Zero, retainedPath);
        var retried = await LibraryDrm.RefreshAsync(Library(), retry, force: true);
        Probe.Check("a validated rotation survives a later failed request and can be reused next run",
            retried.Error is null && retainedHandler.Rejected == 0
            && (await retainedStore.LoadAsync())!.Cookies.Single(c => c.Name == "session").Value == retainedHandler.CurrentCookie);
    }

    private static Task SeedAsync(PcGamingWikiSessionStore store) => store.SaveAsync(new("Tester", 42,
        [new("session", "old-test-cookie", "/w", null), new("oldPreference", "value", "/w", null)]));
    private static ArchiveLibrary Library() => new() { Games = [new(new("steam", "10"), "Game", "game")
        { Versions = new() { [GameOs.Windows] = new("1") } }] };

    private sealed class Handler : HttpMessageHandler
    {
        public required string RotateAt;
        public string? Fault;
        public Action? Cancel;
        public int Rotations, Rejected;
        public string CurrentCookie = "old-test-cookie";
        public static readonly DateTime Expiration = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
            var query = request.RequestUri!.Query;
            if (!query.Contains("assert=user")) throw new Exception("Refresh must reuse cookies, not log in again");
            var cookies = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(';', values) : "";
            if (!cookies.Contains("session=" + CurrentCookie))
            {
                Rejected++;
                return Task.FromResult(Json(new { error = new { code = "assertuserfailed" } }));
            }
            var stage = query.Contains("userinfo") ? "userinfo" : query.Contains("cargoquery") ? "cargo" : "parse";
            var response = stage switch
            {
                "userinfo" => Json(new { query = new { userinfo = new { id = Fault == "identity" ? 43 : 42, name = "Tester" } } }),
                "cargo" => Json(new { cargoquery = Enumerable.Range(0, RotateAt == "absent" ? 0 : RotateAt == "ambiguous" ? 2 : 1)
                    .Select(_ => new { title = new { PageID = "7" } }).ToArray() }),
                _ => Json(new { parse = new { wikitext = new Dictionary<string, string> { ["*"] = "{{Availability/row|Steam|10|DRM-free}}" } } }),
            };
            if (stage == "cargo")
            {
                if (Fault == "http") response.StatusCode = HttpStatusCode.Forbidden;
                if (Fault is "api" or "shape")
                {
                    response.Content.Dispose();
                    response.Content = new StringContent(Fault == "api" ? "{\"error\":{\"code\":\"assertuserfailed\"}}" : "{\"cargoquery\":\"invalid\"}");
                }
            }
            if (stage == "parse" && Fault == "later-parse") response.StatusCode = HttpStatusCode.ServiceUnavailable;
            if (Rotations == 0 && (stage == RotateAt || stage == "cargo" && RotateAt is "absent" or "ambiguous"))
            {
                Rotations++; CurrentCookie = "new-test-cookie";
                response.Headers.Add("Set-Cookie", "session=" + CurrentCookie + "; Path=/w; Secure; HttpOnly; Expires="
                    + Expiration.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                response.Headers.Add("Set-Cookie", "oldPreference=; Path=/w; Max-Age=0");
                response.Headers.Add("Set-Cookie", "newPreference=value; Path=/w");
            }
            if (stage == "cargo" && RotateAt == "unchanged") response.Headers.Add("Set-Cookie", "session=" + CurrentCookie + "; Path=/w; Secure; HttpOnly");
            if (stage == "cargo" && Fault == "cancel") Cancel?.Invoke();
            return Task.FromResult(response);
        }
    }
}
