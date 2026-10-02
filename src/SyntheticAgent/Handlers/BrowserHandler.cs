using System.Windows.Automation;
using SyntheticAgent.Automation;
using SyntheticAgent.Input;
using SyntheticAgent.Timing;

namespace SyntheticAgent.Handlers;

/// <summary>
/// The deliberate anti-GHOSTS browsing handler. Where GHOSTS drives the browser with Selenium
/// (spawning chromedriver.exe and setting --enable-automation, both glaring in Sysmon), this launches
/// the real browser via the shell (explorer-parented, no automation flags) and drives it with genuine
/// input: Ctrl+L to focus the address bar, typed URLs, mouse-wheel reading scrolls, and a UIA-located
/// link click. Nothing in the process tree or command line says "automation".
/// </summary>
public sealed class BrowserHandler
{
    private readonly DelayModel _timing;
    private readonly HumanMouse _mouse;
    private readonly Action<string> _log;

    private readonly string _exe;
    private readonly string _image;
    private readonly string _extraArgs;
    private readonly string[] _urls;

    public BrowserHandler(DelayModel timing, HumanMouse mouse,
                          string exe, string image, string extraArgs, string[] urls,
                          Action<string>? log = null)
    {
        _timing = timing;
        _mouse = mouse;
        _exe = exe;
        _image = image;
        _extraArgs = extraArgs;
        _urls = urls;
        _log = log ?? Console.WriteLine;
    }

    public (List<int> Pids, bool Ok, List<string> Titles) Run()
    {
        var seenTitles = new List<string>();

        // Snapshot existing browser PIDs so we can tell OUR isolated instance from the user's browser.
        var before = ProcessTree.PidsByName(_image);
        int? launched = AppLauncher.LaunchViaShell(_exe, _image, args: _extraArgs, timeoutMs: 25000);
        if (launched is null) { _log("[web] FAIL: browser did not start a new process."); return (new(), false, seenTitles); }

        // Our instance = all browser processes that appeared after launch (browser + renderers + gpu...).
        var ourPids = NewPids(before);
        _log($"[web] our browser PIDs: {string.Join(",", ourPids)}");

        // Find the window among any of our new processes (the window owner may not be the first PID).
        var (window, hwnd, ownerPid) = WaitForBrowserWindow(ourPids, before, 30000);
        if (window is null || hwnd == IntPtr.Zero)
        {
            _log("[web] FAIL: no browser window among our processes.");
            return (NewPids(before), false, seenTitles);
        }
        _log($"[web] window owner pid {ownerPid}: '{SafeName(window)}'");
        if (!Focus(hwnd, window))
        {
            _log("[web] FAIL: could not bring our browser window to the foreground - aborting so input can't leak to another window.");
            return (NewPids(before), false, seenTitles);
        }
        Thread.Sleep(1500);
        _log($"[web] foreground==browser: {NativeInput.ForegroundWindow() == hwnd}");

        // Dismiss any leftover first-run / sign-in / sync overlay before we start browsing.
        DismissOverlays(window, hwnd);

        bool clickedLink = false;
        for (int i = 0; i < _urls.Length; i++)
        {
            string url = _urls[i];
            _log($"[web] navigating to {url}");

            // Focus guard: never send keystrokes unless OUR window holds focus.
            if (!Focus(hwnd, window))
            {
                _log("[web] ABORT: lost foreground before navigation; not typing into another window.");
                break;
            }

            NativeInput.KeyCombo(Vk.Control, Vk.L); // focus address bar (all browsers)
            Thread.Sleep(500 + (int)_timing.NextActionDelayMs() % 600);
            if (NativeInput.ForegroundWindow() != hwnd) { _log("[web] ABORT: focus left before typing URL."); break; }
            TypeUrl(url);
            Thread.Sleep(250);
            NativeInput.KeyPress(Vk.Return);

            string title = WaitForTitleChange(ourPids, 15000);
            _log($"[web] page title: '{title}'");
            if (!string.IsNullOrWhiteSpace(title)) seenTitles.Add(title);

            // Read: scroll down in human bursts with pauses, then a little back up.
            ScrollPage(window, downBursts: 3 + (i % 2));
            Pause();

            // On the second site, click a real link (UIA-located, genuine mouse).
            if (i == 1 && !clickedLink)
            {
                clickedLink = ClickALink(window, ourPids, seenTitles);
            }
            Pause();
        }

        // Close the window gracefully.
        NativeInput.KeyCombo(Vk.Menu, Vk.F4);
        Thread.Sleep(1500);

        bool ok = seenTitles.Count >= _urls.Length; // navigated everywhere we intended
        return (NewPids(before), ok, seenTitles);
    }

