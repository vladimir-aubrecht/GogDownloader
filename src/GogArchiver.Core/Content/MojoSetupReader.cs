using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using GogArchiver.Core.Download.Zip;

namespace GogArchiver.Core.Content;

/// <summary>One entry of the ZIP payload buried inside a MojoSetup installer.</summary>
public sealed record InstallerEntry(
    string Name,
    long DataOffset,
    long CompressedSize,
    long Size,
    uint Crc32,
    bool Deflated,
    bool IsSymlink,
    bool IsDirectory)
{
    public UnixFileMode? UnixMode { get; init; }
}

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
public sealed class MojoSetupReader(HttpClient http, string url, RateLimiter? limiter = null)
{
    private readonly RateLimiter _limiter = limiter ?? new(0);

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

        // Include the maximum ZIP comment and the ZIP64 locator, not just a fixed 100 bytes.
        var tailSize = (int)Math.Min(_fileSize, 66_000);
        var tail = await ReadRangeAsync(_fileSize - tailSize, tailSize, ct);
        var eocd = -1;
        for (var at = tail.Length - 22; at >= 0; at--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(at)) == EndOfCentralDirectory
                && at + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(at + 20)) == tail.Length)
            { eocd = at; break; }
        if (eocd < 0) throw new InstallerFormatException("No complete end-of-central-directory record.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 4)) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 6)) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 8)) != BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10)))
            throw new InstallerFormatException("Split installer ZIPs are unsupported.");
        var directoryEnd = _fileSize - tailSize + eocd - _archiveStart;
        long records = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
        long directorySize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan((int)eocd + 12));
        long directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan((int)eocd + 16));

        var locator = eocd - 20;
        var hasLocator = locator >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator)) == Zip64Locator;
        // Exactly 65,535 entries still fit in a classic ZIP; validate the full directory.
        if (directoryOffset == uint.MaxValue || directorySize == uint.MaxValue || records == ushort.MaxValue && hasLocator)
        {
            if (locator < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator)) != Zip64Locator
                || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator + 4)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator + 16)) != 1)
                throw new InstallerFormatException("Missing or invalid ZIP64 locator.");

            var zip64Offset = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan((int)locator + 8));
            if (zip64Offset < 0 || zip64Offset > directoryEnd - 20 - 56)
                throw new InstallerFormatException("ZIP64 end record is outside the installer ZIP.");
            var zip64 = await ReadRangeAsync(_archiveStart + zip64Offset, 56, ct);

            if (zip64.Length != 56 || BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64EndOfCentralDirectory
                || BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(4)) < 44
                || BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(4)) > directoryEnd - 20 - zip64Offset - 12
                || BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(16)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(20)) != 0
                || BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(24)) != BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(32)))
                throw new InstallerFormatException("Zip64 end-of-central-directory not where the locator says.");

            var actualCount = BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(32));
            var actualSize = BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(40));
            var actualOffset = BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(48));
            if (records != ushort.MaxValue && records != actualCount || directorySize != uint.MaxValue && directorySize != actualSize
                || directoryOffset != uint.MaxValue && directoryOffset != actualOffset)
                throw new InstallerFormatException("ZIP and ZIP64 end records disagree.");
            records = actualCount; directorySize = actualSize; directoryOffset = actualOffset;
            directoryEnd = zip64Offset;
        }

        if (records < 0 || directorySize < 0 || directorySize > int.MaxValue || records > directorySize / 46
            || directoryOffset < 0 || directoryOffset > directoryEnd || directorySize > directoryEnd - directoryOffset)
            throw new InstallerFormatException("Invalid installer ZIP directory size, count or offset.");
        var directory = await ReadRangeAsync(_archiveStart + directoryOffset, (int)directorySize, ct);
        Entries = ParseCentralDirectory(directory, (int)records, directoryOffset);
    }

    /// <summary>
    /// A central directory record does not say where its data begins, only where its local
    /// header does. The data of one entry ends where the next entry's header starts, so the
    /// offset can be derived without a range request per file — which is the whole point.
    /// </summary>
    private static List<InstallerEntry> ParseCentralDirectory(byte[] data, int records, long directoryOffset)
    {
        var headers = new List<(string Name, long LocalOffset, long CompressedSize, long Size, uint Crc, bool Deflated, bool Symlink, bool Directory, UnixFileMode? Mode)>(records);

        var position = 0;
        for (var i = 0; i < records; i++)
        {
            if (data.Length - position < 46 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position)) != CentralHeader)
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

            if (data.Length - position < 46 + nameLength + extraLength + commentLength
                || method is not 0 and not 8 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 34)) != 0)
                throw new InstallerFormatException("Truncated or unsupported installer ZIP entry.");
            var name = Encoding.UTF8.GetString(data, position + 46, nameLength);
            var extra = data.AsSpan(position + 46 + nameLength, extraLength);

            // Zip64 supplies whichever of the three the 32-bit fields could not hold, in order.
            if (size == uint.MaxValue || compressed == uint.MaxValue || localOffset == uint.MaxValue)
            {
                var zip64 = FindExtraField(extra, 0x0001);
                var needed = (size == uint.MaxValue ? 8 : 0) + (compressed == uint.MaxValue ? 8 : 0) + (localOffset == uint.MaxValue ? 8 : 0);
                if (zip64.Length < needed) throw new InstallerFormatException("Truncated ZIP64 entry fields.");
                var cursor = 0;
                if (size == uint.MaxValue) { size = BinaryPrimitives.ReadInt64LittleEndian(zip64[cursor..]); cursor += 8; }
                if (compressed == uint.MaxValue) { compressed = BinaryPrimitives.ReadInt64LittleEndian(zip64[cursor..]); cursor += 8; }
                if (localOffset == uint.MaxValue) localOffset = BinaryPrimitives.ReadInt64LittleEndian(zip64[cursor..]);
            }

            // The high 16 bits of the external attributes are the unix mode.
            var mode = externalAttributes >> 16;
            var symlink = (mode & 0xF000) == 0xA000;

            if (size < 0 || compressed < 0 || localOffset < 0 || localOffset > directoryOffset - 30)
                throw new InstallerFormatException("Invalid installer ZIP entry size or offset.");
            var unix = data[position + 5] == 3;
            headers.Add((name, localOffset, compressed, size, crc, method == 8, symlink, name.EndsWith('/'),
                unix ? (UnixFileMode?)(mode & 0x1ff) : null));
            position += 46 + nameLength + extraLength + commentLength;
        }

        if (position != data.Length) throw new InstallerFormatException("Installer ZIP directory size/count mismatch.");
        // Central directory order need not match the physical local-header order.
        headers.Sort((a, b) => a.LocalOffset.CompareTo(b.LocalOffset));
        var entries = new List<InstallerEntry>(headers.Count);
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];

            // Data ends where the next local header begins; the last entry ends at the directory.
            var nextStart = i + 1 < headers.Count ? headers[i + 1].LocalOffset : directoryOffset;
            var dataOffset = nextStart - header.CompressedSize;
            if (dataOffset < header.LocalOffset + 30)
                throw new InstallerFormatException("Overlapping installer ZIP entries.");

            entries.Add(new InstallerEntry(
                header.Name, dataOffset, header.CompressedSize, header.Size,
                header.Crc, header.Deflated, header.Symlink, header.Directory) { UnixMode = header.Mode });
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
            if (size > extra.Length - position - 4) throw new InstallerFormatException("Truncated ZIP extra field.");
            if (id == wanted) return extra.Slice(position + 4, size);
            position += 4 + size;
        }

        throw new InstallerFormatException($"Missing extra field {wanted:X4} in the central directory.");
    }

    /// <summary>Buffer a small entry, such as a symlink target. Game files use CopyEntryAsync.</summary>
    public async Task<byte[]> ReadEntryAsync(
        InstallerEntry entry, IProgress<long>? bytes, CancellationToken ct)
    {
        if (entry.Size > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(entry), "Use CopyEntryAsync for files beyond the array size limit.");
        using var output = new MemoryStream();
        await CopyEntryAsync(entry, output, bytes, ct);
        return output.ToArray();
    }

    /// <summary>Stream and verify one entry without buffering its compressed or installed size.</summary>
    public async Task CopyEntryAsync(
        InstallerEntry entry, Stream destination, IProgress<long>? bytes, CancellationToken ct)
    {
        var crc = new Crc32();
        if (entry.CompressedSize > 0)
        {
            var from = _archiveStart + entry.DataOffset;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(from, checked(from + entry.CompressedSize - 1));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var raw = await response.Content.ReadAsStreamAsync(ct);
            using var compressed = new EntryReadStream(raw, entry.CompressedSize, bytes, _limiter);
            await using var content = entry.Deflated
                ? (Stream)new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: true) : compressed;
            var buffer = new byte[64 * 1024];
            long written = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                written += read;
                if (written > entry.Size)
                    throw new InstallerFormatException($"{entry.Name}: more than {entry.Size} bytes after decompression.");
                crc.Append(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (written != entry.Size)
                throw new InstallerFormatException($"{entry.Name}: expected {entry.Size} bytes, got {written}.");
            // A deflate decoder may finish before reading the very end of its HTTP range.
            while (await compressed.ReadAsync(buffer, ct) > 0) { }
            if (compressed.Position != entry.CompressedSize || await raw.WithStallTimeoutAsync(buffer.AsMemory(0, 1), ct) != 0)
                throw new InstallerFormatException($"{entry.Name}: compressed size does not match {entry.CompressedSize}.");
        }
        else if (entry.Size != 0)
            throw new InstallerFormatException($"{entry.Name}: nonempty entry has no compressed data.");

        if (crc.Value != entry.Crc32)
            throw new ChunkChecksumException(entry.Crc32.ToString("x8"), crc.Value.ToString("x8"));
    }

    private sealed class EntryReadStream(Stream source, long size, IProgress<long>? progress, RateLimiter limiter) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var remaining = size - _read;
            if (remaining == 0) return 0;
            var read = await source.WithStallTimeoutAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], ct);
            await limiter.ConsumeAsync(read, ct);
            _read += read;
            progress?.Report(read);
            return read;
        }
    }

    private async Task<byte[]> ReadRangeAsync(
        long from, int length, CancellationToken ct, IProgress<long>? bytes = null)
    {
        if (from < 0 || length < 0) throw new InstallerFormatException("Invalid installer ZIP range.");
        if (length == 0) return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, from + length - 1);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent || range?.Length is not > 0
            || range.From != from || range.To != Math.Min(from + length - 1, range.Length.Value - 1)
            || _fileSize != 0 && _fileSize != range.Length.Value)
            throw new InstallerFormatException("The installer server did not return the requested byte range.");
        _fileSize = range.Length.Value;
        var expected = range.To!.Value - from + 1;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var data = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.WithStallTimeoutAsync(buffer, ct)) > 0)
        {
            await _limiter.ConsumeAsync(read, ct);
            if (data.Length + read > expected) throw new InstallerFormatException("Installer range is too long.");
            data.Write(buffer, 0, read);
            bytes?.Report(read);
        }
        if (data.Length != expected) throw new InstallerFormatException("Truncated installer ZIP range.");
        return data.ToArray();
    }

    private static long IndexOfSignature(byte[] data, uint signature)
    {
        Span<byte> needle = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(needle, signature);
        return data.AsSpan().IndexOf(needle);
    }
}
