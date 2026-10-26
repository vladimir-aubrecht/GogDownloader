using GameArchiver;
using GameArchiver.Cli;
using GameArchiver.Core;
using SteamArchiver.Core;

namespace GogArchiver.Probes;

public static class SteamAppInfoProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Steam appinfo recovery and named diagnostics");
        TokenStates();
        await RecoveryAsync();
        await FailuresAsync();
        var root = Probe.ScratchDirectory();
        try { await NamesAsync(root); }
        finally { Directory.Delete(root, true); }
    }

    private static SteamAppInfoResult Missing(SteamAppTokenStatus status) => new(Error: new SteamAppInfoAccessException(1, status));
    private static SteamAppInfoResult App(uint id) => new(new(id, $"App {id}") { Type = "tool" });
    private static Task<IReadOnlyDictionary<uint, SteamAppInfoResult>> Reply(params (uint Id, SteamAppInfoResult Value)[] values) =>
        Task.FromResult<IReadOnlyDictionary<uint, SteamAppInfoResult>>(values.ToDictionary(v => v.Id, v => v.Value));

    private static void TokenStates()
    {
        var tokens = new Dictionary<uint, ulong> { [1] = 876543210123456789, [2] = 0 };
        Probe.Check("PICS distinguishes an explicit denial from an omitted token",
            SteamAppInfoAccessException.Status(3, tokens, [3]) == SteamAppTokenStatus.Denied
            && SteamAppInfoAccessException.Status(4, tokens, [3]) == SteamAppTokenStatus.Omitted);
        Probe.Check("a public zero token is distinct from a granted private token",
            SteamAppInfoAccessException.Status(2, tokens, []) == SteamAppTokenStatus.Public
            && SteamAppInfoAccessException.Status(1, tokens, []) == SteamAppTokenStatus.Granted);
        Probe.Check("token diagnostics contain no token values", !new SteamAppInfoAccessException(1,
            SteamAppInfoAccessException.Status(1, tokens, [])).ToString().Contains(tokens[1].ToString()));
    }

    private static async Task RecoveryAsync()
    {
        var calls = new List<uint[]>();
        var cache = new SteamAppInfoCache((ids, _) =>
        {
            calls.Add(ids.ToArray());
            return calls.Count == 1 ? Reply((1, Missing(SteamAppTokenStatus.Omitted)), (2, App(2))) : Reply((1, App(1)));
        });
        await cache.PreloadAsync(new HashSet<uint> { 1, 2 }, default);
        Probe.Check("a bulk missing-token result is rechecked individually and can recover",
            calls.Count == 2 && calls[0].SequenceEqual([1u, 2u]) && calls[1].SequenceEqual([1u])
            && (await cache.GetAsync(1, default)).AppId == 1 && (await cache.GetAsync(2, default)).AppId == 2);
        await cache.PreloadAsync(new HashSet<uint> { 1, 2 }, default);
        Probe.Check("successful appinfo is cached without repeating token requests", calls.Count == 2);
        calls.Clear();
        cache = new((ids, _) =>
        {
            calls.Add(ids.ToArray());
            return calls.Count == 1 ? Reply((1, Missing(SteamAppTokenStatus.Granted))) : Reply((1, App(1)));
        });
        Probe.Check("a standalone lookup also refreshes its token once", (await cache.GetAsync(1, default)).AppId == 1 && calls.Count == 2);

        calls.Clear();
        cache = new((ids, _) => { calls.Add(ids.ToArray()); return Reply(ids.Select(id => (id, App(id))).ToArray()); });
        await cache.PreloadAsync(Enumerable.Range(1, 101).Select(i => (uint)i).ToHashSet(), default);
        Probe.Check("normal library appinfo remains batched at 100 apps", calls.Select(c => c.Length).SequenceEqual([100, 1]));
    }

    private static async Task FailuresAsync()
    {
        var calls = 0;
        var cache = new SteamAppInfoCache((_, _) => { calls++; return Reply((1, Missing(SteamAppTokenStatus.Denied))); });
        cache.Reset(new Dictionary<uint, uint[]> { [1] = [9, 7, 9] });
        var error = await ErrorAsync(() => cache.GetAsync(1, default));
        Probe.Check("persistent denial reports both token checks and the owning package IDs",
            calls == 2 && error.Contains("Initial token request: explicitly denied") && error.Contains("Licensed packages: 7, 9")
            && error.Contains("Individual retry:") && error.Contains("PICS missing_token"));
        await ErrorAsync(() => cache.GetAsync(1, default));
        await cache.PreloadAsync(new HashSet<uint> { 1 }, default);
        Probe.Check("repeated references to a denied app do not cause a retry loop", calls == 2);
        cache.Reset(new Dictionary<uint, uint[]>());
        error = await ErrorAsync(() => cache.GetAsync(1, default));
        Probe.Check("the next library refresh retries access and drops stale license diagnostics",
            calls == 4 && error.Contains("none in the current license list") && !error.Contains("7, 9"));

        calls = 0;
        cache = new((_, _) => { calls++; return Reply((1, new(Error: new InvalidDataException("Malformed appinfo")))); });
        error = await ErrorAsync(() => cache.GetAsync(1, default));
        Probe.Check("malformed appinfo is preserved as a data error without a token retry", calls == 1 && error.Contains("Malformed appinfo"));

        calls = 0;
        cache = new((_, _) => ++calls == 1 ? Reply((1, Missing(SteamAppTokenStatus.Granted)))
            : throw new TimeoutException("Recheck timed out"));
        error = await ErrorAsync(() => cache.GetAsync(1, default));
        Probe.Check("a failed recheck reports its actual error without inventing a denial",
            calls == 2 && error.Contains("nonzero token returned") && error.Contains("Recheck timed out") && !error.Contains("explicitly denied"));

        calls = 0;
        using var cancel = new CancellationTokenSource();
        cache = new((_, ct) =>
        {
            if (++calls == 1) return Reply((1, Missing(SteamAppTokenStatus.Public)));
            cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Reply((1, App(1)));
        });
        var cancelled = false;
        try { await cache.GetAsync(1, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        Probe.Check("cancellation during the individual recheck is propagated", cancelled && calls == 2);
    }

    private static async Task NamesAsync(string root)
    {
        var session = new NameSession();
        await using var steam = new SteamClient(session);
        await using var aggregate = new GameArchiverClient([steam], ownsStores: false);
        var previous = new ArchiveLibrary { Games = [new(new("steam", "2"), "Old library title", "old_game")],
            Failures = new() { ["gog:9"] = "GOG failure" }, FailureNames = new() { ["gog:9"] = "GOG title" } };
        var library = await aggregate.SyncLibraryAsync(previous);
        Probe.Check("Steam names label failures without granting ownership or creating complete games",
            library.FailureNames["steam:1"] == "Known Steam title" && library.Games.All(g => g.Key.ProductId != "1")
            && !library.FailureNames.ContainsKey("steam:99") && library.Failures.ContainsKey("steam:1"));
        Probe.Check("an old library title labels an app when Steam has no name",
            library.FailureNames["steam:2"] == "Old library title" && library.Games.Single().Key.ProductId == "2");
        Probe.Check("source-filtered sync preserves other providers' failure names", library.FailureNames["gog:9"] == "GOG title");
        var snapshot = Path.Combine(root, "library.json");
        await library.SaveAsync(snapshot);
        library = await ArchiveLibrary.LoadAsync(snapshot);
        Probe.Check("failure names survive snapshot serialization", library.FailureNames["steam:1"] == "Known Steam title");
        var result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(
            ["library", "failures", "--source", "steam", "--snapshot", snapshot], storeFactory: _ => [new OfflineStore()]));
        Probe.Check("offline failure output shows names next to IDs and explicitly labels unknown names",
            result.Exit == 0 && result.Text.Contains("Known Steam title (steam:1)") && result.Text.Contains("Old library title (steam:2)")
            && result.Text.Contains("Name unavailable (steam:3)") && !result.Text.Contains("GOG title"));
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(
            ["library", "failures", "Known Steam", "--source", "steam", "--snapshot", snapshot], storeFactory: _ => [new OfflineStore()]));
        Probe.Check("recorded failures can be searched by display name", result.Text.Contains("1 recorded sync failures") && !result.Text.Contains("steam:2"));

        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(
            ["library", "sync", "--source", "steam", "--snapshot", snapshot], storeFactory: _ => [new SteamClient(new NameSession())]));
        Probe.Check("sync failure output includes the display name and keeps its failing exit status",
            result.Exit == 1 && result.Text.Contains("Failed Known Steam title (steam:1):"));

        session.FailNames = true;
        var unavailable = await aggregate.SyncLibraryAsync(library);
        Probe.Check("optional name lookup failures preserve both app failures and previously known names",
            unavailable.Failures.ContainsKey("steam:1") && unavailable.FailureNames["steam:1"] == "Known Steam title");
        session.FailLibrary = true;
        var outage = await aggregate.SyncLibraryAsync(unavailable);
        Probe.Check("a whole-source outage does not erase previously discovered failure names",
            outage.Failures.ContainsKey("steam") && outage.FailureNames["steam:1"] == "Known Steam title");
        await File.WriteAllTextAsync(snapshot, """{"SchemaVersion":1,"Games":[],"Failures":{"steam:1":"old failure"}}""");
        Probe.Check("older snapshots without failure names still load", (await ArchiveLibrary.LoadAsync(snapshot)).FailureNames.Count == 0);
    }

    private static async Task<string> ErrorAsync(Func<Task> action)
    { try { await action(); return "No error"; } catch (IOException e) { return e.Message; } }
    private static async Task<(int Exit, string Text)> CaptureAsync(Func<Task<int>> action)
    {
        var output = Console.Out; var error = Console.Error; using var writer = new StringWriter();
        try { Console.SetOut(writer); Console.SetError(writer); return (await action(), writer.ToString()); }
        finally { Console.SetOut(output); Console.SetError(error); }
    }
    private sealed class NameSession : ISteamSession
    {
        public Task<IReadOnlyDictionary<uint, string>> GetOwnedGamesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<uint, string>>(new Dictionary<uint, string> { [1] = "", [2] = "", [3] = "" });
        public bool FailNames;
        public bool FailLibrary;
        public Task<IReadOnlySet<uint>> GetOwnedAppIdsAsync(CancellationToken ct) => FailLibrary ? throw new IOException("Steam unavailable")
            : Task.FromResult<IReadOnlySet<uint>>(new HashSet<uint> { 1, 2, 3 });
        public Task<SteamApp> GetAppAsync(uint appId, CancellationToken ct) => throw new IOException("Appinfo unavailable");
        public Task<IReadOnlyDictionary<uint, string>> GetAppNamesAsync(IReadOnlySet<uint> ids, CancellationToken ct) => FailNames
            ? throw new IOException("Name service unavailable") : Task.FromResult<IReadOnlyDictionary<uint, string>>(new Dictionary<uint, string>
                { [1] = "Known Steam title", [99] = "Unrequested name" });
        public Task<ISteamContentSession> OpenContentAsync(long rate, CancellationToken ct) => throw new Exception("No content may be downloaded");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class OfflineStore : IGameStore
    {
        public string Source => "steam";
        public Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default) => throw new Exception("Offline output must not sync");
        public Task<GameVersion> GetVersionAsync(string id, GameSelection options, CancellationToken ct = default) => throw new Exception("Offline output must not refresh");
        public Task<ArchiveResult> DownloadAsync(string id, ArchiveOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) => throw new Exception("Offline output must not download");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
