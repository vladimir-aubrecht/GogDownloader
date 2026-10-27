using System.ComponentModel;
using System.Diagnostics;

namespace GameArchiver;

internal sealed record SteamProcessIdentity(string Name, int SessionId, DateTime StartedAtUtc);

/// <summary>Process inspection boundary; Pin binds subsequent inspection and termination to one handle.</summary>
internal interface ISteamTestProcess : IDisposable
{
    string Name { get; }
    int SessionId { get; }
    bool HasExited { get; }
    SteamProcessIdentity Pin();
    void Kill();
}

/// <summary>Shared session scope for observation, startup checks and cleanup.</summary>
internal sealed class SteamProcessMonitor(int sessionId, Func<IReadOnlyList<ISteamTestProcess>> enumerate)
{
    private static bool IsSteam(string name) => name.Equals("steam", StringComparison.OrdinalIgnoreCase)
        || name.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase)
        || name.Equals("steamerrorreporter", StringComparison.OrdinalIgnoreCase)
        || name.Equals("steamerrorreporter64", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> Observe()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<ISteamTestProcess> processes = [];
        try
        {
            processes = enumerate();
            foreach (var process in processes)
                try
                {
                    if (IsSteam(process.Name) && process.SessionId == sessionId) found.Add(process.Name);
                }
                catch (InvalidOperationException) { } // Process exited during enumeration.
        }
        catch (Exception e) when (e is Win32Exception or UnauthorizedAccessException)
        { throw new IOException("Cannot inspect running processes; Steam observation is unavailable.", e); }
        finally { foreach (var process in processes) process.Dispose(); }
        return found.ToList();
    }

    public async Task<DrmTestCleanupResult> StopStartedAsync(DateTime startedAtUtc, CancellationToken ct)
    {
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var quiet = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var found = false;
                var processes = enumerate();
                try
                {
                    foreach (var process in processes)
                        try
                        {
                            var name = process.Name;
                            // Ignore other sessions before opening handles or collecting evidence.
                            if (!IsSteam(name) || process.SessionId != sessionId) continue;
                            // Observation succeeds independently of permission to terminate the process.
                            observed.Add(name);
                            var identity = process.Pin();
                            if (!IsSteam(identity.Name) || identity.SessionId != sessionId) continue;
                            if (identity.StartedAtUtc < startedAtUtc)
                                throw new IOException("A Steam process predates this test; it was left running.");
                            found = true;
                            process.Kill();
                        }
                        catch (InvalidOperationException) { } // The pinned process already exited.
                        catch (Win32Exception) when (process.HasExited) { }
                }
                finally { foreach (var process in processes) process.Dispose(); }
                if (found) quiet.Restart();
                else if (quiet.Elapsed >= TimeSpan.FromMilliseconds(500)) return new(observed.ToList());
                await Task.Delay(100, ct);
            }
        }
        catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            return new(observed.ToList(), e is OperationCanceledException
                ? "Steam cleanup timed out or was cancelled." : e.Message);
        }
    }
}
