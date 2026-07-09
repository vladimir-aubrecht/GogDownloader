using System.Diagnostics;
using System.IO.Compression;
using GogDownloader.Core;
using GogDownloader.Core.Api;
using GogDownloader.Core.Content;
using GogDownloader.Core.Download;
using GogDownloader.Core.Library;

namespace GogDownloader.Probes;

/// <summary>
/// The promises the downloader makes: it never half-finishes, it can be interrupted, and it
/// refuses to start rather than fail late.
/// </summary>
public static class DownloadProbes
{
    public static async Task RunAsync()
    {
        var root = Probe.ScratchDirectory();

        Probe.Group("Early exits take no network");
        // These clients point nowhere: reaching them at all would hang, which is the point.
        var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") };
        var downloader = new GameDownloader(
            new GogApiClient(http), new ContentClient(http, http, new RateLimiter(0)));

        LibraryEntry Game(BuildRef? depot) => new()
        {
            Id = 1207658930, Title = "The Game: Colon Edition", Slug = "the_game",
            Platforms = depot is null ? [] : new() { [GamePlatform.Windows] = new PlatformAvailability { Depot = depot } },
        };
        DownloadOptions Options() => new() { OutputRoot = root, Platform = GamePlatform.Windows };

        Probe.Check("directory is slug__id, never the title", Game(null).DirectoryName == "the_game__1207658930");

        var noDepot = await downloader.RunAsync(Game(null), Options());
        Probe.Check("no depot yields NoDepot", noDepot.Outcome == DownloadOutcome.NoDepot, noDepot.Outcome.ToString());

        var gen1 = await downloader.RunAsync(Game(new BuildRef("b1", 1, "1.0", "http://x/m")), Options());
        Probe.Check("generation 1 is reported, not attempted",
            gen1.Outcome == DownloadOutcome.UnsupportedGeneration, gen1.Outcome.ToString());

        Probe.Group("Idempotence");
        var game = Game(new BuildRef("build-42", 2, "1.2", "http://x/m"));
        var gameDirectory = Path.Combine(root, game.DirectoryName);
        Directory.CreateDirectory(gameDirectory);

        var manifest = new BackupManifest { GameId = game.Id, Slug = game.Slug, Title = game.Title };
        manifest.Platforms[GamePlatform.Windows] = new PlatformBackup
        {
            BuildId = "build-42", Language = "en", ArchiveName = "The Game (v1.2) (W_P).zip",
            ArchiveSize = 10, InstalledSize = 20,
            Files = { ["game/x.exe"] = new BackedUpFile("abc", 20) },
        };
        await manifest.SaveAsync(gameDirectory);

        var again = await downloader.RunAsync(game, Options());
        Probe.Check("a finished build is not downloaded twice",
            again.Outcome == DownloadOutcome.AlreadyDone, again.Outcome.ToString());

        // Reaching the network proves the guard was bypassed rather than silently honoured.
        Probe.Check("--force downloads anyway", await Throws(() => downloader.RunAsync(game, Options() with { Force = true })));
        Probe.Check("a new buildId downloads again",
            await Throws(() => downloader.RunAsync(Game(new BuildRef("build-43", 2, "1.3", "http://x/m")), Options())));

        Probe.Group("Backup manifest");
        var loaded = await BackupManifest.LoadAsync(gameDirectory);
        Probe.Check("round-trips the platform", loaded!.Platforms.ContainsKey(GamePlatform.Windows));
        Probe.Check("round-trips per-file hashes",
            loaded.Platforms[GamePlatform.Windows].Files["game/x.exe"].Md5 == "abc");
        Probe.Check("round-trips the buildId", loaded.Platforms[GamePlatform.Windows].BuildId == "build-42");
        Probe.Check("a missing manifest means not downloaded",
            await BackupManifest.LoadAsync(Path.Combine(root, "nothing")) is null);
        Probe.Check("saving leaves no .tmp behind",
            !File.Exists(BackupManifest.PathFor(gameDirectory) + ".tmp"));

        Probe.Group("Platform naming");
        Probe.Check("content-system calls macOS osx", GamePlatform.Mac.ContentSystemName() == "osx");
        Probe.Check("api.gog.com calls macOS mac", GamePlatform.Mac.ApiName() == "mac");

        await ZipAndPublishAsync(root);

        Probe.Group("Free space");
        Probe.Check("an impossible request is caught up front", GameDownloader.FreeSpaceShortfall(root, long.MaxValue / 2) is > 0);
        Probe.Check("a reasonable request passes", GameDownloader.FreeSpaceShortfall(root, 1024) is null);
        Probe.Check("an unreadable volume does not block the run", GameDownloader.FreeSpaceShortfall("\0::invalid", 1024) is null);

        Directory.Delete(root, recursive: true);
    }

