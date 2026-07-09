using System.Diagnostics;
using GogDownloader.Core;
using GogDownloader.Core.Api;
using GogDownloader.Core.Auth;
using GogDownloader.Core.Content;
using GogDownloader.Core.Download;
using GogDownloader.Core.Http;
using GogDownloader.Core.Library;

var cts = new CancellationTokenSource();
var interrupts = 0;

Console.CancelKeyPress += (_, e) =>
{
    // Cleaning up a staging directory of tens of gigabytes takes a while. A second Ctrl+C
    // means the user wants out now, so let it kill the process.
    if (Interlocked.Increment(ref interrupts) > 1) return;

    e.Cancel = true;
    cts.Cancel();
    Console.Error.WriteLine("\nInterrupting, cleaning up... press Ctrl+C again to force quit.");
};

try
{
    return await Cli.RunAsync(args, cts.Token);
}
catch (GogAuthException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Interrupted.");
    return 130;
}

public static class Cli
{
    public static string Version =>
        typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var command = string.Join(' ', args.TakeWhile(a => !a.StartsWith('-')));
        var flags = ParseFlags(args);

        if (flags.ContainsKey("version"))
        {
            Console.WriteLine(Version);
            return 0;
        }

        return command switch
        {
            "auth login" => await AuthLoginAsync(flags, ct),
            "auth status" => await AuthStatusAsync(ct),
            "auth logout" => AuthLogout(),
            "library sync" => await LibrarySyncAsync(flags, ct),
            "library stats" => await LibraryStatsAsync(ct),
            _ when command.StartsWith("library list", StringComparison.Ordinal)
                => await LibraryListAsync(args, flags, ct),
            _ when command.StartsWith("download", StringComparison.Ordinal)
                => await DownloadAsync(args, flags, ct),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine($"""
            gogdl {Version} — back up games you own on GOG

              auth login [--code <code|url>]   log in via browser
              auth status                      token status
              auth logout                      forget the token

              library sync [--connections N]   fetch the library from GOG and save a snapshot
              library list [text]              list games and their slugs, filtered by text
                --platform windows|mac|linux   only games available for this platform
                --depot                        only games with a Galaxy depot (downloadable now)
                --dlc                          only games with owned DLC
              library stats                    library breakdown by depot availability

              download <slug|id>               download a game from Galaxy depots into a zip
                --platform windows|mac|linux   default windows
                --language en                  default en
                --out DIR                      default ./gog-backup
                --connections N                concurrent connections, default 8
                --limit-rate 5M                speed cap (k/M/G)
                --staging DIR                  where to assemble and zip, default local temp
                --force                        re-download even a completed build
            """);
        return 1;
    }

