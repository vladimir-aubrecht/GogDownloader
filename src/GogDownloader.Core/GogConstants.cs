namespace GogDownloader.Core;

/// <summary>Verified values, see docs/gog-api.md.</summary>
public static class GogConstants
{
    public const string Auth = "https://auth.gog.com";
    public const string Embed = "https://embed.gog.com";
    public const string Api = "https://api.gog.com";
    public const string ContentSystem = "https://content-system.gog.com";
    public const string Cdn = "https://gog-cdn-fastly.gog.com";

    // Public credentials of the Galaxy client.
    public const string ClientId = "46899977096215655";
    public const string ClientSecret = "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";
    public const string RedirectUri = "https://embed.gog.com/on_login_success?origin=client";

    public static string LoginUrl =>
        $"{Auth}/auth?client_id={ClientId}" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        "&response_type=code&layout=client2";
}

/// <summary>
/// api.gog.com calls macOS "mac", content-system.gog.com calls it "osx". Do not mix them up.
/// </summary>
public enum GamePlatform
{
    Windows,
    Mac,
    Linux,
}

public static class GamePlatformExtensions
{
    /// <summary>The spelling api.gog.com uses in downloads.installers[].os.</summary>
    public static string ApiName(this GamePlatform p) => p switch
    {
        GamePlatform.Windows => "windows",
        GamePlatform.Mac => "mac",
        GamePlatform.Linux => "linux",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>The spelling content-system.gog.com uses in /os/{platform}/builds.</summary>
    public static string ContentSystemName(this GamePlatform p) => p switch
    {
        GamePlatform.Windows => "windows",
        GamePlatform.Mac => "osx",
        GamePlatform.Linux => "linux",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    public static GamePlatform? FromApiName(string os) => os.ToLowerInvariant() switch
    {
        "windows" => GamePlatform.Windows,
        "mac" or "osx" => GamePlatform.Mac,
        "linux" => GamePlatform.Linux,
        _ => null,
    };
}
