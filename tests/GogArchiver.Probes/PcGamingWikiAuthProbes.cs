using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using GameArchiver;
using GameArchiver.Cli;
using GameArchiver.Core;

namespace GogArchiver.Probes;

public static class PcGamingWikiAuthProbes
{
    private const string Password = "fake-bot-secret+&=never-print";
    private const string SessionValue = "fake-session-secret";

    public static async Task RunAsync()
    {
        Probe.Group("PCGamingWiki authenticated DRM reports");
        var root = Probe.ScratchDirectory();
        try { await SessionsAsync(root); await FailuresAsync(root); await CliAsync(root); await PcGamingWikiSessionRefreshProbes.RunAsync(root); }
        finally { Directory.Delete(root, true); }
    }

    private static async Task SessionsAsync(string root)
    {
        var path = Path.Combine(root, "session.json");
        var handler = new Handler(); using var http = new HttpClient(handler);
        using var source = new PcGamingWikiDrmSource(http, TimeSpan.Zero, path);
        await source.LoginAsync(new("Tester@GameArchiver", Password));
        var json = await File.ReadAllTextAsync(path);
        Probe.Check("login sends encoded secrets only in a POST body and shares the login-token cookie", handler.PostValid && handler.BootstrapCookie);
        Probe.Check("only verified session cookies are saved, never the bot password", json.Contains(SessionValue) && !json.Contains(Password)
            && (await new PcGamingWikiSessionStore(path).LoadAsync())?.Username == "Tester");
        if (!OperatingSystem.IsWindows()) Probe.Check("session file is private from creation on Unix",
            File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
        else
        {
            using var identity = WindowsIdentity.GetCurrent();
            var acl = new FileInfo(path).GetAccessControl();
            var privateRules = true;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                privateRules &= !rule.IsInherited && rule.IdentityReference.Equals(identity.User);
            Probe.Check("session file has a private non-inherited Windows ACL", acl.AreAccessRulesProtected && privateRules);
        }
        var reports = await source.GetAsync(new("steam", "10"));
        await source.GetAsync(new("steam", "20"));
        Probe.Check("authenticated lookups use exact Cargo AppIDs and page IDs, never the anonymous resolver",
            handler.ExactLookups == 2 && handler.AnonymousCalls == 0 && handler.Logins == 1 && handler.Identities == 1
            && reports[GameOs.Windows].Status == DrmStatus.Free && reports[GameOs.Linux].Status == DrmStatus.Unknown
            && reports[GameOs.Windows].Url == "https://www.pcgamingwiki.com/w/index.php?curid=7");
        var resumedHandler = new Handler(); using var resumedHttp = new HttpClient(resumedHandler);
        using var resumed = new PcGamingWikiDrmSource(resumedHttp, TimeSpan.Zero, path);
        var library = Library();
        library.DrmOverrides["steam:10"] = new() { [GameOs.Windows] = new() { Status = DrmStatus.Protected, Source = "manual", Notes = "Test" } };
        var refresh = await LibraryDrm.RefreshAsync(library, resumed, force: true);
        Probe.Check("a fresh client reuses saved cookies without a password and verifies the account once", resumedHandler.Logins == 0
            && resumedHandler.Identities == 1 && refresh.Updated == 2 && refresh.Error is null);
        Probe.Check("authenticated enrichment preserves manual overrides", DrmCatalog.Get(refresh.Library, new("steam", "10"), GameOs.Windows).Source == "manual");
        Probe.Check("diagnostic representations never expose passwords or session cookies", !new PcGamingWikiCredentials("Tester@Bot", Password).ToString().Contains(Password)
            && !(await new PcGamingWikiSessionStore(path).LoadAsync())!.Cookies[0].ToString().Contains(SessionValue));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var cancelled = false;
        try { await new PcGamingWikiSessionStore(path).SaveAsync((await new PcGamingWikiSessionStore(path).LoadAsync())!, cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Probe.Check("cancelled session saves preserve the previous login and leave no temporary file", cancelled && json == await File.ReadAllTextAsync(path)
            && Directory.GetFiles(root, "*.tmp").Length == 0);
    }

    private static async Task FailuresAsync(string root)
    {
        var path = Path.Combine(root, "session.json"); var before = await File.ReadAllTextAsync(path);
        foreach (var fault in new[] { "bad-password", "token-shape", "cloudflare", "html", "redirect", "throttled", "no-cookie", "bad-identity", "cancel", "timeout" })
        {
            using var cts = new CancellationTokenSource();
            var handler = new Handler { Fault = fault, Cancel = cts.Cancel }; using var http = new HttpClient(handler);
            using var source = new PcGamingWikiDrmSource(http, TimeSpan.Zero, path);
            string? error = null;
            try { await source.LoginAsync(new("Tester@GameArchiver", Password), cts.Token); }
            catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or TimeoutException) { error = e.Message; }
            Probe.Check("failed login preserves the previous session without retry or secret leakage: " + fault,
                error is not null && !error.Contains(Password) && !error.Contains(SessionValue) && handler.Logins <= 1
                && handler.Calls <= 3 && before == await File.ReadAllTextAsync(path));
            if (fault == "cloudflare") Probe.Check("Cloudflare refusal is distinguished from a rejected bot password", error!.Contains("Cloudflare") && error.Contains("before the API"));
        }
        foreach (var fault in new[] { "expired", "wrong-user", "permission", "cargo-shape", "parse-shape" })
        {
            var handler = new Handler { Fault = fault }; using var http = new HttpClient(handler);
            using var source = new PcGamingWikiDrmSource(http, TimeSpan.Zero, path);
            var result = await LibraryDrm.RefreshAsync(Library(), source, force: true);
            Probe.Check("authentication/API failures stop enrichment without anonymous fallback: " + fault,
                result.Error is not null && result.Updated == 0 && handler.AnonymousCalls == 0 && handler.Logins == 0
                && handler.ExactLookups <= 1 && result.Library.Games.All(g => g.Drm[GameOs.Windows].Status == DrmStatus.Unknown));
            if (fault == "permission") Probe.Check("missing Cargo permission has an actionable diagnostic", result.Error!.Contains("Cargo extension"));
        }
        foreach (var fault in new[] { "absent", "ambiguous" })
        {
            var handler = new Handler { Fault = fault }; using var http = new HttpClient(handler);
            using var source = new PcGamingWikiDrmSource(http, TimeSpan.Zero, path);
            Probe.Check("no title guessing for non-unique authenticated lookup: " + fault,
                (await source.GetAsync(new("steam", "10"))).Values.All(d => d.Status == DrmStatus.Unknown) && handler.Calls == 2);
        }
        var corrupt = Path.Combine(root, "corrupt.json"); await File.WriteAllTextAsync(corrupt, "{\"secret\":\"" + Password);
        var malformedHandler = new Handler(); using var malformedHttp = new HttpClient(malformedHandler);
        using var malformed = new PcGamingWikiDrmSource(malformedHttp, TimeSpan.Zero, corrupt);
        var failure = await LibraryDrm.RefreshAsync(Library(), malformed, force: true);
        Probe.Check("corrupt saved sessions fail before HTTP without disclosing file contents", failure.Error is not null
            && !failure.Error.Contains(Password) && malformedHandler.Calls == 0);
    }

    private static async Task CliAsync(string root)
    {
        var path = Path.Combine(root, "cli-session.json"); var snapshot = Path.Combine(root, "library.json");
        await Library().SaveAsync(snapshot);
        var oldUsername = Environment.GetEnvironmentVariable("GAMEARCHIVE_PCGW_USERNAME");
        var oldPassword = Environment.GetEnvironmentVariable("GAMEARCHIVE_PCGW_BOT_PASSWORD");
        var stdout = Console.Out; var stderr = Console.Error; using var output = new StringWriter(); using var errors = new StringWriter();
        try
        {
            Console.SetOut(output); Console.SetError(errors);
            Environment.SetEnvironmentVariable("GAMEARCHIVE_PCGW_USERNAME", "Tester@GameArchiver");
            Environment.SetEnvironmentVariable("GAMEARCHIVE_PCGW_BOT_PASSWORD", Password);
            var handler = new Handler(); using var http = new HttpClient(handler);
            var factoryCalls = 0;
            Task<int> Run(params string[] args) => ArchiveCommandLine.RunAsync([.. args, "--pcgw-session", path],
                storeFactory: _ => throw new Exception("Auth must not create a game store"),
                pcGamingWikiFactory: file => { factoryCalls++; return new(http, TimeSpan.Zero, file); });
            var login = await Run("auth", "login", "--source", "pcgamingwiki");
            var saved = File.Exists(path);
            Environment.SetEnvironmentVariable("GAMEARCHIVE_PCGW_USERNAME", null);
            Environment.SetEnvironmentVariable("GAMEARCHIVE_PCGW_BOT_PASSWORD", null);
            var status = await Run("auth", "status", "--source", "pcgamingwiki");
            var syncHandler = new Handler(); using var syncHttp = new HttpClient(syncHandler);
            var sync = await ArchiveCommandLine.RunAsync(["library", "drm", "sync", "--source", "steam", "--snapshot", snapshot,
                "--pcgw-session", path], pcGamingWikiFactory: file => new(syncHttp, TimeSpan.Zero, file));
            var stored = await ArchiveLibrary.LoadAsync(snapshot);
            var invalid = await Run("auth", "status", "--source", "pcgamingwiki", "--username", "ignored");
            var invalidSource = await Run("download", "--all", "--source", "pcgamingwiki");
            var logout = await Run("auth", "logout", "--source", "pcgamingwiki");
            var missing = await Run("auth", "status", "--source", "pcgamingwiki");
            Console.SetOut(stdout); Console.SetError(stderr);
            Probe.Check("CLI login/status/logout route independently of game stores and passwords are never printed",
                login == 0 && saved && status == 0 && logout == 0 && missing == 1 && !File.Exists(path) && factoryCalls == 1
                && !output.ToString().Contains(Password) && !errors.ToString().Contains(Password)
                && !output.ToString().Contains(SessionValue) && !errors.ToString().Contains(SessionValue));
            Probe.Check("DRM sync automatically reuses the selected session without prompting or logging in again",
                sync == 0 && syncHandler.Logins == 0 && stored.Games.All(g => g.Drm[GameOs.Windows].Status == DrmStatus.Free));
            Probe.Check("PCGamingWiki auth rejects irrelevant options and is not a game download source", invalid == 1 && invalidSource == 1);
        }
        finally
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            Environment.SetEnvironmentVariable("GAMEARCHIVE_PCGW_USERNAME", oldUsername);
            Environment.SetEnvironmentVariable("GAMEARCHIVE_PCGW_BOT_PASSWORD", oldPassword);
        }
    }

    private static ArchiveLibrary Library() => new() { Games = new[] { "10", "20" }.Select(id => new GameInfo(new("steam", id), "Game " + id, "game_" + id)
        { Versions = new() { [GameOs.Windows] = new("1") }, Drm = new() { [GameOs.Windows] = new() { Status = DrmStatus.Free, Source = "pcgamingwiki" } } }).ToList() };

    private sealed class Handler : HttpMessageHandler
    {
        public string? Fault;
        public Action? Cancel;
        public int Calls, Logins, Identities, ExactLookups, AnonymousCalls;
        public bool PostValid, BootstrapCookie;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var uri = request.RequestUri!;
            if (uri.AbsolutePath != "/w/api.php") { AnonymousCalls++; throw new Exception("Unexpected anonymous lookup"); }
            var cookies = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(';', values) : "";
            HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
            HttpResponseMessage Error(string code) => Json(new { error = new { code, info = Password } });
            if (uri.Query.Contains("type=login"))
            {
                if (Fault == "cloudflare") return new(HttpStatusCode.Forbidden) { Content = new StringContent("<title>Cloudflare challenge</title>") };
                if (Fault == "html") return new(HttpStatusCode.OK) { Content = new StringContent("<html>Access denied</html>") };
                if (Fault == "token-shape") return Json(new { query = new { tokens = new { other = Password } } });
                var response = Json(new { query = new { tokens = new { logintoken = "fake-login-token+\\" } } });
                response.Headers.Add("Set-Cookie", "bootstrap=one; Path=/w; Secure; HttpOnly"); return response;
            }
            if (request.Method == HttpMethod.Post)
            {
                Logins++;
                var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(s => s.Split('=', 2))
                    .ToDictionary(s => Uri.UnescapeDataString(s[0]), s => Uri.UnescapeDataString(s[1].Replace('+', ' ')));
                PostValid = uri.Query == "" && form["lgname"] == "Tester@GameArchiver" && form["lgpassword"] == Password
                    && form["lgtoken"] == "fake-login-token+\\" && form["action"] == "login";
                BootstrapCookie = cookies.Contains("bootstrap=one");
                if (Fault == "cancel") { Cancel?.Invoke(); ct.ThrowIfCancellationRequested(); }
                if (Fault == "timeout") throw new OperationCanceledException();
                if (Fault == "redirect") return new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new("https://example.org/collect") } };
                if (Fault == "throttled") return new(HttpStatusCode.TooManyRequests);
                if (Fault == "bad-password") return Json(new { login = new { result = "Failed", reason = Password } });
                var response = Json(new { login = new { result = "Success", lguserid = 42, lgusername = "Tester" } });
                if (Fault != "no-cookie")
                {
                    response.Headers.Add("Set-Cookie", "session=" + SessionValue + "; Path=/w; Secure; HttpOnly");
                    response.Headers.Add("Set-Cookie", "emptyPreference=; Path=/w; Secure");
                }
                return response;
            }
            if (!uri.Query.Contains("assert=user")) throw new Exception("Authenticated requests must assert a logged-in user");
            if (uri.Query.Contains("meta=userinfo"))
            {
                Identities++;
                if (!cookies.Contains("session=" + SessionValue) || Fault == "expired") return Error("assertuserfailed");
                if (Fault == "bad-identity") return Json(new { query = new { userinfo = new { id = 0, anon = "" } } });
                return Json(new { query = new { userinfo = new { id = Fault == "wrong-user" ? 43 : 42, name = "Tester" } } });
            }
            if (uri.Query.Contains("action=cargoquery"))
            {
                var query = Uri.UnescapeDataString(uri.Query);
                if (query.Contains("fields=Game._pageID=PageID") && query.Contains("limit=2") &&
                    (query.Contains("Game.Steam_AppID HOLDS \"10\"") || query.Contains("Game.Steam_AppID HOLDS \"20\""))) ExactLookups++;
                if (Fault == "permission") return Error("permissiondenied");
                if (Fault == "cargo-shape") return Json(new { cargoquery = "bad" });
                return Json(new { cargoquery = Enumerable.Range(0, Fault == "absent" ? 0 : Fault == "ambiguous" ? 2 : 1)
                    .Select(_ => new { title = new { PageID = "7" } }).ToArray() });
            }
            if (uri.Query.Contains("action=parse") && uri.Query.Contains("pageid=7"))
            {
                if (Fault == "parse-shape") return Json(new { parse = new { title = "No wikitext" } });
                return Json(new { parse = new { wikitext = new Dictionary<string, string>
                    { ["*"] = "{{Availability/row|Steam|10|DRM-free}}\n{{Availability/row|Steam|20|None}}" } } });
            }
            throw new Exception("Unexpected PCGamingWiki request");
        }
    }
}
