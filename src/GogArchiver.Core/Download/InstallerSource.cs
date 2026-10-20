using System.Text;
using GogArchiver.Core.Api;
using GogArchiver.Core.Content;

namespace GogArchiver.Core.Download;

public sealed class NoLinuxInstallerException(string language)
    : Exception($"No Linux installer for language '{language}'.");

/// <summary>
/// Path B: Linux games. Galaxy has no Linux depots at all — GOG ships MojoSetup installers, a
/// shell script with a ZIP payload. The ZIP's central directory lives at the end, so HTTP Range
/// requests reach the game files directly and the installer itself is never downloaded.
/// </summary>
public sealed class InstallerSource(GogApiClient api, HttpClient cdn, long gameId, string language, RateLimiter? limiter = null)
    : ContentSource
{
    private MojoSetupReader? _reader;
    private readonly Dictionary<string, InstallerEntry> _entries = new(StringComparer.Ordinal);
    public Installer? SelectedInstaller { get; private set; }

    public override async Task<DepotPlan> PlanAsync(CancellationToken ct)
    {
        var product = await api.GetProductAsync(gameId, ct)
            ?? throw new InvalidOperationException($"Product {gameId} not found.");

        var installers = (product.Downloads?.Installers ?? [])
            .Where(i => GamePlatformExtensions.FromApiName(i.Os) == GamePlatform.Linux)
            .ToList();

        if (Select(installers, language) is not { } installer || installer.Files.Count == 0)
            throw new NoLinuxInstallerException(language);

        SelectedInstaller = installer;

        var url = await api.ResolveDownlinkAsync(installer.Files[0].Downlink, ct);

        _reader = new MojoSetupReader(cdn, url, limiter);
        await _reader.OpenAsync(ct);

        var files = new List<PlannedFile>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<PlannedLink>();

        foreach (var entry in _reader.Entries)
        {
            // Everything outside data/noarch is installer scaffolding: MojoSetup itself, its
            // Lua scripts, the licence screens.
            if (!entry.Name.StartsWith(MojoSetupReader.GamePrefix, StringComparison.Ordinal)) continue;

            var relative = SafeRelative(entry.Name[MojoSetupReader.GamePrefix.Length..]);
            if (relative.Length == 0) continue;

            if (entry.IsDirectory)
            {
                directories.Add(relative);
                continue;
            }

            if (entry.IsSymlink)
            {
                // A symlink's target is its content, so it has to be read before planning ends.
                var target = Encoding.UTF8.GetString(await _reader.ReadEntryAsync(entry, null, ct));
                links.Add(new PlannedLink(relative, ResolveLink(relative, target)));
                continue;
            }

            _entries[relative] = entry;

            // A ZIP entry is one range, and a CRC is not a hash we can compare a file against,
            // so a resumed run re-fetches an entry it cannot otherwise prove.
            var part = new PlannedPart(0, entry.DataOffset, entry.Size, null, entry.CompressedSize);
            files.Add(new PlannedFile(relative, gameId.ToString(), entry.Size, null, [part]));
        }

        if (files.Count == 0)
            throw new InstallerFormatException($"The installer holds nothing under {MojoSetupReader.GamePrefix}.");

        return new DepotPlan(files, directories.ToList(), links, [], []);
    }

    /// <summary>
    /// A symlink target is relative to the link's own directory, but a plan resolves links from
    /// the install root.
    /// </summary>
    internal static string ResolveLink(string linkPath, string target)
    {
        if (target.StartsWith('/')) return SafeRelative(target);

        var directory = Path.GetDirectoryName(linkPath)?.Replace('\\', '/') ?? "";
        var combined = directory.Length == 0 ? target : $"{directory}/{target}";

        var segments = new List<string>();
        foreach (var segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) throw new InstallerFormatException($"Symlink escapes the game: {combined}");
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    /// <summary>English installers are multilanguage, which makes them the fallback gogdl uses.</summary>
    internal static Installer? Select(List<Installer> installers, string language) =>
        InstallerSelection.Select(installers, language, i => i.Language);

    protected override Task<byte[]> FetchOnceAsync(
        PlannedFile file, PlannedPart part, IProgress<long>? bytes, int attempt, CancellationToken ct) =>
        _reader is null || !_entries.TryGetValue(file.RelativePath, out var entry)
            ? throw new InvalidOperationException("The installer was not opened; call PlanAsync first.")
            : _reader.ReadEntryAsync(entry, bytes, ct);

    protected override Task CopyOnceAsync(
        PlannedFile file, PlannedPart part, Stream destination, IProgress<long>? bytes, int attempt, CancellationToken ct) =>
        _reader is null || !_entries.TryGetValue(file.RelativePath, out var entry)
            ? throw new InvalidOperationException("The installer was not opened; call PlanAsync first.")
            : _reader.CopyEntryAsync(entry, destination, bytes, ct);
}
