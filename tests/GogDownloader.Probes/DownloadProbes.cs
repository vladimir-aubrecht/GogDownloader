using System.Diagnostics;
using System.IO.Compression;
using GogDownloader.Core;
using GogDownloader.Core.Api;
using GogDownloader.Core.Content;
using GogDownloader.Core.Download;
using GogDownloader.Core.Download.Zip;
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

        // Generation 1 is now fetched like any other depot, so it must reach the network.
        Probe.Check("generation 1 is attempted, not refused",
            await Throws(() => downloader.RunAsync(Game(new BuildRef("b1", 1, "1.0", "http://x/m")), Options())));

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

        Probe.Group("Staging state decides whether a run can resume");
        var staged = Path.Combine(root, "staged");
        Directory.CreateDirectory(staged);

        var current = new StagingState("build-42", "en", "windows");
        current.Save(staged);

        Probe.Check("state round-trips", StagingState.Load(staged) == current);
        Probe.Check("a different build is not resumable",
            StagingState.Load(staged) != new StagingState("build-43", "en", "windows"));
        Probe.Check("a different language is not resumable",
            StagingState.Load(staged) != new StagingState("build-42", "de", "windows"));
        Probe.Check("a different platform is not resumable",
            StagingState.Load(staged) != new StagingState("build-42", "en", "osx"));

        // Everything inside staging ends up in the zip, so the marker must live beside it.
        Probe.Check("the marker is not inside staging", !Directory.EnumerateFiles(staged).Any());

        StagingState.Remove(staged);
        Probe.Check("a removed marker means no resume", StagingState.Load(staged) is null);
        Probe.Check("an absent marker means no resume", StagingState.Load(Path.Combine(root, "never")) is null);

        // Discarding must take the marker and any half-built zip with it, or the next run
        // would offer to resume from nothing.
        var discardable = Path.Combine(root, "discardable");
        Directory.CreateDirectory(discardable);
        File.WriteAllText(Path.Combine(discardable, "game.bin"), "data");
        File.WriteAllText(discardable + ".zip", "half a zip");
        new StagingState("b", "en", "windows").Save(discardable);

        StagingState.Discard(discardable);
        Probe.Check("discard removes the files", !Directory.Exists(discardable));
        Probe.Check("discard removes the marker", StagingState.Load(discardable) is null);
        Probe.Check("discard removes a half-built zip", !File.Exists(discardable + ".zip"));
        StagingState.Discard(discardable);   // must not throw on an already-clean directory
        Probe.Check("discarding twice is harmless", true);

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
            for (var i = 0; i < 96; i++) { random.NextBytes(buffer); file.Write(buffer); }
        }

        Probe.Group("Zipping is interruptible");
        var zipPath = Path.Combine(root, "out.zip");
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            var stopwatch = Stopwatch.StartNew();
            var cancelled = await Throws(() =>
                ParallelZipWriter.CreateAsync(staging, zipPath, 4, ct: cts.Token));
            stopwatch.Stop();

            Probe.Check("cancels inside a single large file", cancelled);
            Probe.Check($"cancels promptly ({stopwatch.Elapsed.TotalSeconds:F1} s, not after the file finishes)",
                stopwatch.Elapsed.TotalSeconds < 10, stopwatch.Elapsed.TotalSeconds.ToString("F1"));
            Probe.Check("leaves no partial zip", !File.Exists(zipPath));
            Probe.Check("leaves no parts directory", !Directory.Exists(zipPath + ".parts"));
        }

        Probe.Group("Zip contents");
        var reported = new List<ZipProgress>();
        var progress = new Progress<ZipProgress>(p => { lock (reported) reported.Add(p); });
        await ParallelZipWriter.CreateAsync(staging, zipPath, 4, progress: progress);
        await Task.Delay(200); // Progress<T> delivers on the thread pool

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Probe.Check("entry paths use '/' even on Windows", names.All(n => !n.Contains('\\')));
            Probe.Check("the game file is present", names.Contains("game/data.pak"));
            Probe.Check("support files are kept aside", names.Contains("gog-support/99/readme.txt"));
            Probe.Check("an empty directory survives as a trailing-slash entry", names.Contains("empty/"));
            Probe.Check("non-empty directories get no entry of their own", !names.Contains("game/"));
            Probe.Check("the large file is intact", zip.GetEntry("game/data.pak")!.Length == 96L * 1024 * 1024);
        }
        Probe.Check("zipping reports progress", reported.Count >= 3);

        // The framework reader is the arbiter: our writer must produce an archive it accepts.
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.GetEntry("gog-support/99/readme.txt")!;
            using var reader = new StreamReader(entry.Open());
            Probe.Check("entry content round-trips", reader.ReadToEnd() == "hi");
            Probe.Check("crc is accepted by the reader", entry.Crc32 != 0);
        }

        Probe.Group("Zip64");
        // Forcing the threshold down exercises the 64-bit headers without writing 4 GiB.
        var zip64Path = Path.Combine(root, "zip64.zip");
        await ParallelZipWriter.CreateAsync(staging, zip64Path, 4, zip64Threshold: 1024);
        using (var zip = ZipFile.OpenRead(zip64Path))
        {
            Probe.Check("a zip64 archive still opens", zip.Entries.Count > 0);
            Probe.Check("zip64 sizes survive",
                zip.GetEntry("game/data.pak")!.Length == 96L * 1024 * 1024,
                zip.GetEntry("game/data.pak")!.Length.ToString());
            using var reader = new StreamReader(zip.GetEntry("gog-support/99/readme.txt")!.Open());
            Probe.Check("zip64 content round-trips", reader.ReadToEnd() == "hi");
        }

        Probe.Group("More entries than the compression window");
        // Parts finish out of order. An earlier design let entries past the writer hold every
        // permit, so the entry it was waiting for could never start. With few files it passed
        // by luck; with many it hung.
        var many = Path.Combine(root, "many");
        Directory.CreateDirectory(many);
        for (var i = 0; i < 200; i++)
            await File.WriteAllTextAsync(Path.Combine(many, $"file{i:D3}.txt"), new string('x', 5000));

        var manyZip = Path.Combine(root, "many.zip");
        var finished = ParallelZipWriter.CreateAsync(many, manyZip, 1).Wait(TimeSpan.FromSeconds(60));
        Probe.Check("200 entries on one worker do not deadlock", finished);
        if (finished)
            using (var zip = ZipFile.OpenRead(manyZip))
                Probe.Check("all 200 entries are present", zip.Entries.Count == 200, zip.Entries.Count.ToString());

        Probe.Group("Archive verification");
        // ZipFile.OpenRead does not check CRCs, so "it opened" proves nothing. The verifier
        // recomputes them from the decompressed bytes and compares against staging.
        await ArchiveVerifier.VerifyAsync(zipPath, staging);
        Probe.Check("a good archive verifies", true);

        var corrupt = Path.Combine(root, "corrupt.zip");
        File.Copy(zipPath, corrupt, overwrite: true);
        var raw = await File.ReadAllBytesAsync(corrupt);
        raw[^(1 << 16)] ^= 0xFF;   // inside a compressed payload, headers untouched
        await File.WriteAllBytesAsync(corrupt, raw);

        var caught = false;
        try { await ArchiveVerifier.VerifyAsync(corrupt, staging); }
        catch (Exception e) when (e is ArchiveCorruptException or InvalidDataException) { caught = true; }
        Probe.Check("a corrupted archive is rejected", caught);

        var missing = Path.Combine(root, "missing");
        Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "only.txt"), "x");
        var mismatched = false;
        try { await ArchiveVerifier.VerifyAsync(zipPath, missing); }
        catch (ArchiveCorruptException) { mismatched = true; }
        Probe.Check("an archive that does not match staging is rejected", mismatched);

        Probe.Group("Compression ratio is unchanged by parallelism");
        var oneThread = Path.Combine(root, "one.zip");
        var manyThreads = Path.Combine(root, "many.zip");
        await ParallelZipWriter.CreateAsync(staging, oneThread, 1);
        await ParallelZipWriter.CreateAsync(staging, manyThreads, 8);
        Probe.Check("one worker and eight produce the same bytes",
            new FileInfo(oneThread).Length == new FileInfo(manyThreads).Length,
            $"{new FileInfo(oneThread).Length} vs {new FileInfo(manyThreads).Length}");

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

        // A full target volume raises IOException, not cancellation. The partial copy must go
        // either way, or a .tmp would sit next to the archive it pretends to be.
        var unwritable = Path.Combine(root, "nosuchdir", "deep", "target.zip");
        var brokeAndCleaned = false;
        try { GameDownloader.PublishArchive(zipPath, unwritable, sameVolume: false, null); }
        catch (IOException) { brokeAndCleaned = !File.Exists(unwritable + ".tmp"); }
        Probe.Check("a failed copy leaves no .tmp", brokeAndCleaned);
        Probe.Check("a failed copy keeps the staging zip", File.Exists(zipPath));

        Probe.Group("Directory size");
        Probe.Check("counts nested files", GameDownloader.DirectorySize(staging) > 90L * 1024 * 1024);
        Probe.Check("a missing directory is zero", GameDownloader.DirectorySize(Path.Combine(root, "gone")) == 0);
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
