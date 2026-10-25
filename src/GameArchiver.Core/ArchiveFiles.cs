using System.Text;

namespace GameArchiver.Core;

public enum ArchiveFileOperation { Find, Add, Replace }
public enum ArchiveFileMatch { Name, Path }

/// <summary>A local operation on completed archives; no store login or library snapshot is needed.</summary>
public sealed record ArchiveFileRequest
{
    public required ArchiveFileOperation Operation { get; init; }
    public string? FilePath { get; init; }
    public string? EntryPath { get; init; }
    public string? Match { get; init; }
    public ArchiveFileMatch MatchBy { get; init; } = ArchiveFileMatch.Name;
    public bool IgnoreCase { get; init; }
    public bool DryRun { get; init; }
    public string Search { get; init; } = "";
    public GameOs? Platform { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(Operation) || !Enum.IsDefined(MatchBy)
            || Platform is { } os && !Enum.IsDefined(os)) throw new ArgumentException("Invalid archive file operation or selection.");
        if (Operation != ArchiveFileOperation.Find && (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath)))
            throw new FileNotFoundException("Supply an existing source file.", FilePath);
        if (Operation == ArchiveFileOperation.Find && FilePath is not null)
            throw new ArgumentException("Find does not take a source file.");
        if (Operation == ArchiveFileOperation.Add)
        {
            if (Match is not null) throw new ArgumentException("Add uses an entry path, not a search pattern.");
            ValidatePath(Destination);
        }
        else
        {
            if (EntryPath is not null) throw new ArgumentException("EntryPath is only used by Add.");
            ValidatePath(Match ?? "");
            if (MatchBy == ArchiveFileMatch.Name && Match!.Replace('\\', '/').Contains('/'))
                throw new ArgumentException("A name match must be a filename; use a path match for directories.");
        }
    }

    public string Destination => Normalize(EntryPath ?? Path.GetFileName(FilePath!));
    internal static string Normalize(string path) => path.Replace('\\', '/');
    internal static void ValidatePath(string path)
    {
        _ = ArchivePaths.Relative(path);
        if (path.Any(char.IsControl) || Encoding.UTF8.GetByteCount(path) > ushort.MaxValue)
            throw new InvalidDataException("Invalid or excessively long ZIP entry path.");
    }
    public bool Matches(string entry) => !entry.EndsWith('/') && string.Equals(
        MatchBy == ArchiveFileMatch.Name ? entry[(entry.LastIndexOf('/') + 1)..] : entry,
        Normalize(Match ?? ""), IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>BackupPath is null for a new entry or a find result.</summary>
public sealed record ArchiveFileChange(string EntryPath, string? BackupPath = null);
public enum ArchiveFileStatus { Matched, Preview, Updated, NoMatch, Failed }
public sealed record ArchiveFileResult(GameKey Key, GameOs Platform, string ArchivePath,
    ArchiveFileStatus Status, IReadOnlyList<ArchiveFileChange> Changes, string? Detail = null);
