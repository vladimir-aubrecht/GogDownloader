using GameArchiver.Core;

namespace GameArchiver;

public sealed record DrmRefreshResult(ArchiveLibrary Library, int Updated, int Cached, string? Error);

public static class LibraryDrm
{
    public static async Task<DrmRefreshResult> RefreshAsync(ArchiveLibrary library, IDrmSource source,
        string selectedSource = "all", bool force = false, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = library with { Games = library.Games.ToList() };
        int updated = 0, cached = 0;
        string? error = null;
        for (var i = 0; i < result.Games.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var game = result.Games[i];
            if (selectedSource != "all" && game.Key.Source != selectedSource || !library.IsCatalogEntry(game.Key)) continue;
            if (game.Key.Source == "gog")
            {
                result.Games[i] = game with { Drm = game.Versions.Keys.ToDictionary(p => p, _ => DrmInfo.ForStore("gog")) };
                updated++; continue;
            }
            if (game.Key.Source != "steam" || game.Versions.Count == 0) continue;
            if (!force && game.Versions.Keys.All(p => game.Drm.TryGetValue(p, out var drm) && drm.Source == source.Name
                && drm.CheckedAt is { } time && time <= DateTimeOffset.UtcNow && time > DateTimeOffset.UtcNow.AddDays(-7)))
            { cached++; continue; }
            if (error is null)
                try
                {
                    progress?.Report($"DRM metadata: {game.Key} {game.Title}");
                    var info = await source.GetAsync(game.Key, ct);
                    if (info.Any(p => !Enum.IsDefined(p.Key) || p.Value is null || !p.Value.IsValid || p.Value.Source != source.Name))
                        throw new InvalidDataException("Invalid DRM source response.");
                    result.Games[i] = game with { Drm = game.Versions.Keys.ToDictionary(p => p,
                        p => info.GetValueOrDefault(p) ?? new() { Source = source.Name, CheckedAt = DateTimeOffset.UtcNow }) };
                    updated++; continue;
                }
                catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException or TimeoutException)
                { error = $"{source.Name}: {e.Message}"; }
            // One outage warning, no per-game retries. Unrefreshed evidence must not become a
            // successful/free classification; manual corrections remain independently stored.
            result.Games[i] = game with { Drm = game.Versions.Keys.ToDictionary(p => p,
                _ => new DrmInfo { Source = source.Name, Notes = "Community metadata refresh failed: " + error }) };
        }
        return new(result, updated, cached, error);
    }
}
