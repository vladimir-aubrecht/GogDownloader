using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GogDownloader.Core;
using GogDownloader.Core.Download;
using GogDownloader.Core.Library;

namespace GogDownloader.Probes;

public static class LinkProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Directory and chained links become ordinary archive content");
        foreach (var reverse in new[] { false, true })
        {
            var root = Probe.ScratchDirectory();
            try
            {
                using var fixture = new Fixture("valid", reverse);
                using var http = new HttpClient(fixture);
                var downloader = new GameDownloader(new(http), new(http, http, new(0)), http);
                var (game, options) = Setup(root);
                var stage = Path.Combine(options.StagingDirectory, game.DirectoryName + "__osx");
                Directory.CreateDirectory(Path.Combine(stage, "src"));
                File.WriteAllText(Path.Combine(stage, "src/data.bin"), "DATA");
                Directory.CreateDirectory(Path.Combine(stage, "alias"));
                File.WriteAllText(Path.Combine(stage, "alias/obsolete.bin"), "old");
                new StagingState("build", "en", "osx").Save(stage);
                var result = await downloader.RunAsync(game, options);
                using var zip = ZipFile.OpenRead(result.ArchivePath!);
                var expected = new[] { "src/data.bin", "src/subalias", "alias/data.bin", "alias/subalias",
                    "dir-chain/data.bin", "dir-chain/subalias", "chain-a", "chain-b", "via-dir" };
                var correct = true;
                foreach (var name in expected)
                {
                    if (zip.GetEntry(name) is not { } entry) { correct = false; continue; }
                    using var reader = new StreamReader(entry.Open());
                    correct &= await reader.ReadToEndAsync() == "DATA";
                }
                Probe.Check($"link order {reverse}: every directory, chained and nested alias contains the target bytes", correct);
                Probe.Check($"link order {reverse}: empty target directories survive in every alias",
                    new[] { "src/empty/", "alias/empty/", "dir-chain/empty/" }.All(n => zip.GetEntry(n) is not null));
                Probe.Check($"link order {reverse}: obsolete alias content is removed on resume",
                    zip.GetEntry("alias/obsolete.bin") is null);
                var backup = (await BackupManifest.LoadAsync(Path.Combine(root, game.DirectoryName)))!.Platforms[GamePlatform.Mac];
                Probe.Check($"link order {reverse}: copied bytes count toward installed size",
                    backup.InstalledSize == expected.Length * 4 && fixture.Transfers == 0);
                Probe.Check($"link order {reverse}: the completed archive is idempotent",
                    (await downloader.RunAsync(game, options)).Outcome == DownloadOutcome.AlreadyDone);
            }
            finally { Directory.Delete(root, true); }
        }
        foreach (var mode in new[] { "cycle", "directory-cycle", "missing" })
        {
            var root = Probe.ScratchDirectory();
            try
            {
                using var fixture = new Fixture(mode, false);
                using var http = new HttpClient(fixture);
                var downloader = new GameDownloader(new(http), new(http, http, new(0)), http);
                var (game, options) = Setup(root);
                var rejected = false;
                try { await downloader.RunAsync(game, options); }
                catch (InvalidOperationException) { rejected = true; }
                Probe.Check($"{mode}: unresolved links fail before transferring or publishing content",
                    rejected && fixture.Transfers == 0
                    && !File.Exists(BackupManifest.PathFor(Path.Combine(root, game.DirectoryName))));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static (LibraryEntry, DownloadOptions) Setup(string root) => (new()
    {
        Id = 1, Slug = "links", Title = "Links",
        Platforms = new() { [GamePlatform.Mac] = new() { Depot = new("build", 2, "1", "https://fixture.invalid/repository") } },
    }, new()
    {
        OutputRoot = root, StagingRoot = Path.Combine(root, "staging"), Platform = GamePlatform.Mac,
        WriteLauncher = false, MaxCompressionThreads = 2,
    });

    private sealed class Fixture(string mode, bool reverse) : HttpMessageHandler
    {
        public int Transfers;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object data;
            if (path == "/user/data/games") data = new { owned = new[] { 1 } };
            else if (path == "/repository") data = new
            {
                baseProductId = "1", products = new[] { new { productId = "1" } },
                depots = new[] { new { productId = "1", manifest = "aaaa", languages = new[] { "*" } } },
            };
            else if (path.Contains("/meta/"))
            {
                var links = mode switch
                {
                    "cycle" => new[] { ("a", "b"), ("b", "a") },
                    "directory-cycle" => new[] { ("src/loop", "src") },
                    "missing" => new[] { ("a", "not-in-manifest") },
                    _ => new[] { ("alias", "src"), ("dir-chain", "alias"), ("chain-b", "chain-a"),
                        ("chain-a", "src/data.bin"), ("via-dir", "alias/data.bin"), ("src/subalias", "src/data.bin") },
                };
                if (reverse) Array.Reverse(links);
                data = new { depot = new { items = new object[]
                {
                    new { type = "DepotDirectory", path = "src/empty" },
                    new { type = "DepotFile", path = "src/data.bin", chunks = new[] { new
                    { md5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("DATA"))), size = 4, compressedSize = 12, compressedMd5 = "unused" } } },
                }.Concat(links.Select(link => (object)new { type = "DepotLink", path = link.Item1, target = link.Item2 })) } };
            }
            else { Transfers++; throw new InvalidOperationException("Unexpected transfer: " + path); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") });
        }
    }
}
