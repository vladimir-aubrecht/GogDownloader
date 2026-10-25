using System.Text.Json;
using System.Text.Json.Serialization;
using GogArchiver.Core.Library;

namespace GogArchiver.Core.Download;

public sealed record BackedUpFile(string Md5, long Size);

/// <summary>One platform of one game: one zip.</summary>
public sealed record PlatformBackup
{
    public const int CurrentContentRevision = 1;

    /// <summary>
    /// Revision 1 applies DLC file overrides and binds installer caches to their source.
    /// Older DLC/Windows installer backups may contain wrong bytes despite valid ZIP checksums.
    /// </summary>
    public int ContentRevision { get; init; }

    public bool RequiresContentRefresh(GamePlatform platform) => ContentRevision < CurrentContentRevision
        && (Dlcs.Count > 0 || (platform == GamePlatform.Windows
            && BuildId.StartsWith("installer-", StringComparison.Ordinal)));

    public bool HasArchive(string gameDirectory)
    {
        var archive = new FileInfo(Path.Combine(gameDirectory, ArchiveName));
        return archive.Exists && archive.Length == ArchiveSize;
    }

    public bool ContainsDlcs(IEnumerable<string> expected) => expected.All(Dlcs.Contains);

    public bool Matches(string gameDirectory, string buildId, string language, IEnumerable<string> dlcs, GamePlatform platform) =>
        BuildId == buildId
        && string.Equals(Language, language, StringComparison.OrdinalIgnoreCase)
        && ContainsDlcs(dlcs)
        && !RequiresContentRefresh(platform)
        && HasArchive(gameDirectory);

    public required string BuildId { get; init; }
    public string? VersionName { get; init; }
    public required string Language { get; init; }

    public required string ArchiveName { get; init; }
    public required long ArchiveSize { get; init; }
    public required long InstalledSize { get; init; }

    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>Owned DLC whose depots are in the zip.</summary>
    public List<string> Dlcs { get; init; } = [];

    /// <summary>Redistributables the game asked for and we deliberately skipped.</summary>
    public List<string> SkippedDependencies { get; init; } = [];

    /// <summary>
    /// The executable to launch, from the depot's own metadata, as a path inside the archive.
    /// Null when the game shipped no launch metadata. Fed to GameVault by `gamevault sync`.
    /// </summary>
    public string? LaunchExecutable { get; init; }

    /// <summary>Default launch arguments, if the metadata gave any.</summary>
    public string? LaunchParameters { get; init; }

    /// <summary>Path inside the zip to hash and size. The basis for an incremental patch.</summary>
    public Dictionary<string, BackedUpFile> Files { get; init; } = [];
}

/// <summary>
/// `.gogdl.json` in the game directory. Initially reserves the directory for GameId; a
/// platform entry is committed only after its archive is published and counts as downloaded.
/// </summary>
public sealed record BackupManifest
{
    public const string FileName = ".gogdl.json";
    public const string GogSource = "gog";

    public int SchemaVersion { get; init; } = 1;

    /// <summary>Store/origin, not a temporary CDN URL. Missing in legacy GOG manifests.</summary>
    public string Source { get; init; } = GogSource;

    [JsonIgnore]
    internal bool NeedsSourceUpgrade { get; init; }

    public required long GameId { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }

    public Dictionary<GamePlatform, PlatformBackup> Platforms { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathFor(string gameDirectory) => Path.Combine(gameDirectory, FileName);

    /// <summary>Exclusively claim a directory while its root reservation is held.</summary>
    internal async Task CreateAsync(string gameDirectory, CancellationToken ct)
    {
        await GameArchiver.Core.ArchiveReservation.CreateOwnerAsync(PathFor(gameDirectory), this, Json, ct);
    }

    public Task SaveAsync(string gameDirectory, CancellationToken ct = default) =>
        GameArchiver.Core.ArchiveJson.SaveAsync(PathFor(gameDirectory), this, ct);

    /// <summary>Load GOG metadata only; unmarked directories and explicit foreign sources return null.</summary>
    public static async Task<BackupManifest?> LoadAsync(string gameDirectory, CancellationToken ct = default) =>
        (await InspectAsync(gameDirectory, ct)).Manifest;

    internal static async Task<(string? Source, BackupManifest? Manifest)> InspectAsync(
        string gameDirectory, CancellationToken ct)
    {
        var path = PathFor(gameDirectory);
        if (!File.Exists(path)) return (null, null);
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Backup metadata must be a JSON object.");

        // Inspect the origin before interpreting any GOG-specific fields. Another store can
        // use a string ID or a different platform format, neither of which is ours to parse.
        var source = GogSource;
        var explicitSource = false;
        foreach (var property in document.RootElement.EnumerateObject())
            if (string.Equals(property.Name, nameof(Source), StringComparison.OrdinalIgnoreCase))
            {
                explicitSource = true;
                source = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : "";
            }
        if (!string.Equals(source, GogSource, StringComparison.OrdinalIgnoreCase)) return (source, null);

        var manifest = document.RootElement.Deserialize<BackupManifest>(Json)!;
        if (File.Exists(System.IO.Path.Combine(gameDirectory, GameArchiver.Core.ArchiveMetadata.FileName)))
            await GameArchiver.Core.ArchiveMetadata.AssertOwnerAsync(gameDirectory, new("gog", manifest.GameId.ToString()), ct);
        if (manifest.Platforms is null || manifest.Platforms.Values.Any(b => b is null))
            throw new InvalidDataException("Invalid backup platform metadata.");
        foreach (var backup in manifest.Platforms.Values)
            _ = GameArchiver.Core.ArchivePaths.Within(gameDirectory, backup.ArchiveName);
        return (source, manifest with { NeedsSourceUpgrade = !explicitSource });
    }
}
