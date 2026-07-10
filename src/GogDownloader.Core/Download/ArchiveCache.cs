using System.IO.Compression;
using System.Security.Cryptography;

namespace GogDownloader.Core.Download;

/// <summary>
/// The previous backup, used as a cache for the next one.
///
/// A new build usually changes a handful of files. The manifest records each file's hash, so a
/// file whose hash is unchanged can be lifted straight out of the old zip instead of being
/// downloaded again. For a 60 GiB game that is the difference between a patch and a re-download.
///
/// Content is addressed by hash, not by path: a file that merely moved is still a hit. Nothing
/// is trusted on the strength of the manifest alone — every extracted file is hashed and
/// discarded if it does not match.
/// </summary>
public sealed class ArchiveCache : IDisposable
{
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, string> _byHash;

    private ArchiveCache(ZipArchive archive, Dictionary<string, string> byHash)
    {
        _archive = archive;
        _byHash = byHash;
    }

    public int Count => _byHash.Count;

    /// <summary>Null when there is no previous backup, or it is unreadable.</summary>
    public static ArchiveCache? Open(string archivePath, IReadOnlyDictionary<string, BackedUpFile> files)
    {
        if (files.Count == 0 || !File.Exists(archivePath)) return null;

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            // A corrupt or vanished archive is a cache miss, not a failure.
            return null;
        }

        var byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, file) in files)
            byHash.TryAdd(file.Md5, path);

        return new ArchiveCache(archive, byHash);
    }

    /// <summary>
    /// Writes the cached file at <paramref name="identity"/> to <paramref name="destination"/>,
    /// or returns false when it is absent, the wrong size, or fails its hash.
    /// </summary>
    public async Task<bool> TryRestoreAsync(string identity, string destination, long size, CancellationToken ct)
    {
        if (!_byHash.TryGetValue(identity, out var name)) return false;
        if (_archive.GetEntry(name) is not { } entry || entry.Length != size) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        try
        {
            await using (var source = entry.Open())
            await using (var target = File.Create(destination))
                await source.CopyToAsync(target, ct);
        }
        catch (InvalidDataException)
        {
            SafeDelete(destination);
            return false;
        }

        // ZipFile does not verify CRCs on read, so the old archive proves nothing by itself.
        await using (var written = File.OpenRead(destination))
        {
            var actual = Convert.ToHexStringLower(await MD5.HashDataAsync(written, ct));
            if (string.Equals(actual, identity, StringComparison.OrdinalIgnoreCase)) return true;
        }

        SafeDelete(destination);
        return false;
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* the caller treats this as a miss either way */ }
    }

    public void Dispose() => _archive.Dispose();
}
