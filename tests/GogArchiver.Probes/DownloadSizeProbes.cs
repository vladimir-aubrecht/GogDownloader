using GameArchiver.Cli;
using GameArchiver.Core;
using GogArchiver.Core;
using GogArchiver.Core.Library;
using SteamArchiver.Core;
using SteamKit2;
using SteamStore = SteamArchiver.Core.SteamClient;

namespace GogArchiver.Probes;

public static class DownloadSizeProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Download subsets by recorded size");
        await SteamSizesAsync();
        GogSizes();
        var root = Probe.ScratchDirectory();
        try { await CliAsync(root); await ManifestLimitAsync(root); }
        finally { Directory.Delete(root, true); }
    }

    private static SteamDepot Depot(uint id, long? download, long? installed, ulong manifest = 100) => new(id)
    {
        Manifests = new() { ["public"] = manifest },
        DownloadSizes = download is null ? [] : new() { ["public"] = download.Value },
        InstalledSizes = installed is null ? [] : new() { ["public"] = installed.Value },
    };

    private static async Task SteamSizesAsync()
    {
        var parsed = SteamApp.Parse(1, KeyValue.LoadFromString("""
            "appinfo" {
              "common" { "name" "Game" "oslist" "windows" }
              "depots" {
                "branches" { "public" { "buildid" "1" } "beta" { "buildid" "2" } }
                "10" { "manifests" {
                  "public" { "gid" "100" "download" "5000000000" "size" "6000000000" }
                  "beta" { "gid" "101" "download" "0" "size" "0" }
                  "bad" { "gid" "102" "download" "-1" "size" "9223372036854775808" }
                  "missing" { "gid" "103" }
                } }
              }
            }
            """)!);
        Probe.Check("Steam parses per-branch depot sizes above 4 GiB without truncation",
            parsed.Depots.Single().DownloadSizes["public"] == 5_000_000_000L && parsed.Depots.Single().InstalledSizes["public"] == 6_000_000_000L);
        Probe.Check("zero-byte depots are known while absent, invalid and overflowing sizes stay unknown",
            parsed.Depots.Single().DownloadSizes["beta"] == 0 && parsed.Depots.Single().DownloadSizes.Count == 2
            && parsed.Depots.Single().InstalledSizes.Count == 2);
        var session = new Session { Owned = [1, 2] };
        var game = new SteamApp(1, "Game") { Builds = new() { ["public"] = "1" }, Dlcs = [2], Depots =
        [
            Depot(10, 10, 20) with { Architecture = "64" },
            Depot(20, 5, 10) with { Language = "english" },
            Depot(21, 1000, 2000) with { Language = "german" },
            Depot(30, 1000, 2000) with { Architecture = "32" },
            Depot(40, 1000, 2000) with { OsList = "linux" },
            Depot(50, 7, 9, 500) with { SharedFromApp = 99 },
            Depot(70, 1000, 2000) with { DlcAppId = 3 },
        ] };
        session.Apps[1] = game;
        session.Apps[2] = new(2, "Expansion") { Type = "dlc", ParentAppId = 1, Depots = [Depot(50, null, null, 500), Depot(60, 3, 4)] };
        await using var steam = new SteamStore(session);
        var version = await steam.GetVersionAsync("1", new());
        Probe.Check("Steam sizes include owned DLC and deduplicate shared depots after OS, language and architecture selection",
            version.DownloadSize == 25 && version.InstalledSize == 43 && version.Manifests.Count == 4);
        var library = await steam.SyncLibraryAsync();
        Probe.Check("Steam library sync records the same selected sizes as the version API",
            library.Games.Single(g => g.Key.ProductId == "1").Versions[GameOs.Windows].DownloadSize == 25);
        Probe.Check("adding size metadata does not invalidate a completed content selection",
            version.Matches(version with { DownloadSize = 0, InstalledSize = 0 }));

        session.Apps[2] = session.Apps[2] with { Depots = [Depot(50, null, null, 501), Depot(60, 3, 4)] };
        Probe.Check("a changed depot manifest never inherits the previous manifest's known size",
            (await steam.GetVersionAsync("1", new())).DownloadSize == 0);
        session.Apps[2] = session.Apps[2] with { Depots = [Depot(60, null, 4)] };
        version = await steam.GetVersionAsync("1", new());
        Probe.Check("one missing selected depot size makes the total unknown instead of undercounting",
            version.DownloadSize == 0 && version.InstalledSize == 43);
        session.Apps[2] = session.Apps[2] with { Depots = [Depot(60, 0, 0)] };
        Probe.Check("a known empty DLC depot does not invalidate the remaining size estimate",
            (await steam.GetVersionAsync("1", new())).DownloadSize == 22);
        session.Apps[2] = session.Apps[2] with { Depots = [Depot(60, 0, 7_243_075_694)] };
        Probe.Check("zero download size for a nonempty depot is unknown, never a partial small-game total",
            (await steam.GetVersionAsync("1", new())).DownloadSize == 0);
        session.Apps[2] = session.Apps[2] with { Depots = [Depot(60, 0, null)] };
        Probe.Check("zero download size without an installed size is not proof of an empty depot",
            (await steam.GetVersionAsync("1", new())).DownloadSize == 0);
        session.Owned = [1];
        session.Apps[1] = parsed;
        Probe.Check("Steam branch selection uses that branch's sizes", (await steam.GetVersionAsync("1", new() { Branch = "beta" })).DownloadSize == 0);
        session.Apps[1] = game with { Dlcs = [], Depots = [new(50) { SharedFromApp = 99 }] };
        session.Apps[99] = new(99, "Shared") { Depots = [Depot(50, 8, 10, 505)] };
        Probe.Check("shared-source fallback carries the source manifest's sizes",
            (await steam.GetVersionAsync("1", new())).DownloadSize == 8);
    }

    private static void GogSizes()
    {
        var library = new LibrarySnapshot { Games = [new() { Id = 1, Title = "Installer", Slug = "installer", Platforms = new()
        { [GamePlatform.Windows] = new() { Installers = [new("de", "1", 100), new("en", "1", 30)] } } }] };
        Probe.Check("GOG installer estimates use the selected language's installer size",
            GogClient.ConvertLibrary(library).Single().Versions[GameOs.Windows].DownloadSize == 30);
        library.Games[0].Platforms[GamePlatform.Windows] = new() { Depot = new("1", 2, "1", "manifest"),
            DownloadSize = 20, Installers = [new("en", "1", 30)] };
        Probe.Check("GOG depot estimates remain authoritative when an installer is also present",
            GogClient.ConvertLibrary(library).Single().Versions[GameOs.Windows].DownloadSize == 20);
    }

    private static GameInfo Game(string source, string id, long size, string language = "en", string kind = "game") =>
        new(new(source, id), $"Game {id}", $"game_{id}") { Kind = kind, Versions = new() { [GameOs.Windows] = new("1")
        { DownloadSize = size, SizeEstimateRevision = 1, Language = language, Architecture = source == "gog" ? "" : "64" } } };

    private static async Task CliAsync(string root)
    {
        var parent = Game("steam", "8", 150_000_000) with { OwnedDlcs = ["7"] };
        parent.Versions[GameOs.Windows] = parent.Versions[GameOs.Windows] with { Dlcs = ["7"] };
        var multiOs = Game("steam", "9", 30_000_000);
        multiOs.Versions[GameOs.Linux] = new("1") { DownloadSize = 300_000_000, SizeEstimateRevision = 1 };
        var library = new ArchiveLibrary { Games = [Game("gog", "1", 50_000_000), Game("steam", "2", 100_000_000),
            Game("steam", "3", 120_000_000), Game("steam", "4", 0), Game("steam", "5", 5_000_000, language: "de"),
            Game("steam", "6", 20_000_000), Game("steam", "7", 1, kind: "dlc"), parent, multiOs],
            Failures = new() { ["steam:6"] = "Previous metadata retained after failure" } };
        var snapshot = Path.Combine(root, "library.json"); await library.SaveAsync(snapshot);
        var calls = new List<GameKey>();
        IEnumerable<IGameStore> Stores(string source) => (source == "all" ? new[] { "gog", "steam" } : new[] { source }).Select(s => new Store(s, calls));
        var common = new[] { "--snapshot", snapshot, "--out", Path.Combine(root, "out") };
        var result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "--max-size", "100MB", "--dry-run", ..common], storeFactory: Stores));
        Probe.Check("the size filter selects only games strictly below the limit and dry-run remains offline",
            result.Exit == 0 && result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).SequenceEqual(["gog:1", "steam:9"])
            && calls.Count == 0 && !Directory.Exists(Path.Combine(root, "out")));
        Probe.Check("unknown sizes, failed refreshes and incompatible language selections are reported separately",
            result.Error.Contains("3 at or above the limit") && result.Error.Contains("3 with unknown or incompatible size"));
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "--source", "steam", "--max-size", "100MB", ..common], storeFactory: Stores));
        Probe.Check("actual batch downloads respect both provider and size filters", result.Exit == 0 && calls.SequenceEqual([new GameKey("steam", "9")]));
        calls.Clear();
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "game_7", "--max-size", "100MB", ..common], storeFactory: Stores));
        Probe.Check("a small DLC cannot bypass the size limit of the complete parent game", result.Exit == 1 && calls.Count == 0);
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "--max-size", "100MB", "--platform", "linux", "--dry-run", ..common], storeFactory: Stores));
        Probe.Check("the size filter uses the requested operating system", result.Exit == 1 && result.Output.Length == 0);
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "--max-size", "100MB", "--arch", "32", "--dry-run", ..common], storeFactory: Stores));
        Probe.Check("a different Steam architecture does not reuse a 64-bit estimate while GOG stays architecture-neutral",
            result.Exit == 0 && result.Output.Trim() == "gog:1");
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "--max-size", "100MB", "--branch", "beta", "--dry-run", ..common], storeFactory: Stores));
        Probe.Check("a different branch never uses the public branch's recorded size", result.Exit == 1 && result.Output.Length == 0);
        foreach (var value in new[] { "100MiB", "100M", "0.1GiB", "104857600B", "104857600" })
        {
            result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "--max-size", value, "--dry-run", ..common], storeFactory: Stores));
            Probe.Check($"size unit selection works for {value}", result.Exit == 0 && result.Output.Contains("steam:2") && !result.Output.Contains("steam:3"));
        }
        foreach (var value in new[] { "0", "-1MB", "NaN", "1e2MB", "100MB/s", "9223372036854775808", "99999999999999999999TiB", "0.1B" })
        {
            result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "--max-size", value, ..common], storeFactory: Stores));
            Probe.Check($"invalid size limits are rejected before downloads: {value}", result.Exit == 1 && result.Error.Contains("Invalid --max-size") && calls.Count == 0);
        }
        foreach (var command in new[] { new[] { "download", "steam:9" }, new[] { "library", "sync" }, new[] { "diff" } })
        {
            result = await CaptureAsync(() => ArchiveCommandLine.RunAsync([..command, "--max-size", "100MB", ..common], storeFactory: Stores));
            Probe.Check($"size limits are not silently ignored by {string.Join(' ', command)}", result.Exit == 1 && result.Error.Contains("requires download --all"));
        }
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "--dry-run", ..common], storeFactory: Stores));
        Probe.Check("omitting the size limit retains the existing batch selection", result.Exit == 0 && result.Output.Contains("steam:4") && result.Output.Contains("steam:8"));
        multiOs.Versions[GameOs.Windows] = multiOs.Versions[GameOs.Windows] with { SizeEstimateRevision = 0 };
        await library.SaveAsync(snapshot);
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "--max-size", "100MB", "--dry-run", ..common], storeFactory: Stores));
        Probe.Check("legacy Steam estimates require a sync instead of trusting potentially false zeros", !result.Output.Contains("steam:9"));
    }

    private static async Task ManifestLimitAsync(string root)
    {
        var session = new Session { Owned = [1], Content = new OversizedContent() };
        session.Apps[1] = new(1, "Large game") { Builds = new() { ["public"] = "1" }, Depots = [Depot(10, 1, 1)] };
        await using var client = new SteamStore(session);
        var staging = Path.Combine(root, "guard-staging");
        var options = new ArchiveOptions { OutputRoot = Path.Combine(root, "guard-output"), StagingRoot = staging, MaxDownloadSize = 10_000_000 };
        var result = await client.DownloadAsync("1", options);
        Probe.Check("real manifest size blocks misleading appinfo before any content or staging writes",
            result.Outcome == ArchiveOutcome.SkippedSize && !Directory.Exists(staging) && result.Detail!.Contains("10000000 bytes"));
    }

    private sealed class OversizedContent : ISteamContentSession
    {
        public Task<SteamManifest> GetManifestAsync(SteamDepotRequest depot, CancellationToken ct) => Task.FromResult(new SteamManifest(depot,
            [new SteamFile("large.bin", 10_000_000, new string('a', 40), [new(new string('a', 40), 0, 0, 10_000_000, 10_000_000)])]));
        public Task<byte[]> GetChunkAsync(SteamDepotRequest depot, SteamChunk chunk, CancellationToken ct) => throw new Exception("Oversized content must never be requested");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<(int Exit, string Output, string Error)> CaptureAsync(Func<Task<int>> action)
    {
        var output = Console.Out; var error = Console.Error; using var stdout = new StringWriter(); using var stderr = new StringWriter();
        try { Console.SetOut(stdout); Console.SetError(stderr); return (await action(), stdout.ToString(), stderr.ToString()); }
        finally { Console.SetOut(output); Console.SetError(error); }
    }
    private sealed class Session : ISteamSession
    {
        public Task<IReadOnlyDictionary<uint, string>> GetOwnedGamesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<uint, string>>(Owned.ToDictionary(id => id, id => Apps[id].Title));
        public HashSet<uint> Owned = [];
        public Dictionary<uint, SteamApp> Apps = [];
        public ISteamContentSession? Content;
        public Task<IReadOnlySet<uint>> GetOwnedAppIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlySet<uint>>(Owned);
        public Task<SteamApp> GetAppAsync(uint id, CancellationToken ct) => Task.FromResult(Apps[id]);
        public Task<ISteamContentSession> OpenContentAsync(long rate, CancellationToken ct) => Task.FromResult(Content ?? throw new Exception("Size planning must not download content"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Store(string source, List<GameKey> downloads) : IGameStore
    {
        public string Source => source;
        public Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default) => throw new Exception("No implicit sync");
        public Task<GameVersion> GetVersionAsync(string id, GameSelection options, CancellationToken ct = default) => throw new Exception("Size selection must use the snapshot");
        public Task<ArchiveResult> DownloadAsync(string id, ArchiveOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
        { var key = new GameKey(source, id); downloads.Add(key); return Task.FromResult(new ArchiveResult(key, ArchiveOutcome.Completed)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
