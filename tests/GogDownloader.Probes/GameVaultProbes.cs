using System.Text.RegularExpressions;
using GogDownloader.Core;
using GogDownloader.Core.Download;
using GogDownloader.Core.Library;

namespace GogDownloader.Probes;

/// <summary>
/// Names are checked against GameVault's own regexes, transcribed from
/// gamevault-backend `src/modules/games/files.service.ts` (f95cc9f). Asserting our output
/// against our own idea of the rules would prove nothing.
/// </summary>
public static class GameVaultProbes
{
    private static readonly Regex InvalidCharacters = new("""[/<>:"\\|?*]""");

    private static string IndexerTitle(string file) =>
        Regex.Replace(
            Regex.Replace(Path.GetFileNameWithoutExtension(file), @"\([^)]*\)", ""),
            @"\s+", " ").Trim();

    private static string? IndexerVersion(string file) =>
        Regex.Match(file, @"\((v[^)]+)\)") is { Success: true } m ? m.Groups[1].Value : null;

    private static bool IndexerEarlyAccess(string file) => Regex.IsMatch(file, @"\(EA\)");

    private static bool IndexerAccepts(string file) =>
        !InvalidCharacters.IsMatch(Path.GetFileName(file))
        && Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    private static LibraryEntry Game(string title, bool earlyAccess = false) =>
        new() { Id = 1, Title = title, Slug = "s", EarlyAccess = earlyAccess };

