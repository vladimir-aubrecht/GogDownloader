using GameArchiver.Core;
using GogArchiver.Core.Download.Zip;

namespace GameArchiver;

public enum ArchiveStatus { Current, Missing, Outdated, Conflict, Unavailable, Unknown }
public sealed record ArchiveDifference(GameInfo Game, GameOs Platform, ArchiveStatus Status, string Detail);
public sealed record VerificationResult(GameKey Key, GameOs Platform, string ArchivePath,
    IReadOnlyList<ArchiveProblem> Problems, bool Repaired = false, bool Missing = false);

/// <summary>Aggregates independently usable store clients. Owns the supplied clients by default.</summary>
public sealed partial class GameArchiverClient : IAsyncDisposable
{
    private readonly Dictionary<string, IGameStore> _stores;
    private readonly bool _ownsStores;
    public IReadOnlyCollection<string> Sources => _stores.Keys;
    public GameArchiverClient(params IGameStore[] stores) : this(stores, true) { }
    public GameArchiverClient(IEnumerable<IGameStore> stores, bool ownsStores)
    {
        _stores = new(StringComparer.OrdinalIgnoreCase); _ownsStores = ownsStores;
        foreach (var store in stores)
        {
            ArchivePaths.ValidateName(store.Source);
            if (!_stores.TryAdd(store.Source.ToLowerInvariant(), store))
                throw new ArgumentException($"Duplicate store source '{store.Source}'.", nameof(stores));
        }
    }
    private IGameStore Store(string source) => _stores.GetValueOrDefault(source)
        ?? throw new NotSupportedException($"No store registered for source '{source}'.");

    public async Task<ArchiveLibrary> SyncLibraryAsync(ArchiveLibrary? previous = null, CancellationToken ct = default)
    {
        var results = await Task.WhenAll(_stores.Values.Select(async store =>
        {
            var source = store.Source.ToLowerInvariant();
            try
            {
                var snapshot = await store.SyncLibraryAsync(ct);
                if (snapshot.Games.Any(g => g.Key.Source != source)
                    || snapshot.Games.GroupBy(g => g.Key).Any(g => g.Count() > 1))
                    throw new InvalidDataException($"Store {source} returned invalid or duplicate identities.");
                var games = snapshot.Games.ToList();
                foreach (var old in previous?.Games.Where(g => g.Key.Source == source) ?? [])
                    if (snapshot.Failures.ContainsKey(old.Key.ProductId) && games.All(g => g.Key != old.Key)) games.Add(old);
                var names = new Dictionary<string, string>();
                foreach (var id in snapshot.Failures.Keys)
                {
                    var key = new GameKey(source, id);
                    var name = snapshot.FailureNames.GetValueOrDefault(id);
                    if (string.IsNullOrWhiteSpace(name)) name = games.FirstOrDefault(g => g.Key == key)?.Title;
                    if (string.IsNullOrWhiteSpace(name)) name = previous?.FailureNames.GetValueOrDefault(key.ToString());
                    if (!string.IsNullOrWhiteSpace(name)) names[key.ToString()] = name;
                }
                return (Games: games, Failures: snapshot.Failures.ToDictionary(f => new GameKey(source, f.Key).ToString(), f => f.Value), Names: names,
                    Summaries: snapshot.FailureSummaries.Where(f => snapshot.Failures.ContainsKey(f.Key) && !string.IsNullOrWhiteSpace(f.Value))
                        .ToDictionary(f => new GameKey(source, f.Key).ToString(), f => f.Value));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return (Games: previous?.Games.Where(g => g.Key.Source == source).ToList() ?? [],
                    Failures: new Dictionary<string, string> { [source] = e.Message },
                    Names: previous?.FailureNames.Where(f => f.Key.Split(':', 2)[0] == source).ToDictionary() ?? new Dictionary<string, string>(),
                    Summaries: new Dictionary<string, string>());
            }
        }));
        // Syncing a selected source must not remove other stores from a shared snapshot.
        var unchanged = previous?.Games.Where(g => !_stores.ContainsKey(g.Key.Source)) ?? [];
        return new()
        {
            Games = results.SelectMany(r => r.Games).Concat(unchanged).OrderBy(g => g.Title).ThenBy(g => g.Key.Source).ToList(),
            Failures = results.SelectMany(r => r.Failures).Concat(previous?.Failures
                .Where(f => !_stores.ContainsKey(f.Key.Split(':', 2)[0])) ?? [])
                .ToDictionary(p => p.Key, p => p.Value),
            FailureNames = results.SelectMany(r => r.Names).Concat(previous?.FailureNames
                .Where(f => !_stores.ContainsKey(f.Key.Split(':', 2)[0])) ?? []).ToDictionary(),
            FailureSummaries = results.SelectMany(r => r.Summaries).Concat(previous?.FailureSummaries
                .Where(f => !_stores.ContainsKey(f.Key.Split(':', 2)[0])) ?? []).ToDictionary(),
        };
    }

