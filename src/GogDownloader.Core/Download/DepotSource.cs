using GogDownloader.Core.Api;
using GogDownloader.Core.Content;

namespace GogDownloader.Core.Download;

/// <summary>
/// One transfer that fills part of a file. Generation 2 fetches a compressed chunk addressed by
/// hash; generation 1 fetches a byte range out of one blob. Both land at
/// <see cref="DestinationOffset"/>.
/// </summary>
public sealed record PlannedPart(
    long DestinationOffset,
    long SourceOffset,
    long Size,
    string? Md5,
    long DownloadSize);

public sealed record PlannedFile(
    string RelativePath,
    string ProductId,
    long Size,
    string? Identity,
    List<PlannedPart> Parts)
{
    public long DownloadSize => Parts.Sum(p => p.DownloadSize);
}

public sealed record PlannedLink(string Path, string Target);

public sealed record DepotPlan(
    List<PlannedFile> Files,
    List<string> Directories,
    List<PlannedLink> Links,
    List<string> DlcIds,
    List<string> Dependencies)
{
    public long DownloadBytes => Files.Sum(f => f.DownloadSize);
    public long InstalledBytes => Files.Sum(f => f.Size);
}

public sealed class NoDepotForLanguageException(string language)
    : Exception($"No depot for language '{language}'.");

/// <summary>
/// What differs between the two depot generations, and nothing else. The downloader itself does
/// not know which one it is driving.
/// </summary>
public abstract class DepotSource(ContentClient content)
{
    protected ContentClient Content { get; } = content;

    private readonly Dictionary<string, SecureLinkEndpoint> _links = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public abstract Task<DepotPlan> PlanAsync(CancellationToken ct);

    protected abstract Task<SecureLinkEndpoint> RequestLinkAsync(string productId, CancellationToken ct);

    public abstract Task<byte[]> FetchAsync(
        SecureLinkEndpoint endpoint, PlannedPart part, IProgress<long>? bytes, CancellationToken ct);

