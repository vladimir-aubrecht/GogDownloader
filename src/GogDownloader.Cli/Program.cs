using GogDownloader.Core;
using GogDownloader.Core.Api;
using GogDownloader.Core.Auth;
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
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var command = string.Join(' ', args.TakeWhile(a => !a.StartsWith('-')));
        var flags = ParseFlags(args);

        return command switch
        {
            "auth login" => await AuthLoginAsync(flags, ct),
            "auth status" => await AuthStatusAsync(ct),
            "auth logout" => AuthLogout(),
            "library sync" => await LibrarySyncAsync(flags, ct),
            "library stats" => await LibraryStatsAsync(ct),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("""
            gogdl — záloha vlastních her z GOG

              auth login [--code <code|url>]   přihlášení přes prohlížeč
              auth status                      stav tokenu
              auth logout                      zapomenout token

              library sync [--connections N]   načte knihovnu z GOG a uloží snapshot
              library stats                    rozpad knihovny podle dostupnosti depotů
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
        MaxBytesPerSecond = flags.TryGetValue("limit-rate", out var r) && long.TryParse(r, out var bps) ? bps : 0,
    };

    private static (GogApiClient Api, GogHttpOptions Options) CreateApi(Dictionary<string, string> flags)
    {
        var options = OptionsFrom(flags);
        var store = new TokenStore();
        var authHttp = GogHttpFactory.Create(options);
        var tokens = new TokenProvider(store, new GogAuthClient(authHttp));
        return (new GogApiClient(GogHttpFactory.Create(options, tokens)), options);
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

    private static double Gib(long bytes) => bytes / 1024d / 1024d / 1024d;
}
