namespace GameArchiver.Cli;

public static partial class ArchiveCommandLine
{
    private static async Task<int> RunPcGamingWikiAuthAsync(List<string> args, Dictionary<string, string> flags,
        Func<string, PcGamingWikiDrmSource>? factory, CancellationToken ct)
    {
        var operation = args.ElementAtOrDefault(1);
        if (args.Count != 2 || operation is not "login" and not "status" and not "logout")
            throw new ArgumentException("Usage: auth login|status|logout --source pcgamingwiki [--pcgw-session FILE]");
        var allowed = new HashSet<string> { "source", "pcgw-session" };
        if (operation == "login") allowed.Add("username");
        if (flags.Keys.FirstOrDefault(k => !allowed.Contains(k)) is { } unsupported)
            throw new ArgumentException($"--{unsupported} is not supported by PCGamingWiki auth {operation}.");
        var sessions = new PcGamingWikiSessionStore(flags.GetValueOrDefault("pcgw-session"));
        if (operation == "status")
        {
            var saved = await sessions.LoadAsync(ct);
            Console.WriteLine(saved is null ? "Not logged into PCGamingWiki."
                : $"PCGamingWiki account {saved.Username} (saved session; not validated online).");
            return saved is null ? 1 : 0;
        }
        if (operation == "logout")
        {
            sessions.Delete();
            Console.WriteLine("PCGamingWiki local session deleted. Revoke the bot password on the wiki to revoke all its sessions.");
            return 0;
        }
        Console.Error.WriteLine("Create a bot password at https://www.pcgamingwiki.com/wiki/Special:BotPasswords with Cargo query permission. Use Account@BotName, not your normal account password.");
        var username = flags.GetValueOrDefault("username") ?? Environment.GetEnvironmentVariable("GAMEARCHIVE_PCGW_USERNAME");
        if (username is null)
        {
            if (Console.IsInputRedirected) throw new ArgumentException("Set GAMEARCHIVE_PCGW_USERNAME or --username Account@BotName for noninteractive login.");
            Console.Error.Write("PCGamingWiki bot username (Account@BotName): ");
            username = await ReadLineAsync(ct);
        }
        var password = Environment.GetEnvironmentVariable("GAMEARCHIVE_PCGW_BOT_PASSWORD")
            ?? await ReadPasswordAsync(ct, "PCGamingWiki bot password", "GAMEARCHIVE_PCGW_BOT_PASSWORD");
        using var provider = factory?.Invoke(sessions.Path) ?? new PcGamingWikiDrmSource(sessionPath: sessions.Path);
        await provider.LoginAsync(new(username ?? "", password), ct);
        Console.WriteLine("Logged into PCGamingWiki. Session cookies saved; the bot password was not saved. DRM sync will reuse this session.");
        return 0;
    }
}
