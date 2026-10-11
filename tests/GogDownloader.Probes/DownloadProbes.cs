using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
        var completedArchive = Path.Combine(gameDirectory, manifest.Platforms[GamePlatform.Windows].ArchiveName);
        await File.WriteAllBytesAsync(completedArchive, new byte[10]);

        var again = await downloader.RunAsync(game, Options());
        Probe.Check("a finished build is not downloaded twice",
            again.Outcome == DownloadOutcome.AlreadyDone, again.Outcome.ToString());

        // Reaching the network proves the guard was bypassed rather than silently honoured.
        Probe.Check("--force downloads anyway", await Throws(() => downloader.RunAsync(game, Options() with { Force = true })));
        Probe.Check("a new buildId downloads again",
            await Throws(() => downloader.RunAsync(Game(new BuildRef("build-43", 2, "1.3", "http://x/m")), Options())));
        Probe.Check("newly owned DLC is not skipped at the same build",
            await Throws(() => downloader.RunAsync(game with { Dlcs = [new DlcRef("99", "Expansion")] }, Options())));
        Probe.Check("a different language is not skipped at the same build",
            await Throws(() => downloader.RunAsync(game, Options() with { Language = "de" })));
        await File.WriteAllBytesAsync(completedArchive, new byte[5]);
        Probe.Check("a truncated archive is downloaded again",
            await Throws(() => downloader.RunAsync(game, Options())));
        File.Delete(completedArchive);
        Probe.Check("a manifest without its archive is downloaded again",
            await Throws(() => downloader.RunAsync(game, Options())));

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

        await ArchiveCacheAsync(root);

        Probe.Group("Transient network errors are retried");
        var part = new PlannedPart(0, 0, 3, null, 3);
        var file = new PlannedFile("game/x.bin", "1", 3, null, [part]);

        // A connection dropped mid-body ("response ended prematurely") is an IOException that
        // surfaces after the retry handler has returned, so the part-level retry must catch it.
        var flaky = new FlakySource(failures: 2, () => new IOException("response ended prematurely"));
        var data = await flaky.FetchAsync(file, part, null, default);
        Probe.Check("a part survives transient drops", data.Length == 3);
        Probe.Check("it retried until it succeeded", flaky.Calls == 3, flaky.Calls.ToString());

        var reset = new FlakySource(failures: 1, () => new HttpRequestException("connection reset"));
        Probe.Check("a connection reset is retried too", (await reset.FetchAsync(file, part, null, default)).Length == 3);

        // An expired signed URL can present as a stall, so a transient failure refreshes the link.
        var stalling = new FlakySource(failures: 2, () => new IOException("stall"));
        await stalling.FetchAsync(file, part, null, default);
        Probe.Check("a transient failure refreshes the secure link", stalling.Refreshes == 2, stalling.Refreshes.ToString());
        Probe.Check("the retry bypasses the CDN cache", stalling.LastFreshFromOrigin);
        var firstTry = new FlakySource(failures: 0, () => new IOException("x"));
        await firstTry.FetchAsync(file, part, null, default);
        Probe.Check("the first try is served from cache", !firstTry.LastFreshFromOrigin);

        // The 100.4% bug: a partial read before a failure must not be counted twice by the retry.
        var bigPart = new PlannedPart(0, 0, 1000, null, 1000);
        var bigFile = new PlannedFile("game/big.bin", "1", 1000, null, [bigPart]);
        long counted = 0;
        var counter = new SyncProgress<long>(n => counted += n);
        var partialThenOk = new FlakySource(failures: 3, () => new IOException("drop"), partialBytes: 700);
        await partialThenOk.FetchAsync(bigFile, bigPart, counter, default);
        Probe.Check($"retried bytes are not double-counted (got {counted}, want 1000)", counted == 1000, counted.ToString());

        // A real bug must not be mistaken for a network blip and retried forever.
        var bug = new FlakySource(failures: 99, () => new InvalidOperationException("bug"));
        var propagated = false;
        try { await bug.FetchAsync(file, part, null, default); }
        catch (InvalidOperationException) { propagated = true; }
        Probe.Check("a non-network error propagates on the first try", propagated && bug.Calls == 1);

        // GOG signs several CDN endpoints; an object broken on one may be intact on another, so
        // a retry must move to the next endpoint rather than hammer the broken one.
        var mirrored = new MirroredDepot(brokenEndpoints: 1);
        var fromMirror = await mirrored.FetchAsync(file, part, null, default);
        Probe.Check("a retry fails over to the next CDN endpoint",
            fromMirror.Length == 3 && mirrored.ServedBy == 1, $"served by {mirrored.ServedBy}");
        var allEndpoints = new MirroredDepot(brokenEndpoints: 99);
        Probe.Check("every endpoint is tried before giving up",
            await Throws(() => allEndpoints.FetchAsync(file, part, null, default))
            && allEndpoints.TriedEndpoints.SetEquals([0, 1, 2]),
            string.Join(",", allEndpoints.TriedEndpoints));

        // Ctrl+C must stop the retries, not spin on them.
        using (var cts = new CancellationTokenSource())
        {
            await cts.CancelAsync();
            var stopped = false;
            var cancelled = new FlakySource(failures: 99, () => new IOException("drop"));
            try { await cancelled.FetchAsync(file, part, null, cts.Token); }
            catch (Exception e) when (e is IOException or OperationCanceledException) { stopped = true; }
            Probe.Check("a cancelled fetch stops instead of retrying", stopped && cancelled.Calls <= 1);
        }

        Probe.Group("Free space");
        Probe.Check("an impossible request is caught up front", GameDownloader.FreeSpaceShortfall(root, long.MaxValue / 2) is > 0);
        Probe.Check("a reasonable request passes", GameDownloader.FreeSpaceShortfall(root, 1024) is null);
        Probe.Check("an unreadable volume does not block the run", GameDownloader.FreeSpaceShortfall("\0::invalid", 1024) is null);

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// The old backup is a cache for the next one. It must save real downloads, and it must never
    /// let stale bytes through: the manifest is a claim, the hash is the proof.
    /// </summary>
    private static async Task ArchiveCacheAsync(string root)
    {
        Probe.Group("Reusing the previous backup");

        var source = Path.Combine(root, "cache-src");
        Directory.CreateDirectory(Path.Combine(source, "game"));

        var unchangedPath = Path.Combine(source, "game", "unchanged.bin");
        await File.WriteAllTextAsync(unchangedPath, new string('u', 50_000));
        await File.WriteAllTextAsync(Path.Combine(source, "game", "changed.bin"), new string('c', 10_000));

        var archive = Path.Combine(root, "previous.zip");
        await ParallelZipWriter.CreateAsync(source, archive, 2);

        string Md5Of(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(MD5.HashData(stream));
        }

        var unchangedHash = Md5Of(unchangedPath);
        var files = new Dictionary<string, BackedUpFile>
        {
            ["game/unchanged.bin"] = new(unchangedHash, 50_000),
            ["game/changed.bin"] = new(Md5Of(Path.Combine(source, "game", "changed.bin")), 10_000),
        };

        using (var cache = ArchiveCache.Open(archive, files))
        {
            Probe.Check("the previous archive opens as a cache", cache is not null);

            var destination = Path.Combine(root, "restored.bin");
            Probe.Check("an unchanged file is lifted out of it",
                await cache!.TryRestoreAsync(unchangedHash, destination, 50_000, default));
            Probe.Check("and it arrives byte for byte", Md5Of(destination) == unchangedHash);

            // Content-addressed: a file that merely moved is still a hit.
            Probe.Check("a hash that is not in the archive is a miss",
                !await cache.TryRestoreAsync(new string('0', 32), destination, 50_000, default));
            Probe.Check("a size mismatch is a miss",
                !await cache.TryRestoreAsync(unchangedHash, destination, 49_999, default));
        }

        Probe.Check("no cache without a previous archive",
            ArchiveCache.Open(Path.Combine(root, "absent.zip"), files) is null);
        Probe.Check("no cache without a manifest", ArchiveCache.Open(archive, new Dictionary<string, BackedUpFile>()) is null);

        // The manifest claims a hash the archive does not actually contain. Trusting it would
        // copy the wrong file into a new backup and record it as correct.
        var lying = new Dictionary<string, BackedUpFile> { ["game/changed.bin"] = new(unchangedHash, 50_000) };
        using (var cache = ArchiveCache.Open(archive, lying))
        {
            var destination = Path.Combine(root, "lied.bin");
            Probe.Check("a file whose content contradicts the manifest is refused",
                !await cache!.TryRestoreAsync(unchangedHash, destination, 50_000, default));
            Probe.Check("and the bad copy is not left behind", !File.Exists(destination));
        }

        var corrupt = Path.Combine(root, "corrupt.zip");
        await File.WriteAllTextAsync(corrupt, "this is not a zip");
        Probe.Check("a corrupt previous archive is a miss, not a crash",
            ArchiveCache.Open(corrupt, files) is null);
    }

    /// <summary>Where an entry's local header starts, by name.</summary>
    private static int IndexOfLocalHeader(byte[] bytes, string entryName)
    {
        var name = Encoding.UTF8.GetBytes(entryName);

        for (var i = 0; i + 30 + name.Length <= bytes.Length; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x04034b50
                && bytes.AsSpan(i + 30, name.Length).SequenceEqual(name))
                return i;

        throw new InvalidOperationException($"No local header for {entryName}.");
    }

    /// <summary>Whether .NET reads an entry without raising — it does not check much.</summary>
    private static async Task<bool> Inflates(string zipPath, string entryName)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            await using var stream = zip.GetEntry(entryName)!.Open();
            await stream.CopyToAsync(Stream.Null);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Rewrites one entry's compression method to deflate in both headers, reproducing the
    /// archives this tool wrote for empty files before 0.39.0.
    /// </summary>
    private static void MarkAsDeflated(string zipPath, string entryName)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var name = Encoding.UTF8.GetBytes(entryName);

        for (var i = 0; i + 46 <= bytes.Length; i++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));

            // The method sits at +8 in a local header and at +10 in a central one; the name
            // follows the fixed part, whose length differs between the two.
            var (methodAt, nameAt) = signature switch
            {
                0x04034b50 => (i + 8, i + 30),
                0x02014b50 => (i + 10, i + 46),
                _ => (-1, -1),
            };

            if (methodAt < 0 || nameAt + name.Length > bytes.Length) continue;
            if (!bytes.AsSpan(nameAt, name.Length).SequenceEqual(name)) continue;

            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(methodAt), 8);
        }

        File.WriteAllBytes(zipPath, bytes);
    }

    /// <summary>
    /// Compression method and compressed size per entry, read out of the local headers.
    /// `ZipArchiveEntry` does not expose the method, and the method is exactly what was wrong.
    /// </summary>
    private static Dictionary<string, (ushort Method, uint CompressedSize)> LocalHeaders(string zipPath)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var headers = new Dictionary<string, (ushort, uint)>(StringComparer.Ordinal);

        for (var i = 0; i + 30 <= bytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) != 0x04034b50) continue;

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 26));
            if (i + 30 + nameLength > bytes.Length) continue;

            headers[Encoding.UTF8.GetString(bytes, i + 30, nameLength)] = (
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 18)));
        }

        return headers;
    }

    private static async Task ZipAndPublishAsync(string root)
    {
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(Path.Combine(staging, "game"));
        Directory.CreateDirectory(Path.Combine(staging, "empty"));
        Directory.CreateDirectory(Path.Combine(staging, "gog-support", "99"));
        File.WriteAllText(Path.Combine(staging, "gog-support", "99", "readme.txt"), "hi");

        // Depots ship empty files (Duke Nukem 3D's CDROM.INI), and one of those is what made a
        // finished archive unreadable to everything except .NET.
        File.WriteAllText(Path.Combine(staging, "game", "CDROM.INI"), "");

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
            Probe.Check("an empty file is an entry of its own", names.Contains("game/CDROM.INI"));
        }

        // A DeflateStream that is never written to emits nothing, so an empty file used to be
        // recorded as deflated with a zero-byte payload — which is not a deflate stream at all.
        // .NET read it back as empty and the verifier saw a matching CRC, while Info-ZIP said
        // "invalid compressed data to inflate" and Windows Explorer refused the archive.
        Probe.Group("Empty entries are stored, never deflated");
        var headers = LocalHeaders(zipPath);
        Probe.Check("an empty file is stored",
            headers["game/CDROM.INI"].Method == 0, headers["game/CDROM.INI"].Method.ToString());
        Probe.Check("an empty directory is stored", headers["empty/"].Method == 0);
        Probe.Check("a real file is still deflated", headers["game/data.pak"].Method == 8);

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var deflatedAndEmpty = zip.Entries
                .Where(e => headers.TryGetValue(e.FullName, out var h) && h is { Method: 8, CompressedSize: 0 })
                .Select(e => e.FullName)
                .ToList();

            Probe.Check("no entry claims deflate over an empty payload",
                deflatedAndEmpty.Count == 0, string.Join(", ", deflatedAndEmpty));
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

        // `verify` checks archives whose staging is long gone, so it reads the headers the
        // framework hides and, on demand, inflates everything.
        Probe.Group("Checking an archive on its own");
        Probe.Check("a sound archive has nothing to report", ArchiveCheck.Headers(zipPath).Count == 0);
        Probe.Check("a zip64 archive reads too",
            ZipInspector.ReadCentralDirectory(zip64Path).Any(e => e.Name == "game/data.pak" && e.Size == 96L * 1024 * 1024));
        Probe.Check("the central directory holds every entry",
            ZipInspector.ReadCentralDirectory(manyZip).Count == 200);

        // The archives written before 0.39.0: the empty entry says deflate and carries nothing.
        // Flipping the method back is the only way to reproduce them now.
        var legacy = Path.Combine(root, "legacy-empty-deflate.zip");
        File.Copy(zipPath, legacy, overwrite: true);
        MarkAsDeflated(legacy, "game/CDROM.INI");

        var found = ArchiveCheck.Headers(legacy);
        Probe.Check("an empty deflated entry is caught",
            found is [{ Entry: "game/CDROM.INI" }], string.Join(", ", found.Select(f => f.Entry)));
        Probe.Check("and .NET still reads it without complaint, which is why the check exists",
            await Inflates(legacy, "game/CDROM.INI"));

        Probe.Check("and it is marked as repairable", found is [{ Repairable: true }]);

        // Four bytes per entry, no payload moves: a repaired archive has to come out byte for
        // byte the same as one the fixed writer would have produced.
        var beforeRepair = await File.ReadAllBytesAsync(legacy);
        var mended = ArchiveRepair.Repair(legacy);
        Probe.Check("repairing names the entry it mended", mended is ["game/CDROM.INI"]);
        Probe.Check("a repaired archive is clean", ArchiveCheck.Headers(legacy).Count == 0);
        var afterRepair = await File.ReadAllBytesAsync(legacy);
        var written = await File.ReadAllBytesAsync(zipPath);
        Probe.Check("a repaired archive equals a correctly written one", afterRepair.SequenceEqual(written));
        Probe.Check("only the method changed",
            beforeRepair.Length == new FileInfo(legacy).Length);
        Probe.Check("a sound archive has nothing to repair", ArchiveRepair.Repair(zipPath).Count == 0);
        Probe.Check("the repaired entries still read", await Inflates(legacy, "game/CDROM.INI"));
        Probe.Check("and so does everything else", (await ArchiveCheck.DeepAsync(legacy)).Count == 0);

        // The central directory is what the repair trusts; if the local header disagrees, this
        // is not the defect it looks like and the four bytes must not be written.
        var mismatch = Path.Combine(root, "mismatched-headers.zip");
        File.Copy(zipPath, mismatch, overwrite: true);
        MarkAsDeflated(mismatch, "game/CDROM.INI");
        var raw2 = await File.ReadAllBytesAsync(mismatch);
        var localAt = IndexOfLocalHeader(raw2, "game/CDROM.INI");
        BinaryPrimitives.WriteUInt32LittleEndian(raw2.AsSpan(localAt + 18), 7);  // a payload that is not there
        await File.WriteAllBytesAsync(mismatch, raw2);

        var refused = false;
        try { ArchiveRepair.Repair(mismatch); } catch (ArchiveRepairException) { refused = true; }
        Probe.Check("a repair that does not add up is refused", refused);

        var deep = await ArchiveCheck.DeepAsync(corrupt);
        Probe.Check("a deep check catches a flipped byte", deep.Count > 0,
            string.Join(", ", deep.Select(d => d.Entry)));
        Probe.Check("a deep check passes a sound archive", (await ArchiveCheck.DeepAsync(zipPath)).Count == 0);

        var notAZip = Path.Combine(root, "not-a-zip.bin");
        await File.WriteAllTextAsync(notAZip, "PK, but no");
        var rejected = false;
        try { ArchiveCheck.Headers(notAZip); } catch (ZipFormatException) { rejected = true; }
        Probe.Check("a file that is not a ZIP is reported, not thrown at the user", rejected);

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

    /// <summary>A content source whose fetch fails a set number of times before it succeeds.</summary>
    private sealed class FlakySource(int failures, Func<Exception> error, long partialBytes = 0) : ContentSource
    {
        public int Calls { get; private set; }
        public int Refreshes { get; private set; }

        public override Task<DepotPlan> PlanAsync(CancellationToken ct) => throw new NotSupportedException();

        protected override Task RefreshAsync(PlannedFile file, CancellationToken ct)
        {
            Refreshes++;
            return Task.CompletedTask;
        }

        public bool LastFreshFromOrigin { get; private set; }

        protected override Task<byte[]> FetchOnceAsync(
            PlannedFile file, PlannedPart part, IProgress<long>? bytes, int attempt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastFreshFromOrigin = attempt > 0;
            Calls++;
            if (Calls <= failures)
            {
                // A real transfer reports the bytes it read before the connection broke.
                bytes?.Report(partialBytes);
                throw error();
            }
            // The successful attempt reports the part's full download size, once.
            bytes?.Report(part.DownloadSize);
            return Task.FromResult(new byte[] { 1, 2, 3 });
        }
    }

    /// <summary>A depot with three CDN endpoints, the first N of which serve nothing but errors.</summary>
    private sealed class MirroredDepot(int brokenEndpoints) : DepotSource(
        new ContentClient(new HttpClient(), new HttpClient(), new RateLimiter(0)))
    {
        public int ServedBy { get; private set; } = -1;
        public HashSet<int> TriedEndpoints { get; } = [];

        public override Task<DepotPlan> PlanAsync(CancellationToken ct) => throw new NotSupportedException();

        protected override Task<IReadOnlyList<SecureLinkEndpoint>> RequestLinksAsync(string productId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SecureLinkEndpoint>>(
                [new SecureLinkEndpoint { Priority = 0 }, new SecureLinkEndpoint { Priority = 1 }, new SecureLinkEndpoint { Priority = 2 }]);

        protected override Task<byte[]> FetchPartAsync(
            SecureLinkEndpoint endpoint, PlannedPart part, IProgress<long>? bytes, bool freshFromOrigin, CancellationToken ct)
        {
            TriedEndpoints.Add(endpoint.Priority);
            if (endpoint.Priority < brokenEndpoints)
                throw new IOException("response ended prematurely");
            ServedBy = endpoint.Priority;
            return Task.FromResult(new byte[] { 1, 2, 3 });
        }
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
