using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GogArchiver.Cli;
using GogArchiver.Core;
using GogArchiver.Core.Download;
using GogArchiver.Core.Download.Zip;
using GogArchiver.Core.Library;

namespace GogArchiver.Probes;

public static class ArchiveSafetyProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Archive checks and publication preserve backup integrity");
        var root = Probe.ScratchDirectory();
        try
        {
            var stage = Path.Combine(root, "stage");
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "data.bin"), "GOOD");
            File.WriteAllBytes(Path.Combine(stage, "empty.bin"), []);
            var good = Path.Combine(root, "good.zip");
            using (var zip = ZipFile.Open(good, ZipArchiveMode.Create))
            {
                foreach (var path in Directory.EnumerateFiles(stage))
                {
                    using var target = zip.CreateEntry(Path.GetFileName(path), CompressionLevel.NoCompression).Open();
                    using var source = File.OpenRead(path);
                    source.CopyTo(target);
                }
            }
            await ArchiveVerifier.VerifyAsync(good, stage);
            foreach (var name in new[] { "data.bin", "empty.bin" })
            {
                var bad = Path.Combine(root, "bad-crc.zip");
                File.Copy(good, bad, overwrite: true);
                var header = ZipInspector.ReadCentralDirectory(bad).Single(e => e.Name == name);
                using (var file = File.OpenWrite(bad))
                {
                    var crc = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(crc, header.Crc ^ 1);
                    file.Position = header.CentralHeaderOffset + 16;
                    file.Write(crc);
                    file.Position = header.LocalHeaderOffset + 14;
                    file.Write(crc);
                }
                var rejected = false;
                try { await ArchiveVerifier.VerifyAsync(bad, stage); }
                catch (ArchiveCorruptException) { rejected = true; }
                Probe.Check($"publication rejects a wrong header CRC for {name} even when content matches staging", rejected);
            }

            var backupRoot = Path.Combine(root, "backups");
            var directory = Path.Combine(backupRoot, "game__1");
            Directory.CreateDirectory(directory);
            var archive = Path.Combine(directory, "game.zip");
            var manifest = new BackupManifest { GameId = 1, Slug = "game", Title = "Game" };
            manifest.Platforms[GamePlatform.Windows] = new()
            {
                BuildId = "old", Language = "en", ArchiveName = "game.zip",
                ArchiveSize = new FileInfo(good).Length, InstalledSize = 4,
            };
            await manifest.SaveAsync(directory);
            foreach (var slugs in new[] { false, true })
            {
                File.Copy(good, archive, overwrite: true);
                MarkEmptyDeflated(archive);
                var header = ZipInspector.ReadCentralDirectory(archive).Single(e => e.Name == "data.bin");
                using (var file = File.Open(archive, FileMode.Open, FileAccess.ReadWrite))
                {
                    var local = new byte[30];
                    file.Position = header.LocalHeaderOffset;
                    file.ReadExactly(local);
                    file.Position = header.LocalHeaderOffset + 30
                        + BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26))
                        + BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28));
                    file.WriteByte((byte)'X');
                }
                var result = await VerifyAsync(backupRoot, slugs);
                Probe.Check($"--deep --fix{(slugs ? " --slugs" : "")} still reports corrupt payload after repairing headers",
                    result.Code == 1 && (slugs ? result.Output.Trim() == "game" : result.Output.Contains("0 repaired, 1 broken")));
                Probe.Check("the repair completed but did not hide the remaining corruption",
                    ArchiveCheck.Headers(archive).Count == 0 && (await ArchiveCheck.DeepAsync(archive)).Count == 1);
            }
            File.Copy(good, archive, overwrite: true);
            MarkEmptyDeflated(archive);
            var repaired = await VerifyAsync(backupRoot, false);
            Probe.Check("--deep --fix succeeds when every payload is intact",
                repaired.Code == 0 && repaired.Output.Contains("1 repaired, 0 broken"));
            await PublicationFailureAsync(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void MarkEmptyDeflated(string path)
    {
        var header = ZipInspector.ReadCentralDirectory(path).Single(e => e.Name == "empty.bin");
        using var file = File.OpenWrite(path);
        file.Position = header.CentralHeaderOffset + 10;
        file.Write([8, 0]);
        file.Position = header.LocalHeaderOffset + 8;
        file.Write([8, 0]);
    }

    private static async Task<(int Code, string Output)> VerifyAsync(string root, bool slugs)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(TextWriter.Null);
            var flags = new Dictionary<string, string> { ["out"] = root, ["deep"] = "", ["fix"] = "" };
            if (slugs) flags["slugs"] = "";
            return (await VerifyCommands.RunAsync(["verify"], flags, default), output.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    private static async Task PublicationFailureAsync(string root)
    {
        using var http = new HttpClient(new DepotFixture());
        var downloader = new GameDownloader(new(http), new(http, http, new(0)), http);
        var game = new LibraryEntry
        {
            Id = 1, Slug = "publication", Title = "Publication",
            Platforms = new() { [GamePlatform.Windows] = new() { Depot = new("new", 2, "2", "https://fixture.invalid/repository") } },
        };
        var options = new DownloadOptions
        {
            OutputRoot = Path.Combine(root, "out"), StagingRoot = Path.Combine(root, "download"),
            Platform = GamePlatform.Windows, Reuse = false, WriteLauncher = false, MaxCompressionThreads = 1,
        };
        var directory = Path.Combine(options.OutputRoot, game.DirectoryName);
        Directory.CreateDirectory(directory);
        var previous = Path.Combine(directory, "old.zip");
        File.WriteAllText(previous, "OLD!");
        var manifest = new BackupManifest { GameId = game.Id, Slug = game.Slug, Title = game.Title };
        manifest.Platforms[GamePlatform.Windows] = new()
        { BuildId = "old", Language = "en", ArchiveName = "old.zip", ArchiveSize = 4, InstalledSize = 4 };
        await manifest.SaveAsync(directory);
        // Block the final destination after its old metadata has been read. Atomic saves
        // use unique temporary names, so obstructing one assumed .tmp name proves nothing.
        var obstruction = BackupManifest.PathFor(directory);
        var savedManifest = Path.Combine(directory, "saved-manifest.json");
        var originalManifest = File.ReadAllText(obstruction);
        var blocked = false;
        var progress = new GameArchiver.Core.InlineProgress<DownloadProgress>(p =>
        {
            if (p.Phase != "check" || blocked) return;
            File.Move(obstruction, savedManifest);
            Directory.CreateDirectory(obstruction);
            blocked = true;
        });
        var failed = false;
        try { await downloader.RunAsync(game, options, progress); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
        finally
        {
            if (blocked)
            {
                Directory.Delete(obstruction);
                File.Move(savedManifest, obstruction);
            }
        }
        Probe.Check("a failed manifest commit retains the previous archive and manifest",
            blocked && failed && File.Exists(previous) && File.ReadAllText(previous) == "OLD!"
            && Directory.EnumerateFiles(directory, "*.zip").Count() == 1
            && File.ReadAllText(obstruction) == originalManifest
            && !Directory.EnumerateFiles(directory, "*.tmp").Any()
            && (await BackupManifest.LoadAsync(directory))!.Platforms[GamePlatform.Windows].BuildId == "old");
        var result = await downloader.RunAsync(game, options);
        Probe.Check("a successful retry commits the new archive before removing the old one",
            result.Outcome == DownloadOutcome.Completed && File.Exists(result.ArchivePath)
            && !File.Exists(previous) && (await BackupManifest.LoadAsync(directory))!.Platforms[GamePlatform.Windows].BuildId == "new");
    }

    private sealed class DepotFixture : HttpMessageHandler
    {
        private static readonly byte[] Data = Encoding.UTF8.GetBytes("NEW!");
        private static readonly byte[] Packed = Compress();
        private static string Hash(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));
        private static byte[] Compress()
        {
            using var output = new MemoryStream();
            using (var stream = new ZLibStream(output, CompressionLevel.Fastest, true)) stream.Write(Data);
            return output.ToArray();
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpContent body = path switch
            {
                "/user/data/games" => Json(new { owned = new[] { 1 } }),
                "/repository" => Json(new { baseProductId = "1", depots = new[] { new { productId = "1", manifest = "aaaa", languages = new[] { "*" } } } }),
                _ when path.Contains("/meta/") => Json(new { depot = new { items = new[] { new { type = "DepotFile", path = "game.exe",
                    chunks = new[] { new { md5 = Hash(Data), compressedMd5 = Hash(Packed), size = Data.Length, compressedSize = Packed.Length } } } } } }),
                _ when path.EndsWith("/secure_link") => Json(new { urls = new[] { new { url_format = "https://fixture.invalid{path}", parameters = new { path = "/data" } } } }),
                _ when path.StartsWith("/data/") => new ByteArrayContent(Packed),
                _ => throw new InvalidOperationException("Unexpected fixture request: " + path),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }
        private static HttpContent Json(object value) => new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }
}
