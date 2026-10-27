using System.IO.Compression;
using GameArchiver;
using GameArchiver.Cli;
using GameArchiver.Core;

namespace GogArchiver.Probes;

public static class DrmTestProbes
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "drm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ResumeAndPrecedenceAsync(root);
            await ObservationAsync(root);
            await ExtractionAsync(root);
            await CommandsAsync(root);
            await GogAndLayoutAsync(root);
            if (OperatingSystem.IsWindows()) await NativeAsync(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<(ArchiveLibrary Library, ArchiveOptions Options, string Snapshot)> FixtureAsync(string root, string name, int count = 1)
    {
        var directory = Path.Combine(root, name);
        var options = new ArchiveOptions { OutputRoot = Path.Combine(directory, "games"), StagingRoot = Path.Combine(directory, "staging") };
        var library = new ArchiveLibrary();
        for (var i = 1; i <= count; i++)
        {
            var version = new GameVersion("build1") { Manifests = new() { ["1"] = "100" } };
            var game = new GameInfo(new("steam", i.ToString()), "Game " + i, "game_" + i)
            { Versions = new() { [GameOs.Windows] = version } };
            library.Games.Add(game);
            var gameDirectory = Path.Combine(options.OutputRoot, game.Slug); Directory.CreateDirectory(gameDirectory);
            var zip = Path.Combine(gameDirectory, "game.zip");
            await ZipAsync(zip, ("game.exe", "fixture executable"), ("data/value.txt", "fixture data"));
            await new ArchiveMetadata
            {
                Source = "steam", ProductId = i.ToString(), Slug = game.Slug, Title = game.Title,
                Platforms = new() { [GameOs.Windows] = new() { Version = version, ArchiveName = "game.zip", ArchiveSize = new FileInfo(zip).Length, LaunchExecutable = "game.exe" } },
            }.SaveAsync(gameDirectory);
        }
        var snapshot = Path.Combine(directory, "library.json"); await library.SaveAsync(snapshot);
        return (library, options, snapshot);
    }

    private static DrmTestOptions Request(string search = "") => new() { ObserveFor = TimeSpan.FromSeconds(1), Search = search };
    private static bool EmptyStaging(ArchiveOptions options) => !Directory.Exists(options.StagingRoot)
        || !Directory.EnumerateDirectories(options.StagingRoot, "*", SearchOption.AllDirectories).Any(p => Path.GetFileName(p) != "drm-tests");

    private static async Task ResumeAndPrecedenceAsync(string root)
    {
        var (library, options, snapshot) = await FixtureAsync(root, "resume", 2);
        await using var client = new GameArchiverClient(new Store(library.Games));
        var backup = Path.Combine(options.OutputRoot, "game_1");
        var zipBytes = await File.ReadAllBytesAsync(Path.Combine(backup, "game.zip"));
        var metadataBytes = await File.ReadAllBytesAsync(Path.Combine(backup, ArchiveMetadata.FileName));
        Task Save(ArchiveLibrary value, CancellationToken token) => value.SaveAsync(snapshot, token);
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime { OnStart = count => { if (count == 2) cancellation.Cancel(); } };
        var cancelled = false;
        try { await client.TestDrmAsync(library, options, Request(), Save, runtime, ct: cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        library = await ArchiveLibrary.LoadAsync(snapshot);
        Probe.Check("DRM cancellation checkpoints finished games and disposes the current process tree",
            cancelled && runtime.Starts == 2 && runtime.Disposals == 2 && library.DrmTests.Count == 1 && EmptyStaging(options));
        runtime = new();
        var resumed = await client.TestDrmAsync(library, options, Request(), Save, runtime);
        Probe.Check("DRM rerun skips completed fingerprints and resumes untested games", resumed.Tested == 1 && resumed.Skipped == 1
            && runtime.Starts == 1 && resumed.Library.DrmTests.Count == 2 && EmptyStaging(options));
        Probe.Check("automatic process survival does not assert DRM-free", resumed.Library.DrmTests.All(t => t.Outcome == DrmTestOutcome.RunningWithoutSteam
            && t.Confirmation == DrmTestConfirmation.Unknown) && DrmCatalog.Get(resumed.Library, new("steam", "1"), GameOs.Windows).Status == DrmStatus.Unknown);
        var confirmed = await client.TestDrmAsync(resumed.Library, options, Request("steam:1") with { Retest = true }, Save, new Runtime(),
            (_, _) => Task.FromResult(DrmTestConfirmation.PlayableWithoutSteam));
        Probe.Check("confirmed local gameplay takes precedence for the tested build", confirmed.Library.DrmTests.Count == 2
            && DrmCatalog.Get(confirmed.Library, new("steam", "1"), GameOs.Windows) is { Status: DrmStatus.Free, Source: "local-verification" });
        var classification = DrmCatalog.Get(confirmed.Library, new("steam", "1"), GameOs.Windows);
        Probe.Check("a live download cannot stamp an old local verdict onto a different version",
            classification.ForVersion(confirmed.Library.Games[0].Versions[GameOs.Windows]).Status == DrmStatus.Free
            && classification.ForVersion(new("new-build")).Status == DrmStatus.Unknown);
        Probe.Check("local results do not apply to another language or architecture selection",
            DrmCatalog.Get(confirmed.Library, new("steam", "1"), new GameSelection { Language = "fr" }).Status == DrmStatus.Unknown
            && DrmCatalog.Get(confirmed.Library, new("steam", "1"), new GameSelection { Architecture = "32" }).Status == DrmStatus.Unknown);
        var synced = await client.SyncLibraryAsync(confirmed.Library);
        var refreshed = (await LibraryDrm.RefreshAsync(synced, new Community())).Library;
        Probe.Check("local confirmations survive store and community syncs", DrmCatalog.Get(refreshed, new("steam", "1"), GameOs.Windows).Source == "local-verification");
        refreshed.DrmOverrides["steam:1"] = new() { [GameOs.Windows] = new() { Status = DrmStatus.Protected, Source = "manual" } };
        Probe.Check("manual overrides remain above local confirmation", DrmCatalog.Get(refreshed, new("steam", "1"), GameOs.Windows).Source == "manual");
        refreshed.DrmOverrides.Clear();
        refreshed.Games[0] = refreshed.Games[0] with { Versions = new() { [GameOs.Windows] = new("build2") } };
        Probe.Check("local confirmation cannot leak to another build, branch or platform",
            DrmCatalog.Get(refreshed, new("steam", "1"), GameOs.Windows).Source == "fixture-community"
            && DrmCatalog.Get(confirmed.Library, new("steam", "1"), GameOs.Windows, "beta").Status == DrmStatus.Unknown
            && DrmCatalog.Get(confirmed.Library, new("steam", "1"), GameOs.Linux).Status == DrmStatus.Unknown);
        Probe.Check("DRM tests never rewrite archive bytes or metadata", zipBytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(backup, "game.zip")))
            && metadataBytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(backup, ArchiveMetadata.FileName))));
        var metadata = (await ArchiveMetadata.LoadAsync(backup))!;
        metadata.Platforms[GameOs.Windows] = metadata.Platforms[GameOs.Windows] with { ModifiedAt = DateTimeOffset.UtcNow };
        await metadata.SaveAsync(backup);
        var modified = await client.TestDrmAsync(confirmed.Library, options, Request("steam:1"), Save, new Runtime(),
            (_, _) => Task.FromResult(DrmTestConfirmation.PlayableWithoutSteam));
        Probe.Check("edited archives are retested but cannot classify the provider's unmodified content",
            modified.Tested == 1 && !modified.Library.DrmTests.Last().AppliesToCatalog);
        var archived = metadata.Platforms[GameOs.Windows];
        metadata.Platforms[GameOs.Windows] = archived with { ModifiedAt = null, Version = archived.Version with { BuildId = "build2" } };
        await metadata.SaveAsync(backup);
        var newer = await client.TestDrmAsync(modified.Library, options, Request("steam:1"), Save, new Runtime());
        Probe.Check("a new archived build gets a new test without --retest", newer.Tested == 1 && newer.Library.DrmTests.Last().Version.BuildId == "build2");
    }

    private static async Task ObservationAsync(string root)
    {
        var (library, options, snapshot) = await FixtureAsync(root, "observations", 2);
        await using var client = new GameArchiverClient(new Store(library.Games));
        Task Save(ArchiveLibrary value, CancellationToken token) => value.SaveAsync(snapshot, token);
        var runtime = new Runtime { Steam = () => ["steam"] };
        var result = await client.TestDrmAsync(library, options, Request(), Save, runtime);
        Probe.Check("preexisting Steam blocks testing without recording an outcome", result.StoppedReason is not null
            && result.Library.DrmTests.Count == 0 && runtime.Starts == 0 && EmptyStaging(options));
        runtime = new Runtime(); runtime.Steam = () => runtime.Starts > 0 ? ["steam"] : [];
        result = await client.TestDrmAsync(library, options, Request(), Save, runtime,
            (_, _) => Task.FromResult(DrmTestConfirmation.PlayableWithoutSteam));
        Probe.Check("observed Steam prevents a free confirmation and stops the batch before another game", result.Tested == 1
            && result.StoppedReason is not null && runtime.Starts == 1 && runtime.Disposals == 1
            && result.Library.DrmTests.Single() is { Outcome: DrmTestOutcome.SteamObserved, Confirmation: DrmTestConfirmation.Unknown });
        runtime = new();
        result = await client.TestDrmAsync(library, options, Request("steam:1"), Save, runtime, async (_, token) =>
        {
            runtime.Steam = () => ["steamwebhelper"];
            await Task.Delay(300, token);
            return DrmTestConfirmation.PlayableWithoutSteam;
        });
        Probe.Check("monitoring continues during gameplay confirmation", result.Library.DrmTests.Single().Outcome == DrmTestOutcome.SteamObserved
            && result.Library.DrmTests.Single().Confirmation == DrmTestConfirmation.Unknown);
        runtime = new() { Running = false };
        result = await client.TestDrmAsync(library, options, Request("steam:1"), Save, runtime);
        Probe.Check("immediate process exit stays inconclusive instead of free", result.Library.DrmTests.Single().Outcome == DrmTestOutcome.Exited
            && result.Library.DrmTests.Single().Classification.Status == DrmStatus.Unknown);
        runtime = new() { StartFails = true };
        result = await client.TestDrmAsync(library, options, Request("steam:1"), Save, runtime);
        Probe.Check("launcher failure records an unavailable test and removes extraction", result.Library.DrmTests.Single().Outcome == DrmTestOutcome.Unavailable
            && result.Library.DrmTests.Single().Classification.Status == DrmStatus.Unknown && EmptyStaging(options));
        runtime = new();
        result = await client.TestDrmAsync(library, options, Request("steam:1"), Save, runtime,
            (_, _) => Task.FromResult(DrmTestConfirmation.SteamRequired));
        Probe.Check("explicit Steam-required confirmation can classify protected content", result.Library.DrmTests.Single().Classification.Status == DrmStatus.Protected);
        using var cancellation = new CancellationTokenSource(); runtime = new();
        var before = await File.ReadAllTextAsync(snapshot);
        try
        {
            await client.TestDrmAsync(library, options, Request("steam:1"), Save, runtime,
                async (_, token) => { cancellation.Cancel(); await Task.Delay(1000, token); return DrmTestConfirmation.PlayableWithoutSteam; }, ct: cancellation.Token);
        }
        catch (OperationCanceledException) { }
        Probe.Check("cancelling a confirmation records no result and cleans up", before == await File.ReadAllTextAsync(snapshot)
            && runtime.Disposals == 1 && EmptyStaging(options));
    }

    private static async Task ExtractionAsync(string root)
    {
        var (library, options, snapshot) = await FixtureAsync(root, "unsafe");
        await using var client = new GameArchiverClient(new Store(library.Games));
        var directory = Path.Combine(options.OutputRoot, "game_1");
        var zip = Path.Combine(directory, "game.zip");
        async Task<DrmTestRecord> Test(params (string Name, string Data)[] entries)
        {
            await ZipAsync(zip, entries);
            var metadata = (await ArchiveMetadata.LoadAsync(directory))!;
            metadata.Platforms[GameOs.Windows] = metadata.Platforms[GameOs.Windows] with { ArchiveSize = new FileInfo(zip).Length };
            await metadata.SaveAsync(directory);
            var runtime = new Runtime();
            var result = await client.TestDrmAsync(library, options, Request(), (value, token) => value.SaveAsync(snapshot, token), runtime);
            Probe.Check("invalid extraction never starts a game or leaves scratch files", runtime.Starts == 0 && EmptyStaging(options));
            return result.Library.DrmTests.Single();
        }
        Probe.Check("DRM extraction rejects traversal", (await Test(("../escape.exe", "x"))).Outcome == DrmTestOutcome.Unavailable);
        Probe.Check("DRM extraction rejects absolute paths", (await Test(("/escape.exe", "x"))).Outcome == DrmTestOutcome.Unavailable);
        Probe.Check("DRM extraction rejects case-insensitive path collisions", (await Test(("game.exe", "x"), ("GAME.EXE", "y"))).Outcome == DrmTestOutcome.Unavailable);
        Probe.Check("DRM extraction rejects file/directory collisions", (await Test(("data", "x"), ("data/game.exe", "y"))).Outcome == DrmTestOutcome.Unavailable);
        Probe.Check("DRM extraction rejects Windows device paths", (await Test(("NUL.txt", "x"))).Outcome == DrmTestOutcome.Unavailable);
        await ZipAsync(zip, ("game.exe", "fixture executable"));
        using (var file = new FileStream(zip, FileMode.Open, FileAccess.ReadWrite))
        { file.Position = 30 + "game.exe".Length; var value = file.ReadByte(); file.Position--; file.WriteByte((byte)(value ^ 1)); }
        var meta = (await ArchiveMetadata.LoadAsync(directory))!;
        meta.Platforms[GameOs.Windows] = meta.Platforms[GameOs.Windows] with { ArchiveSize = new FileInfo(zip).Length };
        await meta.SaveAsync(directory);
        var bad = await client.TestDrmAsync(library, options, Request(), (value, token) => value.SaveAsync(snapshot, token), new Runtime());
        Probe.Check("corrupt ZIP data fails CRC before launch", bad.Library.DrmTests.Single() is { Outcome: DrmTestOutcome.Unavailable, Detail: { } detail }
            && detail.Contains("integrity") && EmptyStaging(options));
        await ZipAsync(zip, ("game.exe", "fixture executable"));
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update)) archive.Entries[0].ExternalAttributes = unchecked((int)0xa1ff0000);
        meta.Platforms[GameOs.Windows] = meta.Platforms[GameOs.Windows] with { ArchiveSize = new FileInfo(zip).Length };
        await meta.SaveAsync(directory);
        bad = await client.TestDrmAsync(library, options, Request(), (value, token) => value.SaveAsync(snapshot, token), new Runtime());
        Probe.Check("symlink ZIP entries cannot escape the test directory", bad.Library.DrmTests.Single().Outcome == DrmTestOutcome.Unavailable && EmptyStaging(options));
        await File.WriteAllBytesAsync(zip, [1, 2, 3]);
        meta.Platforms[GameOs.Windows] = meta.Platforms[GameOs.Windows] with { ArchiveSize = 3 };
        await meta.SaveAsync(directory);
        var malformedRuntime = new Runtime();
        bad = await client.TestDrmAsync(library, options, Request(), (value, token) => value.SaveAsync(snapshot, token), malformedRuntime);
        Probe.Check("malformed ZIP headers are an unavailable test rather than an unhandled batch crash",
            bad.Unavailable == 1 && bad.Library.DrmTests.Single().Outcome == DrmTestOutcome.Unavailable
            && malformedRuntime.Starts == 0 && EmptyStaging(options));
    }

    private static async Task CommandsAsync(string root)
    {
        var (library, options, snapshot) = await FixtureAsync(root, "cli");
        var runtime = new Runtime();
        Task<(int Exit, string Output)> Run(params string[] args) => CaptureAsync(() => ArchiveCommandLine.RunAsync(
            [..args, "--snapshot", snapshot, "--source", "steam"], storeFactory: _ => [new Store(library.Games)], drmTestRuntime: runtime));
        var before = await File.ReadAllTextAsync(snapshot);
        var result = await Run("library", "drm", "test", "--out", options.OutputRoot, "--staging", options.StagingRoot!, "--dry-run");
        Probe.Check("DRM dry-run previews offline without launching, extracting or saving", result.Exit == 0 && result.Output.Contains("WouldTest")
            && runtime.Starts == 0 && EmptyStaging(options) && before == await File.ReadAllTextAsync(snapshot));
        result = await Run("library", "drm", "test", "--out", options.OutputRoot, "--staging", options.StagingRoot!, "--no-prompt", "--observe-seconds", "1");
        Probe.Check("CLI exposes automatic observations with checkpointing", result.Exit == 0 && result.Output.Contains("1 saved")
            && (await ArchiveLibrary.LoadAsync(snapshot)).DrmTests.Count == 1);
        result = await Run("library", "drm", "tests", "--details");
        Probe.Check("saved local test listing includes version and evidence", result.Exit == 0 && result.Output.Contains("RunningWithoutSteam") && result.Output.Contains("Build: build1"));
        result = await Run("library", "drm", "test", "--out", options.OutputRoot, "--arguments", "--game-option", "--dry-run");
        Probe.Check("game argument values may start with double dashes", result.Exit == 0 && result.Output.Contains("WouldTest"));
        result = await Run("library", "drm", "test", "--out", options.OutputRoot, "--platform", "linux", "--dry-run");
        Probe.Check("foreign-platform DRM tests are refused", result.Exit == 1 && result.Output.Contains("Windows archives"));
        result = await Run("download", "--all", "--no-prompt");
        Probe.Check("DRM test flags are rejected outside the test command", result.Exit == 1 && result.Output.Contains("requires library drm test"));
        var cwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Path.Combine(options.OutputRoot, "game_1"));
            result = await Run("library", "drm", "test", "--dry-run");
            Probe.Check("DRM tests infer identity from current-folder metadata", result.Exit == 0 && result.Output.Contains("Game 1 (steam:1)"));
        }
        finally { Directory.SetCurrentDirectory(cwd); }
        await using var client = new GameArchiverClient(new Store(library.Games));
        library.Games[0] = library.Games[0] with { Kind = "application" };
        var skipped = await client.TestDrmAsync(library, options, Request() with { DryRun = true }, (_, _) => throw new Exception("No writes"), runtime);
        Probe.Check("batch DRM tests default to games", skipped.Tested == 0 && skipped.Skipped == 0);
        var included = await client.TestDrmAsync(library, options, Request() with { DryRun = true, IncludeNonGames = true }, (_, _) => throw new Exception("No writes"), runtime);
        Probe.Check("DRM tests can explicitly include applications", included.Skipped == 1);
    }

    private static async Task GogAndLayoutAsync(string root)
    {
        var (library, options, snapshot) = await FixtureAsync(root, "layout");
        var providerRoot = Path.Combine(root, "layout", "providers"); Directory.CreateDirectory(providerRoot);
        Directory.Move(options.OutputRoot, Path.Combine(providerRoot, "from_steam"));
        options = options with { OutputRoot = providerRoot, Layout = ArchiveLayout.Provider,
            ProviderDirectories = new() { ["steam"] = "from_steam", ["gog"] = "from_gog" } };
        var gogDir = Path.Combine(providerRoot, "from_gog", "game_1"); Directory.CreateDirectory(gogDir);
        var zip = Path.Combine(gogDir, "game.zip"); await ZipAsync(zip, ("game.exe", "fixture"));
        await new GogArchiver.Core.Download.BackupManifest { GameId = 1, Slug = "game_1", Title = "GOG game", Platforms = new()
        {
            [GogArchiver.Core.GamePlatform.Windows] = new() { ContentRevision = 2, BuildId = "gog1", Language = "en",
                ArchiveName = "game.zip", ArchiveSize = new FileInfo(zip).Length, InstalledSize = 7, LaunchExecutable = "game.exe" },
        } }.SaveAsync(gogDir);
        File.Move(Path.Combine(gogDir, ArchiveMetadata.FileName), Path.Combine(gogDir, ArchiveMetadata.LegacyFileName));
        var metadata = (await ArchiveMetadata.LoadAsync(gogDir))!;
        library.Games.Add(new(new("gog", "1"), "GOG game", "game_1") { Versions = new() { [GameOs.Windows] = metadata.Platforms[GameOs.Windows].Version } });
        await using var client = new GameArchiverClient(new Store(library.Games), new Store(library.Games, "gog"));
        var result = await client.TestDrmAsync(library, options, Request("gog:1"), (value, token) => value.SaveAsync(snapshot, token), new Runtime(),
            (_, _) => Task.FromResult(DrmTestConfirmation.PlayableWithoutSteam));
        Probe.Check("local tests read legacy GOG metadata in custom provider folders without confusing equal Steam IDs",
            result.Tested == 1 && result.Library.DrmTests.Single().Key == new GameKey("gog", "1")
            && DrmCatalog.Get(result.Library, new("gog", "1"), GameOs.Windows).Source == "local-verification"
            && DrmCatalog.Get(result.Library, new("steam", "1"), GameOs.Windows).Status == DrmStatus.Unknown
            && File.Exists(Path.Combine(gogDir, ArchiveMetadata.LegacyFileName)) && !File.Exists(Path.Combine(gogDir, ArchiveMetadata.FileName)));
        var invalid = result.Library with { DrmTests = [result.Library.DrmTests.Single() with { Outcome = DrmTestOutcome.SteamObserved }] };
        await invalid.SaveAsync(snapshot);
        var rejected = false;
        try { await ArchiveLibrary.LoadAsync(snapshot); } catch (InvalidDataException) { rejected = true; }
        Probe.Check("snapshot validation rejects a free confirmation contradicting a Steam observation", rejected);
        await using var steam = new GameArchiverClient(new Store(library.Games));
        rejected = false;
        try { await steam.TestDrmAsync(library, options, Request() with { GameDirectory = gogDir, DryRun = true }, (_, _) => throw new Exception("No writes")); }
        catch (ArgumentException) { rejected = true; }
        Probe.Check("current-folder inference rejects an incompatible provider selection", rejected);
        var other = await FixtureAsync(root, "ambiguous", 2);
        rejected = false;
        try { await steam.TestDrmAsync(other.Library, other.Options, Request() with { Executable = "game.exe", DryRun = true }, (_, _) => throw new Exception("No writes")); }
        catch (ArgumentException) { rejected = true; }
        Probe.Check("launch overrides cannot silently affect a whole batch", rejected);
    }

    private static async Task NativeAsync(string root)
    {
        var directory = Path.Combine(root, "native"); Directory.CreateDirectory(directory);
        var launcher = Path.Combine(directory, "launcher.cmd");
        await File.WriteAllTextAsync(launcher, "@echo off\r\nstart \"\" /b cmd.exe /d /c \"echo started>child.txt & ping -n 60 127.0.0.1 >nul\"\r\nexit /b 0\r\n");
        var runtime = new WindowsDrmTestRuntime();
        var process = runtime.Start(new(directory, launcher, "", directory));
        try
        {
            for (var i = 0; i < 50 && !File.Exists(Path.Combine(directory, "child.txt")); i++) await Task.Delay(100);
            await Task.Delay(200);
            Probe.Check("Windows job retains a child after its launcher exits", File.Exists(Path.Combine(directory, "child.txt")) && process.IsRunning);
        }
        finally { process.Dispose(); }
        Probe.Check("Windows job disposal stops the contained process tree", !process.IsRunning);
    }

    private static async Task ZipAsync(string path, params (string Name, string Data)[] entries)
    {
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            await using var writer = new StreamWriter(entry.Open()); await writer.WriteAsync(data);
        }
    }

    private sealed class Runtime : IDrmTestRuntime
    {
        public bool IsSupported => true;
        public bool Running = true, StartFails;
        public int Starts, Disposals;
        public Func<IReadOnlyList<string>> Steam = () => [];
        public Action<int>? OnStart;
        public IReadOnlyList<string> GetSteamProcesses() => Steam();
        public IDrmTestProcess Start(DrmTestLaunch launch)
        {
            Starts++; OnStart?.Invoke(Starts);
            if (StartFails) throw new System.ComponentModel.Win32Exception("Cannot start fixture");
            if (!File.Exists(launch.Executable)) throw new Exception("Not extracted before starting");
            return new Process(this);
        }
        private sealed class Process(Runtime runtime) : IDrmTestProcess
        { public bool IsRunning => runtime.Running; public void Dispose() => runtime.Disposals++; }
    }
    private sealed class Store(List<GameInfo> games, string source = "steam") : IGameStore
    {
        public string Source => source;
        public Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default) => Task.FromResult(new StoreLibraryResult(games.Where(g => g.Key.Source == source).ToList()));
        public Task<GameVersion> GetVersionAsync(string productId, GameSelection options, CancellationToken ct = default) => throw new Exception("No online selection in local tests");
        public Task<ArchiveResult> DownloadAsync(string productId, ArchiveOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) => throw new Exception("No download in local tests");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Community : IDrmSource
    {
        public string Name => "fixture-community";
        public Task<IReadOnlyDictionary<GameOs, DrmInfo>> GetAsync(GameKey key, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<GameOs, DrmInfo>>(new Dictionary<GameOs, DrmInfo>
            { [GameOs.Windows] = new() { Status = DrmStatus.Protected, Source = Name, CheckedAt = DateTimeOffset.UtcNow } });
    }
    private static async Task<(int Exit, string Output)> CaptureAsync(Func<Task<int>> action)
    {
        var stdout = Console.Out; var stderr = Console.Error; using var output = new StringWriter();
        try { Console.SetOut(output); Console.SetError(output); return (await action(), output.ToString()); }
        finally { Console.SetOut(stdout); Console.SetError(stderr); }
    }
}
