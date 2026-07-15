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

/// <summary>A progress that reports on the calling thread, so ordered reports stay ordered.</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>
/// Where a game's bytes come from. Depots of either generation, or a Linux installer read over
/// HTTP Range — the downloader does not know which it is driving.
/// </summary>
public abstract class ContentSource
{
    /// <summary>
    /// Set by the CLI's --verbose to surface why a part is being retried. A download of thousands
    /// of tiny files hides the reason, and knowing it — an expired link versus a stalled
    /// connection — is the difference between diagnosing and guessing.
    /// </summary>
    public static Action<string>? RetryObserver { get; set; }

    public abstract Task<DepotPlan> PlanAsync(CancellationToken ct);

    protected abstract Task<byte[]> FetchOnceAsync(
        PlannedFile file, PlannedPart part, IProgress<long>? bytes, bool freshFromOrigin, CancellationToken ct);

    /// <summary>Called when a signed URL expired, so the next attempt asks for a fresh one.</summary>
    protected virtual Task RefreshAsync(PlannedFile file, CancellationToken ct) => Task.CompletedTask;

    private const int MaxAttempts = 5;

    /// <summary>
    /// Fetches one part, retrying the failures that a long download will inevitably hit and
    /// that a single try must not abandon a whole game over: an expired signed URL, a corrupted
    /// transfer, and — the common one — a connection dropped mid-body.
    /// </summary>
    public async Task<byte[]> FetchAsync(
        PlannedFile file, PlannedPart part, IProgress<long>? bytes, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            // A failed attempt has already reported the bytes it read before it broke; undo them
            // so the retry does not count them twice and push the total past 100%.
            long attemptBytes = 0;
            var counting = bytes is null ? null
                : new SyncProgress<long>(n => { attemptBytes += n; bytes.Report(n); });

            try
            {
                // First try may be served from the CDN cache; every retry bypasses it, in case
                // the edge is holding a truncated object — the deterministic short read that no
                // amount of same-URL retrying fixes.
                return await FetchOnceAsync(file, part, counting, freshFromOrigin: attempt > 0, ct);
            }
            catch (SecureLinkExpiredException) when (attempt < MaxAttempts)
            {
                RetryObserver?.Invoke($"{file.RelativePath}: signed URL expired (401/403), refreshing");
                bytes?.Report(-attemptBytes);
                await RefreshAsync(file, ct);
            }
            catch (ChunkChecksumException e) when (attempt < MaxAttempts)
            {
                RetryObserver?.Invoke($"{file.RelativePath}: {e.Message}");
                bytes?.Report(-attemptBytes);
            }
            catch (Exception e) when (attempt < MaxAttempts && IsTransientNetwork(e, ct))
            {
                // The body is streamed after the retry handler has already returned the
                // headers, so a connection that drops mid-transfer ("response ended
                // prematurely"), a reset, or a per-request timeout surfaces here, not there.
                RetryObserver?.Invoke($"{file.RelativePath}: {e.Message}");
                bytes?.Report(-attemptBytes);

                // An expired signed URL can present as a stall rather than a clean 401/403, and
                // a download long enough to hit the CDN's per-link cap stalls at the same point
                // every run. Get a fresh link so the retry is not against the dead one.
                await RefreshAsync(file, ct);
                await Task.Delay(Backoff(attempt), ct);
            }
        }
    }

    /// <summary>A dropped connection or a timeout — not a bug, and not the user's Ctrl+C.</summary>
    private static bool IsTransientNetwork(Exception e, CancellationToken ct) =>
        !ct.IsCancellationRequested && e switch
        {
            HttpRequestException => true,
            IOException => true,                       // includes HttpIOException / ResponseEnded
            TaskCanceledException => true,             // a per-request timeout, since ct is not set
            _ => false,
        };

    /// <summary>
    /// Starts small: the usual cause is a reused keep-alive connection the CDN has closed, which
    /// the next attempt fixes on a fresh connection. A game with thousands of tiny files hits
    /// this often, and a slow first retry there stalls a worker and collapses throughput. Grows
    /// for the rarer sustained failure.
    /// </summary>
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(200 * Math.Pow(2, attempt), 10_000) + Random.Shared.Next(200));

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

/// <summary>A depot of either generation: signed links, cached per product, refreshed on 401.</summary>
public abstract class DepotSource(ContentClient content) : ContentSource
{
    protected ContentClient Content { get; } = content;

    private readonly Dictionary<string, SecureLinkEndpoint> _links = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected abstract Task<SecureLinkEndpoint> RequestLinkAsync(string productId, CancellationToken ct);

    protected abstract Task<byte[]> FetchPartAsync(
        SecureLinkEndpoint endpoint, PlannedPart part, IProgress<long>? bytes, bool freshFromOrigin, CancellationToken ct);

    protected override async Task<byte[]> FetchOnceAsync(
        PlannedFile file, PlannedPart part, IProgress<long>? bytes, bool freshFromOrigin, CancellationToken ct) =>
        await FetchPartAsync(await LinkAsync(file.ProductId, ct), part, bytes, freshFromOrigin, ct);

    protected override async Task RefreshAsync(PlannedFile file, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _links.Remove(file.ProductId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SecureLinkEndpoint> LinkAsync(string productId, CancellationToken ct)
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

    protected override async Task<SecureLinkEndpoint> RequestLinkAsync(string productId, CancellationToken ct) =>
        (await Content.GetSecureLinkAsync(productId, ct))[0];

    protected override Task<byte[]> FetchPartAsync(
        SecureLinkEndpoint endpoint, PlannedPart part, IProgress<long>? bytes, bool freshFromOrigin, CancellationToken ct) =>
        Content.DownloadChunkAsync(
            endpoint,
            new Chunk { CompressedMd5 = part.Md5 ?? "", Size = part.Size, CompressedSize = part.DownloadSize },
            bytes, freshFromOrigin, ct);
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

    protected override Task<byte[]> FetchPartAsync(
        SecureLinkEndpoint endpoint, PlannedPart part, IProgress<long>? bytes, bool freshFromOrigin, CancellationToken ct) =>
        Content.DownloadRangeAsync(endpoint, part.SourceOffset, part.Size, part.Md5, bytes, freshFromOrigin, ct);
}
