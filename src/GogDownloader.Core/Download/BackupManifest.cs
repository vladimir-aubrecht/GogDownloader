using System.Text.Json;
using System.Text.Json.Serialization;
using GogDownloader.Core.Library;

namespace GogDownloader.Core.Download;

public sealed record BackedUpFile(string Md5, long Size);

/// <summary>One platform of one game: one zip.</summary>
public sealed record PlatformBackup
{
    public required string BuildId { get; init; }
    public string? VersionName { get; init; }
    public required string Language { get; init; }

    public required string ArchiveName { get; init; }
    public required long ArchiveSize { get; init; }
    public required long InstalledSize { get; init; }

    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Owned DLC whose depots are in the zip.</summary>
    public List<string> Dlcs { get; init; } = [];

    /// <summary>Redistributables the game asked for and we deliberately skipped.</summary>
    public List<string> SkippedDependencies { get; init; } = [];

    /// <summary>Path inside the zip to hash and size. The basis for an incremental patch.</summary>
    public Dictionary<string, BackedUpFile> Files { get; init; } = [];
}

/// <summary>
/// `.gogdl.json` in the game directory. Written **as the very last step** — until it has
/// an entry for a platform, that platform counts as not downloaded and a restart redoes it.
/// </summary>
public sealed record BackupManifest
{
    public const string FileName = ".gogdl.json";

    public int SchemaVersion { get; init; } = 1;

    public required long GameId { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }

    public Dictionary<GamePlatform, PlatformBackup> Platforms { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathFor(string gameDirectory) => Path.Combine(gameDirectory, FileName);

    public async Task SaveAsync(string gameDirectory, CancellationToken ct = default)
    {
        var path = PathFor(gameDirectory);
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, this, Json, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task<BackupManifest?> LoadAsync(string gameDirectory, CancellationToken ct = default)
    {
        var path = PathFor(gameDirectory);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, Json, ct);
    }
}
