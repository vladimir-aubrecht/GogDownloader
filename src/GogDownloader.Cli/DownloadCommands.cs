using System.Diagnostics;
using GogDownloader.Core;
using GogDownloader.Core.Download;

namespace GogDownloader.Cli;

public static class DownloadCommands
{
    public static async Task<int> RunAsync(
        string[] args, Dictionary<string, string> flags, CancellationToken ct)
    {
        // Only positional arguments count. Scanning every argument would pick up a flag's
        // value, so `download --platform windows` would try to download a game called
        // "windows".
        var needle = args.TakeWhile(a => !a.StartsWith('-')).Skip(1).FirstOrDefault();
        if (needle is null)
        {
            Console.Error.WriteLine("Missing game. Usage: gogdl download <slug|id>");
            return 1;
        }

        if (await CommandLine.LoadSnapshotAsync(flags, ct) is not { } snapshot) return 1;

        var game = snapshot.Games.FirstOrDefault(g =>
            string.Equals(g.Slug, needle, StringComparison.OrdinalIgnoreCase)
            || g.Id.ToString() == needle);

        if (game is null)
        {
            Console.Error.WriteLine($"Game '{needle}' is not in the library snapshot.");
            return 1;
        }

        if (!LibraryCommands.TryPlatform(flags, out var chosen)) return 1;
        var platform = chosen ?? GamePlatform.Windows;

        if (!snapshot.KnowsEarlyAccess)
            Console.Error.WriteLine(
                "Note: this snapshot predates the early-access flag, so an (EA) game will be " +
                "named without it. Refresh it with: gogdl library sync");

        var (api, content, options) = CommandLine.CreateContent(flags);

        var downloadOptions = new DownloadOptions
        {
            OutputRoot = Path.GetFullPath(flags.GetValueOrDefault("out", "gog-backup")),
            Platform = platform,
            Language = flags.GetValueOrDefault("language", "en"),
            MaxConnections = options.MaxConnections,
            StagingRoot = flags.TryGetValue("staging", out var staging) ? Path.GetFullPath(staging) : null,
            Force = flags.ContainsKey("force"),
            Clean = flags.ContainsKey("clean"),
            VerifyArchive = !flags.ContainsKey("no-verify"),
            MaxCompressionThreads = flags.TryGetValue("compression-threads", out var t) && int.TryParse(t, out var n) && n > 0
                ? n
                : Environment.ProcessorCount,
        };

        Console.Error.WriteLine($"{game.Title} → {Path.Combine(downloadOptions.OutputRoot, game.DirectoryName)}");
        Console.Error.WriteLine(
            $"staging in {downloadOptions.StagingDirectory}, " +
            $"zipping on {downloadOptions.MaxCompressionThreads} threads");

        DownloadResult result;
        try
        {
            result = await new GameDownloader(api, content)
                .RunAsync(game, downloadOptions, new ConsoleProgress(), ct);
        }
        catch (DownloadInterruptedException interrupted)
        {
            // Deleting tens of gigabytes on the user's behalf is not ours to decide.
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Interrupted. Downloaded files kept in:");
            Console.Error.WriteLine($"  {interrupted.StagingDirectory}");
            Console.Error.WriteLine("Run the same command again to resume, or add --clean to start over.");
            return 130;
        }

        Console.Error.WriteLine();

        switch (result.Outcome)
        {
            case DownloadOutcome.Completed:
                Console.WriteLine($"Done: {result.ArchivePath}");
                return 0;
            case DownloadOutcome.AlreadyDone:
                Console.WriteLine(result.Detail);
                return 0;
            default:
                Console.Error.WriteLine($"Skipped: {result.Detail}");
                return 3;
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