    private static async Task ZipAndPublishAsync(string root)
    {
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(Path.Combine(staging, "game"));
        Directory.CreateDirectory(Path.Combine(staging, "empty"));
        Directory.CreateDirectory(Path.Combine(staging, "gog-support", "99"));
        File.WriteAllText(Path.Combine(staging, "gog-support", "99", "readme.txt"), "hi");

        // Poorly compressible, like a game's packed assets: compressing it takes long enough
        // that a cancellation observed only between files would be observed far too late.
        var big = Path.Combine(staging, "game", "data.pak");
        var random = new Random(1);
        await using (var file = File.Create(big))
        {
            var buffer = new byte[1 << 20];
            for (var i = 0; i < 400; i++) { random.NextBytes(buffer); file.Write(buffer); }
        }

        Probe.Group("Zipping is interruptible");
        var zipPath = Path.Combine(root, "out.zip");
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            var stopwatch = Stopwatch.StartNew();
            var cancelled = Throws(() => GameDownloader.ZipDirectory(staging, zipPath, null, cts.Token));
            stopwatch.Stop();

            Probe.Check("cancels inside a single large file", cancelled);
            Probe.Check($"cancels promptly ({stopwatch.Elapsed.TotalSeconds:F1} s, not after the file finishes)",
                stopwatch.Elapsed.TotalSeconds < 5, stopwatch.Elapsed.TotalSeconds.ToString("F1"));
            Probe.Check("leaves no partial zip", !File.Exists(zipPath));
        }

        Probe.Group("Zip contents");
        var reported = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(p => { lock (reported) reported.Add(p); });
        GameDownloader.ZipDirectory(staging, zipPath, progress, default);
        await Task.Delay(200); // Progress<T> delivers on the thread pool

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Probe.Check("entry paths use '/' even on Windows", names.All(n => !n.Contains('\\')));
            Probe.Check("the game file is present", names.Contains("game/data.pak"));
            Probe.Check("support files are kept aside", names.Contains("gog-support/99/readme.txt"));
            Probe.Check("an empty directory survives as a trailing-slash entry", names.Contains("empty/"));
            Probe.Check("non-empty directories get no entry of their own", !names.Contains("game/"));
            Probe.Check("the large file is intact", zip.GetEntry("game/data.pak")!.Length == 400L * 1024 * 1024);
        }
        Probe.Check("zipping reports progress per file", reported.Count(p => p.Phase == "zip") >= 3);

        Probe.Group("Publishing the archive");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);

        var sameVolumeSource = Path.Combine(root, "a.zip");
        File.Copy(zipPath, sameVolumeSource);
        var sameVolumeTarget = Path.Combine(target, "same.zip");
        GameDownloader.PublishArchive(sameVolumeSource, sameVolumeTarget, sameVolume: true, null);
        Probe.Check("same volume: the archive arrives", File.Exists(sameVolumeTarget));
        Probe.Check("same volume: the staging zip is gone", !File.Exists(sameVolumeSource));

        var crossSource = Path.Combine(root, "b.zip");
        File.Copy(zipPath, crossSource);
        var crossTarget = Path.Combine(target, "cross.zip");
        GameDownloader.PublishArchive(crossSource, crossTarget, sameVolume: false, null);
        Probe.Check("cross volume: the archive arrives", File.Exists(crossTarget));
        Probe.Check("cross volume: no .tmp is left", !File.Exists(crossTarget + ".tmp"));
        Probe.Check("cross volume: the copy is complete",
            new FileInfo(crossTarget).Length == new FileInfo(zipPath).Length);

        var cancelSource = Path.Combine(root, "c.zip");
        File.Copy(zipPath, cancelSource);
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            var cancelTarget = Path.Combine(target, "cancelled.zip");
            Probe.Check("cross volume: the copy is interruptible",
                Throws(() => GameDownloader.PublishArchive(cancelSource, cancelTarget, false, null, cts.Token)));
            Probe.Check("cross volume: a cancelled copy leaves no .tmp", !File.Exists(cancelTarget + ".tmp"));
            Probe.Check("cross volume: a cancelled copy creates no target", !File.Exists(cancelTarget));
            Probe.Check("cross volume: a cancelled copy keeps the source", File.Exists(cancelSource));
        }

        Probe.Check("paths under one root share a volume", GameDownloader.SameVolume(root, target));
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (OperationCanceledException) { return true; }
    }

    private static async Task<bool> Throws(Func<Task> action)
    {
        try { await action(); return false; }
        catch { return true; }
    }
}
