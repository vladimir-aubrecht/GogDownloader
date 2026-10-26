using System.Text.Json;
using System.Text.Json.Nodes;
using GameArchiver.Core;
using GogArchiver.Core.Download.Zip;

namespace GameArchiver;

public sealed partial class GameArchiverClient
{
    /// <summary>
    /// Find, add or replace files in completed ZIPs discovered through backup metadata.
    /// Each archive commits independently. Failures are returned per archive; cancellation throws.
    /// </summary>
    public async Task<IReadOnlyList<ArchiveFileResult>> ProcessArchiveFilesAsync(ArchiveOptions options,
        ArchiveFileRequest request, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        options.Validate(); request.Validate(); ct.ThrowIfCancellationRequested();
        var results = new List<ArchiveFileResult>();
        var locations = await ScanAsync(options, ct);
        ZipFileEditor.PreparedFile? prepared = null;
        string? workDirectory = null;
        try
        {
            foreach (var found in locations)
            {
                ct.ThrowIfCancellationRequested();
                if (request.Search.Length > 0 && !found.Metadata.Title.Contains(request.Search, StringComparison.OrdinalIgnoreCase)
                    && !found.Metadata.Slug.Contains(request.Search, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var os in found.Metadata.Platforms.Keys.Where(os => request.Platform is null || os == request.Platform))
                {
                    var path = ArchivePaths.Within(found.Directory, found.Metadata.Platforms[os].ArchiveName);
                    var changes = (IReadOnlyList<ArchiveFileChange>)Array.Empty<ArchiveFileChange>();
                    string? temporary = null, published = null;
                    var committed = false;
                    try
                    {
                        var mutating = !request.DryRun && request.Operation != ArchiveFileOperation.Find;
                        using var lease = mutating ? await ArchiveCatalog.AcquireOwnedAsync(found, found.Metadata.Key, ct) : null;
                        if (lease is not null) await lease.MigrateMetadataAsync(ct);
                        var location = lease?.Location ?? found;
                        if (!location.Metadata.Platforms.TryGetValue(os, out var backup))
                            throw new ArchiveConflictException("Archive platform changed while waiting for its lock.");
                        path = ArchivePaths.Within(location.Directory, backup.ArchiveName);
                        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Only ZIP archives can be edited.");
                        if (!backup.HasArchive(location.Directory)) throw new InvalidDataException("Archive missing or size differs from metadata; run verify first.");
                        if (location.Metadata.Platforms.Values.Count(p => string.Equals(
                            ArchivePaths.Within(location.Directory, p.ArchiveName), path, StringComparison.OrdinalIgnoreCase)) != 1)
                            throw new ArchiveConflictException("Multiple platforms reference the same ZIP.");
                        progress?.Report(new($"Planning {location.Metadata.Key} {os}"));
                        changes = ZipFileEditor.Plan(path, request, ct);
                        // Preview and execution validate the same publication name constraints.
                        var name = changes.Count > 0 && request.Operation != ArchiveFileOperation.Find
                            ? EditedArchiveName.Create(Path.GetFileName(path)) : null;
                        if (!mutating || changes.Count == 0)
                        {
                            results.Add(new(location.Metadata.Key, os, path, changes.Count == 0 ? ArchiveFileStatus.NoMatch
                                : request.Operation == ArchiveFileOperation.Find ? ArchiveFileStatus.Matched : ArchiveFileStatus.Preview, changes));
                            continue;
                        }
                        if (prepared is null)
                        {
                            workDirectory ??= Path.Combine(options.StagingDirectory, "archive-files-" + Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(workDirectory);
                            progress?.Report(new("Compressing source file"));
                            prepared = await ZipFileEditor.PrepareAsync(request.FilePath!, Path.Combine(workDirectory, "content.deflate"), ct);
                        }
                        // A unique published name keeps the previously committed ZIP valid until
                        // the atomic metadata replacement. The parenthesized marker is ignored by
                        // GameVault's title parser, and does not accumulate across successive edits.
                        var destination = ArchivePaths.Within(location.Directory, name!);
                        temporary = ArchivePaths.Within(location.Directory, ".archive-edit-" + Guid.NewGuid().ToString("N") + ".tmp");
                        var required = checked(backup.ArchiveSize + prepared.CompressedSize * changes.Count
                            + changes.Sum(c => (long)(c.EntryPath.Length + (c.BackupPath?.Length ?? 0)) * 8 + 256) + 65536);
                        if (ArchiveStorage.FreeSpaceShortfall(location.Directory, required) is { } missing && missing > 0)
                            throw new IOException($"Not enough space to rewrite the ZIP; approximately {missing} more bytes required.");
                        await ZipFileEditor.RewriteAsync(path, temporary, prepared, changes, progress, ct);
                        var headers = ZipInspector.ReadCentralDirectory(temporary);
                        if (ArchiveCheck.Headers(temporary).Count > 0) throw new InvalidDataException("Edited archive failed header validation.");
                        // Only the inserted entries were compressed anew. Validate their CRCs and
                        // hashes in the output; unchanged compressed bytes were
                        // copied directly, without a costly full-library decompression pass.
                        await VerifyInsertedAsync(temporary, changes, prepared, ct);
                        var metadata = await UpdatedMetadataAsync(location, os, name!, new FileInfo(temporary).Length,
                            headers.Sum(h => h.Size), changes, prepared, ct);
                        ct.ThrowIfCancellationRequested();
                        File.Move(temporary, destination);
                        published = destination;
                        await ArchiveJson.SaveAsync(metadata.Path, metadata.Json, ct);
                        committed = true;
                        string? detail = null;
                        try { File.Delete(path); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        { detail = $"Committed; previous unreferenced ZIP could not be removed: {path}: {e.Message}"; }
                        results.Add(new(location.Metadata.Key, os, destination, ArchiveFileStatus.Updated, changes, detail));
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                        or ZipFormatException or ArgumentException or JsonException or InvalidOperationException or OverflowException)
                    { results.Add(new(found.Metadata.Key, os, path, ArchiveFileStatus.Failed, changes, e.Message)); }
                    finally
                    {
                        TryDelete(temporary);
                        if (!committed) TryDelete(published);
                    }
                }
            }
        }
        finally
        {
            if (workDirectory is not null)
            {
                try { Directory.Delete(workDirectory, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* leave recoverable scratch */ }
            }
        }
        return results;
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* preserve the original failure */ }
    }

    private static async Task VerifyInsertedAsync(string zipPath, IReadOnlyList<ArchiveFileChange> changes,
        ZipFileEditor.PreparedFile prepared, CancellationToken ct)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
        var buffer = new byte[1 << 20];
        foreach (var change in changes)
        {
            var entry = entries[change.EntryPath];
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA1);
            var crc = new Crc32(); long length = 0;
            await using var stream = entry.Open();
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) != 0)
            { hash.AppendData(buffer, 0, read); crc.Append(buffer.AsSpan(0, read)); length += read; }
            if (length != prepared.Size || crc.Value != entry.Crc32
                || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), prepared.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Inserted ZIP entry failed verification: {change.EntryPath}");
        }
    }

    private static async Task<(string Path, JsonObject Json)> UpdatedMetadataAsync(ArchiveLocation location, GameOs os,
        string archiveName, long archiveSize, long installedSize, IReadOnlyList<ArchiveFileChange> changes,
        ZipFileEditor.PreparedFile prepared, CancellationToken ct)
    {
        // Use the reader's authoritative file and payload format, independently of its name.
        // Patch JSON so provider-specific and unknown fields survive.
        var fileName = location.Metadata.LoadedFileName
            ?? throw new InvalidDataException("Archive metadata has no authoritative source file.");
        var legacy = location.Metadata.LoadedGogFormat;
        var path = ArchivePaths.Within(location.Directory, fileName);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))?.AsObject()
            ?? throw new InvalidDataException("Missing archive metadata.");
        string Key(JsonObject obj, string key) => obj.Select(p => p.Key).SingleOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
        var platforms = json[Key(json, "Platforms")]!.AsObject();
        var platform = platforms[Key(platforms, os.ToString())]!.AsObject();
        platform[Key(platform, "ArchiveName")] = archiveName;
        platform[Key(platform, "ArchiveSize")] = archiveSize;
        platform[Key(platform, "InstalledSize")] = installedSize;
        platform[Key(platform, "ModifiedAt")] = DateTimeOffset.UtcNow;
        var filesKey = Key(platform, "Files");
        var files = platform[filesKey]?.AsObject();
        if (files is null) { files = new JsonObject(); platform[filesKey] = files; }
        foreach (var change in changes)
        {
            // Remove stale case variants too: these hashes must never advertise the replaced bytes.
            foreach (var existing in files.Where(f => string.Equals(f.Key, change.EntryPath, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (change.BackupPath is not null) files[change.BackupPath] = existing.Value?.DeepClone();
                files.Remove(existing.Key);
            }
            files[change.EntryPath] = legacy
                ? new JsonObject { ["Md5"] = prepared.Md5, ["Size"] = prepared.Size }
                : new JsonObject { ["Hash"] = prepared.Sha1, ["Size"] = prepared.Size, ["Algorithm"] = "sha1" };
        }
        return (path, json);
    }
}
