using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GogDownloader.Core.Api;

namespace GogDownloader.Core.Content;

/// <summary>
/// Access to the Galaxy content system. Metadata goes through the authenticated client,
/// chunks through the bare one — a secure link is signed and does not want a Bearer header.
/// </summary>
public sealed class ContentClient(HttpClient authed, HttpClient cdn, RateLimiter limiter)
{
    internal RateLimiter Limiter => limiter;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<RepositoryManifest?> GetRepositoryManifestAsync(string url, CancellationToken ct) =>
        GetZlibJsonAsync<RepositoryManifest>(url, ct);

    public Task<DepotManifest?> GetDepotManifestAsync(string manifestHash, CancellationToken ct) =>
        GetZlibJsonAsync<DepotManifest>(
            $"{GogConstants.Cdn}/content-system/v2/meta/{GalaxyPath.For(manifestHash)}", ct);

    public Task<RepositoryManifestV1?> GetRepositoryManifestV1Async(string url, CancellationToken ct) =>
        GetZlibJsonAsync<RepositoryManifestV1>(url, ct);

    public Task<DepotManifestV1?> GetDepotManifestV1Async(
        string gameId, string platform, string timestamp, string manifest, CancellationToken ct) =>
        GetZlibJsonAsync<DepotManifestV1>(
            $"{GogConstants.Cdn}/content-system/v1/manifests/{gameId}/{platform}/{timestamp}/{manifest}", ct);

    /// <summary>
    /// Generation 2 signs the store root; generation 1 signs one platform-and-timestamp folder,
    /// and asks for `type=depot` rather than a generation.
    /// </summary>
    public async Task<IReadOnlyList<SecureLinkEndpoint>> GetSecureLinkAsync(
        string productId, CancellationToken ct, int generation = 2, string path = "/")
    {
        var query = generation == 1
            ? $"_version=2&type=depot&path={path}"
            : $"_version=2&generation=2&path={path}";

        var url = $"{GogConstants.ContentSystem}/products/{productId}/secure_link?{query}";

        var response = await authed.GetFromJsonAsync<SecureLinkResponse>(url, Json, ct);
        var urls = response?.Urls ?? [];
        if (urls.Count == 0)
            throw new InvalidOperationException($"GOG returned no secure link for product {productId}.");

        return urls.OrderBy(u => u.Priority).ToList();
    }

    /// <summary>
    /// Buffered convenience for small v1 ranges. Game downloads use CopyRangeAsync.
    /// </summary>
    public async Task<byte[]> DownloadRangeAsync(
        SecureLinkEndpoint endpoint, long offset, long size, string? md5,
        IProgress<long>? bytes, bool freshFromOrigin, CancellationToken ct)
    {
        if (size > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(size), "Use CopyRangeAsync for files beyond the array size limit.");
        using var output = new MemoryStream();
        await CopyRangeAsync(endpoint, offset, size, md5, output, bytes, freshFromOrigin, ct);
        return output.ToArray();
    }

    /// <summary>Streams a v1 file with bounded memory, including files beyond the array size limit.</summary>
    public async Task CopyRangeAsync(
        SecureLinkEndpoint endpoint, long offset, long size, string? md5, Stream destination,
        IProgress<long>? bytes, bool freshFromOrigin, CancellationToken ct)
    {
        if (size == 0) return;
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.BlobUrl());
        request.Headers.Range = new RangeHeaderValue(offset, offset + size - 1);
        if (freshFromOrigin) BypassCache(request);

        using var response = await cdn.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new SecureLinkExpiredException();
        response.EnsureSuccessStatusCode();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        long received = 0;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var scratch = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int read;
                while ((read = await stream.WithStallTimeoutAsync(scratch, ct)) > 0)
                {
                    await limiter.ConsumeAsync(read, ct);
                    received += read;
                    if (received > size)
                        throw new ChunkChecksumException($"{size} bytes", $"at least {received} bytes");
                    hash.AppendData(scratch, 0, read);
                    await destination.WriteAsync(scratch.AsMemory(0, read), ct);
                    bytes?.Report(read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            throw new ChunkTransferException(
                $"range {offset}-{offset + size - 1} of main.bin: " +
                $"HTTP {(int)response.StatusCode} {response.Version} " +
                $"content-length={response.Content.Headers.ContentLength?.ToString() ?? "?"} " +
                $"encoding=[{string.Join(",", response.Content.Headers.ContentEncoding)}] " +
                $"got={received}B — {e.Message}", e);
        }

        if (received != size)
            throw new ChunkChecksumException($"{size} bytes", $"{received} bytes");

        if (md5 is { Length: > 0 })
        {
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, md5, StringComparison.OrdinalIgnoreCase))
                throw new ChunkChecksumException(md5, actual);
        }
    }

    /// <summary>
    /// Fastly serves a chunk from the nearest edge cache. If that cached object is truncated the
    /// same short read comes back every time, so a retry sends no-cache to force a fresh copy.
    /// </summary>
    private static void BypassCache(HttpRequestMessage request)
    {
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.Pragma.ParseAdd("no-cache");
    }

    /// <summary>Downloads a chunk, verifies the MD5 over the *compressed* bytes, and inflates it.</summary>
    public async Task<byte[]> DownloadChunkAsync(
        SecureLinkEndpoint endpoint, Chunk chunk, IProgress<long>? bytes, bool freshFromOrigin, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.ChunkUrl(chunk.CompressedMd5));
        if (freshFromOrigin) BypassCache(request);

        using var response = await cdn.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new SecureLinkExpiredException();
        response.EnsureSuccessStatusCode();

        var compressed = new MemoryStream(capacity: (int)Math.Max(chunk.CompressedSize, 1));
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int read;
                while ((read = await stream.WithStallTimeoutAsync(buffer, ct)) > 0)
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
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            // Enrich the failure with what the CDN actually said, so --verbose can pin down why
            // specific chunks truncate: the status, the version, the promised length against what
            // arrived, and whether a Content-Encoding is in play.
            throw new ChunkTransferException(
                $"chunk {chunk.CompressedMd5[..8]} expected {chunk.CompressedSize}B: " +
                $"HTTP {(int)response.StatusCode} {response.Version} " +
                $"content-length={response.Content.Headers.ContentLength?.ToString() ?? "?"} " +
                $"encoding=[{string.Join(",", response.Content.Headers.ContentEncoding)}] " +
                $"got={compressed.Length}B — {e.Message}", e);
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
    /// Manifests are zlib JSON. Generation 1 sends them uncompressed, hence the fallback.
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

/// <summary>Secure link cache. On a 401 the link is dropped and requested again.</summary>
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
            // Another thread may have refreshed it already — do not drop a fresh link.
            if (_cache.TryGetValue(productId, out var current) && ReferenceEquals(current, stale))
                _cache.Remove(productId);
        }
        finally
        {
            _gate.Release();
        }
    }
}
