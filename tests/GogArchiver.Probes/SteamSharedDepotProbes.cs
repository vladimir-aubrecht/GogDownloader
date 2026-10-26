using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GameArchiver;
using GameArchiver.Core;
using SteamArchiver.Core;
using SteamKit2;
using SteamStore = SteamArchiver.Core.SteamClient;

namespace GogArchiver.Probes;

public static class SteamSharedDepotProbes
{
    private const uint SharedApp = 1523210, SharedDepot = 1523211;
    private const ulong SharedManifest = 5488617169383930545;

    public static async Task RunAsync()
    {
        Probe.Group("Steam shared depots with inaccessible source appinfo");
        var root = Probe.ScratchDirectory();
        try
        {
            await LibraryAsync(root);
            await FallbackAsync();
            await VariantFilteringAsync();
            await KnownConstraintsAsync();
        }
        finally { Directory.Delete(root, true); }
    }

    private static SteamApp Game(uint id, string title)
    {
        // Reduced from the public appinfo for BioShock/XCOM: both depotfromapp and
        // an accessible public manifest occur on the same shared depot entry.
        var info = KeyValue.LoadFromString($$"""
            "appinfo"
            {
              "common" { "name" "{{title}}" "type" "game" "oslist" "windows" }
              "depots"
              {
                "branches" { "public" { "buildid" "1" } "beta" { "buildid" "2" } }
                "10" { "manifests" { "public" { "gid" "100" } "beta" { "gid" "101" } } }
                "1523211"
                {
                  "config" { "oslist" "windows" }
                  "depotfromapp" "1523210"
                  "manifests" { "public" { "gid" "5488617169383930545" } }
                }
              }
            }
            """)!;
        return SteamApp.Parse(id, info);
    }

    private static async Task LibraryAsync(string root)
    {
        var fixture = new Session();
        foreach (var (id, title) in new (uint, string)[] { (8870, "BioShock Infinite"), (268500, "XCOM 2"),
            (409710, "BioShock Remastered"), (409720, "BioShock 2 Remastered"), (882100, "XCOM Chimera Squad") })
            fixture.Apps[id] = Game(id, title);
        fixture.Owned = fixture.Apps.Keys.Append(SharedApp).ToHashSet();
        await using var steam = new SteamStore(fixture);
        await using var archive = new GameArchiverClient([steam], ownsStores: false);
        var previous = new GameInfo(new("steam", SharedApp.ToString()), "Previously known app", "previously_known_app");
        var snapshot = await archive.SyncLibraryAsync(new() { Games = [previous] });
        Probe.Check("an inaccessible source app does not remove five games with local shared manifests",
            fixture.Apps.Keys.All(id => snapshot.Games.Any(g => g.Key.ProductId == id.ToString())));
        Probe.Check("all five games retain the shared depot in their exact content selection",
            snapshot.Games.Count == 6 && snapshot.Games.Where(g => g != previous).All(g => g.Versions[GameOs.Windows].Manifests.Count == 2
                && g.Versions[GameOs.Windows].Manifests[SharedDepot.ToString()] == SharedManifest.ToString()));
        Probe.Check("only the inaccessible app itself is reported failed and its old library entry survives",
            snapshot.Failures.Keys.SequenceEqual([$"steam:{SharedApp}"]) && snapshot.Games.Contains(previous));
        Probe.Check("source appinfo is queried only for its own library entry", fixture.AppRequests.Count(id => id == SharedApp) == 1);

        fixture.AppRequests.Clear();
        var options = new ArchiveOptions { OutputRoot = Path.Combine(root, "backups"), StagingRoot = Path.Combine(root, "stage"),
            CompressionThreads = 1, MaxConnections = 1 };
        var result = await steam.DownloadAsync("8870", options);
        using (var zip = ZipFile.OpenRead(result.ArchivePath!))
            Probe.Check("downloading with local shared appinfo archives both base and shared content",
                result.Outcome == ArchiveOutcome.Completed && Read(zip, "game.exe") == "base content"
                && Read(zip, "shared.txt") == "shared content");
        Probe.Check("shared content authorization uses the licensed game and its local manifest",
            fixture.ManifestRequests.Single(d => d.DepotId == SharedDepot) == new SteamDepotRequest(8870, SharedDepot, SharedManifest, "public")
            && !fixture.AppRequests.Contains(SharedApp));
        var metadata = (await ArchiveMetadata.LoadAsync(Path.GetDirectoryName(result.ArchivePath!)!))!;
        Probe.Check("completion metadata includes the shared file and manifest",
            metadata.Platforms[GameOs.Windows].Files.ContainsKey("shared.txt")
            && metadata.Platforms[GameOs.Windows].Version.Manifests[SharedDepot.ToString()] == SharedManifest.ToString());

        fixture.DenySharedContent = true;
        var failedOutput = options with { OutputRoot = Path.Combine(root, "denied") };
        Probe.Check("an accessible manifest ID never bypasses a denial of the actual depot content",
            await RejectsAsync<SteamAuthenticationException>(() => steam.DownloadAsync("8870", failedOutput)));
        var incomplete = (await ArchiveCatalog.ScanAsync(failedOutput.OutputRoot)).Single();
        Probe.Check("a failed shared depot cannot produce a completed base-only archive",
            incomplete.Metadata.Platforms.Count == 0 && !Directory.EnumerateFiles(failedOutput.OutputRoot, "*.zip", SearchOption.AllDirectories).Any());
    }

