using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GogDownloader.Core.Auth;

public sealed record GogTokens
{
    [JsonPropertyName("access_token")] public required string AccessToken { get; init; }
    [JsonPropertyName("refresh_token")] public required string RefreshToken { get; init; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("user_id")] public string UserId { get; init; } = "";

    /// <summary>We add this ourselves — GOG does not send it. Without it expiry cannot be computed.</summary>
    [JsonPropertyName("obtained_at")] public DateTimeOffset ObtainedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresAt => ObtainedAt.AddSeconds(ExpiresIn);

    /// <summary>A token is treated as dead one minute before it actually expires.</summary>
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt.AddMinutes(-1);
}

public sealed class TokenStore(string? path = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Path { get; } = path ?? DefaultPath();

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "gogdownloader", "tokens.json");

    public async Task<GogTokens?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(Path)) return null;
        await using var stream = File.OpenRead(Path);
        return await JsonSerializer.DeserializeAsync<GogTokens>(stream, Json, ct);
    }

    public async Task SaveAsync(GogTokens tokens, CancellationToken ct = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        // Write to a temp file and move, so a crash cannot leave half a token behind.
        var tmp = Path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, tokens, Json, ct);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.Move(tmp, Path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}

public sealed class GogAuthClient(HttpClient http)
{
    public async Task<GogTokens> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        var url = $"{GogConstants.Auth}/token" +
                  $"?client_id={GogConstants.ClientId}" +
                  $"&client_secret={GogConstants.ClientSecret}" +
                  "&grant_type=authorization_code" +
                  $"&redirect_uri={Uri.EscapeDataString(GogConstants.RedirectUri)}" +
                  $"&code={Uri.EscapeDataString(code)}";
        return await GetTokensAsync(url, ct);
    }

    public async Task<GogTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var url = $"{GogConstants.Auth}/token" +
                  $"?client_id={GogConstants.ClientId}" +
                  $"&client_secret={GogConstants.ClientSecret}" +
                  "&grant_type=refresh_token" +
                  $"&refresh_token={Uri.EscapeDataString(refreshToken)}";
        return await GetTokensAsync(url, ct);
    }

    private async Task<GogTokens> GetTokensAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new GogAuthException($"GOG rejected authentication ({(int)response.StatusCode}): {body}");
        }

        return await response.Content.ReadFromJsonAsync<GogTokens>(cancellationToken: ct)
               ?? throw new GogAuthException("GOG returned an empty response to the token request.");
    }

    /// <summary>Extracts the authorization code from a full redirect URL or a bare code.</summary>
    public static string? ExtractCode(string input)
    {
        input = input.Trim();
        if (!input.Contains("://", StringComparison.Ordinal))
            return input.Length > 0 ? input : null;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return null;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair.AsSpan(0, eq).SequenceEqual("code"))
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }
}

/// <summary>Holds a valid access token and refreshes it when it expires.</summary>
public sealed class TokenProvider(TokenStore store, GogAuthClient auth)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private GogTokens? _cached;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default, bool forceRefresh = false)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _cached ??= await store.LoadAsync(ct)
                        ?? throw new GogAuthException("Not logged in. Run: gogdl auth login");

            if (forceRefresh || _cached.IsExpired)
            {
                var refreshed = await auth.RefreshAsync(_cached.RefreshToken, ct);
                await store.SaveAsync(refreshed, ct);
                _cached = refreshed;
            }

            return _cached.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }
}

public sealed class GogAuthException(string message) : Exception(message);
