using System.Diagnostics;
using GogDownloader.Core;
using GogDownloader.Core.Api;
using GogDownloader.Core.Auth;
using GogDownloader.Core.Content;
using GogDownloader.Core.Download;
using GogDownloader.Core.Http;
using GogDownloader.Core.Library;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

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
    Console.Error.WriteLine("Přerušeno.");
    return 130;
}

internal static class Cli
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
            _ when command.StartsWith("download", StringComparison.Ordinal)
                => await DownloadAsync(args, flags, ct),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine($"""
            gogdl {Version} — záloha vlastních her z GOG

              auth login [--code <code|url>]   přihlášení přes prohlížeč
              auth status                      stav tokenu
              auth logout                      zapomenout token

              library sync [--connections N]   načte knihovnu z GOG a uloží snapshot
              library stats                    rozpad knihovny podle dostupnosti depotů

              download <slug|id>               stáhne hru z Galaxy depotů do zipu
                --platform windows|mac|linux   výchozí windows
                --language en                  výchozí en
                --out DIR                      výchozí ./gog-backup
                --connections N                souběžná spojení, výchozí 8
                --limit-rate 5M                strop rychlosti (k/M/G)
                --force                        stáhnout znovu i hotový build
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

    /// <summary>`5M` → 5 * 1024^2. Bez přípony bajty za sekundu.</summary>
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

    private static (GogApiClient Api, GogHttpOptions Options) CreateApi(Dictionary<string, string> flags)
    {
        var options = OptionsFrom(flags);
        return (new GogApiClient(GogHttpFactory.Create(options, Tokens(options))), options);
    }

    private static TokenProvider Tokens(GogHttpOptions options) =>
        new(new TokenStore(), new GogAuthClient(GogHttpFactory.Create(options)));

    private static (GogApiClient Api, ContentClient Content, GogHttpOptions Options) CreateContent(
        Dictionary<string, string> flags)
    {
        var options = OptionsFrom(flags);
        var tokens = Tokens(options);
        var authed = GogHttpFactory.Create(options, tokens);

        // Chunky jdou přes podepsanou URL — Bearer hlavička na CDN nepatří.
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
            Console.WriteLine("Otevři v prohlížeči a přihlas se:");
            Console.WriteLine();
            Console.WriteLine("  " + GogConstants.LoginUrl);
            Console.WriteLine();
            Console.WriteLine("Po přihlášení skončíš na prázdné stránce. Zkopíruj z adresního řádku");
            Console.WriteLine("celou URL (obsahuje ?code=...) a vlož ji sem:");
            Console.Write("> ");
            raw = Console.ReadLine() ?? "";
        }

        if (GogAuthClient.ExtractCode(raw) is not { Length: > 0 } code)
        {
            Console.Error.WriteLine("V zadaném vstupu není authorization code.");
            return 1;
        }

        var tokens = await auth.ExchangeCodeAsync(code, ct);
        await store.SaveAsync(tokens, ct);

        Console.WriteLine($"Přihlášeno. Token uložen do {store.Path}");
        return 0;
    }

    private static async Task<int> AuthStatusAsync(CancellationToken ct)
    {
        var tokens = await new TokenStore().LoadAsync(ct);
        if (tokens is null)
        {
            Console.WriteLine("Nepřihlášen. Spusť: gogdl auth login");
            return 1;
        }

        var state = tokens.IsExpired
            ? "vypršel (obnoví se sám)"
            : $"platný do {tokens.ExpiresAt.ToLocalTime():HH:mm:ss}";
        Console.WriteLine($"Přihlášen jako user_id={tokens.UserId}, access token {state}.");
        return 0;
    }

    private static int AuthLogout()
    {
        new TokenStore().Delete();
        Console.WriteLine("Token smazán.");
        return 0;
    }

    private static async Task<int> LibrarySyncAsync(Dictionary<string, string> flags, CancellationToken ct)
    {
        var (api, options) = CreateApi(flags);

        if (options.MaxBytesPerSecond > 0)
            Console.Error.WriteLine("Pozn.: --limit-rate zatím nemá efekt, uplatní se až u stahování her.");

        Console.Error.WriteLine($"Načítám knihovnu ({options.MaxConnections} spojení)...");

        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (p.Done % 10 == 0 || p.Done == p.Total)
                Console.Error.Write($"\r  {p.Done}/{p.Total} her");
        });

        var sync = new LibrarySync(api, options.MaxConnections);
        var snapshot = await sync.RunAsync(progress, ct);
        Console.Error.WriteLine();

        var path = LibrarySnapshot.DefaultPath();
        await snapshot.SaveAsync(path, ct);

        Console.WriteLine($"Uloženo {snapshot.Games.Count} her do {path}");
        if (snapshot.Failures.Count > 0)
            Console.WriteLine($"Nepodařilo se načíst {snapshot.Failures.Count} her (viz snapshot).");

        return 0;
    }

    private static async Task<int> LibraryStatsAsync(CancellationToken ct)
    {
        var snapshot = await LibrarySnapshot.LoadAsync(LibrarySnapshot.DefaultPath(), ct);
        if (snapshot is null)
        {
            Console.Error.WriteLine("Snapshot chybí. Spusť nejdřív: gogdl library sync");
            return 1;
        }

        Console.WriteLine($"Snapshot z {snapshot.SyncedAt.ToLocalTime():yyyy-MM-dd HH:mm}, {snapshot.Games.Count} her.");
        Console.WriteLine();

        foreach (var platform in Enum.GetValues<GamePlatform>())
        {
            var present = snapshot.Games
                .Select(g => g.Platforms.GetValueOrDefault(platform))
                .OfType<PlatformAvailability>()
                .ToList();

            var depot = present.Count(p => p.HasDepot);
            var installerOnly = present.Count(p => !p.HasDepot && p.HasInstaller);
            var size = present.Sum(p => p.LargestInstallerSize);

            Console.WriteLine(
                $"{platform,-8} {present.Count,4} her   depot: {depot,4}   " +
                $"jen instalátor: {installerOnly,4}   ~{Gib(size):N0} GiB");
        }

        Console.WriteLine();

        // Tohle je číslo, kvůli kterému sync vznikl: kolik titulů by potřebovalo
        // innoextract/Wine, protože pro ně Galaxy depot neexistuje.
        var needsInstallerPath = snapshot.Games
            .Where(g => new[] { GamePlatform.Windows, GamePlatform.Mac }
                .Any(p => g.Platforms.GetValueOrDefault(p) is { HasDepot: false, HasInstaller: true }))
            .ToList();

        Console.WriteLine($"Windows/macOS titulů bez depotu: {needsInstallerPath.Count}");
        foreach (var game in needsInstallerPath.Take(30))
            Console.WriteLine($"  {game.Title}");
        if (needsInstallerPath.Count > 30)
            Console.WriteLine($"  ... a dalších {needsInstallerPath.Count - 30}");

        return 0;
    }

    private static async Task<int> DownloadAsync(
        string[] args, Dictionary<string, string> flags, CancellationToken ct)
    {
        var needle = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (needle is null)
        {
            Console.Error.WriteLine("Chybí hra. Použití: gogdl download <slug|id>");
            return 1;
        }

        var snapshot = await LibrarySnapshot.LoadAsync(LibrarySnapshot.DefaultPath(), ct);
        if (snapshot is null)
        {
            Console.Error.WriteLine("Snapshot chybí. Spusť nejdřív: gogdl library sync");
            return 1;
        }

        var game = snapshot.Games.FirstOrDefault(g =>
            string.Equals(g.Slug, needle, StringComparison.OrdinalIgnoreCase)
            || g.Id.ToString() == needle);

        if (game is null)
        {
            Console.Error.WriteLine($"Hra '{needle}' není ve snapshotu knihovny.");
            return 1;
        }

        var platformName = flags.GetValueOrDefault("platform", "windows");
        if (GamePlatformExtensions.FromApiName(platformName) is not { } platform)
        {
            Console.Error.WriteLine($"Neznámá platforma '{platformName}'. Použij windows, mac nebo linux.");
            return 1;
        }

        var (api, content, options) = CreateContent(flags);

        var downloadOptions = new DownloadOptions
        {
            OutputRoot = Path.GetFullPath(flags.GetValueOrDefault("out", "gog-backup")),
            Platform = platform,
            Language = flags.GetValueOrDefault("language", "en"),
            MaxConnections = options.MaxConnections,
            Force = flags.ContainsKey("force"),
        };

        Console.Error.WriteLine($"{game.Title} → {downloadOptions.OutputRoot}/{game.DirectoryName}");

        var result = await new GameDownloader(api, content)
            .RunAsync(game, downloadOptions, new ConsoleProgress(), ct);

        Console.Error.WriteLine();

        switch (result.Outcome)
        {
            case DownloadOutcome.Completed:
                Console.WriteLine($"Hotovo: {result.ArchivePath}");
                return 0;
            case DownloadOutcome.AlreadyDone:
                Console.WriteLine(result.Detail);
                return 0;
            default:
                Console.Error.WriteLine($"Přeskočeno: {result.Detail}");
                return 3;
        }
    }

    private static double Gib(long bytes) => bytes / 1024d / 1024d / 1024d;
}

/// <summary>Přepisuje jeden řádek, ne aby to zaplavilo terminál.</summary>
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
        var percent = p.TotalBytes > 0 ? 100.0 * p.Bytes / p.TotalBytes : 0;

        Console.Error.Write(
            $"\r  {p.Phase,-8} {percent,5:F1} %  {p.Files}/{p.TotalFiles} souborů  {speed,6:F1} MiB/s   ");
    }
}
