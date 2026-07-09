namespace GogDownloader.Core.Download.Zip;

/// <summary>
/// CRC-32 (IEEE), which ZIP requires per entry. Hand-rolled because `System.IO.Hashing`
/// is a NuGet package and this repo takes no dependencies.
/// </summary>
public sealed class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private uint _crc = 0xFFFFFFFF;

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        var crc = _crc;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        _crc = crc;
    }

    public uint Value => _crc ^ 0xFFFFFFFF;
}
