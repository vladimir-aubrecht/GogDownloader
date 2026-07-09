using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace GogDownloader.Core.Download.Zip;

public sealed record ZipProgress(int EntriesDone, int EntriesTotal, long BytesRead, long BytesTotal);

/// <summary>
/// Writes a ZIP whose entries are deflated on many cores at once.
///
/// `ZipArchive` compresses entries one at a time on the calling thread, so a large game
/// spends most of an hour with one core busy and the disk idle. ZIP compresses every entry
/// independently, so deflating them in parallel produces byte-identical entry payloads at
/// the same compression level — the ratio is unchanged, only the wall clock moves.
///
/// The archive is assembled in two phases: entries are compressed into temporary parts by a
/// bounded pool of workers, and a single writer splices them into the archive in order. The
/// bound keeps the parts on disk from outgrowing the archive itself.
/// </summary>
public static class ParallelZipWriter
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;

    private const ushort Deflated = 8;
    private const ushort Stored = 0;
    private const ushort Utf8NameFlag = 0x0800;

    /// <summary>Values at or above this need a Zip64 extra field.</summary>
    private const long Zip64Threshold = uint.MaxValue;

    /// <summary>
    /// Entries this small are compressed into memory rather than a temporary file. Most game
    /// files are small, and a disk round-trip per entry costs more than it saves. The bound on
    /// in-flight parts keeps the worst case at roughly (workers + 2) times this.
    /// </summary>
    private const long InMemoryPartLimit = 8L << 20;

    private sealed record Entry(string FullPath, string Name, bool IsDirectory, long Size, DateTime Modified)
    {
        /// <summary>Exactly one of these is set for a file entry.</summary>
        public string? PartPath { get; set; }

        public byte[]? PartBuffer { get; set; }

        public uint Crc { get; set; }
        public long CompressedSize { get; set; }
        public long LocalHeaderOffset { get; set; }
    }

    public static async Task CreateAsync(
        string sourceDirectory,
        string zipPath,
        int maxParallelism,
        CompressionLevel level = CompressionLevel.Optimal,
        IProgress<ZipProgress>? progress = null,
        CancellationToken ct = default,
        long zip64Threshold = Zip64Threshold)
    {
        var entries = Enumerate(sourceDirectory);
        var partsDirectory = zipPath + ".parts";
        Directory.CreateDirectory(partsDirectory);

        var totalBytes = entries.Sum(e => e.Size);
        long bytesRead = 0;
        var done = 0;

        using var cores = new SemaphoreSlim(maxParallelism);

        // Compression runs ahead of the writer by a fixed window. A semaphore would not do:
        // parts finish out of order, so entries past the writer could hold every permit while
        // the entry the writer is waiting for never gets one. Starting tasks in order, and
        // only as the writer advances, bounds the parts in flight without that deadlock.
        var window = maxParallelism + 2;
        var compressing = new Task<Entry>?[entries.Count];

        Task<Entry> Start(Entry entry) => Task.Run(async () =>
        {
            if (entry.IsDirectory) return entry;

            await cores.WaitAsync(ct);
            try
            {
                await CompressAsync(entry, partsDirectory, level, read =>
                {
                    var total = Interlocked.Add(ref bytesRead, read);
                    progress?.Report(new ZipProgress(Volatile.Read(ref done), entries.Count, total, totalBytes));
                }, ct);
                return entry;
            }
            finally
            {
                cores.Release();
            }
        }, ct);

        try
        {
            for (var i = 0; i < Math.Min(window, entries.Count); i++)
                compressing[i] = Start(entries[i]);

            await using (var output = new FileStream(
                zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: false))
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = await compressing[i]!;
                    ct.ThrowIfCancellationRequested();

                    entry.LocalHeaderOffset = output.Position;
                    WriteLocalHeader(output, entry, zip64Threshold);

                    if (entry.PartBuffer is { } buffer)
                    {
                        await output.WriteAsync(buffer, ct);
                        entry.PartBuffer = null;
                    }
                    else if (entry.PartPath is not null)
                    {
                        await using (var part = File.OpenRead(entry.PartPath))
                            await part.CopyToAsync(output, ct);

                        File.Delete(entry.PartPath);
                    }

                    // One entry written, one more may start: the window slides.
                    if (i + window < entries.Count)
                        compressing[i + window] = Start(entries[i + window]);

                    progress?.Report(new ZipProgress(
                        Interlocked.Increment(ref done), entries.Count, Volatile.Read(ref bytesRead), totalBytes));
                }

                WriteCentralDirectory(output, entries, zip64Threshold);
            }
        }
        catch
        {
            // Let the started workers finish failing, or their exceptions go unobserved.
            try { await Task.WhenAll(compressing.Where(t => t is not null)!); }
            catch { /* the original failure is the interesting one */ }

            // A half-written archive must never survive: the next run would trust it.
            SafeDelete(zipPath);
            throw;
        }
        finally
        {
            if (Directory.Exists(partsDirectory)) Directory.Delete(partsDirectory, recursive: true);
        }
    }

    private static List<Entry> Enumerate(string root)
    {
        var entries = new List<Entry>();

        foreach (var path in Directory
                     .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetRelativePath(root, path).Replace('\\', '/');

            if (Directory.Exists(path))
            {
                // Only empty directories need an entry; the rest are implied by their files.
                if (!Directory.EnumerateFileSystemEntries(path).Any())
                    entries.Add(new Entry(path, name + "/", true, 0, Directory.GetLastWriteTime(path)));
            }
            else
            {
                var info = new FileInfo(path);
                entries.Add(new Entry(path, name, false, info.Length, info.LastWriteTime));
            }
        }

        return entries;
    }

    private static async Task CompressAsync(
        Entry entry, string partsDirectory, CompressionLevel level, Action<int> onRead, CancellationToken ct)
    {
        var toMemory = entry.Size <= InMemoryPartLimit;
        var part = toMemory ? null : Path.Combine(partsDirectory, Guid.NewGuid().ToString("N"));

        var crc = new Crc32();
        Stream destination = toMemory ? new MemoryStream((int)entry.Size + 64) : File.Create(part!);

        await using (destination)
        {
            await using (var deflate = new DeflateStream(destination, level, leaveOpen: true))
            await using (var source = File.OpenRead(entry.FullPath))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        crc.Append(buffer.AsSpan(0, read));
                        await deflate.WriteAsync(buffer.AsMemory(0, read), ct);
                        onRead(read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            if (destination is MemoryStream memory) entry.PartBuffer = memory.ToArray();
        }

        entry.PartPath = part;
        entry.Crc = crc.Value;
        entry.CompressedSize = entry.PartBuffer?.Length ?? new FileInfo(part!).Length;
    }

    private static void WriteLocalHeader(Stream output, Entry entry, long threshold)
    {
        var name = Encoding.UTF8.GetBytes(entry.Name);
        var zip64 = entry.Size >= threshold || entry.CompressedSize >= threshold;

        var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(LocalHeaderSignature);
        writer.Write((ushort)(zip64 ? 45 : 20));
        writer.Write(Utf8NameFlag);
        writer.Write(entry.IsDirectory ? Stored : Deflated);
        writer.Write(DosTime(entry.Modified));
        writer.Write(DosDate(entry.Modified));
        writer.Write(entry.Crc);
        writer.Write(zip64 ? uint.MaxValue : (uint)entry.CompressedSize);
        writer.Write(zip64 ? uint.MaxValue : (uint)entry.Size);
        writer.Write((ushort)name.Length);
        writer.Write((ushort)(zip64 ? 20 : 0));
        writer.Write(name);

        if (zip64)
        {
            writer.Write((ushort)0x0001);
            writer.Write((ushort)16);
            writer.Write(entry.Size);
            writer.Write(entry.CompressedSize);
        }

        writer.Flush();
    }

    private static void WriteCentralDirectory(Stream output, List<Entry> entries, long threshold)
    {
        var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        var start = output.Position;

        foreach (var entry in entries)
        {
            var name = Encoding.UTF8.GetBytes(entry.Name);

            var bigSizes = entry.Size >= threshold || entry.CompressedSize >= threshold;
            var bigOffset = entry.LocalHeaderOffset >= threshold;
            var zip64 = bigSizes || bigOffset;

            var extraLength = zip64 ? 4 + (bigSizes ? 16 : 0) + (bigOffset ? 8 : 0) : 0;

            writer.Write(CentralHeaderSignature);
            writer.Write((ushort)45);                       // version made by
            writer.Write((ushort)(zip64 ? 45 : 20));        // version needed
            writer.Write(Utf8NameFlag);
            writer.Write(entry.IsDirectory ? Stored : Deflated);
            writer.Write(DosTime(entry.Modified));
            writer.Write(DosDate(entry.Modified));
            writer.Write(entry.Crc);
            writer.Write(bigSizes ? uint.MaxValue : (uint)entry.CompressedSize);
            writer.Write(bigSizes ? uint.MaxValue : (uint)entry.Size);
            writer.Write((ushort)name.Length);
            writer.Write((ushort)extraLength);
            writer.Write((ushort)0);                        // comment length
            writer.Write((ushort)0);                        // disk number
            writer.Write((ushort)0);                        // internal attributes
            writer.Write(entry.IsDirectory ? 0x10u : 0u);   // external attributes
            writer.Write(bigOffset ? uint.MaxValue : (uint)entry.LocalHeaderOffset);
            writer.Write(name);

            if (zip64)
            {
                writer.Write((ushort)0x0001);
                writer.Write((ushort)(extraLength - 4));
                if (bigSizes)
                {
                    writer.Write(entry.Size);
                    writer.Write(entry.CompressedSize);
                }
                if (bigOffset) writer.Write(entry.LocalHeaderOffset);
            }
        }

        writer.Flush();
        var size = output.Position - start;

        var needsZip64 = entries.Count >= ushort.MaxValue || start >= threshold || size >= threshold;
        if (needsZip64)
        {
            var zip64End = output.Position;

            writer.Write(Zip64EndOfCentralDirectorySignature);
            writer.Write(44L);                              // size of this record minus 12
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write(0u);                               // this disk
            writer.Write(0u);                               // disk with central directory
            writer.Write((long)entries.Count);
            writer.Write((long)entries.Count);
            writer.Write(size);
            writer.Write(start);

            writer.Write(Zip64LocatorSignature);
            writer.Write(0u);
            writer.Write(zip64End);
            writer.Write(1u);
        }

        writer.Write(EndOfCentralDirectorySignature);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)Math.Min(entries.Count, ushort.MaxValue));
        writer.Write((ushort)Math.Min(entries.Count, ushort.MaxValue));
        writer.Write(size >= threshold ? uint.MaxValue : (uint)size);
        writer.Write(start >= threshold ? uint.MaxValue : (uint)start);
        writer.Write((ushort)0);
        writer.Flush();
    }

    private static ushort DosTime(DateTime t) => (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));

    private static ushort DosDate(DateTime t)
    {
        var year = Math.Max(t.Year, 1980);
        return (ushort)(((year - 1980) << 9) | (t.Month << 5) | t.Day);
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* the caller is already failing; do not mask it */ }
    }
}
