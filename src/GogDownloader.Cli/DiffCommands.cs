using GogDownloader.Core;
using GogDownloader.Core.Download;
using GogDownloader.Core.Library;

namespace GogDownloader.Cli;

/// <summary>
/// Compares a backup directory against the library snapshot: what is missing, what a new build
/// has made stale, and what sits in the directory without belonging to the library any more.
/// </summary>
public static class DiffCommands
{
    private enum State { Missing, Outdated, Current, Unsupported, Pack }

    private sealed record Row(State State, LibraryEntry Game, string Detail, long Size);

    public static async Task<int> RunAsync(
        string[] args, Dictionary<string, string> flags, CancellationToken ct)
    {
        if (await CommandLine.LoadSnapshotAsync(flags, ct) is not { } snapshot) return 1;
        if (!GameFilter.TryPlatform(flags, out var chosen)) return 1;
        var platform = chosen ?? GamePlatform.Windows;

        var outputRoot = Path.GetFullPath(flags.GetValueOrDefault("out", "gog-backup"));
        var search = GameFilter.SearchText(args, skip: 1);

        var games = GameFilter.Apply(snapshot, search, platform, depotOnly: false, dlcOnly: false);
        games = GameFilter.ResolveDownloads(snapshot, games, platform, (dlc, parents) =>
            Console.Error.WriteLine(
                $"{dlc.Title} is DLC; checking {string.Join(", ", parents.Select(p => p.Title))}."));
        var rows = new List<Row>();

        foreach (var game in games)
        {
            if (game.Platforms.GetValueOrDefault(platform) is not { } availability) continue;

            // A pack of games the library has on its own is not missing, it is duplicated:
            // `download` skips it, so reporting it as missing would never stop.
            if (snapshot.PackDuplicates(game) is { Count: > 0 } contents)
            {
                rows.Add(new Row(State.Pack, game,
                    $"pack of {string.Join(", ", contents.Select(c => c.Title))}", 0));
                continue;
            }

            var directory = Path.Combine(outputRoot, game.DirectoryName);
            var backup = (await BackupManifest.LoadAsync(directory, ct))?.Platforms.GetValueOrDefault(platform);
            var size = GameFilter.Size(game, platform);

            if (!availability.CanBackUp)
                rows.Add(new Row(State.Unsupported, game, availability.HasInstaller ? "macOS installer, not extractable" : "no depot, no installer", size));
            else if (backup is null)
                rows.Add(new Row(State.Missing, game, "not backed up", size));
            else if (!backup.HasArchive(directory))
                rows.Add(new Row(State.Missing, game, "archive missing or its size changed", size));
            else if (availability.Depot is not null && backup.BuildId != availability.Depot.BuildId)
                rows.Add(new Row(State.Outdated, game,
                    $"have {Describe(backup.VersionName, backup.BuildId)}, " +
                    $"GOG has {Describe(availability.Depot.VersionName, availability.Depot.BuildId)}", size));
            else if (!backup.ContainsDlcs(game.DlcsFor(platform).Select(d => d.ProductId)))
                rows.Add(new Row(State.Outdated, game, "owned DLC missing from the backup", size));
            else
                rows.Add(new Row(State.Current, game, backup.ArchiveName, size));
        }

        var orphans = Orphans(outputRoot, snapshot);

        // Bare slugs of everything a download would fix, so the output can drive one.
        if (flags.ContainsKey("slugs"))
        {
            foreach (var row in rows.Where(r => r.State is State.Missing or State.Outdated))
                Console.WriteLine(row.Game.Slug);
            return 0;
        }

        Console.WriteLine($"{outputRoot}  vs  library snapshot ({platform.ApiName()})");
        Console.WriteLine();

        Section("Missing", rows, State.Missing, flags);
        Section("Outdated", rows, State.Outdated, flags);
        Section("Cannot back up yet", rows, State.Unsupported, flags);
        Section("Packs, skipped as duplicates", rows, State.Pack, flags);
        if (flags.ContainsKey("all")) Section("Up to date", rows, State.Current, flags);

        if (orphans.Count > 0)
        {
            Console.WriteLine("Not in the library (left over, or owned by someone else):");
            foreach (var orphan in orphans) Console.WriteLine($"  {orphan}");
            Console.WriteLine();
        }

        var missing = rows.Where(r => r.State is State.Missing or State.Outdated).ToList();
        Console.WriteLine(
            $"{Count(rows, State.Current)} up to date, {Count(rows, State.Missing)} missing, " +
            $"{Count(rows, State.Outdated)} outdated, {Count(rows, State.Unsupported)} unsupported, " +
            $"{Count(rows, State.Pack)} duplicate pack{(Count(rows, State.Pack) == 1 ? "" : "s")}.");

        if (missing.Count > 0)
        {
            Console.WriteLine($"{CommandLine.Bytes(missing.Sum(r => r.Size))} to download.");
            Console.WriteLine($"Fetch them with: gogdl download --all --platform {platform.ApiName()}");
        }

        return 0;
    }

    private static string Describe(string? version, string buildId) =>
        version is { Length: > 0 } ? $"{version} ({buildId})" : buildId;

    private static int Count(List<Row> rows, State state) => rows.Count(r => r.State == state);

    private static void Section(string title, List<Row> rows, State state, Dictionary<string, string> flags)
    {
        var selected = rows.Where(r => r.State == state).ToList();
        if (selected.Count == 0) return;

        Console.WriteLine($"{title} ({selected.Count}):");

        var width = Math.Min(selected.Max(r => r.Game.Slug.Length), 44);
        foreach (var row in selected)
            Console.WriteLine($"  {row.Game.Slug.PadRight(width)}  {CommandLine.Bytes(row.Size),9}  {row.Detail}");

        Console.WriteLine();
    }

    /// <summary>
    /// Directories that look like ours but name a game the snapshot does not have. Reported, never
    /// deleted: the library may simply be stale, and these are gigabytes.
    /// </summary>
    private static List<string> Orphans(string outputRoot, LibrarySnapshot snapshot)
    {
        if (!Directory.Exists(outputRoot)) return [];

        var known = snapshot.Games.Select(g => g.DirectoryName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateDirectories(outputRoot)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !name.StartsWith('.') && !known.Contains(name))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
