using System.Diagnostics;
using GogDownloader.Core.Api;
using GogDownloader.Core.Content;

namespace GogDownloader.Core.Download;

public sealed class InnoExtractMissingException()
    : Exception($"innoextract is not on PATH. {InstallHint} Or skip these titles.")
{
    /// <summary>The hint has to match the machine that will read it, not the one that built it.</summary>
    private static string InstallHint =>
        OperatingSystem.IsWindows()
            ? "Install it with 'winget install --id dscharrer.innoextract' " +
              "(or 'scoop install innoextract', or 'choco install innoextract')."
            : OperatingSystem.IsMacOS()
                ? "Install it with 'brew install innoextract'."
                : "Install it with 'apt install innoextract' (or your distro's package manager).";
}

public sealed class InnoExtractFailedException(string message) : Exception(message);

/// <summary>
/// Path C: Windows titles with no Galaxy depot — old classics whose only download is an Inno
/// Setup installer. There is no way to read one over Range, so it is downloaded, extracted with
/// `innoextract`, and deleted. The installer never reaches the archive.
///
/// The installer lives *beside* the staging directory, because everything inside staging is
/// zipped, and it survives an interrupted run so the next one does not download it again.
/// </summary>
public sealed class InnoSetupExtractor(GogApiClient api, HttpClient cdn)
{
    /// <summary>Inno Setup puts the game under `app`; the rest is installer bookkeeping.</summary>
    private const string GameDirectory = "app";

    public static bool IsAvailable => Which("innoextract") is not null;

    public static Installer? Select(List<Installer> installers, GamePlatform platform, string language)
    {
        var candidates = installers
            .Where(i => GamePlatformExtensions.FromApiName(i.Os) == platform)
            .ToList();

        return InstallerSource.Select(candidates, language);
    }

    /// <summary>
    /// Downloads the installer's parts, skipping any already on disk at the right size, then
    /// extracts the game into staging.
    /// </summary>
    public async Task FillAsync(
        Installer installer,
        string staging,
        string installerDirectory,
        string scratch,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        if (!IsAvailable) throw new InnoExtractMissingException();

        Directory.CreateDirectory(installerDirectory);

        var total = installer.Files.Sum(f => f.Size);
        long downloaded = 0;
        var parts = new List<string>();

        foreach (var file in installer.Files)
        {
            var url = await api.ResolveDownlinkAsync(file.Downlink, ct);
            var name = PartNameFrom(url, file.Id);
            var path = Path.Combine(installerDirectory, name);
            parts.Add(path);

            // The installer is the expensive part of this path; never fetch one twice.
            if (File.Exists(path) && new FileInfo(path).Length == file.Size)
            {
                downloaded += file.Size;
                progress?.Report(new DownloadProgress("download", downloaded, total, parts.Count, installer.Files.Count));
                continue;
            }

            // A connection that drops mid-file must not abandon the game; restart the file.
            var before = downloaded;
            for (var attempt = 0; ; attempt++)
            {
                downloaded = before;
                try
                {
                    using var response = await cdn.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    await using var destination = File.Create(path);
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = await source.WithStallTimeoutAsync(buffer, ct)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                        downloaded += read;
                        progress?.Report(new DownloadProgress("download", downloaded, total, parts.Count, installer.Files.Count));
                    }
                    break;
                }
                catch (Exception e) when (attempt < 5 && !ct.IsCancellationRequested
                                          && e is IOException or HttpRequestException or TaskCanceledException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 20)), ct);
                }
            }
        }

        var setup = parts.FirstOrDefault(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InnoExtractFailedException("The installer has no .exe to extract.");

        progress?.Report(new DownloadProgress("extract", 0, 0, 0, 1));
        await ExtractAsync(setup, staging, scratch, ct);

        // The zip is made of whatever is in staging, so the installer must not be in it.
        Directory.Delete(installerDirectory, recursive: true);
    }

    /// <summary>
    /// `-g` pulls in the GOG `.bin` slices, `-m` leaves out Inno's temporary files, `-s` keeps it
    /// quiet. Everything lands under `app`, which is then lifted to the root of the archive.
    ///
    /// The scratch directory is the caller's to name, and it must be *short*: innoextract is not
    /// long-path aware, so every path it creates under scratch counts against Windows MAX_PATH,
    /// and old bundles nest directories deep enough to blow through it.
    /// </summary>
    internal static async Task ExtractAsync(string setup, string staging, string scratch, CancellationToken ct)
    {
        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        Directory.CreateDirectory(scratch);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo("innoextract")
            {
                ArgumentList = { "-e", "-m", "-g", "-s", "-d", scratch, setup },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            Directory.Delete(scratch, recursive: true);
            throw new InnoExtractFailedException($"innoextract failed ({process.ExitCode}): {stderr.Trim()}");
        }

        var app = Path.Combine(scratch, GameDirectory);
        var root = Directory.Exists(app) ? app : scratch;

        foreach (var source in Directory.EnumerateFileSystemEntries(root))
        {
            var destination = Path.Combine(staging, Path.GetFileName(source));
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination, overwrite: true);
        }

        Directory.Delete(scratch, recursive: true);
    }

    /// <summary>
    /// innoextract finds a multi-part installer's slices by the setup's base name — `name-N.bin`
    /// beside `name.exe` — so the parts may be renamed as long as they are renamed together. And
    /// they must be: GOG's real filenames (130+ characters, URL-escapes included) push the full
    /// path past Windows MAX_PATH, which innoextract cannot open. The parts therefore get the
    /// shortest names that keep the slice convention.
    /// </summary>
    internal static string PartNameFrom(string url, string fallback)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var name = Uri.UnescapeDataString(Path.GetFileName(path));

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "s.exe";

        var slice = System.Text.RegularExpressions.Regex.Match(name, @"-(\d+)\.bin$");
        if (slice.Success) return $"s-{slice.Groups[1].Value}.bin";

        // Unknown pattern: keep the real name rather than guess at what innoextract expects.
        return name.Length == 0 || name.Contains('/') || name.Contains('\\')
            ? $"setup_{fallback}.exe"
            : name;
    }

    private static string? Which(string executable)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var names = OperatingSystem.IsWindows() ? new[] { executable + ".exe", executable } : [executable];

        return (from directory in paths
                from name in names
                let candidate = Path.Combine(directory, name)
                where File.Exists(candidate)
                select candidate).FirstOrDefault();
    }
}
