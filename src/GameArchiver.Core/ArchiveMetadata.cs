using System.Text.Json;

namespace GameArchiver.Core;

public sealed record ArchivedFile(string Hash, long Size, string Algorithm = "sha1");
public sealed record PlatformArchive
{
    public required GameVersion Version { get; init; }
    public required string ArchiveName { get; init; }
    public required long ArchiveSize { get; init; }
    public long InstalledSize { get; init; }
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ModifiedAt { get; init; }
    public bool NeedsRefresh { get; init; }
    public string RuntimeRequirement { get; init; } = "unknown";
    public DrmInfo Drm { get; init; } = new();
    public string? LaunchExecutable { get; init; }
    public string? LaunchParameters { get; init; }
    public Dictionary<string, ArchivedFile> Files { get; init; } = [];
    public bool HasArchive(string directory)
    {
        var file = new FileInfo(ArchivePaths.Within(directory, ArchiveName));
        return file.Exists && file.Length == ArchiveSize;
    }
}

public sealed record ArchiveMetadata
{
    public const string FileName = ".gamebackup.json";
    public const string LegacyFileName = ".gogdl.json";
    public int SchemaVersion { get; init; } = 2;
    public required string Source { get; init; }
    public required string ProductId { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public Dictionary<GameOs, PlatformArchive> Platforms { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public GameKey Key => new(Source, ProductId);

    /// <summary>The authoritative file selected by LoadAsync; null for newly constructed metadata.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LoadedFileName { get; private init; }

    public Task SaveAsync(string directory, CancellationToken ct = default) =>
        ArchiveJson.SaveAsync(Path.Combine(directory, FileName), this, ct);

    public static async Task<ArchiveMetadata?> LoadAsync(string directory, CancellationToken ct = default,
        IReadOnlySet<string>? sources = null)
    {
        if (File.Exists(Path.Combine(directory, FileName)) && File.Exists(Path.Combine(directory, LegacyFileName)))
            _ = await LoadIdentityAsync(directory, ct); // Check ownership before applying a source filter.
        var modern = await ReadAsync(Path.Combine(directory, FileName), false, sources, ct);
        var legacy = await ReadAsync(Path.Combine(directory, LegacyFileName), true, sources, ct);
        if (modern is not null && legacy is not null && modern.Key != legacy.Key)
            throw new ArchiveConflictException($"Conflicting metadata owners in '{directory}'.");
        // The original GOG CLI may update its manifest independently of the aggregator.
        if (legacy is not null) return legacy with { LoadedFileName = LegacyFileName };
        return modern is null ? null : modern with { LoadedFileName = FileName };
    }

    /// <summary>Ownership lookup does not interpret another provider's platform schema.</summary>
    internal static async Task<ArchiveMetadata?> LoadIdentityAsync(string directory, CancellationToken ct)
    {
        ArchiveMetadata? owner = null;
        foreach (var file in new[] { FileName, LegacyFileName })
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path)) continue;
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
            var values = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var legacy = file == LegacyFileName;
            var source = values.TryGetValue("Source", out var s) ? s.ValueKind == JsonValueKind.String ? s.GetString()?.ToLowerInvariant() : null
                : legacy ? "gog" : null;
            if (string.IsNullOrEmpty(source) || legacy && source != "gog") continue;
            var id = legacy && values.TryGetValue("GameId", out var gameId) && gameId.TryGetInt64(out var number) ? number.ToString()
                : !legacy && values.TryGetValue("ProductId", out var product) && product.ValueKind == JsonValueKind.String ? product.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var candidate = new ArchiveMetadata { Source = source, ProductId = id, Slug = Path.GetFileName(directory), Title = Path.GetFileName(directory) };
            if (owner is not null && owner.Key != candidate.Key) throw new ArchiveConflictException($"Conflicting metadata owners in '{directory}'.");
            owner = candidate;
        }
        return owner;
    }

    public static async Task AssertOwnerAsync(string directory, GameKey expected, CancellationToken ct = default)
    {
        if ((await LoadIdentityAsync(directory, ct))?.Key != expected)
            throw new ArchiveConflictException($"Backup ownership changed in '{directory}'.");
    }

    public static async Task<GameKey?> GetOwnerAsync(string directory, CancellationToken ct = default) =>
        (await LoadIdentityAsync(directory, ct))?.Key;

    private static async Task<ArchiveMetadata?> ReadAsync(string path, bool legacy, IReadOnlySet<string>? sources, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Invalid metadata: {path}");
        var properties = root.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var source = properties.TryGetValue("Source", out var rawSource)
            ? rawSource.ValueKind == JsonValueKind.String ? rawSource.GetString()?.ToLowerInvariant() : null
            : legacy ? "gog" : null;
        // .gogdl.json with a different source belongs to an external producer, not this schema.
        if (legacy && source != "gog" || !legacy && string.IsNullOrWhiteSpace(source)) return null;
        if (sources is not null && !sources.Contains(source!)) return null;
        if (!legacy)
        {
            var result = root.Deserialize<ArchiveMetadata>(ArchiveJson.Options)!;
            if (result.SchemaVersion != 2) throw new InvalidDataException($"Unsupported archive schema in {path}");
            if (result.Platforms is null || result.Platforms.Any(p => !Enum.IsDefined(p.Key) || p.Value is null
                || p.Value.Version is null || !p.Value.Version.IsValid || p.Value.Files is null || p.Value.Drm is null || !p.Value.Drm.IsValid
                || p.Value.Files.Values.Any(f => f is null || f.Hash is null || f.Algorithm is null)
                || p.Value.ArchiveSize < 0 || string.IsNullOrWhiteSpace(p.Value.ArchiveName)))
                throw new InvalidDataException($"Invalid platform metadata in {path}");
            foreach (var backup in result.Platforms.Values) _ = ArchivePaths.Within(Path.GetDirectoryName(path)!, backup.ArchiveName);
            if (source == "steam")
                foreach (var (platform, backup) in result.Platforms.ToArray())
                    if (platform != GameOs.Windows && backup.Version.ContentRevision < 2)
                        result.Platforms[platform] = backup with { NeedsRefresh = true };
            _ = result.Key;
            return result with { Source = source! };
        }
        string Text(JsonElement obj, string name, string fallback = "") =>
            obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : fallback;
        var id = properties["GameId"].GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var metadata = new ArchiveMetadata { Source = "gog", ProductId = id,
            Slug = properties["Slug"].GetString()!, Title = properties["Title"].GetString()! };
        if (!properties.TryGetValue("Platforms", out var platforms)) return metadata;
        foreach (var p in platforms.EnumerateObject())
        {
            if (!Enum.TryParse<GameOs>(p.Name, true, out var platform)) continue;
            var v = p.Value;
            var dlcs = v.TryGetProperty("Dlcs", out var ds) ? ds.EnumerateArray().Select(d => d.GetString()!).ToList() : [];
            var build = Text(v, "BuildId");
            var contentRevision = v.TryGetProperty("ContentRevision", out var revision) ? revision.GetInt32() : 0;
            var oldContent = contentRevision < 1;
            var reportedDrm = v.TryGetProperty("Drm", out var drm) ? drm.Deserialize<DrmInfo>(ArchiveJson.Options) ?? DrmInfo.ForStore("gog") : DrmInfo.ForStore("gog");
            if (!reportedDrm.IsValid) throw new InvalidDataException($"Invalid DRM metadata in {path}");
            metadata.Platforms[platform] = new()
            {
                Version = new(build) { Name = Text(v, "VersionName"), Language = Text(v, "Language", "en"), Architecture = "",
                    BuildVariesByLanguage = build.StartsWith("installer-", StringComparison.Ordinal), Dlcs = dlcs,
                    ContentRevision = platform == GameOs.Windows ? 1 : Math.Max(1, contentRevision) },
                ArchiveName = Text(v, "ArchiveName"), ArchiveSize = v.GetProperty("ArchiveSize").GetInt64(),
                InstalledSize = v.GetProperty("InstalledSize").GetInt64(),
                CompletedAt = v.TryGetProperty("CompletedAt", out var time) ? time.GetDateTimeOffset() : DateTimeOffset.MinValue,
                ModifiedAt = v.TryGetProperty("ModifiedAt", out var modified) && modified.ValueKind != JsonValueKind.Null ? modified.GetDateTimeOffset() : null,
                NeedsRefresh = platform != GameOs.Windows && contentRevision < 2 || oldContent && (dlcs.Count > 0 || platform == GameOs.Windows && build.StartsWith("installer-")),
                RuntimeRequirement = "drm-free", LaunchExecutable = Text(v, "LaunchExecutable"), LaunchParameters = Text(v, "LaunchParameters"),
                Drm = reportedDrm,
                Files = v.TryGetProperty("Files", out var files) ? files.EnumerateObject().ToDictionary(f => f.Name,
                    f => new ArchivedFile(Text(f.Value, "Md5"), f.Value.GetProperty("Size").GetInt64(), "md5")) : [],
            };
        }
        return metadata;
    }
}

