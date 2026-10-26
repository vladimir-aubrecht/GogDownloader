using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameArchiver;
using GameArchiver.Core;
using GogArchiver.Core;
using GogArchiver.Core.Content;
using GogArchiver.Core.Download;
using GogArchiver.Core.Download.Zip;
using GogArchiver.Core.Library;
using SteamArchiver.Core;
using SteamStore = SteamArchiver.Core.SteamClient;

namespace GogArchiver.Probes;

public static class ArchiveRegressionProbes
{
    public static async Task RunAsync()
    {
        var root = Probe.ScratchDirectory();
        try
        {
            await LargeInstallerAsync(root);
            await MalformedDirectoryAsync(root);
            await PermissionsAsync(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task LargeInstallerAsync(string root)
    {
        Probe.Group("Complete ZIP64 installer inventories");
        foreach (var count in new[] { 65534, 65535, 65536 })
        {
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            {
                zip.Comment = new string('x', 1024); // EOCD cannot be found in a 100-byte tail.
                for (var i = 0; i < count - 1; i++) zip.CreateEntry($"data/noarch/empty-{i:D5}");
                using var stream = zip.CreateEntry("data/noarch/game.bin").Open();
                stream.Write(Encoding.UTF8.GetBytes("essential payload"));
            }
            var zipBytes = bytes.ToArray();
            var path = Path.Combine(root, "large.zip");
            await File.WriteAllBytesAsync(path, zipBytes);
            Probe.Check($"ZIP inspector reads every one of {count} records", ZipInspector.ReadCentralDirectory(path).Count == count);
            using var fixture = new InstallerFixture(zipBytes);
            using var http = new HttpClient(fixture);
            var source = new InstallerSource(new(http), http, 1, "en");
            var plan = await source.PlanAsync(default);
            var game = plan.Files.SingleOrDefault(f => f.RelativePath == "game.bin");
            var payload = game is null ? [] : await source.FetchAsync(game, game.Parts.Single(), null, default);
            Probe.Check($"installer with {count} entries includes and reads the last game file",
                plan.Files.Count == count && Encoding.UTF8.GetString(payload) == "essential payload");
            if (count == 65535)
            {
                var end = zipBytes.Length - 22 - 1024;
                var classic = zipBytes[..(end - 76)].Concat(zipBytes[end..]).ToArray();
                await File.WriteAllBytesAsync(path, classic);
                using var classicFixture = new InstallerFixture(classic);
                using var classicHttp = new HttpClient(classicFixture);
                var classicReader = new MojoSetupReader(classicHttp, "https://fixture.invalid/installer.sh");
                await classicReader.OpenAsync(default);
                Probe.Check("classic ZIP with exactly 65,535 entries needs no ZIP64 locator",
                    ZipInspector.ReadCentralDirectory(path).Count == count && classicReader.Entries.Count == count);
            }
        }
    }

    private static async Task MalformedDirectoryAsync(string root)
    {
        Probe.Group("Header verification rejects incomplete central directories");
        var directory = Path.Combine(root, "headers", "game");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "game.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "one", "two" })
            { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(name); }
        }
        var bytes = await File.ReadAllBytesAsync(path);
        var headers = ZipInspector.ReadCentralDirectory(path);
        await new BackupManifest { GameId = 1, Slug = "game", Title = "Game", Platforms = new()
        { [GamePlatform.Windows] = new() { BuildId = "1", Language = "en", ArchiveName = "game.zip", ArchiveSize = bytes.Length, InstalledSize = 6 } } }.SaveAsync(directory);
        await using var archive = new GameArchiverClient(new GogClient());
        var end = bytes.Length - 22;
        foreach (var damage in new[] { "first header", "later header", "too few records", "too many records", "short directory", "bad offset", "truncated end" })
        {
            var bad = bytes.ToArray();
            switch (damage)
            {
                case "first header": bad[(int)headers[0].CentralHeaderOffset] = 0; break;
                case "later header": bad[(int)headers[1].CentralHeaderOffset] = 0; break;
                case "too few records": SetCount(bad, end, 1); break;
                case "too many records": SetCount(bad, end, 3); break;
                case "short directory": BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(end + 12), 47); break;
                case "bad offset": BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(end + 16), (uint)bad.Length + 1); break;
                default: bad = bad[..^1]; break;
            }
            await File.WriteAllBytesAsync(path, bad);
            var results = await archive.VerifyAsync(Path.GetDirectoryName(directory)!);
            Probe.Check($"default verification reports {damage} as broken", results.Count == 1 && results[0].Problems.Count > 0);
            var refused = false;
            try { ZipFileEditor.Plan(path, new() { Operation = ArchiveFileOperation.Add, FilePath = path, EntryPath = "readme.txt" }); }
            catch (Exception e) when (e is InvalidDataException or ZipFormatException or IOException) { refused = true; }
            Probe.Check($"archive editing refuses {damage}", refused);
        }
        await File.WriteAllBytesAsync(path, bytes);
        Probe.Check("an intact directory still verifies", (await archive.VerifyAsync(Path.GetDirectoryName(directory)!)).Single().Problems.Count == 0);
        // The installer parser must reject mismatched counts too, instead of planning a prefix.
        var wrongCount = bytes.ToArray(); SetCount(wrongCount, end, 1);
        using var fixture = new InstallerFixture(wrongCount);
        using var http = new HttpClient(fixture);
        var rejected = false;
        try { await new MojoSetupReader(http, "https://fixture.invalid/installer.sh").OpenAsync(default); }
        catch (InstallerFormatException) { rejected = true; }
        Probe.Check("installer count mismatch fails before planning", rejected);
    }

