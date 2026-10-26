using System.Buffers.Binary;
using System.Text;

namespace GogArchiver.Core.Download.Zip;

/// <summary>
/// What the central directory records about one entry, and where both of its headers sit —
/// a repair has to patch the same field in each.
/// </summary>
public sealed record ZipEntryHeader(
    string Name,
    ushort Method,
    long CompressedSize,
    long Size,
    uint Crc,
    long CentralHeaderOffset,
    long LocalHeaderOffset)
{
    public bool IsDirectory => Name.EndsWith('/');

    /// <summary>
    /// Deflated, yet carrying no bytes. A deflate stream is never empty — it has at least a
    /// final block — so nothing can inflate this. It is what the writer produced for empty
    /// files until 0.39.0: `DeflateStream` that is never written to emits nothing at all.
    /// .NET reads such an entry back as empty without complaint; Info-ZIP, 7-Zip and Windows
    /// Explorer refuse it.
    /// </summary>
    public bool IsEmptyDeflate => Method == 8 && CompressedSize == 0;
}

public sealed class ZipFormatException(string message) : Exception(message);

/// <summary>
/// Reads a ZIP's central directory without inflating anything. `ZipArchiveEntry` does not
/// expose the compression method, and the method is what tells a well-formed archive from the
/// one described above — so the headers have to be read by hand.
/// </summary>
public static class ZipInspector
{
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;

    /// <summary>The end record holds a comment of up to 64 KiB, so the tail is that plus its own.</summary>
    private const int TailLength = 66_000;

    public static List<ZipEntryHeader> ReadCentralDirectory(string zipPath)
    {
        using var file = File.OpenRead(zipPath);

        var (offset, size, count) = LocateCentralDirectory(file);
        if (size > int.MaxValue || count > int.MaxValue || count > size / 46)
            throw new ZipFormatException($"Central directory of {size} bytes is implausible.");

        file.Position = offset;
        var directory = new byte[size];
        file.ReadExactly(directory);

        var entries = new List<ZipEntryHeader>();
        var at = 0;

        for (long i = 0; i < count; i++)
        {
            if (directory.Length - at < 46 || BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at)) != CentralHeaderSignature)
                throw new ZipFormatException("Missing or malformed central directory entry.");
            var method = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 10));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at + 16));
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at + 20));
            long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at + 24));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 32));
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at + 42));

            if (at + 46 + nameLength + extraLength + commentLength > directory.Length)
                throw new ZipFormatException("A central directory entry runs past the directory.");

            var name = Encoding.UTF8.GetString(directory, at + 46, nameLength);
            var extra = directory.AsSpan(at + 46 + nameLength, extraLength);

            // A value of 0xFFFFFFFF is a promise that the real one is in the Zip64 extra field,
            // in the order size, compressed size, local header offset — but only the fields
            // that overflowed are there at all, so they have to be consumed in that order.
            if (uncompressed == uint.MaxValue || compressed == uint.MaxValue || localOffset == uint.MaxValue)
            {
                var zip64 = FindExtra(extra, 0x0001);
                var needed = (uncompressed == uint.MaxValue ? 8 : 0) + (compressed == uint.MaxValue ? 8 : 0)
                    + (localOffset == uint.MaxValue ? 8 : 0);
                if (zip64.Length < needed) throw new ZipFormatException("Missing or truncated ZIP64 entry fields.");
                var read = 0;

                if (uncompressed == uint.MaxValue && zip64.Length >= read + 8)
                {
                    uncompressed = BinaryPrimitives.ReadInt64LittleEndian(zip64[read..]);
                    read += 8;
                }

                if (compressed == uint.MaxValue && zip64.Length >= read + 8)
                {
                    compressed = BinaryPrimitives.ReadInt64LittleEndian(zip64[read..]);
                    read += 8;
                }

                if (localOffset == uint.MaxValue && zip64.Length >= read + 8)
                    localOffset = BinaryPrimitives.ReadInt64LittleEndian(zip64[read..]);
            }

            if (compressed < 0 || uncompressed < 0 || localOffset < 0 || localOffset > offset - 30
                || BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 34)) != 0)
                throw new ZipFormatException("Invalid ZIP entry size, offset or disk number.");
            entries.Add(new ZipEntryHeader(name, method, compressed, uncompressed, crc, offset + at, localOffset));
            at += 46 + nameLength + extraLength + commentLength;
        }

        // An optional central-directory digital signature is the only permitted trailer.
        if (at != directory.Length && !(directory.Length - at >= 6
            && BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at)) == 0x05054b50
            && directory.Length - at == 6 + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 4))))
            throw new ZipFormatException("Central directory size/count does not match its entries.");
        return entries;
    }

    private static (long Offset, long Size, long Count) LocateCentralDirectory(FileStream file)
    {
        var tailLength = (int)Math.Min(TailLength, file.Length);
        var tail = new byte[tailLength];
        file.Position = file.Length - tailLength;
        file.ReadExactly(tail);

        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndOfCentralDirectorySignature
                && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length)
            { end = i; break; }
        if (end < 0) throw new ZipFormatException("No complete end-of-central-directory record.");
        var endOffset = file.Length - tailLength + end;
        if (BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 4)) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 6)) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 8)) != BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10)))
            throw new ZipFormatException("Split ZIP archives are unsupported.");
        long count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));

        var locator = end - 20;
        var hasLocator = locator >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator)) == Zip64LocatorSignature;
        // Classic ZIPs may contain exactly 65,535 records without a ZIP64 locator.
        // A larger inventory cannot pass the exact count/size validation below.
        if (size == uint.MaxValue || offset == uint.MaxValue || count == ushort.MaxValue && hasLocator)
        {
            if (locator < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator)) != Zip64LocatorSignature
                || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator + 4)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator + 16)) != 1)
                throw new ZipFormatException("Missing or invalid ZIP64 locator.");
            var zip64End = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(locator + 8));
            if (zip64End < 0 || zip64End > endOffset - 20 - 56)
                throw new ZipFormatException("ZIP64 end record is outside the archive.");
            var record = new byte[56];
            file.Position = zip64End;
            file.ReadExactly(record);
            var recordSize = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(4));
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndSignature || recordSize < 44
                || recordSize > endOffset - 20 - zip64End - 12
                || BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(20)) != 0
                || BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(24)) != BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(32)))
                throw new ZipFormatException("Invalid or split ZIP64 end record.");
            var actualCount = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(32));
            var actualSize = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(40));
            var actualOffset = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(48));
            if (count != ushort.MaxValue && count != actualCount || size != uint.MaxValue && size != actualSize
                || offset != uint.MaxValue && offset != actualOffset)
                throw new ZipFormatException("ZIP and ZIP64 end records disagree.");
            count = actualCount; size = actualSize; offset = actualOffset; endOffset = zip64End;
        }
        if (count < 0 || size < 0 || offset < 0 || offset > endOffset || size > endOffset - offset)
            throw new ZipFormatException("Central directory is outside the archive.");
        return (offset, size, count);
    }

    /// <summary>The payload of one extra field, or empty when the archive has no such field.</summary>
    private static ReadOnlySpan<byte> FindExtra(ReadOnlySpan<byte> extra, ushort id)
    {
        var at = 0;
        while (at + 4 <= extra.Length)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(extra[at..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(extra[(at + 2)..]);
            if (at + 4 + length > extra.Length) break;
            if (tag == id) return extra.Slice(at + 4, length);
            at += 4 + length;
        }

        return [];
    }
}
