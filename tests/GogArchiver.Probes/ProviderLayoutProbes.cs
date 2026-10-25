using System.IO.Compression;
using GameArchiver;
using GameArchiver.Cli;
using GameArchiver.Core;

namespace GogArchiver.Probes;

public static class ProviderLayoutProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Provider selection and output folders");
        var root = Probe.ScratchDirectory();
        try
        {
            await ApiAsync(root);
            await CliAsync(root);
            await ConflictsAsync(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static GameInfo Game(string source, string id = "1") => new(new(source, id), "Game", "game")
    { Versions = new() { [GameOs.Windows] = new("1") { Manifests = new() { ["10"] = "100" } } } };

    private static async Task ApiAsync(string root)
    {
        var gog = new Store("gog"); var steam = new Store("steam"); var extra = new Store("itch");
        await using var client = new GameArchiverClient(gog, steam, extra);
        var options = new ArchiveOptions { OutputRoot = Path.Combine(root, "api"), Layout = ArchiveLayout.Provider,
            StagingRoot = Path.Combine(root, "staging") };
        var library = await client.SyncLibraryAsync();
        var missing = await client.DiffAsync(library, options);
        Probe.Check("provider diff finds missing archives without creating directories",
            missing.Count == 3 && missing.All(r => r.Status == ArchiveStatus.Missing) && !Directory.Exists(options.OutputRoot));
        var results = new List<ArchiveResult>();
        foreach (var game in library.Games) results.Add(await client.DownloadAsync(game.Key, options));
        Probe.Check("same IDs and slugs are archived separately for every registered provider",
            results.All(r => r.ArchivePath == Path.Combine(options.OutputRoot, r.Key.Source, "game", "Game.zip")));
        Probe.Check("provider routing flattens options once and preserves local staging",
            new[] { gog, steam, extra }.All(s => s.LastOptions?.Layout == ArchiveLayout.Flat
                && s.LastOptions.StagingRoot == options.StagingRoot));
        Probe.Check("diff and deep verification discover all provider subfolders",
            (await client.DiffAsync(library, options)).All(r => r.Status == ArchiveStatus.Current)
            && (await client.VerifyAsync(options, deep: true)) is { Count: 3 } checks && checks.All(r => r.Problems.Count == 0));
        Probe.Check("provider downloads reuse existing archives on a second run",
            (await client.DownloadAsync(new("steam", "1"), options)).Outcome == ArchiveOutcome.AlreadyDone);

        var custom = options with { ProviderDirectories = new() { ["GOG"] = "drm_free", ["steam"] = "steam_library" } };
        Directory.Move(Path.Combine(options.OutputRoot, "gog"), Path.Combine(options.OutputRoot, "drm_free"));
        Directory.Move(Path.Combine(options.OutputRoot, "steam"), Path.Combine(options.OutputRoot, "steam_library"));
        Probe.Check("custom provider names rediscover manually moved archives by metadata",
            (await client.DiffAsync(library, custom)).All(r => r.Status == ArchiveStatus.Current)
            && (await client.DownloadAsync(new("gog", "1"), custom)).Outcome == ArchiveOutcome.AlreadyDone);
        var orphan = Path.Combine(options.OutputRoot, "steam_library", "orphan");
        await new ArchiveMetadata { Source = "steam", ProductId = "999", Slug = "orphan", Title = "Orphan" }.SaveAsync(orphan);
        Probe.Check("orphan discovery uses configured provider folders",
            (await client.FindOrphansAsync(library, custom)).Single().Metadata.Key == new GameKey("steam", "999"));
        await using var onlyGog = new GameArchiverClient(new Store("gog"));
        Probe.Check("verification of one provider excludes other provider archives",
            (await onlyGog.VerifyAsync(custom)).Single().Key.Source == "gog");
        Probe.Check("legacy flat verification still uses only the explicitly supplied root",
            (await client.VerifyAsync(Path.Combine(options.OutputRoot, "drm_free"))).Single().Key.Source == "gog");
    }

    private static async Task CliAsync(string root)
    {
        var gog = new Store("gog"); var steam = new Store("steam"); var extra = new Store("itch");
        var stores = new[] { gog, steam, extra };
        IEnumerable<IGameStore> Factory(string provider) => stores.Where(s => provider == "all" || s.Source == provider);
        var snapshot = Path.Combine(root, "cli-library.json");
        await new ArchiveLibrary { Games = stores.Select(s => Game(s.Source)).ToList() }.SaveAsync(snapshot);
        var output = Path.Combine(root, "cli");
        var common = new[] { "--snapshot", snapshot, "--out", output, "--layout", "provider" };
        var result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "--source", "steam", ..common], storeFactory: Factory));
        Probe.Check("--source limits batch downloads to the selected store",
            result.Exit == 0 && steam.Downloads == 1 && gog.Downloads == 0 && extra.Downloads == 0
            && File.Exists(Path.Combine(output, "steam", "game", "Game.zip")) && !Directory.Exists(Path.Combine(output, "gog")));
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["diff", "--source", "steam", ..common], storeFactory: Factory));
        Probe.Check("CLI diff uses the provider layout", result.Exit == 0 && result.Text.Contains("Current") && !result.Text.Contains("Missing"));
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["verify", "--source", "steam", "--deep", ..common], storeFactory: Factory));
        Probe.Check("CLI verification uses the provider layout", result.Exit == 0 && result.Text.Contains("1 sound"));
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["library", "list", "--source", "GOG", "--keys", ..common], storeFactory: Factory));
        Probe.Check("--source remains case-insensitive", result.Exit == 0 && result.Text.Trim() == "gog:1");
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "itch:1", "--source", "steam", ..common], storeFactory: Factory));
        Probe.Check("qualified IDs cannot bypass the provider filter", result.Exit == 1 && extra.Downloads == 0);
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "1", "--source", "itch", "--provider-dir", "itch=indie", ..common], storeFactory: Factory));
        Probe.Check("injected future providers use the same selection and folder options",
            result.Exit == 0 && File.Exists(Path.Combine(output, "indie", "game", "Game.zip")));
        var customRoot = Path.Combine(root, "custom-cli");
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "--snapshot", snapshot, "--out", customRoot,
            "--provider-dir", "gog=drm_free", "--provider-dir", "steam=steam_library", "--directory", "chosen"], storeFactory: Factory));
        Probe.Check("single-game directory overrides cannot apply to multiple provider downloads", result.Exit == 1 && !Directory.Exists(customRoot));
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "--all", "--snapshot", snapshot, "--out", customRoot,
            "--provider-dir", "gog=drm_free", "--provider-dir", "steam=steam_library"], storeFactory: Factory));
        Probe.Check("repeatable provider-dir settings imply provider layout and retain unmapped defaults",
            result.Exit == 0 && File.Exists(Path.Combine(customRoot, "drm_free", "game", "Game.zip"))
            && File.Exists(Path.Combine(customRoot, "steam_library", "game", "Game.zip"))
            && File.Exists(Path.Combine(customRoot, "itch", "game", "Game.zip")));
        var previewRoot = Path.Combine(root, "preview");
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "gog:1", "--dry-run", "--layout", "provider", "--out", previewRoot], storeFactory: Factory));
        Probe.Check("dry-run reports the provider destination without allocating it",
            result.Exit == 0 && result.Text.Contains(Path.Combine(previewRoot, "gog")) && !Directory.Exists(previewRoot));
        foreach (var flags in new[] {
            new[] { "--provider-dir", "gog=../outside" }, new[] { "--provider-dir", "gog=/absolute" },
            new[] { "--provider-dir", "gog=" }, new[] { "--provider-dir", "unknown=games" },
            new[] { "--provider-dir", "gog=a", "--provider-dir", "GOG=b" }, new[] { "--provider-dir", "gog=steam" },
            new[] { "--provider-dir", "gog=games", "--provider-dir", "steam=GAMES" },
            new[] { "--layout", "flat", "--provider-dir", "gog=games" }, new[] { "--layout", "unknown" } })
        {
            result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["download", "gog:1", "--dry-run", ..flags], storeFactory: Factory));
            Probe.Check($"invalid provider layout is rejected: {string.Join(' ', flags)}", result.Exit == 1);
        }
    }

    private static async Task ConflictsAsync(string root)
    {
        var options = new ArchiveOptions { OutputRoot = Path.Combine(root, "conflicts"), Layout = ArchiveLayout.Provider };
        var folder = Path.Combine(options.OutputRoot, "gog");
        await new ArchiveMetadata { Source = "steam", ProductId = "1", Slug = "gog", Title = "GOG" }.SaveAsync(folder);
        Probe.Check("a game backup cannot become a provider container", await RejectsAsync(() => ProviderOutput.PrepareAsync(options, "gog")));
        File.Delete(Path.Combine(folder, ArchiveMetadata.FileName));
        Directory.Delete(folder);
        await File.WriteAllTextAsync(folder, "existing file");
        Probe.Check("a provider directory cannot replace a file", await RejectsAsync(() => ProviderOutput.PrepareAsync(options, "gog")) && await File.ReadAllTextAsync(folder) == "existing file");
        File.Delete(folder);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(folder); Directory.CreateDirectory(Path.Combine(options.OutputRoot, "GOG"));
            Probe.Check("provider folders reject case-equivalent collisions", await RejectsAsync(() => ProviderOutput.PrepareAsync(options, "gog")));
            Directory.Delete(Path.Combine(options.OutputRoot, "GOG")); Directory.Delete(folder);
            var external = Path.Combine(root, "external"); Directory.CreateDirectory(external);
            Directory.CreateSymbolicLink(folder, external);
            Probe.Check("provider folders never follow directory links", await RejectsAsync(() => ProviderOutput.PrepareAsync(options, "gog")) && !Directory.EnumerateFileSystemEntries(external).Any());
            Directory.Delete(folder);
        }
        var resolved = await ProviderOutput.PrepareAsync(options, "gog");
        Probe.Check("prepared provider folders cannot be adopted by a flat-layout game",
            await RejectsAsync(() => ArchiveCatalog.ReserveAsync(new(new("steam", "2"), "GOG", "gog"),
                new() { OutputRoot = options.OutputRoot }, default)));
        Probe.Check("a per-game alias stays below the selected provider folder",
            (await ArchiveCatalog.ReserveAsync(Game("gog"), resolved with { DirectoryName = "chosen" }, default)).Directory == Path.Combine(folder, "chosen"));
    }

    private static async Task<bool> RejectsAsync(Func<Task> action)
    { try { await action(); return false; } catch (ArchiveConflictException) { return true; } }

    private static async Task<(int Exit, string Text)> CaptureAsync(Func<Task<int>> action)
    {
        var stdout = Console.Out; var stderr = Console.Error;
        using var output = new StringWriter();
        try { Console.SetOut(output); Console.SetError(output); return (await action(), output.ToString()); }
        finally { Console.SetOut(stdout); Console.SetError(stderr); }
    }

    private sealed class Store(string source) : IGameStore
    {
        public string Source => source;
        public int Downloads;
        public ArchiveOptions? LastOptions;
        public Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default) => Task.FromResult(new StoreLibraryResult([Game(Source)]));
        public Task<GameVersion> GetVersionAsync(string productId, GameSelection options, CancellationToken ct = default) => Task.FromResult(Game(Source).Versions[options.Platform]);
        public async Task<ArchiveResult> DownloadAsync(string productId, ArchiveOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
        {
            Downloads++; LastOptions = options;
            var game = Game(Source, productId);
            var location = await ArchiveCatalog.ReserveAsync(game, options, ct);
            var path = Path.Combine(location.Directory, "Game.zip");
            if (File.Exists(path)) return new(game.Key, ArchiveOutcome.AlreadyDone, path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("game.txt").Open())) writer.Write(Source);
            location.Metadata.Platforms[GameOs.Windows] = new()
            { Version = game.Versions[GameOs.Windows], ArchiveName = "Game.zip", ArchiveSize = new FileInfo(path).Length };
            await location.Metadata.SaveAsync(location.Directory, ct);
            return new(game.Key, ArchiveOutcome.Completed, path);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
