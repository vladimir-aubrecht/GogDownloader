using System.Text.Json;

namespace GogDownloader.Core.Download;

/// <summary>
/// Marks what a staging directory holds. A directory left behind by an interrupted run is
/// only worth resuming when it was produced by the same build, language and platform;
/// otherwise its files are stale and reusing them would silently ship the wrong game.
/// </summary>
public sealed record StagingState(string BuildId, string Language, string Platform)
{
    /// <summary>Kept beside the staging directory, never inside it: everything inside is zipped.</summary>
    private static string PathFor(string staging) => staging + ".state.json";

    public static StagingState? Load(string staging)
    {
        var path = PathFor(staging);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<StagingState>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            // Truncated by a crash mid-write. Treat it as unusable rather than guessing.
            return null;
        }
    }

    public void Save(string staging) => File.WriteAllText(PathFor(staging), JsonSerializer.Serialize(this));

    public static void Remove(string staging)
    {
        var path = PathFor(staging);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Throw away a staging directory and its marker together; one without the other
    /// would either strand gigabytes or offer a resume with nothing to resume from.</summary>
    public static void Discard(string staging)
    {
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Remove(staging);

        var zip = staging + ".zip";
        if (File.Exists(zip)) File.Delete(zip);
    }
}

/// <summary>Carries the staging path so the CLI can tell the user how to resume.</summary>
public sealed class DownloadInterruptedException(string stagingDirectory)
    : OperationCanceledException("Download interrupted.")
{
    public string StagingDirectory { get; } = stagingDirectory;
}

/// <summary>
/// Some files could not be downloaded from GOG even after retries — typically a chunk GOG serves
/// truncated. The rest downloaded; the game is left unfinished rather than zipped incomplete.
/// </summary>
public sealed class DownloadIncompleteException(IReadOnlyDictionary<string, string> files)
    : Exception($"{files.Count} file(s) could not be downloaded from GOG.")
{
    public IReadOnlyDictionary<string, string> Files { get; } = files;
}
