using System.IO;
using System.Windows.Automation;
using SyntheticAgent.Automation;
using SyntheticAgent.Input;
using SyntheticAgent.Timing;

namespace SyntheticAgent.Handlers;

/// <summary>
/// Office-worker activity: open an existing (blank) Word document, type a short memo with human
/// timing + typos, Ctrl+S (silent save to the known path), close. Reuses the Excel handler's lessons:
/// open-an-existing-file (no start screen / Save As dialog), UIA SetFocus (not coordinate clicks),
/// readiness polling, genuine SendInput typing.
/// </summary>
public sealed class WordHandler
{
    private const string Exe = "winword.exe";
    private const string Image = "WINWORD.EXE";

    // Distinctive words checked by the test against the saved document.
    public const string Memo =
        "Team update - 2 October\n\n" +
        "Quick note: the Q3 numbers are in and they look solid. " +
        "I'll circulate the full deck tomorrow morning; shout if you need anything sooner.\n\n" +
        "Thanks,\nSam";

    private readonly DelayModel _timing;
    private readonly HumanMouse _mouse;
    private readonly Action<string> _log;

    public WordHandler(DelayModel timing, HumanMouse mouse, Action<string>? log = null)
    {
        _timing = timing;
        _mouse = mouse;
        _log = log ?? Console.WriteLine;
    }

    public (int? Pid, bool Ok) Run(string savePath)
    {
        MinimalDocx.EnsureBlank(savePath);
        DateTime mtimeBefore = File.GetLastWriteTimeUtc(savePath);
        _log($"[word] prepared blank document: {savePath}");

        int? pid = AppLauncher.LaunchViaShell(Exe, Image, args: $"\"{savePath}\"", timeoutMs: 25000);
        if (pid is null) { _log("[word] FAIL: Word did not start."); return (null, false); }
        _log($"[word] launched pid {pid}");

        var doc = WaitForDocReady(pid.Value, out AutomationElement? body, timeoutMs: 40000);
        if (doc is null) { _log("[word] FAIL: document did not become ready."); return (pid, false); }
        _log($"[word] document ready: {UiaHelpers.Describe(doc)}; body={(body is not null ? "yes" : "no")}");

        IntPtr hwnd = AppLauncher.WaitForWindow(pid.Value, 10000);
        if (hwnd == IntPtr.Zero) { _log("[word] FAIL: no document window handle."); return (pid, false); }
        AppLauncher.BringToForeground(hwnd);
        Thread.Sleep(1500);
        _log($"[word] foreground==document: {NativeInput.ForegroundWindow() == hwnd}");

        // Focus the editable body via UIA (reliable), then make sure we're at the very start.
        if (body is not null) { UiaHelpers.SetFocus(body); Thread.Sleep(400); }
        NativeInput.KeyCombo(Vk.Control, Vk.Home);
        Thread.Sleep(300);

        var typist = new HumanTyping(_timing, () => NativeInput.ForegroundWindow() == hwnd);
        try
        {
            typist.TypeText(Memo);
        }
        catch (FocusLostException)
        {
            _log("[word] ABORT: focus left the document mid-type.");
            return (pid, false);
        }

        // Save to the SAME file — Ctrl+S, no dialog. Confirm via modified-time change.
        Thread.Sleep(500);
        NativeInput.KeyCombo(Vk.Control, Vk.S);
        bool saved = false;
        for (int i = 0; i < 15; i++)
        {
            Thread.Sleep(400);
            try { if (File.GetLastWriteTimeUtc(savePath) > mtimeBefore) { saved = true; break; } } catch { }
        }
        _log(saved ? $"[word] saved (Ctrl+S) to {savePath}" : "[word] WARN: save not confirmed via mtime.");

        NativeInput.KeyCombo(Vk.Menu, Vk.F4);
        Thread.Sleep(1500);

        return (pid, saved);
    }

    /// <summary>Poll until the Word window title shows a document ("... - Word") and the body exists.</summary>
    private AutomationElement? WaitForDocReady(int pid, out AutomationElement? body, int timeoutMs)
    {
        body = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long docSeenAt = -1;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (var w in UiaHelpers.WindowsForPid(pid))
            {
                string name;
                try { name = w.Current.Name ?? ""; } catch { continue; }
                if (!name.Contains(" - Word", StringComparison.OrdinalIgnoreCase)) continue;

                if (docSeenAt < 0) docSeenAt = sw.ElapsedMilliseconds;
                body = UiaHelpers.FindByControlType(w, ControlType.Document, 600)
                       ?? UiaHelpers.FindByControlType(w, ControlType.Edit, 400);
                if (body is not null || sw.ElapsedMilliseconds - docSeenAt > 4000)
                    return w;
            }
            Thread.Sleep(500);
        }
        return null;
    }
}
