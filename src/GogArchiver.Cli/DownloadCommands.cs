using System.Diagnostics;
using GogArchiver.Core;
using GogArchiver.Core.Download;
using GogArchiver.Core.Library;

namespace GogArchiver.Cli;

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
                Console.Error.WriteLine("Missing game. Usage: gogarchiver download <slug|id>, or download --all");
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
                "named without it. Refresh it with: gogarchiver library sync");

        if (!snapshot.KnowsPacks)
            Console.Error.WriteLine(
                "Note: this snapshot predates pack detection, so a compilation of games the " +
                "library already has will be downloaded twice. Refresh it with: gogarchiver library sync");

        // A pack is a container GOG never installs: it grants the games it contains as products
        // of their own, and those are what a backup holds. Downloading it as well would archive
        // the same games twice, and some packs refuse their own installer outright (403/404).
        var packs = games
            .Select(game => (Pack: game, Contents: snapshot.PackDuplicates(game)))
            .Where(p => p.Contents.Count > 0)
            .ToList();

        if (packs.Count > 0)
        {
            var duplicated = packs.Select(p => p.Pack.Id).ToHashSet();
            games = games.Where(g => !duplicated.Contains(g.Id)).ToList();

            foreach (var (pack, contents) in packs)
                Console.Error.WriteLine(
                    $"Skipping {pack.Title}: a pack of games the library has on its own " +
                    $"({string.Join(", ", contents.Select(c => c.Title))}).");

            if (games.Count == 0)
            {
                Console.Error.WriteLine("Nothing left to download.");
                return 0;
            }
        }

        games = GameFilter.ResolveDownloads(snapshot, games, platform, (dlc, parents) =>
            Console.Error.WriteLine(
                $"{dlc.Title} is DLC; backing it up with {string.Join(", ", parents.Select(p => p.Title))}."));

        try { BackupDirectory.ValidateSelection(snapshot.Games, games); }
        catch (BackupDirectoryConflictException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

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
            WriteLauncher = !flags.ContainsKey("no-launcher"),
            MaxCompressionThreads = flags.TryGetValue("compression-threads", out var t) && int.TryParse(t, out var n) && n > 0
                ? n
                : Environment.ProcessorCount,
        };

        var total = games.Sum(g => GameFilter.Size(g, platform));
        // The version leads the header: three bug hunts in a row turned out to be a stale build
        // on the user's machine, which one glance here would have settled.
        Console.Error.WriteLine(
            $"gogarchiver {CommandLine.Version} · " +
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
        if (flags.ContainsKey("verbose"))
            GogArchiver.Core.Download.ContentSource.RetryObserver =
                reason => Console.Error.WriteLine($"\n  retry {reason}");

        var (api, content, cdn, _) = CommandLine.CreateContent(flags);
        var downloader = new GameDownloader(api, content, cdn);

        var completed = new List<string>();
        var alreadyDone = new List<string>();
        var skipped = new List<(string Title, string Reason)>();
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
                Summarise(completed, alreadyDone, skipped, failed, games.Count);
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
                    alreadyDone.Add(game.Title);
                    break;
                case DownloadOutcome.Incomplete:
                    Console.Error.WriteLine($"Incomplete: {result.Detail}");
                    failed.Add(game.Title);
                    break;
                default:
                    Console.Error.WriteLine($"Skipped: {result.Detail}");
                    skipped.Add((game.Title, result.Detail ?? result.Outcome.ToString()));
                    break;
            }
        }

        if (games.Count > 1) Summarise(completed, alreadyDone, skipped, failed, games.Count);

        // A single game keeps its old exit codes; a batch fails only if something failed.
        if (games.Count == 1)
            return failed.Count > 0 ? 1
                : (skipped.Count > 0 || alreadyDone.Count > 0) && completed.Count == 0 ? 3
                : 0;

        return failed.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// Already-done games are only counted — on a resumed batch there are hundreds of them and
    /// each already printed its own line. What was skipped for a reason, or failed, is worth
    /// repeating at the end: after a long run those lines have scrolled far out of sight.
    /// </summary>
    private static void Summarise(
        List<string> completed, List<string> alreadyDone, List<(string Title, string Reason)> skipped,
        List<string> failed, int total)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            $"{completed.Count} downloaded, {alreadyDone.Count} already done, " +
            $"{skipped.Count} skipped, {failed.Count} failed, of {total}.");

        foreach (var (title, reason) in skipped)
            Console.Error.WriteLine($"  skipped: {title} — {FirstLine(reason)}");
        foreach (var title in failed)
            Console.Error.WriteLine($"  failed: {title}");
    }

    /// <summary>A skip reason can be a multi-line explanation; the summary wants its gist.</summary>
    private static string FirstLine(string text)
    {
        var newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline];
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

