using SteamKit2;
using SteamKit2.Internal;
using ProtoBuf;

namespace SteamArchiver.Core;

/// <summary>Diagnostic list protocol; separate from PICS ownership and content planning.</summary>
internal static class SteamOwnedGames
{
    internal static CPlayer_GetOwnedGames_Request Request(ulong steamId, IEnumerable<uint>? appIds = null)
    {
        var request = new CPlayer_GetOwnedGames_Request { steamid = steamId, include_appinfo = true,
            include_played_free_games = true, include_free_sub = true, skip_unvetted_apps = false };
        // SteamKit2 3.4.0 predates include_all_free_games (protobuf field 10).
        // Request unplayed free titles too; see the verified schema in docs/steamkit2.md.
        Extensible.AppendValue(request, 10, true);
        if (appIds is not null) request.appids_filter.AddRange(appIds);
        return request;
    }

    internal static IReadOnlyDictionary<uint, string> Parse(EResult status, CPlayer_GetOwnedGames_Response response)
    {
        if (status != EResult.OK) throw new IOException($"Steam GetOwnedGames failed: {status}.");
        if (!response.ShouldSerializegame_count() || response.game_count != response.games.Count)
            throw new IOException("Steam GetOwnedGames returned no complete game list; comparison was not performed.");
        var games = new Dictionary<uint, string>();
        foreach (var game in response.games)
            if (game.appid <= 0 || !games.TryAdd((uint)game.appid, game.name ?? ""))
                throw new InvalidDataException("Steam GetOwnedGames returned invalid or duplicate AppIDs.");
        return games;
    }
}
