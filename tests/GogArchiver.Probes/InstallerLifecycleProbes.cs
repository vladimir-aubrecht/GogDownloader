using System.Diagnostics;
using GogArchiver.Core.Download;

namespace GogArchiver.Probes;

public static class InstallerLifecycleProbes
{
    public static async Task RunAsync()
    {
        // These controlled shell extractors exercise process ownership without shipping Inno binaries.
        if (OperatingSystem.IsWindows()) return;
        Probe.Group("Inno extraction can resume and owns its child processes");
        var root = Probe.ScratchDirectory();
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + oldPath);
            var executable = Path.Combine(bin, "innoextract");
            var stage = Path.Combine(root, "stage");
            var scratch = Path.Combine(root, "scratch");
            Directory.CreateDirectory(stage);

            foreach (var app in new[] { "app/", "" })
            {
                await ScriptAsync(executable,
                    $"mkdir -p \"$6/{app}bin/empty\"\nprintf NEW > \"$6/{app}bin/data\"\nprintf ROOT > \"$6/{app}root.txt\"\n");
                await InnoSetupExtractor.ExtractAsync("setup.exe", stage, scratch, default);
                File.WriteAllText(Path.Combine(stage, "bin/data"), "OLD");
                File.WriteAllText(Path.Combine(stage, "obsolete.txt"), "old launcher");
                File.WriteAllText(Path.Combine(stage, "bin/obsolete.txt"), "old data");
                var resumed = false;
                try { await InnoSetupExtractor.ExtractAsync("setup.exe", stage, scratch, default); resumed = true; }
                catch (IOException) { }
                Probe.Check($"{app}root: an interrupted backup can extract again over existing nested directories",
                    resumed && File.ReadAllText(Path.Combine(stage, "bin/data")) == "NEW"
                    && Directory.Exists(Path.Combine(stage, "bin/empty")));
                Probe.Check($"{app}root: replacement staging contains only the successful extraction",
                    !File.Exists(Path.Combine(stage, "obsolete.txt")) && !File.Exists(Path.Combine(stage, "bin/obsolete.txt"))
                    && File.ReadAllText(Path.Combine(stage, "root.txt")) == "ROOT" && !Directory.Exists(scratch));
            }

            await ScriptAsync(executable, "mkdir -p \"$6/app\"\nprintf BAD > \"$6/app/root.txt\"\nprintf failed >&2\nexit 7\n");
            var rejected = false;
            try { await InnoSetupExtractor.ExtractAsync("setup.exe", stage, scratch, default); }
            catch (InnoExtractFailedException e) { rejected = e.Message.Contains("7") && e.Message.Contains("failed"); }
            Probe.Check("a failed extractor preserves the previous staging tree",
                rejected && File.ReadAllText(Path.Combine(stage, "root.txt")) == "ROOT");

            await CancelAsync(executable, stage, scratch);
            await CancelAfterExitAsync(executable, stage, scratch);

