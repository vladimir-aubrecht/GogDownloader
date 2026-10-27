using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameArchiver.Core;

namespace GameArchiver;

/// <summary>Reads explicit Steam availability rows; missing or ambiguous evidence stays unknown.</summary>
public sealed partial class PcGamingWikiDrmSource : IDrmSource, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest;
    public string Name => "pcgamingwiki";
    /// <param name="http">Optional transport; its handler must disable automatic redirects and cookies.</param>
    /// <param name="sessionPath">Optional saved login session. Omit for anonymous, in-memory use.</param>
    public PcGamingWikiDrmSource(HttpClient? http = null, TimeSpan? minimumInterval = null, string? sessionPath = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }); _ownsHttp = http is null;
        _sessions = sessionPath is null ? null : new PcGamingWikiSessionStore(sessionPath);
        _interval = minimumInterval ?? TimeSpan.FromSeconds(1.1);
        if (_interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumInterval));
    }

    public async Task<IReadOnlyDictionary<GameOs, DrmInfo>> GetAsync(GameKey key, CancellationToken ct = default)
    {
        if (key.Source != "steam" || !uint.TryParse(key.ProductId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
            throw new ArgumentException("PCGamingWiki lookup requires a numeric Steam AppID.");
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureSessionAsync(ct);
            if (_authenticated) return await GetAuthenticatedAsync(id, ct);
            // The official AppID resolver redirects to a wiki page. A search/disambiguation page
            // is not an exact match and must never be resolved by guessing from a title.
            using var response = await ResolveAnonymousAsync(id, ct);
            var uri = response.RequestMessage?.RequestUri;
            if (uri is null || uri.Scheme != "https" || uri.Host != "www.pcgamingwiki.com" || !uri.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal))
                return Unknown("No unique PCGamingWiki page for this Steam AppID.");
            var title = Uri.UnescapeDataString(uri.AbsolutePath[6..]);
            using var json = await ApiAsync("action=parse&prop=wikitext&redirects=1&page=" + Uri.EscapeDataString(title), ct);
            if (!TryProperty(json.RootElement, "parse", out var parsed) || !TryProperty(parsed, "wikitext", out var text)
                || !TryProperty(text, "*", out var raw) || raw.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("PCGamingWiki did not return page wikitext.");
            return Parse(raw.GetString()!, id, uri.AbsoluteUri, DateTimeOffset.UtcNow);
        }
        finally { _gate.Release(); }
    }

    private async Task<HttpResponseMessage> ResolveAnonymousAsync(uint id, CancellationToken ct)
    {
        var uri = new Uri($"https://www.pcgamingwiki.com/api/appid.php?appid={id}");
        for (var hop = 0; hop < 5; hop++)
        {
            var response = await SendAsync(uri.AbsoluteUri, ct);
            if ((int)response.StatusCode is < 300 or >= 400) return response;
            using (response)
            {
                if (response.Headers.Location is not { } location) throw new InvalidDataException("PCGamingWiki returned a redirect without a destination.");
                uri = new Uri(uri, location);
                if (uri.Scheme != "https" || uri.Host != "www.pcgamingwiki.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
                    throw new InvalidDataException("PCGamingWiki redirected outside its HTTPS site.");
            }
        }
        throw new InvalidDataException("Too many PCGamingWiki redirects.");
    }

    private async Task<HttpResponseMessage> SendAsync(string uri, CancellationToken ct, HttpContent? content = null)
    {
        var delay = _lastRequest + _interval - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        _lastRequest = DateTimeOffset.UtcNow;
        using var request = new HttpRequestMessage(content is null ? HttpMethod.Get : HttpMethod.Post, uri) { Content = content };
        request.Headers.UserAgent.ParseAdd("GameArchiver/1.0 (+https://github.com/vladimir-aubrecht/gogdownloader)");
        var api = request.RequestUri!.GetLeftPart(UriPartial.Path) == ApiUrl;
        if (api && _cookies.GetCookieHeader(new Uri(ApiUrl)) is { Length: > 0 } cookies) request.Headers.Add("Cookie", cookies);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            try
            {
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var challenge = response.Headers.TryGetValues("cf-mitigated", out var values) && values.Contains("challenge");
                    var body = await ReadBoundedAsync(response.Content, ct);
                    challenge |= body.Contains("cloudflare", StringComparison.OrdinalIgnoreCase);
                    throw new HttpRequestException(challenge
                        ? "Cloudflare blocked access to PCGamingWiki (HTTP 403) before the API could respond. Login cannot bypass this block; contact PCGamingWiki about API access"
                        : "PCGamingWiki denied API access (HTTP 403). Check its API access requirements; this does not establish that the bot password is incorrect", null, response.StatusCode);
                }
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if (api) throw new HttpRequestException("PCGamingWiki redirected an API request. Login data was not forwarded; check API access", null, response.StatusCode);
                    return response;
                }
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new HttpRequestException("PCGamingWiki rate limit reached (HTTP 429). Wait before retrying; no automatic retry was attempted", null, response.StatusCode);
                if (api && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                    foreach (var cookie in setCookies)
                        try { _cookies.SetCookies(new Uri(ApiUrl), cookie); }
                        catch (CookieException) { throw new InvalidDataException("PCGamingWiki returned an invalid session cookie."); }
                if (!response.IsSuccessStatusCode) throw new HttpRequestException(
                    $"PCGamingWiki request failed (HTTP {(int)response.StatusCode})", null, response.StatusCode);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("PCGamingWiki request timed out."); }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await using var stream = await content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("PCGamingWiki response exceeds 2 MiB.");
                output.Write(buffer, 0, count);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("PCGamingWiki response body timed out."); }
    }

    private static Dictionary<GameOs, DrmInfo> Unknown(string note, string? url = null) => Enum.GetValues<GameOs>().ToDictionary(p => p,
        _ => new DrmInfo { Source = "pcgamingwiki", Url = url, CheckedAt = DateTimeOffset.UtcNow, Notes = note });

    public static IReadOnlyDictionary<GameOs, DrmInfo> Parse(string wikitext, uint appId, string pageUrl, DateTimeOffset checkedAt)
    {
        var rows = new Dictionary<GameOs, List<DrmStatus>>();
        // Ignore documentation examples and commented-out availability rows.
        wikitext = Regex.Replace(wikitext, @"<!--[\s\S]*?-->|<nowiki\b[^>]*>[\s\S]*?</nowiki>", "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (wikitext.Contains("<!--", StringComparison.Ordinal) || wikitext.Contains("<nowiki", StringComparison.OrdinalIgnoreCase))
            return Unknown("Unresolved comment/nowiki markup; classification requires manual review.", pageUrl);
        foreach (var template in Templates(wikitext))
        {
            var fields = Split(template);
            if (fields.Count < 4 || !fields[0].Equals("Availability/row", StringComparison.OrdinalIgnoreCase)
                || !fields[1].Equals("Steam", StringComparison.OrdinalIgnoreCase)) continue;
            var id = fields[2];
            if (Uri.TryCreate(id, UriKind.Absolute, out var link) && link.Host == "store.steampowered.com")
                id = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["app", var number, ..] ? number : "";
            if (id != appId.ToString(CultureInfo.InvariantCulture)) continue;
            var named = fields.Skip(4).Where(f => f.Contains('=')).Select(f => f.Split('=', 2)).ToList();
            var os = named.FirstOrDefault(f => f[0].Trim().Equals("os", StringComparison.OrdinalIgnoreCase))?.ElementAtOrDefault(1);
            // Unscoped PCGW PC availability must not imply that native Linux/macOS builds use
            // the same DRM. Only explicit OS qualifiers extend classification to those ports.
            var platforms = os is null ? new[] { GameOs.Windows } : os.Split([',', '/'], StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant() switch { "windows" => (GameOs?)GameOs.Windows, "linux" => GameOs.Linux, "mac" or "macos" or "os x" => GameOs.Mac, _ => null })
                .Where(p => p.HasValue).Select(p => p!.Value).Distinct().ToArray();
            var status = fields[3].ToLowerInvariant() switch
            {
                "drm-free" or "none" => DrmStatus.Free,
                "steam" or "denuvo" or "securom" or "safedisc" or "uplay" or "ubisoft connect" or "origin" or "ea app" or "games for windows live" or "rockstar games launcher" => DrmStatus.Protected,
                _ => DrmStatus.Unknown,
            };
            if (named.Count(f => f[0].Trim().Equals("os", StringComparison.OrdinalIgnoreCase)) > 1) status = DrmStatus.Unknown;
            // Notes may qualify DRM-free claims (first launch, executable replacement, edition).
            // Do not evaluate wiki templates or silently discard conditions.
            if (fields.Skip(4).Any(f => f.Length > 0 && (!f.Contains('=')
                || !f.Split('=', 2)[0].Trim().Equals("os", StringComparison.OrdinalIgnoreCase)
                    && f.Split('=', 2)[1].Trim().Length > 0))) status = DrmStatus.Unknown;
            foreach (var platform in platforms)
            {
                if (!rows.TryGetValue(platform, out var statuses)) rows[platform] = statuses = [];
                statuses.Add(status);
            }
        }
        return Enum.GetValues<GameOs>().ToDictionary(p => p, p => new DrmInfo
        {
            Status = rows.TryGetValue(p, out var matches) && matches.Distinct().Count() == 1 ? matches[0] : DrmStatus.Unknown,
            Source = "pcgamingwiki", Url = pageUrl, CheckedAt = checkedAt,
            Notes = "Explicit matching Steam availability row; absent, conditional, unrecognized or conflicting evidence is unknown. Community report, not a per-build test.",
        });
    }

    private static IEnumerable<string> Templates(string text)
    {
        var start = -1; var depth = 0;
        for (var i = 0; i + 1 < text.Length; i++)
            if (text[i] == '{' && text[i + 1] == '{') { if (depth++ == 0) start = i + 2; i++; }
            else if (text[i] == '}' && text[i + 1] == '}' && depth > 0)
            { if (--depth == 0) yield return text[start..i]; i++; }
    }

    private static List<string> Split(string text)
    {
        var result = new List<string>(); var start = 0; var braces = 0; var links = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (i + 1 < text.Length)
            {
                var pair = text.Substring(i, 2);
                if (pair == "{{") { braces++; i++; continue; }
                if (pair == "}}") { braces--; i++; continue; }
                if (pair == "[[") { links++; i++; continue; }
                if (pair == "]]") { links--; i++; continue; }
            }
            if (text[i] == '|' && braces == 0 && links == 0) { result.Add(text[start..i].Trim()); start = i + 1; }
        }
        result.Add(text[start..].Trim()); return result;
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); _gate.Dispose(); }
}
