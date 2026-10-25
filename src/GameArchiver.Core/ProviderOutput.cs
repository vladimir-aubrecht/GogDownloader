namespace GameArchiver.Core;

/// <summary>Routes archive operations to provider folders without changing product identity.</summary>
public static class ProviderOutput
{
    private static string Folder(ArchiveOptions options, string provider) => options.ProviderDirectories
        .FirstOrDefault(p => string.Equals(p.Key, provider, StringComparison.OrdinalIgnoreCase)).Value ?? provider.ToLowerInvariant();

    public static void Validate(ArchiveOptions options, IEnumerable<string> providers)
    {
        options.Validate();
        if (options.Layout == ArchiveLayout.Flat) return;
        var names = providers.Concat(options.ProviderDirectories.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var name in names) ArchivePaths.ValidateName(name);
        if (names.GroupBy(p => Folder(options, p), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new ArchiveConflictException("Each provider must use a different directory.");
    }

    /// <summary>Read-only resolution for diff, verification and download previews.</summary>
    public static ArchiveOptions Resolve(ArchiveOptions options, string provider)
    {
        Validate(options, [provider]);
        if (options.Layout == ArchiveLayout.Flat) return options;
        var name = Folder(options, provider);
        var matches = Directory.Exists(options.OutputRoot)
            ? Directory.EnumerateFileSystemEntries(options.OutputRoot)
                .Where(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase)).ToList() : [];
        if (matches.Count > 1) throw new ArchiveConflictException($"Multiple entries use provider directory '{name}'.");
        var path = ArchivePaths.Within(options.OutputRoot, matches.Count == 1 ? Path.GetFileName(matches[0]) : name);
        if (matches.Count == 1 && (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new ArchiveConflictException($"Provider directory is occupied by a file or link: {path}");
        if (File.Exists(Path.Combine(path, ArchiveMetadata.FileName)) || File.Exists(Path.Combine(path, ArchiveMetadata.LegacyFileName)))
            throw new ArchiveConflictException($"Provider directory is already a game backup: {path}");
        return options with { OutputRoot = path, Layout = ArchiveLayout.Flat, ProviderDirectories = [] };
    }

    public static async Task<ArchiveOptions> PrepareAsync(ArchiveOptions options, string provider, CancellationToken ct = default)
    {
        Validate(options, [provider]);
        if (options.Layout == ArchiveLayout.Flat) return options;
        using var root = await ArchiveReservation.AcquireAsync(options.OutputRoot, ct);
        var resolved = Resolve(options, provider);
        // Leave the provider's allocation lock in place before releasing the parent lock.
        // A concurrent flat-layout writer must not adopt an empty provider folder as a game.
        using var child = await ArchiveReservation.AcquireAsync(resolved.OutputRoot, ct);
        return resolved;
    }
}
