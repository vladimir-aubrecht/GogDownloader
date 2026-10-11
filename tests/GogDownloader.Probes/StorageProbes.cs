using GogDownloader.Core.Download;

namespace GogDownloader.Probes;

public static class StorageProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Linux mount points");
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm"))
        {
            Probe.Note("The live cross-filesystem checks require Linux /dev/shm.");
            return;
        }

        var root = Probe.ScratchDirectory();
        var other = Path.Combine("/dev/shm", "gogdl-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            var rootFree = new DriveInfo("/").AvailableFreeSpace;
            var otherFree = new DriveInfo(other).AvailableFreeSpace;
            Probe.Check("local disk and tmpfs are different volumes", !GameDownloader.SameVolume(root, other));
            Probe.Check("two directories on tmpfs share a volume",
                GameDownloader.SameVolume(other, Path.Combine(other, "subdir", "archive.zip")));
            Probe.Check("a mount prefix is matched at a directory boundary",
                !GameDownloader.SameVolume(other, "/dev/shm-other/archive.zip"));
            Probe.Check("Linux mount names are case-sensitive",
                !GameDownloader.SameVolume(other, "/dev/SHM/archive.zip"));
            Probe.Check("an unknown volume never takes the local-move shortcut",
                !GameDownloader.SameVolume(root, "\0::invalid"));

            // No large allocation: a request between the two free-space values distinguishes
            // statvfs(staging) from the broken statvfs("/") call.
            if (Math.Abs(rootFree - otherFree) > 64 * 1024 * 1024)
            {
                var needed = Math.Min(rootFree, otherFree) + Math.Abs(rootFree - otherFree) / 2;
                Probe.Check("free space is checked on the selected mount, not /",
                    (GameDownloader.FreeSpaceShortfall(other, needed) is > 0) == (otherFree < needed));
            }

            // A symlink must not make a remote staging/backup volume look like the local disk.
            var linked = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(linked, other);
            Probe.Check("a symlink into another volume is not considered local",
                !GameDownloader.SameVolume(root, Path.Combine(linked, "game", "archive.zip")));

            var source = Path.Combine(root, "source.zip");
            var target = Path.Combine(other, "target.zip");
            await File.WriteAllBytesAsync(source, new byte[2 * 1024 * 1024]);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var cancelled = false;
            try
            {
                GameDownloader.PublishArchive(source, target, GameDownloader.SameVolume(source, target), null, cts.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Probe.Check("publishing to another Linux mount honours cancellation", cancelled);
            Probe.Check("cancelled publication keeps the source and exposes no final archive",
                File.Exists(source) && !File.Exists(target) && !File.Exists(target + ".tmp"));

            await File.WriteAllBytesAsync(source, new byte[2 * 1024 * 1024]);
            var reports = new List<DownloadProgress>();
            GameDownloader.PublishArchive(source, target, GameDownloader.SameVolume(source, target),
                new SyncProgress<DownloadProgress>(reports.Add));
            Probe.Check("publication across real mounts uses the progress-reporting copy",
                reports.Count > 0 && reports[^1] is { Phase: "copy", Bytes: 2 * 1024 * 1024 });
            Probe.Check("publication across real mounts leaves only the complete target",
                new FileInfo(target).Length == 2 * 1024 * 1024
                && !File.Exists(source) && !File.Exists(target + ".tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }
}
