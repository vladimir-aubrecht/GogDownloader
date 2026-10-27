namespace GameArchiver.Core;

public enum DrmTestOutcome { RunningWithoutSteam, SteamObserved, Exited, Unavailable }
public enum DrmTestConfirmation { Unknown, PlayableWithoutSteam, SteamRequired }

/// <summary>Automatic local process observations, scoped to the tested content.</summary>
public sealed record DrmTestRecord
{
    public required GameKey Key { get; init; }
    public required GameOs Platform { get; init; }
    public required GameVersion Version { get; init; }
    public required string Fingerprint { get; init; }
    public required string ArchiveName { get; init; }
    public required string Executable { get; init; }
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string Machine { get; init; } = Environment.MachineName;
    public DateTimeOffset TestedAt { get; init; } = DateTimeOffset.UtcNow;
    public double ObservedSeconds { get; init; }
    public DrmTestOutcome Outcome { get; init; }
    /// <summary>Zero identifies historical observation-only/interactive tests.</summary>
    public int RuleRevision { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public DrmTestConfirmation Confirmation { get; init; }
    public List<string> SteamProcesses { get; init; } = [];
    public string? Detail { get; init; }
    public bool AppliesToCatalog { get; init; }

    internal bool IsValid => Key is not null && Enum.IsDefined(Platform) && Version is not null && Version.IsValid
        && Fingerprint is { Length: 64 } && Fingerprint.All(char.IsAsciiHexDigit) && !string.IsNullOrWhiteSpace(ArchiveName)
        && Executable is not null && Arguments is not null && WorkingDirectory is not null && Machine is not null
        && double.IsFinite(ObservedSeconds) && ObservedSeconds >= 0 && Enum.IsDefined(Outcome) && Enum.IsDefined(Confirmation)
        && RuleRevision is 0 or 1
        && SteamProcesses is not null && SteamProcesses.All(p => !string.IsNullOrWhiteSpace(p))
        && (Confirmation != DrmTestConfirmation.PlayableWithoutSteam || Outcome is not DrmTestOutcome.SteamObserved and not DrmTestOutcome.Unavailable
            && SteamProcesses.Count == 0)
        && (Confirmation == DrmTestConfirmation.Unknown || Outcome != DrmTestOutcome.Unavailable)
        && (RuleRevision == 0 || Confirmation == DrmTestConfirmation.Unknown
            && (Outcome != DrmTestOutcome.SteamObserved || SteamProcesses.Count > 0)
            && (Outcome != DrmTestOutcome.RunningWithoutSteam || SteamProcesses.Count == 0));

    public DrmInfo Classification => new()
    {
        Status = RuleRevision == 1 ? Outcome switch
        {
            DrmTestOutcome.SteamObserved => DrmStatus.Protected,
            DrmTestOutcome.RunningWithoutSteam => DrmStatus.Free,
            _ => DrmStatus.Unknown,
        } : Confirmation switch
        {
            DrmTestConfirmation.PlayableWithoutSteam => DrmStatus.Free,
            DrmTestConfirmation.SteamRequired => DrmStatus.Protected,
            _ => DrmStatus.Unknown,
        },
        Source = RuleRevision == 1 ? "local-process-test" : "local-verification",
        Branch = Version.Branch, CheckedAt = TestedAt, TestedVersion = Version,
        Notes = (RuleRevision == 1 ? "Automatic process heuristic" : $"Legacy confirmation: {Confirmation}")
            + $"; observed {Outcome} for {ObservedSeconds:F1}s on {Machine}. Build {Version.BuildId}; "
            + $"launch: {Executable}. Not a clean-machine or offline-play guarantee. {Detail}",
    };
}