            // Filling stdout used to block the extractor while the parent only drained stderr.
            await ScriptAsync(executable,
                "dd if=/dev/zero bs=65536 count=2 2>/dev/null\n" +
                "(dd if=/dev/zero bs=65536 count=2 2>/dev/null) >&2\n" +
                "mkdir -p \"$6/app\"\nprintf QUIET > \"$6/app/root.txt\"\n");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var drained = false;
            try { await InnoSetupExtractor.ExtractAsync("setup.exe", stage, scratch, timeout.Token); drained = true; }
            catch (OperationCanceledException) { }
            Probe.Check("stdout and stderr are drained concurrently without blocking extraction",
                drained && File.ReadAllText(Path.Combine(stage, "root.txt")) == "QUIET");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CancelAsync(string executable, string stage, string scratch)
    {
        await ScriptAsync(executable,
            "echo $$ > \"$6/parent.pid\"\nsleep 60 &\necho $! > \"$6/child.pid\"\nwait\n");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var task = InnoSetupExtractor.ExtractAsync("setup.exe", stage, scratch, cancel.Token);
        Process? parent = null;
        int childPid = 0;
        try
        {
            int parentPid = 0;
            while (!ReadPid(Path.Combine(scratch, "parent.pid"), out parentPid)
                   || !ReadPid(Path.Combine(scratch, "child.pid"), out childPid))
                await Task.Delay(10, cancel.Token);
            parent = Process.GetProcessById(parentPid);
            cancel.Cancel();
            var cancelled = false;
            try { await task; }
            catch (OperationCanceledException) { cancelled = true; }
            Probe.Check("cancellation returns only after innoextract has exited", cancelled && parent.HasExited);
            // Kill(entireProcessTree) signals descendants asynchronously; waiting for the
            // parent alone does not guarantee that every child has finished exiting yet.
            using var childExit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                while (Running(childPid)) await Task.Delay(10, childExit.Token);
            }
            catch (OperationCanceledException) when (childExit.IsCancellationRequested) { }
            // A killed orphan can briefly remain as a zombie until init reaps it on Linux.
            Probe.Check("cancellation also terminates the extractor's child process", !Running(childPid));
            Probe.Check("a cancelled extraction preserves completed staging content",
                File.ReadAllText(Path.Combine(stage, "root.txt")) == "ROOT");
        }
        finally
        {
            cancel.Cancel();
            if (parent is not null)
            {
                if (!parent.HasExited) { parent.Kill(entireProcessTree: true); await parent.WaitForExitAsync(); }
                parent.Dispose();
            }
            if (childPid != 0 && Running(childPid))
            {
                using var child = Process.GetProcessById(childPid);
                child.Kill();
            }
            try { await task; } catch (OperationCanceledException) { }
        }
    }

    private static async Task CancelAfterExitAsync(string executable, string stage, string scratch)
    {
        foreach (var pipe in new[] { "stdout", "stderr" })
        {
            // The parent has exited, so Kill(entireProcessTree) can no longer reach the child.
            // Keeping either inherited pipe open must not prevent cancellation from returning.
            var redirect = pipe == "stdout" ? "2>/dev/null" : ">/dev/null";
            await ScriptAsync(executable,
                $"echo $$ > \"$6/parent.pid\"\nsleep 60 {redirect} &\necho $! > \"$6/child.pid\"\nexit 0\n");
            using var cancel = new CancellationTokenSource();
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var task = InnoSetupExtractor.ExtractAsync("setup.exe", stage, scratch, cancel.Token);
            int parentPid = 0, childPid = 0;
            try
            {
                while (!ReadPid(Path.Combine(scratch, "parent.pid"), out parentPid)
                       || !ReadPid(Path.Combine(scratch, "child.pid"), out childPid))
                    await Task.Delay(10, startup.Token);
                while (Running(parentPid)) await Task.Delay(10, startup.Token);
                Probe.Check($"{pipe}: the descendant holds the pipe after the extractor has exited",
                    Running(childPid) && !task.IsCompleted);

                cancel.Cancel();
                var cancelled = false;
                try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (OperationCanceledException) { cancelled = true; }
                catch (TimeoutException) { }
                Probe.Check($"{pipe}: cancellation returns without waiting for the orphan's EOF", cancelled);
                Probe.Check($"{pipe}: cancellation after parent exit preserves staging",
                    File.ReadAllText(Path.Combine(stage, "root.txt")) == "ROOT");
            }
            finally
            {
                cancel.Cancel();
                // Always clean up the controlled orphan, including when the regression fails.
                if (parentPid != 0 && Running(parentPid))
                {
                    using var parent = Process.GetProcessById(parentPid);
                    parent.Kill(entireProcessTree: true);
                    await parent.WaitForExitAsync();
                }
                if (childPid != 0 && Running(childPid))
                {
                    using var child = Process.GetProcessById(childPid);
                    child.Kill();
                }
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static bool ReadPid(string path, out int pid)
    {
        pid = 0;
        return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out pid);
    }

    private static bool Running(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && (!OperatingSystem.IsLinux()
                || !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z "));
        }
        catch (ArgumentException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static async Task ScriptAsync(string path, string body)
    {
        await File.WriteAllTextAsync(path, "#!/bin/sh\nset -e\n" + body);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
