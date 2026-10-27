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
    public GameVersion? TestedVersion { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Enum.IsDefined(Status) && !string.IsNullOrWhiteSpace(Source) && !string.IsNullOrWhiteSpace(Branch)
        && (TestedVersion is null || TestedVersion.IsValid);
    public DrmInfo ForVersion(GameVersion version) => TestedVersion is null || TestedVersion.Matches(version) ? this
        : new() { Branch = version.Branch, Notes = "Local verification belongs to different content; this version has not been tested." };
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
        => GetCore(library, key, platform, branch, null, null);

    public static DrmInfo Get(ArchiveLibrary library, GameKey key, GameSelection selection)
        => GetCore(library, key, selection.Platform, selection.Branch, selection.Language, selection.Architecture);

    private static DrmInfo GetCore(ArchiveLibrary library, GameKey key, GameOs platform, string branch, string? language, string? architecture)
    {
        var game = library.Games.FirstOrDefault(g => g.Key == key);
        if (game is null || !game.Versions.ContainsKey(platform)) return new() { Branch = branch, Notes = "No saved product version for this platform." };
        var local = library.DrmTests.Where(t => t.Key == key && t.Platform == platform && t.AppliesToCatalog
                && t.Version.Branch == branch && t.Version.Matches(game.Versions[platform])
                && (language is null || t.Version.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
                && (architecture is null || t.Version.Architecture.Length == 0 || t.Version.Architecture == architecture))
            .OrderByDescending(t => t.TestedAt).FirstOrDefault();
        var info = library.DrmOverrides.GetValueOrDefault(key.ToString())?.GetValueOrDefault(platform)
            ?? (local is not null && local.Classification.Status != DrmStatus.Unknown ? local.Classification : null)
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
