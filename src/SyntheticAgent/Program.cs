using System.IO;
using System.Runtime.InteropServices;
using SyntheticAgent.Automation;
using SyntheticAgent.Handlers;
using SyntheticAgent.Input;
using SyntheticAgent.Timing;

namespace SyntheticAgent;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4. Makes GetSystemMetrics + UIA agree in physical
    // pixels so coordinate clicks land correctly on scaled displays.
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [STAThread] // required for clipboard access in the M2 smoke test
    private static int Main(string[] args)
    {
        try { SetProcessDpiAwarenessContext(PerMonitorAwareV2); } catch { /* older OS: ignore */ }

        string mode = args.Length > 0 ? args[0] : "";
        return mode switch
        {
            "" => DoubleClickStart(),
            "spike-launch" => SpikeLaunch(),
            "spike-type" => SpikeType(),
            "spike-timing" => SpikeTiming(args),
            "spike-humantype" => SpikeHumanType(args),
            "spike-mouse" => SpikeMouse(args),
            "spike-uia" => SpikeUia(args),
            "spike-tree" => SpikeTree(args),
            "handler-oocalc" => HandlerOoCalc(args),
            "handler-oowriter" => HandlerOoWriter(args),
            "handler-browser" => HandlerBrowser(args),
            "run" => RunSession(args, dryRun: false),
            "run-dry" => RunSession(args, dryRun: true),
            "audit" => Audit(args),
            _ => Help(),
        };
    }

    /// <summary>
    /// Where the agent writes logs/artifacts. In dev we walk up to the project root (marked by PLAN.md)
    /// and use its `test-output/`; for the published single-file exe (no project tree above it) we use a
    /// `test-output/` next to the exe. Robust whether run from bin/Debug or double-clicked on the Desktop.
    /// </summary>
    /// <summary>The project root (dir containing PLAN.md) when running from the dev tree, else null.</summary>
    private static string? ProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PLAN.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string OutDir()
    {
        string? root = ProjectRoot();
        string outDir = root is not null
            ? Path.Combine(root, "test-output")
            : Path.Combine(AppContext.BaseDirectory, "test-output");
        Directory.CreateDirectory(outDir);
        return outDir;
    }

    /// <summary>
    /// What a double-click (no args) does — the production path. Confirms with the user (so they can
    /// close windows and stay hands-off), then runs the default 15-minute session. Emergency stop is
    /// Ctrl+Alt+Shift+Q. A GUI MessageBox is used because WinExe has no console.
    /// </summary>
    private static int DoubleClickStart()
    {
        const uint MB_YESNOCANCEL = 0x3, MB_ICONQUESTION = 0x20, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        const int IDYES = 6, IDNO = 7;
        string msg =
            "Start a synthetic desktop session?\n\n" +
            "The agent types and moves the mouse on its own (OpenOffice + web browsing).\n\n" +
            "• Close your other windows first.\n" +
            "• Do not use the mouse or keyboard while it runs.\n" +
            "• Emergency stop at any time: Ctrl + Alt + Shift + Q\n\n" +
            "[Yes] = 15-minute run (full session)\n" +
            "[No]  = 5-minute run (for recording)\n" +
            "[Cancel] = don't start";
        int r = MessageBoxW(IntPtr.Zero, msg, "Synthetic Agent",
            MB_YESNOCANCEL | MB_ICONQUESTION | MB_SETFOREGROUND | MB_TOPMOST);
        return r switch
        {
            IDYES => RunSession(new[] { "run", "15" }, dryRun: false),
            IDNO => RunSession(new[] { "run", "5" }, dryRun: false),
            _ => 0, // Cancel
        };
    }

    private static int Help()
    {
        Console.WriteLine("SyntheticAgent — dev harness");
        Console.WriteLine("  spike-launch   M1: launch Notepad via explorer, verify parent == explorer.exe");
        Console.WriteLine("  spike-type     M2: type into Notepad via SendInput, verify via clipboard round-trip");
        Console.WriteLine("  spike-timing   M3: sample the delay model, dump CSV + stats (no input, safe)");
        Console.WriteLine("  spike-humantype M4: human typing (latency + typos) into Notepad, verify final text");
        Console.WriteLine("  spike-mouse    M5: plan a mouse path, dump CSV + curvature/overshoot stats (no input, safe)");
        Console.WriteLine("  spike-uia      M6: launch Notepad, UIA-find its edit control + list named controls (no input)");
        Console.WriteLine("  spike-tree <exe> <image> [fileArg]  M8 discovery: launch an app, dump its UIA tree to a file, close");
        Console.WriteLine("  handler-oocalc M8: OpenOffice Calc handler - blank .ods, table + formula, save, close");
        Console.WriteLine("  handler-oowriter M8: OpenOffice Writer handler - blank .odt, memo, save, close");
        Console.WriteLine("  handler-browser M8: browser handler - navigate sites, scroll, click a link (Edge, isolated profile)");
        Console.WriteLine("  run [minutes] [seed]      M9: full session - cycle handlers with idle gaps until the budget");
        Console.WriteLine("  run-dry [minutes] [seed]  M9: dry-run the schedule (NO apps launched, safe)");
        Console.WriteLine("  audit [seed]              M10: self-audit report (timing + process + flags), NO input, safe");
        return 0;
    }

    /// <summary>
    /// M1 launch-ancestry spike. Launches Notepad through explorer, confirms the new process parents
    /// under explorer.exe, then closes ONLY the Notepad it started (by exact PID, with a name recheck).
    /// </summary>
    private static int SpikeLaunch()
    {
        const string image = "notepad.exe";
        Console.WriteLine("[M1] Launching Notepad via explorer (IShellDispatch2.ShellExecute) ...");

        int? pid = AppLauncher.LaunchViaShell("notepad.exe", image);
        if (pid is null)
        {
            Console.WriteLine("[M1] FAIL: no new notepad.exe appeared within timeout.");
            return 1;
        }

        Console.WriteLine($"[M1] New Notepad PID = {pid}");

        var parent = ProcessTree.ParentOf(pid.Value);
        if (parent is null)
        {
            Console.WriteLine("[M1] FAIL: launched process vanished before parent could be read.");
            return 1;
        }

        Console.WriteLine($"[M1] Parent PID = {parent.Value.ParentPid}, Parent image = {parent.Value.ParentName}");

        bool pass = string.Equals(parent.Value.ParentName, "explorer.exe", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine(pass
            ? "[M1] PASS: parent is explorer.exe - launch ancestry trick holds."
            : $"[M1] FAIL: parent is '{parent.Value.ParentName}', expected explorer.exe.");

        bool killed = ProcessTree.KillIfMatches(pid.Value, image);
        Console.WriteLine(killed
            ? $"[M1] Cleanup: closed the Notepad we started (PID {pid})."
            : $"[M1] Cleanup: PID {pid} was not a live notepad.exe - left untouched.");

        return pass ? 0 : 1;
    }

    /// <summary>
    /// M2 native-input smoke test. Launches Notepad, focuses it (with a foreground guard so stray
    /// keystrokes can't land elsewhere), types a known string, then Ctrl+A / Ctrl+C and reads it back
    /// from the clipboard. The user's clipboard is saved first and restored at the end. Closes only
    /// the Notepad it started, by exact PID (discards the unsaved text, no save dialog).
    /// </summary>
    private static int SpikeType()
    {
        const string image = "notepad.exe";
        const string expected = "the quick brown fox 0123";

        string? savedClipboard = ClipboardText.Get();
        int? pid = null;
        try
        {
            Console.WriteLine("[M2] Launching Notepad via explorer ...");
            pid = AppLauncher.LaunchViaShell("notepad.exe", image);
            if (pid is null) { Console.WriteLine("[M2] FAIL: Notepad did not start."); return 1; }

            IntPtr hwnd = AppLauncher.WaitForWindow(pid.Value);
            if (hwnd == IntPtr.Zero) { Console.WriteLine("[M2] FAIL: no Notepad window."); return 1; }

            if (!AppLauncher.BringToForeground(hwnd))
            {
                Console.WriteLine("[M2] FAIL: could not focus Notepad (foreground guard) - aborting without typing.");
                return 1;
            }
            Thread.Sleep(400); // let the edit control settle

            Console.WriteLine($"[M2] Typing: \"{expected}\"");
            foreach (char ch in expected)
            {
                // Foreground guard before every keystroke: never type into another window.
                if (NativeInput.ForegroundWindow() != hwnd)
                {
                    Console.WriteLine("[M2] ABORT: focus left Notepad mid-type.");
                    return 1;
                }
                NativeInput.UnicodeChar(ch);
                Thread.Sleep(15);
            }

            Thread.Sleep(200);
            NativeInput.KeyCombo(Vk.Control, Vk.A); // select all
            Thread.Sleep(120);
            NativeInput.KeyCombo(Vk.Control, Vk.C); // copy
            Thread.Sleep(250);

            string? got = ClipboardText.Get();
            Console.WriteLine($"[M2] Clipboard round-trip = \"{got}\"");

            bool pass = string.Equals(got?.TrimEnd('\r', '\n'), expected, StringComparison.Ordinal);
            Console.WriteLine(pass
                ? "[M2] PASS: typed text matches - SendInput keyboard path works (incl. Ctrl combos)."
                : "[M2] FAIL: clipboard text did not match typed text.");
            return pass ? 0 : 1;
        }
        finally
        {
            if (pid is not null)
            {
                bool killed = ProcessTree.KillIfMatches(pid.Value, image);
                Console.WriteLine(killed
                    ? $"[M2] Cleanup: closed the Notepad we started (PID {pid})."
                    : $"[M2] Cleanup: PID {pid} was not a live notepad.exe - left untouched.");
            }
            ClipboardText.Set(savedClipboard);
            Console.WriteLine("[M2] Cleanup: clipboard restored.");
        }
    }

    /// <summary>
    /// M3 timing-model check. Dumps N action-delay samples (with state) and a set of keystroke
    /// latencies to test-output/, prints summary stats + an ASCII histogram so the heavy tail and
    /// non-uniformity are visible. Pure computation — no input, no app launch.
    /// </summary>
    private static int SpikeTiming(string[] args)
    {
        int n = args.Length > 1 && int.TryParse(args[1], out int parsed) ? parsed : 1000;
        int? seed = args.Length > 2 && int.TryParse(args[2], out int s) ? s : null;

        var model = new DelayModel(seed);
        Console.WriteLine($"[M3] DelayModel seed = {model.Seed}, samples = {n}");

        string outDir = OutDir();
        Directory.CreateDirectory(outDir);

        // Action delays.
        var delays = new double[n];
        var states = new bool[n];
        for (int i = 0; i < n; i++)
        {
            delays[i] = model.NextActionDelayMs();
            states[i] = model.InThinkingState;
        }
        string delaysCsv = Path.Combine(outDir, "delays.csv");
        using (var w = new StreamWriter(delaysCsv, false))
        {
            w.WriteLine("index,delay_ms,state");
            for (int i = 0; i < n; i++)
                w.WriteLine($"{i},{delays[i]:F1},{(states[i] ? "thinking" : "active")}");
        }

        // Keystroke latencies over a representative sentence.
        const string sample = "The quick brown fox jumps over 13 lazy dogs. Then, it rested; quietly!";
        var typing = new List<double>();
        for (int i = 1; i < sample.Length; i++)
            typing.Add(model.TypingLatencyMs(sample[i - 1], sample[i]));
        string typingCsv = Path.Combine(outDir, "typing_latency.csv");
        using (var w = new StreamWriter(typingCsv, false))
        {
            w.WriteLine("from,to,latency_ms");
            for (int i = 1; i < sample.Length; i++)
                w.WriteLine($"{Escape(sample[i - 1])},{Escape(sample[i])},{typing[i - 1]:F1}");
        }

        PrintStats("Action delays (ms)", delays);
        int thinking = states.Count(x => x);
        Console.WriteLine($"  thinking-state share: {thinking}/{n} ({100.0 * thinking / n:F1}%)");
        PrintHistogram(delays, bins: 20, maxBarWidth: 50);
        PrintStats("Keystroke latency (ms)", typing.ToArray());

        Console.WriteLine($"[M3] CSV written: {delaysCsv}");
        Console.WriteLine($"[M3] CSV written: {typingCsv}");
        return 0;
    }

    /// <summary>
    /// M4 human-typing smoke test. Types a paragraph (with punctuation + a newline) into a clean
    /// Notepad using the latency + typo model, then Ctrl+A/Ctrl+C and checks the FINAL text matches
    /// the intended text (so the typo→backspace→correct cycles must net out correctly). Clipboard is
    /// saved/restored; only the launched Notepad PID is closed.
    /// </summary>
    private static int SpikeHumanType(string[] args)
    {
        const string image = "notepad.exe";
        const string intended =
            "Dear team, here is the status update. We shipped the feature today; testing continues tomorrow.\n" +
            "Thanks, Sam";
        int? seed = args.Length > 1 && int.TryParse(args[1], out int s) ? s : null;

        var timing = new DelayModel(seed);
        Console.WriteLine($"[M4] DelayModel seed = {timing.Seed}");

        // Dedicated scratch file so we type into OUR tab, never the user's session content.
        string outDir = OutDir();
        Directory.CreateDirectory(outDir);
        string scratch = Path.Combine(outDir, "m4_scratch.txt");
        File.WriteAllText(scratch, "");

        int? pid = null;
        try
        {
            Console.WriteLine($"[M4] Launching Notepad on scratch file: {scratch}");
            pid = AppLauncher.LaunchViaShell("notepad.exe", image, args: $"\"{scratch}\"");
            if (pid is null) { Console.WriteLine("[M4] FAIL: Notepad did not start."); return 1; }

            IntPtr hwnd = AppLauncher.WaitForWindow(pid.Value);
            if (hwnd == IntPtr.Zero) { Console.WriteLine("[M4] FAIL: no Notepad window."); return 1; }
            if (!AppLauncher.BringToForeground(hwnd))
            {
                Console.WriteLine("[M4] FAIL: could not focus Notepad - aborting without typing.");
                return 1;
            }
            Thread.Sleep(600); // let the scratch tab become active

            // Clear the (our, empty) active tab so stray session text can't affect the check.
            NativeInput.KeyCombo(Vk.Control, Vk.A);
            Thread.Sleep(80);
            NativeInput.KeyPress(Vk.Delete);
            Thread.Sleep(120);

            var typist = new HumanTyping(timing, () => NativeInput.ForegroundWindow() == hwnd);
            Console.WriteLine("[M4] Typing a paragraph with human timing + typos ...");
            HumanTyping.Stats stats;
            try
            {
                stats = typist.TypeText(intended);
            }
            catch (FocusLostException)
            {
                Console.WriteLine("[M4] ABORT: focus left Notepad mid-type.");
                return 1;
            }
            Console.WriteLine($"[M4] Typed {stats.Chars} chars, injected {stats.Typos} typos, " +
                              $"{stats.ElapsedMs} ms (~{stats.EffectiveWpm:F0} wpm effective).");

            Thread.Sleep(250);
            NativeInput.KeyCombo(Vk.Control, Vk.S); // saves to the known scratch path (no dialog)
            Thread.Sleep(600);

            string got = File.ReadAllText(scratch).Replace("\r\n", "\n").TrimEnd('\n', '\r');
            string want = intended.Replace("\r\n", "\n");
            bool pass = string.Equals(got, want, StringComparison.Ordinal);
            Console.WriteLine($"[M4] Final text {(pass ? "matches" : "MISMATCH")} intended (read from scratch file).");
            if (!pass)
            {
                Console.WriteLine($"[M4]   intended: {want.Replace("\n", "\\n")}");
                Console.WriteLine($"[M4]   got     : {got.Replace("\n", "\\n")}");
            }
            else
            {
                Console.WriteLine("[M4] PASS: typos were self-corrected; final document is clean.");
            }
            return pass ? 0 : 1;
        }
        finally
        {
            if (pid is not null)
            {
                bool killed = ProcessTree.KillIfMatches(pid.Value, image);
                Console.WriteLine(killed
                    ? $"[M4] Cleanup: closed the Notepad we started (PID {pid})."
                    : $"[M4] Cleanup: PID {pid} was not a live notepad.exe - left untouched.");
            }
        }
    }

    /// <summary>
    /// M5 mouse-path check. Plans a WindMouse path and dumps it to CSV with curvature + overshoot
    /// stats so the curve and momentum overshoot are measurable. Pure math — no input, no app launch.
    /// </summary>
    private static int SpikeMouse(string[] args)
    {
        int seed = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 20251002;
        var mouse = new HumanMouse(seed);

        int x0 = 120, y0 = 820, x1 = 1480, y1 = 240; // a long diagonal
        var path = mouse.PlanPath(x0, y0, x1, y1);
        Console.WriteLine($"[M5] seed={seed} path from ({x0},{y0}) to ({x1},{y1}): {path.Count} points");

        string outDir = OutDir();
        Directory.CreateDirectory(outDir);
        string csv = Path.Combine(outDir, "mouse_path.csv");
        using (var w = new StreamWriter(csv, false))
        {
            w.WriteLine("i,x,y,perp_dev,along");
            Geometry(x0, y0, x1, y1, path, (i, p, perp, along) => w.WriteLine($"{i},{p.X},{p.Y},{perp:F2},{along:F2}"));
        }

        // Curvature = max perpendicular deviation from the straight line; overshoot = travel beyond target.
        double maxPerp = 0, maxAlong = 0;
        double d = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        Geometry(x0, y0, x1, y1, path, (_, _, perp, along) =>
        {
            maxPerp = Math.Max(maxPerp, Math.Abs(perp));
            maxAlong = Math.Max(maxAlong, along);
        });
        double overshoot = Math.Max(0, maxAlong - d);
        double totalLen = 0;
        for (int i = 1; i < path.Count; i++)
            totalLen += Math.Sqrt(Math.Pow(path[i].X - path[i - 1].X, 2) + Math.Pow(path[i].Y - path[i - 1].Y, 2));

        Console.WriteLine($"[M5] straight-line distance = {d:F0}px, path length = {totalLen:F0}px " +
                          $"(+{100 * (totalLen / d - 1):F0}% vs straight)");
        Console.WriteLine($"[M5] max perpendicular deviation (curvature) = {maxPerp:F1}px");
        Console.WriteLine($"[M5] overshoot beyond target = {overshoot:F1}px");
        Console.WriteLine($"[M5] est. Fitts move time = {HumanMouse.FittsMs(d):F0}ms");
        Console.WriteLine(maxPerp > 2 && totalLen > d
            ? "[M5] PASS: path is curved (not a straight line) and finite-length."
            : "[M5] WARN: path looks suspiciously straight.");
        Console.WriteLine($"[M5] CSV written: {csv}");
        return 0;
    }

    /// <summary>
    /// M6 UIA-plumbing check. Launches Notepad, resolves its top-level window via UIA, finds the text
    /// (Document/Edit) control and prints a click point, and lists named controls in the tree (how we'll
    /// discover real Excel/Edge control names). No input is sent; only the launched PID is closed.
    /// </summary>
    private static int SpikeUia(string[] args)
    {
        const string image = "notepad.exe";
        int? pid = null;
        try
        {
            Console.WriteLine("[M6] Launching Notepad via explorer ...");
            pid = AppLauncher.LaunchViaShell("notepad.exe", image);
            if (pid is null) { Console.WriteLine("[M6] FAIL: Notepad did not start."); return 1; }

            var window = UiaHelpers.WindowForPid(pid.Value);
            if (window is null) { Console.WriteLine("[M6] FAIL: UIA could not resolve the Notepad window."); return 1; }
            Console.WriteLine($"[M6] Window: {UiaHelpers.Describe(window)}");

            // The editable area is a Document control on Win11 Notepad (Edit on older builds).
            var edit = UiaHelpers.FindByControlType(window, System.Windows.Automation.ControlType.Document)
                       ?? UiaHelpers.FindByControlType(window, System.Windows.Automation.ControlType.Edit);
            if (edit is null)
            {
                Console.WriteLine("[M6] FAIL: no Document/Edit control found.");
            }
            else
            {
                var pt = UiaHelpers.GetClickPoint(edit);
                Console.WriteLine($"[M6] Edit control: {UiaHelpers.Describe(edit)}");
                Console.WriteLine(pt is null
                    ? "[M6] WARN: edit control has no clickable point."
                    : $"[M6] PASS: edit control click point = ({pt.Value.X},{pt.Value.Y}).");
            }

            // Dump named controls (control-name discovery aid).
            Console.WriteLine("[M6] Named controls in the window (first 25):");
            int shown = 0;
            var all = window.FindAll(System.Windows.Automation.TreeScope.Descendants,
                                     System.Windows.Automation.Condition.TrueCondition);
            foreach (System.Windows.Automation.AutomationElement el in all)
            {
                string name = SafeName(el);
                if (string.IsNullOrWhiteSpace(name)) continue;
                Console.WriteLine($"   - {UiaHelpers.Describe(el)}");
                if (++shown >= 25) break;
            }

            return edit is not null ? 0 : 1;
        }
        finally
        {
            if (pid is not null)
            {
                bool killed = ProcessTree.KillIfMatches(pid.Value, image);
                Console.WriteLine(killed
                    ? $"[M6] Cleanup: closed the Notepad we started (PID {pid})."
                    : $"[M6] Cleanup: PID {pid} was not a live notepad.exe - left untouched.");
            }
        }
    }

    /// <summary>
    /// M8 browser handler test. Uses the same browser PickBrowser() chooses (Chrome if installed,
    /// isolated in dev), visits a couple of allowed sites, scrolls, follows a link on each. Pass =
    /// navigated all intended sites. Sends input — hands-off.
    /// </summary>
    private static int HandlerBrowser(string[] args)
    {
        int? seed = args.Length > 1 && int.TryParse(args[1], out int s) ? s : null;
        var timing = new DelayModel(seed);
        var mouse = new HumanMouse(timing.Seed);
        Console.WriteLine($"[web] seed = {timing.Seed}");

        string outDir = OutDir();
        Directory.CreateDirectory(outDir);
        var (exe, image, extraArgs) = PickBrowser(outDir);
        Console.WriteLine($"[web] browser: {exe} args='{extraArgs}'");
        string[] urls = { "https://www.bbc.com/", "https://www.apple.com/" };

        var handler = new BrowserHandler(timing, mouse, exe, image, extraArgs, urls);
        List<int> pids = new();
        bool ok = false; List<string> titles = new();
        try
        {
            (pids, ok, titles) = handler.Run();
            bool pass = ok && titles.Count(t => !string.IsNullOrWhiteSpace(t)) >= urls.Length;
            Console.WriteLine($"[web] titles seen: {string.Join(" | ", titles)}");
            Console.WriteLine(pass
                ? "[web] PASS: navigated the allowed sites with genuine input (no chromedriver, no flags)."
                : "[web] FAIL: did not navigate all intended sites.");
            return pass ? 0 : 1;
        }
        finally
        {
            // Kill every NEW browser PID (our whole isolated instance), by exact PID — never the user's browser.
            int closed = 0;
            foreach (int pid in pids)
                if (ProcessTree.KillIfMatches(pid, image)) closed++;
            Console.WriteLine($"[web] Cleanup: closed {closed}/{pids.Count} of our {image} processes (user's browser untouched).");
        }
    }

    /// <summary>M8 OpenOffice Calc handler test (direct, so it runs even on the dev machine).</summary>
    private static int HandlerOoCalc(string[] args)
    {
        int? seed = args.Length > 1 && int.TryParse(args[1], out int s) ? s : null;
        var timing = new DelayModel(seed);
        Console.WriteLine($"[oo-calc] seed = {timing.Seed}");
        string? soffice = Handlers.OpenOfficeLauncher.FindSoffice();
        if (soffice is null) { Console.WriteLine("[oo-calc] FAIL: OpenOffice (soffice.exe) not found."); return 1; }
        Console.WriteLine($"[oo-calc] soffice: {soffice}");

        string savePath = Path.Combine(OutDir(), "calc_demo.ods");
        (int? pid, bool ok) = (null, false);
        try
        {
            (pid, ok) = new Handlers.OoCalcHandler(timing).Run(soffice, savePath);
            bool contentOk = ok
                && SavedOdfContains(savePath, "Item", "Apples", "Oranges", "Bananas", "Grapes", "Total")
                && SavedOdfContains(savePath, "office:value=\"35\"") // Qty =SUM(B3:B6) = 35
                && !SavedOdfContains(savePath, "Err:");              // no formula error
            Console.WriteLine(contentOk
                ? $"[oo-calc] PASS: .ods saved AND contains the typed data ({new FileInfo(savePath).Length} bytes)."
                : ok ? "[oo-calc] FAIL: saved but expected cell text not found." : "[oo-calc] FAIL: handler did not complete.");
            return contentOk ? 0 : 1;
        }
        finally { if (pid is not null) ProcessTree.KillIfMatches(pid.Value, "soffice.bin"); }
    }

    /// <summary>M8 OpenOffice Writer handler test (direct).</summary>
    private static int HandlerOoWriter(string[] args)
    {
        int? seed = args.Length > 1 && int.TryParse(args[1], out int s) ? s : null;
        var timing = new DelayModel(seed);
        Console.WriteLine($"[oo-writer] seed = {timing.Seed}");
        string? soffice = Handlers.OpenOfficeLauncher.FindSoffice();
        if (soffice is null) { Console.WriteLine("[oo-writer] FAIL: OpenOffice (soffice.exe) not found."); return 1; }
        Console.WriteLine($"[oo-writer] soffice: {soffice}");

        string savePath = Path.Combine(OutDir(), "writer_demo.odt");
        (int? pid, bool ok) = (null, false);
        try
        {
            (pid, ok) = new Handlers.OoWriterHandler(timing).Run(soffice, savePath);
            bool contentOk = ok && SavedOdfContains(savePath, "circulate", "numbers", "Sam");
            Console.WriteLine(contentOk
                ? $"[oo-writer] PASS: .odt saved AND contains the memo ({new FileInfo(savePath).Length} bytes)."
                : ok ? "[oo-writer] FAIL: saved but memo text not found." : "[oo-writer] FAIL: handler did not complete.");
            return contentOk ? 0 : 1;
        }
        finally { if (pid is not null) ProcessTree.KillIfMatches(pid.Value, "soffice.bin"); }
    }

    /// <summary>True if the saved ODF (.ods/.odt) content.xml contains all expected substrings.</summary>
    private static bool SavedOdfContains(string odfPath, params string[] expected)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(odfPath);
            var entry = zip.GetEntry("content.xml");
            if (entry is null) return false;
            using var r = new StreamReader(entry.Open());
            string xml = r.ReadToEnd();
            return expected.All(e => xml.Contains(e));
        }
        catch { return false; }
    }

    /// <summary>
    /// M9 session runner. `run [minutes] [seed]` runs the full session; `run-dry` logs the schedule
    /// without launching anything (safe). Default budget 15 minutes.
    /// </summary>
    private static int RunSession(string[] args, bool dryRun)
    {
        double minutes = args.Length > 1 && double.TryParse(args[1], out double m) ? m : 15.0;
        int? seed = args.Length > 2 && int.TryParse(args[2], out int s) ? s : null;

        var timing = new DelayModel(seed);
        var mouse = new HumanMouse(timing.Seed);

        string outDir = OutDir();
        var logger = new Logging.RunLogger(outDir, timing.Seed);

        var (bExe, bImage, bArgs) = PickBrowser(outDir);
        string? soffice = Handlers.OpenOfficeLauncher.FindSoffice();
        logger.Log($"browser: {bExe} args='{bArgs}'  |  office: OpenOffice{(soffice is not null ? " @ " + soffice : " (not found)")}");
        var cfg = new Timeline.SessionConfig(
            OutDir: outDir,
            BrowserExe: bExe,
            BrowserImage: bImage,
            BrowserArgs: bArgs,
            // Allowed sites in the test range (all served through a TLS-intercepting proxy -> cert warning,
            // which the browser handler clicks through). Content-rich sites so there's plenty to scroll
            // and real links to follow; internal links stay on the same allowed host.
            // Pool of allowed sites; the browser handler picks a random few each time it browses.
            Urls: new[]
            {
                "https://www.bbc.com/",
                "https://www.cnn.com/",
                "https://www.bleacherreport.com/",
                "https://www.cnet.com/",
                "https://www.apple.com/",
                "https://www.amazon.com/",
                "https://www.samsung.com/",
                "https://www.dell.com/",
                "https://www.asus.com/",
                "https://www.aliexpress.com/",
                "https://www.coursera.org/",
                "https://arxiv.org/",
                "https://archive.org/",
                "https://www.booking.com/",
                "https://coinmarketcap.com/",
                "https://www.coingecko.com/",
                "https://cloud.google.com/",
                "https://www.cloudflare.com/",
                "https://www.bing.com/",
            },
            Budget: TimeSpan.FromMinutes(minutes),
            SofficePath: soffice);

        if (!dryRun)
            Console.WriteLine($"[run] starting {minutes}-minute session (emergency stop: Ctrl+Alt+Shift+Q). Log: {logger.Path}");

        new Timeline.SessionRunner(timing, mouse, logger, cfg).Run(dryRun);
        return 0;
    }

    /// <summary>
    /// Choose the browser by environment. Dev (run from the project tree) uses isolated Edge InPrivate
    /// so test runs never touch the user's real browser session. Production (the published exe on a VM
    /// Desktop) uses Chrome with a PRISTINE command line — no automation/profile flags — falling back to
    /// plain Edge if Chrome isn't installed.
    /// </summary>
    private static (string Exe, string Image, string Args) PickBrowser(string outDir)
    {
        bool dev = ProjectRoot() is not null;
        if (ChromeInstalled())
        {
            // Chrome is the test-range browser. Dev: isolate with incognito + own profile so test runs
            // never touch the user's browsing. Production (VM): pristine command line.
            if (dev)
            {
                string profileDir = Path.Combine(outDir, "chrome-profile");
                return ("chrome.exe", "chrome.exe",
                    $"--incognito --no-first-run --no-default-browser-check --user-data-dir=\"{profileDir}\"");
            }
            return ("chrome.exe", "chrome.exe", "");
        }
        // Chrome absent (e.g. this dev box has only Edge): fall back to Edge, isolated in dev.
        if (dev)
        {
            string profileDir = Path.Combine(outDir, "edge-profile");
            return ("msedge.exe", "msedge.exe",
                $"--inprivate --no-first-run --no-default-browser-check --user-data-dir=\"{profileDir}\"");
        }
        return ("msedge.exe", "msedge.exe", "");
    }

    private static bool ChromeInstalled()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
        };
        return candidates.Any(File.Exists);
    }

    /// <summary>
    /// M10 self-audit. Builds the one-page report (timing autocorrelation/histogram, process-authenticity
    /// notes, command-line flag scan) and includes the most recent run log if present. Writes markdown to
    /// test-output/ and prints it. Pure analysis — no input, no app launch.
    /// </summary>
    private static int Audit(string[] args)
    {
        int seed = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 4242;

        string outDir = OutDir();
        Directory.CreateDirectory(outDir);

        // Use the newest run-*.log for real session evidence, if any.
        string? latestLog = Directory.Exists(outDir)
            ? Directory.GetFiles(outDir, "run-*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;

        string report = SelfAudit.TimingAnalyzer.BuildReport(seed, 1000, latestLog);
        string reportPath = Path.Combine(outDir, $"self-audit-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(reportPath, report);

        Console.WriteLine(report);
        Console.WriteLine($"[audit] report written: {reportPath}");
        return 0;
    }

    private static string SafeName(System.Windows.Automation.AutomationElement el)
    {
        try { return el.Current.Name; }
        catch { return ""; }
    }

    /// <summary>
    /// M8 discovery helper. Launches an arbitrary app, dumps its UIA control tree (indented, capped) to
    /// test-output/&lt;image&gt;_tree.txt so we can learn real control names, then closes it by PID.
    /// Launch + read-only inspect + close; sends no input. Usage: spike-tree &lt;exe&gt; &lt;image&gt; [fileArg]
    /// </summary>
    private static int SpikeTree(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: spike-tree <exe> <image.exe> [fileArg]");
            return 2;
        }
        string exe = args[1], image = args[2];
        string fileArg = args.Length > 3 ? $"\"{args[3]}\"" : "";

        int? pid = null;
        try
        {
            Console.WriteLine($"[tree] Launching {exe} ...");
            pid = AppLauncher.LaunchViaShell(exe, image, args: fileArg, timeoutMs: 20000);
            if (pid is null) { Console.WriteLine("[tree] FAIL: process did not start (or reused an existing one)."); return 1; }

            // Apps like Excel/Edge need a moment to build their UI.
            Thread.Sleep(3500);
            var window = UiaHelpers.WindowForPid(pid.Value, 15000);
            if (window is null) { Console.WriteLine("[tree] FAIL: no UIA window for pid."); return 1; }

            string outDir = OutDir();
            Directory.CreateDirectory(outDir);
            string treeFile = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(image)}_tree.txt");

            int nodes = 0;
            using (var w = new StreamWriter(treeFile, false))
            {
                w.WriteLine($"# UIA tree for {exe} (pid {pid}) captured {DateTime.Now:s}");
                DumpTree(window, w, 0, ref nodes, maxNodes: 600, maxDepth: 14);
            }
            Console.WriteLine($"[tree] Window: {UiaHelpers.Describe(window)}");
            Console.WriteLine($"[tree] Dumped {nodes} nodes to {treeFile}");
            return 0;
        }
        finally
        {
            if (pid is not null)
            {
                bool killed = ProcessTree.KillIfMatches(pid.Value, image);
                Console.WriteLine(killed
                    ? $"[tree] Cleanup: closed the process we started (PID {pid})."
                    : $"[tree] Cleanup: PID {pid} not a live {image} - left untouched.");
            }
        }
    }

    private static void DumpTree(System.Windows.Automation.AutomationElement el, StreamWriter w,
                                 int depth, ref int nodes, int maxNodes, int maxDepth)
    {
        if (nodes >= maxNodes || depth > maxDepth) return;
        nodes++;
        string indent = new string(' ', depth * 2);
        string name, type, autoId;
        try
        {
            var c = el.Current;
            name = c.Name; type = c.ControlType.ProgrammaticName.Replace("ControlType.", "");
            autoId = c.AutomationId;
        }
        catch { return; }
        if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(autoId))
            w.WriteLine($"{indent}[{type}] name='{name}' autoId='{autoId}'");
        else
            w.WriteLine($"{indent}[{type}]");

        var walker = System.Windows.Automation.TreeWalker.ControlViewWalker;
        try
        {
            var child = walker.GetFirstChild(el);
            while (child is not null && nodes < maxNodes)
            {
                DumpTree(child, w, depth + 1, ref nodes, maxNodes, maxDepth);
                child = walker.GetNextSibling(child);
            }
        }
        catch (System.Windows.Automation.ElementNotAvailableException) { }
    }

    // Project each path point onto the start->target axis: 'along' = distance along it, 'perp' = offset.
    private static void Geometry(int x0, int y0, int x1, int y1,
                                 List<(int X, int Y)> path, Action<int, (int X, int Y), double, double> emit)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / len, uy = dy / len;   // unit along-axis
        for (int i = 0; i < path.Count; i++)
        {
            double rx = path[i].X - x0, ry = path[i].Y - y0;
            double along = rx * ux + ry * uy;
            double perp = rx * (-uy) + ry * ux;
            emit(i, path[i], perp, along);
        }
    }

    private static string Escape(char c) => c == ',' ? "comma" : c == ' ' ? "space" : c.ToString();

    private static void PrintStats(string label, double[] xs)
    {
        var sorted = (double[])xs.Clone();
        Array.Sort(sorted);
        double Pct(double p) => sorted[(int)Math.Clamp(p / 100.0 * (sorted.Length - 1), 0, sorted.Length - 1)];
        Console.WriteLine($"[M3] {label}: n={xs.Length} min={sorted[0]:F0} " +
                          $"p50={Pct(50):F0} p90={Pct(90):F0} p95={Pct(95):F0} p99={Pct(99):F0} " +
                          $"max={sorted[^1]:F0} mean={xs.Average():F0}");
    }

    private static void PrintHistogram(double[] xs, int bins, int maxBarWidth)
    {
        double max = xs.Max();
        // Cap the display range at p98 so the long tail doesn't flatten the bulk; tail shown as overflow.
        var sorted = (double[])xs.Clone();
        Array.Sort(sorted);
        double top = sorted[(int)(0.98 * (sorted.Length - 1))];
        double binW = top / bins;
        var counts = new int[bins + 1]; // last bin = overflow (> p98)
        foreach (double x in xs)
        {
            int b = binW > 0 ? (int)(x / binW) : 0;
            counts[Math.Min(b, bins)]++;
        }
        int peak = counts.Max();
        Console.WriteLine("[M3] Histogram of action delays (bin width ~" + $"{binW:F0}ms, last row = tail > p98):");
        for (int b = 0; b <= bins; b++)
        {
            string range = b < bins ? $"{b * binW,6:F0}-{(b + 1) * binW,-6:F0}" : $">{top,6:F0}    ";
            int barLen = peak > 0 ? (int)Math.Round((double)counts[b] / peak * maxBarWidth) : 0;
            Console.WriteLine($"  {range} | {new string('#', barLen)} {counts[b]}");
        }
    }

    /// <summary>Minimal CF_UNICODETEXT clipboard get/set, used only to verify the M2 round-trip.</summary>
    private static class ClipboardText
    {
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);

        public static string? Get()
        {
            if (!OpenClipboard(IntPtr.Zero)) return null;
            try
            {
                IntPtr h = GetClipboardData(CF_UNICODETEXT);
                if (h == IntPtr.Zero) return null;
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { GlobalUnlock(h); }
            }
            finally { CloseClipboard(); }
        }

        public static void Set(string? text)
        {
            if (!OpenClipboard(IntPtr.Zero)) return;
            try
            {
                EmptyClipboard();
                if (string.IsNullOrEmpty(text)) return;
                byte[] bytes = System.Text.Encoding.Unicode.GetBytes(text + '\0');
                IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (hMem == IntPtr.Zero) return;
                IntPtr p = GlobalLock(hMem);
                if (p == IntPtr.Zero) return;
                try { Marshal.Copy(bytes, 0, p, bytes.Length); }
                finally { GlobalUnlock(hMem); }
                SetClipboardData(CF_UNICODETEXT, hMem); // ownership passes to the system
            }
            finally { CloseClipboard(); }
        }
    }
}
