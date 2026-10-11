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

            // Three ways in, and they cost different things, so say which.
            if (GameFilter.Depots(game, platform).ToList() is { Count: > 0 } depots)
                parts.Add(string.Join(",", depots.Select(p => Short(p) + Suffix(game.Platforms[p]))));

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
            foreach (var target in only.Platforms.Keys.Where(p => platform is null || p == platform))
            {
                var parents = snapshot.DlcParents(only, target);
                if (parents.Count > 0)
                    Console.WriteLine(
                        $"  {target.ApiName()}: DLC backed up with {string.Join(", ", parents.Select(p => p.Slug))}.");
            }
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
        Console.WriteLine("  win:gen1   old depot format, size shown unpacked");
        Console.WriteLine("  win:inno   no depot, extracted from the installer (needs innoextract)");
        Console.WriteLine("  lin:installer  read out of the MojoSetup installer over HTTP Range");
        Console.WriteLine("  (osx)      installer only, and macOS installers cannot be extracted");
        Console.WriteLine("Pass the slug to: gogdl download <slug>");
        return 0;
    }

    public static async Task<int> StatsAsync(Dictionary<string, string> flags, CancellationToken ct)
    {
        if (await CommandLine.LoadSnapshotAsync(flags, ct) is not { } snapshot) return 1;

        Console.WriteLine($"Snapshot from {snapshot.SyncedAt.ToLocalTime():yyyy-MM-dd HH:mm}, {snapshot.Games.Count} games.");
        Console.WriteLine();

        // Packs of games the library has on their own are never downloaded, so counting them
        // here would promise bytes no backup will ever fetch.
        var duplicatePacks = snapshot.Games.Where(g => snapshot.PackDuplicates(g).Count > 0).ToList();
        var duplicated = duplicatePacks.Select(g => g.Id).ToHashSet();

        foreach (var platform in Enum.GetValues<GamePlatform>())
        {
            var present = GameFilter.ResolveDownloads(snapshot,
                    snapshot.Games.Where(g => !duplicated.Contains(g.Id)), platform)
                .Select(g => g.Platforms.GetValueOrDefault(platform))
                .OfType<PlatformAvailability>()
                .ToList();

            var depot = present.Count(p => p.Depot is { Generation: 2 });
            var legacy = present.Count(p => p.HasLegacyDepot);
            var installer = present.Count(p => !p.HasDepot && p.CanBackUp);
            var blocked = present.Count(p => !p.CanBackUp);
            var size = present.Sum(p => p.BestKnownSize);

            var noun = present.Count == 1 ? "title " : "titles";
            Console.WriteLine(
                $"{platform,-8} {present.Count,4} {noun}   depot: {depot,4}   gen1: {legacy,4}   " +
                $"installer: {installer,4}   cannot back up: {blocked,4}   " +
                $"{CommandLine.Bytes(size),9} to download");
        }

        Console.WriteLine();

        // Windows without a depot is extracted with innoextract, so say how many need it.
        var needsInnoExtract = snapshot.Games
            .Count(g => !duplicated.Contains(g.Id) && g.Platforms.Any(p =>
                p.Value.NeedsInnoExtract && snapshot.DlcParents(g, p.Key).Count == 0));

        Console.WriteLine($"Titles needing innoextract: {needsInnoExtract}");

        Console.WriteLine($"Packs skipped as duplicates (their games are owned separately): {duplicatePacks.Count}");
        foreach (var pack in duplicatePacks)
            Console.WriteLine($"  {pack.Title}");

        // What is left over: macOS installers, which are neither a depot nor Inno Setup.
        var unsupported = snapshot.Games
            .Where(g => !duplicated.Contains(g.Id)
                        && g.Platforms.Count > 0 && g.Platforms.Values.All(p => !p.CanBackUp))
            .ToList();

        Console.WriteLine($"Titles that cannot be backed up at all: {unsupported.Count}");
        foreach (var game in unsupported.Take(30))
            Console.WriteLine($"  {game.Title}");
        if (unsupported.Count > 30)
            Console.WriteLine($"  ... and {unsupported.Count - 30} more");

        return 0;
    }

    private static string Suffix(PlatformAvailability availability) => availability switch
    {
        { HasLegacyDepot: true } => ":gen1",
        { NeedsInnoExtract: true } => ":inno",
        { IsLinux: true, HasDepot: false } => ":installer",
        _ => "",
    };

    private static string Short(GamePlatform platform) => platform switch
    {
        GamePlatform.Windows => "win",
        GamePlatform.Mac => "osx",
        _ => "lin",
    };
}
