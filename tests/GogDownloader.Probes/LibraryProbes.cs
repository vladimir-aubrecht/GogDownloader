using GogDownloader.Cli;
using GogDownloader.Core.Api;
using GogDownloader.Core.Content;
using GogDownloader.Core.Download;
using GogDownloader.Core.Library;

namespace GogDownloader.Probes;

/// <summary>
/// Snapshot semantics. A stale snapshot silently reporting zeros is the failure mode worth
/// guarding, so the schema gates get their own checks.
/// </summary>
public static class LibraryProbes
{
    public static void Run()
    {
        Probe.Group("Size reported for a game");
        var withDepot = new PlatformAvailability
        {
            Depot = new BuildRef("b", 2, "1", "http://x"),
            DownloadSize = 5_000_000_000,
            InstalledSize = 12_000_000_000,
            Installers = [new InstallerRef("en", null, 999)],
        };
        Probe.Check("a depot wins over an installer", withDepot.BestKnownSize == 5_000_000_000);
        Probe.Check("a generation 1 depot is downloadable too",
            new PlatformAvailability { Depot = new BuildRef("b", 1, null, "u") } is
                { CanBackUp: true, HasLegacyDepot: true });
        // Linux has no depots at all; its installer is the only source there is.
        Probe.Check("a Linux installer can be backed up",
            new PlatformAvailability { IsLinux = true, Installers = [new InstallerRef("en", null, 5)] }.CanBackUp);
        // Windows without a depot goes through innoextract; only macOS has nowhere to go.
        Probe.Check("a Windows installer goes through innoextract",
            new PlatformAvailability { Installers = [new InstallerRef("en", null, 5)] } is
                { CanBackUp: true, NeedsInnoExtract: true });
        Probe.Check("a macOS installer cannot be extracted",
            !new PlatformAvailability { IsMac = true, Installers = [new InstallerRef("en", null, 5)] }.CanBackUp);
        Probe.Check("a Linux entry with no installer cannot",
            !new PlatformAvailability { IsLinux = true }.CanBackUp);
        Probe.Check("a depot never needs innoextract",
            !new PlatformAvailability { Depot = new BuildRef("b", 2, null, "u"), Installers = [new InstallerRef("en", null, 5)] }.NeedsInnoExtract);
        Probe.Check("without a depot the installer is used",
            new PlatformAvailability { Installers = [new InstallerRef("en", null, 777)] }.BestKnownSize == 777);
        Probe.Check("with neither the size is zero", new PlatformAvailability().BestKnownSize == 0);

        Probe.Group("Depot language filter drives the recorded size");
        Probe.Check("a neutral depot counts", new DepotRef { Languages = ["*"] }.MatchesLanguage("en"));
        Probe.Check("a German depot does not count towards English",
            !new DepotRef { Languages = ["de"] }.MatchesLanguage("en"));

        Probe.Group("Snapshot schema gates");
        Probe.Check("schema 0 knows neither DLC nor sizes",
            new LibrarySnapshot { SchemaVersion = 0 } is { KnowsDlcs: false, KnowsDepotSizes: false });
        Probe.Check("schema 2 knows DLC but not sizes",
            new LibrarySnapshot { SchemaVersion = 2 } is { KnowsDlcs: true, KnowsDepotSizes: false });
        // Schema 3 and 4 recorded sizes, but a broken language filter made them far too small,
        // so they must not be trusted any more than a missing field.
        Probe.Check("schema 5 knows early access but not trustworthy sizes",
            new LibrarySnapshot { SchemaVersion = 5 } is { KnowsEarlyAccess: true, KnowsDepotSizes: false });
        Probe.Check("schema 6 knows sizes but not packs",
            new LibrarySnapshot { SchemaVersion = 6 } is
                { KnowsDlcs: true, KnowsDepotSizes: true, KnowsEarlyAccess: true, KnowsPacks: false });
        Probe.Check("schema 7 knows everything",
            new LibrarySnapshot { SchemaVersion = 7 } is
                { KnowsDlcs: true, KnowsDepotSizes: true, KnowsEarlyAccess: true, KnowsPacks: true });
        Probe.Check($"CurrentSchema is {LibrarySnapshot.CurrentSchema}", LibrarySnapshot.CurrentSchema == 7);

        // Prehistorik 1+2 and Quake are packs: no build on the content system, an installer their
        // own downlink refuses (403/404), and both of their games sitting in the library already.
        Probe.Group("Packs duplicate games the library owns separately");
        var quake = Game(1435828198, "Quake");
        var quakeEnhanced = Game(1739637082, "Quake Enhanced");
        var quakePack = Game(1440166133, "Quake") with
        {
            IsPack = true,
            IncludedGames = [1435828198, 1739637082],
        };
        var full = new LibrarySnapshot { Games = [quake, quakeEnhanced, quakePack] };
        Probe.Check("a pack whose games are all here is a duplicate",
            full.PackDuplicates(quakePack).Select(g => g.Id).ToList() is [1435828198, 1739637082]);
        Probe.Check("the games themselves are not duplicates",
            full.PackDuplicates(quake).Count == 0);

        // The pack is then the only copy of those games there is, so it has to be downloaded.
        var partial = new LibrarySnapshot { Games = [quake, quakePack] };
        Probe.Check("a pack missing one of its games is not a duplicate",
            partial.PackDuplicates(quakePack).Count == 0);
        Probe.Check("a pack GOG lists no contents for is not a duplicate",
            new LibrarySnapshot { Games = [quake, quakePack with { IncludedGames = [] }] }
                .PackDuplicates(quakePack with { IncludedGames = [] }).Count == 0);
        Probe.Check("an ordinary game that names contents is still not a pack",
            new LibrarySnapshot { Games = [quake, quakePack with { IsPack = false }] }
                .PackDuplicates(quakePack with { IsPack = false }).Count == 0);

        Probe.Group("Pack ids come out of the v2 links");
        var links = System.Text.Json.JsonSerializer.Deserialize<GameLinks>(
            """
            {"_links":{"includesGames":[
                {"href":"https://api.gog.com/v2/games/1435828198?locale=en-US"},
                {"href":"https://api.gog.com/v2/games/1739637082?locale=en-US"}],
              "self":{"href":"https://api.gog.com/v2/games/1440166133?locale=en-US"}}}
            """)!;
        Probe.Check("the id is the last path segment, not the query",
            links.IncludedGameIds().ToList() is [1435828198, 1739637082]);
        Probe.Check("a game with no includesGames yields nothing",
            System.Text.Json.JsonSerializer.Deserialize<GameLinks>("""{"_links":{"self":{"href":"x"}}}""")!
                .IncludedGameIds().Count == 0);

        Probe.Group("Argument parsing");
        // A switch that swallows the next word turns `download --all witcher` into a request
        // for every game, and `download --force x` into a request for nothing.
        var flags = CommandLine.ParseFlags(["download", "--all", "witcher", "--platform", "windows"]);
        Probe.Check("a switch takes no value", flags["all"] == "true", flags["all"]);
        Probe.Check("a valued flag still takes its value", flags["platform"] == "windows");
        Probe.Check("the word after a switch stays positional",
            CommandLine.Positionals(["download", "--all", "witcher", "--platform", "windows"]) is ["download", "witcher"]);
        Probe.Check("a flag's value is not positional",
            CommandLine.Positionals(["download", "ftl", "--platform", "windows"]) is ["download", "ftl"]);
        Probe.Check("a trailing switch parses",
            CommandLine.ParseFlags(["download", "ftl", "--force"])["force"] == "true");
        Probe.Check("--force does not eat the game name",
            CommandLine.Positionals(["download", "--force", "ftl"]) is ["download", "ftl"]);
        Probe.Check("no arguments yields nothing", CommandLine.Positionals([]).Count == 0);

        Probe.Group("Connection count");
        Probe.Check("defaults to five, GOG's sensible ceiling",
            CommandLine.OptionsFrom(new()).MaxConnections == 5, CommandLine.OptionsFrom(new()).MaxConnections.ToString());
        Probe.Check("the user can force more",
            CommandLine.OptionsFrom(new() { ["connections"] = "12" }).MaxConnections == 12);
        Probe.Check("the user can force fewer for a rate-limited game",
            CommandLine.OptionsFrom(new() { ["connections"] = "2" }).MaxConnections == 2);
        Probe.Check("a nonsense value falls back to the default",
            CommandLine.OptionsFrom(new() { ["connections"] = "abc" }).MaxConnections == 5);

        Probe.Group("Human-readable sizes");
        Probe.Check("an unknown size is a dash, not '0 B'", CommandLine.Bytes(0) == "-", CommandLine.Bytes(0));
        Probe.Check("bytes", CommandLine.Bytes(512) == "512 B", CommandLine.Bytes(512));
        Probe.Check("the KiB boundary", CommandLine.Bytes(1024) == "1.0 KiB", CommandLine.Bytes(1024));
        Probe.Check("megabytes", CommandLine.Bytes(190_000_000) == "181 MiB", CommandLine.Bytes(190_000_000));
        Probe.Check("gigabytes", CommandLine.Bytes(32_212_254_720) == "30 GiB", CommandLine.Bytes(32_212_254_720));
        Probe.Check("small values keep a decimal", CommandLine.Bytes(1_610_612_736) == "1.5 GiB", CommandLine.Bytes(1_610_612_736));
        Probe.Check("TiB is the largest unit",
            CommandLine.Bytes(5L * 1024 * 1024 * 1024 * 1024).EndsWith("TiB"));
    }

    private static LibraryEntry Game(long id, string title) => new()
    {
        Id = id,
        Title = title,
        Slug = title.ToLowerInvariant().Replace(' ', '_'),
    };
}
