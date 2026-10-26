using GameArchiver.Core;

namespace GameArchiver.Cli;

public static partial class ArchiveCommandLine
{
    private static async Task<int> RunDrmAsync(ArchiveLibrary library, string snapshot, List<string> args,
        Dictionary<string, string> flags, string source, Func<IDrmSource>? factory, CancellationToken ct)
    {
        var operation = args.ElementAtOrDefault(2);
        var allowed = new HashSet<string> { "source", "snapshot" };
        allowed.UnionWith(operation switch
        {
            "sync" => new[] { "refresh" }, "set" => ["platform", "branch", "drm", "note"], "clear" => ["platform", "branch"], _ => [],
        });
        if (flags.Keys.FirstOrDefault(k => !allowed.Contains(k)) is { } unsupported)
            throw new ArgumentException($"--{unsupported} is not supported by library drm {operation}.");
        if (operation == "sync")
        {
            if (args.Count != 3 || flags.ContainsKey("drm")) throw new ArgumentException("Usage: library drm sync [--source steam|gog|all] [--refresh]");
            var provider = factory?.Invoke() ?? new PcGamingWikiDrmSource();
            try
            {
                var result = await LibraryDrm.RefreshAsync(library, provider, source, flags.ContainsKey("refresh"),
                    new InlineProgress<string>(Console.Error.WriteLine), ct);
                await result.Library.SaveAsync(snapshot, ct);
                Console.WriteLine($"DRM metadata: {result.Updated} updated, {result.Cached} cached. Manual overrides preserved.");
                if (result.Error is not null) Console.Error.WriteLine($"DRM metadata unavailable: {result.Error}. Unrefreshed entries are unknown; normal library sync/download remains available.");
                return result.Error is null ? 0 : 1;
            }
            finally { if (provider is IDisposable disposable) disposable.Dispose(); }
        }
        if (operation is not "set" and not "clear" || args.Count != 4 || !flags.ContainsKey("platform"))
            throw new ArgumentException("Usage: library drm set|clear SOURCE:ID --platform windows|linux|mac [--drm free|protected|unknown --note TEXT]");
        var parts = args[3].Split(':', 2);
        if (parts.Length != 2) throw new ArgumentException("Use a source:ID product key.");
        var key = new GameKey(parts[0], parts[1]);
        if (source != "all" && source != key.Source) throw new ArgumentException("Selected product is outside --source.");
        var game = library.Games.SingleOrDefault(g => g.Key == key) ?? throw new ArgumentException("Product is not in this library.");
        var options = Options(flags, []);
        if (!game.Versions.ContainsKey(options.Platform)) throw new ArgumentException("Product has no saved version for this platform.");
        if (operation == "set")
        {
            var status = DrmCatalog.Parse(flags.GetValueOrDefault("drm", ""));
            var note = flags.GetValueOrDefault("note");
            if (string.IsNullOrWhiteSpace(note)) throw new ArgumentException("A manual DRM classification requires --note explaining the evidence.");
            if (!library.DrmOverrides.TryGetValue(key.ToString(), out var entries)) library.DrmOverrides[key.ToString()] = entries = [];
            entries[options.Platform] = new() { Status = status, Source = "manual", Branch = options.Branch, Notes = note, CheckedAt = DateTimeOffset.UtcNow };
        }
        else
        {
            if (flags.ContainsKey("drm")) throw new ArgumentException("library drm clear does not accept --drm.");
            if (library.DrmOverrides.TryGetValue(key.ToString(), out var entries))
            {
                if (entries.GetValueOrDefault(options.Platform)?.Branch == options.Branch) entries.Remove(options.Platform);
                if (entries.Count == 0) library.DrmOverrides.Remove(key.ToString());
            }
        }
        await library.SaveAsync(snapshot, ct);
        var effective = DrmCatalog.Get(library, key, options.Platform, options.Branch);
        Console.WriteLine($"{key} {options.Platform}/{options.Branch}: {effective.Status.ToString().ToLowerInvariant()} ({effective.Source}).");
        return 0;
    }
}
