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
    Completed, AlreadyDone, NoDepot, UnsupportedGeneration, NoDepotForLanguage, NotEnoughSpace,
}

public sealed record DownloadResult(DownloadOutcome Outcome, string? ArchivePath = null, string? Detail = null);

public sealed record DownloadProgress(string Phase, long Bytes, long TotalBytes, int Files, int TotalFiles);

/// <summary>
/// Path A: assembles the game from Galaxy depots in a staging directory, zips it and
/// atomically moves the zip into place. The manifest is written last.
/// </summary>
public sealed class GameDownloader(GogApiClient api, ContentClient content)
{
    private sealed record PlannedFile(string RelativePath, string ProductId, DepotItem Item);

    public async Task<DownloadResult> RunAsync(
        LibraryEntry game,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (game.Platforms.GetValueOrDefault(options.Platform) is not { Depot: { } build })
            return new DownloadResult(DownloadOutcome.NoDepot,
                Detail: "No Galaxy depot for this platform (path B/C, not implemented yet).");

        if (build.Generation != 2)
            return new DownloadResult(DownloadOutcome.UnsupportedGeneration,
                Detail: $"Build is generation {build.Generation}; only 2 is supported so far.");

        var gameDirectory = Path.Combine(options.OutputRoot, game.DirectoryName);
        var archiveName = GameVaultName.For(game, options.Platform, build.VersionName);

        var existing = await BackupManifest.LoadAsync(gameDirectory, ct);
        if (!options.Force
            && existing?.Platforms.GetValueOrDefault(options.Platform) is { } done
            && done.BuildId == build.BuildId)
        {
            return new DownloadResult(DownloadOutcome.AlreadyDone,
                Path.Combine(gameDirectory, done.ArchiveName),
                $"Build {build.BuildId} is already backed up.");
        }

        progress?.Report(new DownloadProgress("manifest", 0, 0, 0, 0));

        var repository = await content.GetRepositoryManifestAsync(build.Manifest!, ct)
            ?? throw new InvalidOperationException("Failed to load the repository manifest.");

        var owned = (await api.GetOwnedGameIdsAsync(ct)).ToHashSet();
        var dlcIds = repository.Products
            .Select(p => p.ProductId)
            .Where(id => id != repository.BaseProductId
                         && long.TryParse(id, out var n) && owned.Contains(n))
            .ToList();

        var wanted = new HashSet<string>(dlcIds) { repository.BaseProductId };

        var depots = repository.Depots
            .Where(d => wanted.Contains(d.ProductId) && d.MatchesLanguage(options.Language))
            .ToList();

        if (depots.Count == 0)
            return new DownloadResult(DownloadOutcome.NoDepotForLanguage,
                Detail: $"No depot for language '{options.Language}'.");

        var (files, directories, links) = await PlanAsync(depots, ct);

        var totalCompressed = files.Sum(f => f.Item.Chunks.Sum(c => c.CompressedSize));
        var installedSize = files.Sum(f => f.Item.Size);

        var staging = Path.Combine(
            options.StagingDirectory, $"{game.DirectoryName}__{options.Platform.ContentSystemName()}");

        Directory.CreateDirectory(options.StagingDirectory);
        Directory.CreateDirectory(gameDirectory);

        // Staging holds the unpacked game and the zip side by side; the target only needs the
        // zip. Failing here beats failing after an hour of downloading.
        if (FreeSpaceShortfall(options.StagingDirectory, installedSize + totalCompressed) is { } short1)
            return new DownloadResult(DownloadOutcome.NotEnoughSpace,
                Detail: $"{options.StagingDirectory} needs {Human(short1)} more free. Pick another --staging.");

        if (!SameVolume(options.StagingDirectory, gameDirectory)
            && FreeSpaceShortfall(gameDirectory, totalCompressed) is { } short2)
            return new DownloadResult(DownloadOutcome.NotEnoughSpace,
                Detail: $"{gameDirectory} needs {Human(short2)} more free.");

        // Staging survives an interrupted run so the next one can resume, but only when it
        // belongs to this exact build and language; otherwise its files are stale.
        var state = new StagingState(build.BuildId, options.Language, options.Platform.ContentSystemName());
        var resumable = !options.Clean && StagingState.Load(staging) == state;

        if (!resumable) StagingState.Discard(staging);
        Directory.CreateDirectory(staging);
        state.Save(staging);

        try
        {
            await MaterializeAsync(staging, files, directories, links, totalCompressed, resumable, options, progress, ct);

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

            // A new build renames the archive, so the old one would linger and GameVault would
            // index the same game twice.
            var previous = existing?.Platforms.GetValueOrDefault(options.Platform)?.ArchiveName;
            if (previous is not null && previous != archiveName)
                File.Delete(Path.Combine(gameDirectory, previous));

            var manifest = existing ?? new BackupManifest
            {
                GameId = game.Id,
                Slug = game.Slug,
                Title = game.Title,
            };

            manifest.Platforms[options.Platform] = new PlatformBackup
            {
                BuildId = build.BuildId,
                VersionName = build.VersionName,
                Language = options.Language,
                ArchiveName = archiveName,
                ArchiveSize = new FileInfo(archivePath).Length,
                InstalledSize = installedSize,
                Dlcs = dlcIds,
                SkippedDependencies = repository.Dependencies,
                Files = files
                    .Where(f => f.Item.Identity is not null)
                    .ToDictionary(f => f.RelativePath, f => new BackedUpFile(f.Item.Identity!, f.Item.Size)),
            };

            // Only now does the game count as downloaded.
            await manifest.SaveAsync(gameDirectory, ct);

            // Only a finished backup may throw its staging away. An interrupted run keeps it,
            // so the next one resumes instead of re-downloading tens of gigabytes.
            Directory.Delete(staging, recursive: true);
            StagingState.Remove(staging);

            return new DownloadResult(DownloadOutcome.Completed, archivePath);
        }
        catch (OperationCanceledException)
        {
            throw new DownloadInterruptedException(staging);
        }
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
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
            return free < needed ? needed - free : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Human(long bytes) => $"{bytes / 1024d / 1024 / 1024:F1} GiB";

    /// <summary>A rename is only atomic within one volume.</summary>
    internal static bool SameVolume(string a, string b) => string.Equals(
        Path.GetPathRoot(Path.GetFullPath(a)),
        Path.GetPathRoot(Path.GetFullPath(b)),
        StringComparison.OrdinalIgnoreCase);

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
        catch (OperationCanceledException)
        {
            File.Delete(temp);
            throw;
        }

        File.Move(temp, archivePath, overwrite: true);
        File.Delete(stagingZip);
        progress?.Report(new DownloadProgress("copy", total, total, 1, 1));
    }

