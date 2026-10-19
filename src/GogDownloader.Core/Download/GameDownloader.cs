using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using GogDownloader.Core.Api;
using GogDownloader.Core.Content;
using GogDownloader.Core.Download.Zip;
using GogDownloader.Core.Library;

namespace GogDownloader.Core.Download;

public sealed record DownloadOptions
{
    public required string OutputRoot { get; init; }
    public required GamePlatform Platform { get; init; }
    public string Language { get; init; } = "en";
    public int MaxConnections { get; init; } = 8;

    /// <summary>
    /// Where the game is assembled and zipped. Never <see cref="OutputRoot"/>: staging on a
    /// network share would push every byte across the wire three times (write files, read them
    /// back to zip, write the zip). Needs room for the unpacked game plus the zip.
    /// </summary>
    public string? StagingRoot { get; init; }

    /// <summary>Re-download even what the manifest already reports as done.</summary>
    public bool Force { get; init; }

    /// <summary>Discard a staging directory left by an interrupted run instead of resuming it.</summary>
    public bool Clean { get; init; }

    /// <summary>
    /// Lift unchanged files out of the previous backup rather than downloading them again. Every
    /// reused file is hashed, so this cannot smuggle stale content into a new archive.
    /// </summary>
    public bool Reuse { get; init; } = true;

    /// <summary>
    /// Write a launcher into the archive root from the game's own launch metadata, so every game
    /// starts the same way. Nothing is guessed; a game without metadata gets no launcher.
    /// </summary>
    public bool WriteLauncher { get; init; } = true;

    /// <summary>
    /// Read the finished archive back and compare it against staging. Costs one extra pass over
    /// the data; the ZIP writer is hand-rolled, so this is on by default.
    /// </summary>
    public bool VerifyArchive { get; init; } = true;

    /// <summary>
    /// Deflate is single-threaded per entry, and ZIP compresses entries independently, so this
    /// is what decides how long zipping takes. Defaults to every core.
    /// </summary>
    public int MaxCompressionThreads { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Local app data, not <see cref="Path.GetTempPath"/>: on Linux temp is often a tmpfs
    /// living in RAM, and a large game would exhaust it.
    /// </summary>
    public string StagingDirectory => StagingRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "gogdownloader", "staging");
}

public enum DownloadOutcome
{
    Completed, AlreadyDone, NoDepot, UnsupportedGeneration, NoDepotForLanguage, NotEnoughSpace, Incomplete,
}

public sealed record DownloadResult(DownloadOutcome Outcome, string? ArchivePath = null, string? Detail = null);

public sealed record DownloadProgress(string Phase, long Bytes, long TotalBytes, int Files, int TotalFiles);

/// <summary>
/// Path A: assembles the game from Galaxy depots in a staging directory, zips it and
/// atomically moves the zip into place. The manifest is written last.
/// </summary>
public sealed class GameDownloader(GogApiClient api, ContentClient content, HttpClient? cdn = null)
{
    private readonly HttpClient cdn = cdn ?? new HttpClient();

