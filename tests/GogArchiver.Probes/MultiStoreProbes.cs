using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GameArchiver;
using GameArchiver.Cli;
using GameArchiver.Core;
using GogArchiver.Core;
using GogArchiver.Core.Api;
using GogArchiver.Core.Download;
using GogArchiver.Core.Library;
using SteamArchiver.Core;
using SteamStore = SteamArchiver.Core.SteamClient;

namespace GogArchiver.Probes;

public static class MultiStoreProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Independent store APIs share the archive engine");
        Probe.Check("Steam depends on SteamKit2 and shared core, not GOG or the aggregator",
            typeof(SteamStore).Assembly.GetReferencedAssemblies().Any(a => a.Name == "SteamKit2")
            && typeof(SteamStore).Assembly.GetReferencedAssemblies().All(a => a.Name is not "GogArchiver.Core" and not "GameArchiver"));
        Probe.Check("GOG does not depend on Steam or the aggregator",
            typeof(GogClient).Assembly.GetReferencedAssemblies().All(a => a.Name is not "SteamArchiver.Core" and not "GameArchiver"));
        Probe.Check("shared core has no store dependencies",
            typeof(ArchivePublisher).Assembly.GetReferencedAssemblies().All(a => a.Name is not "GogArchiver.Core" and not "SteamArchiver.Core"));
        var root = Probe.ScratchDirectory();
        try
        {
            await MixedLibraryAsync(root);
            await ProviderLayoutAsync(root);
            await SteamSnapshotAsync(root);
            await SteamRecoveryAsync(root);
            await SteamFailuresAsync(root);
            await AggregateFailuresAsync(root);
            await MetadataAndClaimsAsync(root);
            await HttpCancellationAsync();
        }
        finally { Directory.Delete(root, true); }
    }

    private static ArchiveOptions Options(string root, string name) => new()
    {
        OutputRoot = Path.Combine(root, name), StagingRoot = Path.Combine(root, name + "-stage"),
        MaxConnections = 1, CompressionThreads = 2, WriteLauncher = false,
    };
    private static async Task MixedLibraryAsync(string root)
    {
        var options = Options(root, "mixed");
        var game = new LibraryEntry { Id = 1, Slug = "the_game", Title = "The Game",
            Platforms = new() { [GamePlatform.Windows] = new() { Depot = new("1", 2, "1", "https://fixture.invalid/repository") } } };
        var old = Path.Combine(options.OutputRoot, game.LegacyDirectoryName);
        Directory.CreateDirectory(old);
        var oldZip = Path.Combine(old, "GOG.zip");
        using (var zip = ZipFile.Open(oldZip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("gog.txt").Open())) writer.Write("GOG content");
        var originalBytes = await File.ReadAllBytesAsync(oldZip);
        await new BackupManifest { GameId = game.Id, Slug = game.Slug, Title = game.Title,
            Platforms = new() { [GamePlatform.Windows] = new() { BuildId = "1", ContentRevision = 1,
                Language = "en", ArchiveName = "GOG.zip", ArchiveSize = originalBytes.Length, InstalledSize = 11 } } }.SaveAsync(old);
        using var requests = new NoRequests();
        using var http = new HttpClient(requests);
        var gog = new GogClient(new(http), new(http, http, new(0)), http,
            new LibrarySnapshot { SchemaVersion = LibrarySnapshot.CurrentSchema, Games = [game] });
        var session = new SteamFixture();
        var steam = new SteamStore(session);
        await using var archive = new GameArchiverClient(gog, steam);
        var foreign = Path.Combine(options.OutputRoot, "unrelated"); Directory.CreateDirectory(foreign);
        await File.WriteAllTextAsync(Path.Combine(foreign, ArchiveMetadata.FileName),
            """{"SchemaVersion":3,"Source":"epic","ProductId":"external","Platforms":"another schema"}""");
        var gogResult = await gog.DownloadAsync("1", options);
        Probe.Check("standalone GOG API migrates a completed backup without network or re-zipping",
            gogResult.Outcome == ArchiveOutcome.AlreadyDone && requests.Calls == 0 && !Directory.Exists(old)
            && (await File.ReadAllBytesAsync(gogResult.ArchivePath!)).SequenceEqual(originalBytes));
        var steamResult = await archive.DownloadAsync(new("steam", "1"), options);
        Probe.Check("same numeric product ID and slug coexist across stores without ID suffixes",
            steamResult.Outcome == ArchiveOutcome.Completed && Path.GetFileName(Path.GetDirectoryName(steamResult.ArchivePath)) == "the_game_steam"
            && (await File.ReadAllBytesAsync(gogResult.ArchivePath!)).SequenceEqual(originalBytes));
        var metadata = (await ArchiveMetadata.LoadAsync(Path.GetDirectoryName(steamResult.ArchivePath!)!))!;
        Probe.Check("Steam metadata records origin, product ID, depot manifests and launch information",
            metadata.Key == new GameKey("steam", "1") && metadata.Platforms[GameOs.Windows].Version.Manifests.Count == 3
            && metadata.Platforms[GameOs.Windows].LaunchExecutable == "game.exe"
            && metadata.Platforms[GameOs.Windows].RuntimeRequirement.Contains("not-verified"));
        using (var zip = ZipFile.OpenRead(steamResult.ArchivePath!))
        {
            Probe.Check("Steam selects Windows 64-bit English and dereferences directory links",
                Read(zip, "game.exe") == "BASE" && Read(zip, "language.txt") == "ENGLISH"
                && Read(zip, "copy/data.txt") == "assets" && zip.GetEntry("empty.txt") is { Length: 0 }
                && zip.GetEntry("unused/") is not null && zip.GetEntry("arch32.txt") is null);
        }
        var calls = session.ChunkCalls;
        Probe.Check("a current Steam backup skips without reading manifests or chunks",
            (await archive.DownloadAsync(new("steam", "1"), options)).Outcome == ArchiveOutcome.AlreadyDone && session.ChunkCalls == calls);
        session.Owned.Add(2);
        var dlc = await archive.DownloadAsync(new("steam", "2"), options);
        using (var zip = ZipFile.OpenRead(dlc.ArchivePath!))
            Probe.Check("selecting owned DLC selects its base game and its files override the base depot",
                dlc.Key == new GameKey("steam", "1") && Read(zip, "game.exe") == "EXPANSION"
                && session.ManifestRequests.Last(r => r.DepotId == 50).AppId == 1);
        Probe.Check("newly owned DLC replaces a backup at an unchanged base build",
            (await ArchiveMetadata.LoadAsync(Path.GetDirectoryName(dlc.ArchivePath!)!))!.Platforms[GameOs.Windows].Version.Dlcs.SequenceEqual(["2"])
            && !File.Exists(steamResult.ArchivePath));
        var linux = await archive.DownloadAsync(new("steam", "1"), options with { Platform = GameOs.Linux });
        Probe.Check("adding another OS preserves the completed Windows backup",
            linux.Outcome == ArchiveOutcome.Completed && File.Exists(dlc.ArchivePath)
            && (await ArchiveMetadata.LoadAsync(Path.GetDirectoryName(linux.ArchivePath!)!))!.Platforms.Count == 2);
        var before = session.ChunkCalls;
        session.Revision++;
        var updated = await archive.DownloadAsync(new("steam", "1"), options);
        Probe.Check("changed depot manifest IDs invalidate the backup even at the same Steam build ID",
            updated.Outcome == ArchiveOutcome.Completed && updated.ArchivePath != dlc.ArchivePath && !File.Exists(dlc.ArchivePath));
        Probe.Check("unchanged Steam files are restored from the previous ZIP", session.ChunkCalls == before);
        var beta = await steam.GetVersionAsync("1", options with { Branch = "beta", Language = "de" });
        Probe.Check("branch and language selection follow Steam appinfo", beta.Branch == "beta" && beta.BuildId == "2"
            && beta.Manifests.ContainsKey("104") && !beta.Manifests.ContainsKey("103"));
        var library = new ArchiveLibrary { Games = GogClient.ConvertLibrary(new LibrarySnapshot { Games = [game] }).ToList() };
        library.Games.AddRange((await steam.SyncLibraryAsync()).Games);
        var diff = await archive.DiffAsync(library, options);
        Probe.Check("aggregate diff treats GOG and Steam as independent identities", diff.Count == 2 && diff.All(d => d.Status == ArchiveStatus.Current),
            string.Join(", ", diff.Select(d => $"{d.Game.Key}: {d.Status} {d.Detail}")));
        var verify = await archive.VerifyAsync(options.OutputRoot, deep: true);
        Probe.Check("aggregate verification checks both formats and all completed operating systems",
            verify.Count == 3 && verify.All(v => v.Problems.Count == 0));
        Probe.Check("unsupported modern sources are skipped before interpreting their schema",
            await File.ReadAllTextAsync(Path.Combine(foreign, ArchiveMetadata.FileName)) ==
                """{"SchemaVersion":3,"Source":"epic","ProductId":"external","Platforms":"another schema"}""");
        var snapshot = Path.Combine(root, "aggregate.json");
        await library.SaveAsync(snapshot);
        var roundtrip = await ArchiveLibrary.LoadAsync(snapshot);
        Probe.Check("aggregate snapshot preserves source-qualified product identities", roundtrip.Games.Select(g => g.Key).Distinct().Count() == library.Games.Count);
        await CliAsync(snapshot, options, root);
        var legacyManifest = (await BackupManifest.LoadAsync(Path.GetDirectoryName(gogResult.ArchivePath!)!))!;
        legacyManifest.Platforms[GamePlatform.Windows] = legacyManifest.Platforms[GamePlatform.Windows]
            with { ArchiveName = "../../outside.zip" };
        await legacyManifest.SaveAsync(Path.GetDirectoryName(gogResult.ArchivePath!)!);
        Probe.Check("GOG archive references cannot escape their backup directory",
            await ThrowsAsync(() => gog.DownloadAsync("1", options with { Force = true })) && requests.Calls == 0
            && (await File.ReadAllBytesAsync(gogResult.ArchivePath!)).SequenceEqual(originalBytes));
    }

    private static async Task ProviderLayoutAsync(string root)
    {
        var options = Options(root, "providers") with { Layout = ArchiveLayout.Provider };
        var game = new LibraryEntry { Id = 1, Slug = "the_game", Title = "The Game",
            Platforms = new() { [GamePlatform.Windows] = new() { Depot = new("1", 2, "1", "https://fixture.invalid/repository") } } };
        var snapshot = new LibrarySnapshot { SchemaVersion = LibrarySnapshot.CurrentSchema, Games = [game] };
        var legacy = Path.Combine(options.OutputRoot, "gog", game.LegacyDirectoryName);
        Directory.CreateDirectory(legacy);
        var zipPath = Path.Combine(legacy, "GOG.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("gog.txt").Open())) writer.Write("GOG content");
        var bytes = await File.ReadAllBytesAsync(zipPath);
        await new BackupManifest { GameId = 1, Slug = game.Slug, Title = game.Title, Platforms = new()
        { [GamePlatform.Windows] = new() { BuildId = "1", ContentRevision = 1, Language = "en", ArchiveName = "GOG.zip",
            ArchiveSize = bytes.Length, InstalledSize = 11 } } }.SaveAsync(legacy);
        using var requests = new NoRequests();
        using var http = new HttpClient(requests);
        var gog = new GogClient(new(http), new(http, http, new(0)), http, snapshot);
        var fixture = new SteamFixture();
        var steam = new SteamStore(fixture);
        await using var archive = new GameArchiverClient(gog, steam);
        var gogResult = await gog.DownloadAsync("1", options);
        var steamResult = await steam.DownloadAsync("1", options);
        Probe.Check("standalone GOG and Steam clients honor provider output folders",
            gogResult.ArchivePath == Path.Combine(options.OutputRoot, "gog", "the_game", "GOG.zip")
            && Path.GetDirectoryName(steamResult.ArchivePath) == Path.Combine(options.OutputRoot, "steam", "the_game")
            && gogResult.Outcome == ArchiveOutcome.AlreadyDone && steamResult.Outcome == ArchiveOutcome.Completed);
        Probe.Check("legacy folder migration within a provider reuses GOG content without network",
            !Directory.Exists(legacy) && requests.Calls == 0 && bytes.SequenceEqual(await File.ReadAllBytesAsync(gogResult.ArchivePath!)));
        var calls = fixture.ManifestCalls;
        var again = await archive.DownloadAsync(new("steam", "1"), options);
        Probe.Check("aggregate routing does not nest an additional provider folder or rebuild the archive",
            again.Outcome == ArchiveOutcome.AlreadyDone && again.ArchivePath == steamResult.ArchivePath
            && fixture.ManifestCalls == calls && !Directory.Exists(Path.Combine(options.OutputRoot, "steam", "steam")));
        var library = new ArchiveLibrary { Games = GogClient.ConvertLibrary(snapshot)
            .Concat((await steam.SyncLibraryAsync()).Games).ToList() };
        Probe.Check("aggregate diff and verification read actual provider archives",
            (await archive.DiffAsync(library, options)) is { Count: 2 } rows && rows.All(r => r.Status == ArchiveStatus.Current)
            && (await archive.VerifyAsync(options, deep: true)) is { Count: 2 } checks && checks.All(r => r.Problems.Count == 0));
    }

    private static async Task SteamSnapshotAsync(string root)
    {
        var options = Options(root, "steam-snapshot");
        var fixture = new SteamFixture { Owned = [1, 2] };
        var steam = new SteamStore(fixture);
        await using var archive = new GameArchiverClient(steam);
        var downloaded = await steam.DownloadAsync("1", options);
        await steam.DownloadAsync("1", options with { Platform = GameOs.Linux });
        var before = fixture.OwnershipCalls;
        var manifestCalls = fixture.ManifestCalls;
        var library = await archive.SyncLibraryAsync();
        var version = library.Games.Single(g => g.Key.ProductId == "1").Versions[GameOs.Windows];
        var backup = (await ArchiveMetadata.LoadAsync(Path.GetDirectoryName(downloaded.ArchivePath!)!))!;
        Probe.Check("Steam sync records the exact default base, shared and DLC manifest selection",
            version.Manifests.Count == 4 && version.Matches(backup.Platforms[GameOs.Windows].Version)
            && fixture.OwnershipCalls == before + 1 && fixture.ManifestCalls == manifestCalls);
        Probe.Check("offline Steam diff recognizes an unchanged full content selection",
            (await archive.DiffAsync(library, options)).Single().Status == ArchiveStatus.Current);

        fixture.DlcRevision++;
        library = await archive.SyncLibraryAsync(library);
        var path = Path.Combine(root, "steam-snapshot.json");
        await library.SaveAsync(path);
        library = await ArchiveLibrary.LoadAsync(path);
        var current = library.Games.Single(g => g.Key.ProductId == "1");
        Probe.Check("offline diff detects a DLC-only update after sync and snapshot roundtrip",
            current.Versions[GameOs.Windows].BuildId == version.BuildId
            && current.Versions[GameOs.Windows].Manifests["50"] != version.Manifests["50"]
            && (await archive.DiffAsync(library, options)).Single().Status == ArchiveStatus.Outdated);
        Probe.Check("DLC changes outside the selected OS do not invalidate its backup",
            (await archive.DiffAsync(library, options with { Platform = GameOs.Linux })).Single().Status == ArchiveStatus.Current);
        var output = await CaptureAsync(() => ArchiveCommandLine.RunAsync(
            ["diff", "--keys", "--source", "steam", "--snapshot", path, "--out", options.OutputRoot],
            storeFactory: _ => [new FakeStore("steam")]));
        Probe.Check("offline diff --keys includes the base game after a DLC-only update",
            output.Exit == 0 && output.Text.Trim() == "steam:1");

        foreach (var selection in new[] { options with { Language = "de" }, options with { Architecture = "32" }, options with { Branch = "beta" } })
            Probe.Check($"offline diff requires refresh outside the saved selection ({selection.Language}/{selection.Architecture}/{selection.Branch})",
                (await archive.DiffAsync(library, selection)).Single().Status == ArchiveStatus.Unknown);
        var legacy = library with { Games = [current with { Versions = new()
            { [GameOs.Windows] = current.Versions[GameOs.Windows] with { Manifests = [] } } }] };
        Probe.Check("older Steam snapshots without manifests never report a backup current",
            (await archive.DiffAsync(legacy, options)).Single().Status == ArchiveStatus.Unknown);
        Probe.Check("live refresh still resolves an older Steam snapshot",
            (await archive.DiffAsync(legacy, options, refreshVersions: true)).Single().Status == ArchiveStatus.Outdated);

        fixture.FailShared = true;
        var partial = await archive.SyncLibraryAsync(library);
        Probe.Check("a failed shared-depot lookup retains the previous Steam entry and records a sync failure",
            partial.Failures.ContainsKey("steam:1")
            && partial.Games.Single(g => g.Key.ProductId == "1").Versions[GameOs.Windows].Matches(current.Versions[GameOs.Windows]));
    }

    private static async Task SteamRecoveryAsync(string root)
    {
        Probe.Group("Steam resume, integrity and output ownership");
        var options = Options(root, "resume");
        var fixture = new SteamFixture();
        await using var steam = new SteamStore(fixture);
        using var interrupt = new CancellationTokenSource();
        var cancelled = false;
        try
        {
            await steam.DownloadAsync("1", options, new InlineProgress<ArchiveProgress>(p =>
            { if (p.Phase == "download" && p.Bytes > 0) interrupt.Cancel(); }), interrupt.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        var calls = fixture.ChunkCalls;
        Probe.Check("interruption never marks a partially downloaded Steam game completed", cancelled && calls == 1
            && (await ArchiveMetadata.LoadAsync(Path.Combine(options.OutputRoot, "the_game")))!.Platforms.Count == 0);
        if (!OperatingSystem.IsWindows())
        {
            var staged = Directory.EnumerateFiles(options.StagingDirectory, "game.exe", SearchOption.AllDirectories).Single();
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(staged)!, "GAME.EXE"), "stale case variant");
        }
        var completed = await steam.DownloadAsync("1", options);
        Probe.Check("Steam resumes verified chunks inside a partially downloaded file", completed.Outcome == ArchiveOutcome.Completed
            && fixture.ChunkRequests.Count(r => r == SteamFixture.Hash("BA")) == 1);
        using (var zip = ZipFile.OpenRead(completed.ArchivePath!))
            Probe.Check("resuming a Windows game on Linux removes stale case variants before ZIP publication",
                zip.Entries.Count(e => e.FullName.Equals("game.exe", StringComparison.OrdinalIgnoreCase)) == 1);
        var metadata = (await ArchiveMetadata.LoadAsync(Path.GetDirectoryName(completed.ArchivePath!)!))!;
        await File.WriteAllBytesAsync(completed.ArchivePath!, new byte[3]);
        Probe.Check("a truncated Steam archive is rebuilt at the same version", (await steam.DownloadAsync("1", options)).Outcome == ArchiveOutcome.Completed);
        metadata = (await ArchiveMetadata.LoadAsync(Path.GetDirectoryName(completed.ArchivePath!)!))!;
        var oldArchive = Path.Combine(Path.GetDirectoryName(completed.ArchivePath!)!, metadata.Platforms[GameOs.Windows].ArchiveName);
        using (var corrupt = ZipFile.Open(oldArchive, ZipArchiveMode.Update))
        { var entry = corrupt.GetEntry("assets/data.txt")!; entry.Delete(); using var writer = new StreamWriter(corrupt.CreateEntry("assets/data.txt").Open()); writer.Write("broken"); }
        calls = fixture.ChunkCalls;
        fixture.Revision++;
        var repaired = await steam.DownloadAsync("1", options);
        using (var zip = ZipFile.OpenRead(repaired.ArchivePath!))
            Probe.Check("cached Steam files are rehashed and corrupt payloads are downloaded again",
                fixture.ChunkCalls > calls && Read(zip, "assets/data.txt") == "assets");
        var foreignOptions = Options(root, "foreign");
        var directory = Path.Combine(foreignOptions.OutputRoot, "the_game");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "keep.zip"), "foreign");
        Probe.Check("Steam cannot adopt an unidentified directory even with force",
            await ThrowsAsync(() => steam.DownloadAsync("1", foreignOptions with { Force = true }))
            && await File.ReadAllTextAsync(Path.Combine(directory, "keep.zip")) == "foreign");
        var custom = await steam.DownloadAsync("1", foreignOptions with { DirectoryName = "the_game_custom" });
        Probe.Check("explicit folder aliases are remembered by source and product ID",
            (await steam.DownloadAsync("1", foreignOptions)).ArchivePath == custom.ArchivePath);
        var parallelFixture = new SteamFixture { Fault = "single-file" };
        await using var parallelSteam = new SteamStore(parallelFixture);
        var parallel = await parallelSteam.DownloadAsync("1", Options(root, "parallel") with { MaxConnections = 3 });
        Probe.Check("a single large Steam file uses concurrent chunks under the global connection limit",
            parallel.Outcome == ArchiveOutcome.Completed && parallelFixture.MaxInFlight is >= 2 and <= 3);
    }

    private static async Task SteamFailuresAsync(string root)
    {
        foreach (var mode in new[] { "hash", "path", "cycle", "missing-link", "chunk-map", "no-files", "manifest-id" })
        {
            var fixture = new SteamFixture { Fault = mode };
            await using var client = new SteamStore(fixture);
            var options = Options(root, mode);
            var rejected = await ThrowsAsync(() => client.DownloadAsync("1", options));
            var metadata = await ArchiveMetadata.LoadAsync(Path.Combine(options.OutputRoot, "the_game"));
            Probe.Check($"invalid Steam content ({mode}) cannot publish a completed archive", rejected && metadata?.Platforms.Count == 0
                && !Directory.EnumerateFiles(options.OutputRoot, "*.zip", SearchOption.AllDirectories).Any());
        }
        var notOwned = new SteamFixture(); notOwned.Owned.Clear();
        await using var unauthorized = new SteamStore(notOwned);
        Probe.Check("unlicensed apps are rejected before depot access",
            await ThrowsAsync(() => unauthorized.DownloadAsync("1", Options(root, "unowned"))) && notOwned.ManifestCalls == 0);
        await using var unsupported = new SteamStore(new SteamFixture());
        Probe.Check("unsupported language and private branch selections fail before publication",
            await ThrowsAsync(() => unsupported.GetVersionAsync("1", Options(root, "lang") with { Language = "fr" }))
            && await ThrowsAsync(() => unsupported.GetVersionAsync("1", Options(root, "branch") with { Branch = "private" })));
    }

    private static async Task AggregateFailuresAsync(string root)
    {
        Probe.Group("Aggregate API preserves partial libraries");
        var oldGame = new GameInfo(new("steam", "7"), "Old Steam", "old_steam")
            { Versions = new() { [GameOs.Windows] = new("1") } };
        var gogGame = new GameInfo(new("gog", "7"), "Old GOG", "old_gog");
        var old = new ArchiveLibrary { Games = [oldGame, gogGame] };
        await using var failed = new GameArchiverClient(new FakeStore("steam") { Failure = true });
        var snapshot = await failed.SyncLibraryAsync(old);
        Probe.Check("a failed store sync preserves its previous games and unselected sources",
            snapshot.Games.Count == 2 && snapshot.Failures.ContainsKey("steam"));
        Probe.Check("a retained failed-sync product is not presented as current",
            (await failed.DiffAsync(snapshot, Options(root, "failed-sync"))).Single().Status == ArchiveStatus.Unknown);
        await using var partial = new GameArchiverClient(new FakeStore("steam")
        { Result = new([]) { Failures = new Dictionary<string, string> { ["7"] = "offline" } } });
        var partialSnapshot = await partial.SyncLibraryAsync(old);
        Probe.Check("per-product failures preserve the previous product instead of dropping it",
            partialSnapshot.Games.Count == 2 && partialSnapshot.Failures.ContainsKey("steam:7"));
        await using var duplicate = new GameArchiverClient(new FakeStore("steam") { Result = new([oldGame, oldGame]) });
        Probe.Check("duplicate identities from a provider are rejected", (await duplicate.SyncLibraryAsync()).Failures.ContainsKey("steam"));
        Probe.Check("unsupported sources cannot be routed to a different store",
            await ThrowsAsync(() => partial.DownloadAsync(new("epic", "7"), Options(root, "route"))));
    }

    private static async Task MetadataAndClaimsAsync(string root)
    {
        Probe.Group("Steam appinfo and metadata ownership");
        var windows = new PlatformAvailability { Depot = new("1", 2, "1", "https://fixture.invalid/repository") };
        var main = new LibraryEntry { Id = 1, Slug = "base", Title = "Base", Dlcs = [new("2", "Expansion")],
            Platforms = new() { [GamePlatform.Windows] = windows } };
        var expansion = new LibraryEntry { Id = 2, Slug = "expansion", Title = "Expansion",
            Platforms = new() { [GamePlatform.Windows] = windows, [GamePlatform.Linux] = new() { IsLinux = true, Installers = [new("en", "1", 1)] } } };
        var duplicatePack = new LibraryEntry { Id = 3, Slug = "pack", Title = "Pack", IsPack = true,
            IncludedGames = [1], Platforms = new() { [GamePlatform.Windows] = windows } };
        var standalonePack = duplicatePack with { Id = 4, Slug = "other_pack", IncludedGames = [999] };
        var products = GogClient.ConvertLibrary(new() { Games = [main, expansion, duplicatePack, standalonePack] });
        Probe.Check("aggregate GOG selection preserves OS-specific DLC and standalone pack rules",
            !products.Single(g => g.Key.ProductId == "2").CanBackUp(GameOs.Windows)
            && products.Single(g => g.Key.ProductId == "2").CanBackUp(GameOs.Linux)
            && !products.Single(g => g.Key.ProductId == "3").CanBackUp(GameOs.Windows)
            && products.Single(g => g.Key.ProductId == "4").CanBackUp(GameOs.Windows));
        var raw = SteamKit2.KeyValue.LoadFromString("""
            "appinfo" {
              "common" { "name" "Test Game" "type" "Game" "oslist" "windows,linux,macos" }
              "extended" { "listofdlc" "2,3" }
              "config" { "launch" { "0" { "executable" "bin/game.exe" "arguments" "--mode test" "config" { "oslist" "windows" } } } }
              "depots" {
                "branches" { "public" { "buildid" "100" } }
                "10" { "config" { "oslist" "windows" "osarch" "64" "language" "english" } "manifests" { "public" { "gid" "9876543210123456" } } }
                "20" { "depotfromapp" "99" "dlcappid" "2" }
              }
            }
            """)!;
        var app = SteamApp.Parse(1, raw);
        Probe.Check("Steam VDF parsing preserves 64-bit manifest IDs and depot constraints",
            app.Depots[0].Manifests["public"] == 9876543210123456UL && app.Depots[0].Language == "english"
            && app.Depots[0].Architecture == "64" && app.Depots[1].SharedFromApp == 99 && app.Depots[1].DlcAppId == 2);
        Probe.Check("Steam VDF parsing preserves supported OS, DLC and launch tasks",
            app.Platforms.Count == 3 && app.Dlcs.SequenceEqual([2u, 3u]) && app.Launches.Single().Executable == "bin/game.exe");
        var path = Path.Combine(root, "private", "steam-tokens.json");
        await SteamTokenFile.SaveAsync(path, new("account", "fixture-token", "fixture-guard"), default);
        Probe.Check("Steam remembered sessions roundtrip without a stored password",
            (await SteamTokenFile.LoadAsync(path, default))?.RefreshToken == "fixture-token"
            && !(await File.ReadAllTextAsync(path)).Contains("Password"));
        if (!OperatingSystem.IsWindows())
            Probe.Check("Steam session files are private from creation", File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
        var safe = true;
        for (var round = 0; round < 40; round++)
        {
            var options = Options(root, "claims" + round);
            using var start = new ManualResetEventSlim();
            async Task<GameKey?> Claim(string id)
            {
                start.Wait();
                try { return (await ArchiveCatalog.ReserveAsync(new(new("steam", id), "Race", "race"), options, default)).Metadata.Key; }
                catch (IOException) { return null; }
            }
            var first = Task.Run(() => Claim("1")); var second = Task.Run(() => Claim("2")); start.Set();
            var owners = await Task.WhenAll(first, second);
            var stored = await ArchiveMetadata.LoadAsync(Path.Combine(options.OutputRoot, "race"));
            safe &= owners.Count(k => k is not null) == 1 && owners.Single(k => k is not null) == stored?.Key;
        }
        Probe.Check("concurrent same-source slug claims cannot overwrite the winning product identity", safe);
        var lockRoot = Path.Combine(root, "reservation-lock");
        using (await ArchiveReservation.AcquireAsync(lockRoot, default))
        {
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var interrupted = false;
            try { using var unexpected = await ArchiveReservation.AcquireAsync(lockRoot, cancelled.Token); }
            catch (OperationCanceledException) { interrupted = true; }
            Probe.Check("directory reservations exclude other allocators and remain cancellable", interrupted);
        }
        var ownerPath = Path.Combine(lockRoot, "owner.json");
        var created = await ArchiveReservation.CreateOwnerAsync(ownerPath, new { Id = 1 }, ArchiveJson.Options, default);
        var replaced = await ArchiveReservation.CreateOwnerAsync(ownerPath, new { Id = 2 }, ArchiveJson.Options, default);
        Probe.Check("exclusive ownership creation cannot replace an existing identity",
            created && !replaced && (await File.ReadAllTextAsync(ownerPath)).Contains("\"Id\": 1"));
        var bad = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["diff", "--connections", "not-a-number"], storeFactory: _ => []));
        Probe.Check("malformed numeric CLI options report a normal failure", bad.Exit == 1);
    }

    private static async Task CliAsync(string snapshot, ArchiveOptions options, string root)
    {
        IEnumerable<IGameStore> Stores(string source)
        {
            if (source is "all" or "gog") yield return new FakeStore("gog");
            if (source is "all" or "steam") yield return new FakeStore("steam");
        }
        var common = new[] { "--snapshot", snapshot, "--out", options.OutputRoot };
        var listed = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["library", "list", "--source", "steam", "--keys", .. common], storeFactory: Stores));
        Probe.Check("shared CLI filters source-qualified library keys", listed.Exit == 0 && listed.Text.Contains("steam:1") && !listed.Text.Contains("gog:1"));
        var ambiguous = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "1", "--dry-run", .. common], storeFactory: Stores));
        Probe.Check("shared CLI rejects an ambiguous bare product ID", ambiguous.Exit == 1 && ambiguous.Text.Contains("ambiguous"));
        var direct = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "steam:1", "--dry-run", "--snapshot", Path.Combine(root, "absent.json")], storeFactory: Stores));
        Probe.Check("explicit source:ID download does not require an aggregate snapshot", direct.Exit == 0 && direct.Text.Contains("steam:1"));
        var wrongSource = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "steam:1", "--source", "gog", "--dry-run", .. common], storeFactory: Stores));
        Probe.Check("shared CLI cannot download outside its source filter", wrongSource.Exit == 1);
        var typo = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["library", "sync", "--soruce", "steam"], storeFactory: Stores));
        Probe.Check("unknown CLI flags fail before starting a sync", typo.Exit == 1 && typo.Text.Contains("Unknown option"));
        var verified = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["verify", "--deep", "--snapshot", Path.Combine(root, "absent.json"), "--out", options.OutputRoot], storeFactory: Stores));
        Probe.Check("shared CLI verifies both providers without authentication or a snapshot", verified.Exit == 0 && verified.Text.Contains("3 sound"));
    }

    private static async Task HttpCancellationAsync()
    {
        Probe.Group("SteamKit2 HTTP cancellation bridge");
        using var cancellation = new CancellationTokenSource();
        var source = new BlockingStream();
        using var handler = new SteamContentHttpHandler(new(cancellation.Token, new(0))) { InnerHandler = new StreamHandler(source) };
        using var http = new HttpClient(handler);
        using var response = await http.GetAsync("https://fixture.invalid/chunk", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var read = stream.ReadAsync(new byte[32]).AsTask();
        cancellation.Cancel();
        var stopped = false;
        try { await read.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { stopped = true; }
        Probe.Check("cancelling a Steam CDN body aborts and awaits its outstanding read", stopped && source.Disposed);
    }

    private static string Read(ZipArchive zip, string name) { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return reader.ReadToEnd(); }
    private static async Task<bool> ThrowsAsync(Func<Task> action) { try { await action(); return false; } catch (Exception e) when (e is not OperationCanceledException) { return true; } }
    private static async Task<(int Exit, string Text)> CaptureAsync(Func<Task<int>> action)
    {
        var oldOut = Console.Out; var oldError = Console.Error; using var output = new StringWriter();
        try { Console.SetOut(output); Console.SetError(output); return (await action(), output.ToString()); }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }
    private sealed class NoRequests : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; throw new IOException("Unexpected GOG request."); }
    }
    private sealed class FakeStore(string source) : IGameStore
    {
        public string Source => source;
        public bool Failure;
        public StoreLibraryResult Result = new([]);
        public Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default) => Failure ? throw new IOException("store offline") : Task.FromResult(Result);
        public Task<GameVersion> GetVersionAsync(string productId, GameSelection options, CancellationToken ct = default) => Task.FromResult(new GameVersion("1"));
        public Task<ArchiveResult> DownloadAsync(string productId, ArchiveOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class SteamFixture : ISteamSession
    {
        public Task<IReadOnlyDictionary<uint, string>> GetOwnedGamesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<uint, string>>(Owned.ToDictionary(id => id, id => $"Listed app {id}"));
        public HashSet<uint> Owned = [1];
        public int Revision;
        public int DlcRevision, OwnershipCalls;
        public bool FailShared;
        public string? Fault;
        public int ChunkCalls, ManifestCalls;
        public int InFlight, MaxInFlight;
        public List<string> ChunkRequests = [];
        public List<SteamDepotRequest> ManifestRequests = [];
        private readonly Dictionary<string, byte[]> _chunks = [];
        public Task<IReadOnlySet<uint>> GetOwnedAppIdsAsync(CancellationToken ct)
        { OwnershipCalls++; return Task.FromResult<IReadOnlySet<uint>>(Owned); }
        public Task<SteamApp> GetAppAsync(uint id, CancellationToken ct)
        {
            if (id == 999 && FailShared) throw new IOException("Shared appinfo is unavailable.");
            SteamDepot Depot(uint depot, string os = "windows", string language = "", string arch = "", uint dlc = 0) => new(depot)
            { OsList = os, Language = language, Architecture = arch, DlcAppId = dlc,
                Manifests = new() { ["public"] = depot * 100 + (ulong)Revision + 1 + (depot == 50 ? (ulong)DlcRevision : 0),
                    ["beta"] = depot * 100 + 50 + (ulong)Revision } };
            return Task.FromResult(id switch
            {
                1 => new SteamApp(1, "The Game") { Dlcs = [2], Platforms = [GameOs.Windows, GameOs.Linux, GameOs.Mac],
                    Launches = [new("game.exe", "--test", "windows")],
                    Builds = new() { ["public"] = "1", ["beta"] = "2" },
                    Depots = [Depot(50, dlc: 2), Depot(100), Depot(103, language: "english", arch: "64"),
                        Depot(104, language: "german", arch: "64"), Depot(105, arch: "32"), Depot(106, "linux"), Depot(107, "macos"),
                        new(200) { SharedFromApp = 999 }] },
                2 => new SteamApp(2, "Expansion") { Type = "dlc", ParentAppId = 1, Depots = [Depot(50)] },
                999 => new SteamApp(999, "Shared content") { Depots = [Depot(200, "")] },
                _ => throw new IOException("unknown app"),
            });
        }
        public Task<ISteamContentSession> OpenContentAsync(long maxBytesPerSecond, CancellationToken ct) => Task.FromResult<ISteamContentSession>(new Content(this));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public static string Hash(string text) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text)));
        private sealed class Content(SteamFixture fixture) : ISteamContentSession
        {
            private SteamFile File(string name, string text, bool split = false)
            {
                var bytes = Encoding.UTF8.GetBytes(text); var chunks = new List<SteamChunk>();
                var length = split ? Math.Max(1, bytes.Length / 2) : Math.Max(1, bytes.Length);
                for (var offset = 0; offset < bytes.Length; offset += length)
                {
                    var part = bytes[offset..Math.Min(bytes.Length, offset + length)];
                    var hash = Convert.ToHexStringLower(SHA1.HashData(part)); fixture._chunks[hash] = part;
                    chunks.Add(new(hash, 0, offset, part.Length, part.Length));
                }
                return new(name, bytes.Length, Hash(text), chunks);
            }
            public Task<SteamManifest> GetManifestAsync(SteamDepotRequest depot, CancellationToken ct)
            {
                fixture.ManifestCalls++;
                fixture.ManifestRequests.Add(depot);
                var files = depot.DepotId switch
                {
                    50 => new List<SteamFile> { File("game.exe", "EXPANSION") },
                    100 => [File("game.exe", depot.Branch == "beta" ? "BETA" : "BASE", true), File("assets/data.txt", "assets"), File("empty.txt", ""),
                        new("unused/", 0, "", []) { IsDirectory = true }, new("copy", 0, "", []) { IsDirectory = true, LinkTarget = "assets" }],
                    103 => [File("language.txt", "ENGLISH")], 104 => [File("language.txt", "GERMAN")],
                    105 => [File("arch32.txt", "32-bit")], 106 => [File("start.sh", "linux")], 107 => [File("Game.app/game", "macOS")],
                    200 => [File("shared.txt", "shared")], _ => [],
                };
                if (depot.DepotId == 100)
                {
                    switch (fixture.Fault)
                    {
                        case "hash": files[0] = files[0] with { Sha1 = new string('0', 40) }; break;
                        case "path": files[0] = files[0] with { Path = "../escaped" }; break;
                        case "cycle": files.Add(new("loop", 0, "", []) { LinkTarget = "loop" }); break;
                        case "missing-link": files.Add(new("lost", 0, "", []) { LinkTarget = "absent" }); break;
                        case "chunk-map": files[0] = files[0] with { Size = 100 }; break;
                    }
                }
                if (fixture.Fault == "no-files") files.Clear();
                if (fixture.Fault == "single-file") files = depot.DepotId == 100 ? [files[0]] : [];
                return Task.FromResult(new SteamManifest(fixture.Fault == "manifest-id" ? depot with { ManifestId = 0 } : depot, files));
            }
            public async Task<byte[]> GetChunkAsync(SteamDepotRequest depot, SteamChunk chunk, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref fixture.ChunkCalls);
                lock (fixture.ChunkRequests) fixture.ChunkRequests.Add(chunk.Id);
                var active = Interlocked.Increment(ref fixture.InFlight);
                int previous;
                do { previous = fixture.MaxInFlight; }
                while (active > previous && Interlocked.CompareExchange(ref fixture.MaxInFlight, active, previous) != previous);
                try { await Task.Delay(10, ct); return fixture._chunks[chunk.Id]; }
                finally { Interlocked.Decrement(ref fixture.InFlight); }
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class StreamHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(stream) });
    }
    private sealed class BlockingStream : Stream
    {
        public bool Disposed;
        private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => 1; public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await _end.Task.WaitAsync(ct); throw new ObjectDisposedException(nameof(BlockingStream)); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; _end.TrySetResult(); base.Dispose(disposing); }
    }
}
