using GameArchiver.Cli;
using GameArchiver.Core;
using SteamArchiver.Core;
using SteamKit2;
using SteamKit2.Internal;
using ProtoBuf;
using SteamStore = SteamArchiver.Core.SteamClient;

namespace GogArchiver.Probes;

public static class SteamComparisonProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Steam GetOwnedGames diagnostic comparison");
        Protocol();
        var root = Probe.ScratchDirectory();
        try { await ComparisonAsync(root); }
        finally { Directory.Delete(root, true); }
    }

    private static void Protocol()
    {
        var request = SteamOwnedGames.Request(76561198000000001);
        Probe.Check("full GetOwnedGames uses the logged-in account without an AppID filter",
            request.steamid == 76561198000000001 && request.appids_filter.Count == 0 && request.include_appinfo);
        Probe.Check("diagnostic list explicitly includes played free games, free subscriptions and unvetted apps",
            request.include_played_free_games && request.include_free_sub && !request.skip_unvetted_apps);
        var decoded = DecodeCurrentRequest(request);
        Probe.Check("the serialized full list request includes unplayed free games in field 10",
            decoded.IncludeAllFreeGames && decoded.SteamId == 76561198000000001 && decoded.AppIds.Count == 0);
        Probe.Check("including free games does not enable family-shared licenses in field 9", !decoded.IncludeFamilyLicenses);
        // A generated message from the pinned SDK must preserve the newer field when
        // reserialized, even though that SDK has no named property for it.
        decoded = DecodeCurrentRequest(Serializer.DeepClone(request));
        Probe.Check("SteamKit's older generated request retains the new field across a protobuf round trip", decoded.IncludeAllFreeGames);
        request = SteamOwnedGames.Request(76561198000000001, [7, 9]);
        Probe.Check("best-effort name lookup retains its restricted AppID filter", request.appids_filter.SequenceEqual([7u, 9u]));
        decoded = DecodeCurrentRequest(request);
        Probe.Check("filtered name lookup includes unplayed free titles without widening its AppID filter",
            decoded.IncludeAllFreeGames && decoded.AppIds.SequenceEqual([7u, 9u]) && !decoded.IncludeFamilyLicenses);

        var response = new CPlayer_GetOwnedGames_Response { game_count = 2 };
        response.games.Add(new() { appid = 1, name = "Game" });
        response.games.Add(new() { appid = 2 });
        var games = SteamOwnedGames.Parse(EResult.OK, response);
        Probe.Check("complete GetOwnedGames preserves IDs even when a title is unavailable", games.Count == 2 && games[1] == "Game" && games[2] == "");
        Probe.Check("access denial cannot masquerade as an empty diagnostic list", Rejects<IOException>(() => SteamOwnedGames.Parse(EResult.AccessDenied, new())));
        Probe.Check("an absent response count is unavailable data, not an empty library", Rejects<IOException>(() => SteamOwnedGames.Parse(EResult.OK, new())));
        Probe.Check("an explicit complete empty list is accepted", SteamOwnedGames.Parse(EResult.OK, new() { game_count = 0 }).Count == 0);
        response.game_count = 3;
        Probe.Check("partial GetOwnedGames results are rejected before comparison", Rejects<IOException>(() => SteamOwnedGames.Parse(EResult.OK, response)));
        response.game_count = 2;
        response.games[1].appid = 1;
        Probe.Check("duplicate AppIDs are rejected instead of reducing the reported count", Rejects<InvalidDataException>(() => SteamOwnedGames.Parse(EResult.OK, response)));
        response.games[1].appid = 0;
        Probe.Check("invalid GetOwnedGames AppIDs are rejected", Rejects<InvalidDataException>(() => SteamOwnedGames.Parse(EResult.OK, response)));
    }

    private static CurrentOwnedGamesRequest DecodeCurrentRequest(CPlayer_GetOwnedGames_Request request)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, request);
        stream.Position = 0;
        return Serializer.Deserialize<CurrentOwnedGamesRequest>(stream);
    }

    // Subset of Valve's current webui/service_player.proto. Read the wire output
    // with the newer schema rather than asserting the local extension buffer.
    [ProtoContract]
    private sealed class CurrentOwnedGamesRequest
    {
        [ProtoMember(1)] public ulong SteamId { get; set; }
        [ProtoMember(4)] public List<uint> AppIds { get; } = [];
        [ProtoMember(9)] public bool IncludeFamilyLicenses { get; set; }
        [ProtoMember(10)] public bool IncludeAllFreeGames { get; set; }
    }

    private static async Task ComparisonAsync(string root)
    {
        var snapshot = Path.Combine(root, "library.json");
        var library = new ArchiveLibrary
        {
            Games = [new(new("gog", "1"), "GOG title", "gog_title"), Game(1, "Same title"), Game(2, "Warhammer 40,000 Soundtrack"),
                Game(3, "Music album", "music"), Game(4, "Level editor", "application"), Game(6, "Old failed title"),
                Game(7, "Same title"), Game(8, "Expansion", "dlc")],
            Failures = new() { ["steam:5"] = "Detailed denied appinfo", ["steam:6"] = "Detailed dependency failure", ["steam:10"] = "Unknown appinfo",
                ["steam"] = "Previous license outage", ["gog:1"] = "GOG failure" },
            FailureSummaries = new() { ["steam:5"] = "Short denial" },
        };
        await library.SaveAsync(snapshot);
        var before = await File.ReadAllBytesAsync(snapshot);
        var fixture = new Session();
        var output = await RunAsync(snapshot, fixture);
        Probe.Check("comparison makes one list request and no PICS, sync or content calls", output.Exit == 0 && fixture.ListRequests == 1);
        Probe.Check("comparison separates product types and the API list from snapshot game counts",
            output.Text.Contains("Steam snapshot: 7 products, 4 tagged as game.") && output.Text.Contains("GetOwnedGames: 6 entries")
            && output.Text.Contains("requests free games including unplayed titles")
            && output.Text.Contains("Snapshot games present in GetOwnedGames: 2 of 4.")
            && output.Text.Contains("2 snapshot games absent") && output.Text.Contains("2 GetOwnedGames entries absent")
            && output.Text.Contains("2 listed products with another snapshot type"));
        Probe.Check("a soundtrack tagged as game is identified by AppID without guessing a classification",
            output.Text.Contains("SnapshotGameOnly steam:2: Warhammer 40,000 Soundtrack (type: game)")
            && output.Text.Contains("OtherListedProduct steam:3: Music album (type: music)"));
        Probe.Check("comparison matches AppIDs, never identical display names", output.Text.Contains("SnapshotGameOnly steam:7: Same title"));
        Probe.Check("non-game products and other sources do not create false missing-game rows",
            !output.Text.Contains("SnapshotGameOnly steam:8") && !output.Text.Contains("gog:") && !output.Text.Contains("GOG title"));
        Probe.Check("API-only products show titles and IDs", output.Text.Contains("GetOwnedGamesOnly steam:9: Only in API"));
        Probe.Check("a named app returned by GetOwnedGames remains an unresolved sync failure",
            output.Text.Contains("SyncFailure Known unavailable app (steam:5) [listed by GetOwnedGames]: Short denial")
            && output.Text.Contains("SyncFailure New failed title (steam:6) [listed by GetOwnedGames]: Detailed dependency failure"));
        Probe.Check("unlisted AppIDs and source outages retain their own failure diagnostics",
            output.Text.Contains("SyncFailure Name unavailable (steam:10) [not listed by GetOwnedGames]: Unknown appinfo")
            && output.Text.Contains("SyncFailure steam [source failure]: Previous license outage"));
        Probe.Check("comparison does not rewrite metadata, erase failures or persist API-only ownership",
            before.SequenceEqual(await File.ReadAllBytesAsync(snapshot)) && library.Failures.Count == 5);
        output = await RunAsync(snapshot, new(), ["--details"]);
        Probe.Check("comparison --details exposes full failures", output.Text.Contains("Detailed denied appinfo") && !output.Text.Contains("Short denial"));

        fixture = new() { Error = new IOException("GetOwnedGames unavailable") };
        output = await RunAsync(snapshot, fixture);
        Probe.Check("an API failure aborts comparison instead of reporting every game missing",
            output.Exit == 1 && fixture.ListRequests == 1 && output.Text.Contains("GetOwnedGames unavailable") && !output.Text.Contains("SnapshotGameOnly"));
        output = await RunAsync(snapshot, new() { Error = new OperationCanceledException() });
        Probe.Check("provider cancellation is a failed comparison, not a user interruption",
            output.Exit == 1 && output.Text.Contains("Operation timed out or was canceled by the provider") && !output.Text.Contains("Interrupted"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        output = await RunAsync(snapshot, new(), ct: cancellation.Token);
        Probe.Check("diagnostic cancellation returns 130 without claiming download work", output.Exit == 130 && output.Text.Contains("comparison was not completed"));
        Probe.Check("failed or cancelled comparison leaves the snapshot untouched", before.SequenceEqual(await File.ReadAllBytesAsync(snapshot)));

        fixture = new();
        output = await RunAsync(snapshot, fixture, ["--source", "all"]);
        Probe.Check("comparison requires an explicit Steam source before contacting the service", output.Exit == 1 && fixture.ListRequests == 0);
        fixture = new();
        output = await RunAsync(snapshot, fixture, ["warhammer"]);
        Probe.Check("comparison refuses ignored positional filters", output.Exit == 1 && fixture.ListRequests == 0);
        fixture = new();
        output = await RunAsync(Path.Combine(root, "missing.json"), fixture);
        Probe.Check("comparison requires a snapshot before contacting Steam", output.Exit == 1 && fixture.ListRequests == 0);
        await new ArchiveLibrary { Games = [Game(1, "GOG title") with { Key = new("gog", "1") }] }.SaveAsync(snapshot);
        fixture = new();
        output = await RunAsync(snapshot, fixture);
        Probe.Check("a GOG-only snapshot cannot be compared as a Steam library", output.Exit == 1 && fixture.ListRequests == 0 && output.Text.Contains("No Steam data"));
    }

    private static GameInfo Game(uint id, string title, string kind = "game") => new(new("steam", id.ToString()), title, $"app_{id}") { Kind = kind };
    private static bool Rejects<T>(Action action) where T : Exception
    { try { action(); return false; } catch (T) { return true; } }
    private static async Task<(int Exit, string Text)> RunAsync(string snapshot, Session session, string[]? extra = null, CancellationToken ct = default)
    {
        var output = Console.Out; var error = Console.Error; using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer); Console.SetError(writer);
            var exit = await ArchiveCommandLine.RunAsync(["library", "compare", "--source", "steam", "--snapshot", snapshot, .. extra ?? []],
                ct, storeFactory: _ => [new SteamStore(session)]);
            return (exit, writer.ToString());
        }
        finally { Console.SetOut(output); Console.SetError(error); }
    }
    private sealed class Session : ISteamSession
    {
        public int ListRequests;
        public Exception? Error;
        public Task<IReadOnlyDictionary<uint, string>> GetOwnedGamesAsync(CancellationToken ct)
        {
            ListRequests++;
            if (Error is not null) throw Error;
            return Task.FromResult<IReadOnlyDictionary<uint, string>>(new Dictionary<uint, string>
            { [1] = "Same title", [3] = "Music album", [4] = "Level editor", [5] = "Known unavailable app", [6] = "New failed title", [9] = "Only in API" });
        }
        public Task<IReadOnlySet<uint>> GetOwnedAppIdsAsync(CancellationToken ct) => throw new Exception("Comparison must not reload PICS licenses");
        public Task<SteamApp> GetAppAsync(uint id, CancellationToken ct) => throw new Exception("Comparison must not reload PICS appinfo");
        public Task<ISteamContentSession> OpenContentAsync(long rate, CancellationToken ct) => throw new Exception("Comparison must not download content");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
