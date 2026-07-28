using GogDownloader.Core;
using GogDownloader.Core.Download;
using GogDownloader.Core.Download.Zip;

namespace GogDownloader.Cli;

/// <summary>
/// Checks the archives in a backup directory against nothing but themselves. `download` verifies
/// what it has just written against staging; this is for the archives already on disk, whose
/// staging is long gone.
/// </summary>
public static class VerifyCommands
{
    private sealed record Archive(string Slug, string Title, string Path, GamePlatform Platform);

    public static async Task<int> RunAsync(
        string[] args, Dictionary<string, string> flags, CancellationToken ct)
    {
        var outputRoot = Path.GetFullPath(flags.GetValueOrDefault("out", "gog-backup"));
        if (!Directory.Exists(outputRoot))
        {
            Console.Error.WriteLine($"No such backup directory: {outputRoot}");
            return 1;
        }

        var search = GameFilter.SearchText(args, skip: 1);
        var deep = flags.ContainsKey("deep");
        var slugsOnly = flags.ContainsKey("slugs");

        var archives = await FindArchivesAsync(outputRoot, search, ct);
        if (archives.Count == 0)
        {
            Console.Error.WriteLine("Nothing to check: no .gogdl.json in that directory.");
            return 1;
        }

        if (!slugsOnly)
        {
            Console.Error.WriteLine(
                $"{outputRoot}: checking {archives.Count} {(archives.Count == 1 ? "archive" : "archives")}" +
                (deep ? ", inflating every entry" : " (headers only, pass --deep to inflate everything)"));
            Console.Error.WriteLine();
        }

        var broken = new List<Archive>();
        var missing = new List<Archive>();
        var checkedCount = 0;

        foreach (var archive in archives)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(archive.Path))
            {
                missing.Add(archive);
                if (!slugsOnly) Report(archive, [new ArchiveProblem("", "the archive is gone")]);
                continue;
            }

            if (!slugsOnly)
                Console.Error.Write($"\r  [{++checkedCount}/{archives.Count}] {archive.Slug}".PadRight(78)[..78]);

            List<ArchiveProblem> problems;
            try
            {
                problems = ArchiveCheck.Headers(archive.Path);
                if (deep && problems.Count == 0)
                    problems = await ArchiveCheck.DeepAsync(archive.Path, ct: ct);
            }
            catch (Exception e) when (e is ZipFormatException or InvalidDataException or IOException)
            {
                problems = [new ArchiveProblem("", e.Message)];
            }

            if (problems.Count == 0) continue;

            broken.Add(archive);
            if (!slugsOnly) Report(archive, problems);
        }

        if (slugsOnly)
        {
            foreach (var slug in broken.Concat(missing).Select(a => a.Slug).Distinct())
                Console.WriteLine(slug);
            return broken.Count + missing.Count > 0 ? 1 : 0;
        }

        Console.Error.Write($"\r{new string(' ', 78)}\r");
        Console.WriteLine();
        Console.WriteLine(
            $"{archives.Count - broken.Count - missing.Count} sound, {broken.Count} broken, {missing.Count} missing.");

        if (broken.Count + missing.Count == 0) return 0;

        // Re-downloading is cheap: the old archive is still a cache, so every file that is
        // intact comes off the disk rather than the network.
        Console.WriteLine();
        Console.WriteLine("Rewrite them with:");
        foreach (var archive in broken.Concat(missing).DistinctBy(a => (a.Slug, a.Platform)))
            Console.WriteLine(
                $"  gogdl download {archive.Slug} --force --platform {archive.Platform.ApiName()} --out \"{outputRoot}\"");

        return 1;
    }

    private static void Report(Archive archive, List<ArchiveProblem> problems)
    {
        Console.Error.Write($"\r{new string(' ', 78)}\r");
        Console.WriteLine($"{archive.Title} — {Path.GetFileName(archive.Path)}");

        foreach (var problem in problems.Take(10))
            Console.WriteLine(problem.Entry.Length == 0
                ? $"  {problem.Problem}"
                : $"  {problem.Entry}: {problem.Problem}");

        if (problems.Count > 10)
            Console.WriteLine($"  ... and {problems.Count - 10} more entries");

        Console.WriteLine();
    }

    /// <summary>
    /// Every archive a manifest claims. Going by the manifests rather than by `*.zip` keeps a
    /// leftover from an older build out of the report, and gives each archive its slug back.
    /// </summary>
    private static async Task<List<Archive>> FindArchivesAsync(
        string outputRoot, string search, CancellationToken ct)
    {
        var archives = new List<Archive>();

        foreach (var directory in Directory.EnumerateDirectories(outputRoot).Order(StringComparer.Ordinal))
        {
            if (await BackupManifest.LoadAsync(directory, ct) is not { } manifest) continue;

            if (search.Length > 0
                && !manifest.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !manifest.Slug.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var (platform, backup) in manifest.Platforms)
                archives.Add(new Archive(
                    manifest.Slug, manifest.Title, Path.Combine(directory, backup.ArchiveName), platform));
        }

        return archives;
    }
}
