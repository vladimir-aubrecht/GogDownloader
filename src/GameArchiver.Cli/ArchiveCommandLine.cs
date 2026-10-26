using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameArchiver.Core;
using GogArchiver.Core;
using GogArchiver.Core.Auth;
using GogArchiver.Core.Library;
using SteamArchiver.Core;

namespace GameArchiver.Cli;

public static partial class ArchiveCommandLine
{
    private static readonly HashSet<string> Switches = ["all", "dry-run", "force", "clean", "no-reuse", "no-verify", "no-launcher",
        "deep", "fix", "refresh", "keys", "help", "version", "ignore-case", "details", "inventory"];
    private static readonly HashSet<string> Values = ["source", "layout", "provider-dir", "snapshot", "gog-snapshot", "out", "staging", "platform", "language",
        "arch", "branch", "directory", "connections", "compression-threads", "limit-rate", "gog-tokens", "steam-tokens", "username", "code",
        "file", "entry", "name", "path", "max-size", "drm", "note"];

    /// <summary>Run the same CLI with supplied stores for embedding or offline integration tests.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default,
        Func<string, IEnumerable<IGameStore>>? storeFactory = null, Func<IDrmSource>? drmSourceFactory = null)
    {
        try { return await RunCoreAsync(args, storeFactory, drmSourceFactory, ct); }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(args.Length >= 2 && args[0] == "library" && args[1] == "drm"
                ? "Interrupted. DRM metadata changes were not committed."
                : args.Length >= 2 && args[0] == "library" && args[1] == "compare"
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

    private static async Task<int> RunCoreAsync(string[] args, Func<string, IEnumerable<IGameStore>>? factory, Func<IDrmSource>? drmSourceFactory, CancellationToken ct)
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
        var drmFilter = flags.GetValueOrDefault("drm")?.ToLowerInvariant();
        if (drmFilter is not null)
        {
            if (command == "library" && subcommand == "drm") { /* Validated by the DRM command. */ }
            else if (command == "library" && subcommand == "list" || command == "download" && flags.ContainsKey("all"))
            { if (drmFilter != "any") _ = DrmCatalog.Parse(drmFilter); }
            else throw new ArgumentException("--drm requires library list, library drm set, or download --all.");
        }
        if (flags.ContainsKey("note") && !(command == "library" && subcommand == "drm" && positionals.ElementAtOrDefault(2) == "set"))
            throw new ArgumentException("--note requires library drm set.");
        if (flags.ContainsKey("inventory") && (command != "library" || subcommand is not "list" and not "stats"))
            throw new ArgumentException("--inventory requires library list or library stats.");
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
            if (legacy)
            {
                var gogSnapshot = (await LibrarySnapshot.LoadAsync(snapshotPath, ct))!;
                library = new()
                {
                    Games = GogClient.ConvertLibrary(gogSnapshot).ToList(),
                    Failures = gogSnapshot.Failures.ToDictionary(f => $"gog:{f.Key.ToString(CultureInfo.InvariantCulture)}", f => f.Value),
                };
            }
            else library = await ArchiveLibrary.LoadAsync(snapshotPath, ct);
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
        if (command == "library" && subcommand == "drm")
        {
            if (legacy) throw new ArgumentException("DRM annotations require an aggregate snapshot. Run library sync first.");
            return await RunDrmAsync(library ?? throw new FileNotFoundException("Run library sync first."), snapshotPath,
                positionals, flags, source, drmSourceFactory, ct);
        }
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
            PrintCatalogStats(library, source);
            var currentFailures = library.Failures.Where(f => archive.Sources.Contains(f.Key.Split(':', 2)[0])).ToList();
            foreach (var failure in currentFailures)
            {
                var parts = failure.Key.Split(':', 2);
                var inventoryOnly = parts.Length == 2 && library.PrimaryCatalogs.TryGetValue(parts[0], out var catalog)
                    && !catalog.Contains(parts[1]) && library.FailureImpacts.GetValueOrDefault(failure.Key, []).Count == 0;
                Console.Error.WriteLine($"{(inventoryOnly ? "Inventory metadata unavailable" : "Failed")} {FailureLabel(library, failure.Key)}: "
                    + (flags.ContainsKey("details") ? failure.Value : library.FailureSummaries.GetValueOrDefault(failure.Key, failure.Value)));
                PrintFailureImpact(library, failure.Key, flags.ContainsKey("details"), Console.Error);
            }
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
                if (source is not "steam" and not "gog" || positionals.Count != 2 || stores.Length != 1)
                    throw new ArgumentException("Usage: library compare --source gog|steam [--snapshot FILE] [--details]");
                var emptyAggregate = !legacy && library.Games.Count == 0 && library.Failures.Count == 0;
                if (!emptyAggregate && !(legacy && source == "gog") && !library.Games.Any(g => g.Key.Source == source)
                    && !library.PrimaryCatalogs.ContainsKey(source)
                    && !library.Failures.Keys.Any(k => k == source || k.StartsWith(source + ":", StringComparison.Ordinal)))
                    throw new InvalidOperationException($"No {(source == "steam" ? "Steam" : "GOG")} data in this snapshot. Run library sync --source {source} first.");
                if (source == "steam" && stores[0] is SteamArchiver.Core.SteamClient steam)
                    PrintSteamComparison(library, await steam.GetOwnedGamesAsync(ct), flags.ContainsKey("details"));
                else if (source == "gog" && stores[0] is GogClient gog)
                    PrintGogComparison(library, await gog.GetOwnedGameIdsAsync(ct), flags.ContainsKey("details"));
                else throw new NotSupportedException($"Library comparison is not supported by the supplied {source} provider.");
                return 0;
            }
            if (subcommand == "failures")
            {
                var failures = library.Failures.Where(f => (source == "all" || f.Key.Split(':', 2)[0] == source)
                    && (search.Length == 0 || f.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || FailureLabel(library, f.Key).Contains(search, StringComparison.OrdinalIgnoreCase)
                        || library.FailureImpacts.GetValueOrDefault(f.Key, []).Any(i => FailureLabel(library, f.Key.Split(':', 2)[0] + ":" + i.ProductId).Contains(search, StringComparison.OrdinalIgnoreCase))
                        || f.Value.Contains(search, StringComparison.OrdinalIgnoreCase))).OrderBy(f => f.Key).ToList();
                foreach (var failure in failures)
                {
                    Console.WriteLine($"{FailureLabel(library, failure.Key)}: "
                        + (flags.ContainsKey("details") ? failure.Value : library.FailureSummaries.GetValueOrDefault(failure.Key, failure.Value)));
                    PrintFailureImpact(library, failure.Key, flags.ContainsKey("details"), Console.Out);
                }
                Console.WriteLine($"{failures.Count} recorded sync failures.");
                return 0;
            }
            games = games.Where(g => Search(g, search) && (flags.ContainsKey("inventory") || library.IsCatalogEntry(g.Key))).ToList();
            var drmSelection = Options(flags, providerDirectories);
            if (drmFilter is not null && drmFilter != "any") games = games.Where(g =>
                DrmCatalog.Get(library, g.Key, drmSelection.Platform, drmSelection.Branch).Status == DrmCatalog.Parse(drmFilter)).ToList();
            if (subcommand == "stats")
            { PrintLibraryStats(games); if (search.Length == 0) PrintCatalogStats(library, source); return 0; }
            if (subcommand != "list") throw new ArgumentException("Usage: library sync|list|stats|failures|compare");
            foreach (var game in games)
            {
                Console.WriteLine(flags.ContainsKey("keys") ? game.Key.ToString() : $"{game.Key,-25} {game.Slug,-35} {game.Kind,-12} {game.Title}"
                    + (drmFilter is null ? "" : $" [DRM: {DrmCatalog.Get(library, game.Key, drmSelection.Platform, drmSelection.Branch).Status.ToString().ToLowerInvariant()}]"));
                if (drmFilter is not null && flags.ContainsKey("details") && !flags.ContainsKey("keys"))
                {
                    var drm = DrmCatalog.Get(library, game.Key, drmSelection.Platform, drmSelection.Branch);
                    Console.WriteLine($"  Source: {drm.Source}; branch: {drm.Branch}; checked: {drm.CheckedAt?.ToString("O") ?? "not verified"}");
                    if (drm.Url is not null) Console.WriteLine($"  {drm.Url}");
                    if (drm.Notes is not null) Console.WriteLine($"  {drm.Notes}");
                }
            }
            return games.Count == 0 ? 1 : 0;
        }
        var options = Options(flags, providerDirectories) with { MaxDownloadSize = maxSize };
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
                var tooLarge = 0; var drmExcluded = 0;
                var failedSync = 0; var incompatible = 0; var missing = 0; var outdated = 0;
                var downloadEstimates = 0; var installedEstimates = 0;
                foreach (var game in candidates)
                {
                    if (drmFilter is not null && drmFilter != "any"
                        && DrmCatalog.Get(library, game.Key, options.Platform, options.Branch).Status != DrmCatalog.Parse(drmFilter))
                    { drmExcluded++; continue; }
                    if (maxSize is null) { keys.Add(game.Key); continue; }
                    var version = game.Versions[options.Platform];
                    void Explain(string reason)
                    {
                        if (flags.ContainsKey("details")) Console.Error.WriteLine($"{game.Key} ({game.Title}): {reason}");
                    }
                    if (library.Failures.ContainsKey(game.Key.Source) || library.Failures.ContainsKey(game.Key.ToString()))
                    { failedSync++; Explain("size excluded because the last source/product sync failed"); continue; }
                    if (version.Branch != options.Branch
                        || !string.Equals(version.Language, options.Language, StringComparison.OrdinalIgnoreCase)
                        || version.Architecture.Length > 0 && version.Architecture != options.Architecture)
                    { incompatible++; Explain($"size describes {options.Platform}/{version.Language}/{version.Architecture}/{version.Branch}, not the requested selection"); continue; }
                    var oldSteamEstimate = game.Key.Source == "steam" && version.SizeEstimateRevision < 1;
                    // Old Steam compressed totals could omit nonempty zero-estimate depots.
                    // Installed totals were recorded independently and remain a useful estimate
                    // for selecting small games. The provider still checks real transfer sizes.
                    var useInstalled = game.Key.Source == "steam" && (oldSteamEstimate || version.DownloadSize <= 0);
                    var estimate = useInstalled ? version.InstalledSize : version.DownloadSize;
                    if (estimate <= 0)
                    {
                        if (oldSteamEstimate) { outdated++; Explain("old Steam download estimate with no usable installed-size fallback; run library sync --source steam"); }
                        else { missing++; Explain("no usable size estimate in the snapshot"); }
                        continue;
                    }
                    if (useInstalled) installedEstimates++; else downloadEstimates++;
                    Explain($"{estimate} bytes ({(useInstalled ? "installed-size fallback" : "download estimate")}); {(estimate < maxSize.Value ? "below" : "at or above")} limit");
                    if (estimate >= maxSize.Value) { tooLarge++; continue; }
                    keys.Add(game.Key);
                }
                if (drmFilter is not null) Console.Error.WriteLine($"DRM filter ({drmFilter}): {drmExcluded} excluded. Uses reported status for {options.Platform}/{options.Branch}; unknown is not DRM-free.");
                if (maxSize is not null)
                {
                    var unknown = failedSync + incompatible + missing + outdated;
                    Console.Error.WriteLine($"Size filter: {keys.Count} selected below {maxSize.Value} bytes; "
                        + $"{tooLarge} at or above the limit; {unknown} with unknown or incompatible size.");
                    Console.Error.WriteLine($"Estimate basis: {downloadEstimates} download estimates, {installedEstimates} Steam installed-size fallbacks.");
                    if (unknown > 0) Console.Error.WriteLine($"Skipped sizes: {outdated} old Steam estimates without a fallback; {missing} missing estimates; "
                        + $"{failedSync} failed syncs; {incompatible} language/branch/architecture mismatches. Use --details for individual reasons.");
                    if (outdated > 0) Console.Error.WriteLine("Run library sync --source steam with the rebuilt version to replace old estimates.");
                    else if (missing > 0 || failedSync > 0) Console.Error.WriteLine("Run library sync to refresh metadata; Steam may still omit some sizes.");
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
                    var downloadOptions = options with { Drm = library is null ? DrmInfo.ForStore(key.Source) with { Branch = options.Branch } : DrmCatalog.Get(library, key, options.Platform, options.Branch) };
                    var result = await archive.DownloadAsync(key, downloadOptions, new InlineProgress<ArchiveProgress>(p =>
                        Console.Error.Write($"\r{key} {p.Phase}: {p.Bytes}/{p.TotalBytes} bytes, {p.Files}/{p.TotalFiles} files       ")), ct);
                    Console.Error.WriteLine();
                    Console.WriteLine($"{result.Outcome}: {result.Key} {result.ArchivePath ?? result.Detail}");
                    failed |= result.Outcome is not ArchiveOutcome.Completed and not ArchiveOutcome.AlreadyDone and not ArchiveOutcome.SkippedSize;
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

    private static void PrintCatalogStats(ArchiveLibrary library, string selectedSource)
    {
        foreach (var (source, ids) in library.PrimaryCatalogs.Where(c => selectedSource == "all" || selectedSource == c.Key).OrderBy(c => c.Key))
        {
            var known = library.Games.Where(g => g.Key.Source == source && ids.Contains(g.Key.ProductId)).ToList();
            Console.WriteLine($"{source} primary catalog: {ids.Count} entries, {known.Count} with saved metadata, "
                + $"{known.Count(g => g.Versions.Keys.Any(g.CanBackUp))} archivable; "
                + $"{library.Games.Count(g => g.Key.Source == source && !ids.Contains(g.Key.ProductId))} additional inventory products.");
            if (library.Failures.ContainsKey(source)) Console.WriteLine($"{source} catalog is from the previous sync; the source refresh failed.");
        }
    }

    private static void PrintFailureImpact(ArchiveLibrary library, string key, bool details, TextWriter writer)
    {
        var parts = key.Split(':', 2);
        if (parts.Length != 2) return;
        var impacts = library.FailureImpacts.GetValueOrDefault(key, []);
        foreach (var impact in impacts)
        {
            var selection = impact.Platform is { } platform ? $"{platform}/{impact.Language}/{impact.Architecture}/{impact.Branch}" : "product metadata";
            var stale = impact.IsStale || library.Failures.ContainsKey(parts[0]);
            writer.WriteLine($"  {(stale ? "Previously blocked (not rechecked)" : "Blocks")} {FailureLabel(library, parts[0] + ":" + impact.ProductId)} [{selection}]"
                + (impact.RequiredProductId is null ? "." : $"; requires {parts[0]}:{impact.RequiredProductId}"
                    + (impact.DepotId is null ? "." : $", depot {impact.DepotId}.")));
            if (details && impact.Detail is not null) writer.WriteLine($"    {impact.Detail}");
        }
        if (impacts.Count == 0 && library.PrimaryCatalogs.TryGetValue(parts[0], out var catalog))
            writer.WriteLine(catalog.Contains(parts[1])
                ? "  Primary catalog product; dependency impact is not yet known."
                : "  Inventory metadata outside the primary catalog; no blocking catalog dependency confirmed. Other selections may still require it.");
        if (impacts.Count > 0 && library.Failures.ContainsKey(parts[0]))
            writer.WriteLine("  Impact is from the previous sync; the source refresh failed.");
        else if (library.Failures.ContainsKey(parts[0]) && library.PrimaryCatalogs.ContainsKey(parts[0]))
            writer.WriteLine("  Catalog membership is from the previous sync; the source refresh failed.");
    }

    private static void PrintGogComparison(ArchiveLibrary library, IReadOnlyList<long> listed, bool details)
    {
        var products = library.Games.Where(g => g.Key.Source == "gog").ToDictionary(g => g.Key.ProductId);
        var owned = listed.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToHashSet();
        var onlySnapshot = products.Values.Where(g => !owned.Contains(g.Key.ProductId)).OrderBy(g => g.Title).ToList();
        var onlyOwned = listed.Where(id => !products.ContainsKey(id.ToString(CultureInfo.InvariantCulture))).Order().ToList();
        Console.WriteLine($"GOG snapshot: {products.Count} products.");
        Console.WriteLine($"GOG owned-product API: {listed.Count} products (includes DLC and packs).");
        Console.WriteLine($"Snapshot products present in the owned list: {products.Count - onlySnapshot.Count} of {products.Count}.");
        Console.WriteLine($"Differences: {onlySnapshot.Count} snapshot products absent from the owned list; "
            + $"{onlyOwned.Count} owned products absent from the snapshot.");
        foreach (var game in onlySnapshot) Console.WriteLine($"SnapshotOnly {game.Key}: {game.Title} (type: {game.Kind})");
        foreach (var id in onlyOwned) Console.WriteLine($"OwnedOnly {FailureLabel(library, $"gog:{id.ToString(CultureInfo.InvariantCulture)}")}");
        foreach (var (key, failure) in library.Failures.Where(f => f.Key == "gog" || f.Key.StartsWith("gog:", StringComparison.Ordinal)).OrderBy(f => f.Key))
        {
            var id = key.Split(':', 2).ElementAtOrDefault(1);
            var state = id is null ? "source failure" : owned.Contains(id) ? "listed as owned" : "not listed as owned";
            Console.WriteLine($"SyncFailure {FailureLabel(library, key)} [{state}]: "
                + (details ? failure : library.FailureSummaries.GetValueOrDefault(key, failure)));
        }
        Console.WriteLine("This compares saved product IDs with GOG's live ownership list, the same endpoint used by sync. "
            + "Names come from the snapshot; the API returns IDs only. Ownership does not prove download access. "
            + "The snapshot and its failures are unchanged.");
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
        Console.WriteLine($"GetOwnedGames: {listed.Count} entries (requests free games including unplayed titles, free packages and unvetted apps).");
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
            PrintFailureImpact(library, key, details, Console.Out);
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
            --inventory includes products outside the primary catalog in library list/stats
          library compare --source gog|steam [--details]   compare the snapshot with a live product list
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
          --max-size 100MB (download --all: download estimate, falling back to Steam installed size; unknown sizes skipped)
          --drm free|protected|unknown|any (library list or download --all; reported status for the selected OS/branch)
        DRM: library drm sync [--source steam|gog|all] [--refresh]
          library drm set SOURCE:ID --platform windows|linux|mac --drm free|protected|unknown --note TEXT
          library drm clear SOURCE:ID --platform windows|linux|mac
          --force --clean --no-reuse --no-verify --no-launcher --dry-run
        Auth: --gog-tokens FILE --steam-tokens FILE --username NAME --code URL
          Steam password: interactive prompt or GAMEARCHIVE_STEAM_PASSWORD
          Steam Guard: interactive prompt/mobile approval or GAMEARCHIVE_STEAM_GUARD_CODE
        """);
}
