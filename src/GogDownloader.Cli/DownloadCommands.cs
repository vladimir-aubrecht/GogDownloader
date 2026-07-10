using System.Diagnostics;
using GogDownloader.Core;
using GogDownloader.Core.Download;
using GogDownloader.Core.Library;

namespace GogDownloader.Cli;

public static class DownloadCommands
{
    public static async Task<int> RunAsync(
        string[] args, Dictionary<string, string> flags, CancellationToken ct)
    {
        if (await CommandLine.LoadSnapshotAsync(flags, ct) is not { } snapshot) return 1;

        if (!GameFilter.TryPlatform(flags, out var chosen)) return 1;
        var platform = chosen ?? GamePlatform.Windows;

        List<LibraryEntry> games;

        if (flags.ContainsKey("all"))
        {
            // The same filter the listing uses, so what `library list` shows is what this takes.
            // A game with no depot cannot be fetched at all, hence --depot is implied.
            var search = GameFilter.SearchText(args, skip: 1);
            games = GameFilter.Apply(snapshot, search, platform, depotOnly: true, dlcOnly: flags.ContainsKey("dlc"));

            if (games.Count == 0)
            {
                Console.Error.WriteLine("Nothing matches.");
                return 1;
            }
        }
        else
        {
            var needle = CommandLine.Positionals(args).Skip(1).FirstOrDefault();
            if (needle is null)
            {
                Console.Error.WriteLine("Missing game. Usage: gogdl download <slug|id>, or download --all");
                return 1;
            }

            var game = snapshot.Games.FirstOrDefault(g =>
                string.Equals(g.Slug, needle, StringComparison.OrdinalIgnoreCase)
                || g.Id.ToString() == needle);

            if (game is null)
            {
                Console.Error.WriteLine($"Game '{needle}' is not in the library snapshot.");
                return 1;
            }

            games = [game];
        }

        if (!snapshot.KnowsEarlyAccess)
            Console.Error.WriteLine(
                "Note: this snapshot predates the early-access flag, so an (EA) game will be " +
                "named without it. Refresh it with: gogdl library sync");

        var downloadOptions = new DownloadOptions
        {
            OutputRoot = Path.GetFullPath(flags.GetValueOrDefault("out", "gog-backup")),
            Platform = platform,
            Language = flags.GetValueOrDefault("language", "en"),
            MaxConnections = CommandLine.OptionsFrom(flags).MaxConnections,
            StagingRoot = flags.TryGetValue("staging", out var staging) ? Path.GetFullPath(staging) : null,
            Force = flags.ContainsKey("force"),
            Clean = flags.ContainsKey("clean"),
            VerifyArchive = !flags.ContainsKey("no-verify"),
            Reuse = !flags.ContainsKey("no-reuse"),
            MaxCompressionThreads = flags.TryGetValue("compression-threads", out var t) && int.TryParse(t, out var n) && n > 0
                ? n
                : Environment.ProcessorCount,
        };

        var total = games.Sum(g => GameFilter.Size(g, platform));
        Console.Error.WriteLine(
            $"{games.Count} {(games.Count == 1 ? "game" : "games")} for {platform.ApiName()}, " +
            $"{CommandLine.Bytes(total)} to download → {downloadOptions.OutputRoot}");
        Console.Error.WriteLine(
            $"staging in {downloadOptions.StagingDirectory}, " +
            $"zipping on {downloadOptions.MaxCompressionThreads} threads");

        if (flags.ContainsKey("dry-run"))
        {
            foreach (var game in games)
                Console.WriteLine($"{CommandLine.Bytes(GameFilter.Size(game, platform)),9}  {game.Title}");
            Console.Error.WriteLine("\nDry run, nothing downloaded.");
            return 0;
        }

        return await DownloadEachAsync(games, downloadOptions, flags, ct);
    }

