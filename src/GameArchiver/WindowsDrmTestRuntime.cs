using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameArchiver;

/// <summary>Native Windows launches in a kill-on-close job, assigned before the first instruction.
/// This contains the process tree for cleanup; it is not a security sandbox.</summary>
public sealed class WindowsDrmTestRuntime : IDrmTestRuntime
{
    public bool IsSupported => OperatingSystem.IsWindows();
    private DateTime? _launchStartedAtUtc;

    private static bool IsSteam(string name) => name.Equals("steam", StringComparison.OrdinalIgnoreCase)
        || name.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase)
        || name.Equals("steamerrorreporter", StringComparison.OrdinalIgnoreCase)
        || name.Equals("steamerrorreporter64", StringComparison.OrdinalIgnoreCase);

    internal static IDisposable AcquireLease()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gamearchive");
        Directory.CreateDirectory(root);
        try { return new FileStream(Path.Combine(root, "drm-test.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("Another local DRM test may be running. Run one test batch at a time.", e); }
    }

    public IReadOnlyList<string> GetSteamProcesses()
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Windows process observation requires Windows.");
        var found = new List<string>();
        Process[] processes = [];
        try
        {
            processes = Process.GetProcesses();
            foreach (var process in processes)
                try
                {
                    var name = process.ProcessName;
                    if (IsSteam(name)) found.Add(name);
                }
                catch (InvalidOperationException) { } // Process exited during enumeration.
        }
        catch (Exception e) when (e is Win32Exception or UnauthorizedAccessException)
        { throw new IOException("Cannot inspect running processes; Steam observation is unavailable.", e); }
        finally { foreach (var process in processes) process.Dispose(); }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IDrmTestProcess Start(DrmTestLaunch launch)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Native DRM tests require Windows.");
        _launchStartedAtUtc = null;
        if (GetSteamProcesses().Count > 0) throw new IOException("Steam started before the game launch. No test was started.");
        var app = launch.Executable;
        var command = $"\"{app}\" {launch.Arguments}";
        if (Path.GetExtension(app).Equals(".bat", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(app).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // Percent expansion happens even inside quotes in cmd.exe; do not reinterpret a filename.
            if (app.Contains('%') || app.Contains('"')) throw new NotSupportedException("Batch launcher paths cannot contain percent signs or quotes.");
            app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            command = $"\"{app}\" /d /s /c \"\"{launch.Executable}\" {launch.Arguments}\"";
        }
        if (command.Length >= 32767) throw new ArgumentException("Launcher command line is too long.");
        var job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var process = new Native.ProcessInformation();
        try
        {
            var limits = new Native.ExtendedLimits { Basic = new() { LimitFlags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new Native.StartupInfo { Size = (uint)Marshal.SizeOf<Native.StartupInfo>() };
            if (!Native.CreateProcessW(app, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false,
                    0x4 | 0x200, IntPtr.Zero, launch.WorkingDirectory, ref startup, out process)) // SUSPENDED | NEW_PROCESS_GROUP
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Native.AssignProcessToJobObject(job, process.Process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _launchStartedAtUtc = DateTime.UtcNow;
            if (Native.ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new Job(job);
            job = IntPtr.Zero;
            return result;
        }
        catch
        {
            if (process.Process != IntPtr.Zero) Native.TerminateProcess(process.Process, 1);
            throw;
        }
        finally
        {
            if (process.Thread != IntPtr.Zero) Native.CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) Native.CloseHandle(process.Process);
            if (job != IntPtr.Zero) Native.CloseHandle(job);
        }
    }

    public async Task<IReadOnlyList<string>> StopStartedSteamAsync(CancellationToken ct)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Steam cleanup requires Windows.");
        if (_launchStartedAtUtc is not { } started) return [];
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var current = Process.GetCurrentProcess();
        var session = current.SessionId;
        var quiet = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var found = false;
                var processes = Process.GetProcesses();
                try
                {
                    foreach (var process in processes)
                        try
                        {
                            if (!IsSteam(process.ProcessName)) continue;
                            // Pin the handle before checking creation time so a recycled PID
                            // cannot redirect Kill to a different process.
                            var handle = process.SafeHandle;
                            if (process.HasExited) continue;
                            var imagePath = new StringBuilder(32768); uint length = (uint)imagePath.Capacity;
                            if (!Native.QueryFullProcessImageNameW(handle, 0, imagePath, ref length))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                            if (!IsSteam(Path.GetFileNameWithoutExtension(imagePath.ToString()))) continue;
                            observed.Add(Path.GetFileNameWithoutExtension(imagePath.ToString()));
                            if (process.StartTime.ToUniversalTime() < started || process.SessionId != session)
                                throw new IOException("A Steam process predates this test or belongs to another session; it was left running.");
                            found = true;
                            process.Kill();
                        }
                        catch (InvalidOperationException) { } // The pinned process already exited.
                        catch (Win32Exception) when (process.HasExited) { } // A helper exited as its parent stopped.
                }
                finally { foreach (var process in processes) process.Dispose(); }
                if (found) quiet.Restart();
                else if (quiet.Elapsed >= TimeSpan.FromMilliseconds(500)) return observed.ToList();
                await Task.Delay(100, ct);
            }
        }
        catch (Exception e) when (e is Win32Exception or UnauthorizedAccessException)
        { throw new IOException("Cannot stop Steam processes started during the test.", e); }
    }

    private sealed class Job(IntPtr handle) : IDrmTestProcess
    {
        private IntPtr _handle = handle;
        public bool IsRunning
        {
            get
            {
                if (_handle == IntPtr.Zero) return false;
                if (!Native.QueryInformationJobObject(_handle, 1, out var accounting, (uint)Marshal.SizeOf<Native.Accounting>(), IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return accounting.ActiveProcesses != 0;
            }
        }
        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            try
            {
                if (!Native.TerminateJobObject(_handle, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var watch = Stopwatch.StartNew();
                while (IsRunning && watch.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(20);
                if (IsRunning) throw new IOException("The test process tree did not stop. Batch testing was stopped.");
            }
            catch (Exception e) when (e is Win32Exception or IOException)
            { throw new InvalidOperationException("Could not confirm test process cleanup; batch testing was stopped.", e); }
            finally { Native.CloseHandle(_handle); _handle = IntPtr.Zero; }
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits
        {
            internal long ProcessTime, JobTime;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters
        { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits
        {
            internal BasicLimits Basic;
            internal IoCounters Io;
            internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Accounting
        {
            internal long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
            internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
        {
            internal uint Size;
            internal string? Reserved, Desktop, Title;
            internal uint X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
            internal ushort ShowWindow, ReservedSize;
            internal IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation
        { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryInformationJobObject(IntPtr job, int type, out Accounting info, uint length, IntPtr returned);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string app, StringBuilder command, IntPtr processAttributes,
            IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment,
            string directory, ref StartupInfo startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageNameW(
            Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
    }
}
