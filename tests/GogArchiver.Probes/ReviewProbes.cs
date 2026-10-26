using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using GameArchiver;
using GameArchiver.Cli;
using GameArchiver.Core;
using GogArchiver.Core;
using GogArchiver.Core.Download;
using GogArchiver.Core.Library;
using SteamArchiver.Core;

namespace GogArchiver.Probes;

public static class ReviewProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Multi-store review regressions");
        var root = Probe.ScratchDirectory();
        try
        {
            await BodyTimeoutAsync();
            await SteamPacingAsync();
            await GogIdentityAsync(root);
            await GogRefreshAsync(root);
            await SteamSelectionAsync();
            await MetadataLeaseAsync(root);
            await NewDirectoryCollisionsAsync(root);
            await AggregateAsync(root);
            await PublicationAsync(root);
            await ResumeAccountingAsync(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static ArchiveOptions Options(string root, string name) => new()
    { OutputRoot = Path.Combine(root, name), StagingRoot = Path.Combine(root, name + "-staging") };

    private static LibraryEntry GogGame(long id = 1) => new()
    {
        Id = id, Slug = "game", Title = "Game", Platforms = new()
        { [GamePlatform.Windows] = new() { Depot = new("old", 2, "old", "https://fixture.invalid/repository") } },
    };

    private static async Task WriteGogAsync(string path, LibraryEntry game)
    {
        Directory.CreateDirectory(path);
        var zip = Path.Combine(path, "game.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("game.txt").Open())) writer.Write("game");
        await new BackupManifest { GameId = game.Id, Slug = game.Slug, Title = game.Title, Platforms = new()
        { [GamePlatform.Windows] = new() { BuildId = "old", ContentRevision = 1, Language = "en", ArchiveName = "game.zip",
            ArchiveSize = new FileInfo(zip).Length, InstalledSize = 4 } } }.SaveAsync(path);
    }

    private static async Task GogIdentityAsync(string root)
    {
        var options = Options(root, "gog-alias");
        var path = Path.Combine(options.OutputRoot, "custom");
        var game = GogGame();
        await WriteGogAsync(path, game);
        using var http = new HttpClient(new Handler(_ => throw new IOException("Unexpected network request.")));
        await using var gog = new GogClient(new(http), new(http, http, new(0)), http,
            new() { Games = [game, GogGame(2)] });
        var result = await gog.DownloadAsync("1", options with { DirectoryName = "custom" });
        Probe.Check("an explicit GOG alias resolves a library slug collision without rebuilding",
            result.Outcome == ArchiveOutcome.AlreadyDone && Path.GetDirectoryName(result.ArchivePath) == path);
        var renamed = Path.Combine(options.OutputRoot, "My Renamed Game");
        Directory.Move(path, renamed);
        result = await gog.DownloadAsync("1", options);
        Probe.Check("GOG discovers a manual folder rename by identity and preserves its exact name",
            result.Outcome == ArchiveOutcome.AlreadyDone && Path.GetDirectoryName(result.ArchivePath) == renamed);
        Probe.Check("the original GOG directory resolver also discovers aggregate aliases",
            (await BackupDirectory.FindAsync(options.OutputRoot, game)).Directory == renamed);
        var fresh = await BackupDirectory.PrepareAsync(Path.Combine(root, "fresh-alias"), game, "other", default);
        Probe.Check("a GOG directory alias does not change the product slug in metadata", fresh.Manifest?.Slug == game.Slug);
        await new ArchiveMetadata { Source = "steam", ProductId = "1", Slug = "game", Title = "Other" }.SaveAsync(renamed);
        Probe.Check("the original GOG reader refuses conflicting native and legacy owners",
            await RejectsAsync<ArchiveConflictException>(() => BackupManifest.LoadAsync(renamed)));
    }

    private static async Task GogRefreshAsync(string root)
    {
        var failProduct = false;
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            return path switch
            {
                "/user/data/games" => Json("""{"owned":[1,2]}"""),
                "/products/1" when failProduct => new(HttpStatusCode.ServiceUnavailable),
                "/products/1" => Json("""{"id":1,"title":"Game","slug":"game","downloads":{"installers":[]}}"""),
                "/products/2" => Json("""{"id":2,"title":"DLC","slug":"dlc","downloads":{"installers":[]}}"""),
                "/products/1/os/windows/builds" => Json("""{"items":[{"build_id":"live","generation":2,"repository_manifest":"https://fixture.invalid/repository"}]}"""),
                "/repository" => Zlib("""{"baseProductId":"1","products":[{"productId":"1","name":"Game"},{"productId":"2","name":"DLC"}],"depots":[{"productId":"1","languages":["en-US"],"size":10,"compressedSize":5},{"productId":"1","languages":["de-DE"],"size":30,"compressedSize":20}]}"""),
                _ => Json("""{"items":[]}"""),
            };
        }));
        var snapshotPath = Path.Combine(root, "gog-snapshot.json");
        await using var gog = new GogClient(new(http), new(http, http, new(0)), http,
            new() { SchemaVersion = LibrarySnapshot.CurrentSchema, Games = [GogGame()] }, new() { SnapshotPath = snapshotPath });
        var live = await gog.GetVersionAsync("1", new() { Language = "de" });
        Probe.Check("GOG version refresh reads live builds, ownership and selected-language sizes",
            live.BuildId == "live" && live.Dlcs.SequenceEqual(["2"]) && live.DownloadSize == 20 && live.InstalledSize == 30
            && !calls.Any(p => p.Contains("/os/osx/")));
        var options = Options(root, "refreshed-gog");
        var directory = Path.Combine(options.OutputRoot, "game");
        await WriteGogAsync(directory, GogGame());
        var completed = (await BackupManifest.LoadAsync(directory))!;
        completed.Platforms[GamePlatform.Windows] = completed.Platforms[GamePlatform.Windows] with
        { BuildId = "live", Language = "de", Dlcs = ["2"] };
        await completed.SaveAsync(directory);
        var before = calls.Count;
        Probe.Check("a GOG client uses its refreshed version for subsequent downloads",
            (await gog.DownloadAsync("1", options with { Language = "de" })).Outcome == ArchiveOutcome.AlreadyDone && calls.Count == before);
        failProduct = true;
        var partial = await gog.SyncGogLibraryAsync();
        Probe.Check("partial GOG sync retains failed products in the provider's own snapshot",
            partial.Games.Any(g => g.Id == 1) && partial.Failures.ContainsKey(1)
            && (await LibrarySnapshot.LoadAsync(snapshotPath))!.Games.Any(g => g.Id == 1));
    }

    private static SteamDepot Depot(uint id, string arch = "", string language = "") => new(id)
    { Architecture = arch, Language = language, OsList = "windows", Manifests = new() { ["public"] = id * 100UL } };
    private static async Task SteamSelectionAsync()
    {
        var fixture = new SelectionSession();
        var game = new SteamApp(1, "Game") { Builds = new() { ["public"] = "1" }, Depots = [Depot(10)] };
        fixture.Apps[1] = game with { Depots = [Depot(10), new(20) { SharedFromApp = 999, DlcAppId = 2 },
            new(30) { SharedFromApp = 998, OsList = "linux" }] };
        await using var client = new SteamArchiver.Core.SteamClient(fixture);
        var version = await client.GetVersionAsync("1", new());
        Probe.Check("unowned and other-OS shared depots are filtered before protected appinfo lookup",
            version.Manifests.Count == 1 && fixture.Requests.SequenceEqual([1u]));
        fixture.Apps[1] = game with { Depots = [Depot(10), Depot(11, arch: "32")] };
        Probe.Check("an unsupported architecture cannot silently archive only neutral Steam content",
            await RejectsAsync<NotSupportedException>(() => client.GetVersionAsync("1", new() { Architecture = "64" })));
        Probe.Check("selecting the available Steam architecture includes its executable depot",
            (await client.GetVersionAsync("1", new() { Architecture = "32" })).Manifests.ContainsKey("11"));
        fixture.Owned.Add(2);
        fixture.Apps[1] = game with { Dlcs = [2], Depots = [Depot(10, language: "english")] };
        fixture.Apps[2] = new(2, "DLC") { Type = "dlc", ParentAppId = 1, Depots = [Depot(20, language: "german")] };
        Probe.Check("a missing DLC language cannot be recorded as a completed owned expansion",
            await RejectsAsync<NotSupportedException>(() => client.GetVersionAsync("1", new())));
        fixture.Apps[1] = game with { Depots = [Depot(10, language: "english"), Depot(20, language: "german") with { DlcAppId = 2 }] };
        fixture.Apps[2] = fixture.Apps[2] with { Depots = [] };
        Probe.Check("language availability is checked separately for DLC depots in the base appinfo",
            await RejectsAsync<NotSupportedException>(() => client.GetVersionAsync("1", new())));
        fixture.Owned = [1];
        fixture.Apps[1] = game with { Depots = [Depot(10, arch: "32")] };
        var snapshot = await client.SyncLibraryAsync();
        Probe.Check("sync preserves a 32-bit-only title without inventing a complete 64-bit selection",
            snapshot.Failures.Count == 0 && snapshot.Games.Single().CanBackUp(GameOs.Windows)
            && snapshot.Games.Single().Versions[GameOs.Windows].Manifests.Count == 0
            && (await client.GetVersionAsync("1", new() { Architecture = "32" })).Manifests.ContainsKey("10"));
    }

    private static async Task MetadataLeaseAsync(string root)
    {
        var options = Options(root, "leases");
        var game = new GameInfo(new("steam", "1"), "Game", "game");
        var reserved = await ArchiveCatalog.ReserveAsync(game, options, default);
        var saved = reserved.Metadata with { Platforms = new() { [GameOs.Windows] = new()
        { Version = new("1"), ArchiveName = "windows.zip", ArchiveSize = 10 } } };
        await saved.SaveAsync(reserved.Directory); // Another writer finishes between reservation and acquisition.
        using (var lease = await ArchiveCatalog.AcquireOwnedAsync(reserved, game.Key, default))
            Probe.Check("acquiring a Steam output lease reloads another writer's platform completions",
                lease.Location.Metadata.Platforms.ContainsKey(GameOs.Windows));
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            var duplicate = Path.Combine(options.OutputRoot, "GAME");
            Directory.CreateDirectory(duplicate);
            Probe.Check("rediscovering an owned directory still rejects a case-insensitive name collision",
                await RejectsAsync<ArchiveConflictException>(() => ArchiveCatalog.ReserveAsync(game, options, default)));
            Directory.Delete(duplicate);
        }
        await (saved with { ProductId = "2" }).SaveAsync(reserved.Directory);
        Probe.Check("an output lease rejects ownership changes after reservation",
            await RejectsAsync<ArchiveConflictException>(async () =>
            { using var lease = await ArchiveCatalog.AcquireOwnedAsync(reserved, game.Key, default); }));
    }

    private static async Task NewDirectoryCollisionsAsync(string root)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return;
        foreach (var scenario in new[] { "empty-first", "owned-first", "both-empty", "file", "explicit", "source-alias" })
        {
            var options = Options(root, "new-collision-" + scenario);
            var name = scenario == "explicit" ? "chosen" : scenario == "source-alias" ? "game_steam" : "game";
            if (scenario == "explicit") options = options with { DirectoryName = name };
            Directory.CreateDirectory(Path.Combine(options.OutputRoot, name));
            Directory.CreateDirectory(Path.Combine(options.OutputRoot, name.ToUpperInvariant()));
            var variants = Directory.EnumerateDirectories(options.OutputRoot).ToArray();
            var occupied = variants[scenario == "owned-first" ? 0 : 1];
            string? original = null;
            if (scenario == "file")
            {
                Directory.Delete(occupied);
                await File.WriteAllTextAsync(occupied, "other content");
            }
            else if (scenario != "both-empty")
            {
                await new ArchiveMetadata { Source = "steam", ProductId = "99", Slug = name, Title = "Existing game" }.SaveAsync(occupied);
                original = await File.ReadAllTextAsync(Path.Combine(occupied, ArchiveMetadata.FileName));
            }
            if (scenario == "source-alias")
                await new BackupManifest { GameId = 1, Slug = "game", Title = "GOG game" }.SaveAsync(Path.Combine(options.OutputRoot, "game"));
            var game = new GameInfo(new("steam", "100"), "New game", "game");
            var rejected = await RejectsAsync<ArchiveConflictException>(() => ArchiveCatalog.ReserveAsync(game, options, default));
            var unchanged = original is null || original == await File.ReadAllTextAsync(Path.Combine(occupied, ArchiveMetadata.FileName));
            var empty = variants.Where(v => v != occupied || scenario == "both-empty");
            Probe.Check($"new directory allocation rejects all case variants without claiming or changing them ({scenario})",
                rejected && unchanged && empty.All(v => Directory.Exists(v) && !Directory.EnumerateFileSystemEntries(v).Any())
                && (scenario != "file" || await File.ReadAllTextAsync(occupied) == "other content"));
        }
    }

    private static async Task AggregateAsync(string root)
    {
        var options = Options(root, "aggregate-review");
        var parent = new GameInfo(new("gog", "1"), "Base", "base")
        { Versions = new() { [GameOs.Windows] = new("old") { Dlcs = ["2"], Architecture = "" } } };
        var dlc = new GameInfo(new("gog", "2"), "Expansion", "expansion") { Kind = "dlc",
            Versions = new() { [GameOs.Windows] = new("dlc"), [GameOs.Linux] = new("installer") }, BackupPlatforms = [GameOs.Linux] };
        var library = new ArchiveLibrary { Games = [parent, dlc] };
        var snapshot = Path.Combine(root, "aggregate-review.json");
        await library.SaveAsync(snapshot);
        var result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "expansion", "--dry-run",
            "--source", "gog", "--snapshot", snapshot], storeFactory: _ => [new EmptyStore("gog")]));
        Probe.Check("filtered aggregate batches resolve matching DLC to the base game", result.Exit == 0 && result.Text.Trim() == "gog:1");
        Probe.Check("DLC mapping preserves standalone installers on other operating systems",
            library.SelectDownloads([dlc], GameOs.Linux).Single().Key == dlc.Key);
        var emptyGog = Path.Combine(root, "empty-gog.json");
        await new LibrarySnapshot { SchemaVersion = LibrarySnapshot.CurrentSchema }.SaveAsync(emptyGog);
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["library", "stats", "--snapshot", emptyGog],
            storeFactory: _ => [new EmptyStore("gog")]));
        Probe.Check("the aggregate CLI recognizes an empty original GOG snapshot", result.Exit == 0);

        var path = Path.Combine(options.OutputRoot, "game");
        await WriteGogAsync(path, GogGame());
        var backup = (await BackupManifest.LoadAsync(path))!;
        backup.Platforms[GamePlatform.Linux] = backup.Platforms[GamePlatform.Windows] with { ArchiveName = "missing.zip" };
        await backup.SaveAsync(path);
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["verify", "--platform", "windows", "--out", options.OutputRoot],
            storeFactory: _ => [new EmptyStore("gog")]));
        Probe.Check("aggregate verify respects an explicit operating-system filter", result.Exit == 0 && result.Text.Contains("1 sound") && !result.Text.Contains("Linux"));
        backup.Platforms.Remove(GamePlatform.Linux);
        backup.Platforms[GamePlatform.Windows] = backup.Platforms[GamePlatform.Windows] with { ArchiveSize = 1 };
        await backup.SaveAsync(path);
        await using var aggregate = new GameArchiverClient(new EmptyStore("gog"));
        Probe.Check("a valid replacement ZIP with a different recorded size is not reported sound",
            (await aggregate.VerifyAsync(options.OutputRoot)).Single().Problems.Any(p => p.Problem.Contains("size")));

        backup.Platforms[GamePlatform.Windows] = backup.Platforms[GamePlatform.Windows] with
        { BuildId = "installer-de-1-20", Language = "de", ArchiveSize = new FileInfo(Path.Combine(path, "game.zip")).Length };
        await backup.SaveAsync(path);
        var installerLibrary = new ArchiveLibrary { Games = [new(new("gog", "1"), "Game", "game")
        { Versions = new() { [GameOs.Windows] = new("installer-en-1-10") { BuildVariesByLanguage = true, Architecture = "" } } }] };
        Probe.Check("offline diff does not invent an outdated installer from a different language",
            (await aggregate.DiffAsync(installerLibrary, options with { Language = "de" })).Single().Status == ArchiveStatus.Unknown);
        var current = new EmptyStore("gog") { Version = new("installer-de-1-20") { Language = "de", Architecture = "" } };
        await using var refreshed = new GameArchiverClient(current);
        installerLibrary.Failures["gog:1"] = "previous sync failed";
        Probe.Check("explicit version refresh can recover a product retained after a failed sync",
            (await refreshed.DiffAsync(installerLibrary, options with { Language = "de" }, refreshVersions: true)).Single().Status == ArchiveStatus.Current);
        var invalid = Path.Combine(root, "invalid-native");
        Directory.CreateDirectory(invalid);
        await new ArchiveMetadata { Source = "steam", ProductId = "1", Slug = "game", Title = "Game", Platforms = new()
        { [GameOs.Windows] = new() { Version = null!, ArchiveName = "game.zip", ArchiveSize = 1 } } }.SaveAsync(invalid);
        Probe.Check("malformed native completion records fail as data errors rather than null dereferences",
            await RejectsAsync<InvalidDataException>(() => ArchiveMetadata.LoadAsync(invalid)));
    }

    private static async Task PublicationAsync(string root)
    {
        var staging = Path.Combine(root, "publish-stage"); Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "game.txt"), "game");
        var target = Path.Combine(root, "published.zip");
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = await RejectsAsync<OperationCanceledException>(() => ArchivePublisher.PublishAsync(staging, target, 1,
                progress: new InlineProgress<ArchiveProgress>(p => { if (p.Phase == "check") cancellation.Cancel(); }), ct: cancellation.Token));
            Probe.Check("cancelling verification removes the staging ZIP and preserves resumable game files",
                cancelled && !File.Exists(staging + ".zip") && !File.Exists(target) && File.Exists(Path.Combine(staging, "game.txt")));
        }
        var source = Path.Combine(root, "copy-source.zip"); await File.WriteAllTextAsync(source, "new archive");
        await File.WriteAllTextAsync(target, "previous archive");
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = false;
            try { ArchiveStorage.PublishArchive(source, target, false,
                new InlineProgress<ArchiveProgress>(_ => cancellation.Cancel()), cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Probe.Check("cancellation after the final copied block cannot publish a replacement",
                cancelled && File.Exists(source) && await File.ReadAllTextAsync(target) == "previous archive"
                && !Directory.EnumerateFiles(root, "*.tmp").Any());
        }
        if (!OperatingSystem.IsWindows())
        {
            var unrelated = Path.Combine(root, "unrelated.txt"); await File.WriteAllTextAsync(unrelated, "keep");
            File.CreateSymbolicLink(target + ".tmp", unrelated);
            ArchiveStorage.PublishArchive(source, target, false, null);
            Probe.Check("publication cannot follow an existing temporary-file symlink",
                await File.ReadAllTextAsync(unrelated) == "keep" && await File.ReadAllTextAsync(target) == "new archive");
        }
    }

    private static async Task ResumeAccountingAsync(string root)
    {
        static string Hash(string value) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(value)));
        var path = Path.Combine(root, "partial.bin");
        await using (var output = File.Create(path)) { output.SetLength(4); await output.WriteAsync("AB"u8.ToArray()); }
        var file = new SteamFile("partial.bin", 4, Hash("ABCD"), [new(Hash("AB"), 0, 0, 2, 2), new(Hash("CD"), 0, 2, 2, 2)]);
        var resume = await SteamArchiver.Core.SteamClient.InspectResumeAsync(path, file, default);
        Probe.Check("Steam free-space accounting credits verified chunks instead of a sparse file's logical length",
            !resume.Complete && resume.Bytes == 2 && resume.Offsets.SetEquals([0L]));
        await File.WriteAllTextAsync(path, "ABCD");
        resume = await SteamArchiver.Core.SteamClient.InspectResumeAsync(path, file, default);
        Probe.Check("complete verified Steam files credit their full size without being downloaded again", resume.Complete && resume.Bytes == 4);
        Probe.Check("invalid resume versions are never accepted as current",
            !(new GameVersion("1") { Dlcs = null! }).Matches(new("1")));
    }

    private static async Task BodyTimeoutAsync()
    {
        using var bodyTimeout = new CancellationTokenSource();
        var body = new StalledBody();
        using var handler = new SteamContentHttpHandler(new(default, new(0)))
        { InnerHandler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(body) }) };
        using var http = new HttpClient(handler);
        using var response = await http.GetAsync("https://fixture.invalid/chunk", HttpCompletionOption.ResponseHeadersRead);
        var copy = response.Content.CopyToAsync(Stream.Null, bodyTimeout.Token);
        bodyTimeout.Cancel();
        var stopped = await Task.WhenAny(copy, Task.Delay(1000)) == copy;
        response.Dispose(); // Also releases a stalled baseline implementation before reporting failure.
        try { await copy; } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or HttpRequestException) { }
        Probe.Check("Steam CDN body timeout cancels and disposes a read after headers have arrived", stopped && body.Disposed);
    }

    private static async Task SteamPacingAsync()
    {
        foreach (var manifest in new[] { false, true })
        {
            var context = new ContentHttpContext(default, new(0));
            using var handler = new SteamContentHttpHandler(context)
            { InnerHandler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[15]) }) };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
            var transfer = new SteamContentTransfer(10, context);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Task<byte[]> Fetch() => http.GetByteArrayAsync("https://fixture.invalid/content");
            var bytes = manifest ? await transfer.ManifestAsync(Fetch, default) : await transfer.ChunkAsync(15, Fetch, default);
            Probe.Check($"Steam {(manifest ? "manifest" : "chunk")} bandwidth pacing does not consume the HTTP deadline",
                bytes.Length == 15 && clock.Elapsed >= TimeSpan.FromMilliseconds(350));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            Probe.Check("Steam bandwidth waiting remains cancellable before a request starts",
                await RejectsAsync<OperationCanceledException>(() => transfer.ChunkAsync<byte[]>(1000,
                    () => throw new InvalidOperationException("No request should start."), cancellation.Token)));
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Zlib(string json)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZLibStream(memory, CompressionMode.Compress, true)) zip.Write(Encoding.UTF8.GetBytes(json));
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(memory.ToArray()) };
    }
    private static async Task<bool> RejectsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); return false; } catch (T) { return true; } }
    private static async Task<(int Exit, string Text)> CaptureAsync(Func<Task<int>> action)
    {
        var output = Console.Out; var error = Console.Error; using var writer = new StringWriter();
        try { Console.SetOut(writer); Console.SetError(writer); return (await action(), writer.ToString()); }
        finally { Console.SetOut(output); Console.SetError(error); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
    private sealed class SelectionSession : ISteamSession
    {
        public Task<IReadOnlyDictionary<uint, string>> GetOwnedGamesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<uint, string>>(Owned.ToDictionary(id => id, id => Apps[id].Title));
        public Dictionary<uint, SteamApp> Apps = [];
        public HashSet<uint> Owned = [1];
        public List<uint> Requests = [];
        public Task<IReadOnlySet<uint>> GetOwnedAppIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlySet<uint>>(Owned);
        public Task<SteamApp> GetAppAsync(uint appId, CancellationToken ct)
        { Requests.Add(appId); return Task.FromResult(Apps[appId]); }
        public Task<ISteamContentSession> OpenContentAsync(long rate, CancellationToken ct) => throw new InvalidOperationException("Selection should not download.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class EmptyStore(string source) : IGameStore
    {
        public string Source => source;
        public GameVersion? Version;
        public Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<GameVersion> GetVersionAsync(string id, GameSelection options, CancellationToken ct = default) =>
            Task.FromResult(Version ?? throw new InvalidOperationException());
        public Task<ArchiveResult> DownloadAsync(string id, ArchiveOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class StalledBody : Stream
    {
        private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => 1; public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await _end.Task; throw new ObjectDisposedException(nameof(StalledBody)); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; _end.TrySetResult(); base.Dispose(disposing); }
    }
}
