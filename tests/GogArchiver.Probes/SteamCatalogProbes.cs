using GameArchiver;
using GameArchiver.Cli;
using GameArchiver.Core;
using SteamArchiver.Core;

namespace GogArchiver.Probes;

public static class SteamCatalogProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Steam primary catalog, inventory and confirmed dependency failures");
        var root = Probe.ScratchDirectory();
        try { await CatalogAsync(root); }
        finally { Directory.Delete(root, true); }
    }

    private static async Task CatalogAsync(string root)
    {
        var session = new Session();
        await using var steam = new SteamClient(session);
        await using var archive = new GameArchiverClient([steam], ownsStores: false);
        var previousGame = new GameInfo(new("steam", "4"), "Old root four", "root_four")
            { Versions = new() { [GameOs.Windows] = new("old") } };
        var previous = new ArchiveLibrary
        {
            Games = [previousGame, new(new("gog", "1"), "GOG game", "gog_game")],
            PrimaryCatalogs = new() { ["gog"] = ["1"] },
            Failures = new() { ["gog:9"] = "Other source failure" },
            FailureImpacts = new() { ["gog:9"] = [new("1")] },
        };
        var library = await archive.SyncLibraryAsync(previous);
        Probe.Check("sync reads one complete catalog and one license inventory", session.CatalogCalls == 1 && session.OwnershipCalls == 1);
        Probe.Check("primary catalog preserves API membership without manufacturing license grants",
            library.PrimaryCatalogs["steam"].SetEquals(["1", "4", "8"]) && library.Failures["steam:8"].Contains("no active package license")
            && !library.Games.Any(g => g.Key == new GameKey("steam", "8")) && !session.AppRequests.Contains(8));
        Probe.Check("DLC, tools and non-catalog games stay in the complete inventory",
            new[] { "2", "6", "7" }.All(id => library.Games.Any(g => g.Key == new GameKey("steam", id))) && !session.AppRequests.Contains(99));
        Probe.Check("failed catalog products retain their older metadata", library.Games.Contains(previousGame));
        Probe.Check("source-filtered sync preserves other catalogs, failures and impacts",
            library.PrimaryCatalogs["gog"].SetEquals(["1"]) && library.Failures.ContainsKey("gog:9") && library.FailureImpacts["gog:9"].Single().ProductId == "1");
        var game = library.Games.Single(g => g.Key == new GameKey("steam", "1"));
        Probe.Check("selected content distinguishes required DLC appinfo from a shared local manifest",
            game.Versions[GameOs.Windows].Dependencies.Contains(new("2", "dlc"))
            && game.Versions[GameOs.Windows].Dependencies.Contains(new("3", "shared-depot", "11", false)));
        Probe.Check("adding dependency provenance does not invalidate an identical content selection",
            game.Versions[GameOs.Windows].Matches(game.Versions[GameOs.Windows] with { Dependencies = [] }));
        Probe.Check("denied inventory appinfo with a locally available manifest does not block its parent",
            library.Failures.ContainsKey("steam:3") && !library.Failures.ContainsKey("steam:1") && !library.FailureImpacts.ContainsKey("steam:3")
            && session.AppRequests.Count(id => id == 3) == 1);
        var impact = library.FailureImpacts["steam:5"].Single();
        Probe.Check("a required shared source links its failure to the exact catalog selection",
            impact.ProductId == "4" && impact.RequiredProductId == "5" && impact.DepotId == "40"
            && impact.Platform == GameOs.Windows && impact.Language == "en" && impact.Architecture == "64" && impact.Branch == "public"
            && impact.Detail!.Contains("Denied app 5") && library.FailureImpacts["steam:4"].Contains(impact));
        Probe.Check("batch download selection uses catalog roots but DLC search still resolves to its catalog parent",
            library.SelectDownloads(library.Games, GameOs.Windows).Select(g => g.Key.ProductId).ToHashSet().SetEquals(["1", "4"])
            && library.SelectDownloads(library.Games.Where(g => g.Key == new GameKey("steam", "2")), GameOs.Windows).Single().Key == game.Key);
        var backupOptions = new ArchiveOptions { OutputRoot = Path.Combine(root, "existing-backups") };
        await new ArchiveMetadata { Source = "steam", ProductId = "6", Slug = "inventory_game", Title = "Inventory game" }
            .SaveAsync(Path.Combine(backupOptions.OutputRoot, "inventory_game"));
        Probe.Check("an existing non-catalog backup is not misidentified as an orphan", (await archive.FindOrphansAsync(library, backupOptions)).Count == 0);
        var legacy = library with { PrimaryCatalogs = [] };
        Probe.Check("old snapshots without catalog membership retain their previous selection behavior",
            legacy.SelectDownloads(legacy.Games, GameOs.Windows).Any(g => g.Key == new GameKey("steam", "6")));

        var path = Path.Combine(root, "library.json");
        await library.SaveAsync(path);
        library = await ArchiveLibrary.LoadAsync(path);
        Probe.Check("catalogs, dependency provenance and failure impacts survive serialization",
            library.PrimaryCatalogs["steam"].SetEquals(["1", "4", "8"]) && library.FailureImpacts["steam:5"].Single() == impact
            && library.Games.Single(g => g.Key == game.Key).Versions[GameOs.Windows].Dependencies.SequenceEqual(game.Versions[GameOs.Windows].Dependencies));
        var output = await CliAsync(path, ["library", "list"]);
        Probe.Check("normal library listing shows the primary catalog", output.Exit == 0 && output.Text.Contains("Root one") && !output.Text.Contains("Inventory game"));
        output = await CliAsync(path, ["library", "list", "--inventory"]);
        Probe.Check("inventory listing exposes retained non-catalog products", output.Exit == 0 && output.Text.Contains("Inventory game") && output.Text.Contains("Expansion"));
        output = await CliAsync(path, ["library", "stats"]);
        Probe.Check("stats clearly distinguish the catalog count from additional inventory products",
            output.Text.Contains("steam primary catalog: 3 entries, 2 with saved metadata") && output.Text.Contains("3 additional inventory products"));
        output = await CliAsync(path, ["download", "--all", "--dry-run", "--out", Path.Combine(root, "output")]);
        Probe.Check("CLI batch downloads exclude non-catalog games", output.Exit == 0 && output.Text.Contains("steam:1") && output.Text.Contains("steam:4") && !output.Text.Contains("steam:6"));
        output = await CliAsync(path, ["library", "failures"]);
        Probe.Check("offline failures distinguish a confirmed blocker from unresolved inventory metadata",
            output.Text.Contains("Blocks Root four (steam:4) [Windows/en/64/public]; requires steam:5, depot 40.")
            && output.Text.Contains("Inventory metadata outside the primary catalog; no blocking catalog dependency confirmed"));
        output = await CliAsync(path, ["library", "failures", "Root four", "--details"]);
        Probe.Check("failure search also finds dependency failures by their blocked game's name",
            output.Text.Contains("steam:5") && output.Text.Contains("2 recorded sync failures") && output.Text.Contains("Denied app 5"));
        var syncPath = Path.Combine(root, "sync.json");
        await previous.SaveAsync(syncPath);
        output = await CliAsync(syncPath, ["library", "sync"], () => new SteamClient(new Session()));
        Probe.Check("sync distinguishes unresolved inventory metadata without clearing its error status",
            output.Exit == 1 && output.Text.Contains("Inventory metadata unavailable Name unavailable (steam:3)")
            && output.Text.Contains("Blocks Root four (steam:4)") && output.Text.Contains("4 unresolved sync failures"));

        session.CatalogError = new IOException("Incomplete GetOwnedGames response");
        var ownershipCalls = session.OwnershipCalls;
        var unavailable = await archive.SyncLibraryAsync(library);
        Probe.Check("an incomplete catalog preserves previous products, membership and failure evidence",
            unavailable.Games.SequenceEqual(library.Games) && unavailable.PrimaryCatalogs["steam"].SetEquals(library.PrimaryCatalogs["steam"])
            && unavailable.Failures.ContainsKey("steam") && unavailable.Failures["steam:5"] == library.Failures["steam:5"]
            && unavailable.FailureImpacts["steam:5"].SequenceEqual(library.FailureImpacts["steam:5"])
            && session.OwnershipCalls == ownershipCalls);
        await unavailable.SaveAsync(path);
        output = await CliAsync(path, ["library", "failures"]);
        Probe.Check("retained impact and membership data are labelled stale after a source outage",
            output.Text.Contains("Impact is from the previous sync") && output.Text.Contains("Catalog membership is from the previous sync"));
        session.CatalogError = new OperationCanceledException();
        var cancelled = false;
        try { await archive.SyncLibraryAsync(library); } catch (OperationCanceledException) { cancelled = true; }
        Probe.Check("catalog cancellation propagates without turning into ownership loss", cancelled);
        session.CatalogError = null;
        session.Recover = true;
        session.Catalog.Remove(8);
        var recovered = await archive.SyncLibraryAsync(library);
        Probe.Check("recovery removes obsolete failures and impacts while keeping catalog roots",
            !recovered.Failures.ContainsKey("steam:4") && !recovered.Failures.ContainsKey("steam:5")
            && !recovered.FailureImpacts.ContainsKey("steam:5") && !recovered.FailureImpacts.ContainsKey("steam:4")
            && recovered.Games.Any(g => g.Key == new GameKey("steam", "4") && g.Versions[GameOs.Windows].Manifests.ContainsKey("40")));
        session.Catalog.Clear();
        var empty = await archive.SyncLibraryAsync(library);
        Probe.Check("an explicitly empty catalog does not fall back to every licensed game",
            empty.PrimaryCatalogs["steam"].Count == 0 && empty.Games.Any(g => g.Key == new GameKey("steam", "6"))
            && !empty.SelectDownloads(empty.Games, GameOs.Windows).Any(g => g.Key.Source == "steam"));
        session.Catalog[4] = "Root four";
        session.Recover = false;
        session.MultiPlatform = true;
        var multi = await archive.SyncLibraryAsync(library);
        Probe.Check("all failed platform selections retain their own dependency and full diagnostic",
            multi.FailureImpacts["steam:4"].Count == 2
            && multi.FailureImpacts["steam:5"].Single().Platform == GameOs.Windows
            && multi.FailureImpacts["steam:3"].Single().Platform == GameOs.Mac
            && multi.FailureImpacts["steam:4"].Any(i => i.Detail!.Contains("Denied app 5"))
            && multi.FailureImpacts["steam:4"].Any(i => i.Detail!.Contains("Denied app 3")));
        session.MultiPlatform = false;
        session.Recover = true;
        session.MissingSourceDepot = true;
        var missing = await archive.SyncLibraryAsync(library);
        Probe.Check("missing shared-depot metadata identifies its source even when appinfo itself is accessible",
            missing.FailureImpacts["steam:4"].Single().RequiredProductId == "5"
            && missing.FailureImpacts["steam:4"].Single().DepotId == "40"
            && missing.Failures["steam:4"].Contains("missing from app 5") && !missing.Failures.ContainsKey("steam:5"));
    }

    private static async Task<(int Exit, string Text)> CliAsync(string path, string[] args, Func<IGameStore>? store = null)
    {
        var output = Console.Out; var error = Console.Error; using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer); Console.SetError(writer);
            var exit = await ArchiveCommandLine.RunAsync([.. args, "--source", "steam", "--snapshot", path], storeFactory: _ => [store?.Invoke() ?? new OfflineStore()]);
            return (exit, writer.ToString());
        }
        finally { Console.SetOut(output); Console.SetError(error); }
    }

    private sealed class Session : ISteamSession
    {
        public Dictionary<uint, string> Catalog = new() { [1] = "Root one", [4] = "Root four", [8] = "Unlicensed listed game" };
        public Exception? CatalogError;
        public bool Recover;
        public bool MultiPlatform;
        public bool MissingSourceDepot;
        public int CatalogCalls, OwnershipCalls;
        public List<uint> AppRequests = [];
        public Task<IReadOnlyDictionary<uint, string>> GetOwnedGamesAsync(CancellationToken ct)
        { CatalogCalls++; if (CatalogError is not null) throw CatalogError; return Task.FromResult<IReadOnlyDictionary<uint, string>>(Catalog); }
        public Task<IReadOnlySet<uint>> GetOwnedAppIdsAsync(CancellationToken ct)
        { OwnershipCalls++; return Task.FromResult<IReadOnlySet<uint>>(new HashSet<uint> { 1, 2, 3, 4, 5, 6, 7 }); }
        public Task<SteamApp> GetAppAsync(uint id, CancellationToken ct)
        {
            AppRequests.Add(id);
            SteamDepot Local(uint depot) => new(depot) { Manifests = new() { ["public"] = depot * 10 } };
            SteamApp Game(uint app, string name, params SteamDepot[] depots) => new(app, name) { Builds = new() { ["public"] = "1" }, Depots = depots.ToList() };
            return Task.FromResult(id switch
            {
                1 => Game(1, "Root one", Local(10), Local(11) with { SharedFromApp = 3 }) with { Dlcs = [2] },
                2 => Game(2, "Expansion", Local(20)) with { Type = "dlc", ParentAppId = 1 },
                4 when MultiPlatform => Game(4, "Root four", new SteamDepot(40) { SharedFromApp = 5, OsList = "windows" },
                    new SteamDepot(41) { SharedFromApp = 3, OsList = "macos" }) with { Platforms = [GameOs.Windows, GameOs.Mac] },
                4 => Game(4, "Root four", new SteamDepot(40) { SharedFromApp = 5 }),
                5 when Recover => Game(5, "Recovered source", MissingSourceDepot ? [] : [Local(40)]) with { Type = "config" },
                6 => Game(6, "Inventory game", new SteamDepot(60) { SharedFromApp = 99 }),
                7 => Game(7, "Tool") with { Type = "tool" },
                _ => throw new IOException($"Denied app {id}"),
            });
        }
        public Task<ISteamContentSession> OpenContentAsync(long rate, CancellationToken ct) => throw new Exception("Sync cannot download content");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OfflineStore : IGameStore
    {
        public string Source => "steam";
        public Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default) => throw new Exception("Unexpected sync");
        public Task<GameVersion> GetVersionAsync(string id, GameSelection options, CancellationToken ct = default) => throw new Exception("Unexpected version query");
        public Task<ArchiveResult> DownloadAsync(string id, ArchiveOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) => throw new Exception("Unexpected download");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
