using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameArchiver.Core;

namespace SteamArchiver.Core;

/// <summary>Standalone Steam library and archive API, implemented directly with SteamKit2.</summary>
public sealed class SteamClient : IGameStore
{
    public string Source => "steam";
    private readonly SteamClientOptions _options;
    private ISteamSession _session;
    private readonly bool _injected;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private async Task EnsureSessionAsync()
    {
        if (!_injected && _session is SteamKitSession { Disconnected: true })
        { await _session.DisposeAsync(); _session = new SteamKitSession(_options); }
    }
    public SteamClient(SteamClientOptions? options = null)
    { _options = options ?? new(); _session = new SteamKitSession(_options); }
    public SteamClient(ISteamSession session, SteamClientOptions? options = null)
    { _session = session; _options = options ?? new(); _injected = true; }

    public async Task LoginAsync(SteamCredentials credentials, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_injected) throw new InvalidOperationException("Authentication belongs to the injected Steam session.");
            await _session.DisposeAsync();
            var kit = new SteamKitSession(_options);
            _session = kit;
            await kit.LoginAsync(credentials, ct);
        }
        finally { _gate.Release(); }
    }
    public Task<SteamTokens?> GetAuthenticationAsync(CancellationToken ct = default) => SteamTokenFile.LoadAsync(_options.TokenPath, ct);
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_injected) throw new InvalidOperationException("Authentication belongs to the injected Steam session.");
            await _session.DisposeAsync();
            _session = new SteamKitSession(_options);
            File.Delete(_options.TokenPath);
        }
        finally { _gate.Release(); }
    }

    public async Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureSessionAsync();
            var owned = await _session.GetOwnedAppIdsAsync(ct);
            await _session.PreloadAppsAsync(owned, ct);
            var games = new List<GameInfo>();
            var failures = new Dictionary<string, string>();
            var names = new Dictionary<string, string>();
            foreach (var id in owned.Order())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var app = await _session.GetAppAsync(id, ct);
                    names[id.ToString()] = app.Title;
                    var dlcs = app.Dlcs.Concat(app.Depots.Select(d => d.DlcAppId).Where(d => d > 0))
                        .Where(owned.Contains).Distinct().Order().Select(d => d.ToString()).ToList();
                    var game = new GameInfo(new(Source, id.ToString()), app.Title, ArchivePaths.Slug(app.Title))
                    {
                        Kind = app.Type,
                        OwnedDlcs = dlcs,
                        Versions = app.Platforms.ToDictionary(p => p, _ => new GameVersion(app.Builds.GetValueOrDefault("public", ""))
                        { Name = app.Builds.GetValueOrDefault("public"), Dlcs = dlcs }),
                    };
                    foreach (var platform in app.Platforms.Where(game.CanBackUp))
                    {
                        try
                        {
                            // Reuse the same selection as download, including shared and DLC depots.
                            // Ownership and bulk appinfo have already been loaded for this sync.
                            game.Versions[platform] = (await SelectAsync(app, owned, new() { Platform = platform }, ct)).Version;
                        }
                        catch (NotSupportedException)
                        {
                            // The default English/64-bit/public selection may be unavailable.
                            // Keep the product discoverable; an empty manifest map makes diff
                            // request a live refresh for the user's explicit selection.
                        }
                    }
                    games.Add(game);
                }
                catch (Exception e) when (e is not OperationCanceledException) { failures[id.ToString()] = e.Message; }
            }
            var missingNames = failures.Keys.Where(id => !names.ContainsKey(id)).Select(uint.Parse).ToHashSet();
            if (missingNames.Count > 0)
            {
                try
                {
                    foreach (var (id, name) in await _session.GetAppNamesAsync(missingNames, ct))
                        if (missingNames.Contains(id) && !string.IsNullOrWhiteSpace(name)) names[id.ToString()] = name;
                }
                catch (Exception e) when (e is not OperationCanceledException) { /* Optional labels must not discard a successful sync. */ }
            }
            return new(games) { Failures = failures, FailureNames = names.Where(p => failures.ContainsKey(p.Key)).ToDictionary() };
        }
        finally { _gate.Release(); }
    }

    private sealed record Selection(GameInfo Game, GameVersion Version, List<SteamDepotRequest> Depots, SteamApp App);
    private async Task<Selection> SelectAsync(string productId, GameSelection options, CancellationToken ct)
    {
        options.Validate();
        await EnsureSessionAsync();
        if (!uint.TryParse(productId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id == 0)
            throw new ArgumentException("Steam product ID must be a positive AppID.", nameof(productId));
        var owned = await _session.GetOwnedAppIdsAsync(ct);
        if (!owned.Contains(id)) throw new SteamAuthenticationException($"Steam app {id} is not in the licensed library.");
        var app = await _session.GetAppAsync(id, ct);
        if (app.Type == "dlc")
        {
            if (app.ParentAppId == 0 || !owned.Contains(app.ParentAppId))
                throw new InvalidOperationException($"DLC {id} has no licensed base game in the library.");
            app = await _session.GetAppAsync(app.ParentAppId, ct);
        }
        return await SelectAsync(app, owned, options, ct);
    }

    private async Task<Selection> SelectAsync(SteamApp app, IReadOnlySet<uint> owned, GameSelection options, CancellationToken ct)
    {
        if (!app.Platforms.Contains(options.Platform)) throw new NotSupportedException($"{app.Title} has no {options.Platform} release.");
        if (!app.Builds.TryGetValue(options.Branch, out var buildId))
            throw new NotSupportedException($"Steam branch '{options.Branch}' is unavailable or password-protected for {app.AppId}.");
        var language = SteamLanguages.Name(options.Language);
        var os = options.Platform switch { GameOs.Windows => "windows", GameOs.Mac => "macos", _ => "linux" };
        var dlcs = app.Dlcs.Concat(app.Depots.Select(d => d.DlcAppId).Where(id => id > 0))
            .Where(owned.Contains).Distinct().Order().ToList();
        var apps = new List<SteamApp> { app };
        foreach (var dlc in dlcs) apps.Add(await _session.GetAppAsync(dlc, ct));
        var selected = new Dictionary<uint, (SteamDepotRequest Request, bool Dlc, long? DownloadSize, long? InstalledSize)>();
        foreach (var parent in apps)
        {
            var eligible = new List<SteamDepot>();
            foreach (var original in parent.Depots)
            {
                // Public appinfo can advertise private/shared DLC we do not own, or another OS.
                // Filter these before querying the source application's protected appinfo.
                if (original.DlcAppId != 0 && !owned.Contains(original.DlcAppId)) continue;
                if (original.OsList.Length > 0 && !original.OsList.Split(',').Contains(os, StringComparer.OrdinalIgnoreCase)) continue;
                var depot = original;
                var visited = new HashSet<uint>();
                // A shared depot can already publish its manifest in the licensed app's
                // appinfo. Only look up its source when the selected branch is missing:
                // source appinfo may be private even though this depot is accessible.
                while (depot.SharedFromApp != 0 && !depot.Manifests.ContainsKey(options.Branch))
                {
                    if (!visited.Add(depot.SharedFromApp)) throw new InvalidDataException("Cyclic shared Steam depot.");
                    var shared = (await _session.GetAppAsync(depot.SharedFromApp, ct)).Depots.SingleOrDefault(d => d.DepotId == depot.DepotId)
                        ?? throw new InvalidDataException($"Shared depot {depot.DepotId} is missing.");
                    depot = shared with { DlcAppId = original.DlcAppId != 0 ? original.DlcAppId : shared.DlcAppId,
                        OsList = original.OsList.Length > 0 ? original.OsList : shared.OsList,
                        Architecture = original.Architecture.Length > 0 ? original.Architecture : shared.Architecture,
                        Language = original.Language.Length > 0 ? original.Language : shared.Language };
                }
                if (depot.DlcAppId != 0 && !owned.Contains(depot.DlcAppId)) continue;
                if (depot.OsList.Length > 0 && !depot.OsList.Split(',').Contains(os, StringComparer.OrdinalIgnoreCase)) continue;
                eligible.Add(depot);
            }
            var compatible = new List<SteamDepot>();
            foreach (var group in eligible.GroupBy(d => d.DlcAppId))
            {
                var variants = group.ToList();
                if (variants.Any(d => d.Architecture.Length > 0) && !variants.Any(d => d.Architecture == options.Architecture))
                    throw new NotSupportedException($"Steam architecture '{options.Architecture}' is unavailable for app {parent.AppId}, depot group {group.Key}.");
                variants = variants.Where(d => d.Architecture.Length == 0 || d.Architecture == options.Architecture).ToList();
                if (variants.Any(d => d.Language.Length > 0) && !variants.Any(d => string.Equals(d.Language, language, StringComparison.OrdinalIgnoreCase)))
                    throw new NotSupportedException($"Steam language '{language}' is unavailable for app {parent.AppId}, depot group {group.Key}.");
                compatible.AddRange(variants.Where(d => d.Language.Length == 0 || string.Equals(d.Language, language, StringComparison.OrdinalIgnoreCase)));
            }
            foreach (var depot in compatible)
            {
                if (!depot.Manifests.TryGetValue(options.Branch, out var manifest))
                    throw new NotSupportedException($"No accessible '{options.Branch}' manifest for depot {depot.DepotId}; private branches are not supported.");
                var isDlc = parent.AppId != app.AppId || depot.DlcAppId != 0;
                var sameManifest = selected.TryGetValue(depot.DepotId, out var existing) && existing.Request.ManifestId == manifest;
                var request = sameManifest
                    ? existing.Request : new SteamDepotRequest(parent.AppId, depot.DepotId, manifest, options.Branch);
                long? downloadSize = depot.DownloadSizes.TryGetValue(options.Branch, out var download) ? download : null;
                long? installedSize = depot.InstalledSizes.TryGetValue(options.Branch, out var installed) ? installed : null;
                if (sameManifest)
                {
                    // A repeated depot is downloaded once. Its size can be present in only
                    // one app's metadata; never carry sizes over to a different manifest.
                    downloadSize = MergeSize(downloadSize, existing.DownloadSize);
                    installedSize = MergeSize(installedSize, existing.InstalledSize);
                }
                selected[depot.DepotId] = (request, isDlc || existing.Dlc, downloadSize, installedSize);
            }
        }
        if (selected.Count == 0) throw new InvalidDataException("Steam selection contains no downloadable depots.");
        var game = new GameInfo(new(Source, app.AppId.ToString()), app.Title, ArchivePaths.Slug(app.Title))
            { Kind = app.Type, OwnedDlcs = dlcs.Select(d => d.ToString()).ToList() };
        var version = new GameVersion(buildId)
        {
            Name = buildId, Branch = options.Branch, Architecture = options.Architecture, Language = options.Language.ToLowerInvariant(),
            Dlcs = game.OwnedDlcs, Manifests = selected.Values.Select(d => d.Request).OrderBy(d => d.DepotId)
                .ToDictionary(d => d.DepotId.ToString(), d => d.ManifestId.ToString()),
            DownloadSize = TotalSize(selected.Values.Select(d => d.DownloadSize)),
            InstalledSize = TotalSize(selected.Values.Select(d => d.InstalledSize)),
        };
        return new(game, version, selected.Values.OrderBy(d => d.Dlc).ThenBy(d => d.Request.DepotId).Select(d => d.Request).ToList(), app);
    }

    private static long? MergeSize(long? current, long? previous) => current is not null && previous is not null
        ? Math.Max(current.Value, previous.Value) : current ?? previous;
    private static long TotalSize(IEnumerable<long?> sizes)
    {
        var values = sizes.ToArray();
        return values.All(s => s is >= 0) ? values.Sum(s => s!.Value) : 0;
    }

    public async Task<GameVersion> GetVersionAsync(string productId, GameSelection options, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return (await SelectAsync(productId, options, ct)).Version; }
        finally { _gate.Release(); }
    }

    private sealed record FileSource(SteamFile File, SteamDepotRequest Depot);
    private sealed record StageState(GameKey Key, GameVersion Version);
    public async Task<ArchiveResult> DownloadAsync(string productId, ArchiveOptions options,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await DownloadCoreAsync(productId, options, progress, ct); }
        finally { _gate.Release(); }
    }

    private async Task<ArchiveResult> DownloadCoreAsync(string productId, ArchiveOptions options, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var selection = await SelectAsync(productId, options, ct);
        options = await ProviderOutput.PrepareAsync(options, Source, ct);
        var reservation = await ArchiveCatalog.ReserveAsync(selection.Game, options, ct);
        using var lease = await ArchiveCatalog.AcquireOwnedAsync(reservation, selection.Game.Key, ct);
        var location = lease.Location;
        var previous = location.Metadata.Platforms.GetValueOrDefault(options.Platform);
        if (!options.Force && previous is not null && !previous.NeedsRefresh
            && previous.Version.Matches(selection.Version) && previous.HasArchive(location.Directory))
            return new(selection.Game.Key, ArchiveOutcome.AlreadyDone, Path.Combine(location.Directory, previous.ArchiveName));

        await using var content = await _session.OpenContentAsync(options.MaxBytesPerSecond, ct);
        var comparer = options.Platform == GameOs.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var files = new Dictionary<string, FileSource>(comparer);
        var directories = new HashSet<string>(comparer);
        var links = new Dictionary<string, ContentLink>(comparer);
        foreach (var depot in selection.Depots)
        {
            progress?.Report(new("manifest"));
            var manifest = await content.GetManifestAsync(depot, ct);
            if (manifest.Depot != depot) throw new InvalidDataException("Steam manifest identity mismatch.");
            foreach (var original in manifest.Files)
            {
                var rawPath = original.IsDirectory && original.LinkTarget is null ? original.Path.Replace('\\', '/').TrimEnd('/') : original.Path;
                var path = ArchivePaths.Relative(rawPath);
                var file = original with { Path = path };
                if (file.IsDirectory && file.LinkTarget is null) { directories.Add(path); continue; }
                files.Remove(path); links.Remove(path);
                if (file.LinkTarget is not null) { links[path] = new(path, LinkTarget(path, file.LinkTarget)); continue; }
                if (file.Size < 0 || file.Sha1.Length != 40 || !file.Sha1.All(Uri.IsHexDigit))
                    throw new InvalidDataException($"Invalid Steam file identity for {path}.");
                long position = 0;
                foreach (var chunk in file.Chunks.OrderBy(c => c.Offset))
                {
                    if (chunk.Offset != position || chunk.Size <= 0 || chunk.Size > 64 * 1024 * 1024 || chunk.CompressedSize <= 0 || chunk.CompressedSize > 64 * 1024 * 1024
                        || chunk.Id.Length != 40 || !chunk.Id.All(Uri.IsHexDigit))
                        throw new InvalidDataException($"Incomplete or invalid Steam chunk map for {path}.");
                    position = checked(position + chunk.Size);
                }
                if (position != file.Size) throw new InvalidDataException($"Steam chunks do not cover {path}.");
                files.Add(path, new(file, depot));
            }
        }
        if (files.Count == 0) throw new InvalidDataException("Steam selection has no game files; refusing an empty archive.");
        var layout = ContentLinks.Resolve(new(files.Values.Select(f => new ContentEntry(f.File.Path, f.File.Size)).ToList(),
            directories.ToList(), links.Values.ToList()), options.Platform == GameOs.Windows);
        var expectedFiles = files.Keys.Concat(layout.Links.Select(l => l.Path)).ToHashSet(comparer);
        foreach (var path in expectedFiles.Concat(layout.Directories))
            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                if (expectedFiles.Contains(parent)) throw new InvalidDataException($"Steam file/directory collision at {parent}.");
                directories.Add(parent.Replace('\\', '/'));
            }
        if (directories.Any(expectedFiles.Contains)) throw new InvalidDataException("Steam file/directory collision.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selection.Game.Key.ToString())))[..20];
        Directory.CreateDirectory(options.StagingDirectory);
        var staging = Path.Combine(options.StagingDirectory, $"steam-{hash}__{options.Platform.ToString().ToLowerInvariant()}");
        using var stageLease = new FileStream(staging + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var marker = staging + ".state.json";
        StageState? state = null;
        if (File.Exists(marker))
            try { state = JsonSerializer.Deserialize<StageState>(await File.ReadAllTextAsync(marker, ct), ArchiveJson.Options); }
            catch (JsonException) { }
        EnsureNoLinks(staging);
        var resume = !options.Clean && state?.Key == selection.Game.Key && state.Version?.Matches(selection.Version) == true;
        if (!resume && Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var totalSize = checked(files.Values.Sum(f => f.File.Size) + layout.LinkBytes);
        var estimate = checked(totalSize + totalSize / 100 + (long)files.Count * 1024 + (1 << 20));
        foreach (var dir in layout.Directories) directories.Add(dir);
        // Staging can be on a case-sensitive host even for a Windows game. A stale case
        // variant must not survive alongside the selected path and enter the same ZIP.
        var exactFiles = files.Values.Select(f => f.File.Path).Concat(layout.Links.Select(l => l.Path)).ToHashSet(StringComparer.Ordinal);
        var exactDirectories = layout.Directories.ToHashSet(StringComparer.Ordinal);
        foreach (var path in exactFiles.Concat(exactDirectories).ToArray())
            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                exactDirectories.Add(parent.Replace('\\', '/'));
        foreach (var path in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            if (!exactFiles.Contains(Path.GetRelativePath(staging, path).Replace('\\', '/'))) File.Delete(path);
        foreach (var path in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
            if (!exactDirectories.Contains(Path.GetRelativePath(staging, path).Replace('\\', '/'))) Directory.Delete(path);
        foreach (var dir in exactDirectories) Directory.CreateDirectory(ArchivePaths.Within(staging, dir));

        var resumes = new Dictionary<string, ResumeFile>(comparer);
        if (resume)
            foreach (var file in files.Values)
                resumes[file.File.Path] = await InspectResumeAsync(ArchivePaths.Within(staging, file.File.Path), file.File, ct);
        // SetLength creates sparse files: their logical length is not downloaded content.
        // Reuse this verified chunk inventory during transfer instead of hashing it twice.
        var verifiedBytes = resumes.Values.Sum(r => r.Bytes);
        if (ArchiveStorage.FreeSpaceShortfall(options.StagingDirectory, checked(totalSize + estimate - verifiedBytes)) is > 0
            || !ArchiveStorage.SameVolume(staging, location.Directory) && ArchiveStorage.FreeSpaceShortfall(location.Directory, estimate) is > 0)
            return new(selection.Game.Key, ArchiveOutcome.NotEnoughSpace, Detail: "Not enough space for Steam staging and archive.");
        await ArchiveJson.SaveAsync(marker, new StageState(selection.Game.Key, selection.Version), ct);

        var pending = new List<FileSource>();
        var cachedFiles = new Dictionary<(string Hash, long Size), string>();
        if (previous is not null)
            foreach (var entry in previous.Files.Where(f => f.Value.Algorithm == "sha1"))
                cachedFiles.TryAdd((entry.Value.Hash.ToLowerInvariant(), entry.Value.Size), entry.Key);
        using (var cache = options.Reuse && previous is not null ? OpenCache(ArchivePaths.Within(location.Directory, previous.ArchiveName)) : null)
        {
            foreach (var file in files.Values)
            {
                var path = ArchivePaths.Within(staging, file.File.Path);
                if (resumes.GetValueOrDefault(file.File.Path)?.Complete == true) continue;
                if (cache is not null)
                {
                    var restored = await RestoreAsync(cache, cachedFiles, file.File, path, ct);
                    if (restored == true) continue;
                    if (restored == false) resumes.Remove(file.File.Path); // A corrupt cache overwrote the staged bytes.
                }
                pending.Add(file);
            }
        }
        var totalBytes = pending.Sum(f => f.File.Chunks.Sum(c => (long)c.CompressedSize));
        long downloaded = 0; var done = 0;
        using var chunkSlots = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = options.MaxConnections, CancellationToken = ct }, async (item, token) =>
        {
            var path = ArchivePaths.Within(staging, item.File.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var output = new FileStream(path, resume ? FileMode.OpenOrCreate : FileMode.Create,
                FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                output.SetLength(item.File.Size);
                var needed = new List<SteamChunk>();
                foreach (var chunk in item.File.Chunks.OrderBy(c => c.Offset))
                {
                    if (resumes.GetValueOrDefault(item.File.Path)?.Offsets.Contains(chunk.Offset) == true)
                    { Interlocked.Add(ref totalBytes, -chunk.CompressedSize); continue; }
                    needed.Add(chunk);
                }
                var handle = output.SafeFileHandle;
                await Parallel.ForEachAsync(needed, new ParallelOptions { MaxDegreeOfParallelism = options.MaxConnections, CancellationToken = token }, async (chunk, partToken) =>
                {
                    await chunkSlots.WaitAsync(partToken);
                    try
                    {
                        var bytes = await content.GetChunkAsync(item.Depot, chunk, partToken);
                        if (bytes.Length != chunk.Size || !string.Equals(Convert.ToHexStringLower(SHA1.HashData(bytes)), chunk.Id, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"Steam chunk hash mismatch for {item.File.Path}.");
                        await RandomAccess.WriteAsync(handle, bytes, chunk.Offset, partToken);
                        progress?.Report(new("download", Interlocked.Add(ref downloaded, chunk.CompressedSize),
                            Volatile.Read(ref totalBytes), Volatile.Read(ref done), pending.Count));
                    }
                    finally { chunkSlots.Release(); }
                });
            }
            if (!await MatchesAsync(path, item.File, token)) throw new InvalidDataException($"Steam file hash mismatch: {item.File.Path}.");
            progress?.Report(new("download", Volatile.Read(ref downloaded), totalBytes, Interlocked.Increment(ref done), pending.Count));
        });
        foreach (var link in layout.Links)
        {
            var target = ArchivePaths.Within(staging, link.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = File.OpenRead(ArchivePaths.Within(staging, link.Target));
            await using var destination = File.Create(target);
            await source.CopyToAsync(destination, ct);
        }
        // Distinct content selections must never overwrite the previous ZIP before metadata
        // commits, even if Steam reused its build number or the user changed language/DLC.
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(selection.Version, ArchiveJson.Options)))[..12];
        var archiveName = ArchiveName.For(selection.Game, options.Platform, $"{selection.Version.Name}-{fingerprint}", portable: false);
        var archivePath = Path.Combine(location.Directory, archiveName);
        await ArchivePublisher.PublishAsync(staging, archivePath, options.CompressionThreads, options.Verify, progress, ct);
        var backedFiles = files.Values.ToDictionary(f => f.File.Path, f => new ArchivedFile(f.File.Sha1, f.File.Size));
        foreach (var link in layout.Links) backedFiles[link.Path] = backedFiles[link.Target];
        var osName = options.Platform switch { GameOs.Windows => "windows", GameOs.Mac => "macos", _ => "linux" };
        var launch = selection.App.Launches.FirstOrDefault(l =>
            (l.OsList.Length == 0 || l.OsList.Split(',').Contains(osName, StringComparer.OrdinalIgnoreCase))
            && expectedFiles.Contains(l.Executable.Replace('\\', '/')));
        location.Metadata.Platforms[options.Platform] = new()
        {
            Version = selection.Version, ArchiveName = archiveName, ArchiveSize = new FileInfo(archivePath).Length,
            InstalledSize = totalSize, Files = backedFiles, RuntimeRequirement = "steam-or-game-specific-drm; portability-not-verified",
            LaunchExecutable = launch?.Executable.Replace('\\', '/'), LaunchParameters = launch?.Arguments,
        };
        await location.Metadata.SaveAsync(location.Directory, ct);
        if (previous is not null && previous.ArchiveName != archiveName) File.Delete(ArchivePaths.Within(location.Directory, previous.ArchiveName));
        Directory.Delete(staging, true); File.Delete(marker);
        return new(selection.Game.Key, ArchiveOutcome.Completed, archivePath);
    }

    private static string LinkTarget(string path, string target)
    {
        if (string.IsNullOrEmpty(target) || target.StartsWith('/') || target.StartsWith('\\') || target.Contains(':'))
            throw new InvalidDataException($"Invalid Steam link target '{target}'.");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gamearchive-virtual-root"));
        var full = Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(path) ?? "", target.Replace('\\', '/')));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Steam link escapes game root.");
        return ArchivePaths.Relative(Path.GetRelativePath(root, full));
    }
    private static void EnsureNoLinks(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out var path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArchiveConflictException($"Staging contains a link: {path}");
            if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
        }
    }
    private static ZipArchive? OpenCache(string path)
    {
        try { return File.Exists(path) ? ZipFile.OpenRead(path) : null; }
        catch (Exception e) when (e is InvalidDataException or IOException) { return null; }
    }
    private static async Task<bool?> RestoreAsync(ZipArchive cache, Dictionary<(string Hash, long Size), string> previous,
        SteamFile file, string path, CancellationToken ct)
    {
        previous.TryGetValue((file.Sha1.ToLowerInvariant(), file.Size), out var name);
        if (name is null || cache.GetEntry(name) is not { } entry || entry.Length != file.Size) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using (var input = entry.Open()) await using (var output = File.Create(path)) await input.CopyToAsync(output, ct);
            return await MatchesAsync(path, file, ct);
        }
        catch (InvalidDataException) { return false; }
    }
    internal sealed record ResumeFile(bool Complete, HashSet<long> Offsets, long Bytes);
    internal static async Task<ResumeFile> InspectResumeAsync(string path, SteamFile file, CancellationToken ct)
    {
        if (!File.Exists(path)) return new(false, [], 0);
        if (await MatchesAsync(path, file, ct)) return new(true, [], file.Size);
        var offsets = new HashSet<long>();
        long bytes = 0;
        await using var stream = File.OpenRead(path);
        foreach (var chunk in file.Chunks)
            if (await ChunkMatchesAsync(stream, chunk, ct)) { offsets.Add(chunk.Offset); bytes += chunk.Size; }
        return new(false, offsets, bytes);
    }
    private static async Task<bool> ChunkMatchesAsync(Stream file, SteamChunk chunk, CancellationToken ct)
    {
        file.Position = chunk.Offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            var remaining = chunk.Size;
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), ct);
                if (read == 0) return false;
                hash.AppendData(buffer.AsSpan(0, read)); remaining -= read;
            }
            return string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), chunk.Id, StringComparison.OrdinalIgnoreCase);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
    private static async Task<bool> MatchesAsync(string path, SteamFile file, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
        await using var input = File.OpenRead(path);
        return string.Equals(Convert.ToHexStringLower(await SHA1.HashDataAsync(input, ct)), file.Sha1, StringComparison.OrdinalIgnoreCase);
    }
    public async ValueTask DisposeAsync() { await _session.DisposeAsync(); _gate.Dispose(); }
}
