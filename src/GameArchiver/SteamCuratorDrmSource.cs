using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameArchiver.Core;

namespace GameArchiver;

/// <summary>Explicit Windows DRM-free reports from the public DRM-Free Complete List curator.</summary>
public sealed class SteamCuratorDrmSource : IDrmSource, IDisposable
{
    public const string CuratorUrl = "https://store.steampowered.com/curator/45614612/";
    public string Name => "steam-curator:45614612";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest, _checkedAt;
    private Dictionary<uint, Report>? _reports;
    private sealed record Report(string Text, bool Windows);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public SteamCuratorDrmSource(HttpClient? http = null, TimeSpan? minimumInterval = null)
    {
        _interval = minimumInterval ?? TimeSpan.FromSeconds(1.1);
        if (_interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        _http = http ?? new HttpClient(); _ownsHttp = http is null;
    }

    public async Task<IReadOnlyDictionary<GameOs, DrmInfo>> GetAsync(GameKey key, CancellationToken ct = default)
    {
        if (key.Source != "steam" || !uint.TryParse(key.ProductId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
            throw new ArgumentException("Steam curator lookup requires a numeric Steam AppID.");
        await _gate.WaitAsync(ct);
        try
        {
            if (_reports is null)
            {
                // Never publish a partial list: a missing page is an outage, not negative evidence.
                _reports = await LoadAsync(ct);
                _checkedAt = DateTimeOffset.UtcNow;
            }
            var report = _reports.GetValueOrDefault(id);
            var free = report is { Windows: true } && report.Text.ToLowerInvariant() is
                "drm-free" or "drm-free." or "drm-free, according to pcgamingwiki.";
            return Enum.GetValues<GameOs>().ToDictionary(p => p, p => new DrmInfo
            {
                Source = Name, Url = CuratorUrl + "?appid=" + id.ToString(CultureInfo.InvariantCulture), CheckedAt = _checkedAt,
                Status = p == GameOs.Windows && free ? DrmStatus.Free : DrmStatus.Unknown,
                Notes = report is null ? "Not listed by this curator; absence is not evidence of DRM."
                    : "Curator report: " + report.Text.TrimEnd('.') + ". "
                        + (p != GameOs.Windows ? "Native Linux/macOS DRM is not classified by this adapter. "
                            : !report.Windows ? "The curator entry does not identify a Windows release. "
                            : !free ? "Conditional or unrecognized report requires manual review. " : "")
                        + "Community report for the public branch, not a per-build offline-play test.",
            });
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<uint, Report>> LoadAsync(CancellationToken ct)
    {
        var reports = new Dictionary<uint, Report>();
        int start = 0, pages = 0; int? total = null;
        do
        {
            if (++pages > 100) throw new InvalidDataException("Steam curator pagination exceeds 100 pages.");
            using var page = await GetPageAsync(start, ct);
            var root = page.RootElement;
            if (Number(root, "success") != 1 || Number(root, "start") != start)
                throw new InvalidDataException("Steam curator returned an unsuccessful or misaligned page.");
            var count = Number(root, "total_count"); var pageSize = Number(root, "pagesize");
            if (count < 0 || count > 10000 || pageSize is < 1 or > 100 || total is not null && count != total)
                throw new InvalidDataException("Steam curator pagination changed or exceeds supported limits; retry the refresh.");
            total = count;
            if (!root.TryGetProperty("results_html", out var body) || body.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Steam curator response is missing its recommendations.");
            var rows = ParseRows(body.GetString()!);
            if (rows.Count != Math.Min(pageSize, count - start))
                throw new InvalidDataException("Steam curator returned an incomplete recommendation page.");
            foreach (var (id, report) in rows)
                if (!reports.TryAdd(id, report)) throw new InvalidDataException("Steam curator returned duplicate AppIDs across pages.");
            start += rows.Count;
        } while (start < total.GetValueOrDefault());
        return reports;
    }

    private static int Number(JsonElement root, string key)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number)) return number;
        }
        throw new InvalidDataException("Invalid Steam curator pagination field: " + key);
    }

    private static Dictionary<uint, Report> ParseRows(string html)
    {
        // The endpoint returns HTML fragments, not structured recommendations. Match the
        // observed row/link/description boundaries strictly and fail closed on format changes.
        MatchCollection Tags(string input, string tag, string css) => Regex.Matches(input,
            "<" + tag + "\\b(?=[^>]*\\sclass\\s*=\\s*[\"']" + css + "[\"'])[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        var starts = Tags(html, "div", "recommendation");
        var rows = new Dictionary<uint, Report>();
        for (var i = 0; i < starts.Count; i++)
        {
            var row = html[starts[i].Index..(i + 1 < starts.Count ? starts[i + 1].Index : html.Length)];
            var links = Tags(row, "a", "recommendation_link");
            var descriptions = Tags(row, "div", "recommendation_desc");
            if (links.Count != 1 || descriptions.Count != 1) throw new InvalidDataException("Unrecognized Steam curator recommendation markup.");
            var href = Regex.Match(links[0].Value, "\\shref\\s*=\\s*[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase, RegexTimeout);
            if (!href.Success || !Uri.TryCreate(WebUtility.HtmlDecode(href.Groups["url"].Value), UriKind.Absolute, out var uri)
                || uri.Scheme != "https" || uri.Host != "store.steampowered.com"
                || uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is not ["app", var app, ..]
                || !uint.TryParse(app, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
                throw new InvalidDataException("Steam curator recommendation has no exact Steam AppID.");
            var begin = descriptions[0].Index + descriptions[0].Length;
            var end = row.IndexOf("</div>", begin, StringComparison.OrdinalIgnoreCase);
            if (end < 0) throw new InvalidDataException("Incomplete Steam curator description.");
            var text = Regex.Replace(WebUtility.HtmlDecode(row[begin..end]), @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
            if (text.Length > 4096) throw new InvalidDataException("Steam curator description exceeds 4096 characters.");
            // Do not strip tags or qualifications to make a report look unconditional.
            var windows = Tags(row, "span", "platform_img win").Count > 0;
            if (!rows.TryAdd(id, new(text, windows))) throw new InvalidDataException("Steam curator returned duplicate AppIDs on one page.");
        }
        return rows;
    }

    private async Task<JsonDocument> GetPageAsync(int start, CancellationToken ct)
    {
        var delay = _lastRequest + _interval - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        _lastRequest = DateTimeOffset.UtcNow;
        using var request = new HttpRequestMessage(HttpMethod.Get, CuratorUrl
            + $"ajaxgetfilteredrecommendations/?start={start}&count=100&sort=recent&l=english&filter=all");
        request.Headers.UserAgent.ParseAdd("GameArchiver/1.0 (+https://github.com/vladimir-aubrecht/gogdownloader)");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream(); var buffer = new byte[16384];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (output.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("Steam curator response exceeds 2 MiB.");
                output.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(output.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Steam curator request timed out."); }
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); _gate.Dispose(); }
}
