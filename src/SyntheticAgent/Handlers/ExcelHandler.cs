using System.IO;
using System.Windows.Automation;
using SyntheticAgent.Automation;
using SyntheticAgent.Input;
using SyntheticAgent.Timing;

namespace SyntheticAgent.Handlers;

/// <summary>
/// Office-worker activity: open Excel, create a blank workbook, enter a small table with a formula
/// using human typing + Tab/Enter navigation, Save As to a path, then close. Every launch is
/// explorer-parented and every keystroke/click is genuine SendInput located via UIA.
/// </summary>
public sealed class ExcelHandler
{
    private const string Exe = "excel.exe";
    private const string Image = "EXCEL.EXE";

    private readonly DelayModel _timing;
    private readonly HumanMouse _mouse;
    private readonly Action<string> _log;

    public ExcelHandler(DelayModel timing, HumanMouse mouse, Action<string>? log = null)
    {
        _timing = timing;
        _mouse = mouse;
        _log = log ?? Console.WriteLine;
    }

    /// <summary>Returns the launched PID (for cleanup) and whether the task completed.</summary>
    public (int? Pid, bool Ok) Run(string savePath)
    {
        // Open an EXISTING blank workbook (no start screen, and Ctrl+S saves silently to this path).
        MinimalXlsx.EnsureBlank(savePath);
        DateTime mtimeBefore = File.GetLastWriteTimeUtc(savePath);
        _log($"[excel] prepared blank workbook: {savePath}");

        int? pid = AppLauncher.LaunchViaShell(Exe, Image, args: $"\"{savePath}\"", timeoutMs: 25000);
        if (pid is null) { _log("[excel] FAIL: Excel did not start."); return (null, false); }
        _log($"[excel] launched pid {pid}");

        var wb = WaitForWorkbookReady(pid.Value, out AutomationElement? nameBox, timeoutMs: 40000);
        if (wb is null) { _log("[excel] FAIL: workbook did not become ready in time."); return (pid, false); }
        _log($"[excel] workbook ready: {UiaHelpers.Describe(wb)}; nameBox={(nameBox is not null ? "yes" : "no")}");

        IntPtr hwnd = AppLauncher.WaitForWindow(pid.Value, 10000);
        if (hwnd == IntPtr.Zero) { _log("[excel] FAIL: no workbook window handle."); return (pid, false); }
        AppLauncher.BringToForeground(hwnd);
        Thread.Sleep(1500); // calm settle before any typing
        _log($"[excel] foreground==workbook: {NativeInput.ForegroundWindow() == hwnd}");

        // Focus cell A1 via the Name Box (UIA focus is reliable; coordinate clicks were not), and prove
        // keystrokes reach Excel by reading the Name Box back. Then type into the cell directly — do NOT
        // SetFocus the grid pane afterwards (that moves focus off the active cell).
        if (nameBox is not null)
        {
            UiaHelpers.SetFocus(nameBox);
            Thread.Sleep(400);
            NativeInput.KeyCombo(Vk.Control, Vk.A);
            foreach (char ch in "A1") NativeInput.UnicodeChar(ch);
            Thread.Sleep(300);
            _log($"[excel] Name Box reads: '{UiaHelpers.GetValue(nameBox)}' (confirms keystrokes reach Excel)");
            NativeInput.KeyPress(Vk.Return);
            Thread.Sleep(600);
        }

        var typist = new HumanTyping(_timing, () => NativeInput.ForegroundWindow() == hwnd);
        try
        {
            // A1 is active. Tab moves right; Enter returns to the row's start column, next row.
            EnterRow(typist, "Item", "Qty");
            EnterRow(typist, "Apples", "10");
            EnterRow(typist, "Oranges", "5");
            EnterRow(typist, "Total", "=SUM(B2:B3)");
        }
        catch (FocusLostException)
        {
            _log("[excel] ABORT: focus left the workbook mid-entry.");
            return (pid, false);
        }

        // Commit the last cell. (Authoritative content verification happens post-save by reading the
        // saved .xlsx; the UIA formula-bar probe is unreliable, so we don't gate on it.)
        NativeInput.KeyCombo(Vk.Control, Vk.Home);
        Thread.Sleep(400);

        // Save to the SAME file — Ctrl+S, no dialog. Confirm by the file's modified time changing.
        Thread.Sleep(400);
        NativeInput.KeyCombo(Vk.Control, Vk.S);
        bool saved = false;
        for (int i = 0; i < 15; i++)
        {
            Thread.Sleep(400);
            try { if (File.GetLastWriteTimeUtc(savePath) > mtimeBefore) { saved = true; break; } } catch { }
        }
        _log(saved ? $"[excel] saved (Ctrl+S) to {savePath}" : "[excel] WARN: save not confirmed via mtime.");

        // Graceful close. Cleanup net kills the PID if it lingers.
        NativeInput.KeyCombo(Vk.Menu, Vk.F4);
        Thread.Sleep(1500);

        return (pid, saved);
    }

    /// <summary>
    /// Poll until the workbook is genuinely interactive: window title indicates a workbook (not the
    /// bare "Excel" start screen) and the Name Box edit exists. Returns the workbook window element.
    /// </summary>
    private AutomationElement? WaitForWorkbookReady(int pid, out AutomationElement? nameBox, int timeoutMs)
    {
        nameBox = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long workbookSeenAt = -1;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (var w in UiaHelpers.WindowsForPid(pid))
            {
                string name;
                try { name = w.Current.Name ?? ""; } catch { continue; }
                // Start screen title is just "Excel"; a workbook is e.g. "Book1 - Excel".
                bool isWorkbook = name.Contains(" - Excel", StringComparison.OrdinalIgnoreCase)
                                  || name.StartsWith("Book", StringComparison.OrdinalIgnoreCase);
                if (!isWorkbook) continue;

                if (workbookSeenAt < 0) workbookSeenAt = sw.ElapsedMilliseconds;
                nameBox = UiaHelpers.FindByName(w, "Name Box", ControlType.Edit, 600)
                          ?? UiaHelpers.FindByName(w, "Name Box", null, 300);
                bool gridLive = nameBox is not null
                                || UiaHelpers.FindByControlType(w, ControlType.DataItem, 300) is not null;

                if (gridLive || sw.ElapsedMilliseconds - workbookSeenAt > 4000)
                    return w;
            }
            Thread.Sleep(500);
        }
        return null;
    }

    private void EnterRow(HumanTyping typist, string a, string b)
    {
        _pause();
        typist.TypeText(a);
        NativeInput.KeyPress(Vk.Tab);
        _pause();
        typist.TypeText(b);
        NativeInput.KeyPress(Vk.Return);
    }

    private void _pause() => Thread.Sleep((int)Math.Min(2500, _timing.NextActionDelayMs()));
}