    private static async Task FallbackAsync()
    {
        var fixture = new Session { Owned = [8870] };
        fixture.Apps[8870] = Game(8870, "Game");
        await using var steam = new SteamStore(fixture);
        Probe.Check("a public manifest cannot satisfy an inaccessible beta branch",
            await RejectsAsync<SteamAuthenticationException>(() => steam.GetVersionAsync("8870", new() { Branch = "beta" })));
        fixture.Apps[SharedApp] = new(SharedApp, "Shared source") { Depots = [new(SharedDepot)
            { Manifests = new() { ["public"] = 999, ["beta"] = 200 } }] };
        var version = await steam.GetVersionAsync("8870", new() { Branch = "beta" });
        Probe.Check("a missing local branch resolves its manifest from source appinfo",
            version.Branch == "beta" && version.Manifests[SharedDepot.ToString()] == "200");
        fixture.AppRequests.Clear();
        version = await steam.GetVersionAsync("8870", new());
        Probe.Check("the game-specific manifest takes precedence over a different source manifest",
            version.Manifests[SharedDepot.ToString()] == SharedManifest.ToString() && !fixture.AppRequests.Contains(SharedApp));

        var game = fixture.Apps[8870];
        fixture.Apps[8870] = game with { Depots = [game.Depots[0], game.Depots[1] with { Manifests = [] }] };
        version = await steam.GetVersionAsync("8870", new());
        Probe.Check("shared entries without local manifests still resolve through the source",
            version.Manifests[SharedDepot.ToString()] == "999");
        fixture.Apps[SharedApp] = fixture.Apps[SharedApp] with { Depots = [new(SharedDepot) { SharedFromApp = SharedApp }] };
        Probe.Check("unresolved shared-depot cycles remain errors",
            await RejectsAsync<InvalidDataException>(() => steam.GetVersionAsync("8870", new())));
        fixture.Apps[SharedApp] = fixture.Apps[SharedApp] with { Depots = [] };
        Probe.Check("missing source depots remain errors instead of disappearing from the archive",
            await RejectsAsync<InvalidDataException>(() => steam.GetVersionAsync("8870", new())));
    }

