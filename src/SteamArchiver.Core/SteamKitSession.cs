using System.Collections.Concurrent;
using System.IO.Compression;
using GameArchiver.Core;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using CdnClient = SteamKit2.CDN.Client;
using CdnServer = SteamKit2.CDN.Server;
using KitClient = SteamKit2.SteamClient;

namespace SteamArchiver.Core;

/// <summary>SteamKit2 transport: authenticated CM session, licenses, PICS and SteamPipe CDN.</summary>
internal sealed class SteamKitSession : ISteamSession
{
    private readonly SteamClientOptions _options;
    private readonly KitClient _client;
    private readonly SteamUser _user;
    private readonly SteamApps _apps;
    private readonly SteamContent _content;
    private readonly CallbackManager _callbacks;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SteamUser.LoggedOnCallback> _loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SteamApps.LicenseListCallback> _licenses = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SteamAppInfoCache _appInfo;
    private readonly ConcurrentDictionary<uint, string> _appNames = new();
    private readonly AsyncLocal<ContentHttpContext?> _httpContext = new();
    private volatile bool _connectionEstablished;
    private volatile string _phase = "connecting to Steam";
    private SteamAuthenticationException? _disconnectError;
    private bool _authenticated;
    private volatile bool _disconnected;
    public bool Disconnected => _disconnected;
    private int _disposed;
    private SteamApps.LicenseListCallback? _currentLicenses;