/// <summary>
/// A download runs through several phases — download, zip, verify — each its own progress bar
/// from 0 to 100. Each gets its own line so a finished phase stays on screen; without that, a
/// completed download vanishing as zipping starts at 0% reads as if it restarted.
/// </summary>
internal sealed class ConsoleProgress : IProgress<DownloadProgress>
{
    private readonly Lock _gate = new();
    private string _phase = "";
    private long _phaseStarted;
    private long _lastRender;
    private bool _phaseMeasurable;

    // A sliding window so the rate reflects the last second, not the average since the phase
    // began. The average stays high through a stall and reads as if bytes are still flowing.
    private long _windowStart;
    private long _windowBytes;
    private double _bytesPerSecond;

    public void Report(DownloadProgress p)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var measurable = p.TotalBytes > 0 || p.TotalFiles > 0;

            if (p.Phase != _phase)
            {
                // Keep a phase that showed real progress on its own line; a phase with nothing to
                // measure (fetching the manifest) is a status blip that the next one overwrites,
                // so it never lingers as a misleading "0.0 %".
                if (_phase.Length > 0 && _phaseMeasurable) Console.Error.WriteLine();
                _phase = p.Phase;
                _phaseStarted = now;
                _lastRender = 0;
                _phaseMeasurable = false;
                _windowStart = now;
                _windowBytes = p.Bytes;
                _bytesPerSecond = 0;
            }

            _phaseMeasurable |= measurable;

            if (!measurable)
            {
                Console.Error.Write($"\r  {Label(p.Phase),-9} reading…        ");
                return;
            }

            // Only these move bytes over the wire; everything else is local work, and showing a
            // MiB/s for zipping reads as a download and made a user think the download restarted.
            var overNetwork = p.Phase is "download" or "copy";

            // Zip and verify count in files; download and copy count in bytes.
            var percent = overNetwork && p.TotalBytes > 0 ? 100.0 * p.Bytes / p.TotalBytes
                : p.TotalFiles > 0 ? 100.0 * p.Files / p.TotalFiles
                : p.TotalBytes > 0 ? 100.0 * p.Bytes / p.TotalBytes
                : 0;

            var atEnd = overNetwork && p.TotalBytes > 0 ? p.Bytes >= p.TotalBytes
                : p.TotalFiles > 0 ? p.Files >= p.TotalFiles
                : true;

            var since = (now - _lastRender) / (double)Stopwatch.Frequency;
            if (since < 0.2 && !atEnd) return;
            _lastRender = now;

            var files = p.TotalFiles > 0 ? $"{p.Files}/{p.TotalFiles} files" : "";

            var transfer = "";
            if (overNetwork)
            {
                // Recompute the rate roughly once a second from the bytes moved in that window.
                var windowSeconds = (now - _windowStart) / (double)Stopwatch.Frequency;
                if (windowSeconds >= 1)
                {
                    _bytesPerSecond = (p.Bytes - _windowBytes) / windowSeconds;
                    _windowStart = now;
                    _windowBytes = p.Bytes;
                }

                // A rate near zero means a stalled connection, so an ETA from it would be noise.
                var eta = _bytesPerSecond > 64 * 1024 && p.TotalBytes > p.Bytes
                    ? TimeSpan.FromSeconds((p.TotalBytes - p.Bytes) / _bytesPerSecond)
                    : (TimeSpan?)null;

                transfer = $"{CommandLine.Bytes(p.Bytes)}/{CommandLine.Bytes(p.TotalBytes)}"
                           + $"  {_bytesPerSecond / 1024 / 1024,6:F1} MiB/s"
                           + (eta is { } left ? $"  ETA {left:hh\\:mm\\:ss}" : "  ETA --");
            }

            Console.Error.Write(
                $"\r  {Label(p.Phase),-9} {percent,5:F1} %  {files,-16}  {transfer}        ");
        }
    }

    /// <summary>Phase names are internal shorthand; give the user something plainer.</summary>
    private static string Label(string phase) => phase switch
    {
        "reuse" => "reusing",
        "verify" => "checking",   // verifying files already on disk before resuming
        "check" => "verifying",   // reading the finished archive back
        "copy" => "copying",
        _ => phase,               // manifest, download, zip
    };
}
