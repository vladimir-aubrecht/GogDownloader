using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using GameArchiver.Core;

namespace GameArchiver;

public sealed record PcGamingWikiCredentials(string Username, string BotPassword)
{
    public override string ToString() => "PcGamingWikiCredentials (redacted)";
}

public sealed class PcGamingWikiAuthenticationException(string message) : IOException(message);

public sealed record PcGamingWikiSessionCookie(string Name, string Value, string Path, DateTime? Expires)
{
    public override string ToString() => "PcGamingWikiSessionCookie (redacted)";
}

public sealed record PcGamingWikiSession(string Username, long UserId, List<PcGamingWikiSessionCookie> Cookies)
{
    public override string ToString() => "PcGamingWikiSession (redacted)";
}

/// <summary>Stores session cookies, never the bot password. Treat this file like a login token.</summary>
public sealed class PcGamingWikiSessionStore(string? path = null)
{
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "gamearchive", "pcgamingwiki-session.json");
    public string Path { get; } = System.IO.Path.GetFullPath(path ?? DefaultPath);

    public async Task<PcGamingWikiSession?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(Path)) return null;
        try
        {
            await using var stream = File.OpenRead(Path);
            if (stream.Length > 1024 * 1024) throw new InvalidDataException("PCGamingWiki session file exceeds 1 MiB.");
            var session = await JsonSerializer.DeserializeAsync<PcGamingWikiSession>(stream, ArchiveJson.Options, ct);
            Validate(session);
            return session;
        }
        catch (JsonException) { throw new InvalidDataException("Invalid PCGamingWiki session file. Run auth login --source pcgamingwiki again."); }
    }

    internal static void Validate(PcGamingWikiSession? session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.Username) || session.Username.Any(char.IsControl)
            || session.Username.Length > 256 || session.UserId <= 0 || session.Cookies is null || session.Cookies.Count is < 1 or > 64)
            throw new InvalidDataException("Invalid PCGamingWiki session. Run auth login --source pcgamingwiki again.");
        foreach (var cookie in session.Cookies)
        {
            if (cookie is null || string.IsNullOrEmpty(cookie.Name) || cookie.Name.Length > 256 || cookie.Value is null
                || cookie.Value.Length > 8192 || cookie.Path is null || !cookie.Path.StartsWith('/') || cookie.Path.Length > 1024
                || (cookie.Name + cookie.Value + cookie.Path).Any(char.IsControl))
                throw new InvalidDataException("Invalid PCGamingWiki session cookie.");
            try { new CookieContainer().Add(new Uri("https://www.pcgamingwiki.com/w/api.php"), new Cookie(cookie.Name, cookie.Value, cookie.Path)); }
            catch (CookieException) { throw new InvalidDataException("Invalid PCGamingWiki session cookie."); }
        }
    }

    public async Task SaveAsync(PcGamingWikiSession session, CancellationToken ct = default)
    {
        Validate(session);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = CreatePrivateFile(temp))
                await JsonSerializer.SerializeAsync(stream, session, ArchiveJson.Options, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, Path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static FileStream CreatePrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096,
                FileOptions.Asynchronous, security);
        }
        return new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
            Share = FileShare.None, Options = FileOptions.Asynchronous, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
    }

    /// <summary>Forgets the local session. Revoke the bot password on the wiki to revoke all its sessions.</summary>
    public void Delete() => File.Delete(Path);
}
