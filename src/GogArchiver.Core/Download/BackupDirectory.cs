using System.Text.Json;
using GogArchiver.Core.Library;

namespace GogArchiver.Core.Download;

public sealed class BackupDirectoryConflictException(string message) : IOException(message);

/// <summary>Slug-only backups, with ownership checked before reuse or migration.</summary>
public static class BackupDirectory
{
    public static void ValidateSelection(IEnumerable<LibraryEntry> library, IEnumerable<LibraryEntry> selected)
    {
        var games = selected.ToList();
        foreach (var game in games) ValidateName(game);
        var ids = games.Select(g => g.Id).ToHashSet();
        foreach (var group in library.GroupBy(g => g.DirectoryName, StringComparer.OrdinalIgnoreCase))
        {
            var owners = group.DistinctBy(g => g.Id).ToList();
            if (owners.Count > 1 && owners.Any(g => ids.Contains(g.Id)))
                throw new BackupDirectoryConflictException(
                    $"Directory name collision for '{group.Key}': " +
                    string.Join(", ", owners.Select(g => $"{g.Title} (GOG {g.Id})")) +
                    ". No backups were changed. Refresh the library snapshot or resolve the conflicting slugs.");
        }
    }

    public static Task<(string Directory, BackupManifest? Manifest)> FindAsync(
        string root, LibraryEntry game, CancellationToken ct = default) => ResolveAsync(root, game, false, ct);

    public static Task<(string Directory, BackupManifest? Manifest)> PrepareAsync(
        string root, LibraryEntry game, CancellationToken ct = default) => ResolveAsync(root, game, true, ct);

    internal static Task<(string Directory, BackupManifest? Manifest)> PrepareAsync(
        string root, LibraryEntry game, string? directoryName, CancellationToken ct) => ResolveAsync(root, game, true, ct, directoryName);

    private static async Task<(string Directory, BackupManifest? Manifest)> ResolveAsync(
        string root, LibraryEntry game, bool prepare, CancellationToken ct, string? directoryName = null)
    {
        ValidateName(game);
        using var reservation = prepare
            ? await GameArchiver.Core.ArchiveReservation.AcquireAsync(root, ct) : null;
        var entries = Directory.Exists(root) ? Directory.EnumerateFileSystemEntries(root).ToList() : [];
        var owned = new List<string>();
        foreach (var path in entries.Where(Directory.Exists))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            // Find aliases by identity, including names chosen through the aggregate CLI.
            // Invalid unrelated metadata must not authorize adoption of its directory.
            try
            {
                if (await GameArchiver.Core.ArchiveMetadata.GetOwnerAsync(path, ct) == new GameArchiver.Core.GameKey("gog", game.Id.ToString()))
                    owned.Add(path);
            }
            catch (Exception e) when (e is JsonException or InvalidDataException) { }
        }
        if (owned.Count > 1) throw Conflict(root, "multiple directories identify the same GOG product");
        var existingName = owned.Count == 1 ? Path.GetFileName(owned[0]) : null;
        var legacy = string.Equals(existingName, game.LegacyDirectoryName, StringComparison.OrdinalIgnoreCase);
        if (existingName is not null && !legacy && directoryName is not null && directoryName != existingName)
            throw Conflict(owned[0], "an explicit alias cannot rename an existing backup");
        var nameToUse = existingName is not null && !legacy ? existingName : directoryName ?? game.DirectoryName;
        if (existingName is null) GameArchiver.Core.ArchivePaths.ValidateName(nameToUse);
        var target = Path.Combine(root, nameToUse);
        var candidates = new List<(string Path, BackupManifest? Manifest, bool Empty)>();
        // Inspect case variants even on Linux: a backup must also be safe on Windows/NAS shares.
        foreach (var path in entries)
            {
                var name = Path.GetFileName(path);
                var matches = string.Equals(name, nameToUse, StringComparison.OrdinalIgnoreCase)
                    || (existingName is null || legacy) && string.Equals(name, game.LegacyDirectoryName, StringComparison.OrdinalIgnoreCase);
                if (!matches) continue;
                if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw Conflict(path, "the name is occupied by a file or a link");
                var manifest = await ReadOwnerAsync(path, game, ct);
                candidates.Add((path, manifest, !Directory.EnumerateFileSystemEntries(path).Any()));
            }

        var populated = candidates.Where(c => !c.Empty).ToList();
        if (populated.Count > 1)
            throw Conflict(target, "multiple backup directories exist: " + string.Join(", ", populated.Select(c => c.Path)));
        var source = populated.Count == 1 ? populated[0]
            : candidates.OrderByDescending(c => c.Path == target).FirstOrDefault();
        if (!prepare) return (source.Path ?? target, source.Manifest);

        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(root);
        // Only empty alternatives can be removed. Delete is non-recursive, so a concurrent
        // writer making one nonempty makes this fail safely instead of losing its files.
        foreach (var candidate in candidates.Where(c => c.Empty && c.Path != source.Path))
            Directory.Delete(candidate.Path);
        if (source.Path is not null && source.Path != target)
            Directory.Move(source.Path, target);
        else
            Directory.CreateDirectory(target);

        // Recheck after the rename/create, then reserve ownership before any download starts.
        var owner = await ReadOwnerAsync(target, game, ct);
        if (owner is null)
        {
            var claim = new BackupManifest { GameId = game.Id, Slug = game.Slug, Title = game.Title };
            await claim.CreateAsync(target, ct);
            owner = await ReadOwnerAsync(target, game, ct);
        }
        // A completed legacy backup still gets its origin recorded without fetching its ZIP.
        if (owner is { NeedsSourceUpgrade: true })
        {
            owner = owner with { NeedsSourceUpgrade = false };
            await owner.SaveAsync(target, ct);
        }
        return (target, owner);
    }

    private static async Task<BackupManifest?> ReadOwnerAsync(string path, LibraryEntry game, CancellationToken ct)
    {
        BackupManifest? manifest;
        string? source;
        try { (source, manifest) = await BackupManifest.InspectAsync(path, ct); }
        catch (JsonException) { throw Conflict(path, "its .gogdl.json is invalid"); }
        if (source is not null && manifest is null)
            throw Conflict(path, $"its source is '{(source.Length == 0 ? "unknown" : source)}', not '{BackupManifest.GogSource}'");
        if (manifest is not null && manifest.GameId != game.Id)
            throw Conflict(path, $"it belongs to GOG {manifest.GameId}, not GOG {game.Id}");
        if (manifest is null && Directory.EnumerateFileSystemEntries(path).Any())
            throw Conflict(path, "it contains files but has no valid .gogdl.json to identify their owner");
        return manifest;
    }

    private static void ValidateName(LibraryEntry game)
    {
        var name = game.DirectoryName;
        var stem = name.Split('.')[0];
        var reserved = stem is "con" or "prn" or "aux" or "nul"
            || (stem.Length == 4 && (stem.StartsWith("com") || stem.StartsWith("lpt")) && stem[3] is >= '1' and <= '9');
        if (name.Length == 0 || name.StartsWith('.') || name.EndsWith('.') || reserved
            || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.'))
            throw new BackupDirectoryConflictException(
                $"Unsafe directory slug '{game.Slug}' for GOG {game.Id}. Refresh the library snapshot or correct the slug.");
    }

    private static BackupDirectoryConflictException Conflict(string path, string reason) =>
        new($"Backup directory conflict at '{path}': {reason}. Existing backups were kept; resolve the conflict before retrying.");
}
