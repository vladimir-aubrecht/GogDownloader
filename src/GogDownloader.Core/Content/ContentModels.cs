using System.Text.Json;
using System.Text.Json.Serialization;

namespace GogDownloader.Core.Content;

/// <summary>Hash → cesta na CDN: `abcdef…` se mapuje na `ab/cd/abcdef…`.</summary>
public static class GalaxyPath
{
    public static string For(string hash) =>
        hash.Contains('/') ? hash : $"{hash[..2]}/{hash[2..4]}/{hash}";
}

public sealed record RepositoryProduct
{
    [JsonPropertyName("productId")] public string ProductId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
}

public sealed record DepotRef
{
    [JsonPropertyName("manifest")] public string Manifest { get; init; } = "";
    [JsonPropertyName("productId")] public string ProductId { get; init; } = "";
    [JsonPropertyName("languages")] public List<string> Languages { get; init; } = [];
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("compressedSize")] public long CompressedSize { get; init; }

    /// <summary>`*` znamená jazykově neutrální depot (binárky, assety).</summary>
    public bool MatchesLanguage(string language) =>
        Languages.Any(l => l == "*" || string.Equals(l, language, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Obsah `build.repository_manifest` po zlib dekompresi.</summary>
public sealed record RepositoryManifest
{
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("baseProductId")] public string BaseProductId { get; init; } = "";
    [JsonPropertyName("buildId")] public string? BuildId { get; init; }
    [JsonPropertyName("installDirectory")] public string InstallDirectory { get; init; } = "";
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; init; } = [];

    /// <summary>Základní hra a všechna její DLC — jediný zdroj seznamu DLC.</summary>
    [JsonPropertyName("products")] public List<RepositoryProduct> Products { get; init; } = [];

    [JsonPropertyName("depots")] public List<DepotRef> Depots { get; init; } = [];
}

public sealed record Chunk
{
    [JsonPropertyName("compressedMd5")] public string CompressedMd5 { get; init; } = "";
    [JsonPropertyName("md5")] public string Md5 { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("compressedSize")] public long CompressedSize { get; init; }
}

public sealed record DepotItem
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("target")] public string? Target { get; init; }
    [JsonPropertyName("md5")] public string? Md5 { get; init; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
    [JsonPropertyName("flags")] public List<string> Flags { get; init; } = [];
    [JsonPropertyName("chunks")] public List<Chunk> Chunks { get; init; } = [];

    public bool IsSupport => Flags.Contains("support");
    public long Size => Chunks.Sum(c => c.Size);

    /// <summary>Identita souboru pro pozdější diff. Jednochunkové soubory `md5` nemívají.</summary>
    public string? Identity => Md5 ?? (Chunks.Count == 1 ? Chunks[0].Md5 : Sha256);
}

public sealed record DepotBody
{
    [JsonPropertyName("items")] public List<DepotItem> Items { get; init; } = [];
}

public sealed record DepotManifest
{
    [JsonPropertyName("depot")] public DepotBody Depot { get; init; } = new();
}

public sealed record SecureLinkEndpoint
{
    [JsonPropertyName("url_format")] public string UrlFormat { get; init; } = "";
    [JsonPropertyName("parameters")] public Dictionary<string, JsonElement> Parameters { get; init; } = [];
    [JsonPropertyName("priority")] public int Priority { get; init; }

    /// <summary>
    /// Do `parameters["path"]` se připojí galaxy_path chunku a teprve pak se hodnoty
    /// dosadí do `url_format`. Pořadí je podstatné — viz docs/gog-api.md.
    /// </summary>
    public string ChunkUrl(string compressedMd5)
    {
        var url = UrlFormat;
        foreach (var (key, element) in Parameters)
        {
            var value = element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? ""
                : element.GetRawText();

            if (key == "path")
                value += "/" + GalaxyPath.For(compressedMd5);

            url = url.Replace("{" + key + "}", value, StringComparison.Ordinal);
        }
        return url;
    }
}

public sealed record SecureLinkResponse
{
    [JsonPropertyName("urls")] public List<SecureLinkEndpoint> Urls { get; init; } = [];
}

/// <summary>Podepsaná URL vypršela. Není to chyba stahování — stačí si říct o novou.</summary>
public sealed class SecureLinkExpiredException : Exception;

public sealed class ChunkChecksumException(string expected, string actual)
    : Exception($"Chunk má MD5 {actual}, čekalo se {expected}.");
