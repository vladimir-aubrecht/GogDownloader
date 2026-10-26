using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameArchiver.Core;

public enum GameOs { Windows, Mac, Linux }
public enum ArchiveLayout { Flat, Provider }

public sealed record GameKey
{
    public string Source { get; }
    public string ProductId { get; }
    [JsonConstructor]
    public GameKey(string source, string productId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        Source = source.ToLowerInvariant();
        ProductId = productId;
    }
    public override string ToString() => $"{Source}:{ProductId}";
}

public sealed record GameInfo(GameKey Key, string Title, string Slug)
{
    public bool EarlyAccess { get; init; }
    public string Kind { get; init; } = "game";
    public Dictionary<GameOs, GameVersion> Versions { get; init; } = [];
    public List<string> OwnedDlcs { get; init; } = [];
    public HashSet<GameOs>? BackupPlatforms { get; init; }
    public bool CanBackUp(GameOs platform) => Versions.ContainsKey(platform) &&
        (BackupPlatforms?.Contains(platform) ?? Kind is "game" or "application" or "demo" or "pack");
}

public sealed record GameVersion(string BuildId)
{
    public int ContentRevision { get; init; } = 1;
    public string? Name { get; init; }
    public string Branch { get; init; } = "public";
    public string Language { get; init; } = "en";
    public bool BuildVariesByLanguage { get; init; }
    public string Architecture { get; init; } = "64";
    public Dictionary<string, string> Manifests { get; init; } = [];
    public List<string> Dlcs { get; init; } = [];
    /// <summary>Content provenance for this platform/language/architecture/branch selection.</summary>
    public List<GameDependency> Dependencies { get; init; } = [];
    public long DownloadSize { get; init; }
    /// <summary>Provider size-calculation revision; old Steam estimates can contain false zero-byte depots.</summary>
    public int SizeEstimateRevision { get; init; }
    public long InstalledSize { get; init; }

    internal bool IsValid => BuildId is not null && Branch is not null && Language is not null && Architecture is not null
        && Manifests is not null && Dlcs is not null && Dlcs.All(d => !string.IsNullOrWhiteSpace(d))
        && Dependencies is not null && Dependencies.All(d => d is not null && !string.IsNullOrWhiteSpace(d.ProductId));

    public bool Matches(GameVersion other) => IsValid && other.IsValid && ContentRevision == other.ContentRevision && BuildId == other.BuildId
        && Branch == other.Branch && Architecture == other.Architecture && string.Equals(Language, other.Language, StringComparison.OrdinalIgnoreCase)
        && Manifests.OrderBy(k => k.Key).SequenceEqual(other.Manifests.OrderBy(k => k.Key))
        && Dlcs.Order().SequenceEqual(other.Dlcs.Order());
}

public sealed record GameDependency(string ProductId, string Kind, string? DepotId = null, bool MetadataRequired = true);

/// <summary>A failed selection; IsStale marks retained evidence that this sync did not recheck.</summary>
public sealed record LibraryFailureImpact(string ProductId, GameOs? Platform = null, string Language = "en",
    string Architecture = "64", string Branch = "public", string? RequiredProductId = null, string? DepotId = null, string? Detail = null,
    bool IsStale = false);

public record GameSelection
{
    public GameOs Platform { get; init; } = GameOs.Windows;
    public string Language { get; init; } = "en";
    public string Branch { get; init; } = "public";
    public string Architecture { get; init; } = "64";
    public virtual void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Language);
        ArgumentException.ThrowIfNullOrWhiteSpace(Branch);
        if (!Enum.IsDefined(Platform) || Architecture is not "32" and not "64" and not "arm64")
            throw new ArgumentException("Invalid platform or architecture selection.");
    }
}

public sealed record ArchiveOptions : GameSelection
{
    public required string OutputRoot { get; init; }
    public ArchiveLayout Layout { get; init; } = ArchiveLayout.Flat;
    public Dictionary<string, string> ProviderDirectories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DirectoryName { get; init; }
    public string? StagingRoot { get; init; }
    public int MaxConnections { get; init; } = 5;
    public int CompressionThreads { get; init; } = Environment.ProcessorCount;
    public long MaxBytesPerSecond { get; init; }
    /// <summary>Skip a full download whose size is unknown or at/above this limit. Not a network-byte cap.</summary>
    public long? MaxDownloadSize { get; init; }
    public bool Force { get; init; }
    public bool Clean { get; init; }
    public bool Reuse { get; init; } = true;
    public bool Verify { get; init; } = true;
    public bool WriteLauncher { get; init; } = true;
    public string StagingDirectory => StagingRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gamearchive", "staging");
    public override void Validate()
    {
        base.Validate();
        if (MaxDownloadSize is <= 0) throw new ArgumentOutOfRangeException(nameof(MaxDownloadSize));
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputRoot);
        if (!Enum.IsDefined(Layout) || ProviderDirectories is null)
            throw new ArgumentException("Invalid archive layout.");
        if (Layout == ArchiveLayout.Flat && ProviderDirectories.Count > 0)
            throw new ArgumentException("Provider directories require the provider layout.");
        foreach (var (provider, directory) in ProviderDirectories)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            ArchivePaths.ValidateName(provider);
            ArchivePaths.ValidateName(directory);
        }
        if (ProviderDirectories.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ProviderDirectories.Count)
            throw new ArgumentException("Duplicate provider directory settings.");
        if (MaxConnections <= 0 || CompressionThreads <= 0 || MaxBytesPerSecond < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConnections), "Invalid platform, concurrency or bandwidth setting.");
        if (DirectoryName is not null) ArchivePaths.ValidateName(DirectoryName);
    }
}

