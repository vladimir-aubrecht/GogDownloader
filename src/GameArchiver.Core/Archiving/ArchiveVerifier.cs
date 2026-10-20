using System.Buffers;
using System.IO.Compression;

namespace GogArchiver.Core.Download.Zip;

public sealed class ArchiveCorruptException(string message) : Exception(message);

/// <summary>
/// Reads a finished archive back and compares it against the directory it was made from.
///
/// This exists because the ZIP writer is ours. `ZipFile.OpenRead` parses the central directory,
/// so a malformed archive fails immediately — but the framework reader does **not** check CRCs,
/// so opening an archive proves nothing about its contents. The CRC is therefore recomputed from
/// the decompressed bytes and compared with the source file, which does not trust the writer's
/// own bookkeeping either.
///
/// It runs before the manifest is written, so a corrupt archive can never be recorded as done.
/// </summary>
public static class ArchiveVerifier
{
    public static async Task VerifyAsync(
        string zipPath,
        string sourceDirectory,
        IProgress<ZipProgress>? progress = null,
        CancellationToken ct = default)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var expected = Directory
            .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(sourceDirectory, p).Replace('\\', '/'));

        var files = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();

        if (files.Count != expected.Count)
            throw new ArchiveCorruptException(
                $"Archive holds {files.Count} files, staging has {expected.Count}.");

        var total = files.Sum(e => e.Length);
        long read = 0;
        var done = 0;

        foreach (var entry in files)
        {
            ct.ThrowIfCancellationRequested();

            if (!expected.TryGetValue(entry.FullName, out var sourcePath))
                throw new ArchiveCorruptException($"Archive holds an unexpected entry: {entry.FullName}");

            await using (var stream = entry.Open())
            {
                var (crc, length) = await ReadAsync(stream, r => progress?.Report(
                    new ZipProgress(done, files.Count, Interlocked.Add(ref read, r), total)), ct);

                if (length != entry.Length)
                    throw new ArchiveCorruptException(
                        $"{entry.FullName}: inflated to {length} bytes, header says {entry.Length}.");
                if (crc != entry.Crc32)
                    throw new ArchiveCorruptException($"{entry.FullName}: CRC does not match the header.");

                await using var source = File.OpenRead(sourcePath);
                var (sourceCrc, sourceLength) = await ReadAsync(source, _ => { }, ct);

                if (length != sourceLength || crc != sourceCrc)
                    throw new ArchiveCorruptException($"{entry.FullName}: content differs from staging.");
            }

            progress?.Report(new ZipProgress(++done, files.Count, Volatile.Read(ref read), total));
        }
    }

    private static async Task<(uint Crc, long Length)> ReadAsync(
        Stream stream, Action<int> onRead, CancellationToken ct)
    {
        var crc = new Crc32();
        long length = 0;

        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) > 0)
            {
                crc.Append(buffer.AsSpan(0, count));
                length += count;
                onRead(count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (crc.Value, length);
    }
}