    public static void Run()
    {
        Probe.Group("GameVault archive names");

        var titanQuest = GameVaultName.For(Game("Titan Quest Anniversary Edition"), GamePlatform.Windows, "2.10.7");
        Probe.Note(titanQuest);
        Probe.Check("exact name", titanQuest == "Titan Quest Anniversary Edition (v2.10.7) (W_P).zip", titanQuest);
        Probe.Check("the indexer accepts it", IndexerAccepts(titanQuest));
        Probe.Check("the indexer recovers the title", IndexerTitle(titanQuest) == "Titan Quest Anniversary Edition");
        Probe.Check("the indexer recovers the version", IndexerVersion(titanQuest) == "v2.10.7");

        var colon = GameVaultName.For(Game("The Witcher 2: Assassins of Kings"), GamePlatform.Windows, "1.0");
        Probe.Note(colon);
        Probe.Check("a colon does not get the file rejected", IndexerAccepts(colon), colon);
        Probe.Check("the title stays readable",
            IndexerTitle(colon) == "The Witcher 2 - Assassins of Kings", IndexerTitle(colon));

        // The indexer strips every round-bracket group, so a bracketed title must not keep them.
        var bracketed = GameVaultName.For(Game("Aztec (Special)"), GamePlatform.Windows, null);
        Probe.Note(bracketed);
        Probe.Check("round brackets in a title survive as square ones",
            IndexerTitle(bracketed) == "Aztec [Special]", IndexerTitle(bracketed));
        Probe.Check("no version flag when GOG gives no version", !bracketed.Contains("(v"), bracketed);

        var earlyAccess = GameVaultName.For(Game("Some Roguelike", earlyAccess: true), GamePlatform.Windows, "0.9");
        Probe.Check("early access is flagged", IndexerEarlyAccess(earlyAccess));
        Probe.Check("flag order is Title (v) (EA) (Type)",
            earlyAccess == "Some Roguelike (v0.9) (EA) (W_P).zip", earlyAccess);

        Probe.Group("Cleaning titles for IGDB matching");
        // GameVault feeds the filename title to IGDB; trademark marks and GOTY suffixes miss.
        Probe.Check("trademark marks are stripped", GameVaultName.Title("Ultima™ 1+2+3") == "Ultima 1+2+3", GameVaultName.Title("Ultima™ 1+2+3"));
        Probe.Check("registered marks too", GameVaultName.Title("Heroes of Might and Magic®") == "Heroes of Might and Magic");
        Probe.Check("GOTY Edition is dropped", GameVaultName.Title("Deus Ex™ GOTY Edition") == "Deus Ex", GameVaultName.Title("Deus Ex™ GOTY Edition"));
        Probe.Check("Game of the Year Edition is dropped after a colon",
            GameVaultName.Title("Fallout - Game of the Year Edition") == "Fallout - Game of the Year Edition"
                ? false : GameVaultName.Title("Fallout - Game of the Year Edition") == "Fallout",
            GameVaultName.Title("Fallout - Game of the Year Edition"));
        Probe.Check("a dangling dash from the stripped suffix is cleaned",
            !GameVaultName.Title("Deus Ex: Game of the Year Edition").EndsWith("-"),
            GameVaultName.Title("Deus Ex: Game of the Year Edition"));

        // These editions are distinct games on IGDB — GOG keeps them in its slug — so keep them.
        Probe.Check("Enhanced Edition is kept",
            GameVaultName.Title("Baldur's Gate - Enhanced Edition").Contains("Enhanced Edition"));
        Probe.Check("Definitive Edition is kept",
            GameVaultName.Title("Divinity - Original Sin 2 - Definitive Edition").Contains("Definitive Edition"));
        Probe.Check("Anniversary Edition is kept",
            GameVaultName.Title("Titan Quest Anniversary Edition").Contains("Anniversary Edition"));
        Probe.Check("a plain title is untouched", GameVaultName.Title("Earth 2150 - Escape from the Blue Planet") == "Earth 2150 - Escape from the Blue Planet");
        Probe.Check("GOTY inside a word is not stripped", GameVaultName.Title("Goofy GOTYA") == "Goofy GOTYA", GameVaultName.Title("Goofy GOTYA"));
        Probe.Check("stripping GOTY out of brackets leaves no empty ones",
            GameVaultName.Title("Fallout 2 (GOTY)") == "Fallout 2", GameVaultName.Title("Fallout 2 (GOTY)"));

        Probe.Group("Game type flags");
        Probe.Check("windows depots are portable", GameVaultName.GameType(GamePlatform.Windows) == "W_P");
        Probe.Check("linux is portable too", GameVaultName.GameType(GamePlatform.Linux) == "L_P");
        Probe.Check("macOS has no flag in GameVault", GameVaultName.GameType(GamePlatform.Mac) is null);

        Probe.Group("Forbidden characters and reserved names");
        foreach (var bad in new[] { "a/b", "a<b", "a>b", "a:b", "a\"b", "a\\b", "a|b", "a?b", "a*b" })
        {
            var name = GameVaultName.For(Game("Game " + bad), GamePlatform.Windows, null);
            Probe.Check($"'{bad}' is sanitised", IndexerAccepts(name), name);
        }

        Probe.Check("a reserved device name is escaped", GameVaultName.Title("CON") == "CON game");
        Probe.Check("a trailing dot is dropped", GameVaultName.Title("Sam & Max.") == "Sam & Max");
        Probe.Check("a title of only bad characters still yields a name", GameVaultName.Title("???") == "Untitled");

        Probe.Group("Version flag");
        Probe.Check("'v1.2' does not become 'vv1.2'", GameVaultName.Version("v1.2") == "v1.2");
        Probe.Check("brackets are stripped from a version", GameVaultName.Version("1.2 (beta)") == "v1.2 beta");
        Probe.Check("a blank version is omitted", GameVaultName.Version("   ") is null);
        Probe.Check("a null version is omitted", GameVaultName.Version(null) is null);

        Probe.Group("Real titles from the library");
        string[] titles =
        [
            "Ultima™ 1+2+3",
            "Might and Magic® 6-pack Limited Edition",
            "Earthworm Jim 1+2: The Whole Can 'O Worms",
            "M.A.X. + M.A.X. 2",
            "Divinity: Original Sin 2 – Sir Lora",
            "POSTAL 2",
        ];

        foreach (var title in titles)
        {
            var name = GameVaultName.For(Game(title), GamePlatform.Windows, "1.0");
            Probe.Check($"{title}", IndexerAccepts(name), name);
            Probe.Note($"→ {name}");
        }
    }
}
