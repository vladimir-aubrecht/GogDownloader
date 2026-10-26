using System.Text.Json;
using System.Text.Json.Serialization;

namespace GogArchiver.Core.Content;

/// <summary>
/// Generation 1 depot metadata. Old GOG titles — Quake, Nox, Unreal Gold — never moved to the
/// v2 content system, and their format differs in ways that punish assumptions: everything is
/// nested under `product`, language-neutral depots say `Neutral` instead of `*`, sizes are
/// strings, and there is no compressed size at all.
/// </summary>
public sealed record DepotRefV1
{
    [JsonPropertyName("manifest")] public string Manifest { get; init; } = "";
    [JsonPropertyName("gameIDs")] public List<string> GameIds { get; init; } = [];
    [JsonPropertyName("languages")] public List<string> Languages { get; init; } = [];

    /// <summary>A string in the JSON, and the only size v1 offers: the unpacked one.</summary>
    [JsonPropertyName("size")] public string SizeText { get; init; } = "0";

    /// <summary>Present on redistributables, which are not game content.</summary>
    [JsonPropertyName("redist")] public string? Redist { get; init; }

    [JsonIgnore] public bool IsRedistributable => Redist is not null;

    /// <summary>Ignored on write: it would collide with `size`, which is the string above.</summary>
    [JsonIgnore] public long Size => long.TryParse(SizeText, out var size) ? size : 0;

    /// <summary>v1 spells the neutral tag `Neutral`; everything else is a full code as in v2.</summary>
    public bool MatchesLanguage(string requested)
    {
        if (Languages.Contains("Neutral")) return true;

        var language = GogLanguage.Parse(requested);
        return language is not null && Languages.Any(language.Matches);
    }
}

public sealed record ProductV1
{
    [JsonPropertyName("rootGameID")] public string RootGameId { get; init; } = "";

    /// <summary>Part of every v1 CDN and secure-link path.</summary>
    [JsonPropertyName("timestamp")] public JsonElement Timestamp { get; init; }

    [JsonPropertyName("installDirectory")] public string InstallDirectory { get; init; } = "";
    [JsonPropertyName("depots")] public List<DepotRefV1> Depots { get; init; } = [];

    /// <summary>GOG sends it as a number here and expects it as a string in URLs.</summary>
    [JsonIgnore] public string TimestampText => Timestamp.ValueKind switch
    {
        JsonValueKind.String => Timestamp.GetString() ?? "",
        JsonValueKind.Number => Timestamp.GetRawText(),
        _ => "",
    };
}

public sealed record RepositoryManifestV1
{
    [JsonPropertyName("product")] public ProductV1 Product { get; init; } = new();
}

public sealed record DepotFileV1
{
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("offset")] public long Offset { get; init; }
    [JsonPropertyName("hash")] public string? Hash { get; init; }
    [JsonPropertyName("support")] public bool Support { get; init; }
    [JsonPropertyName("directory")] public bool Directory { get; init; }
    [JsonPropertyName("executable")] public bool Executable { get; init; }
}

public sealed record DepotBodyV1
{
    [JsonPropertyName("files")] public List<DepotFileV1> Files { get; init; } = [];
}

public sealed record DepotManifestV1
{
    [JsonPropertyName("depot")] public DepotBodyV1 Depot { get; init; } = new();
}
