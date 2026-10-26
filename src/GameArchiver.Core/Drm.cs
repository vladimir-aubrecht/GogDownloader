namespace GameArchiver.Core;

public enum DrmStatus { Unknown, Free, Protected }

/// <summary>A reported DRM classification, not a guarantee of offline play or portability.</summary>
public sealed record DrmInfo
{
    public DrmStatus Status { get; init; }
    public string Source { get; init; } = "unknown";
    public string Branch { get; init; } = "public";
    public string? Url { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Enum.IsDefined(Status) && !string.IsNullOrWhiteSpace(Source) && !string.IsNullOrWhiteSpace(Branch);
    public static DrmInfo ForStore(string source) => source == "gog"
        ? new() { Status = DrmStatus.Free, Source = "gog-distribution-policy", Url = "https://www.gog.com/", Notes = "GOG distribution; not a per-build offline-play test." }
        : new();
}

public interface IDrmSource
{
    string Name { get; }
    Task<IReadOnlyDictionary<GameOs, DrmInfo>> GetAsync(GameKey key, CancellationToken ct = default);
}

public static class DrmCatalog
{
    public static DrmInfo Get(ArchiveLibrary library, GameKey key, GameOs platform, string branch = "public")
    {
        var game = library.Games.FirstOrDefault(g => g.Key == key);
        if (game is null || !game.Versions.ContainsKey(platform)) return new() { Branch = branch, Notes = "No saved product version for this platform." };
        var info = library.DrmOverrides.GetValueOrDefault(key.ToString())?.GetValueOrDefault(platform)
            ?? game.Drm.GetValueOrDefault(platform)
            ?? DrmInfo.ForStore(key.Source);
        return info.Branch == branch ? info : new() { Branch = branch, Notes = "No DRM classification for this branch." };
    }

    public static DrmStatus Parse(string value) => value.ToLowerInvariant() switch
    {
        "free" => DrmStatus.Free, "protected" => DrmStatus.Protected, "unknown" => DrmStatus.Unknown,
        _ => throw new ArgumentException("Use DRM status free, protected or unknown."),
    };
}
