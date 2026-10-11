using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GogDownloader.Cli;
using GogDownloader.Core;
using GogDownloader.Core.Api;
using GogDownloader.Core.Content;
using GogDownloader.Core.Download;
using GogDownloader.Core.Library;

namespace GogDownloader.Probes;

public static class DlcProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("DLC is backed up with its base game");
        var root = Probe.ScratchDirectory();
        try
        {
            var expansion = new LibraryEntry
            {
                Id = 2, Slug = "space_age", Title = "Factorio: Space Age",
                Platforms = new() { [GamePlatform.Windows] = new()
                {
                    Installers = [new InstallerRef("en", "1", 4_000_000_000)],
                } },
            };
            var dlc = new DlcRef("2", expansion.Title);
            var game = new LibraryEntry
            {
                Id = 1, Slug = "factorio", Title = "Factorio", Dlcs = [dlc],
                Platforms = new()
                {
                    [GamePlatform.Windows] = new()
                    {
                        Depot = new BuildRef("build", 2, "1", "https://fixture.invalid/repository"),
                        Dlcs = [dlc],
                    },
                    [GamePlatform.Mac] = new()
                    {
                        Depot = new BuildRef("mac-build", 2, "1", "https://fixture.invalid/mac"),
                        Dlcs = [],
                    },
                    [GamePlatform.Linux] = new()
                    {
                        IsLinux = true, Installers = [new InstallerRef("en", "1", 100)],
                    },
                },
            };
            var snapshot = new LibrarySnapshot
            {
                SchemaVersion = LibrarySnapshot.CurrentSchema, Games = [game, expansion],
            };
            Probe.Check("an explicit DLC selects the base game",
                GameFilter.ResolveDownloads(snapshot, [expansion], GamePlatform.Windows).Select(g => g.Id).ToList() is [1]);
            Probe.Check("a batch downloads the base and its DLC once",
                GameFilter.ResolveDownloads(snapshot, [expansion, game], GamePlatform.Windows).Select(g => g.Id).ToList() is [1]);
            Probe.Check("Windows DLC does not imply macOS DLC",
                snapshot.DlcParents(expansion, GamePlatform.Mac).Count == 0);
            Probe.Check("a Linux installer does not claim to include DLC",
                snapshot.DlcParents(expansion, GamePlatform.Linux).Count == 0);
            Probe.Check("a missing base game does not silently discard a DLC",
                GameFilter.ResolveDownloads(snapshot with { Games = [expansion] }, [expansion], GamePlatform.Windows)
                    .Select(g => g.Id).ToList() is [2]);

            var snapshotPath = Path.Combine(root, "library.json");
            await snapshot.SaveAsync(snapshotPath);
            var loaded = (await LibrarySnapshot.LoadAsync(snapshotPath))!;
            Probe.Check("per-platform DLC survives a snapshot round trip",
                loaded.DlcParents(loaded.Games[1], GamePlatform.Windows).Count == 1
                && loaded.DlcParents(loaded.Games[1], GamePlatform.Mac).Count == 0);

            var single = await Cli("download", expansion.Slug, "--dry-run", "--snapshot", snapshotPath, "--out", root);
            Probe.Check("download <DLC> explains the redirect and selects one game",
                single.Code == 0 && single.Error.Contains("is DLC")
                && single.Error.Contains("1 game for windows") && !single.Output.Contains("Space Age"));
            var batch = await Cli("download", "--all", "--dry-run", "--snapshot", snapshotPath, "--out", root);
            Probe.Check("download --all deduplicates the base game", batch.Code == 0 && batch.Error.Contains("1 game for windows"));
            var filtered = await Cli("download", "--all", "space_age", "--dry-run", "--snapshot", snapshotPath, "--out", root);
            Probe.Check("a DLC-only search still selects its base", filtered.Code == 0 && filtered.Error.Contains("1 game for windows"));

            // A tiny, supposedly completed DLC must not hide an absent base game.
            var oldDirectory = Path.Combine(root, expansion.DirectoryName);
            Directory.CreateDirectory(oldDirectory);
            var old = new BackupManifest { GameId = 2, Slug = expansion.Slug, Title = expansion.Title };
            old.Platforms[GamePlatform.Windows] = new()
            {
                BuildId = "installer-1", Language = "en", ArchiveName = "old.zip", ArchiveSize = 1, InstalledSize = 100,
            };
            await old.SaveAsync(oldDirectory);
            await File.WriteAllBytesAsync(Path.Combine(oldDirectory, "old.zip"), [0]);
            Directory.CreateDirectory(Path.Combine(root, game.DirectoryName));
            var missing = await Cli("diff", "space_age", "--slugs", "--snapshot", snapshotPath, "--out", root);
            Probe.Check("an empty base directory is missing even if the DLC has a manifest",
                missing.Output.Trim() == "factorio");

            await DownloadAndRepair(game, snapshotPath, root);
            Probe.Check("old standalone DLC archives are not deleted", File.Exists(Path.Combine(oldDirectory, "old.zip")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task DownloadAndRepair(LibraryEntry game, string snapshotPath, string root)
    {
        using var handler = new ContentFixture();
        using var http = new HttpClient(handler);
        var downloader = new GameDownloader(new GogApiClient(http), new ContentClient(http, http, new RateLimiter(0)), http);
        var options = new DownloadOptions
        {
            OutputRoot = root, StagingRoot = Path.Combine(root, "staging"),
            Platform = GamePlatform.Windows, WriteLauncher = false,
        };
        var withoutDlc = game with
        {
            Dlcs = [], Platforms = new() { [GamePlatform.Windows] = game.Platforms[GamePlatform.Windows] with { Dlcs = [] } },
        };
        var first = await downloader.RunAsync(withoutDlc, options);
        Probe.Check("an empty directory does not skip the base download", first.Outcome == DownloadOutcome.Completed);
        var missingDlc = await Cli("diff", "space_age", "--slugs", "--snapshot", snapshotPath, "--out", root);
        Probe.Check("diff requests the base when its archive lacks owned DLC", missingDlc.Output.Trim() == "factorio");

        // The old repository link is gone, but the refreshed build matches the existing base
        // archive. The second AlreadyDone guard must still notice that the DLC is absent.
        handler.OwnsDlc = true;
        var stale = game with { Platforms = new() { [GamePlatform.Windows] = game.Platforms[GamePlatform.Windows] with
        {
            Depot = new BuildRef("stale-build", 2, "0", "https://fixture.invalid/stale"),
        } } };
        var repaired = await downloader.RunAsync(stale, options);
        Probe.Check("a refreshed build with missing DLC is rebuilt", repaired.Outcome == DownloadOutcome.Completed);
        using (var zip = ZipFile.OpenRead(repaired.ArchivePath!))
        {
            Probe.Check("one archive contains the base game and its expansion",
                zip.GetEntry("game.exe") is not null && zip.GetEntry("expansion/data.bin") is not null);
            using var reader = new StreamReader(zip.GetEntry("expansion/data.bin")!.Open());
            Probe.Check("DLC content survives download and compression", await reader.ReadToEndAsync() == "expansion payload");
        }
        Probe.Check("the previous base file is reused", handler.BaseDownloads == 1);
        var backup = (await BackupManifest.LoadAsync(Path.Combine(root, game.DirectoryName)))!.Platforms[GamePlatform.Windows];
        Probe.Check("completion records the DLC and actual installed size",
            backup.Dlcs is ["2"] && backup.InstalledSize == "base payload".Length + "expansion payload".Length);
        var current = await Cli("diff", "space_age", "--slugs", "--snapshot", snapshotPath, "--out", root);
        Probe.Check("a complete base with DLC no longer appears in diff --slugs", current.Output.Trim().Length == 0);
        Probe.Check("a complete archive is skipped on the next run",
            (await downloader.RunAsync(game, options)).Outcome == DownloadOutcome.AlreadyDone);
        File.Delete(repaired.ArchivePath!);
        var gone = await Cli("diff", "space_age", "--slugs", "--snapshot", snapshotPath, "--out", root);
        Probe.Check("diff notices a missing archive despite a completion manifest", gone.Output.Trim() == "factorio");
    }

    private static async Task<(int Code, string Output, string Error)> Cli(params string[] args)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var code = await CommandLine.RunAsync(args, default);
            return (code, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    /// <summary>Real zlib manifests and chunks, served in memory without a GOG account.</summary>
    private sealed class ContentFixture : HttpMessageHandler
    {
        public bool OwnsDlc { get; set; }
        public int BaseDownloads { get; private set; }
        private readonly byte[] _base = Compress(Encoding.UTF8.GetBytes("base payload"));
        private readonly byte[] _dlc = Compress(Encoding.UTF8.GetBytes("expansion payload"));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpContent content;
            if (path == "/stale") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (path.EndsWith("/builds"))
                content = Json(new { items = new[] { new { build_id = "build", generation = 2, version_name = "1", repository_manifest = "https://fixture.invalid/repository" } } });
            else if (path == "/user/data/games")
                content = Json(new { owned = OwnsDlc ? new[] { 1, 2 } : new[] { 1 } });
            else if (path == "/repository")
                content = ZlibJson(new
                {
                    baseProductId = "1", products = new[] { new { productId = "1", name = "Base" }, new { productId = "2", name = "DLC" } },
                    depots = new[]
                    {
                        new { productId = "1", manifest = "aaaa", languages = new[] { "en-US" } },
                        new { productId = "2", manifest = "bbbb", languages = new[] { "en-US" } },
                    },
                });
            else if (path.Contains("/meta/"))
            {
                var isBase = path.EndsWith("aaaa");
                var data = isBase ? "base payload" : "expansion payload";
                var compressed = isBase ? _base : _dlc;
                content = ZlibJson(new { depot = new { items = new[] { new
                {
                    type = "DepotFile", path = isBase ? "game.exe" : "expansion/data.bin",
                    chunks = new[] { new { md5 = Hash(Encoding.UTF8.GetBytes(data)), compressedMd5 = Hash(compressed), size = data.Length, compressedSize = compressed.Length } },
                } } } });
            }
            else if (path.EndsWith("/secure_link"))
                content = Json(new { urls = new[] { new { url_format = "https://fixture.invalid{path}", parameters = new { path = "/chunks" } } } });
            else if (path.StartsWith("/chunks/"))
            {
                var isBase = path.EndsWith(Hash(_base));
                if (isBase) BaseDownloads++;
                content = new ByteArrayContent(isBase ? _base : _dlc);
            }
            else throw new InvalidOperationException($"Unexpected fixture request: {path}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        private static HttpContent Json(object value) => new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
        private static HttpContent ZlibJson(object value) => new ByteArrayContent(Compress(JsonSerializer.SerializeToUtf8Bytes(value)));
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
        private static byte[] Compress(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(bytes);
            return output.ToArray();
        }
    }
}
