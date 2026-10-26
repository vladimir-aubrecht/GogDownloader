using System.Text.Json.Nodes;
using GameArchiver;
using GameArchiver.Cli;
using GameArchiver.Core;
using GogArchiver.Core;
using GogArchiver.Core.Download;

namespace GogArchiver.Probes;

public static class MetadataMigrationProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Visible shared metadata names and legacy migration");
        var root = Probe.ScratchDirectory();
        try
        {
            var gog = Path.Combine(root, "gog"); Directory.CreateDirectory(gog);
            await new BackupManifest { GameId = 1, Slug = "gog", Title = "GOG", Platforms = new()
            { [GamePlatform.Windows] = new() { BuildId = "1", Language = "en", ArchiveName = "game.zip", ArchiveSize = 4, InstalledSize = 4,
                ContentRevision = 2, SkippedDependencies = ["directx"], Files = new() { ["game.exe"] = new("abc", 4) } } } }.SaveAsync(gog);
            var raw = JsonNode.Parse(await File.ReadAllTextAsync(BackupManifest.PathFor(gog)))!.AsObject();
            raw["Custom"] = "preserve"; raw.Remove("Source");
            File.Delete(BackupManifest.PathFor(gog));
            var gogOld = Path.Combine(gog, ArchiveMetadata.LegacyFileName);
            await File.WriteAllTextAsync(gogOld, raw.ToJsonString());
            await File.WriteAllTextAsync(Path.Combine(gog, "game.zip"), "data");
            var steam = Path.Combine(root, "steam"); Directory.CreateDirectory(steam);
            await new ArchiveMetadata { Source = "steam", ProductId = "2", Slug = "steam", Title = "Steam", Platforms = new()
            { [GameOs.Windows] = new() { Version = new("2"), ArchiveName = "game.zip", ArchiveSize = 4, InstalledSize = 4 } } }.SaveAsync(steam);
            var steamOld = Path.Combine(steam, ArchiveMetadata.LegacyUnifiedFileName);
            File.Move(Path.Combine(steam, ArchiveMetadata.FileName), steamOld);
            await File.WriteAllTextAsync(Path.Combine(steam, "game.zip"), "data");
            await using var client = new GameArchiverClient(new Offline("gog"), new Offline("steam"));
            var options = new ArchiveOptions { OutputRoot = root };
            var preview = await client.MigrateMetadataAsync(options, dryRun: true);
            Probe.Check("migration preview discovers both old names without writing", preview.Count == 2 && preview.All(r => r.Changed && r.Error is null)
                && File.Exists(gogOld) && File.Exists(steamOld) && !File.Exists(Path.Combine(gog, ArchiveMetadata.FileName)));
            var stdout = Console.Out; using var output = new StringWriter();
            int exit;
            try
            {
                Console.SetOut(output);
                exit = await ArchiveCommandLine.RunAsync(["archive", "migrate", "--source", "steam", "--out", root, "--dry-run"],
                    storeFactory: _ => [new Offline("steam")]);
            }
            finally { Console.SetOut(stdout); }
            Probe.Check("the migration CLI previews only the selected store without a snapshot or network",
                exit == 0 && output.ToString().Contains("Would migrate: steam:2") && !output.ToString().Contains("gog:1")
                && File.Exists(steamOld) && File.Exists(gogOld));
            var result = await client.MigrateMetadataAsync(options);
            Probe.Check("both stores use the same visible filename with their origin inside", result.All(r => r.Changed && r.Error is null)
                && !File.Exists(gogOld) && !File.Exists(steamOld)
                && (await ArchiveMetadata.LoadAsync(gog))!.Source == "gog" && (await ArchiveMetadata.LoadAsync(steam))!.Source == "steam"
                && (File.GetAttributes(Path.Combine(gog, ArchiveMetadata.FileName)) & FileAttributes.Hidden) == 0
                && (File.GetAttributes(Path.Combine(steam, ArchiveMetadata.FileName)) & FileAttributes.Hidden) == 0);
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(BackupManifest.PathFor(gog)))!;
            Probe.Check("migration preserves unknown fields, GOG hashes, content revision and dependency metadata",
                saved["Custom"]!.GetValue<string>() == "preserve" && saved["Platforms"]!["Windows"]!["ContentRevision"]!.GetValue<int>() == 2
                && (await BackupManifest.LoadAsync(gog))!.Platforms[GamePlatform.Windows].SkippedDependencies.SequenceEqual(["directx"])
                && saved["Platforms"]!["Windows"]!["Files"]!["game.exe"]!["Md5"]!.GetValue<string>() == "abc");
            Probe.Check("migration never touches archive bytes", await File.ReadAllTextAsync(Path.Combine(gog, "game.zip")) == "data"
                && await File.ReadAllTextAsync(Path.Combine(steam, "game.zip")) == "data");
            Probe.Check("a second migration is a no-op", (await client.MigrateMetadataAsync(options)).All(r => !r.Changed && r.Error is null));
            // Simulate interruption after canonical publication but before legacy cleanup.
            raw["Source"] = "gog"; raw["Title"] = "Stale title";
            await File.WriteAllTextAsync(gogOld, raw.ToJsonString());
            Probe.Check("canonical metadata wins after interrupted cleanup", (await ArchiveMetadata.LoadAsync(gog))!.Title == "GOG");
            preview = await client.MigrateMetadataAsync(options, dryRun: true);
            Probe.Check("preview reports pending legacy cleanup", preview.Single(r => r.Key.Source == "gog").Changed && File.Exists(gogOld));
            await client.MigrateMetadataAsync(options);
            Probe.Check("interrupted migration cleanup can be retried", !File.Exists(gogOld));
            await File.WriteAllTextAsync(gogOld, "{\"Source\":\"external\",\"Platforms\":\"foreign\"}");
            await client.MigrateMetadataAsync(options);
            Probe.Check("foreign legacy files remain untouched", File.Exists(gogOld));
            File.Delete(gogOld);
            raw["GameId"] = 99;
            await File.WriteAllTextAsync(gogOld, raw.ToJsonString());
            var before = await File.ReadAllTextAsync(BackupManifest.PathFor(gog));
            var rejected = false;
            try { await client.MigrateMetadataAsync(options); }
            catch (ArchiveConflictException) { rejected = true; }
            Probe.Check("conflicting owners fail without overwriting either manifest", rejected
                && before == await File.ReadAllTextAsync(BackupManifest.PathFor(gog)) && File.Exists(gogOld));
            File.Delete(gogOld);
            if (OperatingSystem.IsWindows())
            {
                var path = Path.Combine(steam, ArchiveMetadata.FileName);
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
                await client.MigrateMetadataAsync(options);
                Probe.Check("Windows Hidden is cleared even for an already canonical filename", (File.GetAttributes(path) & FileAttributes.Hidden) == 0);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Offline(string source) : IGameStore
    {
        public string Source => source;
        public Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default) => throw new Exception("No sync during metadata migration");
        public Task<GameVersion> GetVersionAsync(string id, GameSelection options, CancellationToken ct = default) => throw new Exception("No network during metadata migration");
        public Task<ArchiveResult> DownloadAsync(string id, ArchiveOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) => throw new Exception("No content download during metadata migration");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
