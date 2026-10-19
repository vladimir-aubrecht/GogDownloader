using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GogDownloader.Core;
using GogDownloader.Core.Api;
using GogDownloader.Core.Content;
using GogDownloader.Core.Download;
using GogDownloader.Core.Library;

namespace GogDownloader.Probes;

public static class DepotOverlayProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("DLC file overrides in both depot generations");
        foreach (var generation in new[] { 1, 2 })
        foreach (var dlcFirst in new[] { false, true })
        {
            var root = Probe.ScratchDirectory();
            try { await CheckAsync(root, generation, dlcFirst); }
            finally { Directory.Delete(root, recursive: true); }
        }

        Probe.Group("Resuming old DLC staging follows the current file plan");
        foreach (var generation in new[] { 1, 2 })
        foreach (var scenario in new[] { "missing hash", "old casing", "both casings" })
        {
            var root = Probe.ScratchDirectory();
            try { await CheckResumeAsync(root, generation, scenario); }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task CheckAsync(string root, int generation, bool dlcFirst)
    {
        var label = $"gen{generation}, DLC {(dlcFirst ? "first" : "last")}";
        using var fixture = new Fixture(generation, dlcFirst);
        using var http = new HttpClient(fixture);
        var downloader = new GameDownloader(new(http), new(http, http, new(0)), http);
        var game = new LibraryEntry
        {
            Id = 1, Slug = "overlay", Title = "Overlay",
            Platforms = new() { [GamePlatform.Windows] = new()
            {
                Depot = new("build", generation, "1", "https://fixture.invalid/repository"), Dlcs = [],
            } },
        };
        var options = new DownloadOptions
        {
            OutputRoot = root, StagingRoot = Path.Combine(root, "staging"),
            Platform = GamePlatform.Windows, WriteLauncher = false, MaxCompressionThreads = 2,
        };
        var first = await downloader.RunAsync(game, options);
        Probe.Check($"{label}: unowned DLC cannot replace the base executable",
            first.Outcome == DownloadOutcome.Completed && await ReadAsync(first.ArchivePath!, "game.exe") == "BASE");

        // Model an archive written by the old first-wins merger: it claims the DLC but
        // actually contains the base executable, under the same build and version.
        var directory = Path.Combine(root, game.DirectoryName);
        var old = (await BackupManifest.LoadAsync(directory))!;
        old.Platforms[GamePlatform.Windows] = old.Platforms[GamePlatform.Windows] with
        {
            Dlcs = ["2"], ContentRevision = 0,
        };
        await old.SaveAsync(directory);
        fixture.OwnsDlc = true;
        game = game with { Platforms = new() { [GamePlatform.Windows] = game.Platforms[GamePlatform.Windows] with
        {
            Dlcs = [new("2", "Expansion")],
        } } };
        var before = fixture.BaseTransfers;
        var corrected = await downloader.RunAsync(game, options);
        Probe.Check($"{label}: an old DLC backup is rebuilt without --force",
            corrected.Outcome == DownloadOutcome.Completed);
        Probe.Check($"{label}: the archive contains the DLC executable bytes",
            await ReadAsync(corrected.ArchivePath!, "game.exe") == "PLUS");
        Probe.Check($"{label}: unchanged base files are reused", fixture.BaseTransfers == before);
        using (var archive = ZipFile.OpenRead(corrected.ArchivePath!))
            Probe.Check($"{label}: case-only overrides produce one executable",
                archive.Entries.Count(e => e.FullName.Equals("game.exe", StringComparison.OrdinalIgnoreCase)) == 1);
        var backup = (await BackupManifest.LoadAsync(directory))!.Platforms[GamePlatform.Windows];
        Probe.Check($"{label}: metadata identifies the replacement bytes",
            backup.Files["game.exe"].Md5 == Fixture.Hash("PLUS") && backup.Dlcs is ["2"] && !backup.RequiresContentRefresh(GamePlatform.Windows));
        Probe.Check($"{label}: a repaired archive is not downloaded again",
            (await downloader.RunAsync(game, options)).Outcome == DownloadOutcome.AlreadyDone);

        // A partial base-only run is also a possible starting point when DLC is acquired.
        var stage = Path.Combine(options.StagingDirectory, game.DirectoryName + "__windows");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "game.exe"), "BASE");
        new StagingState("build", "en", "windows").Save(stage);
        var resumed = await downloader.RunAsync(game, options with { Force = true, Reuse = false });
        using var resumedZip = ZipFile.OpenRead(resumed.ArchivePath!);
        Probe.Check($"{label}: resuming replaces the base file without leaving a case duplicate",
            await ReadAsync(resumed.ArchivePath!, "game.exe") == "PLUS"
            && resumedZip.Entries.Count(e => e.FullName.Equals("game.exe", StringComparison.OrdinalIgnoreCase)) == 1);
    }

    private static async Task<string> ReadAsync(string zip, string path)
    {
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry(path)!.Open());
        return await reader.ReadToEndAsync();
    }

    private static async Task CheckResumeAsync(string root, int generation, string scenario)
    {
        var label = $"gen{generation}, {scenario}";
        using var fixture = new Fixture(generation, dlcFirst: true)
        {
            OwnsDlc = true, OmitDlcIdentity = scenario == "missing hash", ExecutablePath = "bin/game.exe",
            IncludeLayout = true,
        };
        using var http = new HttpClient(fixture);
        var downloader = new GameDownloader(new(http), new(http, http, new(0)), http);
        var game = new LibraryEntry
        {
            Id = 1, Slug = "overlay", Title = "Overlay",
            Platforms = new() { [GamePlatform.Windows] = new()
            {
                Depot = new("build", generation, "1", "https://fixture.invalid/repository"),
                Dlcs = [new("2", "Expansion")],
            } },
        };
        var options = new DownloadOptions
        {
            OutputRoot = root, StagingRoot = Path.Combine(root, "staging"),
            Platform = GamePlatform.Windows, WriteLauncher = false, MaxCompressionThreads = 2,
        };
        var stage = Path.Combine(options.StagingDirectory, game.DirectoryName + "__windows");
        var canonical = Path.Combine(stage, fixture.ExecutablePath);
        var oldPath = scenario == "missing hash" ? canonical : Path.Combine(stage, "BIN/GAME.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        File.WriteAllText(oldPath, "BASE");
        // A case-sensitive disk can hold a correct new file alongside the obsolete spelling.
        var hasCorrectCopy = scenario == "both casings" && !File.Exists(canonical);
        if (hasCorrectCopy)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
            File.WriteAllText(canonical, "PLUS");
        }
        File.WriteAllText(Path.Combine(stage, "asset.bin"), "asset");
        Directory.CreateDirectory(Path.Combine(stage, "data/empty"));
        Directory.CreateDirectory(Path.Combine(stage, "obsolete/nested"));
        File.WriteAllText(Path.Combine(stage, "obsolete/nested/old.bin"), "old");
        new StagingState("build", "en", "windows").Save(stage);

        var result = await downloader.RunAsync(game, options);
        Probe.Check($"{label}: the resumed archive contains the DLC bytes",
            result.Outcome == DownloadOutcome.Completed && await ReadAsync(result.ArchivePath!, fixture.ExecutablePath) == "PLUS");
        using (var archive = ZipFile.OpenRead(result.ArchivePath!))
        {
            var expected = new[] { "asset.bin", "bin/game.exe", "data/empty/" }
                .Concat(generation == 2 ? ["asset-link.bin"] : []).Order(StringComparer.Ordinal);
            Probe.Check($"{label}: the ZIP keeps planned paths and empty directories, without obsolete spellings",
                archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).SequenceEqual(expected));
        }
        if (generation == 2)
            Probe.Check($"{label}: the planned link still has its target's bytes",
                await ReadAsync(result.ArchivePath!, "asset-link.bin") == "asset");
        Probe.Check($"{label}: verified staging files are reused",
            fixture.BaseTransfers == 0 && (!hasCorrectCopy || fixture.DlcTransfers == 0));
        if (scenario == "missing hash")
            Probe.Check($"{label}: equal size alone cannot skip the DLC transfer",
                fixture.DlcTransfers == (generation == 2 ? 2 : 1));
        var backup = (await BackupManifest.LoadAsync(Path.Combine(root, game.DirectoryName)))!.Platforms[GamePlatform.Windows];
        Probe.Check($"{label}: corrected content is recorded and skipped on the next run",
            backup.ContentRevision == PlatformBackup.CurrentContentRevision && backup.Dlcs is ["2"]
            && (await downloader.RunAsync(game, options)).Outcome == DownloadOutcome.AlreadyDone);
    }

    private sealed class Fixture(int generation, bool dlcFirst) : HttpMessageHandler
    {
        public bool OwnsDlc { get; set; }
        public bool OmitDlcIdentity { get; init; }
        public string ExecutablePath { get; init; } = "game.exe";
        public bool IncludeLayout { get; init; }
        public int BaseTransfers;
        public int DlcTransfers;
        private static readonly string[] Payloads = ["BASE", "PLUS", "NOPE", "asset"];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpContent body;
            if (path == "/user/data/games") body = Json(new { owned = OwnsDlc ? new[] { 1, 2 } : new[] { 1 } });
            else if (path == "/repository")
            {
                var ids = dlcFirst ? new[] { "2", "1", "3" } : new[] { "1", "2", "3" };
                body = generation == 2 ? Json(new
                {
                    baseProductId = "1", products = ids.Select(id => new { productId = id }),
                    depots = ids.Select(id => new { productId = id, manifest = id.PadLeft(4, 'a'), languages = new[] { "*" } }),
                }) : Json(new { product = new
                {
                    rootGameID = "1", timestamp = 1,
                    depots = ids.Select(id => new { gameIDs = new[] { id }, manifest = id.PadLeft(4, 'a'), languages = new[] { "Neutral" } }),
                } });
            }
            else if (path.Contains("/meta/") || path.Contains("/manifests/"))
            {
                var id = int.Parse(path[^1..]);
                var records = new[] { (Path: id == 1 ? ExecutablePath : ExecutablePath.ToUpperInvariant(), Data: Payloads[id - 1], Offset: 0) }
                    .Concat(id == 1 ? [(Path: "asset.bin", Data: "asset", Offset: 4)] : []);
                body = generation == 2 ? Json(new { depot = new { items = records.Select(r => (object)new
                {
                    type = "DepotFile", path = r.Path,
                    // Multi-chunk depots may omit the whole-file hash even though chunks have hashes.
                    chunks = (OmitDlcIdentity && id == 2 ? new[] { r.Data[..2], r.Data[2..] } : new[] { r.Data })
                        .Select(data => new { md5 = Hash(data), compressedMd5 = Hash(Compress(data)), size = data.Length, compressedSize = Compress(data).Length }),
                }).Concat(IncludeLayout && id == 1 ? new object[]
                {
                    new { type = "DepotDirectory", path = "data/empty" },
                    new { type = "DepotLink", path = "asset-link.bin", target = "asset.bin" },
                } : []) } }) : Json(new { depot = new { files = records.Select(r => (object)new
                {
                    path = r.Path, size = r.Data.Length, offset = r.Offset, hash = OmitDlcIdentity && id == 2 ? null : Hash(r.Data),
                }).Concat(IncludeLayout && id == 1 ? new object[] { new { path = "data/empty", directory = true } } : []) } });
            }
            else if (path.EndsWith("/secure_link"))
            {
                var id = path.Split('/')[2];
                body = Json(new { urls = new[] { new { url_format = "https://fixture.invalid{path}", parameters = new { path = "/data/" + id } } } });
            }
            else if (path.StartsWith("/data/"))
            {
                var id = int.Parse(path.Split('/')[2]);
                if (id == 1) Interlocked.Increment(ref BaseTransfers);
                else Interlocked.Increment(ref DlcTransfers);
                if (generation == 2)
                    body = new ByteArrayContent(Compress(Payloads.Concat(["PL", "US"]).Single(p => path.EndsWith(Hash(Compress(p))))));
                else
                {
                    var bytes = Encoding.UTF8.GetBytes(Payloads[id - 1] + (id == 1 ? "asset" : ""));
                    var range = request.Headers.Range!.Ranges.Single();
                    body = new ByteArrayContent(bytes[(int)range.From!..((int)range.To! + 1)]);
                }
            }
            else throw new InvalidOperationException("Unexpected fixture request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }

        public static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
        private static string Hash(byte[] value) => Convert.ToHexStringLower(MD5.HashData(value));
        private static HttpContent Json(object value) => new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
        private static byte[] Compress(string value)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, true)) zlib.Write(Encoding.UTF8.GetBytes(value));
            return output.ToArray();
        }
    }
}
