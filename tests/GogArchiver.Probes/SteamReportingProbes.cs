using GameArchiver;
using GameArchiver.Cli;
using GameArchiver.Core;
using SteamArchiver.Core;

namespace GogArchiver.Probes;

public static class SteamReportingProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Steam product counts and conservative failure reporting");
        var root = Probe.ScratchDirectory();
        try
        {
            await ReportingAsync(root);
            await RefreshAsync();
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task ReportingAsync(string root)
    {
        await using var steam = new SteamClient(new Session());
        var result = await steam.SyncLibraryAsync();
        Probe.Check("an accessible app in the same licensed package never hides a different denied AppID",
            result.Games.Any(g => g.Key.ProductId == "1") && result.Failures.ContainsKey("2")
            && result.FailureNames["2"] == result.Games.Single(g => g.Key.ProductId == "1").Title);
        Probe.Check("multiple packages never turn a persistent denial into success",
            result.Failures["2"].Contains("Licensed packages: 7, 8") && result.FailureSummaries.ContainsKey("2"));
        Probe.Check("a retry that returns the same AppID recovers without a stale failure or summary",
            result.Games.Count(g => g.Key.ProductId == "3") == 1 && !result.Failures.ContainsKey("3") && !result.FailureSummaries.ContainsKey("3"));
        Probe.Check("a required shared app's denial remains a parent failure naming the required ID",
            result.Failures.ContainsKey("6") && result.FailureNames["6"] == "Dependent game"
            && result.FailureSummaries["6"].Contains("Required Steam app 2") && !result.Games.Any(g => g.Key.ProductId == "6"));
        Probe.Check("timeouts and ambiguous token states keep full diagnostics instead of claiming explicit denial",
            result.Failures["4"].Contains("timed out") && result.Failures["5"].Contains("not returned")
            && !result.FailureSummaries.ContainsKey("4") && !result.FailureSummaries.ContainsKey("5"));

        var snapshot = Path.Combine(root, "library.json");
        await new ArchiveLibrary { Games = [new(new("gog", "1"), "GOG title", "gog_title")] }.SaveAsync(snapshot);
        var output = await CaptureAsync(() => ArchiveCommandLine.RunAsync(
            ["library", "sync", "--source", "steam", "--snapshot", snapshot], storeFactory: _ => [new SteamClient(new Session())]));
        Probe.Check("sync reports products and per-source types, not licenses or inflated game counts",
            output.Exit == 1 && output.Text.Contains("Saved 6 products") && !output.Text.Contains("Saved 6 games")
            && output.Text.Contains("steam: 5 products (1 game, 1 DLC, 1 application, 1 demo, 1 tool)")
            && output.Text.Contains("gog: 1 product (1 game)"));
        Probe.Check("concise sync failures retain names, unresolved count and a failing exit status",
            output.Text.Contains("Failed Same title (steam:2): Steam denied access to this app's metadata after retry.")
            && !output.Text.Contains("Licensed packages: 7, 8") && output.Text.Contains("4 unresolved sync failures")
            && output.Text.Contains("--details"));
        var library = await ArchiveLibrary.LoadAsync(snapshot);
        Probe.Check("the snapshot retains complete diagnostics as well as concise display summaries",
            library.Failures["steam:2"].Contains("Initial token request: explicitly denied")
            && library.Failures["steam:2"].Contains("Licensed packages: 7, 8")
            && library.FailureSummaries["steam:2"] == result.FailureSummaries["2"]);

        output = await OfflineAsync(snapshot, "failures", "Same title");
        Probe.Check("offline failure search uses names while displaying concise diagnostics",
            output.Exit == 0 && output.Text.Contains("Same title (steam:2)") && !output.Text.Contains("Initial token request")
            && output.Text.Contains("1 recorded sync failures"));
        output = await OfflineAsync(snapshot, "failures", "Same title", "--details");
        Probe.Check("offline --details restores full saved diagnostics for the same failed AppID",
            output.Exit == 0 && output.Text.Contains("Licensed packages: 7, 8") && output.Text.Contains("Individual retry:"));
        output = await CaptureAsync(() => ArchiveCommandLine.RunAsync(
            ["library", "sync", "--source", "steam", "--snapshot", snapshot, "--details"], storeFactory: _ => [new SteamClient(new Session())]));
        Probe.Check("sync --details preserves full output and the failure exit code",
            output.Exit == 1 && output.Text.Contains("Failed Same title (steam:2): Cannot read Steam app 2:")
            && output.Text.Contains("Licensed packages: 7, 8"));
        output = await OfflineAsync(snapshot, "stats");
        Probe.Check("offline stats counts only the selected source and includes DLC and software separately",
            output.Text.Contains("steam: 5 products (1 game, 1 DLC, 1 application, 1 demo, 1 tool)") && !output.Text.Contains("gog:"));
        output = await OfflineAsync(snapshot, "stats", "Same title");
        Probe.Check("filtered stats describes the matching products", output.Text.Contains("steam: 1 product (1 game)") && !output.Text.Contains("DLC"));
        await File.WriteAllTextAsync(snapshot, """{"SchemaVersion":1,"Games":[],"Failures":{"steam:1":"Original diagnostic"}}""");
        output = await OfflineAsync(snapshot, "failures");
        Probe.Check("old snapshots without summaries remain readable and display the original diagnostic",
            output.Exit == 0 && output.Text.Contains("Original diagnostic") && (await ArchiveLibrary.LoadAsync(snapshot)).FailureSummaries.Count == 0);
    }

    private static async Task RefreshAsync()
    {
        var store = new Store { Result = new([]) { Failures = new Dictionary<string, string> { ["1"] = "Full diagnostic" },
            FailureSummaries = new Dictionary<string, string> { ["1"] = "Short diagnostic", ["2"] = "Stray summary" } } };
        await using var archive = new GameArchiverClient(store);
        var library = await archive.SyncLibraryAsync(new() { Failures = new() { ["gog:1"] = "Other source error" },
            FailureSummaries = new() { ["gog:1"] = "Other source summary", ["steam:9"] = "Old error" } });
        Probe.Check("aggregation scopes summaries to current failures and preserves unselected sources",
            library.FailureSummaries.Count == 2 && library.FailureSummaries["steam:1"] == "Short diagnostic"
            && library.FailureSummaries["gog:1"] == "Other source summary");
        store.Result = new([]) { Failures = new Dictionary<string, string> { ["1"] = "New unrelated failure" } };
        library = await archive.SyncLibraryAsync(library);
        Probe.Check("a changed failure never inherits a stale denial summary", !library.FailureSummaries.ContainsKey("steam:1"));
        store.Result = new([]);
        library = await archive.SyncLibraryAsync(library);
        Probe.Check("recovery removes failures and summaries together", !library.Failures.ContainsKey("steam:1") && library.FailureSummaries.Count == 1);
        store.Fail = true;
        library = await archive.SyncLibraryAsync(library with { FailureSummaries = new(library.FailureSummaries) { ["steam:1"] = "Stale denial" } });
        Probe.Check("a whole-source outage keeps its own error rather than a stale per-app summary",
            library.Failures["steam"] == "Source unavailable" && library.FailureSummaries.Keys.SequenceEqual(["gog:1"]));
    }

    private static Task<(int Exit, string Text)> OfflineAsync(string snapshot, string command, params string[] extra) =>
        CaptureAsync(() => ArchiveCommandLine.RunAsync(["library", command, "--source", "steam", "--snapshot", snapshot, .. extra],
            storeFactory: _ => [new Store { Fail = true }]));

    private static async Task<(int Exit, string Text)> CaptureAsync(Func<Task<int>> action)
    {
        var output = Console.Out; var error = Console.Error; using var writer = new StringWriter();
        try { Console.SetOut(writer); Console.SetError(writer); return (await action(), writer.ToString()); }
        finally { Console.SetOut(output); Console.SetError(error); }
    }

    private sealed class Session : ISteamSession
    {
        private readonly Dictionary<uint, int> _calls = [];
        private readonly SteamAppInfoCache _cache;
        public Session() => _cache = new((ids, _) => Task.FromResult<IReadOnlyDictionary<uint, SteamAppInfoResult>>(
            ids.ToDictionary(id => id, id =>
            {
                _calls[id] = _calls.GetValueOrDefault(id) + 1;
                if (id == 4 && _calls[id] > 1) return new SteamAppInfoResult(Error: new TimeoutException("Recheck timed out"));
                if (id is 2 or 4 or 5 || id == 3 && _calls[id] == 1)
                    return new SteamAppInfoResult(Error: new SteamAppInfoAccessException(id, id == 5 ? SteamAppTokenStatus.Omitted : SteamAppTokenStatus.Denied));
                return new SteamAppInfoResult(new(id, id == 1 ? "Same title" : id == 6 ? "Dependent game" : $"App {id}")
                {
                    Type = id switch { 3 => "dlc", 8 => "application", 9 => "demo", 10 => "tool", _ => "game" },
                    Builds = new() { ["public"] = "1" },
                    Depots = id == 6 ? [new(20) { SharedFromApp = 2 }] : [new(10) { Manifests = new() { ["public"] = 100 } }],
                });
            })));
        public Task<IReadOnlySet<uint>> GetOwnedAppIdsAsync(CancellationToken ct)
        {
            _calls.Clear();
            // Two active packages grant app 3; app 2 remains denied even though its
            // packages also contain the accessible app 1 with an identical display name.
            _cache.Reset(new Dictionary<uint, uint[]> { [1] = [7], [2] = [7, 8], [3] = [7, 8] });
            return Task.FromResult<IReadOnlySet<uint>>(new HashSet<uint> { 1, 2, 3, 4, 5, 6, 8, 9, 10 });
        }
        public Task PreloadAppsAsync(IReadOnlySet<uint> ids, CancellationToken ct) => _cache.PreloadAsync(ids, ct);
        public Task<SteamApp> GetAppAsync(uint appId, CancellationToken ct) => _cache.GetAsync(appId, ct);
        public Task<IReadOnlyDictionary<uint, string>> GetAppNamesAsync(IReadOnlySet<uint> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<uint, string>>(new Dictionary<uint, string> { [2] = "Same title" });
        public Task<ISteamContentSession> OpenContentAsync(long rate, CancellationToken ct) => throw new Exception("Sync must not download content");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Store : IGameStore
    {
        public bool Fail;
        public StoreLibraryResult Result = new([]);
        public string Source => "steam";
        public Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default) => Fail ? throw new IOException("Source unavailable") : Task.FromResult(Result);
        public Task<GameVersion> GetVersionAsync(string id, GameSelection options, CancellationToken ct = default) => throw new Exception("Unexpected version query");
        public Task<ArchiveResult> DownloadAsync(string id, ArchiveOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) => throw new Exception("Unexpected download");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
