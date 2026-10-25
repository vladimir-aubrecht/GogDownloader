using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GameArchiver;
using GameArchiver.Cli;
using GameArchiver.Core;
using GogArchiver.Core;
using GogArchiver.Core.Download;
using GogArchiver.Core.Download.Zip;

namespace GogArchiver.Probes;

public static class ArchiveFileProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Bulk ZIP file operations");
        var root = Probe.ScratchDirectory();
        try
        {
            await OperationsAsync(root);
            await RawZipAsync(root);
            await FailureAsync(root);
            await CliAsync(root);
            await ReviewRegressionsAsync(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static ArchiveOptions Options(string root) => new()
    { OutputRoot = Path.Combine(root, "library"), StagingRoot = Path.Combine(root, "staging"), Layout = ArchiveLayout.Provider };
    private static ArchiveFileRequest Add(string file, string entry = "readme.txt") => new()
    { Operation = ArchiveFileOperation.Add, FilePath = file, EntryPath = entry };
    private static ArchiveFileRequest Find(string match, ArchiveFileMatch by = ArchiveFileMatch.Name) => new()
    { Operation = ArchiveFileOperation.Find, Match = match, MatchBy = by };
    private static string Hash(string value, bool md5 = false) => Convert.ToHexString(md5
        ? MD5.HashData(Encoding.UTF8.GetBytes(value)) : SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static async Task<string> FixtureAsync(ArchiveOptions options, string source, string slug = "game", bool linux = false)
    {
        var dir = Path.Combine(options.OutputRoot, source, slug);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Game (v1) (W_P).zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, value) in new Dictionary<string, string>
            { ["readme.txt"] = "root original", ["docs/readme.txt"] = "nested original", ["readme.txt.backup"] = "earlier backup", ["game.bin"] = "game content" })
            { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(value); }
        }
        var version = new GameVersion("1") { Name = "1", Manifests = new() { ["10"] = "100" } };
        if (source == "gog")
        {
            var backup = new PlatformBackup
            {
                BuildId = "1", ContentRevision = 1, VersionName = "1", Language = "en", ArchiveName = Path.GetFileName(path),
                ArchiveSize = new FileInfo(path).Length, InstalledSize = 999, LaunchExecutable = "game.bin", SkippedDependencies = ["directx"],
                Files = new() { ["readme.txt"] = new(Hash("root original", true), 13), ["game.bin"] = new(Hash("game content", true), 12) },
            };
            var manifest = new BackupManifest { GameId = 1, Slug = slug, Title = "Game", Platforms = new() { [GamePlatform.Windows] = backup } };
            if (linux)
            {
                File.Copy(path, Path.Combine(dir, "Linux.zip"));
                manifest.Platforms[GamePlatform.Linux] = backup with { ArchiveName = "Linux.zip" };
            }
            await manifest.SaveAsync(dir);
        }
        else
        {
            await new ArchiveMetadata { Source = source, ProductId = "1", Slug = slug, Title = "Game", Platforms = new()
            {
                [GameOs.Windows] = new() { Version = version, ArchiveName = Path.GetFileName(path), ArchiveSize = new FileInfo(path).Length,
                    InstalledSize = 999, RuntimeRequirement = "steam", Files = new() { ["readme.txt"] = new(Hash("root original"), 13) } },
            } }.SaveAsync(dir);
        }
        return dir;
    }
    private static async Task<string> ZipPathAsync(string dir, GameOs os = GameOs.Windows) =>
        Path.Combine(dir, (await ArchiveMetadata.LoadAsync(dir))!.Platforms[os].ArchiveName);
    private static Dictionary<string, string> Contents(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToDictionary(e => e.FullName, e =>
        { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); });
    }

    private static async Task OperationsAsync(string root)
    {
        var options = Options(Path.Combine(root, "operations"));
        var gog = await FixtureAsync(options, "gog", linux: true);
        var steam = await FixtureAsync(options, "steam");
        var input = Path.Combine(root, "new.txt"); await File.WriteAllTextAsync(input, "new content");
        await using var client = new GameArchiverClient(new OfflineStore("gog"), new OfflineStore("steam"));
        var results = await client.ProcessArchiveFilesAsync(options, Find("readme.txt"));
        Probe.Check("name search finds root and nested files on all sources and platforms", results.Count == 3 && results.All(r => r.Changes.Count == 2));
        results = await client.ProcessArchiveFilesAsync(options, Find("docs\\readme.txt", ArchiveFileMatch.Path));
        Probe.Check("path search normalizes separators and matches only the exact relative path", results.Count == 3 && results.All(r => r.Changes.Single().EntryPath == "docs/readme.txt"));
        results = await client.ProcessArchiveFilesAsync(options, Find("README.TXT"));
        Probe.Check("search is case-sensitive by default", results.All(r => r.Status == ArchiveFileStatus.NoMatch));
        results = await client.ProcessArchiveFilesAsync(options, Find("README.TXT") with { IgnoreCase = true, Platform = GameOs.Windows });
        Probe.Check("ignore-case and platform filtering apply to search", results.Count == 2 && results.All(r => r.Changes.Count == 2));
        var original = await ZipPathAsync(gog); var bytes = await File.ReadAllBytesAsync(original);
        var metadataPath = Path.Combine(gog, BackupManifest.FileName); var metadataBytes = await File.ReadAllBytesAsync(metadataPath);
        results = await client.ProcessArchiveFilesAsync(options, Add(input) with { DryRun = true });
        Probe.Check("dry-run plans every archive and chooses an unoccupied backup name", results.Count == 3 && results.All(r => r.Status == ArchiveFileStatus.Preview
            && r.Changes.Single().BackupPath == "readme.txt.backup.backup"));
        Probe.Check("dry-run does not modify ZIPs, metadata or create staging/locks", bytes.SequenceEqual(await File.ReadAllBytesAsync(original))
            && metadataBytes.SequenceEqual(await File.ReadAllBytesAsync(metadataPath)) && !Directory.Exists(options.StagingDirectory)
            && !File.Exists(Path.Combine(gog, ".archive.lock")));
        results = await client.ProcessArchiveFilesAsync(options, Add(input));
        Probe.Check("add replaces root entries across all selected archives", results.Count == 3 && results.All(r => r.Status == ArchiveFileStatus.Updated), string.Join(";", results.Select(r => r.Detail)));
        var edited = await ZipPathAsync(gog); var content = Contents(edited);
        Probe.Check("replacement preserves both original and older backup contents", content["readme.txt"] == "new content"
            && content["readme.txt.backup"] == "earlier backup" && content["readme.txt.backup.backup"] == "root original"
            && content["docs/readme.txt"] == "nested original" && content["game.bin"] == "game content");
        Probe.Check("metadata selects a complete new ZIP before the old ZIP is removed", !File.Exists(original) && File.Exists(edited)
            && Directory.GetFiles(gog, "*.zip").Length == 2 && !Directory.GetFiles(gog, "*.tmp").Any());
        var legacy = (await BackupManifest.LoadAsync(gog))!.Platforms[GamePlatform.Windows];
        Probe.Check("GOG metadata updates size and MD5 inventory without losing provider fields", legacy.ArchiveSize == new FileInfo(edited).Length
            && legacy.InstalledSize == content.Sum(p => Encoding.UTF8.GetByteCount(p.Value))
            && legacy.Files["readme.txt"].Md5 == Hash("new content", true) && legacy.Files["readme.txt.backup.backup"].Md5 == Hash("root original", true)
            && legacy.Files["game.bin"].Md5 == Hash("game content", true) && legacy.LaunchExecutable == "game.bin"
            && legacy.SkippedDependencies.SequenceEqual(["directx"]) && legacy.ModifiedAt is not null && legacy.BuildId == "1");
        var modern = (await ArchiveMetadata.LoadAsync(steam))!.Platforms[GameOs.Windows];
        Probe.Check("Steam metadata updates SHA1 inventory and retains version selection", modern.Files["readme.txt"].Hash == Hash("new content")
            && modern.Files["readme.txt.backup.backup"].Hash == Hash("root original") && modern.Version.Manifests["10"] == "100"
            && modern.RuntimeRequirement == "steam" && modern.ModifiedAt is not null);
        var library = new ArchiveLibrary { Games = (await ArchiveCatalog.ScanAsync(Path.Combine(options.OutputRoot, "gog")))
            .Concat(await ArchiveCatalog.ScanAsync(Path.Combine(options.OutputRoot, "steam")))
            .Select(l => new GameInfo(l.Metadata.Key, l.Metadata.Title, l.Metadata.Slug)
            { Versions = new() { [GameOs.Windows] = l.Metadata.Platforms[GameOs.Windows].Version } }).ToList() };
        Probe.Check("local edits remain Current in diff and pass deep verification", (await client.DiffAsync(library, options)).All(r => r.Status == ArchiveStatus.Current)
            && (await client.VerifyAsync(options, deep: true)).All(r => r.Problems.Count == 0));
        await File.WriteAllTextAsync(input, "second version");
        results = await client.ProcessArchiveFilesAsync(options, Find("readme.txt") with
        { Operation = ArchiveFileOperation.Replace, FilePath = input, Platform = GameOs.Windows });
        content = Contents(await ZipPathAsync(gog));
        Probe.Check("replace by name changes each matched path and extends backup chains", results.Count == 2 && results.All(r => r.Status == ArchiveFileStatus.Updated)
            && content["readme.txt"] == "second version" && content["docs/readme.txt"] == "second version"
            && content["docs/readme.txt.backup"] == "nested original" && content["readme.txt.backup.backup.backup"] == "new content");
        Probe.Check("successive edits do not accumulate archive filename markers", Path.GetFileName(await ZipPathAsync(gog)).Split("(local-").Length == 2);
        var second = await ZipPathAsync(gog);
        results = await client.ProcessArchiveFilesAsync(options, Find("missing.txt") with { Operation = ArchiveFileOperation.Replace, FilePath = input });
        Probe.Check("replace never adds missing files", results.All(r => r.Status == ArchiveFileStatus.NoMatch) && await ZipPathAsync(gog) == second);
        await using var onlyGog = new GameArchiverClient(new OfflineStore("gog"));
        results = await onlyGog.ProcessArchiveFilesAsync(options, Add(input, "extras/readme.txt") with { Platform = GameOs.Windows });
        Probe.Check("add accepts a nested destination and honors source/platform selection", results.Count == 1 && results[0].Status == ArchiveFileStatus.Updated
            && Contents(await ZipPathAsync(gog))["extras/readme.txt"] == "second version"
            && !Contents(await ZipPathAsync(gog, GameOs.Linux)).ContainsKey("extras/readme.txt")
            && !Contents(await ZipPathAsync(steam)).ContainsKey("extras/readme.txt"));
        Probe.Check("completed edits clean their source compression staging", !Directory.EnumerateFileSystemEntries(options.StagingDirectory).Any());
    }

    private static async Task RawZipAsync(string root)
    {
        var dir = Path.Combine(root, "raw"); Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "žluťoučký.txt"), new string('x', 3000));
        await File.WriteAllTextAsync(Path.Combine(source, "empty.txt"), "");
        Directory.CreateDirectory(Path.Combine(source, "empty-dir"));
        var zip = Path.Combine(dir, "original.zip");
        await ParallelZipWriter.CreateAsync(source, zip, 2, zip64Threshold: 16);
        var file = Path.Combine(dir, "new.txt"); await File.WriteAllTextAsync(file, "");
        var prepared = await ZipFileEditor.PrepareAsync(file, Path.Combine(dir, "prepared"));
        var changes = ZipFileEditor.Plan(zip, Add(file, "žluťoučký.txt"));
        var output = Path.Combine(dir, "edited.zip");
        await ZipFileEditor.RewriteAsync(zip, output, prepared, changes, zip64Threshold: 16);
        Probe.Check("ZIP64 rewrite preserves Unicode, empty directories and zero-length stored files", (await ArchiveCheck.DeepAsync(output)).Count == 0
            && ArchiveCheck.Headers(output).Count == 0 && Contents(output)["žluťoučký.txt.backup"] == new string('x', 3000)
            && ZipInspector.ReadCentralDirectory(output).Any(e => e.Name == "empty-dir/")
            && ZipInspector.ReadCentralDirectory(output).Single(e => e.Name == "žluťoučký.txt").Method == 0);
        Probe.Check("renamed backup retains byte-identical compressed payload", Payload(zip, "žluťoučký.txt").SequenceEqual(Payload(output, "žluťoučký.txt.backup")));
        var stored = Path.Combine(dir, "stored.zip");
        using (var z = ZipFile.Open(stored, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(z.CreateEntry("stored.txt", CompressionLevel.NoCompression).Open()); writer.Write("stored content"); }
        await ZipFileEditor.RewriteAsync(stored, Path.Combine(dir, "stored-edited.zip"), prepared, ZipFileEditor.Plan(stored, Add(file)));
        Probe.Check("nonempty Stored entries remain readable after raw-copy edits", Contents(Path.Combine(dir, "stored-edited.zip"))["stored.txt"] == "stored content"
            && (await ArchiveCheck.DeepAsync(Path.Combine(dir, "stored-edited.zip"))).Count == 0);
        Probe.Check("add avoids a case-only duplicate by retaining the existing spelling",
            ZipFileEditor.Plan(stored, Add(file, "STORED.TXT")).Single() == new ArchiveFileChange("stored.txt", "stored.txt.backup"));
        var streamed = Path.Combine(dir, "descriptor.zip");
        using (var stream = new NonSeekingFile(streamed))
        using (var z = new ZipArchive(stream, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(z.CreateEntry("streamed.txt").Open()); writer.Write("streamed content"); }
        var streamedOutput = Path.Combine(dir, "descriptor-edited.zip");
        await ZipFileEditor.RewriteAsync(streamed, streamedOutput, prepared, ZipFileEditor.Plan(streamed, Add(file, "streamed.txt")));
        Probe.Check("data-descriptor entries can be renamed and copied without recompression", Contents(streamedOutput)["streamed.txt.backup"] == "streamed content"
            && Payload(streamed, "streamed.txt").SequenceEqual(Payload(streamedOutput, "streamed.txt.backup"))
            && (await ArchiveCheck.DeepAsync(streamedOutput)).Count == 0);
        var bad = Path.Combine(dir, "duplicate.zip");
        using (var z = ZipFile.Open(bad, ZipArchiveMode.Create)) { z.CreateEntry("Readme.txt"); z.CreateEntry("readme.txt"); }
        Probe.Check("ambiguous case-colliding ZIP entries are refused", Throws(() => ZipFileEditor.Plan(bad, Add(file))));
        foreach (var entry in new[] { "../outside.txt", "/absolute.txt", "C:\\bad.txt", "game.bin/child.txt" })
        {
            var fixture = entry.Contains("child") ? stored : zip;
            var target = entry.Contains("child") ? "stored.txt/child.txt" : entry;
            Probe.Check($"unsafe/conflicting entry path rejected: {target}", Throws(() => ZipFileEditor.Plan(fixture, Add(file, target))));
        }
        Probe.Check("directory cannot be replaced by a file", Throws(() => ZipFileEditor.Plan(zip, Add(file, "empty-dir"))));
        var before = await File.ReadAllBytesAsync(zip);
        try { await ZipFileEditor.PrepareAsync(file, zip); } catch (IOException) { }
        Probe.Check("source preparation refuses an existing destination without deleting it", before.SequenceEqual(await File.ReadAllBytesAsync(zip)));
        try { await ZipFileEditor.RewriteAsync(zip, zip, prepared, changes); } catch (IOException) { }
        Probe.Check("rewriter cannot overwrite its input", before.SequenceEqual(await File.ReadAllBytesAsync(zip)));
        var inconsistent = Path.Combine(dir, "inconsistent.zip"); File.Copy(zip, inconsistent);
        using (var stream = File.Open(inconsistent, FileMode.Open, FileAccess.ReadWrite))
        { stream.Position = ZipInspector.ReadCentralDirectory(zip)[0].LocalHeaderOffset + 14; stream.WriteByte(123); }
        Probe.Check("local CRC disagreement is rejected instead of silently rewritten", Throws(() => ZipFileEditor.Plan(inconsistent, Add(file))));
        var encrypted = Path.Combine(dir, "encrypted.zip"); File.Copy(zip, encrypted);
        var first = ZipInspector.ReadCentralDirectory(zip)[0];
        using (var stream = File.Open(encrypted, FileMode.Open, FileAccess.ReadWrite))
        {
            foreach (var offset in new[] { first.LocalHeaderOffset + 6, first.CentralHeaderOffset + 8 })
            { stream.Position = offset; var flag = stream.ReadByte(); stream.Position = offset; stream.WriteByte((byte)(flag | 1)); }
        }
        Probe.Check("encrypted entries are refused", Throws(() => ZipFileEditor.Plan(encrypted, Add(file))));
    }
    private static byte[] Payload(string zip, string name)
    {
        var h = ZipInspector.ReadCentralDirectory(zip).Single(e => e.Name == name);
        using var input = File.OpenRead(zip); input.Position = h.LocalHeaderOffset;
        var header = new byte[30]; input.ReadExactly(header);
        input.Position += BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        var bytes = new byte[(int)h.CompressedSize]; input.ReadExactly(bytes); return bytes;
    }
    private static bool Throws(Action action)
    { try { action(); return false; } catch (Exception e) when (e is InvalidDataException or ArgumentException) { return true; } }

    private static async Task FailureAsync(string root)
    {
        var options = Options(Path.Combine(root, "failures"));
        var dir = await FixtureAsync(options, "gog");
        var zip = await ZipPathAsync(dir); var old = await File.ReadAllBytesAsync(zip);
        var metadataPath = Path.Combine(dir, BackupManifest.FileName); var metadata = await File.ReadAllBytesAsync(metadataPath);
        var file = Path.Combine(root, "failure-source.txt"); await File.WriteAllTextAsync(file, "new data");
        await using var client = new GameArchiverClient(new OfflineStore("gog"));
        using var cts = new CancellationTokenSource();
        var cancelled = false;
        try { await client.ProcessArchiveFilesAsync(options, Add(file), new Immediate(p => { if (p.Phase == "Editing archive") cts.Cancel(); }), cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Probe.Check("cancellation during ZIP rewrite preserves original archive and metadata", cancelled
            && old.SequenceEqual(await File.ReadAllBytesAsync(zip)) && metadata.SequenceEqual(await File.ReadAllBytesAsync(metadataPath)));
        Probe.Check("cancellation removes partial output and source staging", Directory.GetFiles(dir, "*.zip").Length == 1
            && !Directory.GetFiles(dir, "*.tmp").Any() && !Directory.EnumerateFileSystemEntries(options.StagingDirectory).Any());
        using (ArchiveCatalog.Acquire(dir))
        {
            var locked = await client.ProcessArchiveFilesAsync(options, Add(file));
            Probe.Check("edits respect the same exclusive game lease as downloads", locked.Single().Status == ArchiveFileStatus.Failed
                && old.SequenceEqual(await File.ReadAllBytesAsync(zip)) && metadata.SequenceEqual(await File.ReadAllBytesAsync(metadataPath)));
        }
        var damaged = false;
        var results = await client.ProcessArchiveFilesAsync(options, Add(file), new Immediate(p =>
        { if (p.Phase == "Editing archive" && !damaged) { File.WriteAllText(metadataPath, "invalid json"); damaged = true; } }));
        Probe.Check("metadata preparation failure cannot destroy the old ZIP", results.Single().Status == ArchiveFileStatus.Failed
            && old.SequenceEqual(await File.ReadAllBytesAsync(zip)) && Directory.GetFiles(dir, "*.zip").Length == 1
            && !Directory.GetFiles(dir, "*.tmp").Any());
        await File.WriteAllBytesAsync(metadataPath, metadata);
        await File.AppendAllTextAsync(zip, "damage");
        var healthy = await FixtureAsync(options, "gog", "healthy");
        results = await client.ProcessArchiveFilesAsync(options, Add(file));
        Probe.Check("batch reports a bad archive and continues with healthy archives", results.Count == 2
            && results.Count(r => r.Status == ArchiveFileStatus.Failed) == 1 && results.Count(r => r.Status == ArchiveFileStatus.Updated) == 1
            && Contents(await ZipPathAsync(healthy))["readme.txt"] == "new data");
        // Flat libraries and matching shadow metadata are how older GOG backups often appear.
        var flat = options with { OutputRoot = Path.Combine(options.OutputRoot, "gog"), Layout = ArchiveLayout.Flat };
        var owner = (await ArchiveMetadata.LoadAsync(healthy))!;
        await owner.SaveAsync(healthy);
        var legacyPath = Path.Combine(healthy, BackupManifest.FileName);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(legacyPath))!.AsObject();
        json["CustomField"] = "retained";
        await File.WriteAllTextAsync(legacyPath, json.ToJsonString());
        var extraZip = Path.Combine(healthy, "unreferenced.zip"); await File.WriteAllTextAsync(extraZip, "not actually a ZIP");
        results = await client.ProcessArchiveFilesAsync(flat, Add(file, "flat.txt") with { Search = "healthy" });
        Probe.Check("flat layout edits authoritative GOG metadata and preserves unknown fields", results.Single().Status == ArchiveFileStatus.Updated
            && Contents(await ZipPathAsync(healthy))["flat.txt"] == "new data"
            && JsonNode.Parse(await File.ReadAllTextAsync(legacyPath))!["CustomField"]!.GetValue<string>() == "retained");
        Probe.Check("unreferenced ZIPs are not edited", await File.ReadAllTextAsync(extraZip) == "not actually a ZIP");
    }

    private static async Task CliAsync(string root)
    {
        var options = Options(Path.Combine(root, "cli"));
        var gog = await FixtureAsync(options, "gog"); var steam = await FixtureAsync(options, "steam");
        Directory.Move(Path.Combine(options.OutputRoot, "gog"), Path.Combine(options.OutputRoot, "drm_free"));
        gog = Path.Combine(options.OutputRoot, "drm_free", "game");
        var input = Path.Combine(root, "cli.txt"); await File.WriteAllTextAsync(input, "CLI data");
        var snapshot = Path.Combine(root, "invalid-snapshot.json"); await File.WriteAllTextAsync(snapshot, "not a snapshot");
        IEnumerable<IGameStore> Factory(string source) => new[] { "gog", "steam" }.Where(s => source == "all" || source == s).Select(s => new OfflineStore(s));
        var common = new[] { "--out", options.OutputRoot, "--provider-dir", "gog=drm_free", "--source", "gog", "--snapshot", snapshot, "--staging", options.StagingDirectory };
        var result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["archive", "find", "--name", "readme.txt", ..common], storeFactory: Factory));
        Probe.Check("CLI find is offline, uses provider folders and ignores library snapshots", result.Exit == 0 && result.Text.Contains("docs/readme.txt") && !result.Text.Contains("steam:1"));
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["archive", "add", "--file", input, "--entry", "extras/info.txt", "--dry-run", ..common], storeFactory: Factory));
        Probe.Check("CLI add dry-run previews without adding files", result.Exit == 0 && result.Text.Contains("Preview")
            && !Contents(await ZipPathAsync(gog)).ContainsKey("extras/info.txt"));
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["archive", "replace", "--file", input, "--path", "docs/readme.txt", ..common], storeFactory: Factory));
        Probe.Check("CLI replace by exact path changes only that file in the selected provider", result.Exit == 0
            && Contents(await ZipPathAsync(gog))["docs/readme.txt"] == "CLI data" && Contents(await ZipPathAsync(gog))["readme.txt"] == "root original"
            && Contents(await ZipPathAsync(steam))["docs/readme.txt"] == "nested original");
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["archive", "replace", "--file", input, "--name", "readme.txt", "--path", "docs/readme.txt", ..common], storeFactory: Factory));
        Probe.Check("CLI rejects ambiguous matcher options", result.Exit == 1 && result.Text.Contains("either --name or --path"));
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["archive", "find", "--path", "missing.txt", ..common], storeFactory: Factory));
        Probe.Check("CLI reports no match with nonzero exit status", result.Exit == 1 && result.Text.Contains("NoMatch"));
        result = await CaptureAsync(() => ArchiveCommandLine.RunAsync(["archive", "find", "no such game", "--name", "readme.txt", ..common], storeFactory: Factory));
        Probe.Check("CLI game search limits archive selection", result.Exit == 1 && result.Text.Contains("0 matched"));
    }
    private static async Task ReviewRegressionsAsync(string root)
    {
        var input = Path.Combine(root, "review-readme.txt");
        await File.WriteAllTextAsync(input, "review replacement");
        await MetadataSourceAsync(root, input);
        await SharedArchiveAsync(root, input);
        await LongNamesAsync(root, input);
    }

    private static async Task MetadataSourceAsync(string root, string input)
    {
        var options = Options(Path.Combine(root, "metadata-source"));
        var dir = await FixtureAsync(options, "gog");
        var legacyPath = Path.Combine(dir, ArchiveMetadata.LegacyFileName);
        var nativePath = Path.Combine(dir, ArchiveMetadata.FileName);
        var owner = (await ArchiveMetadata.LoadAsync(dir))!;
        await owner.SaveAsync(dir);
        var foreign = JsonNode.Parse(await File.ReadAllTextAsync(legacyPath))!;
        foreign["Source"] = "manual";
        await File.WriteAllTextAsync(legacyPath, foreign.ToJsonString());
        var foreignBytes = await File.ReadAllBytesAsync(legacyPath);
        // A persisted field must never influence which metadata document receives the edit.
        var native = JsonNode.Parse(await File.ReadAllTextAsync(nativePath))!;
        native["LoadedFileName"] = ArchiveMetadata.LegacyFileName;
        await File.WriteAllTextAsync(nativePath, native.ToJsonString());
        Probe.Check("metadata provenance follows ownership, ignoring a foreign legacy document and persisted hints",
            (await ArchiveMetadata.LoadAsync(dir))!.LoadedFileName == ArchiveMetadata.FileName);
        await using var client = new GameArchiverClient(new OfflineStore("gog"));
        var oldZip = await ZipPathAsync(dir);
        var result = (await client.ProcessArchiveFilesAsync(options, Add(input))).Single();
        var updated = (await ArchiveMetadata.LoadAsync(dir))!;
        var backup = updated.Platforms[GameOs.Windows];
        Probe.Check("native GOG edits commit the metadata that actually owns the ZIP", result.Status == ArchiveFileStatus.Updated
            && backup.HasArchive(dir) && result.ArchivePath == Path.Combine(dir, backup.ArchiveName)
            && !File.Exists(oldZip) && Contents(result.ArchivePath)["readme.txt"] == "review replacement");
        Probe.Check("foreign legacy metadata stays byte-identical after an edit", foreignBytes.SequenceEqual(await File.ReadAllBytesAsync(legacyPath)));
        Probe.Check("native GOG edits keep the native hash schema", backup.Files["readme.txt"].Algorithm == "sha1"
            && backup.Files["readme.txt"].Hash == Hash("review replacement") && (await client.VerifyAsync(options, deep: true)).Single().Problems.Count == 0);
        await updated.SaveAsync(dir);
        Probe.Check("metadata provenance is not serialized", !JsonNode.Parse(await File.ReadAllTextAsync(nativePath))!.AsObject().ContainsKey("LoadedFileName"));

        // A genuine legacy GOG manifest still takes precedence, including mixed-case Source.
        var legacyDir = await FixtureAsync(options, "gog", "legacy");
        await (await ArchiveMetadata.LoadAsync(legacyDir))!.SaveAsync(legacyDir);
        var shadowPath = Path.Combine(legacyDir, ArchiveMetadata.FileName);
        var shadow = await File.ReadAllBytesAsync(shadowPath);
        var legacyFile = Path.Combine(legacyDir, ArchiveMetadata.LegacyFileName);
        var legacy = JsonNode.Parse(await File.ReadAllTextAsync(legacyFile))!;
        legacy["Source"] = "GoG";
        await File.WriteAllTextAsync(legacyFile, legacy.ToJsonString());
        result = (await client.ProcessArchiveFilesAsync(options, Add(input) with { Search = "legacy" })).Single();
        Probe.Check("recognized legacy GOG metadata remains authoritative over native metadata", result.Status == ArchiveFileStatus.Updated
            && (await ArchiveMetadata.LoadAsync(legacyDir))!.LoadedFileName == ArchiveMetadata.LegacyFileName
            && (await BackupManifest.LoadAsync(legacyDir))!.Platforms[GamePlatform.Windows].Files["readme.txt"].Md5 == Hash("review replacement", true)
            && shadow.SequenceEqual(await File.ReadAllBytesAsync(shadowPath)));
    }

    private static async Task SharedArchiveAsync(string root, string input)
    {
        var options = Options(Path.Combine(root, "shared-archive"));
        var dir = await FixtureAsync(options, "steam");
        var metadata = (await ArchiveMetadata.LoadAsync(dir))!;
        var original = metadata.Platforms[GameOs.Windows];
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        var zip = Path.Combine(dir, "sub", "Game.zip");
        File.Move(Path.Combine(dir, original.ArchiveName), zip);
        metadata.Platforms[GameOs.Windows] = original with { ArchiveName = "sub/Game.zip" };
        metadata.Platforms[GameOs.Linux] = original with { ArchiveName = "sub\\Game.zip" };
        await metadata.SaveAsync(dir);
        var bytes = await File.ReadAllBytesAsync(zip);
        var metadataPath = Path.Combine(dir, ArchiveMetadata.FileName);
        var saved = await File.ReadAllBytesAsync(metadataPath);
        await using var client = new GameArchiverClient(new OfflineStore("steam"));
        foreach (var dryRun in new[] { true, false })
        {
            var result = (await client.ProcessArchiveFilesAsync(options, Add(input) with { Platform = GameOs.Windows, DryRun = dryRun })).Single();
            Probe.Check($"shared ZIP aliases are rejected before {(dryRun ? "preview" : "execution")}", result.Status == ArchiveFileStatus.Failed
                && result.Detail!.Contains("Multiple platforms") && bytes.SequenceEqual(await File.ReadAllBytesAsync(zip))
                && saved.SequenceEqual(await File.ReadAllBytesAsync(metadataPath)));
        }
        Probe.Check("rejecting a shared archive preserves both platforms and creates no staging", (await ArchiveMetadata.LoadAsync(dir))!.Platforms.Values.All(p => p.HasArchive(dir))
            && !Directory.Exists(options.StagingDirectory) && Directory.GetFiles(dir, "*.zip", SearchOption.AllDirectories).Length == 1);
        // Different ZIPs with the same basename must remain independently editable.
        Directory.CreateDirectory(Path.Combine(dir, "other"));
        File.Copy(zip, Path.Combine(dir, "other", "Game.zip"));
        metadata.Platforms[GameOs.Linux] = original with { ArchiveName = "other/Game.zip" };
        await metadata.SaveAsync(dir);
        var edited = (await client.ProcessArchiveFilesAsync(options, Add(input) with { Platform = GameOs.Windows })).Single();
        Probe.Check("different subdirectory ZIPs with the same basename can be edited independently", edited.Status == ArchiveFileStatus.Updated
            && (await ArchiveMetadata.LoadAsync(dir))!.Platforms.Values.All(p => p.HasArchive(dir))
            && bytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(dir, "other", "Game.zip"))));
    }

    private static async Task LongNamesAsync(string root, string input)
    {
        var options = Options(Path.Combine(root, "long-names"));
        await using var client = new GameArchiverClient(new OfflineStore("steam"));
        var names = new[]
        {
            new string('a', 225) + ".zip",
            new string('a', 251) + ".zip",
            new string('a', 232) + " (v1) (EA) (W_P).zip",
            string.Concat(Enumerable.Repeat("😀", 58)) + " (v1) (EA) (W_P).zip",
            "Game (v" + new string('x', 228) + ") (EA) (W_P).zip",
        };
        for (var i = 0; i < names.Length; i++)
        {
            var slug = "long_" + i;
            var dir = await FixtureAsync(options, "steam", slug);
            var metadata = (await ArchiveMetadata.LoadAsync(dir))!;
            var backup = metadata.Platforms[GameOs.Windows];
            var oldZip = Path.Combine(dir, names[i]);
            File.Move(Path.Combine(dir, backup.ArchiveName), oldZip);
            metadata.Platforms[GameOs.Windows] = backup with { ArchiveName = names[i] };
            await metadata.SaveAsync(dir);
            var before = await File.ReadAllBytesAsync(oldZip);
            var request = Add(input) with { Search = slug };
            var preview = (await client.ProcessArchiveFilesAsync(options, request with { DryRun = true })).Single();
            Probe.Check($"long archive name {i}: preview succeeds without modifying the archive", preview.Status == ArchiveFileStatus.Preview
                && before.SequenceEqual(await File.ReadAllBytesAsync(oldZip)));
            var result = (await client.ProcessArchiveFilesAsync(options, request)).Single();
            var name = Path.GetFileName(result.ArchivePath);
            Probe.Check($"long archive name {i}: edit publishes within filesystem limits", result.Status == ArchiveFileStatus.Updated
                && Encoding.UTF8.GetByteCount(name) <= 255 && name.Length <= 255 && !name.Contains('\ufffd')
                && (await ArchiveMetadata.LoadAsync(dir))!.Platforms[GameOs.Windows].HasArchive(dir)
                && Contents(result.ArchivePath)["readme.txt"] == "review replacement"
                && !File.Exists(oldZip) && !Directory.GetFiles(dir, "*.tmp").Any(), result.Detail);
            if (i >= 2)
                Probe.Check($"long archive name {i}: GameVault type/version tags survive shortening", name.Contains("(EA) (W_P)")
                    && System.Text.RegularExpressions.Regex.IsMatch(name, @"\(v[^)]+\)") && name.Count(c => c == '(') == name.Count(c => c == ')')
                    && (i != 4 || name.StartsWith("Game (v", StringComparison.Ordinal)));
            if (i == 1)
            {
                var repeated = (await client.ProcessArchiveFilesAsync(options, request)).Single();
                Probe.Check("repeated long-name edits retain one marker and valid metadata", repeated.Status == ArchiveFileStatus.Updated
                    && Path.GetFileName(repeated.ArchivePath).Split("(local-").Length == 2
                    && Encoding.UTF8.GetByteCount(Path.GetFileName(repeated.ArchivePath)) <= 255
                    && (await client.VerifyAsync(options, deep: true, search: slug)).Single().Problems.Count == 0);
            }
        }
    }

    private static async Task<(int Exit, string Text)> CaptureAsync(Func<Task<int>> action)
    {
        var stdout = Console.Out; var stderr = Console.Error; using var text = new StringWriter();
        try { Console.SetOut(text); Console.SetError(text); return (await action(), text.ToString()); }
        finally { Console.SetOut(stdout); Console.SetError(stderr); }
    }
    private sealed class Immediate(Action<ArchiveProgress> callback) : IProgress<ArchiveProgress>
    { public void Report(ArchiveProgress value) => callback(value); }
    private sealed class NonSeekingFile(string path) : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }
    private sealed class OfflineStore(string source) : IGameStore
    {
        public string Source => source;
        public Task<StoreLibraryResult> SyncLibraryAsync(CancellationToken ct = default) => throw new Exception("Must stay offline");
        public Task<GameVersion> GetVersionAsync(string productId, GameSelection options, CancellationToken ct = default) => throw new Exception("Must stay offline");
        public Task<ArchiveResult> DownloadAsync(string productId, ArchiveOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) => throw new Exception("Must stay offline");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
