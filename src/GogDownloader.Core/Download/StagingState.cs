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
}

/// <summary>Carries the staging path so the CLI can tell the user how to resume.</summary>
public sealed class DownloadInterruptedException(string stagingDirectory)
    : OperationCanceledException("Download interrupted.")
{
    public string StagingDirectory { get; } = stagingDirectory;
}
