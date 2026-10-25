using GameArchiver.Core;
using GogArchiver.Core.Api;
using GogArchiver.Core.Auth;
using GogArchiver.Core.Content;
using GogArchiver.Core.Download;
using GogArchiver.Core.Http;
using GogArchiver.Core.Library;

namespace GogArchiver.Core;

public sealed record GogClientOptions
{
    public string SnapshotPath { get; init; } = LibrarySnapshot.DefaultPath();
    public string? TokenPath { get; init; }
    public GogHttpOptions Http { get; init; } = new();
}

/// <summary>Standalone GOG API. No console interaction and no aggregator dependency.</summary>
public sealed class GogClient : IGameStore
{
    public string Source => "gog";
    public string LoginUrl => GogConstants.LoginUrl;
    private readonly GogClientOptions _options;
    private readonly GogApiClient _api;
    private readonly ContentClient _content;
    private readonly HttpClient _cdn;
    private readonly HttpClient? _authed;
    private readonly HttpClient? _authHttp;
    private readonly TokenProvider? _tokens;
    private readonly List<HttpClient> _owned = [];
    private LibrarySnapshot? _snapshot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GogClient(GogClientOptions? options = null)
    {
        _options = options ?? new();
        _authHttp = GogHttpFactory.Create(_options.Http);
        _tokens = new(new TokenStore(_options.TokenPath), new GogAuthClient(_authHttp));
        _authed = GogHttpFactory.Create(_options.Http, _tokens);
        _cdn = GogHttpFactory.Create(_options.Http);
        _owned.AddRange([_authHttp, _authed, _cdn]);
        _api = new(_authed);
        _content = new(_authed, _cdn, new(_options.Http.MaxBytesPerSecond));
    }

    /// <summary>Injected clients remain owned by the caller. Useful for embedding and deterministic tests.</summary>
    public GogClient(GogApiClient api, ContentClient content, HttpClient cdn,
        LibrarySnapshot snapshot, GogClientOptions? options = null)
    {
        _api = api; _content = content; _cdn = cdn; _snapshot = snapshot; _options = options ?? new();
    }

