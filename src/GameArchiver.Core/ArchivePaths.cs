using System.Text;

namespace GameArchiver.Core;

public sealed class ArchiveConflictException(string message) : IOException(message);

public static class ArchivePaths
{
    public static void ValidateName(string name)
    {
        var stem = name.Split('.')[0].ToLowerInvariant();
        if (string.IsNullOrEmpty(name) || name.StartsWith('.') || name.EndsWith('.')
            || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.')
            || stem is "con" or "prn" or "aux" or "nul"
            || (stem.Length == 4 && (stem.StartsWith("com") || stem.StartsWith("lpt")) && stem[3] is >= '1' and <= '9'))
            throw new ArchiveConflictException($"Unsafe directory name '{name}'.");
    }

    public static string Slug(string title)
    {
        var value = new StringBuilder();
        foreach (var c in title.Normalize(NormalizationForm.FormD).ToLowerInvariant())
            if (char.IsAsciiLetterOrDigit(c)) value.Append(c);
            else if (value.Length > 0 && value[^1] != '_') value.Append('_');
        var result = value.ToString().Trim('_');
        if (result.Length == 0) result = "game";
        try { ValidateName(result); } catch (ArchiveConflictException) { result = "game_" + result; }
        return result;
    }

    public static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Empty content path.");
        var normalized = path.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':')
            || normalized.Split('/').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException($"Unsafe content path '{path}'.");
        return normalized;
    }

    public static string Within(string root, string relative)
    {
        var safe = Relative(relative);
        var path = Path.GetFullPath(Path.Combine(root, safe));
        for (var ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            if (ancestor == Path.GetFullPath(root)) break;
            if ((File.Exists(ancestor) || Directory.Exists(ancestor))
                && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new ArchiveConflictException($"Content path crosses a link: {ancestor}");
        }
        return path;
    }
}
