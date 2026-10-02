using System.Collections.Concurrent;
using System.IO;
using SyntheticAgent.Automation;
using SyntheticAgent.Input;

namespace SyntheticAgent.Control;

/// <summary>Thrown from deep inside input loops when the user asks to quit, to unwind the session fast.</summary>
public sealed class StopRequestedException : Exception
{
    public StopRequestedException() : base("Stop requested by user.") { }
}

/// <summary>
/// Global quit signal. A background thread polls the emergency-stop chord (Ctrl+Alt+Shift+Q) every
/// ~50ms and sets the flag, so a brief press is caught even mid-keystroke. Every input loop calls
/// <see cref="ThrowIfRequested"/>, so an abort unwinds near-instantly from anywhere.
/// </summary>
public static class StopSignal
{
    private static volatile bool _requested;
    private static Thread? _watcher;
    private static Action<string>? _log;

    public static bool Requested => _requested;

    public static void Start(Action<string>? log = null)
    {
        _log = log;
        _requested = false;
        if (_watcher is not null) return;
        _watcher = new Thread(Watch) { IsBackground = true, Name = "stop-watcher" };
        _watcher.Start();
    }

    public static void Request()
    {
        if (!_requested) { _requested = true; _log?.Invoke("STOP requested."); }
    }

    public static void ThrowIfRequested()
    {
        if (_requested) throw new StopRequestedException();
    }

    private static void Watch()
    {
        while (true)
        {
            if (NativeInput.EmergencyStopHeld()) { Request(); return; }
            Thread.Sleep(50);
        }
    }
}

/// <summary>
/// Registry of processes the agent launched, so a quit (or a crash) can close them and nothing is
/// left running. Keyed by pid; each entry remembers the image name for a safe by-exact-pid kill.
/// Also mirrored to test-output/launched-pids.txt for transparency.
/// </summary>
public static class LaunchedProcesses
{
    private static readonly ConcurrentDictionary<int, string> _map = new();
    private static string? _logPath;

    public static void SetLogPath(string dir)
    {
        try { Directory.CreateDirectory(dir); _logPath = Path.Combine(dir, "launched-pids.txt"); } catch { }
    }

    public static void Register(int pid, string image)
    {
        _map[pid] = image;
        Append($"{DateTime.Now:HH:mm:ss} launched pid={pid} image={image}");
    }

    public static void Unregister(int pid) => _map.TryRemove(pid, out _);

    /// <summary>Kill every still-registered process by exact pid (never by name). Returns count closed.</summary>
    public static int KillAll(Action<string>? log = null)
    {
        int closed = 0;
        foreach (var kv in _map)
        {
            if (ProcessTree.KillIfMatches(kv.Key, kv.Value)) { closed++; Append($"{DateTime.Now:HH:mm:ss} killed pid={kv.Key}"); }
            _map.TryRemove(kv.Key, out _);
        }
        log?.Invoke($"cleanup: closed {closed} launched process(es) on quit.");
        return closed;
    }

    private static void Append(string line)
    {
        if (_logPath is null) return;
        try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
    }
}