    public async Task<DownloadResult> RunAsync(
        LibraryEntry game,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (game.Platforms.GetValueOrDefault(options.Platform) is not { } availability)
            return new DownloadResult(DownloadOutcome.NoDepot,
                Detail: $"The library has nothing for {options.Platform.ApiName()}.");

        // Linux has no Galaxy depots, so its build identity comes from the chosen installer.
        var build = availability.Depot;
        var fromInstaller = build is null && availability.HasInstaller;

        if (build is null && !availability.HasInstaller)
            return new DownloadResult(DownloadOutcome.NoDepot,
                Detail: $"Neither a depot nor an installer for {options.Platform.ApiName()}.");

        // macOS installers are .pkg inside .dmg, which innoextract does not read.
        if (fromInstaller && options.Platform == GamePlatform.Mac)
            return new DownloadResult(DownloadOutcome.NoDepot,
                Detail: "macOS titles without a depot are not supported: their installers are not Inno Setup.");

        var innoSetup = fromInstaller && options.Platform == GamePlatform.Windows;
        if (innoSetup && !InnoSetupExtractor.IsAvailable)
            return new DownloadResult(DownloadOutcome.NoDepot,
                Detail: new InnoExtractMissingException().Message);

        var selectedInstaller = fromInstaller ? availability.InstallerFor(options.Language) : null;
        // A snapshot without a matching language cannot authorize a skip; live product data
        // below may already contain that language (or a new English fallback).
        var buildId = build?.BuildId ?? selectedInstaller?.BuildId ?? "";
        var versionName = build?.VersionName ?? (selectedInstaller ?? availability.InstallerFor(options.Language))?.Version;

        var gameDirectory = Path.Combine(options.OutputRoot, game.DirectoryName);
        var archiveName = GameVaultName.For(game, options.Platform, versionName);

        var existing = await BackupManifest.LoadAsync(gameDirectory, ct);
        if (!options.Force && buildId.Length > 0
            && existing?.Platforms.GetValueOrDefault(options.Platform) is { } done
            && done.Matches(gameDirectory, buildId, options.Language,
                game.DlcsFor(options.Platform).Select(d => d.ProductId), options.Platform))
        {
            return new DownloadResult(DownloadOutcome.AlreadyDone,
                Path.Combine(gameDirectory, done.ArchiveName),
                $"Build {buildId} is already backed up.");
        }

        progress?.Report(new DownloadProgress("manifest", 0, 0, 0, 0));

        // Everything the content sources disagree about lives behind this. Inno Setup is the
        // exception: it cannot be planned file by file, only downloaded and extracted whole.
        ContentSource? source = null;
        DepotPlan? plan = null;
        Installer? setup = null;

        if (innoSetup)
        {
            var product = await api.GetProductAsync(game.Id, ct);
            setup = InnoSetupExtractor.Select(product?.Downloads?.Installers ?? [], options.Platform, options.Language);
            if (setup is null)
                return new DownloadResult(DownloadOutcome.NoDepotForLanguage,
                    Detail: $"No Windows installer for language '{options.Language}'.");
        }
        else
        {
            source = build switch
            {
                null => new InstallerSource(api, cdn, game.Id, options.Language, content.Limiter),
                { Generation: 1 } => new DepotSourceV1(content, api, build.Manifest!, options.Platform.ContentSystemName(), options.Language),
                _ => new DepotSourceV2(content, api, build.Manifest!, options.Language),
            };

            try
            {
                plan = await source.PlanAsync(ct);
            }
            catch (Exception e) when (e is NoDepotForLanguageException or NoLinuxInstallerException)
            {
                return new DownloadResult(DownloadOutcome.NoDepotForLanguage, Detail: e.Message);
            }
            catch (HttpRequestException e) when (build is not null
                && e.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            {
                // The snapshot's depot link went stale: GOG republished the build and the old
                // manifest URL now 403s or 404s. Ask for the current build and carry on instead
                // of failing a game whose depot is perfectly alive.
                var fresh = await api.GetLatestBuildAsync(game.Id, options.Platform, ct)
                    ?? throw new InvalidOperationException(
                        $"The snapshot's depot link is stale ({e.Message}) and GOG lists no " +
                        $"{options.Platform.ApiName()} build anymore. Refresh with: library sync");

                ContentSource.RetryObserver?.Invoke(
                    $"{game.Slug}: snapshot depot link is stale, switching to current build {fresh.BuildId}");

                if (!options.Force
                    && existing?.Platforms.GetValueOrDefault(options.Platform) is { } current
                    && current.Matches(gameDirectory, fresh.BuildId, options.Language,
                        game.DlcsFor(options.Platform).Select(d => d.ProductId), options.Platform))
                {
                    return new DownloadResult(DownloadOutcome.AlreadyDone,
                        Path.Combine(gameDirectory, current.ArchiveName),
                        $"Build {fresh.BuildId} is already backed up.");
                }

                buildId = fresh.BuildId;
                versionName = fresh.VersionName ?? versionName;
                archiveName = GameVaultName.For(game, options.Platform, versionName);

                source = fresh.Generation == 1
                    ? new DepotSourceV1(content, api, fresh.Manifest!, options.Platform.ContentSystemName(), options.Language)
                    : new DepotSourceV2(content, api, fresh.Manifest!, options.Language);

                try
                {
                    plan = await source.PlanAsync(ct);
                }
                catch (Exception inner) when (inner is NoDepotForLanguageException)
                {
                    return new DownloadResult(DownloadOutcome.NoDepotForLanguage, Detail: inner.Message);
                }
            }
        }

        // A snapshot may lag behind the live product. Record the installer actually selected,
        // and recognize an already completed live version even while the snapshot is stale.
        var actualInstaller = setup ?? (source as InstallerSource)?.SelectedInstaller;
        if (actualInstaller is not null)
        {
            buildId = InstallerSelection.BuildId(actualInstaller.Language, actualInstaller.Version, actualInstaller.TotalSize);
            versionName = actualInstaller.Version;
            archiveName = GameVaultName.For(game, options.Platform, versionName);
            if (!options.Force && existing?.Platforms.GetValueOrDefault(options.Platform) is { } current
                && current.Matches(gameDirectory, buildId, options.Language, [], options.Platform))
                return new DownloadResult(DownloadOutcome.AlreadyDone, Path.Combine(gameDirectory, current.ArchiveName),
                    $"Build {buildId} is already backed up.");
        }

        if (plan is not null) plan = LinkResolver.Resolve(plan, options.Platform);
        var totalCompressed = plan?.DownloadBytes ?? setup!.TotalSize;
        // Link copies also occupy the ZIP, without adding any network download bytes.
        var archiveEstimate = totalCompressed + (plan?.LinkBytes ?? 0);

        // An Inno Setup installer does not say how large the game unpacks to. Three times the
        // installer is the rule of thumb, and the check is a courtesy anyway.
        var installedSize = plan?.InstalledBytes ?? setup!.TotalSize * 3;

        var staging = Path.Combine(
            options.StagingDirectory, $"{game.DirectoryName}__{options.Platform.ContentSystemName()}");

        Directory.CreateDirectory(options.StagingDirectory);
        Directory.CreateDirectory(gameDirectory);

        // Staging survives an interrupted run so the next one can resume, but only when it
        // belongs to this exact build and language; otherwise its files are stale.
        var state = new StagingState(buildId, options.Language, options.Platform.ContentSystemName());
        var resumable = !options.Clean && StagingState.Load(staging) == state;

        // Bytes already on disk do not need to be found again, or a resume would be refused on
        // a volume that in fact has room for what is left.
        var alreadyStaged = resumable ? DirectorySize(staging) : 0;
        var stagingNeeds = Math.Max(0, installedSize + archiveEstimate - alreadyStaged);

        // Staging holds the unpacked game and the zip side by side; the target only needs the
        // zip. Failing here beats failing after an hour of downloading.
        if (FreeSpaceShortfall(options.StagingDirectory, stagingNeeds) is { } short1)
            return new DownloadResult(DownloadOutcome.NotEnoughSpace,
                Detail: $"{options.StagingDirectory} needs {Human(short1)} more free. Pick another --staging.");

        if (!SameVolume(options.StagingDirectory, gameDirectory)
            && FreeSpaceShortfall(gameDirectory, archiveEstimate) is { } short2)
            return new DownloadResult(DownloadOutcome.NotEnoughSpace,
                Detail: $"{gameDirectory} needs {Human(short2)} more free.");

        if (!resumable) StagingState.Discard(staging);
        Directory.CreateDirectory(staging);
        state.Save(staging);

        try
        {
            if (innoSetup)
                // The extract scratch is named by game id alone: innoextract is not long-path
                // aware, and a slug-length prefix under a deep game tree crosses Windows MAX_PATH.
                await new InnoSetupExtractor(api, cdn, content.Limiter)
                    .FillAsync(setup!, staging, staging + ".installer",
                        Path.Combine(options.StagingDirectory, $"{game.Id}.x"), progress, ct);
            else
            {
                // The previous backup of this platform is a cache: a new build changes a few
                // files, and the rest can be lifted out of the old zip instead of the network.
                using var cache = options.Reuse && existing?.Platforms.GetValueOrDefault(options.Platform) is { } old
                    ? ArchiveCache.Open(Path.Combine(gameDirectory, old.ArchiveName), old.Files)
                    : null;

                await MaterializeAsync(staging, source!, plan!, resumable, cache, options, progress, ct);
            }

            // A single entry point in the archive root, from the depot's own launch metadata.
            if (options.WriteLauncher)
                await StartScript.WriteAsync(staging, options.Platform, ct);

            // Recorded for `gamevault sync`, which turns it into a launch_executable API call.
            var (launchExecutable, launchParameters) =
                await StartScript.ResolveLaunchAsync(staging, options.Platform, ct);

            var stagingZip = staging + ".zip";
            if (File.Exists(stagingZip)) File.Delete(stagingZip);

            await ParallelZipWriter.CreateAsync(
                staging, stagingZip, options.MaxCompressionThreads,
                progress: new Progress<ZipProgress>(z => progress?.Report(
                    new DownloadProgress("zip", z.BytesRead, z.BytesTotal, z.EntriesDone, z.EntriesTotal))),
                ct: ct);

            // The zip writer is ours, so the archive is proven against staging before anything
            // records it as done.
            if (options.VerifyArchive)
                await ArchiveVerifier.VerifyAsync(stagingZip, staging,
                    new Progress<ZipProgress>(z => progress?.Report(
                        new DownloadProgress("check", z.BytesRead, z.BytesTotal, z.EntriesDone, z.EntriesTotal))),
                    ct);

            var archivePath = Path.Combine(gameDirectory, archiveName);
            PublishArchive(stagingZip, archivePath, SameVolume(stagingZip, archivePath), progress, ct);

            var previous = existing?.Platforms.GetValueOrDefault(options.Platform)?.ArchiveName;

            var manifest = existing ?? new BackupManifest
            {
                GameId = game.Id,
                Slug = game.Slug,
                Title = game.Title,
            };

            manifest.Platforms[options.Platform] = new PlatformBackup
            {
                ContentRevision = PlatformBackup.CurrentContentRevision,
                BuildId = buildId,
                VersionName = versionName,
                Language = options.Language,
                ArchiveName = archiveName,
                ArchiveSize = new FileInfo(archivePath).Length,
                InstalledSize = DirectorySize(staging),
                Dlcs = plan?.DlcIds ?? [],
                SkippedDependencies = plan?.Dependencies ?? [],
                LaunchExecutable = launchExecutable,
                LaunchParameters = launchParameters,

                // Inno Setup yields no per-file hashes, so a later patch cannot reuse this zip.
                Files = plan?.Files
                    .Where(f => f.Identity is not null)
                    .ToDictionary(f => f.RelativePath, f => new BackedUpFile(f.Identity!, f.Size)) ?? [],
            };

            // Only now does the game count as downloaded.
            await manifest.SaveAsync(gameDirectory, ct);

            // Keep the previous archive until the manifest commits. If saving fails or is
            // cancelled, the on-disk manifest must still reference an intact backup.
            if (previous is not null && previous != archiveName)
                File.Delete(Path.Combine(gameDirectory, previous));

            // Only a finished backup may throw its staging away. An interrupted run keeps it,
            // so the next one resumes instead of re-downloading tens of gigabytes.
            Directory.Delete(staging, recursive: true);
            StagingState.Remove(staging);

            if (Directory.Exists(staging + ".installer"))
                Directory.Delete(staging + ".installer", recursive: true);

            var scratch = Path.Combine(options.StagingDirectory, $"{game.Id}.x");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);

            return new DownloadResult(DownloadOutcome.Completed, archivePath);
        }
        catch (DownloadIncompleteException e)
        {
            // Keep staging so the good files are not re-downloaded if GOG later serves the rest.
            var detail = "GOG could not serve these files:\n" +
                         string.Join("\n", e.Files.Take(20).Select(f => $"    {f.Key}")) +
                         (e.Files.Count > 20 ? $"\n    ... and {e.Files.Count - 20} more" : "");
            return new DownloadResult(DownloadOutcome.Incomplete, Detail: detail);
        }
        catch (OperationCanceledException)
        {
            throw new DownloadInterruptedException(staging);
        }
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* a leftover partial is harmless; the manifest is what counts */ }
    }

    /// <summary>
    /// Zips the staging directory entry by entry rather than via
    /// <see cref="ZipFile.CreateFromDirectory(string, string)"/>, so that a multi-hour archive
    /// of a large game still reports progress.
    /// </summary>
    private static void CopyInterruptibly(
        Stream source, Stream destination, CancellationToken ct, Action<int>? onWritten = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
                onWritten?.Invoke(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// How many bytes short the volume is, or null when it fits. Returns null when the volume
    /// cannot be inspected — a network share is worth attempting rather than refusing.
    /// </summary>
    internal static long? FreeSpaceShortfall(string path, long needed)
    {
        try
        {
            // On Unix the full path selects the filesystem for statvfs. Taking its path root
            // would always query /, even for staging on a large disk mounted under /mnt.
            var free = new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;
            return free < needed ? needed - free : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Human(long bytes) => $"{bytes / 1024d / 1024 / 1024:F1} GiB";

    /// <summary>Bytes already on disk, used both to size a resume and to report one.</summary>
    public static long DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// A rename is only atomic within one mounted filesystem. On Unix every absolute path
    /// starts with /, including a NAS and a separate staging disk. If the mount cannot be
    /// established, use the interruptible copy-to-temp path rather than assume a local move.
    /// </summary>
    internal static bool SameVolume(string a, string b)
    {
        var first = VolumeFor(a);
        var second = VolumeFor(b);
        return first is not null && second is not null && string.Equals(first, second,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string? VolumeFor(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            // A symlink or junction in any ancestor can cross volumes without changing the
            // lexical path. Conservatively copy through .tmp rather than guess its target.
            for (var ancestor = fullPath; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                if (Directory.Exists(ancestor)
                    && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    return null;

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return DriveInfo.GetDrives()
                .Select(drive => Path.TrimEndingDirectorySeparator(Path.GetFullPath(drive.Name)))
                .Where(mount => string.Equals(fullPath, mount, comparison)
                    || fullPath.StartsWith(Path.EndsInDirectorySeparator(mount)
                        ? mount : mount + Path.DirectorySeparatorChar, comparison))
                .OrderByDescending(mount => mount.Length)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Moves the finished zip into place. A plain rename when staging shares the target's
    /// volume; otherwise a copy to a sibling temp file followed by a rename, so the target
    /// name still appears atomically and a crash cannot leave a half-written archive.
    /// </summary>
    internal static void PublishArchive(
        string stagingZip, string archivePath, bool sameVolume,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (sameVolume)
        {
            File.Move(stagingZip, archivePath, overwrite: true);
            return;
        }

        var total = new FileInfo(stagingZip).Length;
        long copied = 0;

        var temp = archivePath + ".tmp";
        try
        {
            // File.Copy would ignore Ctrl+C for the whole transfer, which over a network
            // share is exactly the part worth interrupting. It would also say nothing while
            // tens of gigabytes crawl across the wire.
            using (var source = File.OpenRead(stagingZip))
            using (var destination = File.Create(temp))
                CopyInterruptibly(source, destination, ct, written =>
                    progress?.Report(new DownloadProgress("copy", copied += written, total, 0, 1)));
        }
        catch
        {
            // Cancelled, or the target volume filled up. Either way the partial copy must not
            // survive next to the archive it pretends to be.
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }

        File.Move(temp, archivePath, overwrite: true);
        File.Delete(stagingZip);
        progress?.Report(new DownloadProgress("copy", total, total, 1, 1));
    }

    /// <summary>
    /// A file left by an interrupted run counts as done only when its size and hash match.
    /// Without a whole-file hash, equal size cannot distinguish an old base file from its DLC
    /// replacement (or a partial write), so it must be fetched again.
    /// </summary>
    private static bool AlreadyMaterialized(string fullPath, PlannedFile file)
    {
        if (!File.Exists(fullPath)) return false;
        if (new FileInfo(fullPath).Length != file.Size) return false;
        if (file.Identity is not { } expected) return false;

        using var stream = File.OpenRead(fullPath);
        var actual = Convert.ToHexStringLower(MD5.HashData(stream));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The entire staging tree will be zipped. Remove paths absent from the current plan,
    /// including spellings left by an older merger that selected DLC before the base game.
    /// Exact casing matters even when the final game will run on a case-insensitive disk.
    /// </summary>
    private static void RemoveUnplannedStaging(string staging, DepotPlan plan, CancellationToken ct)
    {
        var files = plan.Files.Select(f => f.RelativePath)
            .Concat(plan.Links.Select(link => SafeRelative(link.Path)))
            .ToHashSet(StringComparer.Ordinal);
        var directories = plan.Directories.Select(SafeRelative).ToHashSet(StringComparer.Ordinal);
        // Manifests need not list the parents of their files or explicit empty directories.
        foreach (var path in files.Concat(directories).ToArray())
            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                directories.Add(parent.Replace('\\', '/'));

        foreach (var path in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (!files.Contains(Path.GetRelativePath(staging, path).Replace('\\', '/')))
                File.Delete(path);
        }
        // Remove children before their parents; planned empty directories must survive.
        foreach (var path in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            ct.ThrowIfCancellationRequested();
            if (!directories.Contains(Path.GetRelativePath(staging, path).Replace('\\', '/')))
                Directory.Delete(path);
        }
    }

    private async Task MaterializeAsync(
        string staging,
        ContentSource source,
        DepotPlan plan,
        bool resuming,
        ArchiveCache? cache,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        if (resuming) RemoveUnplannedStaging(staging, plan, ct);

        foreach (var directory in plan.Directories)
            Directory.CreateDirectory(Path.Combine(staging, directory));

        var files = plan.Files;
        var totalBytes = plan.DownloadBytes;

        if (resuming)
        {
            progress?.Report(new DownloadProgress("verify", 0, 0, 0, files.Count));

            var verified = 0;
            var complete = new bool[files.Count];
            Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = ct }, (int i) =>
            {
                complete[i] = AlreadyMaterialized(Path.Combine(staging, files[i].RelativePath), files[i]);
                progress?.Report(new DownloadProgress("verify", 0, 0, Interlocked.Increment(ref verified), files.Count));
            });

            totalBytes -= files.Where((_, i) => complete[i]).Sum(f => f.DownloadSize);
            files = files.Where((_, i) => !complete[i]).ToList();
        }

        if (cache is not null && files.Count > 0)
        {
            progress?.Report(new DownloadProgress("reuse", 0, 0, 0, files.Count));

            var restored = new List<PlannedFile>();
            var seen = 0;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                if (file.Identity is { } identity
                    && await cache.TryRestoreAsync(identity, Path.Combine(staging, file.RelativePath), file.Size, ct))
                    restored.Add(file);

                progress?.Report(new DownloadProgress("reuse", 0, 0, ++seen, files.Count));
            }

            totalBytes -= restored.Sum(f => f.DownloadSize);
            files = files.Except(restored).ToList();
        }

        var slots = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
        var unfetchable = new Dictionary<string, string>();

        long downloaded = 0;
        var filesDone = 0;

        // Synchronous, in-order reporting: a retry undoes its failed attempt's bytes with a
        // negative report, and an async Progress<T> could deliver that out of order.
        var bytesProgress = new SyncProgress<long>(n =>
        {
            var total = Interlocked.Add(ref downloaded, n);
            progress?.Report(new DownloadProgress("download", total, totalBytes, filesDone, files.Count));
        });

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxConnections, CancellationToken = ct },
            async (file, token) =>
            {
                var fullPath = Path.Combine(staging, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                try
                {
                    using var handle = File.OpenHandle(
                        fullPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        FileOptions.Asynchronous, preallocationSize: file.Size);

                    var writes = file.Parts
                        .Select(part => FetchPartToAsync(handle, source, file, part, slots, bytesProgress, token))
                        .ToList();

                    await Task.WhenAll(writes);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A file GOG cannot serve — a chunk truncated even from origin, say — must not
                    // abandon the whole game or the batch behind it. Record it, drop the partial,
                    // and let the rest download; the game is reported incomplete at the end.
                    SafeDelete(fullPath);
                    lock (unfetchable) unfetchable[file.RelativePath] = e.Message;
                }

                Interlocked.Increment(ref filesDone);
            });

        if (unfetchable.Count > 0)
            throw new DownloadIncompleteException(unfetchable);

        // The resolver flattened directory and chained links to verified regular files.
        foreach (var link in plan.Links)
        {
            var linkSource = Path.Combine(staging, SafeRelative(link.Target));
            var destination = Path.Combine(staging, SafeRelative(link.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var sourceFile = File.OpenRead(linkSource);
            await using var targetFile = File.Create(destination);
            await sourceFile.CopyToAsync(targetFile, ct);
        }
    }

    /// <summary>Depot paths use `\`. This also prevents escaping the staging directory.</summary>
    private static string SafeRelative(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Split('/').Any(segment => segment is ".." or "."))
            throw new InvalidOperationException($"Suspicious path in depot: {path}");
        return normalized;
    }

    private static async Task FetchPartToAsync(
        SafeFileHandle handle, ContentSource source, PlannedFile file, PlannedPart part,
        SemaphoreSlim slots, IProgress<long> bytes, CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        try
        {
            using var destination = new PartWriteStream(handle, part.DestinationOffset);
            await source.CopyToAsync(file, part, destination, bytes, ct);
        }
        finally
        {
            slots.Release();
        }
    }

}
