using GameArchiver.Core;

namespace GameArchiver.Cli;

public static partial class ArchiveCommandLine
{
    private static async Task<int> RunDrmTestsAsync(GameArchiverClient archive, ArchiveLibrary library, string snapshot,
        List<string> args, Dictionary<string, string> flags, Dictionary<string, string> providerDirectories,
        string source, IDrmTestRuntime? runtime, CancellationToken ct)
    {
        var show = args[2] == "tests";
        var allowed = new HashSet<string> { "source", "snapshot", "platform" };
        allowed.UnionWith(show ? ["details"] : new[] { "out", "layout", "staging", "observe-seconds", "no-prompt", "retest", "dry-run",
            "include-non-games", "executable", "arguments", "working-directory" });
        if (flags.Keys.FirstOrDefault(k => !allowed.Contains(k)) is { } unsupported)
            throw new ArgumentException($"--{unsupported} is not supported by library drm {args[2]}.");
        if (show && providerDirectories.Count > 0) throw new ArgumentException("--provider-dir is not supported by library drm tests.");
        var options = Options(flags, providerDirectories);
        var search = string.Join(' ', args.Skip(3));
        if (show)
        {
            var records = library.DrmTests.Where(t => (source == "all" || t.Key.Source == source)
                && (!flags.ContainsKey("platform") || options.Platform == t.Platform)
                && (search.Length == 0 || t.Key.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
                    || library.Games.FirstOrDefault(g => g.Key == t.Key)?.Title.Contains(search, StringComparison.OrdinalIgnoreCase) == true))
                .OrderByDescending(t => t.TestedAt).ToList();
            foreach (var record in records)
            {
                Console.WriteLine($"{record.Key} {library.Games.FirstOrDefault(g => g.Key == record.Key)?.Title}: {record.Classification.Status} ({record.Outcome}; {record.Classification.Source}, {record.Platform}, {record.TestedAt:u})");
                if (flags.ContainsKey("details")) Console.WriteLine($"  Build: {record.Version.BuildId}; branch: {record.Version.Branch}; language: {record.Version.Language}; machine: {record.Machine}\n"
                    + $"  Archive: {record.ArchiveName}; launch: {record.Executable} {record.Arguments}; working directory: {record.WorkingDirectory}\n"
                    + $"  Observed: {record.ObservedSeconds:F1}s; Steam: {string.Join(", ", record.SteamProcesses)}; catalog eligible: {record.AppliesToCatalog}\n  {record.Detail}");
            }
            Console.WriteLine($"{records.Count} saved local tests. Automatic process classifications remain below manual overrides.");
            return 0;
        }
        string? gameDirectory = null;
        if (!flags.ContainsKey("out"))
        {
            gameDirectory = Directory.GetCurrentDirectory();
            if (await ArchiveMetadata.LoadAsync(gameDirectory, ct) is null)
                throw new ArgumentException("Specify --out DIR, or run library drm test inside a game folder containing archive metadata.");
            if (flags.ContainsKey("layout") || providerDirectories.Count > 0)
                throw new ArgumentException("Output layout options require --out for DRM tests.");
        }
        var dryRun = flags.ContainsKey("dry-run");
        if (!dryRun) Console.Error.WriteLine("Autonomous DRM tests: Steam observed = protected; running without Steam = free; early exit/failure = unknown. Close Steam first; Steam started during a test is stopped automatically.");
        var request = new DrmTestOptions
        {
            Search = search, GameDirectory = gameDirectory, DryRun = dryRun, Retest = flags.ContainsKey("retest"),
            IncludeNonGames = flags.ContainsKey("include-non-games"), ObserveFor = TimeSpan.FromSeconds(Integer(flags, "observe-seconds", 60)),
            Executable = flags.GetValueOrDefault("executable"), Arguments = flags.GetValueOrDefault("arguments"), WorkingDirectory = flags.GetValueOrDefault("working-directory"),
        };
        var result = await archive.TestDrmAsync(library, options, request, async (updated, token) =>
        {
            // Preserve unrelated snapshot updates made during an expensive test.
            var latest = await ArchiveLibrary.LoadAsync(snapshot, token);
            foreach (var record in updated.DrmTests.Except(library.DrmTests))
            {
                latest.DrmTests.RemoveAll(t => t.Key == record.Key && t.Platform == record.Platform && t.Fingerprint == record.Fingerprint);
                latest.DrmTests.Add(record);
            }
            await latest.SaveAsync(snapshot, token);
        }, runtime, new InlineProgress<string>(Console.Error.WriteLine), ct);
        Console.WriteLine($"Local DRM tests: {result.Tested} saved, {result.Skipped} skipped, {result.Unavailable} unavailable. Manual overrides preserved.");
        if (result.StoppedReason is not null) { Console.Error.WriteLine(result.StoppedReason); return 1; }
        return result.Unavailable == 0 ? 0 : 1;
    }

}
