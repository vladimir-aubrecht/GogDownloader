using System.IO.Compression;
using System.Net;
using System.Text;
using GogArchiver.Core.Api;
using GogArchiver.Core.Content;
using GogArchiver.Core.Download;

namespace GogArchiver.Probes;

/// <summary>
/// Path B. A MojoSetup installer is a shell script with a ZIP glued on, and the whole point is
/// to read the game out of it over HTTP Range without ever downloading the installer. That is
/// only worth believing if a real ranged server is on the other end, so these probes start one.
/// </summary>
public static class InstallerProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Choosing a Linux installer");
        var english = new Installer { Os = "linux", Language = "en" };
        var german = new Installer { Os = "linux", Language = "de" };
        var polish = new Installer { Os = "linux", Language = "pl" };

        Probe.Check("a single installer is taken whatever its language",
            InstallerSource.Select([german], "en") == german);
        Probe.Check("the requested language wins",
            InstallerSource.Select([english, german], "de") == german);
        // gogdl relies on this: English installers ship every language.
        Probe.Check("English is the fallback",
            InstallerSource.Select([english, german], "fr") == english);
        Probe.Check("without English there is nothing to fall back to",
            InstallerSource.Select([german, polish], "fr") is null);
        Probe.Check("no installers means no choice", InstallerSource.Select([], "en") is null);

        var payload = BuildInstaller();
        using var server = new RangeServer(payload);

        Probe.Group("Reading a MojoSetup installer over Range");
        using var http = new HttpClient();
        var reader = new MojoSetupReader(http, server.Url);
        await reader.OpenAsync(default);

        // The claim of path B: opening the installer costs a constant, not its size. Two MiB of
        // prologue, a hundred bytes of tail, and the central directory.
        var metadataBytes = server.BytesServed;
        Probe.Check($"opening cost {metadataBytes / 1024} KiB, not the installer's {payload.Length / 1024 / 1024} MiB",
            metadataBytes < 3 * 1024 * 1024, metadataBytes.ToString());

        var names = reader.Entries.Select(e => e.Name).ToList();
        Probe.Check("the ZIP is found past the shell prologue", names.Count > 0);
        Probe.Check("game files are listed", names.Contains("data/noarch/game/run.sh"), string.Join(", ", names));
        Probe.Check("installer scaffolding is listed too", names.Contains("scripts/mojosetup.lua"));

        var game = reader.Entries.First(e => e.Name == "data/noarch/game/data.bin");
        var content = await reader.ReadEntryAsync(game, null, default);
        Probe.Check("an entry inflates to its stated size", content.Length == game.Size, content.Length.ToString());
        Probe.Check("the entry's bytes are the ones we wrote", content[0] == 7 && content[^1] == 7);

        var beforeSmall = server.BytesServed;
        var small = reader.Entries.First(e => e.Name == "data/noarch/game/run.sh");
        var script = Encoding.UTF8.GetString(await reader.ReadEntryAsync(small, null, default));
        Probe.Check("a small entry round-trips", script == "#!/bin/sh\nexec ./game\n", script);

        // Reading one entry fetches that entry, not the file it lives in.
        var costOfSmall = server.BytesServed - beforeSmall;
        Probe.Check($"reading a 21-byte file cost {costOfSmall} bytes", costOfSmall < 1024, costOfSmall.ToString());

        Probe.Group("Path C: Inno Setup installers");
        Probe.Check("innoextract is detected on PATH", InnoSetupExtractor.IsAvailable);

        // The install hint has to name a tool that exists on the machine reading it.
        var hint = new InnoExtractMissingException().Message;
        var expected = OperatingSystem.IsWindows() ? "winget"
            : OperatingSystem.IsMacOS() ? "brew" : "apt";
        Probe.Check($"the missing-innoextract hint suits this OS ({expected})", hint.Contains(expected), hint);
        Probe.Check("the hint does not send Windows users to apt or brew",
            OperatingSystem.IsWindows() ? !hint.Contains("apt install") && !hint.Contains("brew") : true, hint);
        // GOG's real filenames push Windows paths past MAX_PATH, which innoextract cannot open,
        // so the parts are renamed — but consistently, or the slices stop being one installer.
        Probe.Check("the setup exe gets the shortest name",
            InnoSetupExtractor.PartNameFrom("https://cdn/x/setup_bg3_v4.1.1_%2864bit%29_%2889470%29.exe?token=1", "id") == "s.exe");
        Probe.Check("a .bin slice keeps its number beside the renamed exe",
            InnoSetupExtractor.PartNameFrom("https://cdn/setup_game-2.bin", "id") == "s-2.bin");
        Probe.Check("an unrecognised part keeps its real, URL-decoded name",
            InnoSetupExtractor.PartNameFrom("https://cdn/read%20me.txt", "id") == "read me.txt");
        Probe.Check("a nameless URL falls back to the file id",
            InnoSetupExtractor.PartNameFrom("https://cdn/", "77") == "setup_77.exe");

        // Feeding it something that is not an Inno Setup installer must fail loudly, and clean up.
        var scratch = Probe.ScratchDirectory();
        var notAnInstaller = Path.Combine(scratch, "setup.exe");
        await File.WriteAllTextAsync(notAnInstaller, "MZ this is not Inno Setup");

        var target = Path.Combine(scratch, "staging");
        var extractScratch = Path.Combine(scratch, "1.x");
        Directory.CreateDirectory(target);

        var failed = false;
        try { await InnoSetupExtractor.ExtractAsync(notAnInstaller, target, extractScratch, default); }
        catch (InnoExtractFailedException) { failed = true; }

        Probe.Check("a file that is not an installer is rejected", failed);
        Probe.Check("a failed extraction leaves no scratch directory", !Directory.Exists(extractScratch));
        Directory.Delete(scratch, recursive: true);

        Probe.Group("Symlink targets are resolved from the install root");
        Probe.Check("a sibling target", InstallerSource.ResolveLink("game/lib/a.so", "b.so") == "game/lib/b.so");
        Probe.Check("a parent target", InstallerSource.ResolveLink("game/lib/a.so", "../bin/b.so") == "game/bin/b.so");
        Probe.Check("an absolute target", InstallerSource.ResolveLink("game/a", "/game/b") == "game/b");

        var escaped = false;
        try { InstallerSource.ResolveLink("a", "../../etc/passwd"); }
        catch (InstallerFormatException) { escaped = true; }
        Probe.Check("a target escaping the game is refused", escaped);
    }

    /// <summary>A shell prologue, then a ZIP — the shape of every MojoSetup installer.</summary>
    private static byte[] BuildInstaller()
    {
        var prologue = Encoding.ASCII.GetBytes(
            "#!/bin/sh\n# MojoSetup self-extracting installer\n" + new string('#', 700 * 1024) + "\nexit 1\n");

        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Big enough that reading it all would be obvious in the byte counter.
            var big = archive.CreateEntry("data/noarch/game/data.bin", CompressionLevel.NoCompression);
            using (var stream = big.Open())
            {
                var buffer = new byte[1 << 20];
                Array.Fill(buffer, (byte)7);
                for (var i = 0; i < 32; i++) stream.Write(buffer);
            }

            using (var stream = archive.CreateEntry("data/noarch/game/run.sh", CompressionLevel.Optimal).Open())
                stream.Write(Encoding.UTF8.GetBytes("#!/bin/sh\nexec ./game\n"));

            using (var stream = archive.CreateEntry("scripts/mojosetup.lua", CompressionLevel.Optimal).Open())
                stream.Write(Encoding.UTF8.GetBytes("-- not part of the game"));
        }

        return [.. prologue, .. zip.ToArray()];
    }

    /// <summary>The smallest HTTP server that honours a single byte range.</summary>
    private sealed class RangeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _payload;
        private long _served;

        public string Url { get; }
        public long BytesServed => Interlocked.Read(ref _served);

        public RangeServer(byte[] payload)
        {
            _payload = payload;

            var port = Random.Shared.Next(20000, 60000);
            Url = $"http://127.0.0.1:{port}/installer.sh";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) { return; }

                var range = context.Request.Headers["Range"];
                var (from, to) = ParseRange(range, _payload.Length);

                var length = (int)(to - from + 1);
                Interlocked.Add(ref _served, length);

                context.Response.StatusCode = 206;
                context.Response.Headers["Content-Range"] = $"bytes {from}-{to}/{_payload.Length}";
                context.Response.ContentLength64 = length;
                await context.Response.OutputStream.WriteAsync(_payload.AsMemory((int)from, length));
                context.Response.Close();
            }
        }

        private static (long From, long To) ParseRange(string? header, long size)
        {
            if (header is null) return (0, size - 1);

            var span = header["bytes=".Length..].Split('-');
            var from = long.Parse(span[0]);
            var to = span[1].Length > 0 ? long.Parse(span[1]) : size - 1;
            return (from, Math.Min(to, size - 1));
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }
}
