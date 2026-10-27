using System.Globalization;
using System.Net;
using System.Text.Json;
using GameArchiver.Core;

namespace GameArchiver;

public sealed partial class PcGamingWikiDrmSource
{
    private const string ApiUrl = "https://www.pcgamingwiki.com/w/api.php";
    private readonly PcGamingWikiSessionStore? _sessions;
    private CookieContainer _cookies = new();
    private PcGamingWikiSession? _verifiedSession;
    private bool _sessionLoaded, _authenticated;

    /// <summary>Authenticates a bot password, verifies the session, then saves cookies when a session path was supplied.</summary>
    public async Task LoginAsync(PcGamingWikiCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.Username) || !credentials.Username.Contains('@')
            || credentials.Username.StartsWith('@') || credentials.Username.EndsWith('@') || credentials.Username.Any(char.IsControl)
            || credentials.Username.Length > 256 || string.IsNullOrWhiteSpace(credentials.BotPassword))
            throw new ArgumentException("Use the bot username (Account@BotName) and bot password from PCGamingWiki Special:BotPasswords, not your normal account password.");
        await _gate.WaitAsync(ct);
        try
        {
            _authenticated = _sessionLoaded = false;
            _verifiedSession = null;
            _cookies = new();
            using var token = await ApiAsync("action=query&meta=tokens&type=login", ct);
            if (!TryProperty(token.RootElement, "query", out var query) || !TryProperty(query, "tokens", out var tokens)
                || !TryProperty(tokens, "logintoken", out var raw) || raw.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(raw.GetString()) || raw.GetString()!.Length > 4096)
                throw new InvalidDataException("PCGamingWiki did not return a login token.");
            using var login = await ApiAsync("", ct, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["action"] = "login", ["format"] = "json", ["lgname"] = credentials.Username,
                ["lgpassword"] = credentials.BotPassword, ["lgtoken"] = raw.GetString()!,
            }));
            if (!TryProperty(login.RootElement, "login", out var result) || !TryProperty(result, "result", out var status)
                || status.ValueKind != JsonValueKind.String) throw new InvalidDataException("PCGamingWiki returned an invalid login response.");
            if (status.GetString() != "Success")
                throw new PcGamingWikiAuthenticationException(status.GetString() == "Throttled"
                    ? "PCGamingWiki temporarily throttled login attempts. Wait before trying again; no automatic retry was attempted."
                    : "PCGamingWiki rejected the login. Use the exact Account@BotName and bot password from Special:BotPasswords; check that it has not been revoked.");
            if (!TryProperty(result, "lguserid", out var loginId) || loginId.ValueKind != JsonValueKind.Number
                || !loginId.TryGetInt64(out var expectedId) || expectedId <= 0)
                throw new InvalidDataException("PCGamingWiki did not identify the logged-in account.");
            var identity = await ReadIdentityAsync(ct);
            if (identity.Id != expectedId) throw new PcGamingWikiAuthenticationException("PCGamingWiki login and session identities do not match.");
            await SaveSessionAsync(identity.Id, identity.Name, ct);
            _authenticated = _sessionLoaded = true;
        }
        catch { _cookies = new(); _verifiedSession = null; _authenticated = _sessionLoaded = false; throw; }
        finally { _gate.Release(); }
    }

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_sessionLoaded) return;
        var session = _sessions is null ? null : await _sessions.LoadAsync(ct);
        if (session is not null)
        {
            _cookies = new();
            foreach (var cookie in session.Cookies)
            {
                var value = new Cookie(cookie.Name, cookie.Value, cookie.Path) { Secure = true, HttpOnly = true };
                if (cookie.Expires is { } expires) value.Expires = expires;
                _cookies.Add(new Uri(ApiUrl), value);
            }
            var identity = await ReadIdentityAsync(ct);
            if (identity.Id != session.UserId || identity.Name != session.Username)
                throw new PcGamingWikiAuthenticationException("PCGamingWiki session identity changed. Run auth login --source pcgamingwiki again.");
            _verifiedSession = session;
            await SaveSessionAsync(identity.Id, identity.Name, ct);
            _authenticated = true;
        }
        _sessionLoaded = true;
    }

    private async Task SaveSessionAsync(long userId, string username, CancellationToken ct)
    {
        var current = new PcGamingWikiSession(username, userId, _cookies.GetCookies(new Uri(ApiUrl)).Cast<Cookie>()
            .Where(c => !c.Expired).Select(c => new PcGamingWikiSessionCookie(c.Name, c.Value, c.Path,
                c.Expires == DateTime.MinValue ? null : c.Expires.ToUniversalTime())).ToList());
        PcGamingWikiSessionStore.Validate(current);
        static IEnumerable<PcGamingWikiSessionCookie> Ordered(PcGamingWikiSession session) => session.Cookies
            .OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Path, StringComparer.Ordinal);
        if (_sessions is not null && (_verifiedSession is null || _verifiedSession.UserId != userId
            || _verifiedSession.Username != username || !Ordered(_verifiedSession).SequenceEqual(Ordered(current))))
            await _sessions.SaveAsync(current, ct);
        _verifiedSession = current;
    }

    private Task SaveRefreshedSessionAsync(CancellationToken ct)
    {
        var session = _verifiedSession ?? throw new InvalidOperationException("PCGamingWiki session was not verified.");
        return SaveSessionAsync(session.UserId, session.Username, ct);
    }

    private async Task<(long Id, string Name)> ReadIdentityAsync(CancellationToken ct)
    {
        using var json = await ApiAsync("action=query&meta=userinfo&assert=user", ct);
        if (!TryProperty(json.RootElement, "query", out var query) || !TryProperty(query, "userinfo", out var user)
            || TryProperty(user, "anon", out _) || !TryProperty(user, "id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number <= 0
            || !TryProperty(user, "name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
            throw new PcGamingWikiAuthenticationException("PCGamingWiki did not confirm an authenticated session. Run auth login --source pcgamingwiki again.");
        return (number, name.GetString()!);
    }

    private async Task<IReadOnlyDictionary<GameOs, DrmInfo>> GetAuthenticatedAsync(uint id, CancellationToken ct)
    {
        // Query the exact Steam AppID directly through the authenticated API. Never visit the
        // anonymous PHP redirector or select an approximate title from search results.
        var where = Uri.EscapeDataString($"Game.Steam_AppID HOLDS \"{id.ToString(CultureInfo.InvariantCulture)}\"");
        using var lookup = await ApiAsync("action=cargoquery&tables=Game&fields=Game._pageID=PageID&limit=2&where=" + where + "&assert=user", ct);
        if (!TryProperty(lookup.RootElement, "cargoquery", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("PCGamingWiki did not return an AppID lookup result.");
        if (rows.GetArrayLength() != 1)
        {
            await SaveRefreshedSessionAsync(ct);
            return Unknown("No unique PCGamingWiki page for this Steam AppID.");
        }
        if (!TryProperty(rows[0], "title", out var fields) || !TryProperty(fields, "PageID", out var page)
            || !long.TryParse(page.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var pageId) || pageId <= 0)
            throw new InvalidDataException("PCGamingWiki returned an invalid page ID.");
        // Persist each validated authenticated response before the next request. A later
        // lookup failure/cancellation must not discard a session rotation already confirmed.
        await SaveRefreshedSessionAsync(ct);
        using var json = await ApiAsync($"action=parse&prop=wikitext&redirects=1&pageid={pageId.ToString(CultureInfo.InvariantCulture)}&assert=user", ct);
        if (!TryProperty(json.RootElement, "parse", out var parsed) || !TryProperty(parsed, "wikitext", out var text)
            || !TryProperty(text, "*", out var raw) || raw.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("PCGamingWiki did not return page wikitext.");
        await SaveRefreshedSessionAsync(ct);
        return Parse(raw.GetString()!, id, "https://www.pcgamingwiki.com/w/index.php?curid=" + pageId.ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow);
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private async Task<JsonDocument> ApiAsync(string query, CancellationToken ct, HttpContent? content = null)
    {
        using var response = await SendAsync(ApiUrl + (content is null ? "?format=json&" + query : ""), ct, content);
        var body = await ReadBoundedAsync(response.Content, ct);
        if (body.TrimStart().StartsWith('<')) throw new HttpRequestException(
            "PCGamingWiki returned an HTML access/login page instead of API data. Check its access protection; no credentials were forwarded.");
        JsonDocument json;
        try { json = JsonDocument.Parse(body); }
        catch (JsonException) { throw new InvalidDataException("PCGamingWiki returned invalid API JSON."); }
        try
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("PCGamingWiki returned invalid API data.");
            if (TryProperty(json.RootElement, "error", out var error))
            {
                var code = error.ValueKind == JsonValueKind.Object && TryProperty(error, "code", out var value)
                    && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                // Do not echo server-supplied messages: login errors can contain submitted data.
                throw code switch
                {
                    "assertuserfailed" or "notloggedin" => new PcGamingWikiAuthenticationException(
                        "PCGamingWiki session expired or was rejected. Run auth login --source pcgamingwiki again."),
                    "permissiondenied" or "readapidenied" => new PcGamingWikiAuthenticationException(
                        "PCGamingWiki denied API permissions. Check the bot password's grants, including 'Create, query and delete data through the Cargo extension' for AppID lookups."),
                    _ => new InvalidDataException("PCGamingWiki returned an API error. Check API access and bot-password permissions."),
                };
            }
            return json;
        }
        catch { json.Dispose(); throw; }
    }
}
