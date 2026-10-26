using GameArchiver.Core;
using SteamKit2;

namespace SteamArchiver.Core;

public sealed record SteamDepot(uint DepotId)
{
    public uint SharedFromApp { get; init; }
    public uint DlcAppId { get; init; }
    public string OsList { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string Language { get; init; } = "";
    public Dictionary<string, ulong> Manifests { get; init; } = [];
    public Dictionary<string, long> DownloadSizes { get; init; } = [];
    public Dictionary<string, long> InstalledSizes { get; init; } = [];
}

public sealed record SteamApp(uint AppId, string Title)
{
    public string Type { get; init; } = "game";
    public HashSet<GameOs> Platforms { get; init; } = [GameOs.Windows];
    public uint ParentAppId { get; init; }
    public List<uint> Dlcs { get; init; } = [];
    public List<SteamDepot> Depots { get; init; } = [];
    public List<SteamLaunch> Launches { get; init; } = [];
    public Dictionary<string, string> Builds { get; init; } = [];

    public static SteamApp Parse(uint id, KeyValue info)
    {
        var common = info["common"];
        var depots = info["depots"];
        return new(id, common["name"].AsString() ?? $"Steam app {id}")
        {
            Platforms = (common["oslist"].AsString() ?? "windows").Split(',').Select(os => os switch
            { "windows" => GameOs.Windows, "macos" => GameOs.Mac, "linux" => GameOs.Linux, _ => (GameOs?)null })
                .OfType<GameOs>().ToHashSet(),
            Type = (common["type"].AsString() ?? "game").ToLowerInvariant(), ParentAppId = common["parent"].AsUnsignedInteger(),
            Dlcs = (info["extended"]["listofdlc"].AsString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => uint.TryParse(v, out var n) ? n : 0).Where(n => n > 0).Distinct().ToList(),
            Builds = depots["branches"].Children.Where(b => !string.IsNullOrEmpty(b["buildid"].Value))
                .ToDictionary(b => b.Name!, b => b["buildid"].AsString()!),
            Launches = info["config"]["launch"].Children.Where(l => !string.IsNullOrEmpty(l["executable"].Value))
                .Select(l => new SteamLaunch(l["executable"].AsString()!, l["arguments"].AsString(), l["config"]["oslist"].AsString() ?? "")).ToList(),
            Depots = depots.Children.Where(d => uint.TryParse(d.Name, out _)).Select(d => new SteamDepot(uint.Parse(d.Name!))
            {
                SharedFromApp = d["depotfromapp"].AsUnsignedInteger(), DlcAppId = d["dlcappid"].AsUnsignedInteger(),
                OsList = d["config"]["oslist"].AsString() ?? "", Architecture = d["config"]["osarch"].AsString() ?? "",
                Language = d["config"]["language"].AsString() ?? "",
                Manifests = d["manifests"].Children.Select(b => (Name: b.Name!, Id: b["gid"].AsUnsignedLong()))
                    .Where(b => b.Id > 0).ToDictionary(b => b.Name, b => b.Id),
                DownloadSizes = Sizes(d["manifests"], "download"),
                InstalledSizes = Sizes(d["manifests"], "size"),
            }).ToList(),
        };
    }

    private static Dictionary<string, long> Sizes(KeyValue manifests, string field) => manifests.Children
        .Where(b => b["gid"].AsUnsignedLong() > 0)
        .Select(b => (Name: b.Name!, Size: long.TryParse(b[field].Value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var size) ? (long?)size : null))
        .Where(b => b.Size is >= 0).ToDictionary(b => b.Name, b => b.Size!.Value);
}
public sealed record SteamLaunch(string Executable, string? Arguments, string OsList = "");

public sealed record SteamDepotRequest(uint AppId, uint DepotId, ulong ManifestId, string Branch);
public sealed record SteamChunk(string Id, uint Checksum, long Offset, int CompressedSize, int Size);
public sealed record SteamFile(string Path, long Size, string Sha1, IReadOnlyList<SteamChunk> Chunks)
{
    public bool IsDirectory { get; init; }
    public bool IsExecutable { get; init; }
    public string? LinkTarget { get; init; }
}
public sealed record SteamManifest(SteamDepotRequest Depot, IReadOnlyList<SteamFile> Files);

/// <summary>Steam protocol boundary. An application can supply a controlled transport for testing.</summary>
public interface ISteamSession : IAsyncDisposable
{
    Task<IReadOnlySet<uint>> GetOwnedAppIdsAsync(CancellationToken ct);
    Task<SteamApp> GetAppAsync(uint appId, CancellationToken ct);
    Task PreloadAppsAsync(IReadOnlySet<uint> appIds, CancellationToken ct) => Task.CompletedTask;
    /// <summary>Optional labels for diagnostics only; names do not establish ownership or downloadable content.</summary>
    Task<IReadOnlyDictionary<uint, string>> GetAppNamesAsync(IReadOnlySet<uint> appIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<uint, string>>(new Dictionary<uint, string>());
    /// <summary>Complete GetOwnedGames catalog for sync and diagnostics; does not authorize content downloads. Incomplete responses must throw.</summary>
    Task<IReadOnlyDictionary<uint, string>> GetOwnedGamesAsync(CancellationToken ct) =>
        throw new NotSupportedException("This Steam session does not support the full GetOwnedGames catalog.");
    Task<ISteamContentSession> OpenContentAsync(long maxBytesPerSecond, CancellationToken ct);
}

public interface ISteamContentSession : IAsyncDisposable
{
    Task<SteamManifest> GetManifestAsync(SteamDepotRequest depot, CancellationToken ct);
    Task<byte[]> GetChunkAsync(SteamDepotRequest depot, SteamChunk chunk, CancellationToken ct);
}

public static class SteamLanguages
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "english", ["de"] = "german", ["fr"] = "french", ["cs"] = "czech", ["sk"] = "slovak",
        ["pl"] = "polish", ["ru"] = "russian", ["uk"] = "ukrainian", ["it"] = "italian", ["es"] = "spanish",
        ["es-mx"] = "latam", ["pt"] = "portuguese", ["pt-br"] = "brazilian", ["ja"] = "japanese",
        ["ko"] = "koreana", ["zh-cn"] = "schinese", ["zh-tw"] = "tchinese", ["tr"] = "turkish",
        ["da"] = "danish", ["nl"] = "dutch", ["fi"] = "finnish", ["no"] = "norwegian", ["sv"] = "swedish",
        ["hu"] = "hungarian", ["ro"] = "romanian", ["bg"] = "bulgarian", ["el"] = "greek", ["th"] = "thai",
        ["vi"] = "vietnamese", ["id"] = "indonesian", ["ar"] = "arabic",
    };
    public static string Name(string language)
    {
        var key = language.Replace('_', '-').ToLowerInvariant();
        if (Names.TryGetValue(key, out var exact)) return exact;
        if (Names.Values.Contains(key, StringComparer.OrdinalIgnoreCase)) return key;
        if (Names.TryGetValue(key.Split('-')[0], out var primary)) return primary;
        throw new ArgumentException($"Unsupported Steam language '{language}'.", nameof(language));
    }
}
