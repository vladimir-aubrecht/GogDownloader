using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using GogDownloader.Core.Download.Zip;

namespace GogDownloader.Core.Content;

/// <summary>One entry of the ZIP payload buried inside a MojoSetup installer.</summary>
public sealed record InstallerEntry(
    string Name,
    long DataOffset,
    long CompressedSize,
    long Size,
    uint Crc32,
    bool Deflated,
    bool IsSymlink,
    bool IsDirectory);

public sealed class InstallerFormatException(string message) : Exception(message);

/// <summary>
/// Reads a Linux game out of its MojoSetup installer without ever downloading the installer.
///
/// The installer is a shell script with a ZIP appended. A ZIP keeps its central directory at
/// the end, so HTTP Range requests can fetch the directory and then only the entries wanted —
/// for a 10 GiB installer that is a few hundred kilobytes of metadata plus the game itself.
///
/// Verified against heroic-gogdl `gogdl/dl/objects/linux.py`.
/// </summary>
public sealed class MojoSetupReader(HttpClient http, string url)
{
    private const uint LocalFileHeader = 0x04034b50;
    private const uint CentralHeader = 0x02014b50;
    private const uint EndOfCentralDirectory = 0x06054b50;
    private const uint Zip64Locator = 0x07064b50;
    private const uint Zip64EndOfCentralDirectory = 0x06064b50;

    /// <summary>The shell script prologue is small; the ZIP starts inside the first megabytes.</summary>
    private const int PrologueSearchRange = 2 * 1024 * 1024;

    /// <summary>Everything else in the archive is installer scaffolding.</summary>
    public const string GamePrefix = "data/noarch/";

    private long _archiveStart;
    private long _fileSize;

    public IReadOnlyList<InstallerEntry> Entries { get; private set; } = [];