    public Task<GameVersion> GetVersionAsync(GameKey key, GameSelection options, CancellationToken ct = default) =>
        Store(key.Source).GetVersionAsync(key.ProductId, options, ct);
    public async Task<ArchiveResult> DownloadAsync(GameKey key, ArchiveOptions options,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        var store = Store(key.Source);
        ProviderOutput.Validate(options, _stores.Keys);
        options = await ProviderOutput.PrepareAsync(options, key.Source, ct);
        return await store.DownloadAsync(key.ProductId, options, progress, ct);
    }

    private async Task<List<ArchiveLocation>> ScanAsync(ArchiveOptions options, CancellationToken ct)
    {
        ProviderOutput.Validate(options, _stores.Keys);
        if (options.Layout == ArchiveLayout.Flat)
            return await ArchiveCatalog.ScanAsync(options.OutputRoot, ct, _stores.Keys.ToHashSet());
        var result = new List<ArchiveLocation>();
        foreach (var source in _stores.Keys)
        {
            var resolved = ProviderOutput.Resolve(options, source);
            result.AddRange(await ArchiveCatalog.ScanAsync(resolved.OutputRoot, ct, new HashSet<string> { source }));
        }
        return result;
    }

    public async Task<IReadOnlyList<ArchiveDifference>> DiffAsync(ArchiveLibrary library, ArchiveOptions options,
        bool refreshVersions = false, CancellationToken ct = default)
    {
        options.Validate();
        var catalog = await ScanAsync(options, ct);
        var rows = new List<ArchiveDifference>();
        foreach (var game in library.Games.Where(g => _stores.ContainsKey(g.Key.Source) && g.CanBackUp(options.Platform)))
        {
            ct.ThrowIfCancellationRequested();
            if (!game.Versions.TryGetValue(options.Platform, out var expected)) continue;
            if (!refreshVersions && (library.Failures.ContainsKey(game.Key.Source) || library.Failures.ContainsKey(game.Key.ToString())))
            {
                rows.Add(new(game, options.Platform, ArchiveStatus.Unknown, "library refresh failed; previous metadata was retained"));
                continue;
            }
            try
            {
                if (refreshVersions) expected = await GetVersionAsync(game.Key, options, ct);
                var locations = catalog.Where(c => c.Metadata.Key == game.Key).ToList();
                if (locations.Count > 1) throw new ArchiveConflictException($"Multiple directories belong to {game.Key}.");
                var location = locations.SingleOrDefault();
                var backup = location?.Metadata.Platforms.GetValueOrDefault(options.Platform);
                ArchiveStatus state; string detail;
                if (backup is null)
                {
                    _ = await ArchiveCatalog.ResolveNameAsync(game, ProviderOutput.Resolve(options, game.Key.Source), ct);
                    state = ArchiveStatus.Missing; detail = "not backed up";
                }
                else if (!backup.HasArchive(location!.Directory)) { state = ArchiveStatus.Missing; detail = "archive missing or size changed"; }
                else
                {
                    var steam = game.Key.Source == "steam";
                    if (!refreshVersions && (expected.BuildId.Length == 0 || expected.Branch != options.Branch
                        || (expected.BuildVariesByLanguage || steam) && !string.Equals(expected.Language, options.Language, StringComparison.OrdinalIgnoreCase)
                        || steam && (expected.Manifests.Count == 0 || expected.Architecture != options.Architecture)))
                    { state = ArchiveStatus.Unknown; detail = "run diff --refresh for the selected branch/language/architecture"; }
                    else
                    {
                        var matches = expected.BuildId == backup.Version.BuildId && expected.ContentRevision == backup.Version.ContentRevision
                            && options.Branch == backup.Version.Branch
                            && string.Equals(options.Language, backup.Version.Language, StringComparison.OrdinalIgnoreCase)
                            && (expected.Architecture.Length == 0 || options.Architecture == backup.Version.Architecture)
                            && expected.Dlcs.Order().SequenceEqual(backup.Version.Dlcs.Order())
                            && (!(refreshVersions || steam) || expected.Matches(backup.Version)) && !backup.NeedsRefresh;
                        state = matches ? ArchiveStatus.Current : ArchiveStatus.Outdated;
                        detail = matches ? backup.ArchiveName : "build, selection or content revision changed";
                    }
                }
                rows.Add(new(game, options.Platform, state, detail));
            }
            catch (ArchiveConflictException e) { rows.Add(new(game, options.Platform, ArchiveStatus.Conflict, e.Message)); }
            catch (Exception e) when (e is not OperationCanceledException) { rows.Add(new(game, options.Platform, ArchiveStatus.Unavailable, e.Message)); }
        }
        return rows;
    }

