using System.Text.Json;
using GameArchiver.Cli;
using GameArchiver.Core;

namespace GogArchiver.Probes;

internal static class ScheduleProbes
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "schedule-probes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new ScheduleSettings
            {
                Snapshot = Path.Combine(root, "state", "library.json"), StagingDirectory = Path.Combine(root, "scratch"),
                Jobs = [new() { Name = "GOG", Source = "gog", Output = Path.Combine(root, "gog games"), All = true },
                    new() { Name = "Small Steam games", Source = "steam", Output = Path.Combine(root, "steam"), All = true,
                        Search = "title with spaces; no shell", MaxSize = "100MB", Drm = "free", DrmSource = "steam-curator" },
                    new() { Name = "Selected Steam games", Source = "steam", Output = Path.Combine(root, "selected"),
                        Products = ["steam:2270", "game_slug", "steam:2270"] }],
            };
            var plans = ArchiveCommandLine.BuildSchedule(settings);
            Probe.Check("scheduled jobs retain source, output, filters and searches as separate arguments",
                plans[1].Downloads.Single().Contains("title with spaces; no shell")
                && Value(plans[0].Downloads.Single(), "--out") == settings.Jobs[0].Output
                && Value(plans[1].Downloads.Single(), "--drm") == "free"
                && Value(plans[1].Downloads.Single(), "--max-size") == "100MB"
                && plans[2].Downloads.Count == 2 && plans[2].Downloads.All(c => !c.Contains("--all")));

            var invalidCase = 0;
            foreach (var bad in new[]
            {
                settings.Jobs[0] with { All = false },
                settings.Jobs[0] with { Products = ["1"] },
                settings.Jobs[0] with { Source = "all" },
                settings.Jobs[0] with { Output = "relative" },
                settings.Jobs[0] with { Architecture = "invalid" },
                settings.Jobs[0] with { MaxSize = "not a size" },
                settings.Jobs[0] with { Search = "--force" },
                settings.Jobs[2] with { Products = ["gog:1"] },
                settings.Jobs[2] with { Drm = "free" },
                settings.Jobs[2] with { Products = ["--all"] },
            })
            {
                var rejected = false;
                try { ArchiveCommandLine.BuildSchedule(settings with { Jobs = [bad] }); }
                catch (ArgumentException) { rejected = true; }
                Probe.Check("invalid schedule is rejected before any network or download: case " + ++invalidCase, rejected);
            }
            var config = Path.Combine(root, "schedule.json");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(settings));
            var output = Console.Out; var captured = new StringWriter();
            try
            {
                Console.SetOut(captured);
                var code = await ArchiveCommandLine.RunAsync(["schedule", "--config", config, "--dry-run"],
                    storeFactory: _ => throw new Exception("Preview must not open a store"));
                Probe.Check("schedule preview needs no credentials and does not create state", code == 0 && !Directory.Exists(Path.GetDirectoryName(settings.Snapshot)));
            }
            finally { Console.SetOut(output); }
            Probe.Check("schedule preview displays every job", captured.ToString().Contains("Small Steam games"));
            await File.WriteAllTextAsync(config, "{\"jobs\": [], \"intervalMinuts\": 3}");
            Probe.Check("unknown configuration properties are errors instead of ignored settings",
                await ArchiveCommandLine.RunAsync(["schedule", "--config", config, "--dry-run"]) == 1);

            var commands = new List<string[]>();
            var downloadFailures = false;
            var syncFailures = "partial";
            async Task<int> Invoke(string[] args, CancellationToken ct)
            {
                commands.Add(args);
                if (args[0] == "library" && args[1] == "sync")
                {
                    var source = Value(args, "--source");
                    if (source == "steam" && syncFailures == "stale") return 1;
                    var library = new ArchiveLibrary();
                    if (source == "steam" && syncFailures is "partial" or "source")
                        library.Failures[syncFailures == "source" ? "steam" : "steam:123"] = "fixture failure";
                    await library.SaveAsync(settings.Snapshot, ct);
                    return library.Failures.Count == 0 ? 0 : 1;
                }
                return args[0] == "download" && downloadFailures ? 1 : 0;
            }
            var result = await ScheduleRunner.RunAsync(settings, plans, true, Invoke, CancellationToken.None);
            Probe.Check("one cycle syncs each source once and downloads after partial inventory failures",
                result == 1 && commands.Count(c => c.Take(2).SequenceEqual(["library", "sync"])) == 2
                && commands.Count(c => c[0] == "download") == 4 && commands.Count(c => c.Contains("--drm-source")) == 1);
            commands.Clear(); syncFailures = "source";
            result = await ScheduleRunner.RunAsync(settings, plans, true, Invoke, CancellationToken.None);
            Probe.Check("whole-source failure skips its downloads without blocking other sources",
                result == 1 && commands.Count(c => c[0] == "download") == 1 && !commands.Any(c => c.Contains("--drm-source")));
            commands.Clear(); syncFailures = "stale";
            result = await ScheduleRunner.RunAsync(settings, plans, true, Invoke, CancellationToken.None);
            Probe.Check("failed sync cannot download from another source's stale snapshot",
                result == 1 && commands.Count(c => c[0] == "download") == 1);
            commands.Clear(); syncFailures = "none";
            var mixed = settings with { Jobs = [settings.Jobs[1], settings.Jobs[1] with { Name = "Wiki", DrmSource = "pcgamingwiki" },
                settings.Jobs[1] with { Name = "Curator again" }] };
            result = await ScheduleRunner.RunAsync(mixed, ArchiveCommandLine.BuildSchedule(mixed), true, Invoke, CancellationToken.None);
            Probe.Check("interleaved DRM sources refresh the correct evidence before each job",
                result == 0 && commands.Where(c => c.Contains("--drm-source")).Select(c => Value(c, "--drm-source"))
                    .SequenceEqual(["steam-curator", "pcgamingwiki", "steam-curator"]));
            commands.Clear();
            result = await ScheduleRunner.RunAsync(settings, plans, true, (args, token) => args.Contains("--drm-source")
                ? Task.FromResult(1) : Invoke(args, token), CancellationToken.None);
            Probe.Check("a requested DRM refresh failure skips its downloads while unrelated jobs continue",
                result == 1 && commands.Count(c => c[0] == "download") == 3);
            commands.Clear(); syncFailures = "none"; downloadFailures = true;
            var cycles = 0;
            using var cancellation = new CancellationTokenSource();
            try
            {
                await ScheduleRunner.RunAsync(settings, plans, false, Invoke, cancellation.Token, (wait, token) =>
                {
                    Probe.Check("scheduled interval starts after completed jobs", commands.Last()[0] == "download"
                        && wait == TimeSpan.FromMinutes(settings.IntervalMinutes));
                    if (++cycles == 2) cancellation.Cancel();
                    return Task.CompletedTask;
                });
            }
            catch (OperationCanceledException) { }
            Probe.Check("download errors allow later jobs and future cycles; cancellation stops the scheduler",
                cycles == 2 && commands.Count(c => c[0] == "download") == 8);
            commands.Clear();
            using var activeCancellation = new CancellationTokenSource();
            try
            {
                await ScheduleRunner.RunAsync(settings, plans, true, async (args, token) =>
                {
                    if (args[0] != "download") return await Invoke(args, token);
                    commands.Add(args); activeCancellation.Cancel(); return 130;
                }, activeCancellation.Token);
            }
            catch (OperationCanceledException) { }
            Probe.Check("cancelling an active download never starts the next job", commands.Count(c => c[0] == "download") == 1
                && commands.All(c => Value(c, "--source") == "gog"));
            commands.Clear(); downloadFailures = false;
            result = await ScheduleRunner.RunAsync(settings, plans, true, (args, token) => args[0] == "download"
                && Value(args, "--source") == "gog" ? Task.FromResult(130) : Invoke(args, token), CancellationToken.None);
            Probe.Check("a command exit code cannot cancel unrelated jobs while the caller token is active",
                result == 1 && commands.Count(c => c[0] == "download") == 3);

            var downloaded = new List<(GameKey Key, ArchiveOptions Options)>();
            var integration = settings with { Jobs = [settings.Jobs[0], settings.Jobs[1] with { Search = null, DrmSource = null }] };
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(integration));
            result = await ArchiveCommandLine.RunAsync(["schedule", "--config", config, "--once"],
                storeFactory: source => [new Store(source, downloaded)]);
            Probe.Check("scheduled execution uses the real CLI's game, DRM and size selection with per-source destinations",
                result == 0 && downloaded.Select(d => d.Key.ToString()).SequenceEqual(["gog:1", "steam:1"])
                && downloaded.All(d => d.Options.OutputRoot == integration.Jobs.Single(j => j.Source == d.Key.Source).Output
                    && d.Options.StagingDirectory == integration.StagingDirectory));
            await CheckTimeoutsAsync(integration, config, downloaded);
            using var lockFile = new FileStream(Path.Combine(Path.GetDirectoryName(settings.Snapshot)!, "schedule.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var locked = false;
            try { await ScheduleRunner.RunAsync(settings, plans, true, Invoke, CancellationToken.None); }
            catch (IOException) { locked = true; }
            Probe.Check("two schedulers cannot run against the same state directory", locked);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Value(string[] args, string flag) => args[Array.IndexOf(args, flag) + 1];

    private static async Task CheckTimeoutsAsync(ScheduleSettings settings, string config,
        List<(GameKey Key, ArchiveOptions Options)> downloaded)
    {
        var error = Console.Error;
        using var diagnostics = new StringWriter();
        try
        {
            Console.SetError(diagnostics);
            foreach (var duringSync in new[] { true, false })
            {
                downloaded.Clear(); diagnostics.GetStringBuilder().Clear();
                var code = await ArchiveCommandLine.RunAsync(["schedule", "--config", config, "--once"],
                    storeFactory: source => [new Store(source, downloaded,
                        beforeSync: source == "gog" && duringSync ? HttpTimeoutAsync : null,
                        beforeDownload: source == "gog" && !duringSync ? HttpTimeoutAsync : null)]);
                Probe.Check($"HTTP timeout during {(duringSync ? "sync" : "download")} fails the cycle but still downloads from Steam",
                    code == 1 && downloaded.Select(d => d.Key.ToString()).SequenceEqual(["steam:1"])
                    && diagnostics.ToString().Contains("Operation timed out")
                    && !diagnostics.ToString().Contains("Interrupted") && !diagnostics.ToString().Contains("Scheduler stopped"));
            }

            downloaded.Clear();
            var waits = 0; var attempts = 0;
            using var cancellation = new CancellationTokenSource();
            async Task FailOnceAsync(CancellationToken token)
            { if (++attempts == 1) await HttpTimeoutAsync(token); }
            try
            {
                await ScheduleRunner.RunAsync(settings, ArchiveCommandLine.BuildSchedule(settings), false,
                    (command, token) => ArchiveCommandLine.RunAsync(command, token,
                        storeFactory: source => [new Store(source, downloaded, beforeSync: source == "gog" ? FailOnceAsync : null)]),
                    cancellation.Token, (interval, token) =>
                    {
                        waits++;
                        Probe.Check("a timeout cycle completes other jobs before waiting the configured interval",
                            interval == TimeSpan.FromMinutes(settings.IntervalMinutes)
                            && downloaded.Last().Key.Source == "steam" && attempts == waits);
                        if (waits == 2) cancellation.Cancel();
                        return Task.CompletedTask;
                    });
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Probe.Check("a timed-out source recovers on the next scheduled cycle without starving Steam",
                waits == 2 && downloaded.Select(d => d.Key.ToString()).SequenceEqual(["steam:1", "gog:1", "steam:1"]));

            downloaded.Clear(); diagnostics.GetStringBuilder().Clear();
            using var interrupted = new CancellationTokenSource();
            var syncedSources = new List<string>();
            var interruptedCode = await ArchiveCommandLine.RunAsync(["schedule", "--config", config, "--once"], interrupted.Token,
                storeFactory: source => [new Store(source, downloaded, beforeSync: token =>
                {
                    syncedSources.Add(source); interrupted.Cancel(); token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                })]);
            Probe.Check("real caller cancellation still returns 130 and stops before the next source",
                interruptedCode == 130 && downloaded.Count == 0 && syncedSources.SequenceEqual(["gog"])
                && diagnostics.ToString().Contains("Scheduler stopped") && !diagnostics.ToString().Contains("Operation timed out"));
        }
        finally { Console.SetError(error); }
    }

    private static async Task HttpTimeoutAsync(CancellationToken ct)
    {
        // Exercise HttpClient's actual timeout exception without network access or credentials.
        using var http = new HttpClient(new WaitingHandler()) { Timeout = TimeSpan.FromMilliseconds(50) };
        using var response = await http.GetAsync("https://timeout.invalid/", ct);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("The fixture request must time out.");
        }
    }

    private sealed class Store(string source, List<(GameKey Key, ArchiveOptions Options)> downloaded,
        Func<CancellationToken, Task>? beforeSync = null, Func<CancellationToken, Task>? beforeDownload = null) : IGameStore
    {
        public string Source => source;
        public async Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default)
        {
            if (beforeSync is not null) await beforeSync(ct);
            return new(Enumerable.Range(1, source == "gog" ? 1 : 4).Select(i => new GameInfo(new(source, i.ToString()), "Game " + i, "game_" + i)
            {
                Kind = i == 4 ? "application" : "game",
                Drm = new() { [GameOs.Windows] = new() { Status = i == 2 ? DrmStatus.Protected : DrmStatus.Free, Source = "fixture" } },
                Versions = new() { [GameOs.Windows] = new("build") { SizeEstimateRevision = 1, DownloadSize = i == 3 ? 200_000_000 : 100 } },
            }).ToList());
        }
        public Task<GameVersion> GetVersionAsync(string productId, GameSelection options, CancellationToken ct = default)
            => Task.FromResult(new GameVersion("build"));
        public async Task<ArchiveResult> DownloadAsync(string productId, ArchiveOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
        {
            if (beforeDownload is not null) await beforeDownload(ct);
            var key = new GameKey(source, productId); downloaded.Add((key, options));
            return new(key, ArchiveOutcome.AlreadyDone);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
