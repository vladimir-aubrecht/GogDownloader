using GogArchiver.Cli;
using GogArchiver.Core;
using GogArchiver.Core.Download;
using GogArchiver.Core.Library;

namespace GogArchiver.Probes;

public static class BackupDirectoryProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Slug-only backups and legacy migration");
        var root = Probe.ScratchDirectory();
        try
        {
            using var requests = new NoRequests();
            using var http = new HttpClient(requests);
            var downloader = new GameDownloader(new(http), new(http, http, new(0)), http);
            var game = Game(1, "a_game");
            var output = Path.Combine(root, "backups");
            var options = new DownloadOptions { OutputRoot = output, Platform = GamePlatform.Windows };
            var legacy = Path.Combine(output, game.LegacyDirectoryName);
            var current = Path.Combine(output, game.DirectoryName);
            await WriteBackupAsync(legacy, game);
            var originalManifest = await File.ReadAllTextAsync(BackupManifest.PathFor(legacy));
            var snapshot = Path.Combine(root, "library.json");
            await new LibrarySnapshot { SchemaVersion = LibrarySnapshot.CurrentSchema, Games = [game] }.SaveAsync(snapshot);
            var flags = new Dictionary<string, string> { ["snapshot"] = snapshot, ["out"] = output, ["all"] = "" };
            var diff = await CaptureAsync(() => DiffCommands.RunAsync(["diff"], flags, default));
            Probe.Check("diff recognizes a legacy backup without marking it as an orphan or renaming it",
                diff.Exit == 0 && diff.Text.Contains("1 up to date") && !diff.Text.Contains("Not in the library")
                && Directory.Exists(legacy) && !Directory.Exists(current));

            // An empty folder left by a failed run must not block an otherwise safe migration.
            Directory.CreateDirectory(current);
            var result = await downloader.RunAsync(game, options);
            Probe.Check("download renames a completed legacy backup without network or recompression",
                result.Outcome == DownloadOutcome.AlreadyDone && requests.Count == 0 && !Directory.Exists(legacy)
                && result.ArchivePath == Path.Combine(current, "game.zip")
                && await File.ReadAllTextAsync(result.ArchivePath) == "backup");
            Probe.Check("migration preserves GameId, all platforms and the original metadata bytes",
                await File.ReadAllTextAsync(BackupManifest.PathFor(current)) == originalManifest
                && (await BackupManifest.LoadAsync(current)) is { GameId: 1, Platforms.Count: 2 });
            Probe.Check("the renamed backup remains idempotent",
                (await downloader.RunAsync(game, options)).Outcome == DownloadOutcome.AlreadyDone && requests.Count == 0);
            diff = await CaptureAsync(() => DiffCommands.RunAsync(["diff"], flags, default));
            Probe.Check("diff recognizes the new folder layout", diff.Exit == 0 && diff.Text.Contains("1 up to date"));

            Probe.Group("Directory ownership prevents slug collisions");
            var other = game with { Id = 2 };
            foreach (var force in new[] { false, true })
                Probe.Check($"a different GameId cannot skip or overwrite an equal-build backup (force={force})",
                    await ConflictsAsync(() => downloader.RunAsync(other, options with { Force = force })) && requests.Count == 0
                    && await File.ReadAllTextAsync(BackupManifest.PathFor(current)) == originalManifest);

            await WriteBackupAsync(legacy, game);
            Probe.Check("two populated layouts are refused without merging or deleting either backup",
                await ConflictsAsync(() => downloader.RunAsync(game, options))
                && File.Exists(Path.Combine(legacy, "game.zip")) && File.Exists(Path.Combine(current, "game.zip")));
            Directory.Delete(legacy, true);

            var emptyGame = Game(3, "empty_game");
            var emptyLegacy = Path.Combine(output, emptyGame.LegacyDirectoryName);
            Directory.CreateDirectory(emptyLegacy);
            var claim = await BackupDirectory.PrepareAsync(output, emptyGame);
            Probe.Check("an empty legacy directory migrates and is reserved, not marked completed",
                !Directory.Exists(emptyLegacy) && claim.Directory == Path.Combine(output, "empty_game")
                && claim.Manifest is { GameId: 3, Platforms.Count: 0 });
            Probe.Check("interrupted ownership prevents a second GameId from claiming the same slug",
                await ConflictsAsync(() => BackupDirectory.PrepareAsync(output, emptyGame with { Id = 4 })));
            Probe.Check("the original owner can resume from its empty-platform manifest",
                (await BackupDirectory.PrepareAsync(output, emptyGame)).Manifest?.GameId == 3);
            await new LibrarySnapshot { SchemaVersion = LibrarySnapshot.CurrentSchema, Games = [emptyGame] }.SaveAsync(snapshot);
            diff = await CaptureAsync(() => DiffCommands.RunAsync(["diff"], flags, default));
            Probe.Check("a reserved folder is missing in diff until a platform completes",
                diff.Exit == 0 && diff.Text.Contains("0 up to date, 1 missing"));

            var unknown = Game(5, "unknown");
            var unknownPath = Path.Combine(output, unknown.DirectoryName);
            Directory.CreateDirectory(unknownPath);
            await File.WriteAllTextAsync(Path.Combine(unknownPath, "keep.zip"), "unidentified backup");
            Probe.Check("nonempty folders without metadata are preserved and refused",
                await ConflictsAsync(() => BackupDirectory.PrepareAsync(output, unknown))
                && await File.ReadAllTextAsync(Path.Combine(unknownPath, "keep.zip")) == "unidentified backup");
            await File.WriteAllTextAsync(BackupManifest.PathFor(unknownPath), "broken json");
            Probe.Check("invalid metadata cannot authorize ownership",
                await ConflictsAsync(() => BackupDirectory.PrepareAsync(output, unknown)));

            var caseGame = Game(6, "case_game");
            var upperPath = Path.Combine(output, "CASE_GAME");
            await WriteBackupAsync(upperPath, caseGame with { Id = 99 });
            Probe.Check("case variants on disk are checked even on case-sensitive filesystems",
                await ConflictsAsync(() => BackupDirectory.PrepareAsync(output, caseGame))
                && (await BackupManifest.LoadAsync(upperPath))?.GameId == 99);
            var wrongLegacy = Game(7, "wrong_legacy");
            var wrongLegacyPath = Path.Combine(output, wrongLegacy.LegacyDirectoryName);
            await WriteBackupAsync(wrongLegacyPath, wrongLegacy with { Id = 99 });
            Probe.Check("a legacy suffix cannot override the metadata's actual GameId",
                await ConflictsAsync(() => BackupDirectory.PrepareAsync(output, wrongLegacy))
                && Directory.Exists(wrongLegacyPath) && !Directory.Exists(Path.Combine(output, wrongLegacy.DirectoryName)));

            var fileGame = Game(8, "occupied");
            await File.WriteAllTextAsync(Path.Combine(output, fileGame.DirectoryName), "keep");
            Probe.Check("a regular file occupying the slug is not overwritten",
                await ConflictsAsync(() => BackupDirectory.PrepareAsync(output, fileGame)));
            if (!OperatingSystem.IsWindows())
            {
                var linked = Game(9, "linked");
                Directory.CreateSymbolicLink(Path.Combine(output, linked.DirectoryName), current);
                Probe.Check("a directory link cannot alias another backup",
                    await ConflictsAsync(() => BackupDirectory.PrepareAsync(output, linked)));
            }
            foreach (var slug in new[] { "../escape", "CON", "game.", "foo/bar", "game " })
                Probe.Check($"unsafe slug '{slug}' cannot become a backup directory",
                    await ConflictsAsync(() => BackupDirectory.PrepareAsync(output, Game(10, slug))));

            await SelectionAsync(root, snapshot, game);
            await ConcurrentClaimsAsync(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task SelectionAsync(string root, string snapshot, LibraryEntry game)
    {
        Probe.Group("Collision preflight covers unselected games");
        var output = Path.Combine(root, "preflight");
        foreach (var slug in new[] { game.Slug, game.Slug.ToUpperInvariant() })
        {
            var other = game with { Id = 2, Slug = slug, Title = "Another game" };
            var unrelated = Game(3, "unrelated");
            await new LibrarySnapshot { SchemaVersion = LibrarySnapshot.CurrentSchema, Games = [game, other, unrelated] }.SaveAsync(snapshot);
            var flags = new Dictionary<string, string> { ["snapshot"] = snapshot, ["out"] = output, ["dry-run"] = "" };
            var single = await CaptureAsync(() => DownloadCommands.RunAsync(["download", "1"], flags, default));
            var batch = await CaptureAsync(() => DownloadCommands.RunAsync(["download"], new(flags) { ["all"] = "" }, default));
            var diff = await CaptureAsync(() => DiffCommands.RunAsync(["diff"], flags, default));
            Probe.Check($"duplicate slug '{slug}' is refused before writes, even when selected by ID",
                single.Exit == 1 && batch.Exit == 1 && diff.Exit == 1 && single.Text.Contains("Directory name collision")
                && single.Text.Contains("GOG 1") && single.Text.Contains("GOG 2") && !Directory.Exists(output));
            var safe = await CaptureAsync(() => DownloadCommands.RunAsync(["download", "3"], flags, default));
            Probe.Check("an unrelated selection is not blocked by another game's slug conflict", safe.Exit == 0);
        }
    }

    private static async Task ConcurrentClaimsAsync(string root)
    {
        Probe.Group("Concurrent slug ownership");
        // Both callers may observe an empty directory. Only one metadata claim may win.
        var safe = true;
        for (var i = 0; i < 40; i++)
        {
            var output = Path.Combine(root, "race" + i);
            using var start = new ManualResetEventSlim();
            async Task<long?> ClaimAsync(LibraryEntry game)
            {
                start.Wait();
                try { return (await BackupDirectory.PrepareAsync(output, game)).Manifest?.GameId; }
                catch (BackupDirectoryConflictException) { return null; }
            }
            var first = Task.Run(() => ClaimAsync(Game(1, "same_slug")));
            var second = Task.Run(() => ClaimAsync(Game(2, "SAME_SLUG")));
            start.Set();
            var owners = await Task.WhenAll(first, second);
            var stored = await BackupManifest.LoadAsync(Path.Combine(output, "same_slug"));
            safe &= owners.Count(id => id is not null) == 1 && owners.Single(id => id is not null) == stored?.GameId
                && Directory.EnumerateDirectories(output).Count() == 1
                && !Directory.EnumerateFiles(Path.Combine(output, "same_slug"), "*.tmp").Any();
        }
        Probe.Check("simultaneous different GameIds with case-equivalent slugs cannot acquire one folder", safe);
    }

    private static LibraryEntry Game(long id, string slug) => new()
    {
        Id = id, Slug = slug, Title = "Game " + id,
        Platforms = new() { [GamePlatform.Windows] = new() { Depot = new("build", 2, "1", "https://fixture.invalid/repository") } },
    };

    private static async Task WriteBackupAsync(string path, LibraryEntry game)
    {
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "game.zip"), "backup");
        var backup = new PlatformBackup
        {
            ContentRevision = PlatformBackup.CurrentContentRevision, BuildId = "build", Language = "en",
            ArchiveName = "game.zip", ArchiveSize = 6, InstalledSize = 6,
        };
        await new BackupManifest { GameId = game.Id, Slug = game.Slug, Title = game.Title,
            Platforms = new() { [GamePlatform.Windows] = backup, [GamePlatform.Linux] = backup } }.SaveAsync(path);
    }

    private static async Task<bool> ConflictsAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (BackupDirectoryConflictException) { return true; }
    }

    private static async Task<(int Exit, string Text)> CaptureAsync(Func<Task<int>> action)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(output);
            return (await action(), output.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    private sealed class NoRequests : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Count++;
            throw new InvalidOperationException("Unexpected network request: " + request.RequestUri);
        }
    }
}