    public async Task<SecureLinkEndpoint> LinkAsync(string productId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_links.TryGetValue(productId, out var cached)) return cached;
            return _links[productId] = await RequestLinkAsync(productId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A 401 means the signed URL expired, not that the download failed.</summary>
    public async Task InvalidateAsync(string productId, SecureLinkEndpoint stale, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Another worker may have refreshed it already; do not drop a fresh link.
            if (_links.TryGetValue(productId, out var current) && ReferenceEquals(current, stale))
                _links.Remove(productId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Depot paths use `\`. This also prevents escaping the staging directory.</summary>
    protected static string SafeRelative(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Split('/').Any(segment => segment is ".." or "."))
            throw new InvalidOperationException($"Suspicious path in depot: {path}");
        return normalized;
    }

    /// <summary>`support` files do not belong in the game; Galaxy keeps them aside.</summary>
    protected static string RelativePathFor(string path, string productId, bool support)
    {
        var relative = SafeRelative(path);
        return support ? $"gog-support/{productId}/{relative}" : relative;
    }
}

/// <summary>Path A: the modern content system. Files are lists of compressed chunks.</summary>
public sealed class DepotSourceV2(ContentClient content, GogApiClient api, string manifestUrl, string language)
    : DepotSource(content)
{
    public override async Task<DepotPlan> PlanAsync(CancellationToken ct)
    {
        var repository = await Content.GetRepositoryManifestAsync(manifestUrl, ct)
            ?? throw new InvalidOperationException("Failed to load the repository manifest.");

        var owned = (await api.GetOwnedGameIdsAsync(ct)).ToHashSet();
        var dlcIds = repository.Products
            .Select(p => p.ProductId)
            .Where(id => id != repository.BaseProductId
                         && long.TryParse(id, out var n) && owned.Contains(n))
            .ToList();

        var wanted = new HashSet<string>(dlcIds) { repository.BaseProductId };

        var depots = repository.Depots
            .Where(d => wanted.Contains(d.ProductId) && d.MatchesLanguage(language))
            .ToList();

        if (depots.Count == 0) throw new NoDepotForLanguageException(language);

        var files = new Dictionary<string, PlannedFile>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<PlannedLink>();

        foreach (var depot in depots)
        {
            var manifest = await Content.GetDepotManifestAsync(depot.Manifest, ct);
            if (manifest is null) continue;

            foreach (var item in manifest.Depot.Items)
            {
                switch (item.Type)
                {
                    case "DepotFile":
                    {
                        var relative = RelativePathFor(item.Path, depot.ProductId, item.IsSupport);

                        long offset = 0;
                        var parts = new List<PlannedPart>(item.Chunks.Count);
                        foreach (var chunk in item.Chunks)
                        {
                            parts.Add(new PlannedPart(offset, 0, chunk.Size, chunk.CompressedMd5, chunk.CompressedSize));
                            offset += chunk.Size;
                        }

                        files.TryAdd(relative,
                            new PlannedFile(relative, depot.ProductId, item.Size, item.Identity, parts));
                        break;
                    }
                    case "DepotDirectory":
                        directories.Add(SafeRelative(item.Path));
                        break;
                    case "DepotLink" when item.Target is not null:
                        links.Add(new PlannedLink(item.Path, item.Target));
                        break;
                }
            }
        }

        return new DepotPlan(files.Values.ToList(), directories.ToList(), links, dlcIds, repository.Dependencies);
    }

    protected override Task<SecureLinkEndpoint> RequestLinkAsync(string productId, CancellationToken ct) =>
        Content.GetSecureLinkAsync(productId, ct).ContinueWith(t => t.Result[0], ct);

    public override Task<byte[]> FetchAsync(
        SecureLinkEndpoint endpoint, PlannedPart part, IProgress<long>? bytes, CancellationToken ct) =>
        Content.DownloadChunkAsync(
            endpoint,
            new Chunk { CompressedMd5 = part.Md5 ?? "", Size = part.Size, CompressedSize = part.DownloadSize },
            bytes, ct);
}

/// <summary>
/// Generation 1: old titles that never moved to the modern content system. A depot is one blob,
/// and every file is a byte range inside it, stored uncompressed.
/// </summary>
public sealed class DepotSourceV1(
    ContentClient content, GogApiClient api, string manifestUrl, string platform, string language)
    : DepotSource(content)
{
    private string _timestamp = "";

    public override async Task<DepotPlan> PlanAsync(CancellationToken ct)
    {
        var repository = await Content.GetRepositoryManifestV1Async(manifestUrl, ct)
            ?? throw new InvalidOperationException("Failed to load the v1 repository manifest.");

        var product = repository.Product;
        _timestamp = product.TimestampText;

        var owned = (await api.GetOwnedGameIdsAsync(ct)).ToHashSet();

        // v1 has no products[]; a depot names the games it belongs to, so the DLC are whatever
        // ids appear besides the root game and are owned.
        var dlcIds = product.Depots
            .Where(d => !d.IsRedistributable)
            .SelectMany(d => d.GameIds)
            .Distinct()
            .Where(id => id != product.RootGameId && long.TryParse(id, out var n) && owned.Contains(n))
            .ToList();

        var wanted = new HashSet<string>(dlcIds) { product.RootGameId };

        var depots = product.Depots
            .Where(d => !d.IsRedistributable
                        && d.GameIds.Any(wanted.Contains)
                        && d.MatchesLanguage(language))
            .ToList();

        if (depots.Count == 0) throw new NoDepotForLanguageException(language);

        var files = new Dictionary<string, PlannedFile>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var depot in depots)
        {
            var productId = depot.GameIds[0];
            var manifest = await Content.GetDepotManifestV1Async(productId, platform, _timestamp, depot.Manifest, ct);
            if (manifest is null) continue;

            foreach (var record in manifest.Depot.Files)
            {
                if (record.Directory)
                {
                    directories.Add(SafeRelative(record.Path));
                    continue;
                }

                // Zero-length records carry no range to fetch; gogdl skips them too.
                if (record.Size <= 0) continue;

                var relative = RelativePathFor(record.Path, productId, record.Support);
                var part = new PlannedPart(0, record.Offset, record.Size, record.Hash, record.Size);

                files.TryAdd(relative, new PlannedFile(relative, productId, record.Size, record.Hash, [part]));
            }
        }

        // v1 lists redistributables as depots rather than ids.
        var dependencies = product.Depots
            .Where(d => d.IsRedistributable)
            .Select(d => d.Redist!)
            .ToList();

        return new DepotPlan(files.Values.ToList(), directories.ToList(), [], dlcIds, dependencies);
    }

    protected override async Task<SecureLinkEndpoint> RequestLinkAsync(string productId, CancellationToken ct) =>
        (await Content.GetSecureLinkAsync(productId, ct, generation: 1, path: $"/{platform}/{_timestamp}/"))[0];

    public override Task<byte[]> FetchAsync(
        SecureLinkEndpoint endpoint, PlannedPart part, IProgress<long>? bytes, CancellationToken ct) =>
        Content.DownloadRangeAsync(endpoint, part.SourceOffset, part.Size, part.Md5, bytes, ct);
}
