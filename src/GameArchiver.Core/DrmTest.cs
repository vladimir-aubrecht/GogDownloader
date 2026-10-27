namespace GameArchiver.Core;

public enum DrmTestOutcome { RunningWithoutSteam, SteamObserved, Exited, Unavailable }
public enum DrmTestConfirmation { Unknown, PlayableWithoutSteam, SteamRequired }

/// <summary>Local observations and optional human confirmation, scoped to the tested content.</summary>
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
    public DrmTestConfirmation Confirmation { get; init; }
    public List<string> SteamProcesses { get; init; } = [];
    public string? Detail { get; init; }
    public bool AppliesToCatalog { get; init; }

    internal bool IsValid => Key is not null && Enum.IsDefined(Platform) && Version is not null && Version.IsValid
        && Fingerprint is { Length: 64 } && Fingerprint.All(char.IsAsciiHexDigit) && !string.IsNullOrWhiteSpace(ArchiveName)
        && Executable is not null && Arguments is not null && WorkingDirectory is not null && Machine is not null
        && double.IsFinite(ObservedSeconds) && ObservedSeconds >= 0 && Enum.IsDefined(Outcome) && Enum.IsDefined(Confirmation)
        && SteamProcesses is not null && SteamProcesses.All(p => !string.IsNullOrWhiteSpace(p))
        && (Confirmation != DrmTestConfirmation.PlayableWithoutSteam || Outcome is not DrmTestOutcome.SteamObserved and not DrmTestOutcome.Unavailable
            && SteamProcesses.Count == 0)
        && (Confirmation == DrmTestConfirmation.Unknown || Outcome != DrmTestOutcome.Unavailable);

    public DrmInfo Classification => new()
    {
        Status = Confirmation switch
        {
            DrmTestConfirmation.PlayableWithoutSteam => DrmStatus.Free,
            DrmTestConfirmation.SteamRequired => DrmStatus.Protected,
            _ => DrmStatus.Unknown,
        },
        Source = "local-verification", Branch = Version.Branch, CheckedAt = TestedAt, TestedVersion = Version,
        Notes = $"{Confirmation}; observed {Outcome} for {ObservedSeconds:F1}s on {Machine}. Build {Version.BuildId}; "
            + $"launch: {Executable}. Local launch evidence, not a clean-machine or offline-play guarantee. {Detail}",
    };
}
