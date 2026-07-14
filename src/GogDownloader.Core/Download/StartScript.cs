using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GogDownloader.Core;

namespace GogDownloader.Core.Download;

public sealed record PlayTask
{
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("arguments")] public string? Arguments { get; init; }
    [JsonPropertyName("workingDir")] public string? WorkingDir { get; init; }
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("isPrimary")] public bool IsPrimary { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
}

public sealed record GogGameInfo
{
    [JsonPropertyName("playTasks")] public List<PlayTask> PlayTasks { get; init; } = [];

    /// <summary>The task to launch by default: the primary one, or the first that runs a file.</summary>
    public PlayTask? PrimaryTask =>
        PlayTasks.FirstOrDefault(t => t.IsPrimary && IsFile(t))
        ?? PlayTasks.FirstOrDefault(IsFile);

    private static bool IsFile(PlayTask task) =>
        task.Path.Length > 0
        && (task.Type is null || task.Type.Equals("FileTask", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Writes one entry point into the archive root so every game starts the same way, regardless of
/// where GOG buried its executable. The launch target comes from the depot's own
/// `goggame-{id}.info`; nothing is guessed.
///
/// Linux is left alone: its installer already ships a working `start.sh` at the root.
/// </summary>
public static class StartScript
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string WindowsName = "start.bat";

    /// <summary>Returns the launcher filename written, or null when there was no metadata to use.</summary>
    public static async Task<string?> WriteAsync(string staging, GamePlatform platform, CancellationToken ct)
    {
        // Linux already has one, and macOS games are .app bundles a flat script cannot launch.
        if (platform != GamePlatform.Windows) return null;

        var task = await FindPrimaryTaskAsync(staging, ct);
        if (task is null) return null;

        var script = System.IO.Path.Combine(staging, WindowsName);
        await File.WriteAllTextAsync(script, BuildWindowsScript(task), new UTF8Encoding(false), ct);
        return WindowsName;
    }

    /// <summary>
    /// What GameVault should launch: an executable path inside the archive, and its arguments.
    /// Windows and macOS read it from goggame metadata; Linux ships a start.sh at the root.
    /// </summary>
    public static async Task<(string? Executable, string? Parameters)> ResolveLaunchAsync(
        string staging, GamePlatform platform, CancellationToken ct)
    {
        if (platform == GamePlatform.Linux)
            return (File.Exists(System.IO.Path.Combine(staging, "start.sh")) ? "start.sh" : null, null);

        var task = await FindPrimaryTaskAsync(staging, ct);
        if (task is null) return (null, null);

        // Forward slashes: the same path works whether GameVault runs the client on Windows or
        // launches it through Proton on Linux.
        var executable = task.Path.Replace('\\', '/').TrimStart('/');
        return (executable, string.IsNullOrWhiteSpace(task.Arguments) ? null : task.Arguments.Trim());
    }

    private static async Task<PlayTask?> FindPrimaryTaskAsync(string staging, CancellationToken ct)
    {
        // The base game's info holds the play tasks; a DLC's usually has none.
        foreach (var file in Directory.EnumerateFiles(staging, "goggame-*.info", SearchOption.AllDirectories))
        {
            try
            {
                await using var stream = File.OpenRead(file);
                var info = await JsonSerializer.DeserializeAsync<GogGameInfo>(stream, Json, ct);
                if (info?.PrimaryTask is { } task) return task;
            }
            catch (JsonException)
            {
                // A malformed info file is not worth failing a finished download over.
            }
        }

        return null;
    }

    /// <summary>
    /// A batch file that runs the game from its own directory, so relative paths inside the game
    /// resolve whatever the working directory happened to be when the user double-clicked it.
    /// </summary>
    internal static string BuildWindowsScript(PlayTask task)
    {
        var exe = ToWindowsPath(task.Path);
        var workingDir = string.IsNullOrEmpty(task.WorkingDir) ? null : ToWindowsPath(task.WorkingDir);
        var arguments = string.IsNullOrWhiteSpace(task.Arguments) ? "" : " " + task.Arguments;

        var builder = new StringBuilder();
        builder.Append("@echo off\r\n");
        builder.Append("cd /d \"%~dp0\"\r\n");
        if (workingDir is not null)
            builder.Append($"cd /d \"%~dp0{workingDir}\"\r\n");

        // %~dp0 already ends with a backslash, so the exe path must not start with one.
        var fromRoot = workingDir is null ? exe : RelativeToWorkingDir(exe, workingDir);
        builder.Append($"start \"\" \"{fromRoot}\"{arguments}\r\n");
        return builder.ToString();
    }

    /// <summary>The exe is stored relative to the root, but we cd into the working dir first.</summary>
    private static string RelativeToWorkingDir(string exe, string workingDir)
    {
        var prefix = workingDir.EndsWith('\\') ? workingDir : workingDir + '\\';
        return exe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? exe[prefix.Length..] : exe;
    }

    private static string ToWindowsPath(string path) => path.Replace('/', '\\').TrimStart('\\');
}
