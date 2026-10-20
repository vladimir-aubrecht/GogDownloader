using System.Buffers;

namespace GameArchiver.Core;

public static class ArchiveStorage
{
    private static void CopyInterruptibly(
        Stream source, Stream destination, CancellationToken ct, Action<int>? onWritten = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
                onWritten?.Invoke(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// How many bytes short the volume is, or null when it fits. Returns null when the volume
    /// cannot be inspected — a network share is worth attempting rather than refusing.
    /// </summary>
    public static long? FreeSpaceShortfall(string path, long needed)
    {
        try
        {
            // On Unix the full path selects the filesystem for statvfs. Taking its path root
            // would always query /, even for staging on a large disk mounted under /mnt.
            var free = new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;
            return free < needed ? needed - free : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Human(long bytes) => $"{bytes / 1024d / 1024 / 1024:F1} GiB";

    /// <summary>Bytes already on disk, used both to size a resume and to report one.</summary>
    public static long DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// A rename is only atomic within one mounted filesystem. On Unix every absolute path
    /// starts with /, including a NAS and a separate staging disk. If the mount cannot be
    /// established, use the interruptible copy-to-temp path rather than assume a local move.
    /// </summary>
    public static bool SameVolume(string a, string b)
    {
        var first = VolumeFor(a);
        var second = VolumeFor(b);
        return first is not null && second is not null && string.Equals(first, second,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string? VolumeFor(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            // A symlink or junction in any ancestor can cross volumes without changing the
            // lexical path. Conservatively copy through .tmp rather than guess its target.
            for (var ancestor = fullPath; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                if (Directory.Exists(ancestor)
                    && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    return null;

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return DriveInfo.GetDrives()
                .Select(drive => Path.TrimEndingDirectorySeparator(Path.GetFullPath(drive.Name)))
                .Where(mount => string.Equals(fullPath, mount, comparison)
                    || fullPath.StartsWith(Path.EndsInDirectorySeparator(mount)
                        ? mount : mount + Path.DirectorySeparatorChar, comparison))
                .OrderByDescending(mount => mount.Length)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Moves the finished zip into place. A plain rename when staging shares the target's
    /// volume; otherwise a copy to a sibling temp file followed by a rename, so the target
    /// name still appears atomically and a crash cannot leave a half-written archive.
    /// </summary>
    public static void PublishArchive(
        string stagingZip, string archivePath, bool sameVolume,
        IProgress<ArchiveProgress>? progress, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (sameVolume)
        {
            File.Move(stagingZip, archivePath, overwrite: true);
            return;
        }

        var total = new FileInfo(stagingZip).Length;
        long copied = 0;

        var temp = archivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // File.Copy would ignore Ctrl+C for the whole transfer, which over a network
            // share is exactly the part worth interrupting. It would also say nothing while
            // tens of gigabytes crawl across the wire.
            using (var source = File.OpenRead(stagingZip))
            using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                CopyInterruptibly(source, destination, ct, written =>
                    progress?.Report(new ArchiveProgress("copy", copied += written, total, 0, 1)));
            ct.ThrowIfCancellationRequested();
            File.Move(temp, archivePath, overwrite: true);
        }
        catch
        {
            // Cancelled, or the target volume filled up. Either way the partial copy must not
            // survive next to the archive it pretends to be.
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }

        File.Delete(stagingZip);
        progress?.Report(new ArchiveProgress("copy", total, total, 1, 1));
    }

}
