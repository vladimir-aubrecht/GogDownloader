using System.IO.Compression;
using Microsoft.Win32.SafeHandles;
using GogDownloader.Core.Api;
using GogDownloader.Core.Content;
using GogDownloader.Core.Library;

namespace GogDownloader.Core.Download;

public sealed record DownloadOptions
{
    public required string OutputRoot { get; init; }
    public required GamePlatform Platform { get; init; }
    public string Language { get; init; } = "en";
    public int MaxConnections { get; init; } = 8;

    /// <summary>Re-download even what the manifest already reports as done.</summary>
    public bool Force { get; init; }
}

public enum DownloadOutcome { Completed, AlreadyDone, NoDepot, UnsupportedGeneration, NoDepotForLanguage }

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
        var archiveName = $"{options.Platform.ContentSystemName()}.zip";

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

        var staging = Path.Combine(options.OutputRoot, ".gogdl-staging", $"{game.DirectoryName}__{options.Platform.ContentSystemName()}");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        try
        {
            await MaterializeAsync(staging, files, directories, links, totalCompressed, options, progress, ct);

            progress?.Report(new DownloadProgress("zip", totalCompressed, totalCompressed, files.Count, files.Count));

            var stagingZip = staging + ".zip";
            if (File.Exists(stagingZip)) File.Delete(stagingZip);
            ZipFile.CreateFromDirectory(staging, stagingZip, CompressionLevel.Optimal, includeBaseDirectory: false);

            Directory.CreateDirectory(gameDirectory);
            var archivePath = Path.Combine(gameDirectory, archiveName);
            File.Move(stagingZip, archivePath, overwrite: true);

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

            return new DownloadResult(DownloadOutcome.Completed, archivePath);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
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

    private async Task MaterializeAsync(
        string staging,
        List<PlannedFile> files,
        List<string> directories,
        List<DepotItem> links,
        long totalCompressed,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        foreach (var directory in directories)
            Directory.CreateDirectory(Path.Combine(staging, directory));

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
