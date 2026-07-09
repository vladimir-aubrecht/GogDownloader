using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GogDownloader.Core.Api;

namespace GogDownloader.Core.Content;

/// <summary>
/// Přístup ke Galaxy content systemu. Metadata jdou přes autentizovaného klienta,
/// chunky přes holý — secure link je podepsaný a Bearer hlavičku nechce.
/// </summary>
public sealed class ContentClient(HttpClient authed, HttpClient cdn, RateLimiter limiter)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<RepositoryManifest?> GetRepositoryManifestAsync(string url, CancellationToken ct) =>
        GetZlibJsonAsync<RepositoryManifest>(url, ct);

    public Task<DepotManifest?> GetDepotManifestAsync(string manifestHash, CancellationToken ct) =>
        GetZlibJsonAsync<DepotManifest>(
            $"{GogConstants.Cdn}/content-system/v2/meta/{GalaxyPath.For(manifestHash)}", ct);

    public async Task<IReadOnlyList<SecureLinkEndpoint>> GetSecureLinkAsync(
        string productId, CancellationToken ct)
    {
        var url = $"{GogConstants.ContentSystem}/products/{productId}" +
                  "/secure_link?_version=2&generation=2&path=/";

        var response = await authed.GetFromJsonAsync<SecureLinkResponse>(url, Json, ct);
        var urls = response?.Urls ?? [];
        if (urls.Count == 0)
            throw new InvalidOperationException($"GOG nevrátil secure link pro produkt {productId}.");

        return urls.OrderBy(u => u.Priority).ToList();
    }

    /// <summary>Stáhne chunk, ověří MD5 nad *komprimovanými* bajty a rozbalí ho.</summary>
    public async Task<byte[]> DownloadChunkAsync(
        SecureLinkEndpoint endpoint, Chunk chunk, IProgress<long>? bytes, CancellationToken ct)
    {
        using var response = await cdn.GetAsync(
            endpoint.ChunkUrl(chunk.CompressedMd5), HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SecureLinkExpiredException();
        response.EnsureSuccessStatusCode();

        var compressed = new MemoryStream(capacity: (int)Math.Max(chunk.CompressedSize, 1));
        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await limiter.ConsumeAsync(read, ct);
                    compressed.Write(buffer, 0, read);
                    bytes?.Report(read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        var raw = compressed.GetBuffer().AsMemory(0, (int)compressed.Length);
        var actual = Convert.ToHexStringLower(MD5.HashData(raw.Span));
        if (!string.Equals(actual, chunk.CompressedMd5, StringComparison.OrdinalIgnoreCase))
            throw new ChunkChecksumException(chunk.CompressedMd5, actual);

        compressed.Position = 0;
        await using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var output = new MemoryStream(capacity: (int)Math.Max(chunk.Size, 1));
        await zlib.CopyToAsync(output, ct);
        return output.ToArray();
    }

    /// <summary>
    /// Manifesty jsou zlib JSON. Generation 1 je posílá nekomprimované, proto fallback.
    /// </summary>
    private async Task<T?> GetZlibJsonAsync<T>(string url, CancellationToken ct)
    {
        using var response = await authed.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsByteArrayAsync(ct);

        try
        {
            await using var zlib = new ZLibStream(new MemoryStream(payload), CompressionMode.Decompress);
            return await JsonSerializer.DeserializeAsync<T>(zlib, Json, ct);
        }
        catch (InvalidDataException)
        {
            return JsonSerializer.Deserialize<T>(payload, Json);
        }
    }
}

/// <summary>Cache secure linků. Při 401 se link zahodí a vyžádá znovu.</summary>
public sealed class SecureLinkCache(ContentClient content)
{
    private readonly Dictionary<string, SecureLinkEndpoint> _cache = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SecureLinkEndpoint> GetAsync(string productId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(productId, out var cached)) return cached;
            var endpoint = (await content.GetSecureLinkAsync(productId, ct))[0];
            return _cache[productId] = endpoint;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InvalidateAsync(string productId, SecureLinkEndpoint stale, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Jiné vlákno už mohlo obnovit — pak nezahazovat čerstvý link.
            if (_cache.TryGetValue(productId, out var current) && ReferenceEquals(current, stale))
                _cache.Remove(productId);
        }
        finally
        {
            _gate.Release();
        }
    }
}
