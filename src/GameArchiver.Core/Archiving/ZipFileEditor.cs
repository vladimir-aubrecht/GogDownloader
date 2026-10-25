using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GogArchiver.Core.Download.Zip;

namespace GameArchiver.Core;

/// <summary>
/// Rebuilds ZIP/ZIP64 headers while copying unchanged compressed payloads verbatim.
/// Memory use depends on the entry count, never on the size of an individual file.
/// </summary>
public static class ZipFileEditor
{
    public sealed record PreparedFile(string CompressedPath, long Size, long CompressedSize,
        uint Crc, string Md5, string Sha1, DateTime Modified);

    private sealed record Input(ParallelZipWriter.Entry Entry, long DataOffset);

    public static IReadOnlyList<ArchiveFileChange> Plan(string zipPath, ArchiveFileRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var entries = Read(zipPath, ct);
        if (request.Operation == ArchiveFileOperation.Find)
            return entries.Where(e => request.Matches(e.Entry.Name)).Select(e => new ArchiveFileChange(e.Entry.Name)).ToList();
        var names = entries.Select(e => e.Entry.Name.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = entries.Where(e => !e.Entry.IsDirectory).Select(e => e.Entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = request.Operation == ArchiveFileOperation.Add ? new[] { request.Destination }
            : entries.Where(e => request.Matches(e.Entry.Name)).Select(e => e.Entry.Name).ToArray();
        var changes = new List<ArchiveFileChange>();
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            // Case-only variants would become duplicate files on Windows. Preserve the existing
            // entry's spelling, including for an Add that replaces an existing file.
            var existing = entries.SingleOrDefault(e => string.Equals(e.Entry.Name.TrimEnd('/'), target, StringComparison.OrdinalIgnoreCase));
            if (existing?.Entry.IsDirectory == true || HasFileParent(target, files)
                || names.Any(n => n.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"ZIP path conflicts with a directory or parent file: {target}");
            string? backup = null;
            var destination = existing?.Entry.Name ?? target;
            if (existing is not null)
            {
                backup = destination + ".backup";
                while (names.Contains(backup) || names.Any(n => n.StartsWith(backup + "/", StringComparison.OrdinalIgnoreCase)))
                    backup += ".backup";
                ArchiveFileRequest.ValidatePath(backup);
                names.Add(backup); files.Add(backup);
            }
            names.Add(destination); files.Add(destination);
            changes.Add(new(destination, backup));
        }
        return changes;
    }

    public static async Task<PreparedFile> PrepareAsync(string filePath, string compressedPath, CancellationToken ct = default)
    {
        var crc = new Crc32();
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        long size = 0;
        var created = false;
        try
        {
            await using var input = File.OpenRead(filePath);
            await using (var output = new FileStream(compressedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                await using var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true);
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    crc.Append(buffer.AsSpan(0, read));
                    md5.AppendData(buffer, 0, read); sha1.AppendData(buffer, 0, read);
                    size = checked(size + read);
                    await deflate.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            // Empty files use Stored and must have no compressed payload.
            if (size == 0) { using var empty = new FileStream(compressedPath, FileMode.Truncate); }
            ct.ThrowIfCancellationRequested();
            return new(compressedPath, size, new FileInfo(compressedPath).Length, crc.Value,
                Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
                Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(), File.GetLastWriteTime(filePath));
        }
        catch { if (created) File.Delete(compressedPath); throw; }
    }

    /// <summary>Creates a new file; never modifies or replaces the source ZIP.</summary>
    public static async Task RewriteAsync(string zipPath, string outputPath, PreparedFile replacement,
        IReadOnlyList<ArchiveFileChange> changes, IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default, long zip64Threshold = uint.MaxValue)
    {
        var originals = Read(zipPath, ct);
        var byName = changes.ToDictionary(c => c.EntryPath, StringComparer.Ordinal);
        if (changes.Count == 0) throw new ArgumentException("No ZIP changes requested.");
        var originalFiles = originals.Where(e => !e.Entry.IsDirectory).Select(e => e.Entry.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            ArchiveFileRequest.ValidatePath(change.EntryPath);
            if (change.BackupPath is not null) ArchiveFileRequest.ValidatePath(change.BackupPath);
            if ((change.BackupPath is not null) != originalFiles.Contains(change.EntryPath))
                throw new InvalidDataException("The ZIP edit plan no longer matches the source archive.");
        }
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var original in originals)
        {
            var name = byName.TryGetValue(original.Entry.Name, out var change) ? change.BackupPath : original.Entry.Name;
            if (name is null) throw new InvalidDataException("Replacing an entry requires a backup path.");
            ValidateOutputName(name, occupied);
        }
        foreach (var change in changes) ValidateOutputName(change.EntryPath, occupied);
        var outputFiles = originals.Where(e => !e.Entry.IsDirectory)
            .Select(e => byName.TryGetValue(e.Entry.Name, out var change) ? change.BackupPath! : e.Entry.Name)
            .Concat(changes.Select(c => c.EntryPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (occupied.Any(n => HasFileParent(n, outputFiles)))
            throw new InvalidDataException("ZIP edit plan creates conflicting file and directory paths.");
        var total = checked(originals.Sum(e => e.Entry.CompressedSize) + replacement.CompressedSize * changes.Count);
        var written = 0L;
        var buffer = new byte[1 << 20];
        var outputEntries = new List<ParallelZipWriter.Entry>();
        await using var input = File.OpenRead(zipPath);
        // CreateNew is intentional: even a caller passing the source path cannot destroy it.
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        async Task CopyAsync(Stream source, long count)
        {
            while (count > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), ct);
                if (read == 0) throw new EndOfStreamException("Truncated ZIP payload.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                count -= read; written += read;
                progress?.Report(new("Editing archive", written, total, outputEntries.Count, originals.Count + changes.Count));
            }
        }
        foreach (var original in originals)
        {
            ct.ThrowIfCancellationRequested();
            var name = byName.TryGetValue(original.Entry.Name, out var change) ? change.BackupPath! : original.Entry.Name;
            var entry = original.Entry with { Name = name, LocalHeaderOffset = output.Position };
            ParallelZipWriter.WriteLocalHeader(output, entry, zip64Threshold);
            input.Position = original.DataOffset;
            await CopyAsync(input, entry.CompressedSize);
            outputEntries.Add(entry);
        }
        await using var compressed = File.OpenRead(replacement.CompressedPath);
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested();
            var entry = new ParallelZipWriter.Entry("", change.EntryPath, false, replacement.Size, replacement.Modified)
            { CompressedSize = replacement.CompressedSize, Crc = replacement.Crc, LocalHeaderOffset = output.Position };
            ParallelZipWriter.WriteLocalHeader(output, entry, zip64Threshold);
            compressed.Position = 0;
            await CopyAsync(compressed, replacement.CompressedSize);
            outputEntries.Add(entry);
        }
        ct.ThrowIfCancellationRequested();
        ParallelZipWriter.WriteCentralDirectory(output, outputEntries, zip64Threshold);
        await output.FlushAsync(ct);
        output.Flush(flushToDisk: true);
    }

    private static void ValidateOutputName(string name, HashSet<string> names)
    {
        ArchiveFileRequest.ValidatePath(name.EndsWith('/') ? name[..^1] : name);
        if (name.Contains('\\')) throw new InvalidDataException("ZIP entry paths must use forward slashes.");
        if (!names.Add(name.TrimEnd('/'))) throw new InvalidDataException($"Duplicate ZIP entry: {name}");
    }
    private static bool HasFileParent(string path, HashSet<string> files)
    {
        for (var i = path.IndexOf('/'); i >= 0; i = path.IndexOf('/', i + 1))
            if (files.Contains(path[..i])) return true;
        return false;
    }

    private static List<Input> Read(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        using var input = File.OpenRead(path);
        var headers = ZipInspector.ReadCentralDirectory(path);
        if (headers.Count != zip.Entries.Count || ArchiveCheck.Headers(path).Count != 0)
            throw new InvalidDataException("Invalid ZIP headers; run verify before editing.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Input>();
        var raw = new byte[46];
        for (var i = 0; i < headers.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var h = headers[i]; var e = zip.Entries[i];
            ValidateOutputName(h.Name, names);
            if (!h.IsDirectory) files.Add(h.Name);
            if (h.Name != e.FullName || h.Size != e.Length || h.CompressedSize != e.CompressedLength || h.Crc != e.Crc32
                || h.Name.Contains('\\') || h.Method is not 0 and not 8 || h.Size < 0 || h.CompressedSize < 0
                || h.Method == 0 && h.Size != h.CompressedSize || h.LocalHeaderOffset < 0)
                throw new InvalidDataException($"Unsupported or inconsistent ZIP entry: {h.Name}");
            input.Position = h.CentralHeaderOffset; input.ReadExactly(raw);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(8));
            // Deflate options, data descriptors and UTF-8 are supported; encryption is not.
            if ((flags & ~0x080e) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(34)) != 0)
                throw new InvalidDataException($"Encrypted, split or unsupported ZIP entry: {h.Name}");
            input.Position = h.LocalHeaderOffset; input.ReadExactly(raw.AsSpan(0, 30));
            if (BinaryPrimitives.ReadUInt32LittleEndian(raw) != 0x04034b50
                || BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(6)) != flags
                || BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(8)) != h.Method)
                throw new InvalidDataException($"Local ZIP header disagrees: {h.Name}");
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(26));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(28));
            var name = new byte[nameLength]; input.ReadExactly(name);
            if (!name.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(h.Name)))
                throw new InvalidDataException($"Non-UTF8 or inconsistent ZIP filename: {h.Name}");
            var dataOffset = checked(input.Position + extraLength);
            if ((flags & 8) == 0)
            {
                long size = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(22));
                long compressed = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(18));
                var extra = new byte[extraLength]; input.ReadExactly(extra);
                if (size == uint.MaxValue || compressed == uint.MaxValue)
                {
                    var found = false;
                    for (var at = 0; at + 4 <= extra.Length;)
                    {
                        var id = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(at));
                        var length = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(at + 2));
                        at += 4;
                        if (length > extra.Length - at) throw new InvalidDataException("Truncated local ZIP extra field.");
                        if (id == 1)
                        {
                            var needed = (size == uint.MaxValue ? 8 : 0) + (compressed == uint.MaxValue ? 8 : 0);
                            if (length < needed) throw new InvalidDataException("Truncated local ZIP64 sizes.");
                            var offset = at;
                            if (size == uint.MaxValue) { size = BinaryPrimitives.ReadInt64LittleEndian(extra.AsSpan(offset)); offset += 8; }
                            if (compressed == uint.MaxValue) compressed = BinaryPrimitives.ReadInt64LittleEndian(extra.AsSpan(offset));
                            found = true; break;
                        }
                        at += length;
                    }
                    if (!found) throw new InvalidDataException("Missing local ZIP64 sizes.");
                }
                if (size != h.Size || compressed != h.CompressedSize || BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(14)) != h.Crc)
                    throw new InvalidDataException($"Local ZIP sizes or CRC disagree: {h.Name}");
            }
            if (h.CompressedSize > input.Length - dataOffset || dataOffset > headers[0].CentralHeaderOffset
                || h.CompressedSize > headers[0].CentralHeaderOffset - dataOffset)
                throw new InvalidDataException($"ZIP payload is outside the content area: {h.Name}");
            result.Add(new(new("", h.Name, h.IsDirectory, h.Size, e.LastWriteTime.DateTime)
            { OriginalMethod = h.Method, ExternalAttributes = (uint)e.ExternalAttributes, Crc = h.Crc, CompressedSize = h.CompressedSize }, dataOffset));
        }
        if (result.Any(e => HasFileParent(e.Entry.Name.TrimEnd('/'), files)))
            throw new InvalidDataException("ZIP contains conflicting file and directory paths.");
        return result;
    }
}
