using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameArchiver.Core;
using GogArchiver.Core;
using GogArchiver.Core.Auth;
using GogArchiver.Core.Library;
using SteamArchiver.Core;

namespace GameArchiver.Cli;

public static class ArchiveCommandLine
{
    private static readonly HashSet<string> Switches = ["all", "dry-run", "force", "clean", "no-reuse", "no-verify", "no-launcher",
        "deep", "fix", "refresh", "keys", "help", "version", "ignore-case", "details"];
    private static readonly HashSet<string> Values = ["source", "layout", "provider-dir", "snapshot", "gog-snapshot", "out", "staging", "platform", "language",
        "arch", "branch", "directory", "connections", "compression-threads", "limit-rate", "gog-tokens", "steam-tokens", "username", "code",
        "file", "entry", "name", "path", "max-size"];

    /// <summary>Run the same CLI with supplied stores for embedding or offline integration tests.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default,
        Func<string, IEnumerable<IGameStore>>? storeFactory = null)
    {
        try { return await RunCoreAsync(args, storeFactory, ct); }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(args.Length >= 2 && args[0] == "library" && args[1] == "compare"
                ? "Interrupted. Library comparison was not completed."
                : args.Contains("archive")
                ? "Interrupted. Completed archive edits were kept; unfinished edits were not committed."
                : "Interrupted. Downloaded files were kept; rerun to resume.");
            return 130;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or NotSupportedException
            or InvalidOperationException or KeyNotFoundException or JsonException or GogAuthException or SteamAuthenticationException or TimeoutException or HttpRequestException)
        { Console.Error.WriteLine(e.Message); return 1; }
    }

    private static async Task<int> RunCoreAsync(string[] args, Func<string, IEnumerable<IGameStore>>? factory, CancellationToken ct)
    {
        var flags = new Dictionary<string, string>();
        var providerDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
            if (args[i].StartsWith("--"))
            {
                var key = args[i][2..];
                if (!Switches.Contains(key) && !Values.Contains(key)) throw new ArgumentException($"Unknown option --{key}.");
                if (Switches.Contains(key)) flags[key] = "";
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    var value = args[++i];
                    if (key == "provider-dir")
                    {
                        var parts = value.Split('=', 2);
                        if (parts.Length != 2 || !providerDirectories.TryAdd(parts[0].ToLowerInvariant(), parts[1]))
                            throw new ArgumentException("Use --provider-dir PROVIDER=FOLDER once per provider.");
                    }
                    else
                    {
                        flags[key] = value;
                    }
                }
                else throw new ArgumentException($"Missing value for --{key}.");
            }
            else positionals.Add(args[i]);
        if (flags.ContainsKey("version")) { Console.WriteLine(typeof(GameArchiverClient).Assembly.GetName().Version?.ToString(3)); return 0; }
        if (flags.ContainsKey("help") || positionals.Count == 0) { Usage(); return flags.ContainsKey("help") ? 0 : 1; }
        var source = flags.GetValueOrDefault("source", "all").ToLowerInvariant();
        ArchivePaths.ValidateName(source);
        if (factory is null && source is not "gog" and not "steam" and not "all") throw new ArgumentException($"Unknown source '{source}'.");
        var snapshotPath = flags.GetValueOrDefault("snapshot", ArchiveLibrary.DefaultPath);
        var command = positionals[0];
        var subcommand = positionals.ElementAtOrDefault(1);
        long? maxSize = null;
        if (flags.TryGetValue("max-size", out var sizeLimit))
        {
            if (command != "download" || !flags.ContainsKey("all")) throw new ArgumentException("--max-size requires download --all.");
            maxSize = ParseSizeLimit(sizeLimit);
        }
        ArchiveLibrary? library = null;
        var legacy = false;
        if (command is not "auth" and not "verify" and not "archive" && File.Exists(snapshotPath))
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath, ct));
            legacy = json.RootElement.TryGetProperty("Games", out var rawGames) && (rawGames.GetArrayLength() > 0
                && rawGames[0].TryGetProperty("Id", out _) || json.RootElement.TryGetProperty("SchemaVersion", out var schema)
                && schema.TryGetInt32(out var version) && version == LibrarySnapshot.CurrentSchema);
            library = legacy ? new() { Games = GogClient.ConvertLibrary((await LibrarySnapshot.LoadAsync(snapshotPath, ct))!).ToList() }
                : await ArchiveLibrary.LoadAsync(snapshotPath, ct);
        }
        IEnumerable<IGameStore> DefaultStores(string selected)
        {
            if (selected is "all" or "gog") yield return new GogClient(new()
            {
                SnapshotPath = flags.GetValueOrDefault("gog-snapshot", legacy ? snapshotPath : LibrarySnapshot.DefaultPath()),
                TokenPath = flags.GetValueOrDefault("gog-tokens"),
                Http = new() { MaxConnections = Integer(flags, "connections", 5) },
            });
            if (selected is "all" or "steam") yield return new SteamArchiver.Core.SteamClient(new()
            {
                TokenPath = flags.GetValueOrDefault("steam-tokens", new SteamClientOptions().TokenPath),
                Authenticate = async (challenge, token) =>
                {
                    if (challenge.Kind == SteamChallengeKind.DeviceConfirmation)
                    { Console.Error.WriteLine("Approve this login in the Steam mobile app."); return "approve"; }
                    var supplied = Environment.GetEnvironmentVariable("GAMEARCHIVE_STEAM_GUARD_CODE");
                    if (challenge.PreviousCodeIncorrect && supplied is not null) throw new SteamAuthenticationException("Supplied Steam Guard code was rejected.");
                    if (supplied is not null) return supplied;
                    Console.Error.Write($"Steam {challenge.Kind}{(challenge.PreviousCodeIncorrect ? " (previous code rejected)" : "")}: ");
                    return await ReadLineAsync(token) ?? throw new SteamAuthenticationException("No Steam Guard code supplied.");
                },
            });
        }
        var stores = (factory ?? DefaultStores)(source).ToArray();
        await using var archive = new GameArchiverClient(stores);
        if (source != "all" && !archive.Sources.Contains(source)) throw new ArgumentException($"Unknown source '{source}'.");
        if (command == "auth")
        {
            if (source == "all") throw new ArgumentException("Authentication requires --source gog or --source steam.");
            var store = stores.Single();
            if (store is GogClient gog)
            {
                if (subcommand == "login")
                {
                    var code = flags.GetValueOrDefault("code");
                    if (code is null) { Console.WriteLine(gog.LoginUrl); Console.Error.Write("Authorization URL/code: "); code = await ReadLineAsync(ct); }
                    await gog.LoginAsync(code ?? "", ct); Console.WriteLine("Logged into GOG."); return 0;
                }
                if (subcommand == "logout") { await gog.LogoutAsync(ct); Console.WriteLine("GOG tokens deleted."); return 0; }
                if (subcommand == "status") { var auth = await gog.GetAuthenticationAsync(ct); Console.WriteLine(auth is null ? "Not logged into GOG." : $"GOG user {auth.UserId}."); return auth is null ? 1 : 0; }
            }
            if (store is SteamArchiver.Core.SteamClient steam)
            {
                if (subcommand == "login")
                {
                    var username = flags.GetValueOrDefault("username") ?? Environment.GetEnvironmentVariable("GAMEARCHIVE_STEAM_USERNAME");
                    if (username is null) { Console.Error.Write("Steam account: "); username = await ReadLineAsync(ct); }
                    var password = Environment.GetEnvironmentVariable("GAMEARCHIVE_STEAM_PASSWORD") ?? await ReadPasswordAsync(ct);
                    if (string.IsNullOrWhiteSpace(username) || password.Length == 0) throw new ArgumentException("Steam account and password are required.");
                    await steam.LoginAsync(new(username, password), ct); Console.WriteLine("Logged into Steam."); return 0;
                }
                if (subcommand == "logout") { await steam.LogoutAsync(ct); Console.WriteLine("Steam tokens deleted."); return 0; }
                if (subcommand == "status") { var auth = await steam.GetAuthenticationAsync(ct); Console.WriteLine(auth is null ? "Not logged into Steam." : $"Steam account {auth.AccountName} (saved session; not validated online)."); return auth is null ? 1 : 0; }
            }
            throw new ArgumentException("Usage: auth login|status|logout --source gog|steam");
        }
        if (command == "library" && subcommand == "sync")
        {
            if (legacy || Path.GetFullPath(snapshotPath) == Path.GetFullPath(flags.GetValueOrDefault("gog-snapshot", LibrarySnapshot.DefaultPath())))
                throw new ArgumentException("Use a separate path for the aggregate snapshot; --gog-snapshot selects the original GOG snapshot.");
            library = await archive.SyncLibraryAsync(library, ct);
            await library.SaveAsync(snapshotPath, ct);
            Console.WriteLine($"Saved {library.Games.Count} product{(library.Games.Count == 1 ? "" : "s")} to {snapshotPath}.");
            PrintLibraryStats(library.Games);
            var currentFailures = library.Failures.Where(f => archive.Sources.Contains(f.Key.Split(':', 2)[0])).ToList();
            foreach (var failure in currentFailures) Console.Error.WriteLine($"Failed {FailureLabel(library, failure.Key)}: "
                + (flags.ContainsKey("details") ? failure.Value : library.FailureSummaries.GetValueOrDefault(failure.Key, failure.Value)));
            if (currentFailures.Count > 0 && !flags.ContainsKey("details"))
                Console.Error.WriteLine($"{currentFailures.Count} unresolved sync failures. Use --details for full diagnostics (also supported by library failures).");
            return currentFailures.Count == 0 ? 0 : 1;
        }
        var search = string.Join(' ', positionals.Skip(command is "library" or "archive" ? 2 : 1));
        var games = library?.Games.Where(g => source == "all" || g.Key.Source == source).ToList() ?? [];
        if (command == "library")
        {
            if (library is null) throw new FileNotFoundException("No aggregate library snapshot. Run library sync first.");
            if (subcommand == "compare")
            {
                if (source != "steam" || positionals.Count != 2 || stores.SingleOrDefault() is not SteamArchiver.Core.SteamClient steam)
                    throw new ArgumentException("Usage: library compare --source steam [--snapshot FILE] [--details]");
                if (legacy || !library.Games.Any(g => g.Key.Source == "steam")
                    && !library.Failures.Keys.Any(k => k == "steam" || k.StartsWith("steam:", StringComparison.Ordinal)))
                    throw new InvalidOperationException("No Steam data in this snapshot. Run library sync --source steam first.");
                var listed = await steam.GetOwnedGamesAsync(ct);
                PrintSteamComparison(library, listed, flags.ContainsKey("details"));
                return 0;
            }
            if (subcommand == "failures")
            {
                var failures = library.Failures.Where(f => (source == "all" || f.Key.Split(':', 2)[0] == source)
                    && (search.Length == 0 || f.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || FailureLabel(library, f.Key).Contains(search, StringComparison.OrdinalIgnoreCase)
                        || f.Value.Contains(search, StringComparison.OrdinalIgnoreCase))).OrderBy(f => f.Key).ToList();
                foreach (var failure in failures) Console.WriteLine($"{FailureLabel(library, failure.Key)}: "
                    + (flags.ContainsKey("details") ? failure.Value : library.FailureSummaries.GetValueOrDefault(failure.Key, failure.Value)));
                Console.WriteLine($"{failures.Count} recorded sync failures.");
                return 0;
            }
            games = games.Where(g => Search(g, search)).ToList();
            if (subcommand == "stats")
            { PrintLibraryStats(games); return 0; }
            if (subcommand != "list") throw new ArgumentException("Usage: library sync|list|stats|failures|compare");
            foreach (var game in games)
                Console.WriteLine(flags.ContainsKey("keys") ? game.Key.ToString() : $"{game.Key,-25} {game.Slug,-35} {game.Kind,-12} {game.Title}");
            return games.Count == 0 ? 1 : 0;
        }
        var options = Options(flags, providerDirectories);
        var knownProviders = factory is null ? new[] { "gog", "steam" } : archive.Sources.ToArray();
        if (providerDirectories.Keys.Any(p => !knownProviders.Contains(p, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("--provider-dir names an unregistered provider.");
        ProviderOutput.Validate(options, knownProviders);
        if (command == "archive")
        {
            var operation = subcommand switch
            {
                "find" => ArchiveFileOperation.Find, "add" => ArchiveFileOperation.Add, "replace" => ArchiveFileOperation.Replace,
                _ => throw new ArgumentException("Usage: archive find|add|replace [search] --file FILE --entry PATH | --name NAME | --path PATH"),
            };
            if (flags.ContainsKey("name") && flags.ContainsKey("path")) throw new ArgumentException("Use either --name or --path, not both.");
            var request = new ArchiveFileRequest
            {
                Operation = operation, FilePath = flags.GetValueOrDefault("file"), EntryPath = flags.GetValueOrDefault("entry"),
                Match = flags.GetValueOrDefault("name") ?? flags.GetValueOrDefault("path"),
                MatchBy = flags.ContainsKey("path") ? ArchiveFileMatch.Path : ArchiveFileMatch.Name,
                IgnoreCase = flags.ContainsKey("ignore-case"), DryRun = flags.ContainsKey("dry-run"), Search = search,
                Platform = flags.ContainsKey("platform") ? options.Platform : null,
            };
            var results = await archive.ProcessArchiveFilesAsync(options, request, new InlineProgress<ArchiveProgress>(p =>
                Console.Error.Write($"\r{p.Phase}: {p.Bytes}/{p.TotalBytes} bytes       ")), ct);
            Console.Error.WriteLine();
            foreach (var result in results)
            {
                Console.WriteLine($"{result.Status}: {result.Key} {result.Platform}: {result.ArchivePath}");
                foreach (var change in result.Changes)
                    Console.WriteLine($"  {change.EntryPath}{(change.BackupPath is null ? "" : $" (original -> {change.BackupPath})")}");
                if (result.Detail is not null) Console.WriteLine($"  {result.Detail}");
            }
            Console.WriteLine($"{results.Count(r => r.Status == ArchiveFileStatus.Updated)} updated, {results.Count(r => r.Status == ArchiveFileStatus.Preview)} preview, "
                + $"{results.Count(r => r.Status == ArchiveFileStatus.Matched)} matched, {results.Count(r => r.Status == ArchiveFileStatus.NoMatch)} no match, "
                + $"{results.Count(r => r.Status == ArchiveFileStatus.Failed)} failed.");
            return results.Count == 0 || results.Any(r => r.Status == ArchiveFileStatus.Failed)
                || results.All(r => r.Status == ArchiveFileStatus.NoMatch) ? 1 : 0;
        }
        if (command == "verify")
        {
            var results = await archive.VerifyAsync(options, flags.ContainsKey("deep"), flags.ContainsKey("fix"), search, ct,
                flags.ContainsKey("platform") ? options.Platform : null);
            foreach (var result in results)
            {
                if (flags.ContainsKey("keys")) { if (result.Problems.Count > 0) Console.WriteLine(result.Key); continue; }
                Console.WriteLine($"{result.Key} {result.Platform}: {(result.Missing ? "missing" : result.Problems.Count > 0 ? "broken" : result.Repaired ? "repaired" : "sound")}");
                foreach (var issue in result.Problems) Console.WriteLine($"  {issue.Entry}: {issue.Problem}");
            }
            if (!flags.ContainsKey("keys")) Console.WriteLine($"{results.Count(r => r.Problems.Count == 0 && !r.Repaired)} sound, {results.Count(r => r.Repaired)} repaired, {results.Count(r => r.Missing)} missing, {results.Count(r => !r.Missing && r.Problems.Count > 0)} broken.");
            return results.Count == 0 || results.Any(r => r.Problems.Count > 0) ? 1 : 0;
        }
        if (command == "diff")
        {
            if (library is null) throw new FileNotFoundException("No aggregate library snapshot. Run library sync first.");
            var rows = await archive.DiffAsync(library with
            { Games = library.SelectDownloads(games.Where(g => Search(g, search)), options.Platform).ToList() }, options, flags.ContainsKey("refresh"), ct);
            foreach (var row in rows)
                if (flags.ContainsKey("keys")) { if (row.Status is ArchiveStatus.Missing or ArchiveStatus.Outdated) Console.WriteLine(row.Game.Key); }
                else Console.WriteLine($"{row.Status,-12} {row.Game.Key,-25} {row.Game.Slug}: {row.Detail}");
            if (!flags.ContainsKey("keys"))
                foreach (var orphan in await archive.FindOrphansAsync(library, options, ct))
                    Console.WriteLine($"Not in library: {orphan.Metadata.Key} {orphan.Directory}");
            return rows.Any(r => r.Status is ArchiveStatus.Conflict or ArchiveStatus.Unavailable or ArchiveStatus.Unknown) ? 1 : 0;
        }
        if (command == "download")
        {
            var keys = new List<GameKey>();
            if (flags.ContainsKey("all"))
            {
                if (library is null) throw new FileNotFoundException("No aggregate library snapshot. Run library sync first.");
                var candidates = library.SelectDownloads(games.Where(g => Search(g, search)), options.Platform);
                var tooLarge = 0; var unknown = 0;
                foreach (var game in candidates)
                {
                    if (maxSize is null) { keys.Add(game.Key); continue; }
                    var version = game.Versions[options.Platform];
                    if (library.Failures.ContainsKey(game.Key.Source) || library.Failures.ContainsKey(game.Key.ToString())
                        || version.DownloadSize <= 0 || version.Branch != options.Branch
                        || !string.Equals(version.Language, options.Language, StringComparison.OrdinalIgnoreCase)
                        || version.Architecture.Length > 0 && version.Architecture != options.Architecture)
                    { unknown++; continue; }
                    if (version.DownloadSize >= maxSize.Value) { tooLarge++; continue; }
                    keys.Add(game.Key);
                }
                if (maxSize is not null)
                {
                    Console.Error.WriteLine($"Size filter: {keys.Count} selected below {maxSize.Value} bytes (download estimate); "
                        + $"{tooLarge} at or above the limit; {unknown} with unknown or incompatible size.");
                    if (unknown > 0) Console.Error.WriteLine("Run library sync to update sizes. The size filter requires the snapshot's language, branch and architecture.");
                }
            }
            else
            {
                if (search.Length == 0) throw new ArgumentException("Usage: download <source:ID|slug> or download --all");
                var parts = search.Split(':', 2);
                if (parts.Length == 2 && parts[1].All(char.IsAsciiDigit)) keys.Add(new(parts[0], parts[1]));
                else if (source != "all" && search.All(char.IsAsciiDigit)) keys.Add(new(source, search));
                else
                {
                    var matches = games.Where(g => string.Equals(g.Slug, search, StringComparison.OrdinalIgnoreCase) || g.Key.ToString() == search || g.Key.ProductId == search).ToList();
                    if (matches.Count != 1) throw new ArgumentException("Game selection is missing or ambiguous. Use source:ID.");
                    keys.Add(matches[0].Key);
                }
            }
            if (keys.Any(k => !archive.Sources.Contains(k.Source))) throw new ArgumentException("Selected product is outside --source.");
            if (keys.Count == 0) { Console.Error.WriteLine("Nothing matches."); return 1; }
            if (keys.Count > 1 && options.DirectoryName is not null) throw new ArgumentException("--directory requires a single game.");
            var failed = false;
            foreach (var key in keys.Distinct())
            {
                if (flags.ContainsKey("dry-run"))
                {
                    var resolved = ProviderOutput.Resolve(options, key.Source);
                    Console.WriteLine(key);
                    if (options.Layout == ArchiveLayout.Provider) Console.Error.WriteLine($"{key}: output root {resolved.OutputRoot}");
                    continue;
                }
                try
                {
                    var result = await archive.DownloadAsync(key, options, new InlineProgress<ArchiveProgress>(p =>
                        Console.Error.Write($"\r{key} {p.Phase}: {p.Bytes}/{p.TotalBytes} bytes, {p.Files}/{p.TotalFiles} files       ")), ct);
                    Console.Error.WriteLine();
                    Console.WriteLine($"{result.Outcome}: {result.Key} {result.ArchivePath ?? result.Detail}");
                    failed |= result.Outcome is not ArchiveOutcome.Completed and not ArchiveOutcome.AlreadyDone;
                }
                catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"Failed {key}: {e.Message}"); failed = true; }
            }
            return failed ? 1 : 0;
        }
        throw new ArgumentException($"Unknown command '{command}'. Use --help.");
    }

    private static string FailureLabel(ArchiveLibrary library, string key)
    {
        if (!key.Contains(':')) return key;
        var name = library.FailureNames.GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(name)) name = library.Games.FirstOrDefault(g => g.Key.ToString() == key)?.Title;
        return $"{(string.IsNullOrWhiteSpace(name) ? "Name unavailable" : name)} ({key})";
    }

    private static void PrintLibraryStats(IEnumerable<GameInfo> games)
    {
        var order = new[] { "game", "dlc", "application", "demo", "tool", "music", "video", "pack", "container" };
        foreach (var source in games.GroupBy(g => g.Key.Source).OrderBy(g => g.Key))
        {
            var kinds = source.GroupBy(g => (g.Kind ?? "").ToLowerInvariant())
                .OrderBy(g => Array.IndexOf(order, g.Key) is var index && index >= 0 ? index : order.Length).ThenBy(g => g.Key)
                .Select(g => $"{g.Count()} " + (g.Key switch
                {
                    "game" or "application" or "demo" or "tool" or "pack" or "container" or "video" => g.Key + (g.Count() == 1 ? "" : "s"),
                    "dlc" => "DLC", "" => "unknown", _ => g.Key,
                }));
            Console.WriteLine($"{source.Key}: {source.Count()} product{(source.Count() == 1 ? "" : "s")} ({string.Join(", ", kinds)}), "
                + $"{source.Count(g => g.Versions.Keys.Any(g.CanBackUp))} archivable");
        }
    }

    private static void PrintSteamComparison(ArchiveLibrary library, IReadOnlyDictionary<uint, string> listed, bool details)
    {
        var products = library.Games.Where(g => g.Key.Source == "steam").ToDictionary(g => g.Key.ProductId);
        var names = listed.ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture), p => p.Value);
        var games = products.Values.Where(g => string.Equals(g.Kind, "game", StringComparison.OrdinalIgnoreCase)).ToList();
        var onlySnapshot = games.Where(g => !names.ContainsKey(g.Key.ProductId)).OrderBy(g => g.Title).ToList();
        var onlyListed = names.Where(p => !products.ContainsKey(p.Key)).OrderBy(p => p.Value).ThenBy(p => p.Key).ToList();
        var otherListed = products.Values.Where(g => names.ContainsKey(g.Key.ProductId)
            && !string.Equals(g.Kind, "game", StringComparison.OrdinalIgnoreCase)).OrderBy(g => g.Title).ToList();
        Console.WriteLine($"Steam snapshot: {products.Count} products, {games.Count} tagged as game.");
        Console.WriteLine($"GetOwnedGames: {listed.Count} entries (includes played free games, free packages and unvetted apps).");
        Console.WriteLine($"Snapshot games present in GetOwnedGames: {games.Count - onlySnapshot.Count} of {games.Count}.");
        Console.WriteLine($"Differences: {onlySnapshot.Count} snapshot games absent from GetOwnedGames; "
            + $"{onlyListed.Count} GetOwnedGames entries absent from the snapshot; {otherListed.Count} listed products with another snapshot type.");
        foreach (var game in onlySnapshot) Console.WriteLine($"SnapshotGameOnly {game.Key}: {game.Title} (type: {game.Kind})");
        foreach (var (id, name) in onlyListed) Console.WriteLine($"GetOwnedGamesOnly steam:{id}: {(string.IsNullOrWhiteSpace(name) ? "Name unavailable" : name)}");
        foreach (var game in otherListed) Console.WriteLine($"OtherListedProduct {game.Key}: {game.Title} (type: {game.Kind})");
        foreach (var (key, failure) in library.Failures.Where(f => f.Key == "steam" || f.Key.StartsWith("steam:", StringComparison.Ordinal)).OrderBy(f => f.Key))
        {
            var id = key.Split(':', 2).ElementAtOrDefault(1);
            var present = id is not null && names.ContainsKey(id);
            var label = id is not null && names.TryGetValue(id, out var freshName) && !string.IsNullOrWhiteSpace(freshName)
                ? $"{freshName} ({key})" : FailureLabel(library, key);
            var state = id is null ? "source failure" : present ? "listed by GetOwnedGames" : "not listed by GetOwnedGames";
            Console.WriteLine($"SyncFailure {label} [{state}]: "
                + (details ? failure : library.FailureSummaries.GetValueOrDefault(key, failure)));
        }
        Console.WriteLine("This compares saved metadata with a live API list, not the Steam client's counter. "
            + "Listed apps are not proof of depot access. The snapshot and its failures are unchanged.");
    }

    private static long ParseSizeLimit(string value)
    {
        var match = Regex.Match(value.Trim(), @"^([0-9]+(?:\.[0-9]+)?)\s*(B|K|M|G|T|KB|MB|GB|TB|KiB|MiB|GiB|TiB)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success && decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            var multiplier = match.Groups[2].Value.ToLowerInvariant() switch
            {
                "kb" => 1_000L, "mb" => 1_000_000L, "gb" => 1_000_000_000L, "tb" => 1_000_000_000_000L,
                "k" or "kib" => 1L << 10, "m" or "mib" => 1L << 20, "g" or "gib" => 1L << 30, "t" or "tib" => 1L << 40,
                _ => 1L,
            };
            try
            {
                var bytes = number * multiplier;
                if (bytes >= 1 && bytes <= long.MaxValue) return (long)bytes;
            }
            catch (OverflowException) { }
        }
        throw new ArgumentException("Invalid --max-size. Use a positive size in bytes or units, for example 100MB or 1GiB.");
    }

    private static bool Search(GameInfo game, string search) => search.Length == 0
        || game.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || game.Slug.Contains(search, StringComparison.OrdinalIgnoreCase);
    private static int Integer(Dictionary<string, string> flags, string name, int fallback)
    {
        if (!flags.TryGetValue(name, out var value)) return fallback;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
            : throw new ArgumentException($"Invalid integer for --{name}.");
    }
    private static ArchiveOptions Options(Dictionary<string, string> flags, Dictionary<string, string> providerDirectories)
    {
        var platform = flags.GetValueOrDefault("platform", "windows").ToLowerInvariant() switch
        { "windows" or "win" => GameOs.Windows, "linux" => GameOs.Linux, "mac" or "macos" or "osx" => GameOs.Mac, _ => throw new ArgumentException("Unknown platform.") };
        var rate = flags.GetValueOrDefault("limit-rate", "0");
        if (string.IsNullOrWhiteSpace(rate)) throw new ArgumentException("Invalid bandwidth limit.");
        var multiplier = char.ToLowerInvariant(rate[^1]) switch { 'k' => 1024L, 'm' => 1024L * 1024, 'g' => 1024L * 1024 * 1024, _ => 1L };
        if (!double.TryParse(multiplier == 1 ? rate : rate[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw new ArgumentException("Invalid bandwidth limit.");
        var amount = number * multiplier;
        if (!double.IsFinite(amount) || amount < 0 || amount > long.MaxValue) throw new ArgumentException("Invalid bandwidth limit.");
        var options = new ArchiveOptions
        {
            OutputRoot = Path.GetFullPath(flags.GetValueOrDefault("out", "game-backup")), Platform = platform,
            Layout = flags.GetValueOrDefault("layout", providerDirectories.Count > 0 ? "provider" : "flat").ToLowerInvariant() switch
            { "flat" => ArchiveLayout.Flat, "provider" => ArchiveLayout.Provider, _ => throw new ArgumentException("Unknown layout; use flat or provider.") },
            ProviderDirectories = providerDirectories,
            Language = flags.GetValueOrDefault("language", "en"), Branch = flags.GetValueOrDefault("branch", "public"),
            Architecture = flags.GetValueOrDefault("arch", "64"), StagingRoot = flags.GetValueOrDefault("staging"),
            DirectoryName = flags.GetValueOrDefault("directory"), MaxConnections = Integer(flags, "connections", 5),
            CompressionThreads = Integer(flags, "compression-threads", Environment.ProcessorCount), MaxBytesPerSecond = (long)amount,
            Force = flags.ContainsKey("force"), Clean = flags.ContainsKey("clean"), Reuse = !flags.ContainsKey("no-reuse"),
            Verify = !flags.ContainsKey("no-verify"), WriteLauncher = !flags.ContainsKey("no-launcher"),
        };
        options.Validate(); return options;
    }
    private static async Task<string> ReadPasswordAsync(CancellationToken ct)
    {
        if (Console.IsInputRedirected) throw new ArgumentException("Set GAMEARCHIVE_STEAM_PASSWORD for noninteractive login.");
        Console.Error.Write("Steam password: ");
        return await ReadLineAsync(ct, hide: true) ?? "";
    }
    private static async Task<string?> ReadLineAsync(CancellationToken ct, bool hide = false)
    {
        // Console.In's synchronized reader can block even through ReadLineAsync. Keep terminal
        // prompts cancellable while Steam Guard/mobile approval has an operation timeout.
        if (Console.IsInputRedirected) return await Task.Run(Console.ReadLine, CancellationToken.None).WaitAsync(ct);
        var value = new System.Text.StringBuilder();
        while (true)
        {
            while (!Console.KeyAvailable) await Task.Delay(50, ct);
            ct.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace)
            { if (value.Length > 0) { value.Length--; if (!hide) Console.Error.Write("\b \b"); } }
            else if (!char.IsControl(key.KeyChar)) { value.Append(key.KeyChar); if (!hide) Console.Error.Write(key.KeyChar); }
        }
    }
    private static void Usage() => Console.WriteLine("""
        gamearchiver — archive owned GOG and Steam games
          auth login|status|logout --source gog|steam
          library sync|list|stats|failures [search] [--source gog|steam|all]
            --details shows full diagnostics for library sync or library failures
          library compare --source steam [--details]   compare the snapshot with live GetOwnedGames
          download <source:ID|slug> [--source gog|steam] | download --all [search]
          diff [search] [--refresh] [--keys]
          verify [search] [--deep] [--fix] [--keys]
          archive find [search] (--name NAME | --path PATH) [--ignore-case]
          archive add [search] --file FILE [--entry PATH] [--dry-run]
          archive replace [search] --file FILE (--name NAME | --path PATH) [--ignore-case] [--dry-run]
        Archive files: all completed ZIPs in the selected output/source/layout; all platforms unless filtered.
          Replaced entries gain .backup (repeated if occupied). Matches are literal and case-sensitive by default.
        Options: --out DIR --staging DIR --snapshot FILE --gog-snapshot FILE
          --layout flat|provider --provider-dir PROVIDER=FOLDER (repeatable; implies provider layout)
          --platform windows|linux|mac --language en --arch 32|64|arm64 --branch public
          --directory NAME --connections N --compression-threads N --limit-rate 5M
          --max-size 100MB (download --all: below the recorded full download size; unknown sizes skipped)
          --force --clean --no-reuse --no-verify --no-launcher --dry-run
        Auth: --gog-tokens FILE --steam-tokens FILE --username NAME --code URL
          Steam password: interactive prompt or GAMEARCHIVE_STEAM_PASSWORD
          Steam Guard: interactive prompt/mobile approval or GAMEARCHIVE_STEAM_GUARD_CODE
        """);
}
