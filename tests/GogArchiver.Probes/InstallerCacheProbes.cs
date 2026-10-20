using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using GogArchiver.Core;
using GogArchiver.Core.Api;
using GogArchiver.Core.Download;
using GogArchiver.Core.Library;

namespace GogArchiver.Probes;

public static class InstallerCacheProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Installer caches belong to an exact source");
        var root = Probe.ScratchDirectory();
        try
        {
            using var fixture = new Fixture();
            using var http = new HttpClient(fixture);
            var extractor = new InnoSetupExtractor(new(http), http);
            var staging = Path.Combine(root, "game");
            var cache = staging + ".installer";
            var installer = CreateInstaller("1");
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "s.exe"), "OLD!");
            await extractor.DownloadAsync(installer, cache, null, default);
            Probe.Check("a legacy cache without an identity is downloaded again", fixture.Transfers == 3);
            var before = fixture.Transfers;
            await extractor.DownloadAsync(installer, cache, null, default);
            Probe.Check("completed parts of the same installer are reused", fixture.Transfers == before);

            fixture.Payload = "NEW!";
            installer = CreateInstaller("2");
            await extractor.DownloadAsync(installer, cache, null, default);
            Probe.Check("same-size parts from another version are replaced",
                fixture.Transfers == before + 3 && File.ReadAllText(Path.Combine(cache, "s.exe")) == "NEW!"
                && File.ReadAllText(Path.Combine(cache, "s-1.bin")) == "NEW!");

            foreach (var (field, changed) in new[]
            {
                ("language", installer with { Language = "de" }),
                ("installer id", installer with { Id = "another-installer" }),
                ("part ids", installer with { Files = installer.Files.Select(f => f with { Id = "new-" + f.Id }).ToList() }),
                ("part downlinks", installer with { Files = installer.Files.Select(f => f with { Downlink = f.Downlink + "?revision=2" }).ToList() }),
            })
            {
                await extractor.DownloadAsync(installer, cache, null, default);
                before = fixture.Transfers;
                await extractor.DownloadAsync(changed, cache, null, default);
                Probe.Check($"changed {field} invalidates equal-size parts", fixture.Transfers == before + 3);
            }
            installer = installer with { Files = installer.Files.Take(2).ToList(), TotalSize = 8 };
            await extractor.DownloadAsync(installer, cache, null, default);
            Probe.Check("parts removed by a new installer do not survive in its cache", !File.Exists(Path.Combine(cache, "s-2.bin")));

            File.WriteAllText(Path.Combine(cache, ".identity"), "interrupted marker");
            before = fixture.Transfers;
            await extractor.DownloadAsync(installer, cache, null, default);
            Probe.Check("an invalid identity cannot authorize cache reuse", fixture.Transfers == before + 2);

            StagingState.Discard(staging);
            Probe.Check("discarding staging also removes installer parts and their identity", !Directory.Exists(cache));
            using (var cancel = new CancellationTokenSource())
            {
                var progress = new SyncProgress<DownloadProgress>(p => { if (p.Files == 2) cancel.Cancel(); });
                try { await extractor.DownloadAsync(installer, cache, progress, cancel.Token); }
                catch (OperationCanceledException) { }
            }
            Probe.Check("cancellation keeps completed parts but never promotes a partial part",
                File.Exists(Path.Combine(cache, "s.exe")) && !File.Exists(Path.Combine(cache, "s-1.bin")));
            // The interrupted temporary file can even have the expected size; it is not complete.
            before = fixture.Transfers;
            await extractor.DownloadAsync(installer, cache, null, default);
            Probe.Check("resume fetches the interrupted part and reuses the completed part",
                fixture.Transfers == before + 1 && File.ReadAllText(Path.Combine(cache, "s-1.bin")) == "NEW!");

            StagingState.Discard(staging);
            fixture.TruncateNext = true;
            before = fixture.Transfers;
            await extractor.DownloadAsync(installer, cache, null, default);
            Probe.Check("a short HTTP body is retried before it can become a completed part",
                fixture.Transfers == before + 3 && File.ReadAllText(Path.Combine(cache, "s.exe")) == "NEW!"
                && !File.Exists(Path.Combine(cache, "s.exe.tmp")));

            if (!OperatingSystem.IsWindows()) await CheckArchiveAsync(root, fixture, http);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task CheckArchiveAsync(string root, Fixture fixture, HttpClient http)
    {
        // A small controlled extractor lets the entire downloader prove which installer bytes
        // reach the ZIP, without distributing a third-party Windows installer in the tests.
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        var executable = Path.Combine(bin, "innoextract");
        await File.WriteAllTextAsync(executable,
            "#!/bin/sh\nmkdir -p \"$6/app\"\ncat \"$7\" > \"$6/app/game.exe\"\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + oldPath);
        try
        {
            var game = new LibraryEntry
            {
                Id = 10, Slug = "cache", Title = "Cache",
                Platforms = new() { [GamePlatform.Windows] = new() { Installers = [new("en", "2", 12)] } },
            };
            var options = new DownloadOptions
            {
                OutputRoot = Path.Combine(root, "out"), StagingRoot = Path.Combine(root, "staging"),
                Platform = GamePlatform.Windows, Clean = true, MaxCompressionThreads = 2,
            };
            var stage = Path.Combine(options.StagingDirectory, game.LegacyDirectoryName + "__windows");
            Directory.CreateDirectory(stage);
            new StagingState("installer-2-12", "en", "windows").Save(stage);
            fixture.Payload = "OLD!";
            // Even a matching identity cannot override the user's --clean.
            await new InnoSetupExtractor(new(http), http).DownloadAsync(CreateInstaller("2"), stage + ".installer", null, default);
            fixture.Payload = "NEW!";
            var before = fixture.Transfers;
            var downloader = new GameDownloader(new(http), new(http, http, new(0)), http);
            var result = await downloader.RunAsync(game, options);
            using (var archive = ZipFile.OpenRead(result.ArchivePath!))
            using (var reader = new StreamReader(archive.GetEntry("game.exe")!.Open()))
                Probe.Check("--clean replaces the cached installer and the ZIP contains its new bytes",
                    result.Outcome == DownloadOutcome.Completed && fixture.Transfers == before + 3
                    && await reader.ReadToEndAsync() == "NEW!");

            var directory = Path.Combine(options.OutputRoot, game.DirectoryName);
            var manifest = (await BackupManifest.LoadAsync(directory))!;
            manifest.Platforms[GamePlatform.Windows] = manifest.Platforms[GamePlatform.Windows] with { ContentRevision = 0 };
            Probe.Check("old Linux installer backups do not require the Windows cache repair",
                !manifest.Platforms[GamePlatform.Windows].RequiresContentRefresh(GamePlatform.Linux));
            await manifest.SaveAsync(directory);
            result = await downloader.RunAsync(game, options with { Clean = false });
            Probe.Check("an older installer backup is refreshed once without --force", result.Outcome == DownloadOutcome.Completed);
            Probe.Check("a validated installer backup is skipped on subsequent runs",
                (await downloader.RunAsync(game, options with { Clean = false })).Outcome == DownloadOutcome.AlreadyDone);
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
    }

    private static Installer CreateInstaller(string version) => new()
    {
        Id = "setup", Os = "windows", Language = "en", Version = version, TotalSize = 12,
        Files = Enumerable.Range(0, 3).Select(i => new InstallerFile
        {
            Id = i.ToString(), Size = 4, Downlink = "https://fixture.invalid/link/" + i,
        }).ToList(),
    };

    private sealed class Fixture : HttpMessageHandler
    {
        public int Transfers { get; private set; }
        public string Payload { get; set; } = "OLD!";
        public bool TruncateNext { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpContent content;
            if (path.StartsWith("/products/"))
                content = Json(new Product { Id = 10, Downloads = new() { Installers = [CreateInstaller("2")] } });
            else if (path.StartsWith("/link/"))
            {
                var id = path[^1..];
                content = Json(new { downlink = "https://fixture.invalid/data/" + (id == "0" ? "setup.exe" : $"setup-{id}.bin") });
            }
            else if (path.StartsWith("/data/"))
            {
                Transfers++;
                content = new StringContent(TruncateNext ? Payload[..2] : Payload);
                TruncateNext = false;
            }
            else throw new InvalidOperationException("Unexpected fixture request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        private static HttpContent Json(object value) => new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }
}