    private async Task<(List<PlannedFile> Files, List<string> Directories, List<DepotItem> Links)> PlanAsync(
        List<DepotRef> depots, CancellationToken ct)
    {
        var files = new Dictionary<string, PlannedFile>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<DepotItem>();

        foreach (var depot in depots)
        {
            var manifest = await content.GetDepotManifestAsync(depot.Manifest, ct);
            if (manifest is null) continue;

            foreach (var item in manifest.Depot.Items)
            {
                switch (item.Type)
                {
                    case "DepotFile":
                        var relative = RelativePathFor(item, depot.ProductId);
                        files.TryAdd(relative, new PlannedFile(relative, depot.ProductId, item));
                        break;
                    case "DepotDirectory":
                        directories.Add(SafeRelative(item.Path));
                        break;
                    case "DepotLink":
                        links.Add(item);
                        break;
                }
            }
        }

        return (files.Values.ToList(), directories.ToList(), links);
    }

    private static string RelativePathFor(DepotItem item, string productId)
    {
        var relative = SafeRelative(item.Path);
        // `support` files do not belong in the game, Galaxy keeps them aside.
        return item.IsSupport ? $"gog-support/{productId}/{relative}" : relative;
    }

    /// <summary>Depot paths use `\`. This also prevents escaping the staging directory.</summary>
    private static string SafeRelative(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Split('/').Any(s => s is ".." or "."))
            throw new InvalidOperationException($"Suspicious path in depot: {path}");
        return normalized;
    }

    /// <summary>
    /// A file left by an interrupted run counts as done only when its size matches and, when
    /// the depot gives us a hash, its content does too. Trusting size alone would keep a file
    /// truncated exactly at a chunk boundary.
    /// </summary>
    private static bool AlreadyMaterialized(string fullPath, DepotItem item)
    {
        if (!File.Exists(fullPath)) return false;
        if (new FileInfo(fullPath).Length != item.Size) return false;
        if (item.Identity is not { } expected) return true;

        using var stream = File.OpenRead(fullPath);
        var actual = Convert.ToHexStringLower(MD5.HashData(stream));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task MaterializeAsync(
        string staging,
        List<PlannedFile> files,
        List<string> directories,
        List<DepotItem> links,
        long totalCompressed,
        bool resuming,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        foreach (var directory in directories)
            Directory.CreateDirectory(Path.Combine(staging, directory));

        if (resuming)
        {
            progress?.Report(new DownloadProgress("verify", 0, 0, 0, files.Count));

            var verified = 0;
            var complete = new bool[files.Count];
            Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = ct }, i =>
            {
                complete[i] = AlreadyMaterialized(Path.Combine(staging, files[i].RelativePath), files[i].Item);
                progress?.Report(new DownloadProgress("verify", 0, 0, Interlocked.Increment(ref verified), files.Count));
            });

            var kept = files.Where((_, i) => complete[i]).ToList();
            totalCompressed -= kept.Sum(f => f.Item.Chunks.Sum(c => c.CompressedSize));
            files = files.Where((_, i) => !complete[i]).ToList();
        }

        var secureLinks = new SecureLinkCache(content);
        var slots = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);

        long downloaded = 0;
        var filesDone = 0;
        var bytesProgress = new Progress<long>(n =>
        {
            var total = Interlocked.Add(ref downloaded, n);
            progress?.Report(new DownloadProgress("download", total, totalCompressed, filesDone, files.Count));
        });

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxConnections, CancellationToken = ct },
            async (file, token) =>
            {
                var fullPath = Path.Combine(staging, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                var size = file.Item.Size;
                using (var handle = File.OpenHandle(
                           fullPath, FileMode.Create, FileAccess.Write, FileShare.None,
                           FileOptions.Asynchronous, preallocationSize: size))
                {
                    long offset = 0;
                    var writes = new List<Task>(file.Item.Chunks.Count);

                    foreach (var chunk in file.Item.Chunks)
                    {
                        var at = offset;
                        offset += chunk.Size;
                        writes.Add(DownloadChunkToAsync(handle, at, file.ProductId, chunk, secureLinks, slots, bytesProgress, token));
                    }

                    await Task.WhenAll(writes);
                }

                Interlocked.Increment(ref filesDone);
            });

        // Symlinks are dereferenced: the target is relative to the install root.
        foreach (var link in links)
        {
            if (link.Target is null) continue;
            var source = Path.Combine(staging, SafeRelative(link.Target));
            var destination = Path.Combine(staging, SafeRelative(link.Path));
            if (!File.Exists(source)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
    }

    private async Task DownloadChunkToAsync(
        SafeFileHandle handle, long offset, string productId, Chunk chunk,
        SecureLinkCache secureLinks, SemaphoreSlim slots, IProgress<long> bytes, CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var endpoint = await secureLinks.GetAsync(productId, ct);
                try
                {
                    var data = await content.DownloadChunkAsync(endpoint, chunk, bytes, ct);
                    await RandomAccess.WriteAsync(handle, data, offset, ct);
                    return;
                }
                catch (SecureLinkExpiredException) when (attempt < 3)
                {
                    await secureLinks.InvalidateAsync(productId, endpoint, ct);
                }
                catch (ChunkChecksumException) when (attempt < 3)
                {
                    // corrupted transfer, try again
                }
            }
        }
        finally
        {
            slots.Release();
        }
    }
}