    public async Task OpenAsync(CancellationToken ct)
    {
        var prologue = await ReadRangeAsync(0, PrologueSearchRange, ct);

        _archiveStart = IndexOfSignature(prologue, LocalFileHeader);
        if (_archiveStart < 0)
            throw new InstallerFormatException("No ZIP payload found in the first 2 MiB of the installer.");

        // The end of central directory is at most 22 bytes plus a comment; 100 covers it and the
        // zip64 locator that may sit in front of it.
        var tail = await ReadRangeAsync(_fileSize - 100, 100, ct);

        var eocd = IndexOfSignature(tail, EndOfCentralDirectory);
        if (eocd < 0) throw new InstallerFormatException("No end-of-central-directory record.");

        var records = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan((int)eocd + 10));
        long directorySize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan((int)eocd + 12));
        long directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan((int)eocd + 16));

        if (directoryOffset == uint.MaxValue)
        {
            var locator = IndexOfSignature(tail, Zip64Locator);
            if (locator < 0) throw new InstallerFormatException("Zip64 archive without a locator.");

            var zip64Offset = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan((int)locator + 8));
            var zip64 = await ReadRangeAsync(_archiveStart + zip64Offset, 56, ct);

            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64EndOfCentralDirectory)
                throw new InstallerFormatException("Zip64 end-of-central-directory not where the locator says.");

            records = (ushort)Math.Min(BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(32)), ushort.MaxValue);
            directorySize = BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(40));
            directoryOffset = BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(48));
        }

        var directory = await ReadRangeAsync(_archiveStart + directoryOffset, (int)directorySize, ct);
        Entries = ParseCentralDirectory(directory, records, directoryOffset);
    }

    /// <summary>
    /// A central directory record does not say where its data begins, only where its local
    /// header does. The data of one entry ends where the next entry's header starts, so the
    /// offset can be derived without a range request per file — which is the whole point.
    /// </summary>
    private static List<InstallerEntry> ParseCentralDirectory(byte[] data, int records, long directoryOffset)
    {
        var headers = new List<(string Name, long LocalOffset, long CompressedSize, long Size, uint Crc, bool Deflated, bool Symlink, bool Directory)>(records);

        var position = 0;
        for (var i = 0; i < records && position + 46 <= data.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position)) != CentralHeader)
                throw new InstallerFormatException("Malformed central directory.");

            var method = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 10));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 16));
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 20));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 24));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 32));
            var externalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 38));
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 42));

            var name = Encoding.UTF8.GetString(data, position + 46, nameLength);
            var extra = data.AsSpan(position + 46 + nameLength, extraLength);

            // Zip64 supplies whichever of the three the 32-bit fields could not hold, in order.
            if (size == uint.MaxValue || compressed == uint.MaxValue || localOffset == uint.MaxValue)
            {
                var zip64 = FindExtraField(extra, 0x0001);
                var cursor = 0;
                if (size == uint.MaxValue) { size = BinaryPrimitives.ReadInt64LittleEndian(zip64[cursor..]); cursor += 8; }
                if (compressed == uint.MaxValue) { compressed = BinaryPrimitives.ReadInt64LittleEndian(zip64[cursor..]); cursor += 8; }
                if (localOffset == uint.MaxValue) localOffset = BinaryPrimitives.ReadInt64LittleEndian(zip64[cursor..]);
            }

            // The high 16 bits of the external attributes are the unix mode.
            var mode = externalAttributes >> 16;
            var symlink = (mode & 0xF000) == 0xA000;

            headers.Add((name, localOffset, compressed, size, crc, method == 8, symlink, name.EndsWith('/')));
            position += 46 + nameLength + extraLength + commentLength;
        }

        var entries = new List<InstallerEntry>(headers.Count);
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];

            // Data ends where the next local header begins; the last entry ends at the directory.
            var nextStart = i + 1 < headers.Count ? headers[i + 1].LocalOffset : directoryOffset;
            var dataOffset = nextStart - header.CompressedSize;

            entries.Add(new InstallerEntry(
                header.Name, dataOffset, header.CompressedSize, header.Size,
                header.Crc, header.Deflated, header.Symlink, header.Directory));
        }

        return entries;
    }

    private static ReadOnlySpan<byte> FindExtraField(ReadOnlySpan<byte> extra, ushort wanted)
    {
        var position = 0;
        while (position + 4 <= extra.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra[position..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(position + 2)..]);
            if (id == wanted) return extra.Slice(position + 4, size);
            position += 4 + size;
        }

        throw new InstallerFormatException($"Missing extra field {wanted:X4} in the central directory.");
    }

    /// <summary>Reads one entry, inflating it when needed and checking its CRC.</summary>
    public async Task<byte[]> ReadEntryAsync(
        InstallerEntry entry, IProgress<long>? bytes, CancellationToken ct)
    {
        var raw = await ReadRangeAsync(_archiveStart + entry.DataOffset, (int)entry.CompressedSize, ct, bytes);

        byte[] content;
        if (entry.Deflated)
        {
            await using var deflate = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
            var output = new MemoryStream(capacity: (int)Math.Max(entry.Size, 1));
            await deflate.CopyToAsync(output, ct);
            content = output.ToArray();
        }
        else
        {
            content = raw;
        }

        if (content.Length != entry.Size)
            throw new InstallerFormatException($"{entry.Name}: expected {entry.Size} bytes, got {content.Length}.");

        var crc = new Crc32();
        crc.Append(content);
        if (crc.Value != entry.Crc32)
            throw new ChunkChecksumException(entry.Crc32.ToString("x8"), crc.Value.ToString("x8"));

        return content;
    }

    private async Task<byte[]> ReadRangeAsync(
        long from, int length, CancellationToken ct, IProgress<long>? bytes = null)
    {
        from = Math.Max(from, 0);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, from + length - 1);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        // Any ranged response tells us how long the installer is.
        if (_fileSize == 0 && response.Content.Headers.ContentRange?.Length is { } total)
            _fileSize = total;

        var data = await response.Content.ReadAsByteArrayAsync(ct);
        bytes?.Report(data.Length);
        return data;
    }

    private static long IndexOfSignature(byte[] data, uint signature)
    {
        Span<byte> needle = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(needle, signature);
        return data.AsSpan().IndexOf(needle);
    }
}