    private static async Task<int> DownloadEachAsync(
        List<LibraryEntry> games, DownloadOptions options, Dictionary<string, string> flags, CancellationToken ct)
    {
        var (api, content, cdn, _) = CommandLine.CreateContent(flags);
        var downloader = new GameDownloader(api, content, cdn);

        var completed = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();

        for (var i = 0; i < games.Count; i++)
        {
            var game = games[i];
            if (games.Count > 1)
                Console.Error.WriteLine($"\n[{i + 1}/{games.Count}] {game.Title}");

            DownloadResult result;
            try
            {
                result = await downloader.RunAsync(game, options, new ConsoleProgress(), ct);
            }
            catch (DownloadInterruptedException interrupted)
            {
                Console.Error.WriteLine();
                OnInterrupted(interrupted.StagingDirectory, flags);
                Summarise(completed, skipped, failed, games.Count);
                return 130;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One bad game must not abandon an overnight batch of two hundred.
                Console.Error.WriteLine($"\nFailed: {e.Message}");
                failed.Add(game.Title);
                continue;
            }

            Console.Error.WriteLine();
            switch (result.Outcome)
            {
                case DownloadOutcome.Completed:
                    Console.WriteLine($"Done: {result.ArchivePath}");
                    completed.Add(game.Title);
                    break;
                case DownloadOutcome.AlreadyDone:
                    Console.WriteLine(result.Detail);
                    skipped.Add(game.Title);
                    break;
                default:
                    Console.Error.WriteLine($"Skipped: {result.Detail}");
                    skipped.Add(game.Title);
                    break;
            }
        }

        if (games.Count > 1) Summarise(completed, skipped, failed, games.Count);

        // A single game keeps its old exit codes; a batch fails only if something failed.
        if (games.Count == 1)
            return failed.Count > 0 ? 1 : skipped.Count > 0 && completed.Count == 0 ? 3 : 0;

        return failed.Count > 0 ? 1 : 0;
    }

    private static void Summarise(List<string> completed, List<string> skipped, List<string> failed, int total)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"{completed.Count} downloaded, {skipped.Count} skipped, {failed.Count} failed, of {total}.");

        foreach (var title in failed)
            Console.Error.WriteLine($"  failed: {title}");
    }

    /// <summary>
    /// Whether to throw away a partly downloaded game is the user's call, so ask. Without a
    /// terminal there is nobody to ask, and discarding gigabytes unattended is the worse
    /// mistake, so the files stay.
    /// </summary>
    private static void OnInterrupted(string staging, Dictionary<string, string> flags)
    {
        Console.Error.WriteLine();

        var size = GameDownloader.DirectorySize(staging);
        Console.Error.WriteLine($"Interrupted. Downloaded files in {staging}");
        if (size > 0) Console.Error.WriteLine($"  {CommandLine.Bytes(size)} downloaded so far.");

        if (flags.ContainsKey("clean")) { Discard(staging); return; }
        if (flags.ContainsKey("keep")) { Keep(); return; }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("No terminal to ask, so the files are kept.");
            Keep();
            return;
        }

        Console.Error.Write("Delete them? Keeping lets the next run resume. [y/N] ");
        var answer = Console.ReadLine()?.Trim() ?? "";

        // Anything but an explicit yes keeps the data: a stray keypress must not cost a download.
        if (answer.StartsWith('y') || answer.StartsWith('Y'))
            Discard(staging);
        else
            Keep();

        void Keep() =>
            Console.Error.WriteLine("Kept. Run the same command again to resume.");

        void Discard(string path)
        {
            StagingState.Discard(path);
            Console.Error.WriteLine("Deleted.");
        }
    }

}

/// <summary>Rewrites a single line instead of flooding the terminal.</summary>
internal sealed class ConsoleProgress : IProgress<DownloadProgress>
{
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private long _lastRender;

    public void Report(DownloadProgress p)
    {
        var now = Stopwatch.GetTimestamp();
        var since = (now - Interlocked.Read(ref _lastRender)) / (double)Stopwatch.Frequency;
        if (since < 0.2 && p.Bytes < p.TotalBytes) return;
        Interlocked.Exchange(ref _lastRender, now);

        var elapsed = (now - _startedAt) / (double)Stopwatch.Frequency;
        var speed = elapsed > 0 ? p.Bytes / elapsed / 1024 / 1024 : 0;

        // Zipping moves no bytes over the wire, so it can only report progress in files.
        var percent = p.TotalBytes > 0 ? 100.0 * p.Bytes / p.TotalBytes
            : p.TotalFiles > 0 ? 100.0 * p.Files / p.TotalFiles
            : 0;

        // Remaining time is only meaningful once the speed has settled a little.
        var eta = speed > 0.1 && p.Bytes > 0 && p.TotalBytes > p.Bytes
            ? TimeSpan.FromSeconds((p.TotalBytes - p.Bytes) / (p.Bytes / elapsed))
            : (TimeSpan?)null;

        // The manifest and zip phases have no byte counter; "-/-" and "0.0 MiB/s" are noise.
        var size = p.TotalBytes > 0 ? $"{CommandLine.Bytes(p.Bytes)}/{CommandLine.Bytes(p.TotalBytes)}" : "";
        var rate = p.Bytes > 0 ? $"{speed,6:F1} MiB/s" : "";
        var remaining = eta is { } left ? $"  ETA {left:hh\\:mm\\:ss}" : "";

        Console.Error.Write(
            $"\r  {p.Phase,-8} {percent,5:F1} %  {size,-20}  {p.Files}/{p.TotalFiles} files  " +
            $"{rate}{remaining}        ");
    }
}
