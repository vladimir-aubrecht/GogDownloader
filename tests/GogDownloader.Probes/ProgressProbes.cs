using GogDownloader.Cli;
using GogDownloader.Core.Download;

namespace GogDownloader.Probes;

/// <summary>
/// The progress display. Each phase restarts at 0%, so unless a finished phase keeps its line a
/// completed download vanishing as zipping begins reads as a restart — which is exactly what a
/// user reported.
/// </summary>
public static class ProgressProbes
{
    public static void Run()
    {
        Probe.Group("Progress phases each get their own line");

        var original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var progress = new ConsoleProgress();
            // The manifest phase has nothing to measure; it must not linger as a false "0.0 %".
            progress.Report(new DownloadProgress("manifest", 0, 0, 0, 0));
            // Each reported at 100% so the throttle always renders it.
            progress.Report(new DownloadProgress("download", 100, 100, 5, 5));
            progress.Report(new DownloadProgress("zip", 100, 100, 9, 9));
            progress.Report(new DownloadProgress("check", 100, 100, 9, 9));
        }
        finally
        {
            Console.SetError(original);
        }

        // Terminal overwrite: keep only what follows the last carriage return on each line.
        var lines = captured.ToString()
            .Split('\n')
            .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
            .Where(l => l.Trim().Length > 0)
            .ToList();

        Probe.Check("a finished phase stays on its own line", lines.Count >= 2, lines.Count.ToString());
        Probe.Check("the completed download line survives", lines.Any(l => l.Contains("download") && l.Contains("100")));
        Probe.Check("zipping appears on a separate line", lines.Any(l => l.Contains("zip")));
        Probe.Check("archive verification is labelled plainly", lines.Any(l => l.Contains("verifying")));

        // A MiB/s under "zip" reads as a download; only real network phases show a transfer rate.
        Probe.Check("the download line shows a transfer rate", lines.Any(l => l.Contains("download") && l.Contains("MiB/s")));
        Probe.Check("the zip line shows no transfer rate", lines.Any(l => l.Contains("zip")) && !lines.Any(l => l.Contains("zip") && l.Contains("MiB/s")));
        Probe.Check("the zip line counts in files", lines.Any(l => l.Contains("zip") && l.Contains("files")));

        // The manifest blip is overwritten by download, so it never survives as its own line.
        Probe.Check("an unmeasurable phase does not linger with a 0.0 %",
            !lines.Any(l => l.Contains("manifest") && l.Contains("0.0")));
    }
}
