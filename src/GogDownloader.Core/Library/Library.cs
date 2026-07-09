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

    public bool HasDepot => Depot is not null;
    public bool HasInstaller => Installers.Count > 0;
    public bool Unavailable => !HasDepot && !HasInstaller;

    public long LargestInstallerSize => Installers.Count == 0 ? 0 : Installers.Max(i => i.TotalSize);
}

public sealed record DlcRef(string ProductId, string Name);

public sealed record LibraryEntry
{
    public required long Id { get; init; }
    public required string Title { get; init; }
    public required string Slug { get; init; }

    public Dictionary<GamePlatform, PlatformAvailability> Platforms { get; init; } = [];

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
    public const int CurrentSchema = 2;

    /// <summary>Absent in snapshots written before schema versioning, hence 0.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Schema 2 added the DLC list; older snapshots would report no DLC at all.</summary>
    public bool KnowsDlcs => SchemaVersion >= 2;

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

        // Depots exist only for Windows and macOS. Linux always goes through an installer.
        foreach (var platform in DepotPlatforms)
        {
            var build = await api.GetLatestBuildAsync(id, platform, ct);
            if (build is null) continue;
            platforms[platform] = new PlatformAvailability
            {
                Depot = new BuildRef(build.BuildId, build.Generation, build.VersionName, build.Manifest),
                Installers = installers.GetValueOrDefault(platform, []),
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
            Dlcs = await OwnedDlcsAsync(platforms, owned, ct),
        };
    }

    /// <summary>
    /// The DLC list lives in `products[]` of the repository manifest, so it exists only for
    /// games that have a depot. Any depot platform will do — the product list is the same.
    /// </summary>
    private async Task<List<DlcRef>> OwnedDlcsAsync(
        Dictionary<GamePlatform, PlatformAvailability> platforms, HashSet<long> owned, CancellationToken ct)
    {
        var manifestUrl = DepotPlatforms
            .Select(p => platforms.GetValueOrDefault(p)?.Depot?.Manifest)
            .FirstOrDefault(url => url is not null);

        if (manifestUrl is null) return [];

        var repository = await content.GetRepositoryManifestAsync(manifestUrl, ct);
        if (repository is null) return [];

        return repository.Products
            .Where(p => p.ProductId != repository.BaseProductId
                        && long.TryParse(p.ProductId, out var n) && owned.Contains(n))
            .Select(p => new DlcRef(p.ProductId, p.Name))
            .ToList();
    }
}
