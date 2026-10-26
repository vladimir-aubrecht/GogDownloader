using GameArchiver.Core;

namespace GameArchiver;

public sealed record MetadataMigrationResult(GameKey Key, string Directory, bool Changed, string? Error = null);

public sealed partial class GameArchiverClient
{
    /// <summary>Move legacy manifests to visible gamearchiver.json without changing archive bytes.</summary>
    public async Task<IReadOnlyList<MetadataMigrationResult>> MigrateMetadataAsync(ArchiveOptions options,
        bool dryRun = false, string search = "", CancellationToken ct = default)
    {
        options.Validate();
        var results = new List<MetadataMigrationResult>();
        foreach (var found in await ScanAsync(options, ct))
        {
            if (search.Length > 0 && !found.Metadata.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !found.Metadata.Slug.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (dryRun)
                {
                    var changed = await found.Metadata.NeedsMigrationAsync(found.Directory, ct);
                    results.Add(new(found.Metadata.Key, found.Directory, changed));
                    continue;
                }
                using var lease = await ArchiveCatalog.AcquireOwnedAsync(found, found.Metadata.Key, ct);
                results.Add(new(found.Metadata.Key, found.Directory, await lease.MigrateMetadataAsync(ct)));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            { results.Add(new(found.Metadata.Key, found.Directory, false, e.Message)); }
        }
        return results;
    }
}
