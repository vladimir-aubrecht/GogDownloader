using System.Net;
using GameArchiver;
using GameArchiver.Cli;
using GameArchiver.Core;
using GogArchiver.Core;
using GogArchiver.Core.Library;

namespace GogArchiver.Probes;

public static class GogComparisonProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("GOG live ownership comparison");
        var root = Probe.ScratchDirectory();
        try { await CompareAsync(root); }
        finally { Directory.Delete(root, true); }
    }

    private static async Task CompareAsync(string root)
    {
        var snapshot = Path.Combine(root, "aggregate.json");
        var native = Path.Combine(root, "gog.json");
        await File.WriteAllTextAsync(native, "Native snapshot must not be read or overwritten");
        var library = new ArchiveLibrary
        {
            Games = [Game(1, "Same title"), Game(2, "Expansion", "dlc"), Game(3, "Collection", "container"),
                Game(4, "Same title"), Game(6, "Previously known failed game"),
                Game(7, "Steam title") with { Key = new("steam", "7") }],
            Failures = new()
            {
                ["gog:5"] = "Full product failure", ["gog:6"] = "Full retained failure",
                ["gog:8"] = "Unknown old failure", ["gog"] = "Previous source outage", ["steam:1"] = "Steam failure",
            },
            FailureNames = new() { ["gog:5"] = "Failed new game" },
            FailureSummaries = new() { ["gog:5"] = "Short product failure" },
        };
        await library.SaveAsync(snapshot);
        var before = await File.ReadAllBytesAsync(snapshot);
        var fixture = new Ownership();
        var output = await RunAsync(snapshot, native, fixture);
        Probe.Check("GOG comparison makes one ownership request without product, build or content requests", output.Exit == 0 && fixture.Calls == 1);
        Probe.Check("GOG comparison includes DLC and containers and ignores other sources",
            output.Text.Contains("GOG snapshot: 5 products.") && output.Text.Contains("GOG owned-product API: 6 products")
            && output.Text.Contains("present in the owned list: 4 of 5.")
            && output.Text.Contains("1 snapshot products absent") && output.Text.Contains("2 owned products absent")
            && !output.Text.Contains("steam:") && !output.Text.Contains("Steam title"));
        Probe.Check("GOG comparison matches IDs, not equal names", output.Text.Contains("SnapshotOnly gog:4: Same title")
            && !output.Text.Contains("SnapshotOnly gog:1:"));
        Probe.Check("API-only GOG products use saved failure names with an explicit unknown-name fallback",
            output.Text.Contains("OwnedOnly Failed new game (gog:5)") && output.Text.Contains("OwnedOnly Name unavailable (gog:7)"));
        Probe.Check("owned products keep their unresolved sync failures",
            output.Text.Contains("SyncFailure Failed new game (gog:5) [listed as owned]: Short product failure")
            && output.Text.Contains("SyncFailure Previously known failed game (gog:6) [listed as owned]: Full retained failure"));
        Probe.Check("GOG comparison retains unowned and source failures",
            output.Text.Contains("SyncFailure Name unavailable (gog:8) [not listed as owned]: Unknown old failure")
            && output.Text.Contains("SyncFailure gog [source failure]: Previous source outage"));
        output = await RunAsync(snapshot, native, new(), ["--details"]);
        Probe.Check("GOG comparison supports full failure details", output.Text.Contains("Full product failure") && !output.Text.Contains("Short product failure"));
        output = await RunAsync(snapshot, native, new() { Json = "{\"owned\":[]}" });
        Probe.Check("an explicitly empty GOG ownership list is valid", output.Exit == 0 && output.Text.Contains("5 snapshot products absent"));

        foreach (var invalid in new[] { "{}", "null", "{\"owned\":null}", "{\"owned\":[0]}", "{\"owned\":[-1]}", "{\"owned\":[1,1]}", "{\"owned\":[" })
        {
            output = await RunAsync(snapshot, native, new() { Json = invalid });
            Probe.Check($"invalid GOG ownership response cannot masquerade as missing products: {invalid}",
                output.Exit == 1 && !output.Text.Contains("SnapshotOnly") && !output.Text.Contains("Differences:"));
        }
        output = await RunAsync(snapshot, native, new() { Status = HttpStatusCode.Unauthorized });
        Probe.Check("GOG authentication failure aborts comparison", output.Exit == 1 && !output.Text.Contains("Differences:"));
        output = await RunAsync(snapshot, native, new() { Error = new OperationCanceledException() });
        Probe.Check("GOG comparison cancellation is reported without implying download work", output.Exit == 130 && output.Text.Contains("comparison was not completed"));
        Probe.Check("successful, failed and cancelled comparisons leave both snapshots byte-for-byte unchanged",
            before.SequenceEqual(await File.ReadAllBytesAsync(snapshot)) && await File.ReadAllTextAsync(native) == "Native snapshot must not be read or overwritten");

        foreach (var invalid in new[] { "{}", "{\"owned\":null}" })
        {
            using var http = new HttpClient(new Ownership { Json = invalid });
            await using var archive = new GameArchiverClient(new GogClient(new(http), new(http, http, new(0)), http, new(), new() { SnapshotPath = native }));
            var synced = await archive.SyncLibraryAsync(library);
            Probe.Check("invalid ownership data preserves products during sync and records a source failure",
                synced.Games.Select(g => g.Key).ToHashSet().SetEquals(library.Games.Select(g => g.Key))
                && synced.Failures.ContainsKey("gog") && synced.Failures.ContainsKey("steam:1")
                && await File.ReadAllTextAsync(native) == "Native snapshot must not be read or overwritten");
        }

        foreach (var extra in new[] { new[] { "--source", "all" }, new[] { "unhandled-filter" } })
        {
            fixture = new(); output = await RunAsync(snapshot, native, fixture, extra);
            Probe.Check("GOG comparison rejects unsupported scope before authentication", output.Exit == 1 && fixture.Calls == 0);
        }
        fixture = new(); output = await RunAsync(Path.Combine(root, "missing.json"), native, fixture);
        Probe.Check("missing GOG snapshot is rejected before contacting the service", output.Exit == 1 && fixture.Calls == 0);
        await new ArchiveLibrary { Games = [Game(1, "Steam title") with { Key = new("steam", "1") }] }.SaveAsync(snapshot);
        fixture = new(); output = await RunAsync(snapshot, native, fixture);
        Probe.Check("a Steam-only snapshot cannot be compared as a GOG library", output.Exit == 1 && fixture.Calls == 0 && output.Text.Contains("No GOG data"));

        await new ArchiveLibrary().SaveAsync(snapshot);
        output = await RunAsync(snapshot, native, new());
        Probe.Check("empty aggregate snapshots can reveal newly owned GOG products", output.Exit == 0 && output.Text.Contains("6 owned products absent"));

        await new LibrarySnapshot
        {
            SchemaVersion = LibrarySnapshot.CurrentSchema,
            Games = [new() { Id = 1, Slug = "legacy", Title = "Legacy game" }], Failures = new() { [5] = "Legacy failure" },
        }.SaveAsync(snapshot);
        before = await File.ReadAllBytesAsync(snapshot);
        output = await RunAsync(snapshot, native, new());
        Probe.Check("legacy GOG snapshots preserve failures during read-only comparison",
            output.Exit == 0 && output.Text.Contains("GOG snapshot: 1 products.")
            && output.Text.Contains("SyncFailure Name unavailable (gog:5) [listed as owned]: Legacy failure")
            && before.SequenceEqual(await File.ReadAllBytesAsync(snapshot)));
        await new LibrarySnapshot { SchemaVersion = LibrarySnapshot.CurrentSchema }.SaveAsync(snapshot);
        output = await RunAsync(snapshot, native, new());
        Probe.Check("empty native GOG snapshots can reveal newly owned products", output.Exit == 0 && output.Text.Contains("6 owned products absent"));
        await new ArchiveLibrary { Failures = new() { ["gog"] = "Initial sync failed" } }.SaveAsync(snapshot);
        output = await RunAsync(snapshot, native, new());
        Probe.Check("GOG source failures can be compared even before a successful product sync",
            output.Exit == 0 && output.Text.Contains("SyncFailure gog [source failure]: Initial sync failed"));
    }

    private static GameInfo Game(long id, string title, string kind = "game") => new(new("gog", id.ToString()), title, $"product_{id}") { Kind = kind };
    private static async Task<(int Exit, string Text)> RunAsync(string snapshot, string native, Ownership fixture, string[]? extra = null)
    {
        using var http = new HttpClient(fixture);
        var output = Console.Out; var error = Console.Error; using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer); Console.SetError(writer);
            var exit = await ArchiveCommandLine.RunAsync(["library", "compare", "--source", "gog", "--snapshot", snapshot, .. extra ?? []],
                storeFactory: _ => [new GogClient(new(http), new(http, http, new(0)), http, new(), new() { SnapshotPath = native })]);
            return (exit, writer.ToString());
        }
        finally { Console.SetOut(output); Console.SetError(error); }
    }

    private sealed class Ownership : HttpMessageHandler
    {
        public int Calls;
        public string Json = "{\"owned\":[1,2,3,5,6,7]}";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Exception? Error;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.RequestUri!.AbsoluteUri != "https://embed.gog.com/user/data/games")
                throw new Exception($"Comparison must not request product details, builds or content: {request.RequestUri}");
            if (Error is not null) throw Error;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Json) });
        }
    }
}
