using System.Text.Json;
using System.Text.Json.Serialization;

namespace GogDownloader.Core.Content;

/// <summary>Hash to CDN path: `abcdef…` maps to `ab/cd/abcdef…`.</summary>
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

    /// <summary>
    /// `*` means a language-neutral depot (binaries, assets). Everything else is tagged with a
    /// full code such as `en-US`, so the request has to be resolved before comparing — matching
    /// the raw string would drop every localised depot and leave a near-empty archive.
    /// </summary>
    public bool MatchesLanguage(string requested)
    {
        if (Languages.Contains("*")) return true;

        var language = GogLanguage.Parse(requested);
        return language is not null && Languages.Any(language.Matches);
    }
}

/// <summary>The contents of `build.repository_manifest` after zlib decompression.</summary>
public sealed record RepositoryManifest
{
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("baseProductId")] public string BaseProductId { get; init; } = "";
    [JsonPropertyName("buildId")] public string? BuildId { get; init; }
    [JsonPropertyName("installDirectory")] public string InstallDirectory { get; init; } = "";
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; init; } = [];

    /// <summary>The base game and all its DLC — the only source of the DLC list.</summary>
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

    /// <summary>File identity for a later diff. Single-chunk files usually lack `md5`.</summary>
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
    /// The chunk's galaxy_path is appended to `parameters["path"]` and only then are the
    /// values substituted into `url_format`. The order matters — see docs/gog-api.md.
    /// </summary>
    public string ChunkUrl(string compressedMd5) => UrlWith("/" + GalaxyPath.For(compressedMd5));

    /// <summary>
    /// Generation 1 keeps every file of a depot inside one blob, addressed by byte range.
    /// </summary>
    public string BlobUrl() => UrlWith("/main.bin");

    private string UrlWith(string pathSuffix)
    {
        var url = UrlFormat;
        foreach (var (key, element) in Parameters)
        {
            var value = element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? ""
                : element.GetRawText();

            if (key == "path") value += pathSuffix;

            url = url.Replace("{" + key + "}", value, StringComparison.Ordinal);
        }
        return url;
    }
}

public sealed record SecureLinkResponse
{
    [JsonPropertyName("urls")] public List<SecureLinkEndpoint> Urls { get; init; } = [];
}

/// <summary>The signed URL expired. Not a download failure — just ask for a new one.</summary>
public sealed class SecureLinkExpiredException : Exception;

public sealed class ChunkChecksumException(string expected, string actual)
    : Exception($"Chunk MD5 is {actual}, expected {expected}.");

/// <summary>A chunk transfer that failed mid-body, carrying the HTTP details for diagnosis.</summary>
public sealed class ChunkTransferException(string message, Exception inner) : IOException(message, inner);