    public async Task LoginAsync(string codeOrUrl, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_authHttp is null) throw new InvalidOperationException("Authentication belongs to the injected GOG client.");
            var code = GogAuthClient.ExtractCode(codeOrUrl);
            if (string.IsNullOrEmpty(code)) throw new ArgumentException("Missing authorization code.", nameof(codeOrUrl));
            var tokens = await new GogAuthClient(_authHttp).ExchangeCodeAsync(code, ct);
            await _tokens!.SetTokensAsync(tokens, ct);
            _snapshot = null;
        }
        finally { _gate.Release(); }
    }

    public Task<GogTokens?> GetAuthenticationAsync(CancellationToken ct = default) => new TokenStore(_options.TokenPath).LoadAsync(ct);
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_tokens is null) throw new InvalidOperationException("Authentication belongs to the injected GOG client.");
            await _tokens.SetTokensAsync(null, ct);
            _snapshot = null;
        }
        finally { _gate.Release(); }
    }

    public async Task<LibrarySnapshot> SyncGogLibraryAsync(IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await SyncCoreAsync(progress, ct); }
        finally { _gate.Release(); }
    }

    private async Task<LibrarySnapshot> SyncCoreAsync(IProgress<(int done, int total)>? progress, CancellationToken ct)
    {
        var snapshot = await new LibrarySync(_api, _content, _options.Http.MaxConnections).RunAsync(progress, ct);
        if (snapshot.Failures.Count > 0)
        {
            var previous = _snapshot ?? await LibrarySnapshot.LoadAsync(_options.SnapshotPath, ct);
            var retained = previous?.Games.Where(g => snapshot.Failures.ContainsKey(g.Id)
                && snapshot.Games.All(current => current.Id != g.Id)) ?? [];
            snapshot = snapshot with { Games = snapshot.Games.Concat(retained).OrderBy(g => g.Title).ToList() };
        }
        await snapshot.SaveAsync(_options.SnapshotPath, ct);
        _snapshot = snapshot;
        return snapshot;
    }

    public async Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default)
    {
        var snapshot = await SyncGogLibraryAsync(ct: ct);
        return new(ConvertLibrary(snapshot)) { Failures = snapshot.Failures.ToDictionary(f => f.Key.ToString(), f => f.Value) };
    }

    public static IReadOnlyList<GameInfo> ConvertLibrary(LibrarySnapshot snapshot) => snapshot.Games.Select(g => new GameInfo(
        new("gog", g.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), g.Title, g.Slug)
    {
        EarlyAccess = g.EarlyAccess,
        Kind = g.IsPack ? snapshot.PackDuplicates(g).Count > 0 ? "container" : "pack" : snapshot.Games.Any(parent => parent.Id != g.Id
            && parent.Platforms.Keys.Any(p => parent.DlcsFor(p).Any(d => d.ProductId == g.Id.ToString()))) ? "dlc" : "game",
        OwnedDlcs = g.Dlcs.Select(d => d.ProductId).ToList(),
        BackupPlatforms = snapshot.PackDuplicates(g).Count > 0 ? [] : g.Platforms
            .Where(p => p.Value.CanBackUp && snapshot.DlcParents(g, p.Key).Count == 0).Select(p => (GameOs)p.Key).ToHashSet(),
        Versions = g.Platforms.Where(p => p.Value.CanBackUp).ToDictionary(p => (GameOs)p.Key,
            p => Version(g, (GameOs)p.Key, "en")),
    }).ToList();

    private async Task<LibrarySnapshot> SnapshotAsync(CancellationToken ct) =>
        _snapshot ??= await LibrarySnapshot.LoadAsync(_options.SnapshotPath, ct) ?? await SyncCoreAsync(null, ct);

    private async Task<(LibrarySnapshot Snapshot, LibraryEntry Game)> ResolveAsync(string productId, GameOs platform, CancellationToken ct)
    {
        var snapshot = await SnapshotAsync(ct);
        var game = snapshot.Games.SingleOrDefault(g => g.Id.ToString() == productId)
            ?? throw new KeyNotFoundException($"GOG {productId} is not in the library. Sync first.");
        var parents = snapshot.DlcParents(game, (GamePlatform)platform);
        if (parents.Count == 1) game = parents[0];
        else if (parents.Count > 1) throw new InvalidOperationException($"DLC {productId} has multiple base games. Select a base game explicitly.");
        return (snapshot, game);
    }

    private static GameVersion Version(LibraryEntry game, GameOs platform, string language)
    {
        var availability = game.Platforms.GetValueOrDefault((GamePlatform)platform)
            ?? throw new InvalidOperationException($"{game.Title} is not available for {platform}.");
        var installer = availability.InstallerFor(language);
        return new(availability.Depot?.BuildId ?? installer?.BuildId ?? "")
        {
            Name = availability.Depot?.VersionName ?? installer?.Version, Language = language, Architecture = "",
            BuildVariesByLanguage = availability.Depot is null,
            Dlcs = game.DlcsFor((GamePlatform)platform).Select(d => d.ProductId).ToList(),
            DownloadSize = availability.DownloadSize, InstalledSize = availability.InstalledSize,
        };
    }

    public async Task<GameVersion> GetVersionAsync(string productId, GameSelection options, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await GetVersionCoreAsync(productId, options, ct); }
        finally { _gate.Release(); }
    }

    private async Task<GameVersion> GetVersionCoreAsync(string productId, GameSelection options, CancellationToken ct)
    {
        options.Validate();
        if (options.Branch != "public") throw new NotSupportedException("GOG does not support Steam branch selection.");
        var (snapshot, game) = await ResolveAsync(productId, options.Platform, ct);
        var owned = (await _api.GetOwnedGameIdsAsync(ct)).ToHashSet();
        if (!owned.Contains(game.Id)) throw new InvalidOperationException($"GOG {game.Id} is not in the owned library.");
        var current = await new LibrarySync(_api, _content, _options.Http.MaxConnections)
            .FetchAsync(game.Id, owned, ct, (GamePlatform)options.Platform, options.Language)
            ?? throw new InvalidOperationException($"GOG {game.Id} is no longer available.");
        var version = Version(current, options.Platform, options.Language);
        var platforms = game.Platforms.ToDictionary(p => p.Key,
            p => p.Value with { Dlcs = p.Value.Dlcs ?? game.DlcsFor(p.Key).ToList() });
        platforms[(GamePlatform)options.Platform] = current.Platforms[(GamePlatform)options.Platform];
        var refreshed = game with { Platforms = platforms,
            Dlcs = platforms.Values.SelectMany(p => p.Dlcs ?? []).DistinctBy(d => d.ProductId).ToList() };
        _snapshot = snapshot with { Games = snapshot.Games.Select(g => g.Id == game.Id ? refreshed : g).ToList() };
        return version;
    }

    public async Task<ArchiveResult> DownloadAsync(string productId, ArchiveOptions options,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await DownloadCoreAsync(productId, options, progress, ct); }
        finally { _gate.Release(); }
    }

    private async Task<ArchiveResult> DownloadCoreAsync(string productId, ArchiveOptions options,
        IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        options.Validate();
        if (options.Branch != "public") throw new NotSupportedException("GOG does not support Steam branch selection.");
        var (snapshot, game) = await ResolveAsync(productId, options.Platform, ct);
        var key = new GameKey(Source, game.Id.ToString());
        if (snapshot.PackDuplicates(game).Count > 0) return new(key, ArchiveOutcome.Unavailable, Detail: "Pack is covered by separately owned games.");
        options = await ProviderOutput.PrepareAsync(options, Source, ct);
        var name = await ArchiveCatalog.ResolveNameAsync(new(key, game.Title, game.Slug), options, ct);
        if (options.DirectoryName is null && string.Equals(name, game.DirectoryName, StringComparison.OrdinalIgnoreCase))
            BackupDirectory.ValidateSelection(snapshot.Games, [game]);
        var content = _authed is null ? _content : new ContentClient(_authed, _cdn, new(options.MaxBytesPerSecond));
        var result = await new GameDownloader(_api, content, _cdn).RunAsync(game, new()
        {
            OutputRoot = options.OutputRoot, Platform = (GamePlatform)options.Platform, Language = options.Language,
            StagingRoot = options.StagingRoot, StagingKey = game.LegacyDirectoryName, DirectoryName = name,
            MaxConnections = options.MaxConnections, Force = options.Force,
            Clean = options.Clean, Reuse = options.Reuse, VerifyArchive = options.Verify,
            WriteLauncher = options.WriteLauncher, MaxCompressionThreads = options.CompressionThreads,
        }, new InlineProgress<DownloadProgress>(p => progress?.Report(new(p.Phase, p.Bytes, p.TotalBytes, p.Files, p.TotalFiles))), ct);
        var outcome = result.Outcome switch
        {
            DownloadOutcome.Completed => ArchiveOutcome.Completed, DownloadOutcome.AlreadyDone => ArchiveOutcome.AlreadyDone,
            DownloadOutcome.NotEnoughSpace => ArchiveOutcome.NotEnoughSpace, DownloadOutcome.Incomplete => ArchiveOutcome.Incomplete,
            _ => ArchiveOutcome.Unavailable,
        };
        return new(key, outcome, result.ArchivePath, result.Detail);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var http in _owned) http.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
