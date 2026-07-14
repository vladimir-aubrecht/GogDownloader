using System.Text.Json;
using GogDownloader.Core;
using GogDownloader.Core.Download;

namespace GogDownloader.Probes;

/// <summary>
/// The launcher written into each archive so every game starts the same way. The target is read
/// from the depot's own goggame-*.info, never guessed, so the risk worth testing is picking the
/// wrong task or writing a path Windows will not run.
/// </summary>
public static class StartScriptProbes
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync()
    {
        Probe.Group("Choosing the play task");

        var info = JsonSerializer.Deserialize<GogGameInfo>("""
            {"playTasks":[
              {"category":"launcher","path":"config.exe","isPrimary":false,"type":"FileTask"},
              {"category":"game","path":"bin/game.exe","isPrimary":true,"type":"FileTask"},
              {"category":"document","link":"https://gog.com","isPrimary":false,"type":"URLTask"}]}
            """, Json)!;
        Probe.Check("the primary file task is chosen", info.PrimaryTask?.Path == "bin/game.exe", info.PrimaryTask?.Path);

        var noPrimary = JsonSerializer.Deserialize<GogGameInfo>("""
            {"playTasks":[{"path":"only.exe","type":"FileTask"}]}
            """, Json)!;
        Probe.Check("with no primary, the first file task is used", noPrimary.PrimaryTask?.Path == "only.exe");

        var urlOnly = JsonSerializer.Deserialize<GogGameInfo>("""
            {"playTasks":[{"link":"https://gog.com","isPrimary":true,"type":"URLTask"}]}
            """, Json)!;
        Probe.Check("a URL task is never launched as a file", urlOnly.PrimaryTask is null);

        Probe.Check("no tasks means no launcher",
            JsonSerializer.Deserialize<GogGameInfo>("""{"playTasks":[]}""", Json)!.PrimaryTask is null);

        Probe.Group("The Windows launcher script");

        var script = StartScript.BuildWindowsScript(new PlayTask { Path = "bin/game.exe" });
        Probe.Check("the exe path uses backslashes", script.Contains(@"""bin\game.exe"""), script);
        Probe.Check("it runs from the archive root", script.Contains(@"cd /d ""%~dp0"""));
        Probe.Check("lines are CRLF for cmd.exe", script.Contains("\r\n"));

        var withArgs = StartScript.BuildWindowsScript(new PlayTask { Path = "game.exe", Arguments = "-windowed" });
        Probe.Check("arguments are passed through", withArgs.Contains(@"""game.exe"" -windowed"), withArgs);

        var withDir = StartScript.BuildWindowsScript(
            new PlayTask { Path = "bin/game.exe", WorkingDir = "bin" });
        Probe.Check("it cd's into the working dir", withDir.Contains(@"cd /d ""%~dp0bin"""), withDir);
        Probe.Check("and runs the exe relative to it", withDir.Contains(@"""game.exe"""), withDir);

        Probe.Group("Writing the launcher into a game");

        var staging = Probe.ScratchDirectory();
        Directory.CreateDirectory(Path.Combine(staging, "bin"));
        await File.WriteAllTextAsync(Path.Combine(staging, "goggame-1207658930.info"), """
            {"playTasks":[{"category":"game","path":"bin/witcher.exe","isPrimary":true,"type":"FileTask"}]}
            """);

        var written = await StartScript.WriteAsync(staging, GamePlatform.Windows, default);
        Probe.Check("a Windows game gets start.bat", written == "start.bat");
        Probe.Check("and it lands in the archive root", File.Exists(Path.Combine(staging, "start.bat")));
        Probe.Check("pointing at the real exe",
            (await File.ReadAllTextAsync(Path.Combine(staging, "start.bat"))).Contains(@"bin\witcher.exe"));

        // Linux installers already ship start.sh, and macOS games are .app bundles.
        Probe.Check("Linux is left to its own start.sh",
            await StartScript.WriteAsync(staging, GamePlatform.Linux, default) is null);
        Probe.Check("macOS gets no flat launcher",
            await StartScript.WriteAsync(staging, GamePlatform.Mac, default) is null);

        var bare = Probe.ScratchDirectory();
        Probe.Check("a game with no metadata gets no launcher",
            await StartScript.WriteAsync(bare, GamePlatform.Windows, default) is null);

        Probe.Group("Launch info recorded for GameVault");
        var (exe, parms) = await StartScript.ResolveLaunchAsync(staging, GamePlatform.Windows, default);
        Probe.Check("the executable is captured with forward slashes", exe == "bin/witcher.exe", exe);
        Probe.Check("no arguments means null, not empty", parms is null);

        var argStaging = Probe.ScratchDirectory();
        await File.WriteAllTextAsync(Path.Combine(argStaging, "goggame-1.info"), """
            {"playTasks":[{"path":"g.exe","arguments":"-dx12","isPrimary":true,"type":"FileTask"}]}
            """);
        var (_, capturedArgs) = await StartScript.ResolveLaunchAsync(argStaging, GamePlatform.Windows, default);
        Probe.Check("arguments are captured", capturedArgs == "-dx12", capturedArgs);

        var linux = Probe.ScratchDirectory();
        await File.WriteAllTextAsync(Path.Combine(linux, "start.sh"), "#!/bin/sh");
        var (linuxExe, _) = await StartScript.ResolveLaunchAsync(linux, GamePlatform.Linux, default);
        Probe.Check("Linux launch executable is its start.sh", linuxExe == "start.sh", linuxExe);
        Probe.Check("Linux without start.sh has none",
            (await StartScript.ResolveLaunchAsync(bare, GamePlatform.Linux, default)).Executable is null);

        Directory.Delete(staging, recursive: true);
        Directory.Delete(bare, recursive: true);
        Directory.Delete(argStaging, recursive: true);
        Directory.Delete(linux, recursive: true);
    }
}
