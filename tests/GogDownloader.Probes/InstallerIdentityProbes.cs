using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using GogDownloader.Core;
using GogDownloader.Core.Api;
using GogDownloader.Core.Download;
using GogDownloader.Core.Library;
using GogDownloader.Cli;

namespace GogDownloader.Probes;

public static class InstallerIdentityProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Installer identity follows the chosen language and live source");
        foreach (var platform in new[] { GamePlatform.Windows, GamePlatform.Linux })
        {
            if (platform == GamePlatform.Windows && OperatingSystem.IsWindows()) continue;
            var root = Probe.ScratchDirectory();
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                if (platform == GamePlatform.Windows)
                {
                    var bin = Path.Combine(root, "bin");
                    Directory.CreateDirectory(bin);
                    var tool = Path.Combine(bin, "innoextract");
                    await File.WriteAllTextAsync(tool, "#!/bin/sh\nmkdir -p \"$6/app\"\ncat \"$7\" > \"$6/app/game.exe\"\n");
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + oldPath);
                }
                using var fixture = new Fixture(platform);
                using var http = new HttpClient(fixture);
                var downloader = new GameDownloader(new(http), new(http, http, new(0)), http);
                var availability = new PlatformAvailability { Installers = [new("en", "1", fixture.Size), new("de", "2", fixture.Size)] };
                var game = new LibraryEntry
                {
                    Id = 1, Slug = "localized", Title = "Localized", Platforms = new() { [platform] = availability },
                };
                var options = new DownloadOptions
                {
                    OutputRoot = root, StagingRoot = Path.Combine(root, "staging"), Platform = platform,
                    Language = "de-DE", WriteLauncher = false, MaxCompressionThreads = 2,
                };
                Probe.Check($"{platform}: snapshot selection accepts language aliases", availability.InstallerFor("de-DE")?.Version == "2");
                var directory = Path.Combine(root, game.DirectoryName);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "old.zip"), "x");
                var manifest = new BackupManifest { GameId = 1, Slug = game.Slug, Title = game.Title };
                manifest.Platforms[platform] = new()
                {
                    ContentRevision = PlatformBackup.CurrentContentRevision, BuildId = new InstallerRef("de", "1", fixture.Size).BuildId,
                    VersionName = "1", Language = options.Language, ArchiveName = "old.zip", ArchiveSize = 1, InstalledSize = 1,
                };
                await manifest.SaveAsync(directory);
                var snapshot = Path.Combine(root, "library.json");
                Probe.Check($"{platform}: diff notices a localized installer update while English stays unchanged",
                    await DiffSlugsAsync(game, root, snapshot, platform) == game.Slug);

                var result = await downloader.RunAsync(game, options);
                Probe.Check($"{platform}: the localized update replaces the older archive", await ReadAsync(result) == "DEUTSCH");
                var backup = (await BackupManifest.LoadAsync(directory))!.Platforms[platform];
                Probe.Check($"{platform}: version, build identity and archive name describe the selected language",
                    backup.BuildId == $"installer-de-de-2-{fixture.Size}" && backup.VersionName == "2" && backup.ArchiveName.Contains("(v2)"));
                Probe.Check($"{platform}: diff considers the downloaded localized installer current",
                    await DiffSlugsAsync(game, root, snapshot, platform) == "");
                var englishUpdate = game with { Platforms = new() { [platform] = availability with
                    { Installers = [new("en", "99", fixture.Size), new("de", "2", fixture.Size)] } } };
                Probe.Check($"{platform}: diff ignores an update to an installer in another language",
                    await DiffSlugsAsync(englishUpdate, root, snapshot, platform) == "");
                if (platform == GamePlatform.Linux)
                {
                    using var archive = ZipFile.OpenRead(result.ArchivePath!);
                    Probe.Check("Linux: a stored empty installer entry reaches the published archive",
                        archive.GetEntry("empty.txt") is { Length: 0 });
                }
                var calls = fixture.ProductCalls;
                Probe.Check($"{platform}: the updated backup skips without a product request",
                    (await downloader.RunAsync(game, options)).Outcome == DownloadOutcome.AlreadyDone && fixture.ProductCalls == calls);

                fixture.GermanVersion = "3";
                var stale = game with { Platforms = new() { [platform] = availability with
                    { Installers = [new("en", "1", fixture.Size), new("de", "1", fixture.Size)] } } };
                result = await downloader.RunAsync(stale, options);
                backup = (await BackupManifest.LoadAsync(directory))!.Platforms[platform];
                Probe.Check($"{platform}: a stale snapshot records the live installer actually downloaded",
                    await ReadAsync(result) == "NEU!!!!" && backup.BuildId == $"installer-de-de-3-{fixture.Size}" && backup.VersionName == "3");
                var transfers = fixture.Transfers;
                Probe.Check($"{platform}: the live-version guard avoids repeating a download with a stale snapshot",
                    (await downloader.RunAsync(stale, options)).Outcome == DownloadOutcome.AlreadyDone && fixture.Transfers == transfers);
                result = await downloader.RunAsync(game, options with { Language = "fr" });
                backup = (await BackupManifest.LoadAsync(directory))!.Platforms[platform];
                Probe.Check($"{platform}: English fallback records English content and version",
                    await ReadAsync(result) == "ENGLISH" && backup.BuildId == $"installer-en-us-1-{fixture.Size}" && backup.VersionName == "1");
                fixture.FrenchAvailable = true;
                var french = game with { Platforms = new() { [platform] = availability with
                    { Installers = [new("en", "1", fixture.Size), new("de", "3", fixture.Size), new("fr", "1", fixture.Size)] } } };
                result = await downloader.RunAsync(french, options with { Language = "fr" });
                backup = (await BackupManifest.LoadAsync(directory))!.Platforms[platform];
                Probe.Check($"{platform}: a newly available language replaces an equal-size, equal-version fallback",
                    await ReadAsync(result) == "FRANCAI" && backup.BuildId == $"installer-fr-fr-1-{fixture.Size}");
                var unlistedLanguage = game with { Platforms = new() { [platform] = availability with
                    { Installers = [new("de", "2", fixture.Size), new("pl", "2", fixture.Size)] } } };
                Probe.Check($"{platform}: live language selection is allowed when the stale snapshot has no match",
                    (await downloader.RunAsync(unlistedLanguage, options with { Language = "fr" })).Outcome == DownloadOutcome.AlreadyDone);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", oldPath);
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task<string> DiffSlugsAsync(LibraryEntry game, string root, string snapshot, GamePlatform platform)
    {
        await new LibrarySnapshot { SchemaVersion = LibrarySnapshot.CurrentSchema, Games = [game] }.SaveAsync(snapshot);
        using var output = new StringWriter();
        var oldOut = Console.Out;
        try
        {
            Console.SetOut(output);
            await DiffCommands.RunAsync(["diff"], new() { ["slugs"] = "", ["out"] = root, ["snapshot"] = snapshot,
                ["platform"] = platform.ApiName() }, default);
        }
        finally { Console.SetOut(oldOut); }
        return output.ToString().Trim();
    }

    private static async Task<string> ReadAsync(DownloadResult result)
    {
        if (result.ArchivePath is null) return result.Outcome.ToString();
        using var zip = ZipFile.OpenRead(result.ArchivePath);
        using var reader = new StreamReader(zip.GetEntry("game.exe")!.Open());
        return await reader.ReadToEndAsync();
    }

    private sealed class Fixture(GamePlatform platform) : HttpMessageHandler
    {
        public string GermanVersion = "2";
        public bool FrenchAvailable;
        public int ProductCalls, Transfers;
        public long Size => Payload("ENGLISH").Length;
        private byte[] Payload(string text)
        {
            if (platform == GamePlatform.Windows) return Encoding.UTF8.GetBytes(text);
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("data/noarch/game.exe", CompressionLevel.NoCompression).Open()))
                    writer.Write(text);
                zip.CreateEntry("data/noarch/empty.txt", CompressionLevel.NoCompression);
            }
            return [.. Encoding.UTF8.GetBytes("#!/bin/sh\nexit 1\n"), .. output.ToArray()];
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpContent body;
            var status = HttpStatusCode.OK;
            if (path.StartsWith("/products/"))
            {
                ProductCalls++;
                body = Json(new Product { Downloads = new() { Installers = new[] { ("en", "1"), ("de", GermanVersion) }
                    .Concat(FrenchAvailable ? [("fr", "1")] : [])
                    .Select(i => new Installer { Id = i.Item1, Os = platform.ApiName(), Language = i.Item1, Version = i.Item2,
                        TotalSize = Size, Files = [new() { Id = i.Item1, Size = Size, Downlink = "https://fixture.invalid/link/" + i.Item1 }] }).ToList() } });
            }
            else if (path.StartsWith("/link/")) body = Json(new { downlink = "https://fixture.invalid/setup/" + path.Split('/')[2] + ".exe" });
            else if (path.StartsWith("/setup/"))
            {
                var payload = Payload(path.EndsWith("en.exe") ? "ENGLISH" : path.EndsWith("fr.exe") ? "FRANCAI"
                    : GermanVersion == "3" ? "NEU!!!!" : "DEUTSCH");
                if (request.Headers.Range?.Ranges.Single() is { } range)
                {
                    if (range.To - range.From + 1 == 7) Transfers++;
                    var from = (int)range.From!.Value;
                    var to = (int)Math.Min(range.To!.Value, payload.Length - 1);
                    body = new ByteArrayContent(payload[from..(to + 1)]);
                    body.Headers.ContentRange = new(from, to, payload.Length);
                    status = HttpStatusCode.PartialContent;
                }
                else { Transfers++; body = new ByteArrayContent(payload); }
            }
            else throw new InvalidOperationException("Unexpected fixture request: " + path);
            return Task.FromResult(new HttpResponseMessage(status) { Content = body });
        }
        private static HttpContent Json(object value) => new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }
}
