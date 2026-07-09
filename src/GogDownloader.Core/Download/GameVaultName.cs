using System.Text;
using System.Text.RegularExpressions;
using GogDownloader.Core.Library;

namespace GogDownloader.Core.Download;

/// <summary>
/// Builds archive names GameVault can index: `Title (Version) (EA) (GameType).zip`.
/// Verified against gamevault-backend `files.service.ts` (f95cc9f).
///
/// The release year is deliberately absent. GOG's `release_date` is not the year the game
/// came out — The Witcher 2 reports 1991 and Akalabeth (1979) reports 2014 — and GameVault
/// feeds the year to its metadata matcher, so a wrong one is worse than none.
/// </summary>
public static partial class GameVaultName
{
    /// <summary>GameVault's indexer rejects any filename containing these.</summary>
    [GeneratedRegex("""[/<>:"\\|?*]""")]
    private static partial Regex ForbiddenCharacters { get; }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace { get; }

    /// <summary>Device names Windows refuses regardless of extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string For(LibraryEntry game, GamePlatform platform, string? versionName)
    {
        var name = new StringBuilder(Title(game.Title));

        if (Version(versionName) is { } version) name.Append($" ({version})");
        if (game.EarlyAccess) name.Append(" (EA)");
        if (GameType(platform) is { } type) name.Append($" ({type})");

        return name.Append(".zip").ToString();
    }

    /// <summary>
    /// GameVault derives the title by stripping everything in round brackets, so a title that
    /// contains them must not keep them. Square brackets are the documented substitute.
    /// </summary>
    internal static string Title(string title)
    {
        var cleaned = title.Replace(":", " -").Replace('/', '-').Replace('\\', '-');
        cleaned = ForbiddenCharacters.Replace(cleaned, "");
        cleaned = cleaned.Replace('(', '[').Replace(')', ']');
        cleaned = Whitespace.Replace(cleaned, " ").Trim();

        // A trailing dot or space makes a filename unopenable on Windows.
        cleaned = cleaned.TrimEnd('.', ' ');

        if (cleaned.Length == 0) return "Untitled";
        return ReservedNames.Contains(cleaned) ? cleaned + " game" : cleaned;
    }

    /// <summary>
    /// GameVault matches `\((v[^)]+)\)`, so the flag must start with a lowercase `v` and carry
    /// no closing bracket. Null when GOG gave us nothing usable.
    /// </summary>
    internal static string? Version(string? versionName)
    {
        if (versionName is null) return null;

        var cleaned = ForbiddenCharacters.Replace(versionName, "");
        cleaned = cleaned.Replace("(", "").Replace(")", "");
        cleaned = Whitespace.Replace(cleaned, " ").Trim().TrimStart('v', 'V');

        return cleaned.Length == 0 ? null : "v" + cleaned;
    }

    /// <summary>
    /// Depot downloads are the installed game, so they never need a setup step. macOS has no
    /// flag in GameVault, which leaves the type undetected — that is the honest answer.
    /// </summary>
    internal static string? GameType(GamePlatform platform) => platform switch
    {
        GamePlatform.Windows => "W_P",
        GamePlatform.Linux => "L_P",
        _ => null,
    };
}
