using System.Diagnostics;
using System.IO;
using SyntheticAgent.Automation;
using SyntheticAgent.Handlers;
using SyntheticAgent.Input;
using SyntheticAgent.Logging;
using SyntheticAgent.Timing;

namespace SyntheticAgent.Timeline;

/// <summary>Everything a session needs; swap these for a different persona without recompiling logic.</summary>
public sealed record SessionConfig(
    string OutDir,
    string BrowserExe,
    string BrowserImage,
    string BrowserArgs,
    string[] Urls,
    TimeSpan Budget);

/// <summary>
/// Lean orchestrator: cycles the office + browsing activities in a shuffled order with realistic idle
/// gaps between tasks, until the time budget is spent. Enforces the budget, honours the emergency-stop
/// chord (Ctrl+Alt+Shift+Q), logs start/end of every activity, and cleans up each activity's processes.
/// (Deliberately NOT a full config-driven engine — just a runner.)
/// </summary>
public sealed class SessionRunner
{
    private readonly DelayModel _timing;
    private readonly HumanMouse _mouse;
    private readonly RunLogger _log;
    private readonly SessionConfig _cfg;
    private readonly Random _rng;

    public SessionRunner(DelayModel timing, HumanMouse mouse, RunLogger log, SessionConfig cfg)
    {
        _timing = timing;
        _mouse = mouse;
        _log = log;
        _cfg = cfg;
        _rng = new Random(unchecked(timing.Seed ^ 0x51ec7));
    }

    private sealed record Activity(string Name, string Image, Func<List<int>> Run, double EstDurationSec);

    public void Run(bool dryRun = false)
    {
        if (dryRun) { RunDry(); return; }

        // Quit support: background watcher for Ctrl+Alt+Shift+Q, and a registry so an abort closes apps.
        Control.LaunchedProcesses.SetLogPath(_cfg.OutDir);
        Control.StopSignal.Start(_log.Log);

        var activities = BuildActivities();
        Shuffle(activities);
        _log.Log($"SESSION plan: budget={_cfg.Budget.TotalMinutes:F0}min, order=[{string.Join(", ", activities.Select(a => a.Name))}]  (quit anytime: Ctrl+Alt+Shift+Q)");

        var sw = Stopwatch.StartNew();
        int i = 0;
        bool stopped = false;
        while (sw.Elapsed < _cfg.Budget)
        {
            if (Control.StopSignal.Requested) { stopped = true; break; }

            var act = activities[i % activities.Count];
            i++;
            _log.Log($"activity START  #{i}: {act.Name}  (elapsed {sw.Elapsed:mm\\:ss} / {_cfg.Budget:mm\\:ss})");
            try
            {
                var pids = act.Run();
                Cleanup(pids, act.Image);
            }
            catch (Control.StopRequestedException)
            {
                _log.Log($"  STOP during {act.Name} — unwinding.");
                stopped = true;
                break;
            }
            catch (Exception ex)
            {
                _log.Log($"  activity ERROR in {act.Name}: {ex.GetType().Name}: {ex.Message}");
            }
            _log.Log($"activity END    #{i}: {act.Name}");

            if (sw.Elapsed >= _cfg.Budget) break;
            double gapMs = InterTaskGapMs();
            _log.Log($"idle gap: {gapMs / 1000.0:F1}s (worker pause between tasks)");
            if (!SleepWithChecks(gapMs, sw)) { stopped = Control.StopSignal.Requested; break; }
        }

        if (stopped)
        {
            _log.Log("SESSION STOPPED by user — closing anything still open.");
            Control.LaunchedProcesses.KillAll(_log.Log);
        }
        _log.Log($"SESSION {(stopped ? "STOPPED" : "COMPLETE")}  elapsed {sw.Elapsed:mm\\:ss}, activities run={i}");
    }

    /// <summary>Simulated-clock dry run: proves the schedule/budget logic instantly, launches nothing.</summary>
    private void RunDry()
    {
        var activities = BuildActivities();
        Shuffle(activities);
        _log.Log($"SESSION plan (DRY): budget={_cfg.Budget.TotalMinutes:F0}min, order=[{string.Join(", ", activities.Select(a => a.Name))}]");

        double budgetSec = _cfg.Budget.TotalSeconds;
        double elapsed = 0;
        int i = 0;
        while (elapsed < budgetSec)
        {
            var act = activities[i % activities.Count];
            i++;
            _log.Log($"[dry] activity #{i}: {act.Name}  @ {Fmt(elapsed)}  (~{act.EstDurationSec:F0}s)");
            elapsed += act.EstDurationSec;
            if (elapsed >= budgetSec) break;
            double gap = InterTaskGapMs() / 1000.0;
            _log.Log($"[dry]   idle gap {gap:F1}s");
            elapsed += gap;
        }
        _log.Log($"[dry] SESSION would complete at {Fmt(Math.Min(elapsed, budgetSec))}, activities={i}");
    }

    private static string Fmt(double sec) => TimeSpan.FromSeconds(sec).ToString(@"mm\:ss");

    private List<Activity> BuildActivities()
    {
        return new List<Activity>
        {
            new("excel", "EXCEL.EXE", () =>
            {
                var (pid, _) = new ExcelHandler(_timing, _mouse, _log.Log)
                    .Run(Path.Combine(_cfg.OutDir, "excel_demo.xlsx"));
                return pid is null ? new() : new() { pid.Value };
            }, EstDurationSec: 90),
            new("word", "WINWORD.EXE", () =>
            {
                var (pid, _) = new WordHandler(_timing, _mouse, _log.Log)
                    .Run(Path.Combine(_cfg.OutDir, "word_demo.docx"));
                return pid is null ? new() : new() { pid.Value };
            }, EstDurationSec: 75),
            new("browser", _cfg.BrowserImage, () =>
            {
                var (pids, _, _) = new BrowserHandler(_timing, _mouse,
                    _cfg.BrowserExe, _cfg.BrowserImage, _cfg.BrowserArgs, _cfg.Urls, _log.Log).Run();
                return pids;
            }, EstDurationSec: 110),
        };
    }

    /// <summary>Realistic between-task idle: mostly 6–25s, occasionally up to ~45s.</summary>
    private double InterTaskGapMs()
    {
        double baseMs = 6000 + _rng.NextDouble() * 19000;
        if (_rng.NextDouble() < 0.15) baseMs += 10000 + _rng.NextDouble() * 10000;
        return baseMs;
    }

    private bool SleepWithChecks(double ms, Stopwatch sw)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (Control.StopSignal.Requested) return false;
            if (sw.Elapsed >= _cfg.Budget) return false;
            Thread.Sleep(100);
        }
        return true;
    }

    private void Cleanup(List<int> pids, string image)
    {
        int closed = 0;
        foreach (int pid in pids)
        {
            if (ProcessTree.KillIfMatches(pid, image)) closed++;
            Control.LaunchedProcesses.Unregister(pid);
        }
        if (closed > 0) _log.Log($"  cleanup: closed {closed} leftover {image} process(es).");
    }

    private void Shuffle<T>(IList<T> list)
    {
        for (int n = list.Count - 1; n > 0; n--)
        {
            int k = _rng.Next(n + 1);
            (list[n], list[k]) = (list[k], list[n]);
        }
    }
}
