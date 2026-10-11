using GogDownloader.Core.Api;
using GogDownloader.Core.Auth;
using GogDownloader.Core.Content;
using GogDownloader.Core.Http;
using GogDownloader.Core.Library;

namespace GogDownloader.Cli;

/// <summary>Argument parsing, dispatch and the bits of formatting every command shares.</summary>
public static class CommandLine
{
    public static string Version =>
        typeof(CommandLine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

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
            "auth login" => await AuthCommands.LoginAsync(flags, ct),
            "auth status" => await AuthCommands.StatusAsync(ct),
            "auth logout" => AuthCommands.Logout(),
            "library sync" => await LibraryCommands.SyncAsync(flags, ct),
            "library stats" => await LibraryCommands.StatsAsync(flags, ct),
            _ when command.StartsWith("library list", StringComparison.Ordinal)
                => await LibraryCommands.ListAsync(args, flags, ct),
            _ when command.StartsWith("download", StringComparison.Ordinal)
                => await DownloadCommands.RunAsync(args, flags, ct),
            _ when command.StartsWith("diff", StringComparison.Ordinal)
                => await DiffCommands.RunAsync(args, flags, ct),
            _ when command.StartsWith("verify", StringComparison.Ordinal)
                => await VerifyCommands.RunAsync(args, flags, ct),
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
                --depot                        only games that can be backed up now
                --dlc                          only games with owned DLC
                --slugs                        print bare slugs, one per line, for scripting
              library stats                    library breakdown by depot availability

              download <slug|id>               back up a game into a zip
              download --all [text]            back up every matching game
                --dlc                          of those, only games with owned DLC
                --dry-run                      list what would be downloaded, then stop
                --platform windows|mac|linux   default windows
                --language en                  default en
                --out DIR                      default ./gog-backup
                --connections N                concurrent connections, default 5
                --limit-rate 5M                speed cap (k/M/G)
                --staging DIR                  where to assemble and zip, default local app data
                --compression-threads N        deflate workers, default one per core
                --clean                        start over, and delete downloads on interrupt
                --keep                         on interrupt keep downloads without asking
                --no-verify                    skip reading the archive back to check it
                --no-reuse                     do not reuse unchanged files from the old backup
                --no-launcher                  do not add a start.bat to the archive root
                --force                        re-download even a completed build
                --verbose                      print why parts are retried (stalls, expiries)

              diff [text]                     compare a backup directory against the library
                --out DIR                      default ./gog-backup
                --platform windows|mac|linux   default windows
                --all                          also list what is up to date
                --slugs                        print slugs of missing and outdated games

              verify [text]                   check the archives in a backup directory
                --out DIR                      default ./gog-backup
                --deep                         inflate every entry and recompute its CRC
                --fix                          repair in place what does not need downloading
                --slugs                        print slugs of the archives to rewrite

            Any command: --snapshot FILE       use this library snapshot (or $GOGDL_SNAPSHOT)
            """);
        return 1;
    }

    /// <summary>
    /// Flags that take no value. Without this list `download --all witcher` swallows the search
    /// text as the value of `--all`, and `download --force witcher` loses the game name.
    /// </summary>
    private static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        "version", "all", "depot", "dlc", "slugs", "force", "clean", "keep", "no-verify", "no-reuse", "no-launcher", "dry-run", "verbose", "deep", "fix",
    };

    public static Dictionary<string, string> ParseFlags(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;

            var key = args[i][2..];
            var takesValue = !Switches.Contains(key)
                             && i + 1 < args.Length
                             && !args[i + 1].StartsWith("--", StringComparison.Ordinal);

            flags[key] = takesValue ? args[++i] : "true";
        }
        return flags;
    }

    /// <summary>
    /// The arguments that are neither a flag nor a flag's value: the command words, then the
    /// game or the search text. Anything else would mistake `windows` in `--platform windows`
    /// for a game name.
    /// </summary>
    public static List<string> Positionals(string[] args)
    {
        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(args[i]);
                continue;
            }

            if (!Switches.Contains(args[i][2..])
                && i + 1 < args.Length
                && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                i++;   // skip the value
        }
        return positionals;
    }

    /// <summary>
    /// Lets tests and debugging run against a fixture instead of the real library, which lives
    /// in the user's profile and must never be clobbered by a probe.
    /// </summary>
    public static string SnapshotPath(Dictionary<string, string> flags)
    {
        if (flags.TryGetValue("snapshot", out var explicitPath)) return Path.GetFullPath(explicitPath);

        var fromEnvironment = Environment.GetEnvironmentVariable("GOGDL_SNAPSHOT");
        return string.IsNullOrWhiteSpace(fromEnvironment)
            ? LibrarySnapshot.DefaultPath()
            : Path.GetFullPath(fromEnvironment);
    }

    public static async Task<LibrarySnapshot?> LoadSnapshotAsync(
        Dictionary<string, string> flags, CancellationToken ct)
    {
        var snapshot = await LibrarySnapshot.LoadAsync(SnapshotPath(flags), ct);
        if (snapshot is null)
            Console.Error.WriteLine("No snapshot. Run first: gogdl library sync");
        else if (!snapshot.KnowsPlatformDlcs)
            Console.Error.WriteLine(
                "Note: this snapshot has no per-platform DLC information. Run gogdl library sync " +
                "before repairing DLC backups; the old shared DLC list is used until then.");
        return snapshot;
    }

    public static GogHttpOptions OptionsFrom(Dictionary<string, string> flags) => new()
    {
        MaxConnections = flags.TryGetValue("connections", out var c) && int.TryParse(c, out var n) && n > 0 ? n : 5,
        MaxBytesPerSecond = flags.TryGetValue("limit-rate", out var r) ? ParseRate(r) : 0,
    };

    /// <summary>`5M` becomes 5 * 1024^2. Without a suffix, bytes per second.</summary>
    public static long ParseRate(string value)
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

    public static TokenProvider Tokens(GogHttpOptions options) =>
        new(new TokenStore(), new GogAuthClient(GogHttpFactory.Create(options)));

    public static (GogApiClient Api, ContentClient Content, HttpClient Cdn, GogHttpOptions Options) CreateContent(
        Dictionary<string, string> flags)
    {
        var options = OptionsFrom(flags);
        var authed = GogHttpFactory.Create(options, Tokens(options));

        // Chunks and installer ranges go through a signed URL — a Bearer header has no place there.
        var cdn = GogHttpFactory.Create(options);

        return (new GogApiClient(authed), new ContentClient(authed, cdn, new RateLimiter(options.MaxBytesPerSecond)), cdn, options);
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