    public Task<IReadOnlyList<ArchiveLocation>> FindOrphansAsync(ArchiveLibrary library, string outputRoot, CancellationToken ct = default) =>
        FindOrphansAsync(library, new ArchiveOptions { OutputRoot = outputRoot }, ct);

    public async Task<IReadOnlyList<ArchiveLocation>> FindOrphansAsync(ArchiveLibrary library, ArchiveOptions options, CancellationToken ct = default)
    {
        var known = library.Games.Select(g => g.Key).ToHashSet();
        return (await ScanAsync(options, ct))
            .Where(l => _stores.ContainsKey(l.Metadata.Source) && !known.Contains(l.Metadata.Key)).ToList();
    }

    public Task<IReadOnlyList<VerificationResult>> VerifyAsync(string outputRoot, bool deep = false,
        bool fix = false, string search = "", CancellationToken ct = default, GameOs? platform = null) =>
        VerifyAsync(new ArchiveOptions { OutputRoot = outputRoot }, deep, fix, search, ct, platform);

    public async Task<IReadOnlyList<VerificationResult>> VerifyAsync(ArchiveOptions options, bool deep = false,
        bool fix = false, string search = "", CancellationToken ct = default, GameOs? platform = null)
    {
        var results = new List<VerificationResult>();
        foreach (var found in await ScanAsync(options, ct))
        {
            var location = found;
            if (!_stores.ContainsKey(location.Metadata.Source) || search.Length > 0
                && !location.Metadata.Slug.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !location.Metadata.Title.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            using var lease = fix ? await ArchiveCatalog.AcquireOwnedAsync(location, location.Metadata.Key, ct) : null;
            if (lease is not null) location = lease.Location;
            foreach (var (os, backup) in location.Metadata.Platforms.Where(p => platform is null || p.Key == platform))
            {
                var path = ArchivePaths.Within(location.Directory, backup.ArchiveName);
                if (!File.Exists(path)) { results.Add(new(location.Metadata.Key, os, path, [new("", "archive missing")], Missing: true)); continue; }
                var repaired = false;
                List<ArchiveProblem> problems;
                try
                {
                    problems = ArchiveCheck.Headers(path);
                    if (fix && problems.Count > 0 && problems.All(p => p.Repairable))
                    { ArchiveRepair.Repair(path); repaired = true; problems = ArchiveCheck.Headers(path); }
                    if (deep && problems.Count == 0) problems = await ArchiveCheck.DeepAsync(path, ct: ct);
                    if (new FileInfo(path).Length != backup.ArchiveSize)
                        problems.Add(new("", "archive size differs from completion metadata"));
                }
                catch (Exception e) when (e is IOException or InvalidDataException or ZipFormatException or ArchiveRepairException)
                { problems = [new("", e.Message)]; }
                results.Add(new(location.Metadata.Key, os, path, problems, repaired && problems.Count == 0));
            }
        }
        return results;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsStores)
            foreach (var store in _stores.Values) await store.DisposeAsync();
    }
}
