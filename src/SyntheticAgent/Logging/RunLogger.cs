using System.IO;

namespace SyntheticAgent.Logging;

/// <summary>
/// Timestamped action log for a session. Every entry is both printed and appended to a file in
/// test-output/. Records the RNG seed (for reproducibility) and keeps the event timeline in memory so
/// the self-audit (M10) can analyse inter-action gaps for periodicity.
/// </summary>
public sealed class RunLogger
{
    private readonly List<(DateTime Ts, string Msg)> _events = new();
    private readonly object _lock = new();

    public int Seed { get; }
    public string Path { get; }
    public DateTime Start { get; }
    public IReadOnlyList<(DateTime Ts, string Msg)> Events => _events;

    public RunLogger(string dir, int seed)
    {
        Directory.CreateDirectory(dir);
        Seed = seed;
        Start = DateTime.Now;
        Path = System.IO.Path.Combine(dir, $"run-{Start:yyyyMMdd-HHmmss}.log");
        Log($"RUN START  seed={seed}  pid={Environment.ProcessId}");
    }

    public void Log(string msg)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            _events.Add((now, msg));
            string line = $"{now:HH:mm:ss.fff}  +{(now - Start).TotalSeconds,6:F1}s  {msg}";
            Console.WriteLine(line);
            try { File.AppendAllText(Path, line + Environment.NewLine); } catch { /* best effort */ }
        }
    }
}
