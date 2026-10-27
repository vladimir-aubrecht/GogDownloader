using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GameArchiver.Core;

namespace GameArchiver;

public sealed record DrmTestOptions
{
    public TimeSpan ObserveFor { get; init; } = TimeSpan.FromSeconds(60);
    public bool Retest { get; init; }
    public bool DryRun { get; init; }
    public bool IncludeNonGames { get; init; }
    public string Search { get; init; } = "";
    public string? GameDirectory { get; init; }
    public string? Executable { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
}

public sealed record DrmTestLaunch(string Root, string Executable, string Arguments, string WorkingDirectory);
public sealed record DrmTestBatchResult(ArchiveLibrary Library, int Tested, int Skipped, string? StoppedReason, int Unavailable = 0);

/// <summary>Injectable native process boundary. Disposal must stop every process started by the test.</summary>
public interface IDrmTestProcess : IDisposable { bool IsRunning { get; } }
public interface IDrmTestRuntime
{
    bool IsSupported { get; }
    IReadOnlyList<string> GetSteamProcesses();
    IDrmTestProcess Start(DrmTestLaunch launch);
    /// <summary>Stop only Steam clients started during this runtime's last launch; never stop a preexisting client.</summary>
    Task<IReadOnlyList<string>> StopStartedSteamAsync(CancellationToken ct);
}

public sealed partial class GameArchiverClient
{
    /// <summary>Tests downloaded archives sequentially. Checkpoint is awaited after each complete result.
    /// Ctrl+C leaves the current test unrecorded, so repeating the same request resumes it.</summary>
    public async Task<DrmTestBatchResult> TestDrmAsync(ArchiveLibrary library, ArchiveOptions options, DrmTestOptions request,
        Func<ArchiveLibrary, CancellationToken, Task> checkpoint, IDrmTestRuntime? runtime = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        options.Validate();
        if (options.Platform != GameOs.Windows) throw new NotSupportedException("Local DRM tests currently support Windows archives on Windows.");
        if (request.ObserveFor < TimeSpan.FromSeconds(1) || request.ObserveFor > TimeSpan.FromHours(1))
            throw new ArgumentException("Observation time must be between 1 and 3600 seconds.");
        runtime ??= new WindowsDrmTestRuntime();
        if (!request.DryRun && !runtime.IsSupported) throw new NotSupportedException("Local DRM tests currently require Windows. Use --dry-run to preview elsewhere.");
        using var runtimeLease = !request.DryRun && runtime is WindowsDrmTestRuntime ? WindowsDrmTestRuntime.AcquireLease() : null;
        var locations = request.GameDirectory is null ? await ScanAsync(options, ct)
            : new List<ArchiveLocation> { new(Path.GetFullPath(request.GameDirectory),
                await ArchiveMetadata.LoadAsync(request.GameDirectory, ct) ?? throw new InvalidDataException("No game metadata in this directory.")) };
        locations = locations.Where(l => _stores.ContainsKey(l.Metadata.Source)
            && l.Metadata.Platforms.ContainsKey(options.Platform)
            && (request.Search.Length == 0 || l.Metadata.Key.ToString().Equals(request.Search, StringComparison.OrdinalIgnoreCase)
                || l.Metadata.Slug.Contains(request.Search, StringComparison.OrdinalIgnoreCase)
                || l.Metadata.Title.Contains(request.Search, StringComparison.OrdinalIgnoreCase))
            && (request.IncludeNonGames || library.Games.FirstOrDefault(g => g.Key == l.Metadata.Key)?.Kind is null or "game" or "pack")).ToList();
        if (locations.GroupBy(l => l.Metadata.Key).Any(g => g.Count() > 1))
            throw new ArchiveConflictException("Multiple backup directories have the same identity.");
        if ((request.Executable is not null || request.Arguments is not null || request.WorkingDirectory is not null) && locations.Count != 1)
            throw new ArgumentException("Launch overrides require exactly one matching archived game.");
        if (request.GameDirectory is not null && locations.Count == 0) throw new ArgumentException("The current game is outside the selected source/platform or is not a game.");
        var result = library with { DrmTests = library.DrmTests.ToList() };
        int tested = 0, skipped = 0, unavailable = 0;
        foreach (var location in locations)
        {
            ct.ThrowIfCancellationRequested();
            using var lease = request.DryRun ? null : await ArchiveCatalog.AcquireOwnedAsync(location, location.Metadata.Key, ct);
            var current = lease?.Location ?? location;
            var backup = current.Metadata.Platforms[options.Platform];
            var path = ArchivePaths.Within(current.Directory, backup.ArchiveName);
            var zipFile = new FileInfo(path);
            if (!zipFile.Exists || zipFile.Length != backup.ArchiveSize)
            { progress?.Report($"Unavailable {current.Metadata.Key}: archive missing or size differs from metadata."); unavailable++; continue; }
            var executable = request.Executable ?? backup.LaunchExecutable ?? "";
            var arguments = request.Arguments ?? backup.LaunchParameters ?? "";
            var workingDirectory = request.WorkingDirectory ?? "";
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                TestRevision = 2, Environment.MachineName,
                current.Metadata.Key, options.Platform, backup.Version, backup.ArchiveName, backup.ArchiveSize,
                backup.ModifiedAt, backup.NeedsRefresh, zipFile.LastWriteTimeUtc, executable, arguments, workingDirectory,
                Seconds = request.ObserveFor.TotalSeconds,
            }, ArchiveJson.Options)));
            if (!request.Retest && result.DrmTests.Any(t => t.RuleRevision == 1 && t.Key == current.Metadata.Key && t.Platform == options.Platform && t.Fingerprint == fingerprint))
            { progress?.Report($"AlreadyTested {current.Metadata.Title} ({current.Metadata.Key}); use --retest to repeat."); skipped++; continue; }
            progress?.Report($"{(request.DryRun ? "WouldTest" : "Testing")} {current.Metadata.Title} ({current.Metadata.Key}): {(executable.Length == 0 ? "no recorded launcher" : executable)}");
            if (request.DryRun) { skipped++; continue; }
            if (runtime.GetSteamProcesses().Count > 0)
                return new(result, tested, skipped, "Close Steam and its web helpers, then rerun. Steam was already running; no test started.", unavailable);
            var record = new DrmTestRecord
            {
                Key = current.Metadata.Key, Platform = options.Platform, Version = backup.Version,
                Fingerprint = fingerprint, ArchiveName = backup.ArchiveName, Executable = executable,
                Arguments = arguments, WorkingDirectory = workingDirectory,
                RuleRevision = 1,
                AppliesToCatalog = backup.ModifiedAt is null && !backup.NeedsRefresh,
            };
            string? stop = null;
            if (executable.Length == 0)
                record = record with { Outcome = DrmTestOutcome.Unavailable, Detail = "No launcher in metadata. Select this game and use --executable RELATIVE_PATH." };
            else
            {
                // The scratch directory is unique per attempt. Never execute files in the backup
                // or in an old extraction that a previous game could have modified.
                var scratchRoot = Path.GetFullPath(Path.Combine(options.StagingDirectory, "drm-tests"));
                DrmTestExtraction.CheckAncestors(scratchRoot);
                Directory.CreateDirectory(scratchRoot);
                var scratch = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                var launchAttempted = false;
                try
                {
                    var launch = DrmTestExtraction.Launch(scratch, executable, arguments, workingDirectory);
                    await DrmTestExtraction.ExtractAsync(path, scratch, progress, ct);
                    if (runtime.GetSteamProcesses().Count > 0)
                        return new(result, tested, skipped, "Steam started during extraction. Close Steam and rerun; this game remains untested.", unavailable);
                    if (!File.Exists(launch.Executable) || !Directory.Exists(launch.WorkingDirectory))
                        throw new InvalidDataException("Recorded launcher or working directory is missing from the archive. Use a single-game launch override.");
                    launchAttempted = true;
                    using (var process = runtime.Start(launch))
                    {
                        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var watch = Stopwatch.StartNew();
                        void Sample() { foreach (var name in runtime.GetSteamProcesses()) observed.Add(name); }
                        Sample();
                        while (observed.Count == 0 && process.IsRunning && watch.Elapsed < request.ObserveFor)
                        { await Task.Delay(200, ct); Sample(); }
                        // A short-lived launcher can ask the shell to start Steam after it exits.
                        if (observed.Count == 0 && !process.IsRunning)
                            for (var i = 0; i < 10; i++) { await Task.Delay(200, ct); Sample(); }
                        var outcome = observed.Count > 0 ? DrmTestOutcome.SteamObserved
                            : process.IsRunning ? DrmTestOutcome.RunningWithoutSteam : DrmTestOutcome.Exited;
                        Sample(); ct.ThrowIfCancellationRequested();
                        if (observed.Count > 0) outcome = DrmTestOutcome.SteamObserved;
                        record = record with { Outcome = outcome, SteamProcesses = observed.ToList(),
                            ObservedSeconds = watch.Elapsed.TotalSeconds, TestedAt = DateTimeOffset.UtcNow,
                            Detail = "Automatic rule: Steam observed = protected; process tree running without Steam at the deadline = free; early exit or failure = unknown." };
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException or OverflowException)
                { record = record with { Outcome = DrmTestOutcome.Unavailable, Confirmation = DrmTestConfirmation.Unknown, Detail = e.Message }; }
                finally
                {
                    if (launchAttempted)
                    {
                        // Finish bounded cleanup even when the user interrupted observation.
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        try
                        {
                            var lateSteam = await runtime.StopStartedSteamAsync(cleanup.Token);
                            var remainingSteam = runtime.GetSteamProcesses();
                            if ((lateSteam.Count > 0 || remainingSteam.Count > 0) && record.Outcome != DrmTestOutcome.Unavailable)
                                record = record with { Outcome = DrmTestOutcome.SteamObserved,
                                    SteamProcesses = record.SteamProcesses.Concat(lateSteam).Concat(remainingSteam).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
                            if (remainingSteam.Count > 0) stop = "Steam is still running after cleanup. The batch was stopped; completed results were saved.";
                        }
                        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                        {
                            stop = "Steam cleanup failed; the batch was stopped: " + e.Message;
                            if (record.Outcome != DrmTestOutcome.SteamObserved)
                                record = record with { Outcome = DrmTestOutcome.Unavailable, Detail = stop };
                        }
                        if (stop is not null && ct.IsCancellationRequested) progress?.Report(stop);
                    }
                    // Native runtime disposal finishes before deleting extracted files.
                    try { Directory.Delete(scratch, recursive: true); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    { throw new IOException($"Could not remove test extraction '{scratch}'. Stop any remaining game process and remove it manually before retrying.", e); }
                }
            }
            ct.ThrowIfCancellationRequested();
            result.DrmTests.RemoveAll(t => t.Key == record.Key && t.Platform == record.Platform && t.Fingerprint == record.Fingerprint);
            result.DrmTests.Add(record);
            // Once a test is complete, finish its small atomic checkpoint even if Ctrl+C arrives.
            await checkpoint(result, CancellationToken.None);
            tested++;
            if (record.Outcome == DrmTestOutcome.Unavailable) unavailable++;
            progress?.Report($"Tested {current.Metadata.Title} ({record.Key}): {record.Classification.Status} ({record.Outcome}, automatic). {record.Detail}");
            if (stop is not null) return new(result, tested, skipped, stop, unavailable);
        }
        return new(result, tested, skipped, null, unavailable);
    }
}
