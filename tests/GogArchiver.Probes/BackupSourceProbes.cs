using System.IO.Compression;
using System.Text.Json.Nodes;
using GogArchiver.Cli;
using GogArchiver.Core;
using GogArchiver.Core.Download;
using GogArchiver.Core.Library;

namespace GogArchiver.Probes;

public static class BackupSourceProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Backup origin and mixed-source libraries");
        var root = Probe.ScratchDirectory();
        try
        {
            var game = new LibraryEntry { Id = 1, Slug = "gog_game", Title = "GOG game",
                Platforms = new() { [GamePlatform.Windows] = new() { Depot = new("build", 2, "1", "https://fixture.invalid/repository") } } };
            var output = Path.Combine(root, "backups");
            var legacy = Path.Combine(output, game.LegacyDirectoryName);
            var manifest = await WriteBackupAsync(legacy, game);
            var json = (await JsonNodeAsync(legacy))!;
            Probe.Check("new manifests persist their GOG origin", json["Source"]?.GetValue<string>() == "gog");
            json.Remove("Source");
            await File.WriteAllTextAsync(BackupManifest.PathFor(legacy), json.ToJsonString());
            var oldMetadata = await File.ReadAllBytesAsync(BackupManifest.PathFor(legacy));
            var oldArchive = await File.ReadAllBytesAsync(Path.Combine(legacy, "game.zip"));
            Probe.Check("legacy manifests without Source still identify GOG backups",
                (await BackupManifest.LoadAsync(legacy)) is { Source: "gog", GameId: 1 });

            var unmanaged = Path.Combine(output, "manual_game");
            Directory.CreateDirectory(unmanaged);
            await File.WriteAllTextAsync(Path.Combine(unmanaged, "manual.zip"), "not our archive");
            var foreign = Path.Combine(output, "steam_game");
            Directory.CreateDirectory(foreign);
            await File.WriteAllTextAsync(Path.Combine(foreign, "foreign.zip"), "do not repair this archive");
            const string foreignJson = """
                {"Source":"steam","GameId":"foreign-id","Platforms":"a different schema"}
                """;
            await File.WriteAllTextAsync(BackupManifest.PathFor(foreign), foreignJson);
            Probe.Check("foreign metadata is skipped before interpreting GOG-specific fields",
                await BackupManifest.LoadAsync(foreign) is null);
            var orphan = Path.Combine(output, "gog_orphan");
            Directory.CreateDirectory(orphan);
            await new BackupManifest { GameId = 99, Slug = "gog_orphan", Title = "GOG orphan" }.SaveAsync(orphan);
            var snapshot = Path.Combine(root, "library.json");
            await new LibrarySnapshot { SchemaVersion = LibrarySnapshot.CurrentSchema, Games = [game] }.SaveAsync(snapshot);
            var flags = new Dictionary<string, string> { ["out"] = output, ["snapshot"] = snapshot, ["all"] = "" };
            var diff = await CaptureAsync(() => DiffCommands.RunAsync(["diff"], flags, default));
            Probe.Check("diff reports only identified GOG orphans in a mixed library",
                diff.Exit == 0 && diff.Text.Contains("1 up to date") && diff.Text.Contains("gog_orphan")
                && !diff.Text.Contains("manual_game") && !diff.Text.Contains("steam_game"));
            var verify = await CaptureAsync(() => VerifyCommands.RunAsync(["verify"],
                new() { ["out"] = output, ["deep"] = "", ["fix"] = "" }, default));
            Probe.Check("verify --deep --fix inspects only GOG archives in a mixed library",
                verify.Exit == 0 && verify.Text.Contains("1 sound, 0 repaired, 0 broken, 0 missing")
                && await File.ReadAllTextAsync(Path.Combine(foreign, "foreign.zip")) == "do not repair this archive"
                && await File.ReadAllTextAsync(BackupManifest.PathFor(foreign)) == foreignJson);
            Probe.Check("diff and verify do not backfill or rename legacy metadata",
                (await File.ReadAllBytesAsync(BackupManifest.PathFor(legacy))).SequenceEqual(oldMetadata));

            using var requests = new NoRequests();
            using var http = new HttpClient(requests);
            var downloader = new GameDownloader(new(http), new(http, http, new(0)), http);
            var options = new DownloadOptions { OutputRoot = output, Platform = GamePlatform.Windows };
            var result = await downloader.RunAsync(game, options);
            var current = Path.Combine(output, game.DirectoryName);
            json["Source"] = "gog";
            Probe.Check("a current legacy backup gains Source while migrating, without a network request",
                result.Outcome == DownloadOutcome.AlreadyDone && requests.Count == 0 && !Directory.Exists(legacy)
                && JsonNode.DeepEquals(json, await JsonNodeAsync(current))
                && (await File.ReadAllBytesAsync(result.ArchivePath!)).SequenceEqual(oldArchive));
            var missing = game with { Id = 2, Slug = "pending" };
            var reserved = await BackupDirectory.PrepareAsync(output, missing);
            Probe.Check("an interrupted download's ownership includes its source",
                (await JsonNodeAsync(reserved.Directory))?["Source"]?.GetValue<string>() == "gog"
                && reserved.Manifest?.Platforms.Count == 0);

            await CollisionAsync(root, game, manifest, downloader, requests);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task CollisionAsync(string root, LibraryEntry game, BackupManifest manifest,
        GameDownloader downloader, NoRequests requests)
    {
        Probe.Group("Source is part of backup ownership");
        var output = Path.Combine(root, "collisions");
        var options = new DownloadOptions { OutputRoot = output, Platform = GamePlatform.Windows };
        foreach (var legacy in new[] { false, true })
        {
            var path = Path.Combine(output, legacy ? game.LegacyDirectoryName : game.DirectoryName);
            await WriteBackupAsync(path, game);
            foreach (var source in new[] { "\"steam\"", "\"manual\"", "null", "\"\"", "42" })
            {
                var json = (await JsonNodeAsync(path))!;
                json.Remove("Source");
                // Case-insensitive source keys must not evade the ownership check.
                json["source"] = JsonNode.Parse(source);
                var bytes = json.ToJsonString();
                await File.WriteAllTextAsync(BackupManifest.PathFor(path), bytes);
                foreach (var force in new[] { false, true })
                {
                    var refused = false;
                    try { await downloader.RunAsync(game, options with { Force = force }); }
                    catch (BackupDirectoryConflictException e) { refused = e.Message.Contains("source"); }
                    Probe.Check($"source {source}, legacy={legacy}, force={force}: matching GameId/build cannot authorize reuse",
                        refused && requests.Count == 0 && Directory.Exists(path)
                        && await File.ReadAllTextAsync(BackupManifest.PathFor(path)) == bytes);
                }
            }
            await (manifest with { Source = "GOG" }).SaveAsync(path);
            Probe.Check("GOG source values are case-insensitive", (await BackupManifest.LoadAsync(path))?.GameId == game.Id);
            Directory.Delete(path, true);
        }
    }

    private static async Task<BackupManifest> WriteBackupAsync(string path, LibraryEntry game)
    {
        Directory.CreateDirectory(path);
        var archive = Path.Combine(path, "game.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("game.txt").Open()))
            writer.Write("content");
        var manifest = new BackupManifest { GameId = game.Id, Slug = game.Slug, Title = game.Title,
            Platforms = new() { [GamePlatform.Windows] = new() { BuildId = "build", Language = "en",
                ArchiveName = "game.zip", ArchiveSize = new FileInfo(archive).Length, InstalledSize = 7 } } };
        await manifest.SaveAsync(path);
        return manifest;
    }

    private static async Task<JsonObject?> JsonNodeAsync(string directory)
    {
        await using var stream = File.OpenRead(BackupManifest.PathFor(directory));
        return (await JsonNode.ParseAsync(stream))?.AsObject();
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
