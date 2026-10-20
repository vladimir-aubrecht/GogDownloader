using System.Text.Json;
using GameArchiver.Core;

namespace SteamArchiver.Core;

public enum SteamChallengeKind { EmailCode, DeviceCode, DeviceConfirmation }
public sealed record SteamChallenge(SteamChallengeKind Kind, bool PreviousCodeIncorrect = false, string? Email = null);
public sealed record SteamCredentials(string Username, string Password)
{ public override string ToString() => $"SteamCredentials({Username})"; }
public sealed record SteamTokens(string AccountName, string RefreshToken, string? GuardData = null)
{ public override string ToString() => $"SteamTokens({AccountName})"; }

public sealed record SteamClientOptions
{
    public string TokenPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "gamearchive", "steam-tokens.json");
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public Func<SteamChallenge, CancellationToken, Task<string>>? Authenticate { get; init; }
}

public sealed class SteamAuthenticationException(string message, Exception? inner = null) : Exception(message, inner);

internal static class SteamTokenFile
{
    public static async Task<SteamTokens?> LoadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SteamTokens>(stream, ArchiveJson.Options, ct);
    }
    public static async Task SaveAsync(string path, SteamTokens tokens, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
            {
                await JsonSerializer.SerializeAsync(stream, tokens, ArchiveJson.Options, ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }
}
