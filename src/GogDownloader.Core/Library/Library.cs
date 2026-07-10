using System.Text.Json;
using System.Text.Json.Serialization;
using GogDownloader.Core.Api;
using GogDownloader.Core.Content;

namespace GogDownloader.Core.Library;

public sealed record BuildRef(string BuildId, int Generation, string? VersionName, string? Manifest);

public sealed record InstallerRef(string Language, string? Version, long TotalSize);

/// <summary>How a given platform of a given game can be obtained.</summary>
public sealed record PlatformAvailability
{
    /// <summary>Galaxy depot. Null means the game has none for this platform.</summary>
    public BuildRef? Depot { get; init; }

    /// <summary>Offline installers from api.gog.com.</summary>
    public List<InstallerRef> Installers { get; init; } = [];

    /// <summary>
    /// Bytes actually transferred from the CDN: the sum of `compressedSize` over the depots
    /// that would be downloaded. Assumes the default language, see <see cref="LibrarySync"/>.
    /// </summary>
    public long DownloadSize { get; init; }

    /// <summary>Bytes the assembled game occupies before zipping: the sum of depot `size`.</summary>
    public long InstalledSize { get; init; }

    public bool HasDepot => Depot is not null;

    public bool HasDownloadableDepot => Depot is not null;

    /// <summary>
    /// The old content system. Still fetched, but its manifest has no compressed size, so
    /// <see cref="DownloadSize"/> is the unpacked size rather than the bytes off the wire.
    /// </summary>
    public bool HasLegacyDepot => Depot is { Generation: 1 };

    public bool HasInstaller => Installers.Count > 0;
    public bool Unavailable => !HasDepot && !HasInstaller;

    public long LargestInstallerSize => Installers.Count == 0 ? 0 : Installers.Max(i => i.TotalSize);

    /// <summary>What to show as "size": the depot if there is one, otherwise the installer.</summary>
    public long BestKnownSize => HasDepot ? DownloadSize : LargestInstallerSize;
}

public sealed record DlcRef(string ProductId, string Name);

public sealed record LibraryEntry
{
    public required long Id { get; init; }
    public required string Title { get; init; }
    public required string Slug { get; init; }

    public Dictionary<GamePlatform, PlatformAvailability> Platforms { get; init; } = [];

    /// <summary>Still in development on GOG; becomes GameVault's `(EA)` flag.</summary>
    public bool EarlyAccess { get; init; }

    /// <summary>
    /// Owned DLC, read from `products[]` in the repository manifest — the same source the
    /// downloader uses, so the listing cannot disagree with what ends up in the zip.
    /// Empty for games without a depot, whose manifest does not exist.
    /// </summary>
    public List<DlcRef> Dlcs { get; init; } = [];

    /// <summary>Directory on disk. Never derived from the title — see CLAUDE.md.</summary>
    public string DirectoryName => $"{Slug}__{Id}";
}

public sealed record LibrarySnapshot
{
    /// <summary>Bumped when a sync starts recording something older snapshots lack.</summary>
    public const int CurrentSchema = 6;

    /// <summary>Absent in snapshots written before schema versioning, hence 0.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Schema 2 added the DLC list; older snapshots would report no DLC at all.</summary>
    public bool KnowsDlcs => SchemaVersion >= 2;

    /// <summary>
    /// Schema 3 added depot sizes, 4 and older computed them with a broken language filter, and
    /// 5 left generation 1 unsized. Anything older than 6 under-reports.
    /// </summary>
    public bool KnowsDepotSizes => SchemaVersion >= 6;

    /// <summary>Schema 4 added the early-access flag, which names the archive for GameVault.</summary>
    public bool KnowsEarlyAccess => SchemaVersion >= 4;

    public DateTimeOffset SyncedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<LibraryEntry> Games { get; init; } = [];

    /// <summary>Games that failed to load — one bad game must not fail the whole sync.</summary>
    public Dictionary<long, string> Failures { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "gogdownloader", "library.json");

    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, this, Json, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task<LibrarySnapshot?> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LibrarySnapshot>(stream, Json, ct);
    }
}

public sealed class LibrarySync(GogApiClient api, ContentClient content, int maxParallelism)
{
    private static readonly GamePlatform[] DepotPlatforms = [GamePlatform.Windows, GamePlatform.Mac];

    /// <summary>
    /// Depot sizes are language-dependent, so a snapshot has to pick one. This matches the
    /// default of `download --language`; other languages will differ by the size of their
    /// voice-over depots.
    /// </summary>
    private const string SizeLanguage = "en";

