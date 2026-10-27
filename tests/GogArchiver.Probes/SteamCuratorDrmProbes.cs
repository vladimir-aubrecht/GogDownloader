using System.Net;
using System.Text.Json;
using GameArchiver;
using GameArchiver.Cli;
using GameArchiver.Core;

namespace GogArchiver.Probes;

public static class SteamCuratorDrmProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Public Steam curator DRM reports");
        var handler = new Handler(); using var http = new HttpClient(handler);
        using var source = new SteamCuratorDrmSource(http, TimeSpan.Zero);
        var free = await source.GetAsync(new("steam", "10"));
        var conditional = await source.GetAsync(new("steam", "20"));
        var noWindows = await source.GetAsync(new("steam", "30"));
        var absent = await source.GetAsync(new("steam", "40"));
        Probe.Check("a complete paginated curator list is fetched once for all product lookups", handler.Calls == 2);
        Probe.Check("only an explicit Windows report becomes free and retains the curator attribution",
            free[GameOs.Windows].Status == DrmStatus.Free && free[GameOs.Linux].Status == DrmStatus.Unknown
            && free[GameOs.Windows].Source == "steam-curator:45614612" && free[GameOs.Windows].Url!.EndsWith("?appid=10"));
        Probe.Check("conditional, absent and non-Windows records stay unknown, never protected or free",
            conditional.Values.Concat(noWindows.Values).Concat(absent.Values).All(d => d.Status == DrmStatus.Unknown)
            && conditional[GameOs.Windows].Notes!.Contains("first launch"));
        foreach (var text in new[] { "Not DRM-free", "DRM-free for Linux only", "DRM-free if a file is removed", "<b>DRM-free.</b>" })
        {
            using var customHttp = new HttpClient(new Handler { Text = text });
            using var custom = new SteamCuratorDrmSource(customHttp, TimeSpan.Zero);
            Probe.Check("qualifications and unexpected markup are never stripped into a free claim: " + text,
                (await custom.GetAsync(new("steam", "10")))[GameOs.Windows].Status == DrmStatus.Unknown);
        }
        foreach (var fault in new[] { "denied", "partial", "changed-total", "wrong-start", "duplicate", "markup", "foreign-link", "oversized" })
        {
            var failedHandler = new Handler { Fault = fault }; using var failedHttp = new HttpClient(failedHandler);
            using var failed = new SteamCuratorDrmSource(failedHttp, TimeSpan.Zero);
            var result = await LibraryDrm.RefreshAsync(Library(), failed);
            Probe.Check("an invalid curator response cannot publish partial classifications: " + fault,
                result.Error is not null && result.Updated == 0
                && result.Library.Games.All(g => g.Drm[GameOs.Windows].Status == DrmStatus.Unknown) && failedHandler.Calls <= 2);
        }
        using (var cts = new CancellationTokenSource())
        {
            var cancelled = false; cts.Cancel();
            try { await source.GetAsync(new("steam", "10"), cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Probe.Check("curator cancellation propagates even for a loaded list", cancelled);
        }
        var library = Library();
        library.DrmOverrides["steam:20"] = new() { [GameOs.Windows] = new() { Source = "manual", Status = DrmStatus.Free, Notes = "Offline test" } };
        var refreshed = await LibraryDrm.RefreshAsync(library, source);
        using var cachedHttp = new HttpClient(new Handler { Fault = "denied" });
        using var cachedSource = new SteamCuratorDrmSource(cachedHttp, TimeSpan.Zero);
        var cached = await LibraryDrm.RefreshAsync(refreshed.Library, cachedSource);
        Probe.Check("community caching and manual precedence work with the curator source",
            cached.Error is null && cached.Cached == 2 && DrmCatalog.Get(cached.Library, new("steam", "20"), GameOs.Windows).Source == "manual");
        var root = Probe.ScratchDirectory();
        try
        {
            var path = Path.Combine(root, "library.json"); await library.SaveAsync(path);
            var stdout = Console.Out; var stderr = Console.Error;
            using var output = new StringWriter(); using var error = new StringWriter();
            int exit; int factoryCalls = 0;
            try
            {
                Console.SetOut(output); Console.SetError(error);
                Task<int> Run(params string[] args) => ArchiveCommandLine.RunAsync([..args, "--snapshot", path],
                    storeFactory: _ => [], drmSourceFactory: () => { factoryCalls++; return new SteamCuratorDrmSource(http, TimeSpan.Zero); });
                exit = await Run("library", "drm", "sync", "--drm-source", "steam-curator");
                var before = await File.ReadAllTextAsync(path);
                var invalid = await Run("library", "drm", "sync", "--drm-source", "invalid");
                var ignored = await Run("download", "--all", "--drm-source", "steam-curator", "--dry-run");
                Probe.Check("unsupported DRM source options fail before lookup or writes", invalid == 1 && ignored == 1
                    && factoryCalls == 1 && before == await File.ReadAllTextAsync(path));
            }
            finally { Console.SetOut(stdout); Console.SetError(stderr); }
            Probe.Check("the CLI selects and persists the alternative DRM source", exit == 0 && output.ToString().Contains("Source: steam-curator:45614612")
                && (await ArchiveLibrary.LoadAsync(path)).Games[0].Drm[GameOs.Windows].Status == DrmStatus.Free);
        }
        finally { Directory.Delete(root, true); }
    }

    private static ArchiveLibrary Library() => new() { Games = new[] { "10", "20" }.Select(id => new GameInfo(new("steam", id), "Game " + id, "game_" + id)
        { Versions = new() { [GameOs.Windows] = new("1") } }).ToList() };
    private static string Row(int id, string text, bool windows = true) => "<div class=\"recommendation\"><a class=\"recommendation_link\" href=\"https://store.steampowered.com/app/"
        + id + "/Game/\"><div class=\"recommendation_desc\">" + text + "</div>"
        + (windows ? "<span class=\"platform_img win\"></span>" : "<span class=\"platform_img linux\"></span>") + "</a></div>";
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public string? Fault;
        public string Text = "DRM-free, according to PCGamingWiki.";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var second = request.RequestUri!.Query.Contains("start=2&");
            if (Fault == "denied" && second) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            var html = second ? Row(Fault == "duplicate" ? 10 : 30, "DRM-free.", false)
                : Row(10, Text) + Row(20, "DRM-free only after Steam first launch");
            if (Fault == "partial" && second) html = "";
            if (Fault == "markup") html = html.Replace("recommendation_desc", "changed");
            if (Fault == "foreign-link") html = html.Replace("https://store.steampowered.com", "https://example.com");
            var json = JsonSerializer.Serialize(new { success = 1, start = second && Fault != "wrong-start" ? "2" : "0", pagesize = "2",
                total_count = second && Fault == "changed-total" ? 4 : 3, results_html = html });
            if (Fault == "oversized") json = new string('x', 2 * 1024 * 1024 + 1);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
