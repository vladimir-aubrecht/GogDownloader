using GogArchiver.Core.Download.Zip;

namespace GameArchiver.Core;

public static class ArchivePublisher
{
    public static async Task PublishAsync(string staging, string archivePath, int compressionThreads,
        bool verify = true, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, UnixFileMode>? unixModes = null)
    {
        var zip = staging + ".zip";
        if (File.Exists(zip)) File.Delete(zip);
        try
        {
            await ParallelZipWriter.CreateAsync(staging, zip, compressionThreads,
                progress: new InlineProgress<ZipProgress>(p => progress?.Report(
                    new("zip", p.BytesRead, p.BytesTotal, p.EntriesDone, p.EntriesTotal))), ct: ct, unixModes: unixModes);
            if (verify)
                await ArchiveVerifier.VerifyAsync(zip, staging, new InlineProgress<ZipProgress>(p => progress?.Report(
                    new("check", p.BytesRead, p.BytesTotal, p.EntriesDone, p.EntriesTotal))), ct);
            ct.ThrowIfCancellationRequested();
            ArchiveStorage.PublishArchive(zip, archivePath, ArchiveStorage.SameVolume(zip, archivePath), progress, ct);
            progress?.Report(new("publish", Files: 1, TotalFiles: 1));
        }
        finally
        {
            // Verification/publication can be interrupted after compression completed too.
            try { File.Delete(zip); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
