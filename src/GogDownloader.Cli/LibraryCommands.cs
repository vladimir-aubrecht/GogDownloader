using GogDownloader.Core;
using GogDownloader.Core.Library;

namespace GogDownloader.Cli;

public static class LibraryCommands
{
    public static async Task<int> SyncAsync(Dictionary<string, string> flags, CancellationToken ct)
    {
        var (api, content, _, options) = CommandLine.CreateContent(flags);

        if (options.MaxBytesPerSecond > 0)
            Console.Error.WriteLine("Note: --limit-rate has no effect here, it applies to game downloads.");

        Console.Error.WriteLine($"Fetching library ({options.MaxConnections} connections)...");

        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (p.Done % 10 == 0 || p.Done == p.Total)
                Console.Error.Write($"\r  {p.Done}/{p.Total} games");
        });

        var snapshot = await new LibrarySync(api, content, options.MaxConnections).RunAsync(progress, ct);
        Console.Error.WriteLine();

        var path = CommandLine.SnapshotPath(flags);
        await snapshot.SaveAsync(path, ct);

        Console.WriteLine($"Saved {snapshot.Games.Count} games to {path}");
        if (snapshot.Failures.Count > 0)
            Console.WriteLine($"Failed to load {snapshot.Failures.Count} games (see the snapshot).");

        return 0;
    }

    public static async Task<int> ListAsync(
        string[] args, Dictionary<string, string> flags, CancellationToken ct)
    {
        if (await CommandLine.LoadSnapshotAsync(flags, ct) is not { } snapshot) return 1;

        var search = GameFilter.SearchText(args, skip: 2);
        if (!GameFilter.TryPlatform(flags, out var platform)) return 1;

        var depotOnly = flags.ContainsKey("depot");
        var dlcOnly = flags.ContainsKey("dlc");

        // Without this a stale snapshot would quietly report no DLC and zero bytes.
        if (!snapshot.KnowsDlcs || !snapshot.KnowsDepotSizes)
            Console.Error.WriteLine(
                "Note: this snapshot predates DLC and size tracking, so both will be missing. " +
                "Refresh it with: gogdl library sync");

        var games = GameFilter.Apply(snapshot, search, platform, depotOnly, dlcOnly);

        if (games.Count == 0)
        {
            Console.Error.WriteLine("Nothing matches.");
            return 1;
        }

        // Bare slugs, one per line, for feeding into a loop. Everything else goes to stderr.
        if (flags.ContainsKey("slugs"))
        {
            foreach (var game in games) Console.WriteLine(game.Slug);
            return 0;
        }

        var slugWidth = Math.Min(games.Max(g => g.Slug.Length), 44);

        foreach (var game in games)
        {
            var parts = new List<string>();

            // gen1 still downloads, but its size is the unpacked one, not the transfer.
            if (GameFilter.Depots(game, platform).ToList() is { Count: > 0 } depots)
                parts.Add(string.Join(",", depots.Select(p =>
                    Short(p) + (game.Platforms[p].HasLegacyDepot ? ":gen1" : ""))));

            var installers = game.Platforms
                .Where(kv => (platform is null || kv.Key == platform)
                             && !kv.Value.HasDepot && kv.Value.HasInstaller && !kv.Value.CanBackUp)
                .Select(kv => Short(kv.Key))
                .ToList();

            if (installers.Count > 0) parts.Add($"({string.Join(",", installers)})");

            var availability = parts.Count > 0 ? string.Join(" ", parts) : "-";

            var dlc = game.Dlcs.Count > 0 ? $"+{game.Dlcs.Count} DLC" : "";

            Console.WriteLine(
                $"{game.Slug.PadRight(slugWidth)}  {availability,-16}  {CommandLine.Bytes(GameFilter.Size(game, platform)),9}  " +
                $"{dlc,-8}  {game.Title}");
        }

        // A single hit is usually someone inspecting one game before downloading it.
        if (games is [var only])
        {
            if (GameFilter.Depots(only, platform).Select(p => only.Platforms[p]).FirstOrDefault() is { } depot)
            {
                Console.WriteLine();
                Console.WriteLine($"  download {CommandLine.Bytes(depot.DownloadSize)}, unpacked {CommandLine.Bytes(depot.InstalledSize)}");
            }

            if (only.Dlcs.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Owned DLC, all of which go into the zip:");
                foreach (var dlc in only.Dlcs)
                    Console.WriteLine($"  {dlc.Name}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"{games.Count} {(games.Count == 1 ? "game" : "games")}, " +
            $"{CommandLine.Bytes(games.Sum(g => GameFilter.Size(g, platform)))} to download.");
        Console.WriteLine(
            "  win        Galaxy depot, downloadable now");
        Console.WriteLine(
            "  win:gen1   old depot format, downloadable, size shown unpacked");
        Console.WriteLine(
            "  (win)      installer only, no depot");
        Console.WriteLine("Pass the slug to: gogdl download <slug>");
        return 0;
    }

    public static async Task<int> StatsAsync(Dictionary<string, string> flags, CancellationToken ct)
    {
        if (await CommandLine.LoadSnapshotAsync(flags, ct) is not { } snapshot) return 1;

        Console.WriteLine($"Snapshot from {snapshot.SyncedAt.ToLocalTime():yyyy-MM-dd HH:mm}, {snapshot.Games.Count} games.");
        Console.WriteLine();

        foreach (var platform in Enum.GetValues<GamePlatform>())
        {
            var present = snapshot.Games
                .Select(g => g.Platforms.GetValueOrDefault(platform))
                .OfType<PlatformAvailability>()
                .ToList();

            var depot = present.Count(p => p.CanBackUp);
            var legacy = present.Count(p => p.HasLegacyDepot);
            var installerOnly = present.Count(p => !p.HasDepot && p.HasInstaller);
            var size = present.Sum(p => p.BestKnownSize);

            var noun = present.Count == 1 ? "title " : "titles";
            Console.WriteLine(
                $"{platform,-8} {present.Count,4} {noun}   depot: {depot,4}   gen1: {legacy,4}   " +
                $"installer only: {installerOnly,4}   {CommandLine.Bytes(size),9} to download");
        }

        Console.WriteLine();

        // This is the number the sync exists for: how many titles would need
        // innoextract/Wine because no Galaxy depot exists for them.
        var needsInstallerPath = snapshot.Games
            .Where(g => new[] { GamePlatform.Windows, GamePlatform.Mac }
                .Any(p => g.Platforms.GetValueOrDefault(p) is { HasDepot: false, HasInstaller: true }))
            .ToList();

        Console.WriteLine($"Windows/macOS titles without a depot: {needsInstallerPath.Count}");
        foreach (var game in needsInstallerPath.Take(30))
            Console.WriteLine($"  {game.Title}");
        if (needsInstallerPath.Count > 30)
            Console.WriteLine($"  ... and {needsInstallerPath.Count - 30} more");

        return 0;
    }

    private static string Short(GamePlatform platform) => platform switch
    {
        GamePlatform.Windows => "win",
        GamePlatform.Mac => "osx",
        _ => "lin",
    };
}
