using System.Buffers;
using System.IO.Compression;

namespace GogArchiver.Core.Download.Zip;

/// <summary>
/// One thing wrong with an archive. <see cref="Repairable"/> marks the damage that lives in
/// the headers alone, which <see cref="ArchiveRepair"/> can undo without the network.
/// </summary>
public sealed record ArchiveProblem(string Entry, string Problem, bool Repairable = false);

/// <summary>
/// Checks a finished archive on its own, without the staging directory it was made from —
/// which is what <see cref="ArchiveVerifier"/> needs and a backup made months ago no longer has.
///
/// The header check is nearly free: it reads the central directory and looks for entries no
/// standard unzip can read. The deep check inflates everything and recomputes the CRCs, which
/// costs a full pass over the archive.
/// </summary>
public static class ArchiveCheck
{
    /// <summary>
    /// What the headers alone give away. Cheap enough to run over a whole backup: it reads the
    /// central directory and nothing else, however large the archive.
    /// </summary>
    public static List<ArchiveProblem> Headers(string zipPath)
    {
        var problems = new List<ArchiveProblem>();

        foreach (var entry in ZipInspector.ReadCentralDirectory(zipPath))
        {
            if (entry.IsEmptyDeflate)
                problems.Add(new ArchiveProblem(entry.Name,
                    "deflated with an empty payload — no unzip can read it",
                    Repairable: entry is { Size: 0, CompressedSize: 0, Crc: 0 }));

            // A directory entry with content, or a file whose sizes contradict each other, is
            // not something this writer can produce; report it rather than trust it.
            else if (entry.IsDirectory && (entry.Size > 0 || entry.CompressedSize > 0))
                problems.Add(new ArchiveProblem(entry.Name, "a directory entry carrying data"));
            else if (entry.Size > 0 && entry.CompressedSize == 0)
                problems.Add(new ArchiveProblem(entry.Name,
                    $"{entry.Size} bytes stored in nothing"));
        }

        return problems;
    }

    /// <summary>
    /// Inflates every entry and compares what comes out against the CRC and the length the
    /// archive itself records. Costs one full read of the archive.
    /// </summary>
    public static async Task<List<ArchiveProblem>> DeepAsync(
        string zipPath, Action<long>? onRead = null, CancellationToken ct = default)
    {
        var problems = new List<ArchiveProblem>();

        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;

            try
            {
                await using var stream = entry.Open();
                var (crc, length) = await ReadAsync(stream, onRead, ct);

                if (length != entry.Length)
                    problems.Add(new ArchiveProblem(entry.FullName,
                        $"inflated to {length} bytes, the header says {entry.Length}"));
                else if (crc != entry.Crc32)
                    problems.Add(new ArchiveProblem(entry.FullName, "CRC does not match the header"));
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                problems.Add(new ArchiveProblem(entry.FullName, e.Message));
            }
        }

        return problems;
    }

    private static async Task<(uint Crc, long Length)> ReadAsync(
        Stream stream, Action<long>? onRead, CancellationToken ct)
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
                onRead?.Invoke(count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (crc.Value, length);
    }
}
