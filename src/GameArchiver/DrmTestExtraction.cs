using System.IO.Compression;
using System.Diagnostics;
using GameArchiver.Core;
using GogArchiver.Core.Download.Zip;

namespace GameArchiver;

internal static class DrmTestExtraction
{
    public static void CheckAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Test extraction cannot cross a link: {current}");
    }

    private static string Relative(string path)
    {
        var relative = ArchivePaths.Relative(path);
        foreach (var part in relative.Split('/'))
        {
            var stem = part.Split('.')[0].ToLowerInvariant();
            if (part.Any(c => c < 32 || "<>\"|?*".Contains(c)) || part.EndsWith('.') || part.EndsWith(' ')
                || stem is "con" or "prn" or "aux" or "nul"
                || stem.Length == 4 && (stem.StartsWith("com") || stem.StartsWith("lpt")) && stem[3] is >= '1' and <= '9')
                throw new InvalidDataException($"Unsafe Windows archive path '{path}'.");
        }
        return relative;
    }

    public static DrmTestLaunch Launch(string root, string executable, string arguments, string workingDirectory)
    {
        if (arguments.Any(c => c is '\0' or '\r' or '\n')) throw new InvalidDataException("Invalid launcher arguments.");
        var exe = ArchivePaths.Within(root, Relative(executable));
        var extension = Path.GetExtension(exe);
        if (!new[] { ".exe", ".bat", ".cmd" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException("Windows tests require an .exe, .bat or .cmd launcher inside the archive.");
        return new(root, exe, arguments, workingDirectory.Length == 0 ? root : ArchivePaths.Within(root, Relative(workingDirectory)));
    }

    public static async Task ExtractAsync(string zipPath, string root, IProgress<string>? progress, CancellationToken ct)
    {
        CheckAncestors(root);
        List<ArchiveProblem> problems;
        try { problems = ArchiveCheck.Headers(zipPath); }
        catch (ZipFormatException e) { throw new InvalidDataException("Cannot test a malformed ZIP: " + e.Message, e); }
        if (problems.Count != 0) throw new InvalidDataException($"Cannot test a damaged archive: {problems[0].Problem}");
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = new List<(ZipArchiveEntry Entry, string Relative, bool Directory)>();
        var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var relative = Relative(directory ? entry.FullName[..^1] : entry.FullName);
            var type = (entry.ExternalAttributes >> 16) & 0xf000;
            if (type is not 0 and not 0x8000 and not 0x4000 || directory && entry.Length != 0 || !directory && type == 0x4000)
                throw new InvalidDataException($"Links and special ZIP entries cannot be tested: {entry.FullName}");
            if (!names.TryAdd(relative, directory)) throw new InvalidDataException($"Duplicate ZIP path: {relative}");
            total = checked(total + entry.Length);
            entries.Add((entry, relative, directory));
        }
        foreach (var item in entries)
            for (var parent = Path.GetDirectoryName(item.Relative); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (names.TryGetValue(parent.Replace('\\', '/'), out var directory) && !directory)
                    throw new InvalidDataException($"A ZIP file is also used as a directory: {parent}");
        if (ArchiveStorage.FreeSpaceShortfall(root, total) is { } shortfall)
            throw new IOException($"Insufficient local space for test extraction: {shortfall} additional bytes needed.");
        progress?.Report($"Extracting {entries.Count} entries ({total} bytes) for local testing.");
        var buffer = new byte[1024 * 1024];
        var watch = Stopwatch.StartNew();
        long extracted = 0;
        foreach (var (entry, relative, directory) in entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = ArchivePaths.Within(root, relative);
            if (directory) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var crc = new Crc32(); long size = 0; int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                size = checked(size + count);
                if (size > entry.Length) throw new InvalidDataException($"ZIP entry exceeds declared size: {relative}");
                crc.Append(buffer.AsSpan(0, count));
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                extracted += count;
                if (watch.Elapsed >= TimeSpan.FromSeconds(5))
                { progress?.Report($"Extracting: {extracted}/{total} bytes."); watch.Restart(); }
            }
            if (size != entry.Length || crc.Value != entry.Crc32)
                throw new InvalidDataException($"ZIP integrity check failed: {relative}");
        }
    }
}
