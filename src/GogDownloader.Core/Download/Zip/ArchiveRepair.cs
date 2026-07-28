using System.Buffers.Binary;
using System.Text;

namespace GogDownloader.Core.Download.Zip;

public sealed class ArchiveRepairException(string message) : Exception(message);

/// <summary>
/// Fixes the archives this tool wrote for empty files before 0.39.0, in place.
///
/// The entry is empty: no payload, zero sizes, a CRC of zero. Only the compression method is
/// wrong, and the method is two bytes in each of the entry's two headers — so the repair
/// changes four bytes per entry, moves nothing, and cannot touch a byte of game data. A 30 GiB
/// archive on a network share is fixed in the time it takes to seek to the headers.
///
/// Anything else a check reports is damage to the payload, which no local edit can undo: that
/// archive has to be downloaded again.
/// </summary>
public static class ArchiveRepair
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const ushort Stored = 0;

    /// <summary>
    /// Repairs every entry that can be repaired and returns their names. Throws when the
    /// archive does not look the way the central directory says it does — a mismatch there
    /// means this is not the defect it appears to be, and guessing would make things worse.
    /// </summary>
    public static List<string> Repair(string zipPath)
    {
        var broken = ZipInspector.ReadCentralDirectory(zipPath)
            .Where(e => e.IsEmptyDeflate && e is { Size: 0, CompressedSize: 0, Crc: 0 })
            .ToList();

        if (broken.Count == 0) return [];

        using (var file = new FileStream(zipPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            foreach (var entry in broken)
            {
                CheckLocalHeader(file, entry);

                // The method sits at +8 in a local header and at +10 in a central one.
                WriteMethod(file, entry.LocalHeaderOffset + 8);
                WriteMethod(file, entry.CentralHeaderOffset + 10);
            }

            file.Flush(flushToDisk: true);
        }

        // Never report a repair that did not take: read the headers back from disk.
        var left = ArchiveCheck.Headers(zipPath);
        if (left.Count > 0)
            throw new ArchiveRepairException(
                $"{Path.GetFileName(zipPath)} still reports {left[0].Entry}: {left[0].Problem}");

        return broken.Select(e => e.Name).ToList();
    }

    private static void CheckLocalHeader(FileStream file, ZipEntryHeader entry)
    {
        var name = Encoding.UTF8.GetBytes(entry.Name);
        var header = new byte[30];

        file.Position = entry.LocalHeaderOffset;
        file.ReadExactly(header);

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalHeaderSignature)
            throw new ArchiveRepairException(
                $"No local header for {entry.Name} where the central directory says it is.");

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        if (nameLength != name.Length)
            throw new ArchiveRepairException($"The local header at {entry.Name} names something else.");

        var localName = new byte[nameLength];
        file.ReadExactly(localName);

        if (!localName.AsSpan().SequenceEqual(name))
            throw new ArchiveRepairException(
                $"The local header at {entry.Name} names {Encoding.UTF8.GetString(localName)}.");

        // A payload of any length means this is not the empty entry the directory describes,
        // and the four-byte edit would then leave real bytes labelled as stored.
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18)) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22)) != 0)
            throw new ArchiveRepairException($"{entry.Name} is not empty in its local header.");
    }

    private static void WriteMethod(FileStream file, long position)
    {
        Span<byte> method = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(method, Stored);

        file.Position = position;
        file.Write(method);
    }
}