    public SteamKitSession(SteamClientOptions options, Action<ISteamConfigurationBuilder>? configure = null)
    {
        _options = options;
        _client = new(SteamConfiguration.Create(b =>
        {
            b.WithHttpClientFactory(purpose => CreateHttpClient(purpose, options.OperationTimeout, _httpContext.Value));
            configure?.Invoke(b);
        }));
        _user = _client.GetHandler<SteamUser>()!;
        _apps = _client.GetHandler<SteamApps>()!;
        _content = _client.GetHandler<SteamContent>()!;
        _appInfo = new(FetchAppInfoAsync);
        _callbacks = new(_client);
        _callbacks.Subscribe<KitClient.ConnectedCallback>(_ =>
        {
            if (_stop.IsCancellationRequested || Disconnected) return;
            _phase = "starting authentication";
            _connectionEstablished = true;
            _connected.TrySetResult();
        });
        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(r => _loggedOn.TrySetResult(r));
        _callbacks.Subscribe<SteamApps.LicenseListCallback>(r => { _currentLicenses = r; _licenses.TrySetResult(r); });
        _callbacks.Subscribe<KitClient.DisconnectedCallback>(_ =>
        {
            if (_stop.IsCancellationRequested) return;
            var error = new SteamAuthenticationException(_connectionEstablished
                ? $"Steam disconnected while {_phase}. Retry the operation."
                : "Could not establish a Steam connection (before authentication).");
            if (!_connectionEstablished)
            {
                // SteamKit marks the failed CM endpoint bad; ConnectAsync can try another.
                // Do not poison the authentication/license tasks before authentication begins.
                _connected.TrySetException(error);
                return;
            }
            _authenticated = false;
            _disconnectError = error;
            _disconnected = true;
            Fail(error);
        });
        _pump = Task.Run(() =>
        {
            try { while (!_stop.IsCancellationRequested) _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); }
            catch (Exception e)
            {
                _authenticated = false;
                _disconnectError = new SteamAuthenticationException($"Steam callback processing failed while {_phase}.", e);
                _disconnected = true;
                Fail(_disconnectError);
            }
        });
    }

    internal static HttpClient CreateHttpClient(HttpClientPurpose purpose, TimeSpan timeout,
        ContentHttpContext? context = null, HttpMessageHandler? transport = null)
    {
        // ClientWebSocket reads the upgraded response synchronously and requires a duplex
        // stream. The CDN wrapper supports async, read-only bodies and must never wrap it.
        HttpMessageHandler handler = purpose == HttpClientPurpose.CMWebSocket
            ? transport ?? new SocketsHttpHandler() : new SteamContentHttpHandler(context, transport);
        return new HttpClient(handler) { Timeout = timeout };
    }

    private void Fail(Exception error)
    {
        _connected.TrySetException(error); _loggedOn.TrySetException(error); _licenses.TrySetException(error);
    }
    private Task<T> WaitAsync<T>(Task<T> task, CancellationToken ct) => task.WaitAsync(_options.OperationTimeout, ct);

    internal async Task ConnectAsync(CancellationToken ct)
    {
        if (Disconnected) throw _disconnectError ?? new SteamAuthenticationException("Steam session is disconnected. Retry the operation.");
        if (_connectionEstablished) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        timeout.CancelAfter(_options.OperationTimeout);
        SteamAuthenticationException? last = null;
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (Disconnected) throw _disconnectError!;
                _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _client.Connect();
                try
                {
                    await _connected.Task.WaitAsync(timeout.Token);
                    if (Disconnected) throw _disconnectError!;
                    return;
                }
                catch (SteamAuthenticationException e) when (!_connectionEstablished && !Disconnected)
                {
                    last = e;
                    if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), timeout.Token);
                }
            }
            throw new SteamAuthenticationException("Could not connect to Steam after 3 attempts, before authentication. "
                + "Check the network, proxy/firewall and Steam availability, then retry.", last);
        }
        catch (OperationCanceledException e)
        {
            var cancelled = ct.IsCancellationRequested || _stop.IsCancellationRequested;
            _disconnectError = new SteamAuthenticationException(cancelled
                ? "Steam connection was cancelled. Retry the operation."
                : "Timed out connecting to Steam, before authentication. Retry the operation.", e);
            _disconnected = true;
            _client.Disconnect();
            Fail(_disconnectError);
            if (cancelled) throw;
            throw _disconnectError;
        }
        catch (SteamAuthenticationException e)
        {
            _disconnectError = e;
            _disconnected = true;
            Fail(e);
            _client.Disconnect();
            throw;
        }
    }

    public async Task LoginAsync(SteamCredentials? credentials, CancellationToken ct)
    {
        await _authGate.WaitAsync(ct);
        try
        {
            if (Disconnected) throw _disconnectError ?? new SteamAuthenticationException("Steam session is disconnected. Retry the operation.");
            if (_authenticated) return;
            SteamTokens? tokens;
            try { tokens = await SteamTokenFile.LoadAsync(_options.TokenPath, ct); }
            catch (Exception e) when (credentials is not null && e is System.Text.Json.JsonException or InvalidDataException) { tokens = null; }
            if (credentials is null && tokens is null)
                throw new SteamAuthenticationException("Not logged into Steam. Run gamearchiver auth login --source steam.");
            await ConnectAsync(ct);
            if (credentials is not null)
            {
                _phase = "authenticating credentials";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
                timeout.CancelAfter(_options.OperationTimeout);
                var session = await WaitAsync(_client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = credentials.Username, Password = credentials.Password, IsPersistentSession = true,
                    GuardData = tokens?.AccountName == credentials.Username ? tokens.GuardData : null,
                    Authenticator = new Authenticator(_options.Authenticate, timeout.Token,
                        challenge => _phase = $"waiting for Steam Guard ({challenge.Kind})"),
                }), timeout.Token);
                var response = await session.PollingWaitForResultAsync(timeout.Token);
                tokens = new(response.AccountName, response.RefreshToken, response.NewGuardData);
            }
            if (tokens is null) throw new SteamAuthenticationException("Not logged into Steam. Run gamearchiver auth login --source steam.");
            _phase = "logging on with the Steam session token";
            _user.LogOn(new() { Username = tokens.AccountName, AccessToken = tokens.RefreshToken, ShouldRememberPassword = true });
            var result = await WaitAsync(_loggedOn.Task, ct);
            if (result.Result != EResult.OK) throw new SteamAuthenticationException($"Steam login failed: {result.Result}. Log in again.");
            if (Disconnected) throw _disconnectError!;
            _authenticated = true;
            _phase = "using the authenticated session";
            if (credentials is not null) await SteamTokenFile.SaveAsync(_options.TokenPath, tokens, ct);
        }
        catch (SteamKit2.Authentication.AuthenticationException e)
        { throw new SteamAuthenticationException($"Steam rejected authentication: {e.Result}.", e); }
        catch (AsyncJobFailedException e)
        { throw _disconnectError ?? new SteamAuthenticationException($"Steam connection failed while {_phase}. Retry the operation.", e); }
        finally { _authGate.Release(); }
    }

    public async Task<IReadOnlySet<uint>> GetOwnedAppIdsAsync(CancellationToken ct)
    {
        await LoginAsync(null, ct);
        var licenses = _currentLicenses ?? await WaitAsync(_licenses.Task, ct);
        if (licenses.Result != EResult.OK) throw new IOException($"Steam license request failed: {licenses.Result}");
        var invalid = ELicenseFlags.Expired | ELicenseFlags.CancelledByAdmin | ELicenseFlags.CancelledByUser
            | ELicenseFlags.CancelledByPartner | ELicenseFlags.CancelledByFriendlyFraudLock | ELicenseFlags.NotActivated;
        var requests = licenses.LicenseList.Where(l => l.PackageID > 0 && (l.LicenseFlags & invalid) == 0)
            .Select(l => new SteamApps.PICSRequest(l.PackageID, l.AccessToken)).ToList();
        var owned = new HashSet<uint>();
        var packages = new Dictionary<uint, HashSet<uint>>();
        foreach (var batch in requests.Chunk(100))
        {
            var result = await WaitAsync(_apps.PICSGetProductInfo([], batch).ToTask(), ct);
            if (!result.Complete || result.Failed || result.Results is null)
                throw new IOException("Steam returned an incomplete package list.");
            var received = new HashSet<uint>();
            foreach (var callback in result.Results)
            foreach (var (id, package) in callback.Packages)
            {
                received.Add(id);
                foreach (var app in package.KeyValues["appids"].Children)
                    if (app.AsUnsignedInteger() is > 0 and var appId)
                    {
                        owned.Add(appId);
                        if (!packages.TryGetValue(appId, out var ids)) packages[appId] = ids = [];
                        ids.Add(id);
                    }
            }
            if (batch.Any(r => !received.Contains(r.ID))) throw new IOException("Steam omitted an owned package; library sync was not saved.");
        }
        _appInfo.Reset(packages.ToDictionary(p => p.Key, p => p.Value.Order().ToArray()));
        _appNames.Clear();
        return owned;
    }

    public Task PreloadAppsAsync(IReadOnlySet<uint> appIds, CancellationToken ct) => _appInfo.PreloadAsync(appIds, ct);

    public async Task<SteamApp> GetAppAsync(uint appId, CancellationToken ct)
    {
        await LoginAsync(null, ct);
        return await _appInfo.GetAsync(appId, ct);
    }

    private async Task<IReadOnlyDictionary<uint, SteamAppInfoResult>> FetchAppInfoAsync(IReadOnlyCollection<uint> appIds, CancellationToken ct)
    {
        var tokens = await WaitAsync(_apps.PICSGetAccessTokens(appIds, []).ToTask(), ct);
        var requests = appIds.Select(id => new SteamApps.PICSRequest(id, tokens.AppTokens.GetValueOrDefault(id)));
        var result = await WaitAsync(_apps.PICSGetProductInfo(requests, []).ToTask(), ct);
        if (!result.Complete || result.Failed || result.Results is null)
            throw new IOException("Steam returned incomplete appinfo.");
        var entries = new Dictionary<uint, SteamAppInfoResult>();
        foreach (var (id, product) in result.Results.SelectMany(r => r.Apps).Where(p => appIds.Contains(p.Key)))
        {
            if (product.KeyValues["common"]["name"].Value is { } name && !string.IsNullOrWhiteSpace(name)) _appNames[id] = name;
            try
            {
                if (product.MissingToken) throw new SteamAppInfoAccessException(id,
                    SteamAppInfoAccessException.Status(id, tokens.AppTokens, tokens.AppTokensDenied));
                var app = await DecodeProductAsync(id, product, ct);
                _appNames[id] = app.Title;
                entries[id] = new(app);
            }
            catch (Exception e) when (e is not OperationCanceledException) { entries[id] = new(Error: e); }
        }
        return entries;
    }

    public async Task<IReadOnlyDictionary<uint, string>> GetAppNamesAsync(IReadOnlySet<uint> appIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var names = _appNames.Where(p => appIds.Contains(p.Key)).ToDictionary();
        var missing = appIds.Except(names.Keys).ToArray();
        if (missing.Length == 0 || Disconnected || _client.SteamID is not { } account) return names;
        try
        {
            // This service may provide display names even when PICS appinfo is protected.
            // It never supplies ownership or manifests to the archive planner.
            var request = SteamOwnedGames.Request(account.ConvertToUInt64(), missing);
            var service = _client.GetHandler<SteamUnifiedMessages>()!.CreateService<Player>();
            var timeout = _options.OperationTimeout > TimeSpan.Zero && _options.OperationTimeout < TimeSpan.FromSeconds(10)
                ? _options.OperationTimeout : TimeSpan.FromSeconds(10);
            var result = await service.GetOwnedGames(request).ToTask().WaitAsync(timeout, ct);
            if (result.Result == EResult.OK)
                foreach (var game in result.Body.games)
                    if (game.appid > 0 && appIds.Contains((uint)game.appid) && !string.IsNullOrWhiteSpace(game.name))
                        names.TryAdd((uint)game.appid, game.name);
        }
        catch (Exception e) when (e is AsyncJobFailedException or TimeoutException or IOException
            || e is OperationCanceledException && !ct.IsCancellationRequested) { /* Display names are best effort. */ }
        return names;
    }

    public async Task<IReadOnlyDictionary<uint, string>> GetOwnedGamesAsync(CancellationToken ct)
    {
        await LoginAsync(null, ct);
        var account = _client.SteamID ?? throw new SteamAuthenticationException("Steam account is unavailable.");
        var service = _client.GetHandler<SteamUnifiedMessages>()!.CreateService<Player>();
        try
        {
            var result = await WaitAsync(service.GetOwnedGames(SteamOwnedGames.Request(account.ConvertToUInt64())).ToTask(), ct);
            ct.ThrowIfCancellationRequested();
            return SteamOwnedGames.Parse(result.Result, result.Body);
        }
        catch (AsyncJobFailedException e)
        { throw new IOException("Steam GetOwnedGames request failed; comparison was not performed.", e); }
    }

    private async Task<SteamApp> DecodeProductAsync(uint appId, SteamApps.PICSProductInfoCallback.PICSProductInfo product, CancellationToken ct)
    {
        var info = product.KeyValues;
        if (product.UseHttp && product.HttpUri is { } uri)
        {
            using var http = _client.Configuration.HttpClientFactory(HttpClientPurpose.WebAPI);
            var bytes = await http.GetByteArrayAsync(uri, ct);
            using var memory = new MemoryStream(bytes);
            using Stream data = bytes.Length > 2 && bytes[0] == 31 && bytes[1] == 139
                ? new GZipStream(memory, CompressionMode.Decompress) : memory;
            info = new();
            if (!info.ReadAsText(data)) throw new InvalidDataException($"Invalid Steam appinfo for {appId}.");
        }
        if (string.IsNullOrWhiteSpace(info["common"]["name"].Value)) throw new IOException($"Steam appinfo for {appId} has no name.");
        return SteamApp.Parse(appId, info);
    }

    public async Task<ISteamContentSession> OpenContentAsync(long maxBytesPerSecond, CancellationToken ct)
    {
        await LoginAsync(null, ct);
        var httpContext = new ContentHttpContext(ct, new(0));
        _httpContext.Value = httpContext;
        try
        {
            var servers = await WaitAsync(_content.GetServersForSteamPipe(maxNumServers: 20), ct);
            return new ContentConnection(this, new CdnClient(_client), servers.Where(s => !s.UseAsProxy).OrderBy(s => s.WeightedLoad).ToArray(),
                new SteamContentTransfer(maxBytesPerSecond, httpContext));
        }
        finally { _httpContext.Value = null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _client.Disconnect();
        await _pump;
        _authGate.Dispose(); _stop.Dispose();
    }

    private sealed class Authenticator(Func<SteamChallenge, CancellationToken, Task<string>>? prompt, CancellationToken ct,
        Action<SteamChallenge> onChallenge) : IAuthenticator
    {
        private Task<string> Ask(SteamChallenge challenge)
        {
            onChallenge(challenge);
            return prompt is null
                ? throw new SteamAuthenticationException($"Steam requires {challenge.Kind}; supply SteamClientOptions.Authenticate.")
                : prompt(challenge, ct).WaitAsync(ct);
        }
        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => Ask(new(SteamChallengeKind.DeviceCode, previousCodeWasIncorrect));
        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => Ask(new(SteamChallengeKind.EmailCode, previousCodeWasIncorrect, email));
        public async Task<bool> AcceptDeviceConfirmationAsync() => string.Equals(
            await Ask(new(SteamChallengeKind.DeviceConfirmation)), "approve", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ContentConnection(SteamKitSession owner, CdnClient cdn, CdnServer[] servers, SteamContentTransfer transfer) : ISteamContentSession
    {
        private readonly ConcurrentDictionary<(uint App, uint Depot), byte[]> _keys = new();
        private int _nextServer;
        private async Task<byte[]> KeyAsync(SteamDepotRequest depot, CancellationToken ct)
        {
            if (_keys.TryGetValue((depot.AppId, depot.DepotId), out var key)) return key;
            var result = await owner.WaitAsync(owner._apps.GetDepotDecryptionKey(depot.DepotId, depot.AppId).ToTask(), ct);
            if (result.Result != EResult.OK) throw new SteamAuthenticationException($"Depot {depot.DepotId} is not accessible: {result.Result}.");
            return _keys[(depot.AppId, depot.DepotId)] = result.DepotKey;
        }
        private async Task<T> FetchAsync<T>(SteamDepotRequest depot, Func<CdnServer, byte[], string?, Task<T>> fetch, CancellationToken ct)
        {
            if (servers.Length == 0) throw new IOException("Steam returned no content servers.");
            var key = await KeyAsync(depot, ct);
            Exception? last = null;
            for (var attempt = 0; attempt < Math.Max(3, servers.Length); attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var server = servers[(Interlocked.Increment(ref _nextServer) & int.MaxValue) % servers.Length];
                if (server.AllowedAppIds.Length > 0 && !server.AllowedAppIds.Contains(depot.AppId)) continue;
                try
                {
                    string? token = null;
                    try { return await fetch(server, key, null); }
                    catch (SteamKitWebRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
                    {
                        var auth = await owner.WaitAsync(owner._content.GetCDNAuthToken(depot.AppId, depot.DepotId, server.Host!), ct);
                        if (auth.Result != EResult.OK) throw new SteamAuthenticationException($"Steam CDN authorization failed: {auth.Result}.");
                        token = auth.Token;
                    }
                    return await fetch(server, key, token);
                }
                catch (Exception) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }
                catch (Exception e) when (e is IOException or HttpRequestException or SteamKitWebRequestException or InvalidDataException or TaskCanceledException)
                {
                    ct.ThrowIfCancellationRequested(); last = e;
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), ct);
                }
            }
            throw new IOException($"Steam could not serve depot {depot.DepotId} after retries.", last);
        }

        public async Task<SteamManifest> GetManifestAsync(SteamDepotRequest depot, CancellationToken ct)
        {
            var manifest = await FetchAsync(depot, async (server, key, token) =>
            {
                var code = await owner.WaitAsync(owner._content.GetManifestRequestCode(depot.DepotId, depot.AppId, depot.ManifestId, depot.Branch), ct);
                if (code == 0) throw new SteamAuthenticationException($"Steam denied manifest {depot.ManifestId} for depot {depot.DepotId}.");
                return await transfer.ManifestAsync(() => cdn.DownloadManifestAsync(depot.DepotId, depot.ManifestId, code, server, key, cdnAuthToken: token), ct);
            }, ct);
            if (manifest.FilenamesEncrypted || manifest.Files is null || manifest.DepotID != depot.DepotId || manifest.ManifestGID != depot.ManifestId)
                throw new InvalidDataException("Steam returned an encrypted or mismatched manifest.");
            return new(depot, manifest.Files.Select(f => new SteamFile(f.FileName, checked((long)f.TotalSize),
                Convert.ToHexStringLower(f.FileHash), f.Chunks.Select(c => new SteamChunk(Convert.ToHexStringLower(c.ChunkID!),
                    c.Checksum, checked((long)c.Offset), checked((int)c.CompressedLength), checked((int)c.UncompressedLength))).ToList())
            {
                IsDirectory = f.Flags.HasFlag(EDepotFileFlag.Directory),
                IsExecutable = f.Flags.HasFlag(EDepotFileFlag.Executable),
                LinkTarget = f.Flags.HasFlag(EDepotFileFlag.Symlink) ? f.LinkTarget ?? "" : null,
            }).ToList());
        }

        public Task<byte[]> GetChunkAsync(SteamDepotRequest depot, SteamChunk chunk, CancellationToken ct) => FetchAsync(depot, async (server, key, token) =>
        {
            if (chunk.Size is < 0 or > 64 * 1024 * 1024) throw new InvalidDataException("Invalid Steam chunk size.");
            var bytes = new byte[chunk.Size];
            var read = await transfer.ChunkAsync(chunk.CompressedSize, () => cdn.DownloadDepotChunkAsync(depot.DepotId, new DepotManifest.ChunkData(Convert.FromHexString(chunk.Id),
                chunk.Checksum, checked((ulong)chunk.Offset), checked((uint)chunk.CompressedSize), checked((uint)chunk.Size)),
                server, bytes, key, cdnAuthToken: token), ct);
            if (read != chunk.Size) throw new InvalidDataException("Steam chunk length mismatch.");
            return bytes;
        }, ct);
        public ValueTask DisposeAsync() { cdn.Dispose(); return ValueTask.CompletedTask; }
    }
}
