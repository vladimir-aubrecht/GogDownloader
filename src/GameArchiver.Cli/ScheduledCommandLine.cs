using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameArchiver.Core;

namespace GameArchiver.Cli;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ScheduleSettings
{
    public int IntervalMinutes { get; init; } = 360;
    public string Snapshot { get; init; } = ArchiveLibrary.DefaultPath;
    public string StagingDirectory { get; init; } = new ArchiveOptions { OutputRoot = "." }.StagingDirectory;
    public required List<ScheduledDownload> Jobs { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ScheduledDownload
{
    public required string Name { get; init; }
    public required string Source { get; init; }
    public required string Output { get; init; }
    // Explicit opt-in prevents a missing or misspelled selection from downloading a whole library.
    public bool All { get; init; }
    public List<string> Products { get; init; } = [];
    public string? Search { get; init; }
    public string? MaxSize { get; init; }
    public string? Drm { get; init; }
    public string? DrmSource { get; init; }
    public bool IncludeNonGames { get; init; }
    public string Platform { get; init; } = "windows";
    public string Language { get; init; } = "en";
    public string Architecture { get; init; } = "64";
    public string Branch { get; init; } = "public";
    public string Layout { get; init; } = "flat";
    public Dictionary<string, string> ProviderDirectories { get; init; } = [];
    public int Connections { get; init; } = 5;
    public int CompressionThreads { get; init; } = Environment.ProcessorCount;
    public string LimitRate { get; init; } = "0";
}

internal sealed record SchedulePlan(string Name, string Source, string? DrmSource, IReadOnlyList<string[]> Downloads);

public static partial class ArchiveCommandLine
{
    private static async Task<int> RunScheduleAsync(string[] args, Func<string[], CancellationToken, Task<int>> invoke, CancellationToken ct)
    {
        string? config = null;
        var once = false; var dryRun = false;
        for (var i = 1; i < args.Length; i++)
            switch (args[i])
            {
                case "--config" when config is null && i + 1 < args.Length: config = args[++i]; break;
                case "--once" when !once: once = true; break;
                case "--dry-run" when !dryRun: dryRun = true; break;
                default: throw new ArgumentException("Usage: schedule --config FILE [--once | --dry-run]");
            }
        if (string.IsNullOrWhiteSpace(config)) throw new ArgumentException("Specify schedule --config FILE.");
        var settings = JsonSerializer.Deserialize<ScheduleSettings>(await File.ReadAllTextAsync(config, ct),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Empty schedule configuration.");
        var plans = BuildSchedule(settings);
        if (dryRun)
        {
            Console.WriteLine($"Schedule: {plans.Count} jobs, {settings.IntervalMinutes} minutes after each completed cycle.");
            foreach (var plan in plans)
            {
                Console.WriteLine($"Job {plan.Name}:");
                Console.WriteLine("  " + JsonSerializer.Serialize(ScheduleRunner.SyncCommand(settings, plan.Source)));
                if (plan.DrmSource is not null) Console.WriteLine("  " + JsonSerializer.Serialize(ScheduleRunner.DrmCommand(settings, plan)));
                foreach (var command in plan.Downloads) Console.WriteLine("  " + JsonSerializer.Serialize(command));
            }
            return 0;
        }
        return await ScheduleRunner.RunAsync(settings, plans, once, invoke, ct);
    }

    internal static IReadOnlyList<SchedulePlan> BuildSchedule(ScheduleSettings settings)
    {
        if (settings.IntervalMinutes is < 1 or > 10080) throw new ArgumentException("intervalMinutes must be between 1 and 10080.");
        RequireAbsolute(settings.Snapshot, "snapshot");
        RequireAbsolute(settings.StagingDirectory, "stagingDirectory");
        if (settings.Jobs is not { Count: > 0 }) throw new ArgumentException("Configure at least one scheduled job.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<SchedulePlan>();
        foreach (var job in settings.Jobs)
        {
            if (job is null || string.IsNullOrWhiteSpace(job.Name) || !names.Add(job.Name))
                throw new ArgumentException("Each job needs a unique, nonempty name.");
            if (job.Source is not "gog" and not "steam") throw new ArgumentException($"Job {job.Name}: source must be gog or steam; use separate jobs for each source.");
            RequireAbsolute(job.Output, $"Job {job.Name}: output");
            if (job.Products is null || job.All == (job.Products.Count > 0))
                throw new ArgumentException($"Job {job.Name}: select either all: true or a nonempty products list.");
            if (!job.All && (job.Search is not null || job.MaxSize is not null || job.Drm is not null || job.IncludeNonGames))
                throw new ArgumentException($"Job {job.Name}: search, maxSize, drm and includeNonGames require all: true.");
            if (job.Search is not null && (string.IsNullOrWhiteSpace(job.Search) || job.Search.StartsWith("--")))
                throw new ArgumentException($"Job {job.Name}: invalid search.");
            if (job.Products.Any(p => string.IsNullOrWhiteSpace(p) || p.StartsWith("--")
                    || p.Contains(':') && (!p.StartsWith(job.Source + ":", StringComparison.Ordinal) || p.Split(':').Length != 2
                        || p[(p.IndexOf(':') + 1)..].Length == 0 || !p[(p.IndexOf(':') + 1)..].All(char.IsAsciiDigit))))
                throw new ArgumentException($"Job {job.Name}: products must be IDs or slugs from the selected source.");
            if (job.DrmSource is not null and not "pcgamingwiki" and not "steam-curator")
                throw new ArgumentException($"Job {job.Name}: drmSource must be pcgamingwiki or steam-curator.");
            if (job.MaxSize is not null) _ = ParseSizeLimit(job.MaxSize);
            if (job.Drm is not null and not "any") _ = DrmCatalog.Parse(job.Drm);
            var flags = new Dictionary<string, string>
            {
                ["source"] = job.Source, ["snapshot"] = settings.Snapshot, ["out"] = job.Output,
                ["staging"] = settings.StagingDirectory, ["platform"] = job.Platform, ["language"] = job.Language,
                ["arch"] = job.Architecture, ["branch"] = job.Branch, ["layout"] = job.Layout,
                ["connections"] = job.Connections.ToString(CultureInfo.InvariantCulture),
                ["compression-threads"] = job.CompressionThreads.ToString(CultureInfo.InvariantCulture), ["limit-rate"] = job.LimitRate,
            };
            if (flags.Values.Any(v => string.IsNullOrWhiteSpace(v) || v.StartsWith("--")) || job.ProviderDirectories is null)
                throw new ArgumentException($"Job {job.Name}: options must contain nonempty values, not CLI flags.");
            ProviderOutput.Validate(Options(flags, job.ProviderDirectories), [job.Source]);
            var common = flags.SelectMany(pair => new[] { "--" + pair.Key, pair.Value }).ToList();
            foreach (var pair in job.ProviderDirectories) common.AddRange(["--provider-dir", $"{pair.Key}={pair.Value}"]);
            if (job.MaxSize is not null) common.AddRange(["--max-size", job.MaxSize]);
            if (job.Drm is not null) common.AddRange(["--drm", job.Drm]);
            if (job.IncludeNonGames) common.Add("--include-non-games");
            var downloads = job.All
                ? new List<string[]> { new[] { "download", "--all" }.Concat(job.Search is null ? [] : new[] { job.Search }).Concat(common).ToArray() }
                : job.Products.Distinct(StringComparer.Ordinal).Select(p => new[] { "download", p }.Concat(common).ToArray()).ToList();
            plans.Add(new(job.Name, job.Source, job.DrmSource, downloads));
        }
        return plans;
    }

    private static void RequireAbsolute(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException($"{label} must be an absolute path.");
    }
}

internal static class ScheduleRunner
{
    internal static string[] SyncCommand(ScheduleSettings settings, string source)
        => ["library", "sync", "--source", source, "--snapshot", settings.Snapshot];
    internal static string[] DrmCommand(ScheduleSettings settings, SchedulePlan plan)
        => ["library", "drm", "sync", "--source", plan.Source, "--snapshot", settings.Snapshot, "--drm-source", plan.DrmSource!];

    internal static async Task<int> RunAsync(ScheduleSettings settings, IReadOnlyList<SchedulePlan> plans, bool once,
        Func<string[], CancellationToken, Task<int>> invoke, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var state = Path.GetDirectoryName(Path.GetFullPath(settings.Snapshot))!;
        Directory.CreateDirectory(state);
        // Keep the inode: deleting lock files can let two processes lock different files.
        using var lease = new FileStream(Path.Combine(state, "schedule.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Log("Starting scheduled cycle.");
            var failed = false;
            var synced = new Dictionary<string, bool>();
            var enrichment = new Dictionary<string, (string Source, bool Ready)>();
            async Task<int> Execute(string[] command)
            {
                ct.ThrowIfCancellationRequested();
                var exit = await invoke(command, ct);
                ct.ThrowIfCancellationRequested();
                if (exit == 130) throw new OperationCanceledException("Scheduled command was interrupted.", ct);
                if (exit != 0) { failed = true; Log($"Command {command[0]} finished with exit code {exit}; see diagnostics above."); }
                return exit;
            }
            foreach (var plan in plans)
            {
                ct.ThrowIfCancellationRequested();
                Log($"Job {plan.Name} ({plan.Source}).");
                if (!synced.ContainsKey(plan.Source))
                {
                    var started = DateTimeOffset.UtcNow;
                    var exit = await Execute(SyncCommand(settings, plan.Source));
                    // Steam can save a useful fresh catalog while reporting failures for inventory
                    // products. Continue those downloads, but never use a stale/source-failed snapshot.
                    var ready = false;
                    try
                    {
                        var library = await ArchiveLibrary.LoadAsync(settings.Snapshot, ct);
                        ready = library.SyncedAt >= started && !library.Failures.ContainsKey(plan.Source);
                    }
                    catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
                    { Log($"Cannot use refreshed snapshot: {e.Message}"); }
                    synced[plan.Source] = ready;
                    if (ready && exit != 0) Log("Sync saved fresh metadata with product-level failures; continuing available downloads.");
                }
                if (!synced[plan.Source])
                { failed = true; Log("Skipping downloads: this source has no fresh successful catalog. It will be retried next cycle."); continue; }
                if (plan.DrmSource is not null)
                {
                    // A later job may choose another community source, replacing saved reports.
                    // Recheck that the reports still belong to this job's requested source.
                    if (!enrichment.TryGetValue(plan.Source, out var current) || current.Source != plan.DrmSource)
                        enrichment[plan.Source] = (plan.DrmSource, await Execute(DrmCommand(settings, plan)) == 0);
                    if (!enrichment[plan.Source].Ready)
                    { Log("Skipping downloads: the requested DRM refresh failed. It will be retried next cycle."); continue; }
                }
                foreach (var command in plan.Downloads) await Execute(command);
            }
            Log($"Cycle finished{(failed ? " with errors" : " successfully")}.");
            if (once) return failed ? 1 : 0;
            Log($"Next cycle in {settings.IntervalMinutes} minutes.");
            await (delay ?? Task.Delay)(TimeSpan.FromMinutes(settings.IntervalMinutes), ct);
        }
    }

    private static void Log(string text) => Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:u}] {text}");
}