    private static void SetCount(byte[] bytes, int end, ushort count)
    { BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 8), count); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 10), count); }

    private static async Task PermissionsAsync(string root)
    {
        Probe.Group("Portable Unix permissions through downloads and ZIP edits");
        var source = Path.Combine(root, "mode-source"); Directory.CreateDirectory(source);
        var file = Path.Combine(source, "run.sh"); await File.WriteAllTextAsync(file, "#!/bin/sh\necho ok\n");
        var path = Path.Combine(root, "modes.zip");
        await ParallelZipWriter.CreateAsync(source, path, 2, unixModes: new Dictionary<string, UnixFileMode> { ["run.sh"] = (UnixFileMode)0x1e8 }); // 0750, regardless of host
        CheckMode("explicit Unix permissions override the staging host", path, "run.sh", 0x1e8);
        var prepared = await ZipFileEditor.PrepareAsync(file, Path.Combine(root, "replacement.deflate"));
        var request = new ArchiveFileRequest { Operation = ArchiveFileOperation.Replace, FilePath = file, Match = "run.sh" };
        var changes = ZipFileEditor.Plan(path, request);
        var edited = Path.Combine(root, "edited.zip");
        await ZipFileEditor.RewriteAsync(path, edited, prepared, changes);
        CheckMode("ZIP edits preserve executable permissions on the replacement", edited, "run.sh", 0x1e8);
        CheckMode("ZIP edits preserve executable permissions on the backup", edited, "run.sh.backup", 0x1e8);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, (UnixFileMode)0x1ed);
            var hostZip = Path.Combine(root, "host.zip");
            await ParallelZipWriter.CreateAsync(source, hostZip, 2);
            CheckMode("ZIP writer captures the source filesystem mode", hostZip, "run.sh", 0x1ed);
            var extracted = Path.Combine(root, "extracted"); ZipFile.ExtractToDirectory(hostZip, extracted);
            Probe.Check("extraction restores executable bits", (File.GetUnixFileMode(Path.Combine(extracted, "run.sh")) & UnixFileMode.UserExecute) != 0);
        }
        using var fixture = new InstallerFixture(BuildUnixInstaller());
        using var http = new HttpClient(fixture);
        var game = new LibraryEntry { Id = 1, Slug = "modes", Title = "Modes", Platforms = new()
        { [GamePlatform.Linux] = new() { IsLinux = true, Installers = [new("en", "1", fixture.InstallerLength)] } } };
        var options = new DownloadOptions { OutputRoot = Path.Combine(root, "gog"), StagingRoot = Path.Combine(root, "gog-stage"),
            Platform = GamePlatform.Linux, WriteLauncher = false, MaxCompressionThreads = 2 };
        var result = await new GameDownloader(new(http), new(http, http, new(0)), http).RunAsync(game, options);
        Probe.Check("Linux installer download completes", result.Outcome == DownloadOutcome.Completed);
        CheckMode("GOG installer executable mode reaches the final ZIP", result.ArchivePath!, "run.sh", 0x1e8);
        CheckMode("dereferenced GOG links inherit the executable's mode", result.ArchivePath!, "alias.sh", 0x1e8);
        CheckMode("GOG directory permissions reach the final ZIP", result.ArchivePath!, "private/", 0x1c0);
        CheckMode("GOG directory aliases preserve target permissions", result.ArchivePath!, "copy/", 0x1c0);
        CheckMode("executables below a directory alias keep their permissions", result.ArchivePath!, "copy/run.sh", 0x1e8);
        var old = (await BackupManifest.LoadAsync(Path.GetDirectoryName(result.ArchivePath!)!))!;
        old.Platforms[GamePlatform.Linux] = old.Platforms[GamePlatform.Linux] with { ContentRevision = 1 };
        await old.SaveAsync(Path.GetDirectoryName(result.ArchivePath!)!);
        Probe.Check("legacy GOG Linux archives need a one-time content refresh",
            old.Platforms[GamePlatform.Linux].RequiresContentRefresh(GamePlatform.Linux)
            && (await ArchiveMetadata.LoadAsync(Path.GetDirectoryName(result.ArchivePath!)!))!.Platforms[GameOs.Linux].NeedsRefresh);
        using var steamSession = new PermissionSteamSession();
        await using var steam = new SteamStore(steamSession);
        var steamOptions = new ArchiveOptions { OutputRoot = Path.Combine(root, "steam"), StagingRoot = Path.Combine(root, "steam-stage"),
            Platform = GameOs.Linux, CompressionThreads = 2 };
        var steamResult = await steam.DownloadAsync("1", steamOptions);
        CheckMode("Steam executable flags reach the final ZIP", steamResult.ArchivePath!, "run.sh", 0x1ed);
        CheckMode("Steam assets remain non-executable", steamResult.ArchivePath!, "data", 0x1a4);
        CheckMode("dereferenced Steam links inherit target permissions", steamResult.ArchivePath!, "alias.sh", 0x1ed);
        var steamDirectory = Path.GetDirectoryName(steamResult.ArchivePath!)!;
        var metadata = (await ArchiveMetadata.LoadAsync(steamDirectory))!;
        metadata.Platforms[GameOs.Linux] = metadata.Platforms[GameOs.Linux] with { Version = metadata.Platforms[GameOs.Linux].Version with { ContentRevision = 1 } };
        await metadata.SaveAsync(steamDirectory);
        Probe.Check("old Steam Linux metadata requests a refresh", (await ArchiveMetadata.LoadAsync(steamDirectory))!.Platforms[GameOs.Linux].NeedsRefresh);
        var transfers = steamSession.Transfers;
        var refreshed = await steam.DownloadAsync("1", steamOptions);
        Probe.Check("permission refresh reuses verified Steam payloads", refreshed.Outcome == ArchiveOutcome.Completed && steamSession.Transfers == transfers);
        CheckMode("cached content keeps current Steam executable flags", refreshed.ArchivePath!, "run.sh", 0x1ed);
        Probe.Check("permission-aware Steam archive skips on the next run", (await steam.DownloadAsync("1", steamOptions)).Outcome == ArchiveOutcome.AlreadyDone);
    }

    private static void CheckMode(string label, string path, string entry, int mode)
    {
        using var zip = ZipFile.OpenRead(path);
        var file = zip.GetEntry(entry);
        Probe.Check(label, file is not null && ((uint)file.ExternalAttributes >> 16 & 0x1ff) == mode);
    }

    private static byte[] BuildUnixInstaller()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            void Add(string name, string data, int attributes)
            { var e = zip.CreateEntry("data/noarch/" + name); e.ExternalAttributes = attributes; using var stream = e.Open(); stream.Write(Encoding.UTF8.GetBytes(data)); }
            Add("run.sh", "#!/bin/sh\necho ok\n", unchecked((int)0x81e80000));
            Add("alias.sh", "run.sh", unchecked((int)0xa1ff0000));
            Add("private/", "", 0x41c00010);
            Add("private/run.sh", "#!/bin/sh\necho ok\n", unchecked((int)0x81e80000));
            Add("copy", "private", unchecked((int)0xa1ff0000));
        }
        var result = bytes.ToArray();
        // Fixtures represent Unix-created MojoSetup ZIPs on every test host.
        for (var at = 0; at + 46 <= result.Length; at++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(at)) == 0x02014b50) result[at + 5] = 3;
        return result;
    }

    private sealed class InstallerFixture : HttpMessageHandler
    {
        private readonly byte[] _installer;
        public long InstallerLength => _installer.Length;
        public InstallerFixture(byte[] zip) => _installer = [.. Encoding.UTF8.GetBytes("#!/bin/sh\n"), .. zip];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/installer.sh")
            {
                var range = request.Headers.Range!.Ranges.Single();
                var start = (int)range.From!.Value; var end = (int)Math.Min(_installer.Length - 1, range.To!.Value);
                var content = new ByteArrayContent(_installer[start..(end + 1)]);
                content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, _installer.Length);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
            }
            object body = request.RequestUri.AbsolutePath == "/downlink" ? new { downlink = "https://fixture.invalid/installer.sh" }
                : new { id = 1, title = "Modes", downloads = new { installers = new[] { new { os = "linux", language = "en", version = "1",
                    total_size = _installer.Length, files = new[] { new { id = "1", size = _installer.Length, downlink = "https://fixture.invalid/downlink" } } } } } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) });
        }
    }

    private sealed class PermissionSteamSession : ISteamSession, ISteamContentSession, IDisposable
    {
        private readonly byte[] _data = Encoding.UTF8.GetBytes("content");
        public int Transfers;
        public Task<IReadOnlySet<uint>> GetOwnedAppIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlySet<uint>>(new HashSet<uint> { 1 });
        public Task<SteamApp> GetAppAsync(uint id, CancellationToken ct) => Task.FromResult(new SteamApp(id, "Permissions")
        { Platforms = [GameOs.Linux], Builds = new() { ["public"] = "1" }, Depots = [new(10) { OsList = "linux", Manifests = new() { ["public"] = 100 } }] });
        public Task<ISteamContentSession> OpenContentAsync(long maxBytesPerSecond, CancellationToken ct) => Task.FromResult<ISteamContentSession>(this);
        public Task<SteamManifest> GetManifestAsync(SteamDepotRequest depot, CancellationToken ct)
        {
            var hash = Convert.ToHexStringLower(SHA1.HashData(_data));
            SteamFile File(string path, bool executable = false) => new(path, _data.Length, hash, [new(hash, 0, 0, _data.Length, _data.Length)]) { IsExecutable = executable };
            return Task.FromResult(new SteamManifest(depot, [File("run.sh", true), File("data"), new("alias.sh", 0, "", []) { LinkTarget = "run.sh" }]));
        }
        public Task<byte[]> GetChunkAsync(SteamDepotRequest depot, SteamChunk chunk, CancellationToken ct) { Transfers++; return Task.FromResult(_data); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }
}
