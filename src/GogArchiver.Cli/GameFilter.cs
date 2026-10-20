using GogArchiver.Core;
using GogArchiver.Core.Library;

namespace GogArchiver.Cli;

/// <summary>
/// The selection `library list` and `download --all` share. Keeping one implementation is the
/// point: what the listing shows must be exactly what a batch download would fetch.
/// </summary>
public static class GameFilter
{
    /// <summary>
    /// A DLC installer is not a portable game. Select its base game, whose depot includes
    /// owned DLC, even when a search or an explicit slug selected only the expansion.
    /// </summary>
    public static List<LibraryEntry> ResolveDownloads(
        LibrarySnapshot snapshot, IEnumerable<LibraryEntry> games, GamePlatform platform,
        Action<LibraryEntry, IReadOnlyList<LibraryEntry>>? redirected = null)
    {
        var result = new Dictionary<long, LibraryEntry>();
        foreach (var game in games)
        {
            var parents = snapshot.DlcParents(game, platform);
            if (parents.Count == 0)
                result.TryAdd(game.Id, game);
            else
            {
                redirected?.Invoke(game, parents);
                foreach (var parent in parents) result.TryAdd(parent.Id, parent);
            }
        }
        return result.Values.ToList();
    }

    public static bool TryPlatform(Dictionary<string, string> flags, out GamePlatform? platform)
    {
        platform = null;
        if (!flags.TryGetValue("platform", out var name)) return true;

        platform = GamePlatformExtensions.FromApiName(name);
        if (platform is not null) return true;

        Console.Error.WriteLine($"Unknown platform '{name}'. Use windows, mac or linux.");
        return false;
    }

    public static List<LibraryEntry> Apply(
        LibrarySnapshot snapshot, string search, GamePlatform? platform, bool depotOnly, bool dlcOnly) =>
        snapshot.Games
            .Where(g => search.Length == 0
                        || g.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || g.Slug.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(g => platform is null || g.Platforms.ContainsKey(platform.Value))
            .Where(g => !depotOnly || Depots(g, platform).Any())
            .Where(g => !dlcOnly || g.Dlcs.Count > 0)
            .ToList();

    /// <summary>Platforms this game can actually be backed up for.</summary>
    public static IEnumerable<GamePlatform> Depots(LibraryEntry game, GamePlatform? only) =>
        game.Platforms
            .Where(kv => (only is null || kv.Key == only) && kv.Value.CanBackUp)
            .Select(kv => kv.Key);

    public static IEnumerable<GamePlatform> LegacyDepots(LibraryEntry game, GamePlatform? only) =>
        game.Platforms
            .Where(kv => (only is null || kv.Key == only) && kv.Value.HasLegacyDepot)
            .Select(kv => kv.Key);

    public static long Size(LibraryEntry game, GamePlatform? only) =>
        only is not null
            ? game.Platforms.GetValueOrDefault(only.Value)?.BestKnownSize ?? 0
            // No platform picked: one download per game, so the largest is the honest guess.
            : game.Platforms.Values.Select(p => p.BestKnownSize).DefaultIfEmpty(0).Max();

    /// <summary>Everything after the command words, ignoring flags and their values.</summary>
    public static string SearchText(string[] args, int skip) =>
        string.Join(' ', CommandLine.Positionals(args).Skip(skip));
}