    private static async Task VariantFilteringAsync()
    {
        SteamDepot Local(uint id) => new(id) { Manifests = new() { ["public"] = id * 100 } };
        var fixture = new Session { Owned = [1] };
        var game = new SteamApp(1, "Variants") { Builds = new() { ["public"] = "1" }, Depots =
            [Local(10), new(20) { Language = "german", Architecture = "64", SharedFromApp = 999 },
                new(21) { Language = "english", Architecture = "32", SharedFromApp = 998 },
                Local(22) with { Language = "english", Architecture = "64" }] };
        fixture.Apps[1] = game;
        await using var steam = new SteamStore(fixture);
        var version = await steam.GetVersionAsync("1", new());
        Probe.Check("irrelevant language and architecture variants never request protected shared appinfo",
            version.Manifests.Keys.ToHashSet().SetEquals(["10", "22"]) && fixture.AppRequests.SequenceEqual([1u]));
        Probe.Check("requested language availability is still checked after skipping irrelevant sources",
            await RejectsAsync<NotSupportedException>(() => steam.GetVersionAsync("1", new() { Language = "fr" })));
        Probe.Check("requested architecture availability is still checked after skipping irrelevant sources",
            await RejectsAsync<NotSupportedException>(() => steam.GetVersionAsync("1", new() { Architecture = "arm64" })));
        Probe.Check("a selected language with inaccessible shared metadata still fails",
            await RejectsAsync<SteamAuthenticationException>(() => steam.GetVersionAsync("1", new() { Language = "de" })));
        fixture.Apps[1] = game with { Depots = [Local(10), new(22) { Language = "english", Architecture = "64", SharedFromApp = 997 }] };
        Probe.Check("a genuinely required source cannot be silently skipped",
            await RejectsAsync<SteamAuthenticationException>(() => steam.GetVersionAsync("1", new())));

        // An irrelevant variant is not evidence of language/architecture availability
        // until source-only OS and license constraints have been accounted for.
        foreach (var foreign in new[] { new SteamDepot(20) { Language = "german" }, new SteamDepot(20) { Architecture = "32" } })
        {
            fixture.Apps[1] = game with { Depots = [Local(10), foreign with { SharedFromApp = 2 }] };
            foreach (var excluded in new[] { Local(20) with { OsList = "linux" }, Local(20) with { DlcAppId = 5 } })
            {
                fixture.Apps[2] = new(2, "Restricted source") { Depots = [excluded] };
                fixture.AppRequests.Clear();
                version = await steam.GetVersionAsync("1", new());
                Probe.Check("source-only OS/license restrictions cannot make a valid selection unavailable",
                    version.Manifests.Keys.SequenceEqual(["10"]) && fixture.AppRequests.SequenceEqual([1u, 2u]));
            }
        }
        fixture.Apps[1] = game with { Depots = [Local(10), new(20) { SharedFromApp = 2, Language = "german" }] };
        fixture.Apps[2] = new(2, "Other architecture") { Depots = [Local(20) with { Architecture = "32" }] };
        // Include a known 64-bit variant so architecture availability itself is unambiguous.
        fixture.Apps[1] = fixture.Apps[1] with { Depots = [Local(10) with { Architecture = "64" }, fixture.Apps[1].Depots[1]] };
        version = await steam.GetVersionAsync("1", new());
        Probe.Check("source-only architecture restrictions cannot veto the selected language",
            version.Manifests.Keys.SequenceEqual(["10"]));
        fixture.Apps[2] = new(2, "Applicable language variant") { Depots = [Local(20) with { Architecture = "64" }] };
        Probe.Check("a resolved applicable foreign language still prevents an incomplete common-files-only archive",
            await RejectsAsync<NotSupportedException>(() => steam.GetVersionAsync("1", new())));
        fixture.Apps.Remove(2);
        Probe.Check("unresolved availability is reported as a metadata failure, not a partial success",
            await RejectsAsync<SteamAuthenticationException>(() => steam.GetVersionAsync("1", new())));
        fixture.Apps[2] = new(2, "Linux intermediate") { Depots = [new(20) { SharedFromApp = 3, OsList = "linux" }] };
        fixture.AppRequests.Clear();
        version = await steam.GetVersionAsync("1", new());
        Probe.Check("availability resolution stops once an intermediate constraint excludes the variant",
            version.Manifests.Keys.SequenceEqual(["10"]) && !fixture.AppRequests.Contains(3));

        fixture.Apps[1] = game with { Depots = [Local(10) with { Language = "english" },
            Local(11) with { Language = "german", Architecture = "32" }, new(20) { SharedFromApp = 2, Language = "german" }] };
        fixture.Apps[2] = new(2, "64-bit source") { Depots = [Local(20) with { Architecture = "64" }] };
        version = await steam.GetVersionAsync("1", new());
        Probe.Check("an inherited architecture can establish availability alongside common English content",
            version.Manifests.Keys.SequenceEqual(["10"]));
        fixture.Apps[1] = fixture.Apps[1] with { Depots = [Local(10) with { Language = "english" },
            Local(11) with { Language = "german", Architecture = "32" },
            new(20) { SharedFromApp = 2, Language = "german", Architecture = "64" }] };
        fixture.Apps[2] = new(2, "Linux source") { Depots = [Local(20) with { OsList = "linux" }] };
        Probe.Check("an unresolved foreign-OS variant cannot falsely establish architecture availability",
            await RejectsAsync<NotSupportedException>(() => steam.GetVersionAsync("1", new())));
        fixture.Owned = [1, 4];
        fixture.Apps[4] = new(4, "Owned expansion") { Type = "dlc", ParentAppId = 1 };
        fixture.Apps[1] = game with { Depots = [Local(10) with { Language = "english", Architecture = "64" },
            Local(11) with { DlcAppId = 4, Language = "english" }, Local(12) with { DlcAppId = 4, Language = "german", Architecture = "32" },
            new(20) { SharedFromApp = 2, Language = "german", Architecture = "64" }] };
        fixture.Apps[2] = new(2, "Expansion source") { Depots = [Local(20) with { DlcAppId = 4 }] };
        version = await steam.GetVersionAsync("1", new());
        Probe.Check("inherited DLC grouping can resolve architecture availability in another depot group",
            version.Manifests.Keys.ToHashSet().SetEquals(["10", "11"]));

        fixture.Owned = [1, 4];
        fixture.Apps[1] = game with { Depots = [Local(10), new(20) { SharedFromApp = 2 },
            Local(21) with { DlcAppId = 4, Language = "german", Architecture = "32" }] };
        fixture.Apps[2] = new(2, "Intermediate") { Depots = [new(20) { SharedFromApp = 3, OsList = "windows",
            DlcAppId = 4, Language = "english", Architecture = "64" }] };
        fixture.Apps[3] = new(3, "Final source") { Depots = [Local(20)] };
        fixture.Apps[4] = new(4, "Owned expansion") { Type = "dlc", ParentAppId = 1 };
        fixture.AppRequests.Clear();
        version = await steam.GetVersionAsync("1", new());
        Probe.Check("multi-hop shared depots retain intermediate architecture, language and DLC grouping",
            version.Manifests.Keys.ToHashSet().SetEquals(["10", "20"]) && fixture.AppRequests.SequenceEqual([1u, 4u, 2u, 3u]));
        Probe.Check("selection records both required metadata hops and the inherited DLC license",
            version.Dependencies.Contains(new("2", "shared-depot", "20"))
            && version.Dependencies.Contains(new("3", "shared-depot", "20"))
            && version.Dependencies.Contains(new("4", "dlc-license", "20", false)));
        fixture.Apps[1] = game with { Depots = [Local(10), new(20) { SharedFromApp = 2 }] };
        fixture.Apps[2] = new(2, "Linux intermediate") { Depots = [new(20) { SharedFromApp = 3, OsList = "linux" }] };
        fixture.AppRequests.Clear();
        version = await steam.GetVersionAsync("1", new());
        Probe.Check("an intermediate OS restriction stops traversal before an unrelated source",
            version.Manifests.Keys.SequenceEqual(["10"]) && !fixture.AppRequests.Contains(3));
        fixture.Apps[2] = new(2, "Unowned DLC intermediate") { Depots = [new(20) { SharedFromApp = 3, DlcAppId = 5 }] };
        fixture.AppRequests.Clear();
        version = await steam.GetVersionAsync("1", new());
        Probe.Check("an intermediate DLC license restriction cannot disappear at a later hop",
            version.Manifests.Keys.SequenceEqual(["10"]) && !fixture.AppRequests.Contains(3));
    }