    public async Task<LibrarySnapshot> RunAsync(
        IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var ids = await api.GetOwnedGameIdsAsync(ct);
        var owned = ids.ToHashSet();

        var games = new List<LibraryEntry>(ids.Count);
        var failures = new Dictionary<long, string>();
        var done = 0;

        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct },
            async (id, token) =>
            {
                try
                {
                    var entry = await FetchAsync(id, owned, token);
                    if (entry is not null)
                        lock (games) games.Add(entry);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (failures) failures[id] = ex.Message;
                }
                finally
                {
                    progress?.Report((Interlocked.Increment(ref done), ids.Count));
                }
            });

        games.Sort((a, b) => string.CompareOrdinal(a.Title, b.Title));
        return new LibrarySnapshot
        {
            SchemaVersion = LibrarySnapshot.CurrentSchema,
            Games = games,
            Failures = failures,
        };
    }

    private async Task<LibraryEntry?> FetchAsync(long id, HashSet<long> owned, CancellationToken ct)
    {
        var product = await api.GetProductAsync(id, ct);
        if (product is null) return null;

        var platforms = new Dictionary<GamePlatform, PlatformAvailability>();

        // Bucket the installers by platform.
        var installers = new Dictionary<GamePlatform, List<InstallerRef>>();
        foreach (var installer in product.Downloads?.Installers ?? [])
        {
            if (GamePlatformExtensions.FromApiName(installer.Os) is not { } platform) continue;
            if (!installers.TryGetValue(platform, out var list))
                installers[platform] = list = [];
            list.Add(new InstallerRef(installer.Language, installer.Version, installer.TotalSize));
        }

        var dlcs = new List<DlcRef>();

        // Depots exist only for Windows and macOS. Linux always goes through an installer.
        foreach (var platform in DepotPlatforms)
        {
            var build = await api.GetLatestBuildAsync(id, platform, ct);
            if (build is null) continue;

            var sizes = build.Manifest is null
                ? (0L, 0L)
                : await MeasureAsync(build, platform, owned, ct);

            // The DLC list is the same for every platform, so the first manifest wins.
            if (dlcs.Count == 0 && build.Manifest is not null)
                dlcs = await OwnedDlcsAsync(build, owned, ct);

            platforms[platform] = new PlatformAvailability
            {
                Depot = new BuildRef(build.BuildId, build.Generation, build.VersionName, build.Manifest),
                Installers = installers.GetValueOrDefault(platform, []),
                DownloadSize = sizes.Item1,
                InstalledSize = sizes.Item2,
            };
        }

        foreach (var (platform, list) in installers)
        {
            if (platforms.TryGetValue(platform, out var existing))
                platforms[platform] = existing with { Installers = list };
            else
                platforms[platform] = new PlatformAvailability { Installers = list };
        }

        return new LibraryEntry
        {
            Id = product.Id,
            Title = product.Title,
            Slug = string.IsNullOrEmpty(product.Slug) ? id.ToString() : product.Slug,
            Platforms = platforms,
            EarlyAccess = product.InDevelopment?.Active ?? false,
            Dlcs = dlcs,
        };
    }

    /// <summary>
    /// Bytes to download and bytes on disk, over exactly the depots a download would pull.
    /// Generation 1 has no compressed size, so both numbers are the unpacked one.
    /// </summary>
    private async Task<(long Download, long Installed)> MeasureAsync(
        Build build, GamePlatform platform, HashSet<long> owned, CancellationToken ct)
    {
        if (build.Generation == 1)
        {
            var v1 = await content.GetRepositoryManifestV1Async(build.Manifest!, ct);
            if (v1 is null) return (0, 0);

            var wanted = OwnedV1Products(v1, owned);
            var size = v1.Product.Depots
                .Where(d => !d.IsRedistributable
                            && d.GameIds.Any(wanted.Contains)
                            && d.MatchesLanguage(SizeLanguage))
                .Sum(d => d.Size);

            return (size, size);
        }

        var repository = await content.GetRepositoryManifestAsync(build.Manifest!, ct);
        if (repository is null) return (0, 0);

        var products = OwnedDlcs(repository, owned).Select(d => d.ProductId).ToHashSet();
        products.Add(repository.BaseProductId);

        var depots = repository.Depots
            .Where(d => products.Contains(d.ProductId) && d.MatchesLanguage(SizeLanguage))
            .ToList();

        return (depots.Sum(d => d.CompressedSize), depots.Sum(d => d.Size));
    }

    private async Task<List<DlcRef>> OwnedDlcsAsync(Build build, HashSet<long> owned, CancellationToken ct)
    {
        if (build.Generation == 1)
        {
            var v1 = await content.GetRepositoryManifestV1Async(build.Manifest!, ct);
            if (v1 is null) return [];

            // v1 has no products[], so a DLC is any owned id a depot names besides the root game.
            return OwnedV1Products(v1, owned)
                .Where(id => id != v1.Product.RootGameId)
                .Select(id => new DlcRef(id, id))
                .ToList();
        }

        var repository = await content.GetRepositoryManifestAsync(build.Manifest!, ct);
        return repository is null ? [] : OwnedDlcs(repository, owned);
    }

    private static HashSet<string> OwnedV1Products(RepositoryManifestV1 manifest, HashSet<long> owned)
    {
        var wanted = manifest.Product.Depots
            .Where(d => !d.IsRedistributable)
            .SelectMany(d => d.GameIds)
            .Distinct()
            .Where(id => long.TryParse(id, out var n) && owned.Contains(n))
            .ToHashSet();

        wanted.Add(manifest.Product.RootGameId);
        return wanted;
    }

    /// <summary>
    /// Owned DLC from `products[]` of the repository manifest — the same source the downloader
    /// uses, so a listing cannot disagree with what ends up in the zip.
    /// </summary>
    private static List<DlcRef> OwnedDlcs(RepositoryManifest repository, HashSet<long> owned) =>
        repository.Products
            .Where(p => p.ProductId != repository.BaseProductId
                        && long.TryParse(p.ProductId, out var n) && owned.Contains(n))
            .Select(p => new DlcRef(p.ProductId, p.Name))
            .ToList();
}
