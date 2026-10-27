using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameArchiver.Core;

namespace GameArchiver;

/// <summary>Reads explicit Steam availability rows; missing or ambiguous evidence stays unknown.</summary>
public sealed class PcGamingWikiDrmSource : IDrmSource, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest;
    public string Name => "pcgamingwiki";
    public PcGamingWikiDrmSource(HttpClient? http = null, TimeSpan? minimumInterval = null)
    {
        _http = http ?? new HttpClient(); _ownsHttp = http is null;
        _interval = minimumInterval ?? TimeSpan.FromSeconds(1);
        if (_interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumInterval));
    }

    public async Task<IReadOnlyDictionary<GameOs, DrmInfo>> GetAsync(GameKey key, CancellationToken ct = default)
    {
        if (key.Source != "steam" || !uint.TryParse(key.ProductId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
            throw new ArgumentException("PCGamingWiki lookup requires a numeric Steam AppID.");
        await _gate.WaitAsync(ct);
        try
        {
            // The official AppID resolver redirects to a wiki page. A search/disambiguation page
            // is not an exact match and must never be resolved by guessing from a title.
            using var response = await SendAsync($"https://www.pcgamingwiki.com/api/appid.php?appid={id}", ct);
            var uri = response.RequestMessage?.RequestUri;
            if (uri is null || uri.Scheme != "https" || uri.Host != "www.pcgamingwiki.com" || !uri.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal))
                return Unknown("No unique PCGamingWiki page for this Steam AppID.");
            var title = Uri.UnescapeDataString(uri.AbsolutePath[6..]);
            using var page = await SendAsync("https://www.pcgamingwiki.com/w/api.php?action=parse&format=json&prop=wikitext&redirects=1&page="
                + Uri.EscapeDataString(title), ct);
            using var json = JsonDocument.Parse(await ReadBoundedAsync(page.Content, ct));
            if (!json.RootElement.TryGetProperty("parse", out var parsed) || !parsed.TryGetProperty("wikitext", out var text)
                || !text.TryGetProperty("*", out var raw) || raw.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("PCGamingWiki did not return page wikitext.");
            return Parse(raw.GetString()!, id, uri.AbsoluteUri, DateTimeOffset.UtcNow);
        }
        finally { _gate.Release(); }
    }

    private async Task<HttpResponseMessage> SendAsync(string uri, CancellationToken ct)
    {
        var delay = _lastRequest + _interval - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        _lastRequest = DateTimeOffset.UtcNow;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("GameArchiver/1.0 (+https://github.com/vladimir-aubrecht/gogdownloader)");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            try
            {
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    throw new HttpRequestException("PCGamingWiki denied automated access (HTTP 403). Its access protection or API permissions blocked this request; this is not a Steam login failure", null, response.StatusCode);
                response.EnsureSuccessStatusCode(); return response;
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