    private static async Task KnownConstraintsAsync()
    {
        var fixture = new Session { Owned = [1, 4] };
        var foreign = new SteamDepot(20) { DlcAppId = 4, OsList = "windows", Architecture = "64", Language = "german", SharedFromApp = 999 };
        SteamApp Game(SteamDepot variant) => new(1, "Known variants") { Builds = new() { ["public"] = "1" }, Depots =
            [new(5) { OsList = "windows", Architecture = "64", Manifests = new() { ["public"] = 50 } },
             new(10) { DlcAppId = 4, OsList = "windows", Language = "english", Manifests = new() { ["public"] = 100 } },
             new(11) { DlcAppId = 4, OsList = "windows", Architecture = "32", Language = "german", Manifests = new() { ["public"] = 110 } }, variant] };
        fixture.Apps[4] = new(4, "Expansion") { Type = "dlc", ParentAppId = 1 };
        fixture.Apps[2] = new(2, "Intermediate constraints") { Depots = [foreign] };
        await using var steam = new SteamStore(fixture);
        foreach (var variant in new[] { foreign, foreign with { SharedFromApp = 2, OsList = "" } })
        {
            fixture.Apps[1] = Game(variant);
            fixture.AppRequests.Clear();
            GameVersion? version = null;
            try { version = await steam.GetVersionAsync("1", new()); }
            catch (SteamAuthenticationException) { }
            Probe.Check("known variant constraints establish availability without its unused manifest",
                version is not null && version.Manifests.Keys.ToHashSet().SetEquals(["5", "10"])
                && !fixture.AppRequests.Contains(999)
                && fixture.AppRequests.Contains(2) == (variant.SharedFromApp == 2));
        }
        foreach (var incomplete in new[] { foreign with { DlcAppId = 0 }, foreign with { OsList = "" },
            foreign with { Architecture = "" }, foreign with { Language = "" } })
        {
            fixture.Apps[1] = Game(incomplete);
            fixture.AppRequests.Clear();
            Probe.Check("unknown inherited constraints cannot be treated as fully known availability",
                await RejectsAsync<SteamAuthenticationException>(() => steam.GetVersionAsync("1", new()))
                && fixture.AppRequests.Contains(999));
        }
        fixture.Apps[1] = Game(foreign);
        Probe.Check("a selected fully constrained variant still requires its manifest",
            await RejectsAsync<SteamAuthenticationException>(() => steam.GetVersionAsync("1", new() { Language = "de" })));
        fixture.Apps[1] = Game(foreign with { Architecture = "32", Language = "" });
        fixture.AppRequests.Clear();
        Exception? failure = null;
        try { await steam.GetVersionAsync("1", new()); } catch (Exception error) { failure = error; }
        Probe.Check("a proven architecture mismatch does not query an irrelevant inherited language",
            failure is NotSupportedException && !fixture.AppRequests.Contains(999));
    }