public sealed record ArchiveLocation(string Directory, ArchiveMetadata Metadata);

public static class ArchiveCatalog
{
    public static async Task<List<ArchiveLocation>> ScanAsync(string root, CancellationToken ct = default,
        IReadOnlySet<string>? sources = null)
    {
        var result = new List<ArchiveLocation>();
        if (!Directory.Exists(root)) return result;
        foreach (var path in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            if (await ArchiveMetadata.LoadAsync(path, ct, sources) is { } metadata) result.Add(new(path, metadata));
        }
        return result;
    }

    public static async Task<string> ResolveNameAsync(GameInfo game, ArchiveOptions options, CancellationToken ct)
    {
        options = ProviderOutput.Resolve(options, game.Key.Source);
        var entries = Directory.Exists(options.OutputRoot) ? Directory.EnumerateFileSystemEntries(options.OutputRoot).ToList() : [];
        string? FindUniqueEntry(string name)
        {
            var matches = entries.Where(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1) throw new ArchiveConflictException($"Multiple entries use directory name '{name}'.");
            return matches.SingleOrDefault();
        }
        var catalog = new List<ArchiveLocation>();
        foreach (var directory in entries.Where(Directory.Exists))
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0
                && await ArchiveMetadata.LoadIdentityAsync(directory, ct) is { } owner)
                catalog.Add(new(directory, owner));
        var owned = catalog.Where(c => c.Metadata.Key == game.Key).ToList();
        if (owned.Count > 1) throw new ArchiveConflictException($"Multiple directories belong to {game.Key}.");
        if (owned.Count == 1)
        {
            var name = Path.GetFileName(owned[0].Directory);
            _ = FindUniqueEntry(name);
            if (game.Key.Source == "gog" && string.Equals(name, $"{game.Slug}__{game.Key.ProductId}", StringComparison.OrdinalIgnoreCase))
                name = game.Slug.ToLowerInvariant();
            if (options.DirectoryName is not null && !string.Equals(name, options.DirectoryName, StringComparison.Ordinal))
                throw new ArchiveConflictException($"{game.Key} already uses directory '{name}'.");
            return name;
        }
        var candidate = options.DirectoryName ?? game.Slug.ToLowerInvariant();
        ArchivePaths.ValidateName(candidate);
        var occupied = FindUniqueEntry(candidate);
        if (occupied is not null)
        {
            var owner = catalog.FirstOrDefault(c => c.Directory == occupied)?.Metadata.Key;
            if (owner is not null && owner.Source != game.Key.Source && options.DirectoryName is null)
                candidate += "_" + game.Key.Source;
            else if (!Directory.Exists(occupied) || Directory.EnumerateFileSystemEntries(occupied).Any())
                throw new ArchiveConflictException($"Directory '{occupied}' is not available for {game.Key}. Choose --directory.");
        }
        ArchivePaths.ValidateName(candidate);
        var alias = FindUniqueEntry(candidate);
        if (alias is not null && (!Directory.Exists(alias) || Directory.EnumerateFileSystemEntries(alias).Any()))
            throw new ArchiveConflictException($"Directory '{alias}' is not available for {game.Key}.");
        return alias is null ? candidate : Path.GetFileName(alias);
    }

    public static async Task<ArchiveLocation> ReserveAsync(GameInfo game, ArchiveOptions options, CancellationToken ct)
    {
        options = await ProviderOutput.PrepareAsync(options, game.Key.Source, ct);
        using var reservation = await ArchiveReservation.AcquireAsync(options.OutputRoot, ct);
        var name = await ResolveNameAsync(game, options, ct);
        var path = Path.Combine(options.OutputRoot, name);
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArchiveConflictException($"Backup directory is a link: {path}");
        var metadata = await ArchiveMetadata.LoadAsync(path, ct, new HashSet<string> { game.Key.Source });
        if (metadata is null)
        {
            if (Directory.EnumerateFileSystemEntries(path).Any())
                throw new ArchiveConflictException($"Unidentified files occupy '{path}'.");
            var claim = new ArchiveMetadata { Source = game.Key.Source, ProductId = game.Key.ProductId,
                Slug = game.Slug, Title = game.Title };
            var destination = Path.Combine(path, ArchiveMetadata.FileName);
            await ArchiveReservation.CreateOwnerAsync(destination, claim, ArchiveJson.Options, ct);
            metadata = await ArchiveMetadata.LoadAsync(path, ct);
        }
        if (metadata?.Key != game.Key) throw new ArchiveConflictException($"Directory '{path}' belongs to a different product.");
        return new(path, metadata);
    }

    public static FileStream Acquire(string directory) => new(Path.Combine(directory, ".archive.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public static async Task<ArchiveLease> AcquireOwnedAsync(ArchiveLocation location, GameKey expected, CancellationToken ct)
    {
        var stream = Acquire(location.Directory);
        try
        {
            // Reservation metadata can be stale if another writer finishes before this lease.
            var current = await ArchiveMetadata.LoadAsync(location.Directory, ct);
            if (current?.Key != expected) throw new ArchiveConflictException($"Backup ownership changed in '{location.Directory}'.");
            return new(stream, new(location.Directory, current));
        }
        catch { stream.Dispose(); throw; }
    }
}

public sealed class ArchiveLease : IDisposable
{
    private readonly FileStream _stream;
    internal ArchiveLease(FileStream stream, ArchiveLocation location) { _stream = stream; Location = location; }
    public ArchiveLocation Location { get; }
    public void Dispose() => _stream.Dispose();
}
