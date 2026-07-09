using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace GogDownloader.Core.Api;

public sealed record OwnedGames
{
    [JsonPropertyName("owned")] public List<long> Owned { get; init; } = [];
}

public sealed record InstallerFile
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("downlink")] public string Downlink { get; init; } = "";
}

public sealed record Installer
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("os")] public string Os { get; init; } = "";
    [JsonPropertyName("language")] public string Language { get; init; } = "";
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("total_size")] public long TotalSize { get; init; }
    [JsonPropertyName("files")] public List<InstallerFile> Files { get; init; } = [];
}

public sealed record Downloads
{
    [JsonPropertyName("installers")] public List<Installer> Installers { get; init; } = [];
}

public sealed record Product
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("downloads")] public Downloads? Downloads { get; init; }
}

public sealed record Build
{
    [JsonPropertyName("build_id")] public string BuildId { get; init; } = "";
    [JsonPropertyName("generation")] public int Generation { get; init; }
    [JsonPropertyName("version_name")] public string? VersionName { get; init; }
    [JsonPropertyName("branch")] public string? Branch { get; init; }
    [JsonPropertyName("repository_manifest")] public string? RepositoryManifest { get; init; }
    [JsonPropertyName("link")] public string? Link { get; init; }

    /// <summary>gogdl čte repository_manifest; starší odpovědi mají jen link.</summary>
    public string? Manifest => RepositoryManifest ?? Link;
}

public sealed record BuildsResponse
{
    [JsonPropertyName("total_count")] public int TotalCount { get; init; }
    [JsonPropertyName("items")] public List<Build> Items { get; init; } = [];
}

public sealed class GogApiClient(HttpClient http)
{
    public async Task<IReadOnlyList<long>> GetOwnedGameIdsAsync(CancellationToken ct = default)
    {
        var result = await http.GetFromJsonAsync<OwnedGames>(
            $"{GogConstants.Embed}/user/data/games", ct);
        return result?.Owned ?? [];
    }

    public async Task<Product?> GetProductAsync(long id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(
            $"{GogConstants.Api}/products/{id}?expand=downloads", ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Product>(cancellationToken: ct);
    }

    /// <summary>
    /// Buildy pro platformu. Prázdný seznam znamená, že hra nemá Galaxy depot a
    /// depot cesta pro ni není možná. Zkouší generation 2, pak 1.
    /// </summary>
    public async Task<Build?> GetLatestBuildAsync(
        long id, GamePlatform platform, CancellationToken ct = default)
    {
        foreach (var generation in (int[])[2, 1])
        {
            var url = $"{GogConstants.ContentSystem}/products/{id}" +
                      $"/os/{platform.ContentSystemName()}/builds?generation={generation}";

            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) continue;

            var builds = await response.Content.ReadFromJsonAsync<BuildsResponse>(cancellationToken: ct);
            // Zajímá nás jen veřejná větev; branch != null jsou beta kanály.
            var build = builds?.Items.FirstOrDefault(b => b.Branch is null);
            if (build is not null) return build;
        }

        return null;
    }
}
