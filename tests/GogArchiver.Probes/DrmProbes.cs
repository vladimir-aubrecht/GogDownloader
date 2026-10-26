using System.Net;
using System.Text.Json;
using GameArchiver;
using GameArchiver.Cli;
using GameArchiver.Core;

namespace GogArchiver.Probes;

public static class DrmProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Reported DRM metadata, manual corrections and filtering");
        Parsing();
        var root = Probe.ScratchDirectory();
        try { await RefreshAsync(); await CommandsAsync(root); await LocalCommandsAsync(root); await HttpAsync(); await ArchivesAsync(root); }
        finally { Directory.Delete(root, true); }
    }

    private static void Parsing()
    {
        DrmStatus Status(string text, GameOs os = GameOs.Windows) => PcGamingWikiDrmSource.Parse(text, 10,
            "https://www.pcgamingwiki.com/wiki/Example", DateTimeOffset.UtcNow)[os].Status;
        Probe.Check("explicit DRM-free Steam evidence is accepted for the matching AppID",
            Status("{{Availability/row| Steam | 10 | DRM-free }}") == DrmStatus.Free);
        Probe.Check("Steam DRM is classified as protected when explicitly reported",
            Status("{{Availability/row| Steam | 10 | Steam }}") == DrmStatus.Protected);
        Probe.Check("a GOG row cannot classify the Steam edition", Status("{{Availability/row| GOG.com | 10 | DRM-free }}") == DrmStatus.Unknown);
        Probe.Check("other AppIDs and missing DRM cells never imply DRM-free",
            Status("{{Availability/row|Steam|100|DRM-free}}") == DrmStatus.Unknown && Status("{{Availability/row|Steam|10|}}") == DrmStatus.Unknown);
        Probe.Check("notes with nested templates remain unknown instead of losing first-launch conditions",
            Status("{{Availability/row|Steam|10|DRM-free|notes={{note|Requires Steam on first launch}}}}") == DrmStatus.Unknown);
        Probe.Check("conflicting matching rows stay unknown",
            Status("{{Availability/row|Steam|10|DRM-free}}{{Availability/row|Steam|10|Steam}}") == DrmStatus.Unknown);
        Probe.Check("commented-out and nowiki examples are not evidence",
            Status("<!-- {{Availability/row|Steam|10|DRM-free}} --><nowiki>{{Availability/row|Steam|10|DRM-free}}</nowiki>") == DrmStatus.Unknown);
        Probe.Check("unscoped Windows reports do not classify native ports",
            Status("{{Availability/row|Steam|10|DRM-free}}", GameOs.Linux) == DrmStatus.Unknown);
        Probe.Check("explicit OS scope and exact Steam URLs can identify native releases",
            Status("{{Availability/row|Steam|https://store.steampowered.com/app/10/Example/|DRM-free|os=Linux}}", GameOs.Linux) == DrmStatus.Free
            && Status("{{Availability/row|Steam|10|DRM-free|os=Linux}}") == DrmStatus.Unknown);
        Probe.Check("unknown DRM tokens and malformed templates stay unknown",
            Status("{{Availability/row|Steam|10|some-new-system}}") == DrmStatus.Unknown
            && Status("{{Availability/row|Steam|10|DRM-free") == DrmStatus.Unknown);
        Probe.Check("unterminated examples and ambiguous platform qualifiers cannot assert DRM-free",
            Status("<nowiki>{{Availability/row|Steam|10|DRM-free}}") == DrmStatus.Unknown
            && Status("{{Availability/row|Steam|10|DRM-free|os=Windows|os=Linux}}") == DrmStatus.Unknown);
    }

    private static GameInfo Game(string source, string id, DrmStatus? status = null) => new(new(source, id), "Game " + id, "game_" + id)
    {
        Versions = new() { [GameOs.Windows] = new("1") { DownloadSize = 5, SizeEstimateRevision = 1 } },
        Drm = status is null ? [] : new() { [GameOs.Windows] = new() { Status = status.Value, Source = "pcgamingwiki", CheckedAt = DateTimeOffset.UtcNow } },
    };

    private static async Task RefreshAsync()
    {
        var original = new ArchiveLibrary { Games = [Game("steam", "10"), Game("steam", "20"), Game("gog", "10")],
            DrmOverrides = new() { ["steam:10"] = new() { [GameOs.Windows] = new() { Status = DrmStatus.Protected, Source = "manual", Notes = "Verified" } } } };
        var source = new Source();
        var result = await LibraryDrm.RefreshAsync(original, source);
        Probe.Check("community enrichment is keyed by provider and retains manual precedence",
            source.Calls == 2 && DrmCatalog.Get(result.Library, new("steam", "10"), GameOs.Windows).Status == DrmStatus.Protected
            && DrmCatalog.Get(result.Library, new("steam", "20"), GameOs.Windows).Status == DrmStatus.Free
            && DrmCatalog.Get(result.Library, new("gog", "10"), GameOs.Windows).Source == "gog-distribution-policy");
        Probe.Check("enrichment does not mutate the input snapshot", original.Games.All(g => g.Drm.Count == 0));
        result = await LibraryDrm.RefreshAsync(result.Library, source);
        Probe.Check("recent community metadata is cached without more requests", source.Calls == 2 && result.Cached == 2);
        source.Fail = true;
        result = await LibraryDrm.RefreshAsync(result.Library, source, force: true);
        Probe.Check("an outage makes unrefreshed reports unknown and stops further requests",
            source.Calls == 3 && result.Error is not null
            && DrmCatalog.Get(result.Library, new("steam", "20"), GameOs.Windows).Status == DrmStatus.Unknown
            && DrmCatalog.Get(result.Library, new("steam", "10"), GameOs.Windows).Status == DrmStatus.Protected);
        await using var archive = new GameArchiverClient(new Store([Game("steam", "10"), Game("steam", "20")]));
        var synced = await archive.SyncLibraryAsync(result.Library);
        Probe.Check("normal store sync preserves both community evidence and manual overrides",
            synced.Games.Single(g => g.Key == new GameKey("steam", "20")).Drm[GameOs.Windows].Notes!.Contains("failed")
            && DrmCatalog.Get(synced, new("steam", "10"), GameOs.Windows).Source == "manual");
        Probe.Check("a classification cannot leak to another branch or unavailable OS",
            DrmCatalog.Get(synced, new("steam", "10"), GameOs.Windows, "beta").Status == DrmStatus.Unknown
            && DrmCatalog.Get(synced, new("gog", "10"), GameOs.Linux).Status == DrmStatus.Unknown);
    }

    private static async Task CommandsAsync(string root)
    {
        var path = Path.Combine(root, "drm-library.json");
        var library = new ArchiveLibrary { Games = [Game("steam", "10", DrmStatus.Free), Game("steam", "20", DrmStatus.Protected), Game("steam", "30")] };
        await library.SaveAsync(path);
        var calls = new List<ArchiveOptions>();
        Task<(int Exit, string Output, string Error)> Run(params string[] args) => CaptureAsync(() => ArchiveCommandLine.RunAsync(
            [..args, "--source", "steam", "--snapshot", path], storeFactory: _ => [new Store(library.Games, calls)]));
        var result = await Run("download", "--all", "--drm", "free", "--max-size", "10MB", "--dry-run");
        Probe.Check("DRM and size filters compose offline and unknown is excluded", result.Exit == 0 && result.Output.Contains("steam:10")
            && !result.Output.Contains("steam:20") && !result.Output.Contains("steam:30") && calls.Count == 0);
        Probe.Check("DRM diagnostics distinguish unknown from protected and explain the separate refresh",
            result.Error.Contains("1 free, 1 protected, 1 unknown") && result.Error.Contains("library drm sync --source steam")
            && result.Error.Contains("regular library sync does not fetch them"));
        result = await Run("download", "--all", "--drm", "free", "--branch", "beta", "--dry-run", "--details");
        Probe.Check("an entirely unclassified selection reports unknowns and branch scope instead of implying protection",
            result.Exit == 1 && result.Error.Contains("0 free, 0 protected, 3 unknown")
            && result.Error.Contains("No DRM classification for this branch") && result.Error.Contains("--branch beta")
            && result.Error.Contains("Community reports currently cover the public branch") && calls.Count == 0);
        result = await Run("library", "list", "--drm", "unknown", "--keys");
        Probe.Check("library DRM filtering identifies unclassified products", result.Output.Trim() == "steam:30");
        result = await Run("library", "drm", "set", "steam:30", "--platform", "windows", "--drm", "free", "--note", "Tested without Steam");
        Probe.Check("manual evidence can be set using the CLI", result.Exit == 0);
        library = await ArchiveLibrary.LoadAsync(path);
        Probe.Check("manual evidence survives JSON round trips", DrmCatalog.Get(library, new("steam", "30"), GameOs.Windows).Notes == "Tested without Steam");
        result = await Run("library", "list", "--drm", "free", "--details");
        Probe.Check("detailed DRM listing exposes the evidence source and manual note", result.Output.Contains("Source: manual") && result.Output.Contains("Tested without Steam"));
        result = await Run("download", "--all", "--drm", "free", "--out", Path.Combine(root, "out"));
        Probe.Check("selected DRM evidence reaches the provider for archive metadata", result.Exit == 0 && calls.Count == 2 && calls.Any(c => c.Drm?.Source == "manual"));
        result = await Run("library", "drm", "clear", "steam:30", "--platform", "windows");
        Probe.Check("clearing an override restores the underlying unknown state", result.Exit == 0 && result.Output.Contains("unknown"));
        var before = await File.ReadAllTextAsync(path);
        result = await Run("library", "drm", "set", "steam:30", "--platform", "windows", "--drm", "free", "--note", "test", "--dry-run");
        Probe.Check("unsupported dry-run cannot accidentally mutate a manual record", result.Exit == 1 && before == await File.ReadAllTextAsync(path));
        result = await Run("library", "drm", "set", "steam:30", "--platform", "windows", "--drm", "free");
        Probe.Check("manual classification requires an evidence note", result.Exit == 1 && before == await File.ReadAllTextAsync(path));
        result = await Run("download", "steam:30", "--drm", "free");
        Probe.Check("unsupported filter contexts fail instead of silently ignoring DRM", result.Exit == 1);
    }

    private static async Task LocalCommandsAsync(string root)
    {
        var path = Path.Combine(root, "local-drm-library.json");
        var library = new ArchiveLibrary { Games = [Game("steam", "30") with { Versions = new()
            { [GameOs.Windows] = new("1"), [GameOs.Linux] = new("1") } }, Game("steam", "20"), Game("gog", "30")] };
        await library.SaveAsync(path);
        var folder = Path.Combine(root, "local-drm"); Directory.CreateDirectory(folder);
        var metadata = new ArchiveMetadata { Source = "steam", ProductId = "30", Slug = "game", Title = "Game 30", Platforms = new()
        { [GameOs.Windows] = new() { Version = new("1") { Branch = "beta" }, ArchiveName = "game.zip", ArchiveSize = 1 } } };
        await metadata.SaveAsync(folder);
        var canonical = Path.Combine(folder, ArchiveMetadata.FileName);
        var metadataBefore = await File.ReadAllTextAsync(canonical);
        Task<(int Exit, string Output, string Error)> Run(params string[] args) => CaptureAsync(() => ArchiveCommandLine.RunAsync(
            [..args, "--snapshot", path], storeFactory: _ => [new Store(library.Games), new Store(library.Games, source: "gog")]));
        var cwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(folder);
            var result = await Run("library", "drm", "set", "--drm", "free", "--note", "Verified without Steam");
            library = await ArchiveLibrary.LoadAsync(path);
            Probe.Check("local DRM corrections infer source, ID, sole platform and saved branch from metadata",
                result.Exit == 0 && result.Output.Contains("Game 30 (steam:30) Windows/beta")
                && DrmCatalog.Get(library, new("steam", "30"), GameOs.Windows, "beta").Status == DrmStatus.Free
                && metadataBefore == await File.ReadAllTextAsync(canonical));
            result = await Run("download", "--all", "--source", "steam", "--drm", "free", "--branch", "beta", "--dry-run");
            Probe.Check("a local correction reaches batch filters through the saved library", result.Exit == 0 && result.Output.Trim() == "steam:30");
            result = await Run("library", "drm", "clear");
            Probe.Check("local DRM clear uses the same metadata selection", result.Exit == 0
                && !(await ArchiveLibrary.LoadAsync(path)).DrmOverrides.ContainsKey("steam:30"));
            result = await Run("library", "drm", "set", "--branch", "public", "--drm", "free", "--note", "Verified public release");
            Probe.Check("an explicit branch overrides the archived branch", result.Exit == 0
                && (await ArchiveLibrary.LoadAsync(path)).DrmOverrides["steam:30"][GameOs.Windows].Branch == "public");
            result = await Run("library", "drm", "set", "steam:20", "--platform", "windows", "--drm", "free", "--note", "Another game");
            Probe.Check("an explicit product does not inherit the current folder's identity or branch", result.Exit == 0
                && (await ArchiveLibrary.LoadAsync(path)).DrmOverrides["steam:20"][GameOs.Windows].Branch == "public");
            metadata.Platforms[GameOs.Linux] = new() { Version = new("2"), ArchiveName = "linux.zip", ArchiveSize = 1 };
            await metadata.SaveAsync(folder);
            var before = await File.ReadAllTextAsync(path);
            result = await Run("library", "drm", "set", "--drm", "free", "--note", "Ambiguous platform");
            Probe.Check("multiple archived platforms require a choice before saving a correction", result.Exit == 1
                && result.Error.Contains("Specify --platform") && before == await File.ReadAllTextAsync(path));
            result = await Run("library", "drm", "set", "--platform", "linux", "--drm", "free", "--note", "Verified Linux release");
            Probe.Check("explicit platform selects its own archived branch", result.Exit == 0
                && (await ArchiveLibrary.LoadAsync(path)).DrmOverrides["steam:30"][GameOs.Linux].Branch == "public");
            before = await File.ReadAllTextAsync(path);
            result = await Run("library", "drm", "clear", "--platform", "linux", "--source", "gog");
            Probe.Check("a conflicting explicit source never applies a correction to another store", result.Exit == 1
                && result.Error.Contains("outside --source") && before == await File.ReadAllTextAsync(path));
            var oldSteam = Path.Combine(folder, ArchiveMetadata.LegacyUnifiedFileName);
            File.Move(canonical, oldSteam);
            result = await Run("library", "drm", "clear", "--platform", "linux");
            Probe.Check("local context also reads legacy Steam metadata without renaming it", result.Exit == 0
                && File.Exists(oldSteam) && !File.Exists(canonical));
            File.Delete(oldSteam);
            await new GogArchiver.Core.Download.BackupManifest { GameId = 30, Slug = "gog", Title = "GOG", Platforms = new()
            { [GogArchiver.Core.GamePlatform.Windows] = new() { BuildId = "1", Language = "en", ArchiveName = "gog.zip", ArchiveSize = 1, InstalledSize = 1 } } }.SaveAsync(folder);
            result = await Run("library", "drm", "set", "--drm", "protected", "--note", "Manual GOG correction");
            Probe.Check("local GOG context resolves GameId with its own provider despite an equal Steam ID", result.Exit == 0
                && (await ArchiveLibrary.LoadAsync(path)).DrmOverrides["gog:30"][GameOs.Windows].Status == DrmStatus.Protected);
            var oldGog = Path.Combine(folder, ArchiveMetadata.LegacyFileName);
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(canonical))!.AsObject();
            json.Remove("Source"); File.Delete(canonical); await File.WriteAllTextAsync(oldGog, json.ToJsonString());
            result = await Run("library", "drm", "clear");
            Probe.Check("local legacy GOG context accepts a missing Source", result.Exit == 0
                && !(await ArchiveLibrary.LoadAsync(path)).DrmOverrides.ContainsKey("gog:30"));
            await metadata.SaveAsync(folder);
            before = await File.ReadAllTextAsync(path);
            result = await Run("library", "drm", "clear", "--platform", "windows");
            Probe.Check("conflicting local metadata refuses inference without changing the snapshot", result.Exit == 1
                && result.Error.Contains("Conflicting metadata owners") && before == await File.ReadAllTextAsync(path));
            File.Delete(canonical); File.Delete(oldGog);
            result = await Run("library", "drm", "clear");
            Probe.Check("missing local metadata explains how to select a game without changing anything", result.Exit == 1
                && result.Error.Contains("No game metadata") && before == await File.ReadAllTextAsync(path));
        }
        finally { Directory.SetCurrentDirectory(cwd); }
    }

    private static async Task HttpAsync()
    {
        using var http = new HttpClient(new Handler());
        using var source = new PcGamingWikiDrmSource(http, TimeSpan.Zero);
        var result = await source.GetAsync(new("steam", "10"));
        Probe.Check("HTTP integration uses the exact AppID redirect and attributes the page",
            result[GameOs.Windows].Status == DrmStatus.Free && result[GameOs.Windows].Url == "https://www.pcgamingwiki.com/wiki/Example");
        using var deniedHttp = new HttpClient(new Handler { Denied = true });
        using var denied = new PcGamingWikiDrmSource(deniedHttp, TimeSpan.Zero);
        var refresh = await LibraryDrm.RefreshAsync(new() { Games = [Game("steam", "10", DrmStatus.Free)] }, denied, force: true);
        Probe.Check("HTTP 403 never becomes a DRM-free result", refresh.Error is not null && refresh.Library.Games[0].Drm[GameOs.Windows].Status == DrmStatus.Unknown);
    }

    private static async Task ArchivesAsync(string root)
    {
        var drm = new DrmInfo { Status = DrmStatus.Free, Source = "manual", Notes = "Offline test", CheckedAt = DateTimeOffset.UtcNow };
        var metadata = new ArchiveMetadata { Source = "steam", ProductId = "10", Slug = "game", Title = "Game", Platforms = new()
        { [GameOs.Windows] = new() { Version = new("1"), ArchiveName = "game.zip", ArchiveSize = 1, Drm = drm } } };
        var dir = Path.Combine(root, "native"); await metadata.SaveAsync(dir);
        Probe.Check("archive metadata preserves classification and evidence", (await ArchiveMetadata.LoadAsync(dir))!.Platforms[GameOs.Windows].Drm == drm);
        var legacy = new GogArchiver.Core.Download.BackupManifest { GameId = 10, Slug = "game", Title = "Game", Platforms = new()
        { [GogArchiver.Core.GamePlatform.Windows] = new() { BuildId = "1", Language = "en", ArchiveName = "game.zip", ArchiveSize = 1, InstalledSize = 1, Drm = drm } } };
        dir = Path.Combine(root, "legacy"); Directory.CreateDirectory(dir); await legacy.SaveAsync(dir);
        Probe.Check("GOG legacy metadata round trip retains manual classification", (await ArchiveMetadata.LoadAsync(dir))!.Platforms[GameOs.Windows].Drm == drm);
    }

    private sealed class Source : IDrmSource
    {
        public string Name => "pcgamingwiki";
        public int Calls;
        public bool Fail;
        public Task<IReadOnlyDictionary<GameOs, DrmInfo>> GetAsync(GameKey key, CancellationToken ct = default)
        {
            Calls++; if (Fail) throw new HttpRequestException("403 forbidden");
            return Task.FromResult<IReadOnlyDictionary<GameOs, DrmInfo>>(new Dictionary<GameOs, DrmInfo>
            { [GameOs.Windows] = new() { Status = DrmStatus.Free, Source = Name, CheckedAt = DateTimeOffset.UtcNow } });
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public bool Denied;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Denied) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (request.RequestUri!.AbsolutePath == "/api/appid.php") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = new(HttpMethod.Get, "https://www.pcgamingwiki.com/wiki/Example"), Content = new StringContent("") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { parse = new { wikitext = new Dictionary<string, string> { ["*"] = "{{Availability/row|Steam|10|DRM-free}}" } } })) });
        }
    }
    private sealed class Store(IReadOnlyList<GameInfo> games, List<ArchiveOptions>? calls = null, string source = "steam") : IGameStore
    {
        public string Source => source;
        public Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default) => Task.FromResult(new StoreLibraryResult(games));
        public Task<GameVersion> GetVersionAsync(string productId, GameSelection options, CancellationToken ct = default) => throw new Exception("No online planning for DRM filters");
        public Task<ArchiveResult> DownloadAsync(string productId, ArchiveOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
        { calls?.Add(options); return Task.FromResult(new ArchiveResult(new(Source, productId), ArchiveOutcome.Completed)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task<(int Exit, string Output, string Error)> CaptureAsync(Func<Task<int>> action)
    {
        var stdout = Console.Out; var stderr = Console.Error; using var output = new StringWriter(); using var error = new StringWriter();
        try { Console.SetOut(output); Console.SetError(error); var exit = await action(); return (exit, output.ToString(), error.ToString()); }
        finally { Console.SetOut(stdout); Console.SetError(stderr); }
    }
}
