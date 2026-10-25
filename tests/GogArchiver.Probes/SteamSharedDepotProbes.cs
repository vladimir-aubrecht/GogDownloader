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
