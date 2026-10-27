using System.ComponentModel;
using GameArchiver;

namespace GogArchiver.Probes;

internal static class SteamProcessMonitorProbes
{
    public static async Task RunAsync()
    {
        var started = DateTime.UtcNow;
        var foreign = new State("steam", 2, started.AddMinutes(-1)) { PinError = new Win32Exception(5) };
        var local = new State("SteamWebHelper", 1, started.AddSeconds(1));
        var game = new State("game", 1, started.AddSeconds(1));
        var foreignReporter = new State("steamerrorreporter64", 3, started) { PinError = new Win32Exception(5) };
        var monitor = Monitor(foreign, local, game, foreignReporter);
        Probe.Check("Steam observation includes only clients in the current Windows session",
            monitor.Observe().SequenceEqual(["SteamWebHelper"]));
        var cleanup = await monitor.StopStartedAsync(started, CancellationToken.None);
        Probe.Check("cleanup uses the same session scope before opening process handles",
            cleanup.Error is null && cleanup.SteamProcesses.SequenceEqual(["SteamWebHelper"])
            && local.Kills == 1 && foreign.Pins == 0 && foreignReporter.Pins == 0 && game.Pins == 0
            && !foreign.Exited && !foreignReporter.Exited && monitor.Observe().Count == 0);
        Probe.Check("observation and cleanup dispose every enumerated process wrapper",
            new[] { foreign, local, game, foreignReporter }.All(s => s.Created == s.Disposed));

        var old = new State("steam", 1, started.AddSeconds(-1));
        cleanup = await Monitor(old).StopStartedAsync(started, CancellationToken.None);
        Probe.Check("a preexisting current-session client remains running and stops cleanup",
            cleanup.Error is not null && old.Kills == 0 && !old.Exited);
        var changed = new State("steam", 1, started.AddSeconds(1)) { PinnedSession = 2 };
        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            cleanup = await Monitor(changed).StopStartedAsync(started, limit.Token);
        Probe.Check("pinned identity is rechecked before terminating a process", changed.Pins > 0 && changed.Kills == 0 && !changed.Exited);

        foreach (var failure in new[] { "open", "kill" })
        {
            var denied = new State("steam", 1, started.AddSeconds(1));
            if (failure == "open") denied.PinError = new Win32Exception(5); else denied.KillError = new Win32Exception(5);
            cleanup = await Monitor(denied).StopStartedAsync(started, CancellationToken.None);
            Probe.Check("observed Steam survives denied permission to " + failure,
                cleanup.Error is not null && cleanup.SteamProcesses.SequenceEqual(["steam"])
                && denied.Created == denied.Disposed && !denied.Exited);
        }
        using var cancellation = new CancellationTokenSource();
        var disappearing = new State("steam", 1, started.AddSeconds(1)) { OnKill = cancellation.Cancel };
        cleanup = await Monitor(disappearing).StopStartedAsync(started, cancellation.Token);
        Probe.Check("cleanup timeout retains evidence even after the client has disappeared",
            cleanup.Error is not null && cleanup.SteamProcesses.SequenceEqual(["steam"]) && disappearing.Exited);
    }

    private static SteamProcessMonitor Monitor(params State[] states) => new(1, () => states.Where(s => !s.Exited)
        .Select(s => (ISteamTestProcess)new Snapshot(s)).ToList());
    private sealed class State(string name, int session, DateTime started)
    {
        public string Name = name;
        public int Session = session;
        public DateTime Started = started;
        public int? PinnedSession;
        public bool Exited;
        public Exception? PinError, KillError;
        public Action? OnKill;
        public int Pins, Kills, Created, Disposed;
    }
    private sealed class Snapshot : ISteamTestProcess
    {
        private readonly State _state;
        public Snapshot(State state) { _state = state; state.Created++; }
        public string Name => _state.Name;
        public int SessionId => _state.Session;
        public bool HasExited => _state.Exited;
        public SteamProcessIdentity Pin()
        {
            _state.Pins++;
            if (_state.PinError is { } error) throw error;
            return new(_state.Name, _state.PinnedSession ?? _state.Session, _state.Started);
        }
        public void Kill()
        {
            _state.Kills++;
            if (_state.KillError is { } error) throw error;
            _state.Exited = true; _state.OnKill?.Invoke();
        }
        public void Dispose() => _state.Disposed++;
    }
}