public sealed record ArchiveProgress(string Phase, long Bytes = 0, long TotalBytes = 0, int Files = 0, int TotalFiles = 0);
public enum ArchiveOutcome { Completed, AlreadyDone, Unavailable, NotEnoughSpace, Incomplete, SkippedSize }
public sealed record ArchiveResult(GameKey Key, ArchiveOutcome Outcome, string? ArchivePath = null, string? Detail = null);
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
public sealed record StoreLibraryResult(IReadOnlyList<GameInfo> Games)
{
    /// <summary>Null leaves membership unchanged, or uses legacy selection if absent. Empty means an explicitly empty catalog.</summary>
    public IReadOnlySet<string>? PrimaryCatalog { get; init; }
    /// <summary>Products whose download selections were not evaluated, including failures before selection began.</summary>
    public IReadOnlySet<string> MetadataOnlyProducts { get; init; } = new HashSet<string>();
    public IReadOnlyDictionary<string, List<LibraryFailureImpact>> FailureImpacts { get; init; } = new Dictionary<string, List<LibraryFailureImpact>>();
    public IReadOnlyDictionary<string, string> Failures { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> FailureNames { get; init; } = new Dictionary<string, string>();
    /// <summary>Optional display summaries; Failures retains full diagnostics and determines failure status.</summary>
    public IReadOnlyDictionary<string, string> FailureSummaries { get; init; } = new Dictionary<string, string>();
}

/// <summary>Each implementation is independently usable and owns its authenticated session.</summary>
public interface IGameStore : IAsyncDisposable
{
    string Source { get; }
    Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default);
    Task<GameVersion> GetVersionAsync(string productId, GameSelection options, CancellationToken ct = default);
    Task<ArchiveResult> DownloadAsync(string productId, ArchiveOptions options,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default);
}

public sealed record ArchiveLibrary
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset SyncedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<GameInfo> Games { get; init; } = [];
    public Dictionary<string, string> Failures { get; init; } = [];
    public Dictionary<string, string> FailureNames { get; init; } = [];
    /// <summary>Optional display summaries keyed by source:product ID. Full diagnostics remain in Failures.</summary>
    public Dictionary<string, string> FailureSummaries { get; init; } = [];
    public Dictionary<string, HashSet<string>> PrimaryCatalogs { get; init; } = [];
    public Dictionary<string, List<LibraryFailureImpact>> FailureImpacts { get; init; } = [];

    public bool IsCatalogEntry(GameKey key) => !PrimaryCatalogs.TryGetValue(key.Source, out var catalog) || catalog.Contains(key.ProductId);

    /// <summary>Resolve filtered DLC selections to their archivable parents for this OS.</summary>
    public IReadOnlyList<GameInfo> SelectDownloads(IEnumerable<GameInfo> selected, GameOs platform)
    {
        var result = new Dictionary<GameKey, GameInfo>();
        foreach (var game in selected)
        {
            var parents = Games.Where(parent => parent.Key.Source == game.Key.Source && parent.Key != game.Key
                && IsCatalogEntry(parent.Key) && parent.CanBackUp(platform) && parent.Versions[platform].Dlcs.Contains(game.Key.ProductId)).ToList();
            if (parents.Count > 0)
                foreach (var parent in parents) result.TryAdd(parent.Key, parent);
            else if (IsCatalogEntry(game.Key) && game.CanBackUp(platform)) result.TryAdd(game.Key, game);
        }
        return result.Values.ToList();
    }
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "gamearchive", "library.json");
    public Task SaveAsync(string path, CancellationToken ct = default) => ArchiveJson.SaveAsync(path, this, ct);
    public static async Task<ArchiveLibrary> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var library = await JsonSerializer.DeserializeAsync<ArchiveLibrary>(stream, ArchiveJson.Options, ct)
            ?? throw new InvalidDataException("Empty library snapshot.");
        if (library.SchemaVersion != 1) throw new InvalidDataException("Unsupported library snapshot schema.");
        if (library.Games is null || library.Failures is null || library.FailureNames is null || library.FailureSummaries is null
            || library.PrimaryCatalogs is null || library.PrimaryCatalogs.Any(c => string.IsNullOrWhiteSpace(c.Key)
                || c.Value is null || c.Value.Any(string.IsNullOrWhiteSpace))
            || library.FailureImpacts is null || library.FailureImpacts.Any(f => f.Value is null
                || f.Value.Any(i => i is null || string.IsNullOrWhiteSpace(i.ProductId) || i.Platform is { } p && !Enum.IsDefined(p)))
            || library.Games.Any(g => g is null || g.Key is null
            || g.Versions is null || g.Versions.Any(v => !Enum.IsDefined(v.Key) || v.Value is null || !v.Value.IsValid)
            || g.OwnedDlcs is null || g.Title is null || g.Slug is null)
            || library.Games.GroupBy(g => g.Key).Any(g => g.Count() > 1))
            throw new InvalidDataException("Library snapshot contains missing or duplicate identities.");
        return library;
    }
}

public static class ArchiveJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    public static async Task SaveAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, value, Options, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, path, overwrite: true);
        }
        finally { File.Delete(tmp); }
    }
}
