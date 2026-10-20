using System.Collections.Concurrent;
using System.IO.Compression;
using GameArchiver.Core;
using SteamKit2;
using SteamKit2.Authentication;
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
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SteamUser.LoggedOnCallback> _loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SteamApps.LicenseListCallback> _licenses = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<uint, SteamApp> _appCache = new();
    private readonly ConcurrentDictionary<uint, Exception> _appFailures = new();
    private readonly AsyncLocal<ContentHttpContext?> _httpContext = new();
    private bool _connecting;
    private bool _authenticated;
    public bool Disconnected { get; private set; }
    private int _disposed;
    private SteamApps.LicenseListCallback? _currentLicenses;

    public SteamKitSession(SteamClientOptions options)
    {
        _options = options;
        _client = new(SteamConfiguration.Create(b => b.WithHttpClientFactory(_ =>
            new HttpClient(new SteamContentHttpHandler(_httpContext.Value)) { Timeout = options.OperationTimeout })));
        _user = _client.GetHandler<SteamUser>()!;
        _apps = _client.GetHandler<SteamApps>()!;
        _content = _client.GetHandler<SteamContent>()!;
        _callbacks = new(_client);
        _callbacks.Subscribe<KitClient.ConnectedCallback>(_ => _connected.TrySetResult());
        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(r => _loggedOn.TrySetResult(r));
        _callbacks.Subscribe<SteamApps.LicenseListCallback>(r => { _currentLicenses = r; _licenses.TrySetResult(r); });
        _callbacks.Subscribe<KitClient.DisconnectedCallback>(_ =>
        {
            Disconnected = true;
            _authenticated = false;
            Fail(new SteamAuthenticationException("Steam disconnected. Recreate the client to reconnect."));
        });
        _pump = Task.Run(() =>
        {
            try { while (!_stop.IsCancellationRequested) _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); }
            catch (Exception e) { Fail(e); }
        });
    }

    private void Fail(Exception error)
    {
        _connected.TrySetException(error); _loggedOn.TrySetException(error); _licenses.TrySetException(error);
    }
    private Task<T> WaitAsync<T>(Task<T> task, CancellationToken ct) => task.WaitAsync(_options.OperationTimeout, ct);

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (!_connecting) { _connecting = true; _client.Connect(); }
        await _connected.Task.WaitAsync(_options.OperationTimeout, ct);
    }

    public async Task LoginAsync(SteamCredentials? credentials, CancellationToken ct)
    {
        await _authGate.WaitAsync(ct);
        try
        {
            if (Disconnected) throw new SteamAuthenticationException("Steam disconnected; recreate the session to reconnect.");
            if (_authenticated) return;
            SteamTokens? tokens;
            try { tokens = await SteamTokenFile.LoadAsync(_options.TokenPath, ct); }
            catch (Exception e) when (credentials is not null && e is System.Text.Json.JsonException or InvalidDataException) { tokens = null; }
            if (credentials is null && tokens is null)
                throw new SteamAuthenticationException("Not logged into Steam. Run gamearchiver auth login --source steam.");
            await ConnectAsync(ct);
            if (credentials is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
                timeout.CancelAfter(_options.OperationTimeout);
                var session = await WaitAsync(_client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = credentials.Username, Password = credentials.Password, IsPersistentSession = true,
                    GuardData = tokens?.AccountName == credentials.Username ? tokens.GuardData : null,
                    Authenticator = new Authenticator(_options.Authenticate, timeout.Token),
                }), timeout.Token);
                var response = await session.PollingWaitForResultAsync(timeout.Token);
                tokens = new(response.AccountName, response.RefreshToken, response.NewGuardData);
            }
            if (tokens is null) throw new SteamAuthenticationException("Not logged into Steam. Run gamearchiver auth login --source steam.");
            _user.LogOn(new() { Username = tokens.AccountName, AccessToken = tokens.RefreshToken, ShouldRememberPassword = true });
            var result = await WaitAsync(_loggedOn.Task, ct);
            if (result.Result != EResult.OK) throw new SteamAuthenticationException($"Steam login failed: {result.Result}. Log in again.");
            _authenticated = true;
            if (credentials is not null) await SteamTokenFile.SaveAsync(_options.TokenPath, tokens, ct);
        }
        catch (SteamKit2.Authentication.AuthenticationException e)
        { throw new SteamAuthenticationException($"Steam rejected authentication: {e.Result}.", e); }
        catch (AsyncJobFailedException e)
        { throw new SteamAuthenticationException("Steam authentication connection failed. Retry the login.", e); }
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
                    if (app.AsUnsignedInteger() is > 0 and var appId) owned.Add(appId);
            }
            if (batch.Any(r => !received.Contains(r.ID))) throw new IOException("Steam omitted an owned package; library sync was not saved.");
        }
        _appCache.Clear();
        _appFailures.Clear();
        return owned;
    }

    public async Task PreloadAppsAsync(IReadOnlySet<uint> appIds, CancellationToken ct)
    {
        foreach (var batch in appIds.Chunk(100))
        {
            var tokens = await WaitAsync(_apps.PICSGetAccessTokens(batch, []).ToTask(), ct);
            var requests = batch.Select(id => new SteamApps.PICSRequest(id, tokens.AppTokens.GetValueOrDefault(id)));
            var result = await WaitAsync(_apps.PICSGetProductInfo(requests, []).ToTask(), ct);
            if (!result.Complete || result.Failed || result.Results is null)
                throw new IOException("Steam returned incomplete bulk appinfo.");
            foreach (var (id, product) in result.Results.SelectMany(r => r.Apps))
            {
                try { _appCache[id] = await DecodeProductAsync(id, product, ct); }
                catch (Exception e) when (e is not OperationCanceledException) { _appFailures[id] = e; }
            }
            foreach (var id in batch)
                if (!_appCache.ContainsKey(id) && !_appFailures.ContainsKey(id))
                    _appFailures[id] = new IOException($"Steam did not return appinfo for {id}.");
        }
    }

    public async Task<SteamApp> GetAppAsync(uint appId, CancellationToken ct)
    {
        await LoginAsync(null, ct);
        if (_appCache.TryGetValue(appId, out var cached)) return cached;
        if (_appFailures.TryGetValue(appId, out var failure)) throw new IOException($"Cannot read Steam app {appId}: {failure.Message}", failure);
        var tokens = await WaitAsync(_apps.PICSGetAccessTokens(appId, null).ToTask(), ct);
        var request = new SteamApps.PICSRequest(appId, tokens.AppTokens.GetValueOrDefault(appId));
        var result = await WaitAsync(_apps.PICSGetProductInfo(request, null).ToTask(), ct);
        if (!result.Complete || result.Failed || result.Results is null) throw new IOException($"Incomplete Steam appinfo for {appId}.");
        var product = result.Results.SelectMany(r => r.Apps).Where(p => p.Key == appId).Select(p => p.Value).SingleOrDefault()
            ?? throw new IOException($"Steam did not return appinfo for {appId}.");
        return _appCache[appId] = await DecodeProductAsync(appId, product, ct);
    }

    private async Task<SteamApp> DecodeProductAsync(uint appId, SteamApps.PICSProductInfoCallback.PICSProductInfo product, CancellationToken ct)
    {
        if (product.MissingToken) throw new SteamAuthenticationException($"Missing Steam access token for app {appId}.");
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

    private sealed class Authenticator(Func<SteamChallenge, CancellationToken, Task<string>>? prompt, CancellationToken ct) : IAuthenticator
    {
        private Task<string> Ask(SteamChallenge challenge) => prompt is null
            ? throw new SteamAuthenticationException($"Steam requires {challenge.Kind}; supply SteamClientOptions.Authenticate.")
            : prompt(challenge, ct).WaitAsync(ct);
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