    private static string Read(ZipArchive zip, string name)
    { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return reader.ReadToEnd(); }
    private static async Task<bool> RejectsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); return false; } catch (T) { return true; } }

    private sealed class Session : ISteamSession
    {
        public Dictionary<uint, SteamApp> Apps = [];
        public HashSet<uint> Owned = [];
        public List<uint> AppRequests = [];
        public List<SteamDepotRequest> ManifestRequests = [];
        public bool DenySharedContent;
        public Task<IReadOnlyDictionary<uint, string>> GetOwnedGamesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<uint, string>>(Apps.ToDictionary(p => p.Key, p => p.Value.Title));
        public Task<IReadOnlySet<uint>> GetOwnedAppIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlySet<uint>>(Owned);
        public Task<SteamApp> GetAppAsync(uint id, CancellationToken ct)
        {
            AppRequests.Add(id);
            return Task.FromResult(Apps.GetValueOrDefault(id) ?? throw new SteamAuthenticationException($"Appinfo {id} is unavailable."));
        }
        public Task<ISteamContentSession> OpenContentAsync(long rate, CancellationToken ct) => Task.FromResult<ISteamContentSession>(new Content(this));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Content(Session session) : ISteamContentSession
    {
        private static byte[] Bytes(uint depot) => Encoding.UTF8.GetBytes(depot == SharedDepot ? "shared content" : "base content");
        public Task<SteamManifest> GetManifestAsync(SteamDepotRequest depot, CancellationToken ct)
        {
            session.ManifestRequests.Add(depot);
            if (depot.DepotId == SharedDepot && session.DenySharedContent) throw new SteamAuthenticationException("Depot access denied.");
            var bytes = Bytes(depot.DepotId);
            var hash = Convert.ToHexStringLower(SHA1.HashData(bytes));
            return Task.FromResult(new SteamManifest(depot, [new(depot.DepotId == SharedDepot ? "shared.txt" : "game.exe",
                bytes.Length, hash, [new(hash, 0, 0, bytes.Length, bytes.Length)])]));
        }
        public Task<byte[]> GetChunkAsync(SteamDepotRequest depot, SteamChunk chunk, CancellationToken ct) => Task.FromResult(Bytes(depot.DepotId));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