    /// <summary>Best-effort dismissal of a first-run / sign-in / sync overlay in our window.</summary>
    private void DismissOverlays(AutomationElement window, IntPtr hwnd)
    {
        // ONLY very specific sign-in/sync FRE labels — never generic ones like "Close"/"Skip"
        // (those match the window's own close/tab buttons and would close our browser).
        string[] labels =
        {
            "Continue without signing in", "Confirm and start browsing", "Confirm and continue",
            "Start without your data", "Continue without this data"
        };
        for (int round = 0; round < 2; round++)
        {
            bool dismissed = false;
            foreach (string label in labels)
            {
                var btn = UiaHelpers.FindByName(window, label, ControlType.Button, 800);
                if (btn is not null && UiaHelpers.TryInvoke(btn))
                {
                    _log($"[web] dismissed overlay via '{label}'.");
                    dismissed = true;
                    Thread.Sleep(1200);
                    break;
                }
            }
            if (!dismissed) break;
            Focus(hwnd, window);
        }
    }

    /// <summary>Focus our browser window via UIA + forceful foreground; true if it now holds focus.</summary>
    private bool Focus(IntPtr hwnd, AutomationElement window)
    {
        UiaHelpers.SetFocus(window);
        AppLauncher.BringToForeground(hwnd);
        return NativeInput.ForegroundWindow() == hwnd;
    }

    private List<int> NewPids(HashSet<int> before)
    {
        var now = ProcessTree.PidsByName(_image);
        now.ExceptWith(before);
        return now.ToList();
    }

    private (AutomationElement? Window, IntPtr Hwnd, int OwnerPid) WaitForBrowserWindow(
        List<int> pids, HashSet<int> before, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            // Refresh our PID set (renderer/browser processes keep spawning during startup).
            foreach (int pid in NewPids(before))
            {
                foreach (var w in UiaHelpers.WindowsForPid(pid))
                {
                    string name;
                    IntPtr h;
                    try { name = w.Current.Name ?? ""; h = new IntPtr(w.Current.NativeWindowHandle); }
                    catch { continue; }
                    if (h != IntPtr.Zero && !string.IsNullOrWhiteSpace(name))
                        return (w, h, pid);
                }
            }
            Thread.Sleep(500);
        }
        return (null, IntPtr.Zero, 0);
    }

    /// <summary>Wait until a browser window title across our PIDs stabilises to something non-empty.</summary>
    private string WaitForTitleChange(List<int> pids, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string last = "";
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (int pid in pids)
                foreach (var w in UiaHelpers.WindowsForPid(pid))
                {
                    string name;
                    try { name = w.Current.Name ?? ""; } catch { continue; }
                    if (!string.IsNullOrWhiteSpace(name)) last = name;
                }
            Thread.Sleep(600);
            if (sw.ElapsedMilliseconds > 3500 && !string.IsNullOrWhiteSpace(last)) break;
        }
        return last;
    }

    private static string SafeName(AutomationElement el)
    {
        try { return el.Current.Name ?? ""; } catch { return ""; }
    }

    private void ScrollPage(AutomationElement window, int downBursts)
    {
        // Put the cursor over the page before wheel-scrolling.
        try
        {
            var r = window.Current.BoundingRectangle;
            if (!r.IsEmpty)
                _mouse.MoveTo((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        }
        catch (ElementNotAvailableException) { }

        for (int b = 0; b < downBursts; b++)
        {
            _mouse.Scroll(-(2 + (int)(_timing.NextActionDelayMs() % 3))); // scroll down
            Pause();
        }
        _mouse.Scroll(1); // small scroll back up (re-reading)
    }

    private bool ClickALink(AutomationElement window, List<int> pids, List<string> seenTitles)
    {
        try
        {
            // Search links within the page DOCUMENT only, so we don't click Edge's own chrome.
            var doc = UiaHelpers.FindByControlType(window, ControlType.Document, 3000);
            var root = doc ?? window;

            // The page's bounding box, to skip links that render in toolbars/footers off the page.
            System.Windows.Rect pageRect;
            try { pageRect = root.Current.BoundingRectangle; } catch { pageRect = System.Windows.Rect.Empty; }

            var links = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink));
            foreach (AutomationElement link in links)
            {
                string name;
                try { name = link.Current.Name ?? ""; } catch { continue; }
                if (string.IsNullOrWhiteSpace(name) || name.Length < 4) continue;
                if (name.Contains("Privacy", StringComparison.OrdinalIgnoreCase)) continue; // skip chrome footer

                var pt = UiaHelpers.GetClickPoint(link);
                if (pt is null) continue;
                if (!pageRect.IsEmpty &&
                    (pt.Value.Y < pageRect.Y + 80 || pt.Value.Y > pageRect.Y + pageRect.Height - 20))
                    continue; // skip links near the very top/bottom (likely chrome)

                _log($"[web] clicking link '{name}' at {pt}");
                _mouse.MoveClick(pt.Value.X, pt.Value.Y);
                string title = WaitForTitleChange(pids, 12000);
                _log($"[web] after link click, title: '{title}'");
                if (!string.IsNullOrWhiteSpace(title)) seenTitles.Add($"(link) {title}");
                return true;
            }
        }
        catch (ElementNotAvailableException) { }
        _log("[web] no clickable link found.");
        return false;
    }

    private void TypeUrl(string url)
    {
        foreach (char c in url)
        {
            NativeInput.UnicodeChar(c);
            Thread.Sleep(20 + (int)(_timing.TypingLatencyMs(' ', c) % 40));
        }
    }

    private void Pause() => Thread.Sleep((int)Math.Min(3500, _timing.NextActionDelayMs()));
}