    private static Dictionary<string, string> ParseFlags(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";
            flags[key] = value;
        }
        return flags;
    }

    private static GogHttpOptions OptionsFrom(Dictionary<string, string> flags) => new()
    {
        MaxConnections = flags.TryGetValue("connections", out var c) && int.TryParse(c, out var n) ? n : 8,
        MaxBytesPerSecond = flags.TryGetValue("limit-rate", out var r) ? ParseRate(r) : 0,
    };

    /// <summary>`5M` becomes 5 * 1024^2. Without a suffix, bytes per second.</summary>
    private static long ParseRate(string value)
    {
        if (value.Length == 0) return 0;
        var multiplier = char.ToLowerInvariant(value[^1]) switch
        {
            'k' => 1024L,
            'm' => 1024L * 1024,
            'g' => 1024L * 1024 * 1024,
            _ => 1L,
        };
        var digits = multiplier == 1 ? value : value[..^1];
        return long.TryParse(digits, out var n) && n > 0 ? n * multiplier : 0;
    }

    private static TokenProvider Tokens(GogHttpOptions options) =>
        new(new TokenStore(), new GogAuthClient(GogHttpFactory.Create(options)));

    private static (GogApiClient Api, ContentClient Content, GogHttpOptions Options) CreateContent(
        Dictionary<string, string> flags)
    {
        var options = OptionsFrom(flags);
        var tokens = Tokens(options);
        var authed = GogHttpFactory.Create(options, tokens);

        // Chunks go through a signed URL — a Bearer header has no place on the CDN.
        var cdn = GogHttpFactory.Create(options);

        var limiter = new RateLimiter(options.MaxBytesPerSecond);
        return (new GogApiClient(authed), new ContentClient(authed, cdn, limiter), options);
    }

    private static async Task<int> AuthLoginAsync(Dictionary<string, string> flags, CancellationToken ct)
    {
        var store = new TokenStore();
        var auth = new GogAuthClient(GogHttpFactory.Create(new GogHttpOptions()));

        if (!flags.TryGetValue("code", out var raw))
        {
            Console.WriteLine("Open this in a browser and log in:");
            Console.WriteLine();
            Console.WriteLine("  " + GogConstants.LoginUrl);
            Console.WriteLine();
            Console.WriteLine("After logging in you will land on a blank page. Copy the whole URL");
            Console.WriteLine("from the address bar (it contains ?code=...) and paste it here:");
            Console.Write("> ");
            raw = Console.ReadLine() ?? "";
        }

        if (GogAuthClient.ExtractCode(raw) is not { Length: > 0 } code)
        {
            Console.Error.WriteLine("No authorization code in the given input.");
            return 1;
        }

        var tokens = await auth.ExchangeCodeAsync(code, ct);
        await store.SaveAsync(tokens, ct);

        Console.WriteLine($"Logged in. Token saved to {store.Path}");
        return 0;
    }

    private static async Task<int> AuthStatusAsync(CancellationToken ct)
    {
        var tokens = await new TokenStore().LoadAsync(ct);
        if (tokens is null)
        {
            Console.WriteLine("Not logged in. Run: gogdl auth login");
            return 1;
        }

        var state = tokens.IsExpired
            ? "expired (will refresh itself)"
            : $"valid until {tokens.ExpiresAt.ToLocalTime():HH:mm:ss}";
        Console.WriteLine($"Logged in as user_id={tokens.UserId}, access token {state}.");
        return 0;
    }

    private static int AuthLogout()
    {
        new TokenStore().Delete();
        Console.WriteLine("Token deleted.");
        return 0;
    }

    private static async Task<int> LibrarySyncAsync(Dictionary<string, string> flags, CancellationToken ct)
    {
        var (api, content, options) = CreateContent(flags);

        if (options.MaxBytesPerSecond > 0)
            Console.Error.WriteLine("Note: --limit-rate has no effect here, it applies to game downloads.");

        Console.Error.WriteLine($"Fetching library ({options.MaxConnections} connections)...");

        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (p.Done % 10 == 0 || p.Done == p.Total)
                Console.Error.Write($"\r  {p.Done}/{p.Total} games");
        });

        var sync = new LibrarySync(api, content, options.MaxConnections);
        var snapshot = await sync.RunAsync(progress, ct);
        Console.Error.WriteLine();

        var path = LibrarySnapshot.DefaultPath();
        await snapshot.SaveAsync(path, ct);

        Console.WriteLine($"Saved {snapshot.Games.Count} games to {path}");
        if (snapshot.Failures.Count > 0)
            Console.WriteLine($"Failed to load {snapshot.Failures.Count} games (see the snapshot).");

        return 0;
    }

    private static async Task<int> LibraryListAsync(
        string[] args, Dictionary<string, string> flags, CancellationToken ct)
    {
        var snapshot = await LibrarySnapshot.LoadAsync(LibrarySnapshot.DefaultPath(), ct);
        if (snapshot is null)
        {
            Console.Error.WriteLine("No snapshot. Run first: gogdl library sync");
            return 1;
        }

        // Everything after "library list" that is not a flag is the search text.
        var search = string.Join(' ', args.Skip(2).TakeWhile(a => !a.StartsWith('-')));

        GamePlatform? platform = null;
        if (flags.TryGetValue("platform", out var platformName))
        {
            platform = GamePlatformExtensions.FromApiName(platformName);
            if (platform is null)
            {
                Console.Error.WriteLine($"Unknown platform '{platformName}'. Use windows, mac or linux.");
                return 1;
            }
        }

        var depotOnly = flags.ContainsKey("depot");
        var dlcOnly = flags.ContainsKey("dlc");

        // Without this a stale snapshot would quietly report no DLC and zero bytes.
        if (!snapshot.KnowsDlcs || !snapshot.KnowsDepotSizes)
            Console.Error.WriteLine(
                "Note: this snapshot predates DLC and size tracking, so both will be missing. " +
                "Refresh it with: gogdl library sync");

        var games = snapshot.Games
            .Where(g => search.Length == 0
                        || g.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || g.Slug.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(g => platform is null || g.Platforms.ContainsKey(platform.Value))
            .Where(g => !depotOnly || Depots(g, platform).Any())
            .Where(g => !dlcOnly || g.Dlcs.Count > 0)
            .ToList();

        if (games.Count == 0)
        {
            Console.Error.WriteLine("Nothing matches.");
            return 1;
        }

        var slugWidth = Math.Min(games.Max(g => g.Slug.Length), 44);

        foreach (var game in games)
        {
            var depots = string.Join(",", Depots(game, platform).Select(p => Short(p)));
            var installers = string.Join(",", game.Platforms
                .Where(kv => (platform is null || kv.Key == platform) && !kv.Value.HasDepot && kv.Value.HasInstaller)
                .Select(kv => Short(kv.Key)));

            var availability = (depots.Length, installers.Length) switch
            {
                (0, 0) => "-",
                (_, 0) => depots,
                (0, _) => $"({installers})",
                _ => $"{depots} ({installers})",
            };

            var dlc = game.Dlcs.Count > 0 ? $"+{game.Dlcs.Count} DLC" : "";

            Console.WriteLine(
                $"{game.Slug.PadRight(slugWidth)}  {availability,-16}  {Bytes(Size(game, platform)),9}  " +
                $"{dlc,-8}  {game.Title}");
        }

        var total = games.Sum(g => Size(g, platform));

        // A single hit is usually someone inspecting one game before downloading it.
        if (games is [var only])
        {
            var depot = Depots(only, platform)
                .Select(p => only.Platforms[p])
                .FirstOrDefault();

            if (depot is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"  download {Bytes(depot.DownloadSize)}, " +
                                  $"unpacked {Bytes(depot.InstalledSize)}");
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
        Console.WriteLine($"{games.Count} {(games.Count == 1 ? "game" : "games")}, {Bytes(total)} to download. " +
                          "Plain = Galaxy depot, (parentheses) = installer only, not downloadable yet.");
        Console.WriteLine("Pass the slug to: gogdl download <slug>");
        return 0;

        static long Size(LibraryEntry game, GamePlatform? only) =>
            only is not null
                ? game.Platforms.GetValueOrDefault(only.Value)?.BestKnownSize ?? 0
                // No platform picked: one download per game, so the largest is the honest guess.
                : game.Platforms.Values.Select(p => p.BestKnownSize).DefaultIfEmpty(0).Max();

        static IEnumerable<GamePlatform> Depots(LibraryEntry game, GamePlatform? only) =>
            game.Platforms
                .Where(kv => (only is null || kv.Key == only) && kv.Value.HasDepot)
                .Select(kv => kv.Key);

        static string Short(GamePlatform p) => p switch
        {
            GamePlatform.Windows => "win",
            GamePlatform.Mac => "osx",
            _ => "lin",
        };
    }

    private static async Task<int> LibraryStatsAsync(CancellationToken ct)
    {
        var snapshot = await LibrarySnapshot.LoadAsync(LibrarySnapshot.DefaultPath(), ct);
        if (snapshot is null)
        {
            Console.Error.WriteLine("No snapshot. Run first: gogdl library sync");
            return 1;
        }

        Console.WriteLine($"Snapshot from {snapshot.SyncedAt.ToLocalTime():yyyy-MM-dd HH:mm}, {snapshot.Games.Count} games.");
        Console.WriteLine();

        foreach (var platform in Enum.GetValues<GamePlatform>())
        {
            var present = snapshot.Games
                .Select(g => g.Platforms.GetValueOrDefault(platform))
                .OfType<PlatformAvailability>()
                .ToList();

            var depot = present.Count(p => p.HasDepot);
            var installerOnly = present.Count(p => !p.HasDepot && p.HasInstaller);
            var size = present.Sum(p => p.BestKnownSize);

            Console.WriteLine(
                $"{platform,-8} {present.Count,4} titles   depot: {depot,4}   " +
                $"installer only: {installerOnly,4}   {Bytes(size),9} to download");
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

    private static async Task<int> DownloadAsync(
        string[] args, Dictionary<string, string> flags, CancellationToken ct)
    {
        var needle = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (needle is null)
        {
            Console.Error.WriteLine("Missing game. Usage: gogdl download <slug|id>");
            return 1;
        }

        var snapshot = await LibrarySnapshot.LoadAsync(LibrarySnapshot.DefaultPath(), ct);
        if (snapshot is null)
        {
            Console.Error.WriteLine("No snapshot. Run first: gogdl library sync");
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

        var platformName = flags.GetValueOrDefault("platform", "windows");
        if (GamePlatformExtensions.FromApiName(platformName) is not { } platform)
        {
            Console.Error.WriteLine($"Unknown platform '{platformName}'. Use windows, mac or linux.");
            return 1;
        }

        if (!snapshot.KnowsEarlyAccess)
            Console.Error.WriteLine(
                "Note: this snapshot predates the early-access flag, so an (EA) game will be " +
                "named without it. Refresh it with: gogdl library sync");

        var (api, content, options) = CreateContent(flags);

        var downloadOptions = new DownloadOptions
        {
            OutputRoot = Path.GetFullPath(flags.GetValueOrDefault("out", "gog-backup")),
            Platform = platform,
            Language = flags.GetValueOrDefault("language", "en"),
            MaxConnections = options.MaxConnections,
            StagingRoot = flags.TryGetValue("staging", out var staging) ? Path.GetFullPath(staging) : null,
            Force = flags.ContainsKey("force"),
        };

        Console.Error.WriteLine($"{game.Title} → {Path.Combine(downloadOptions.OutputRoot, game.DirectoryName)}");
        Console.Error.WriteLine($"staging in {downloadOptions.StagingDirectory}");

        var result = await new GameDownloader(api, content)
            .RunAsync(game, downloadOptions, new ConsoleProgress(), ct);

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

    /// <summary>Human-readable size. Unknown sizes print as a dash, not as "0 B".</summary>
    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "-";

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value < 10 && unit > 0
            ? $"{value:F1} {units[unit]}"
            : $"{value:F0} {units[unit]}";
    }
}

/// <summary>Rewrites a single line instead of flooding the terminal.</summary>
public sealed class ConsoleProgress : IProgress<DownloadProgress>
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
        var size = p.TotalBytes > 0 ? $"{Cli.Bytes(p.Bytes)}/{Cli.Bytes(p.TotalBytes)}" : "";
        var rate = p.Bytes > 0 ? $"{speed,6:F1} MiB/s" : "";
        var remaining = eta is { } left ? $"  ETA {left:hh\\:mm\\:ss}" : "";

        Console.Error.Write(
            $"\r  {p.Phase,-8} {percent,5:F1} %  {size,-20}  {p.Files}/{p.TotalFiles} files  " +
            $"{rate}{remaining}        ");
    }
}
