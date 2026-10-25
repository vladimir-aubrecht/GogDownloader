using System.Text;
using System.Text.RegularExpressions;

namespace GameArchiver;

/// <summary>Fits a unique local-edit name into a portable filesystem component.</summary>
internal static class EditedArchiveName
{
    // UTF-8's 255-byte component limit also stays within Windows' 255 UTF-16 code units.
    private const int MaxBytes = 255;

    public static string Create(string original)
    {
        var stem = Regex.Replace(Path.GetFileNameWithoutExtension(original), @" \(local-[a-f0-9]{32}\)$", "");
        var suffix = " (local-" + Guid.NewGuid().ToString("N") + ").zip";
        var budget = MaxBytes - Encoding.UTF8.GetByteCount(suffix);
        if (Encoding.UTF8.GetByteCount(stem) <= budget) return stem + suffix;

        // Keep trailing GameVault tags balanced and retain the game type. Shorten the title
        // first; unusually long version labels may also need shortening to leave a title.
        var parts = Regex.Match(stem, @"^(?<title>.*?)(?<tags>(?: \([^)]*\))*)$", RegexOptions.Singleline);
        var title = parts.Groups["title"].Value;
        var tags = Regex.Matches(parts.Groups["tags"].Value, @" \(([^)]*)\)")
            .Select(m => m.Groups[1].Value).ToList();
        if (title.Length == 0) title = "Game";
        // Keep short titles recognizable even when the version label alone is very long.
        var minimumTitle = Encoding.UTF8.GetByteCount(Prefix(title, 32));
        int TagBytes() => tags.Sum(t => Encoding.UTF8.GetByteCount(t) + 3);
        while (TagBytes() > budget - minimumTitle)
        {
            var candidate = tags.Select((text, index) => (Text: text, Index: index, Bytes: Encoding.UTF8.GetByteCount(text)))
                .Where(t => !FixedTag(t.Text) && t.Bytes > MinimumTagBytes(t.Text))
                .OrderByDescending(t => t.Bytes).FirstOrDefault();
            if (candidate.Text is null)
            {
                // Many fixed tags may leave room for less than the preferred title prefix.
                var firstRuneBytes = title.EnumerateRunes().First().Utf8SequenceLength;
                if (minimumTitle > firstRuneBytes) { minimumTitle = firstRuneBytes; continue; }
                throw new PathTooLongException("Archive tags leave no room for a unique local-edit filename.");
            }
            var excess = TagBytes() - (budget - minimumTitle);
            tags[candidate.Index] = Prefix(candidate.Text, Math.Max(MinimumTagBytes(candidate.Text), candidate.Bytes - excess));
        }
        return Prefix(title, budget - TagBytes()) + string.Concat(tags.Select(t => " (" + t + ")")) + suffix;
    }

    private static bool FixedTag(string tag) => tag is "EA" or "NC" or "W_P" or "W_S" or "W_SW" or "L_P" or "L_SW"
        || tag.Length == 4 && tag.All(char.IsAsciiDigit);

    // A version needs both 'v' and a value to remain recognizable to GameVault.
    private static int MinimumTagBytes(string tag) => tag.StartsWith('v')
        ? tag.EnumerateRunes().Take(2).Sum(r => r.Utf8SequenceLength) : 0;

    private static string Prefix(string value, int bytes)
    {
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > bytes) break;
            result.Append(rune.ToString()); bytes -= rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}
