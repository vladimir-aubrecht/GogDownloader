using GogArchiver.Core;
using GogArchiver.Core.Auth;
using GogArchiver.Core.Http;

namespace GogArchiver.Cli;

public static class AuthCommands
{
    public static async Task<int> LoginAsync(Dictionary<string, string> flags, CancellationToken ct)
    {
        var store = new TokenStore();
        var auth = new GogAuthClient(GogHttpFactory.Create(new GogHttpOptions()));

        if (!flags.TryGetValue("code", out var raw))
        {
            Console.WriteLine("Open this in a browser and log in:");
            Console.WriteLine();
            Console.WriteLine("  " + GogConstants.LoginUrl);
            Console.WriteLine();
            Console.WriteLine("After logging in you will land on a blank page. Copy the whole URL");
            Console.WriteLine("from the address bar (it contains ?code=...) and paste it here:");
            Console.Write("> ");
            raw = Console.ReadLine() ?? "";
        }

        if (GogAuthClient.ExtractCode(raw) is not { Length: > 0 } code)
        {
            Console.Error.WriteLine("No authorization code in the given input.");
            return 1;
        }

        var tokens = await auth.ExchangeCodeAsync(code, ct);
        await store.SaveAsync(tokens, ct);

        Console.WriteLine($"Logged in. Token saved to {store.Path}");
        return 0;
    }

    public static async Task<int> StatusAsync(CancellationToken ct)
    {
        var tokens = await new TokenStore().LoadAsync(ct);
        if (tokens is null)
        {
            Console.WriteLine("Not logged in. Run: gogarchiver auth login");
            return 1;
        }

        var state = tokens.IsExpired
            ? "expired (will refresh itself)"
            : $"valid until {tokens.ExpiresAt.ToLocalTime():HH:mm:ss}";
        Console.WriteLine($"Logged in as user_id={tokens.UserId}, access token {state}.");
        return 0;
    }

    public static int Logout()
    {
        new TokenStore().Delete();
        Console.WriteLine("Token deleted.");
        return 0;
    }
}
