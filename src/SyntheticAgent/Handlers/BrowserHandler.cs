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
    private readonly Random _rng;

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
        _rng = new Random(unchecked(timing.Seed ^ 0x53ec));
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

        // Pick a RANDOM few sites from the pool for this browse session (different each time).
        var sites = _urls.OrderBy(_ => _rng.Next()).Take(Math.Min(_urls.Length, _rng.Next(2, 5))).ToList();
        _log($"[web] this session visits {sites.Count}/{_urls.Length} sites: {string.Join(", ", sites)}");

        for (int i = 0; i < sites.Count; i++)
        {
            string url = sites[i];
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
            // The test range serves everything through a TLS-intercepting proxy, so each new host first
            // shows the browser's "your connection isn't private" page — click Advanced -> Proceed.
            if (IsCertWarning(title))
            {
                DismissCertWarning(window, hwnd, ourPids);
                title = WaitForTitleChange(ourPids, 12000);
            }

            // If we're STILL on the interstitial, there was no bypass (e.g. an HSTS host, where Chrome
            // offers no "Proceed" link and "thisisunsafe" is refused). The only links left on that page
            // are traps — "Learn more about this warning" (opens a new tab) and "Running Windows Network
            // Diagnostics" (launches the Windows troubleshooter and hangs). Do NOT scroll or click here;
            // abandon this site and move to the next one.
            if (IsCertWarning(title))
            {
                _log($"[web] still on the security interstitial for {url} (no bypass available) — skipping this site.");
                continue;
            }

            _log($"[web] page title: '{title}'");
            if (!string.IsNullOrWhiteSpace(title)) seenTitles.Add(title);

            // Clear any cookie/consent overlay so it doesn't cover the content we click.
            DismissCookieBanner(window, hwnd);

            // Read: scroll down in human bursts with idle fidgets.
            ScrollPage(window, downBursts: 3 + (i % 3));
            Pause();

            // Follow a real in-page link (layer 1), then read the linked page.
            if (ClickALink(window, hwnd, ourPids, seenTitles))
            {
                DismissCookieBanner(window, hwnd);
                ScrollPage(window, downBursts: 2 + (i % 2));
                Pause();

                // Sometimes go a second layer deep (more likely on news/shopping sites).
                bool deepable = UrlLooksDeepWorthy(url);
                if ((deepable && _rng.NextDouble() < 0.6) || _rng.NextDouble() < 0.25)
                {
                    _log("[web] going a second layer deep ...");
                    if (ClickALink(window, hwnd, ourPids, seenTitles))
                    {
                        ScrollPage(window, downBursts: 2);
                        Pause();
                    }
                }
            }
        }

        // Close the window gracefully.
        NativeInput.KeyCombo(Vk.Menu, Vk.F4);
        Thread.Sleep(1500);

        // Navigated at least the sites we chose for this session (link clicks add extra titles).
        bool ok = seenTitles.Count(t => !string.IsNullOrWhiteSpace(t)) >= sites.Count;
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

    /// <summary>Does a window title look like the browser's TLS/certificate interstitial?</summary>
    private static bool IsCertWarning(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        string t = title.ToLowerInvariant();
        return t.Contains("privacy error") || t.Contains("isn't private") || t.Contains("not private")
               || t.Contains("certificate error") || t.Contains("security certificate");
    }

    /// <summary>
    /// Click through the "your connection isn't private" interstitial: Advanced -> Proceed link. Falls
    /// back to Chrome's keyboard bypass phrase if the proceed link can't be located.
    /// </summary>
    private void DismissCertWarning(AutomationElement window, IntPtr hwnd, List<int> pids)
    {
        _log("[web] certificate warning page detected; clicking through (Advanced -> Proceed).");
        Focus(hwnd, window);

        var advanced = UiaHelpers.FindByName(window, "Advanced", ControlType.Button, 2500);
        if (advanced is not null) { InvokeOrClick(advanced); Thread.Sleep(900); }

        var proceed = FindByNameContains(window,
            new[] { "Proceed to", "unsafe", "Continue to this", "Continue to the" }, 3000);
        if (proceed is not null)
        {
            InvokeOrClick(proceed);
        }
        else
        {
            // Chrome bypass: typing this on the interstitial proceeds for cert errors.
            _log("[web] proceed link not found; using keyboard bypass.");
            foreach (char c in "thisisunsafe") NativeInput.UnicodeChar(c);
        }
        Thread.Sleep(1200);
    }

    /// <summary>First visible clickable element whose name contains any of the substrings (links/buttons).</summary>
    private AutomationElement? FindByNameContains(AutomationElement root, string[] needles, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var cond = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                var els = root.FindAll(TreeScope.Descendants, cond);
                foreach (AutomationElement el in els)
                {
                    string name;
                    try { name = el.Current.Name ?? ""; } catch { continue; }
                    if (needles.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                        return el;
                }
            }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(300);
        }
        return null;
    }

    /// <summary>Activate an element via UIA invoke, falling back to a genuine mouse click at its point.</summary>
    private void InvokeOrClick(AutomationElement el)
    {
        if (UiaHelpers.TryInvoke(el)) return;
        var pt = UiaHelpers.GetClickPoint(el);
        if (pt is not null) _mouse.MoveClick(pt.Value.X, pt.Value.Y);
    }

    // Link/button names that are page chrome or consent UI, not article content — never follow these.
    private static readonly string[] SkipLink =
    {
        "accept", "reject", "cookie", "consent", "sign in", "sign up", "log in", "login",
        "subscribe", "accessibility", "privacy", "terms", "menu", "skip to", "settings",
        "newsletter", "get the app", "download the app", "manage", "preferences", "advertis",
        // Security-interstitial / browser-error-page links — clicking these is a dead end (new tab,
        // support page, or the Windows Network Diagnostics troubleshooter popup that hangs the agent).
        "learn more", "about this warning", "network diagnostics", "diagnos", "help improve",
        "hide advanced", "advanced", "proceed", "back to safety", "reload",
    };

    private static System.Windows.Rect WindowRect(AutomationElement window)
    {
        try { var r = window.Current.BoundingRectangle; return r.IsEmpty ? System.Windows.Rect.Empty : r; }
        catch (ElementNotAvailableException) { return System.Windows.Rect.Empty; }
    }

    private void ScrollPage(AutomationElement window, int downBursts)
    {
        // Move the cursor to a RANDOM spot in the page (not always dead-centre) before wheel-scrolling.
        var r = WindowRect(window);
        if (!r.IsEmpty)
        {
            int x = (int)(r.X + 100 + _rng.NextDouble() * Math.Max(1, r.Width - 200));
            int y = (int)(r.Y + 150 + _rng.NextDouble() * Math.Max(1, r.Height * 0.55));
            _mouse.MoveTo(x, y);
        }

        for (int b = 0; b < downBursts; b++)
        {
            _mouse.Scroll(-(2 + (int)(_timing.NextActionDelayMs() % 3))); // scroll down
            if (_rng.NextDouble() < 0.5) _mouse.Wiggle(30);               // small idle fidget
            Pause();
        }
        if (_rng.NextDouble() < 0.6) _mouse.Scroll(1); // occasional scroll back up (re-reading)
    }

    private readonly HashSet<string> _clicked = new(StringComparer.OrdinalIgnoreCase);

    private bool ClickALink(AutomationElement window, IntPtr hwnd, List<int> pids, List<string> seenTitles)
    {
        try
        {
            var doc = UiaHelpers.FindByControlType(window, ControlType.Document, 3000);
            var root = doc ?? window;
            var wr = WindowRect(window);
            if (wr.IsEmpty) { _log("[web] no window rect."); return false; }

            // Target the CONTENT area: skip the browser toolbar AND the site's sticky header/hero (top
            // ~22%) and the footer (bottom ~60px), where logos/nav/consent live. Prefer longer text
            // (headlines) and never re-click the same link.
            double topCut = wr.Y + Math.Max(110, wr.Height * 0.22);
            double botCut = wr.Y + wr.Height - 60;

            var candidates = new List<(int X, int Y, string Name)>();
            foreach (AutomationElement link in root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink)))
            {
                string name;
                try { name = (link.Current.Name ?? "").Trim(); } catch { continue; }
                if (name.Length < 12) continue;
                if (_clicked.Contains(name)) continue;
                if (SkipLink.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;

                var pt = UiaHelpers.GetClickPoint(link);
                if (pt is null) continue;
                if (pt.Value.X < wr.X + 6 || pt.Value.X > wr.X + wr.Width - 6) continue;
                if (pt.Value.Y < topCut || pt.Value.Y > botCut) continue;          // content band only
                candidates.Add((pt.Value.X, pt.Value.Y, name));
            }

            if (candidates.Count == 0) { _log("[web] no suitable content link found."); return false; }

            // Prefer the longest link texts (headlines), with a little randomness among the top few.
            candidates.Sort((a, b) => b.Name.Length.CompareTo(a.Name.Length));
            var pick = candidates[_rng.Next(Math.Min(candidates.Count, 5))];
            _clicked.Add(pick.Name);

            string before = CurrentTitle(pids);
            _log($"[web] clicking link '{Trunc(pick.Name, 60)}' at ({pick.X},{pick.Y})");
            _mouse.MoveClick(pick.X, pick.Y);

            // Did it actually navigate? Wait for the title to CHANGE from before the click.
            string title = WaitForTitleDifferent(pids, before, 10000);
            if (IsCertWarning(title)) { DismissCertWarning(window, hwnd, pids); title = WaitForTitleDifferent(pids, before, 10000); }
            bool navigated = !string.IsNullOrWhiteSpace(title) && !string.Equals(title, before, StringComparison.Ordinal);
            if (navigated)
            {
                _log($"[web] link navigated -> '{Trunc(title, 80)}'");
                seenTitles.Add($"(link) {title}");
            }
            else
            {
                _log("[web] link did nothing (title unchanged) — moving on to another site.");
            }
            return navigated;
        }
        catch (ElementNotAvailableException) { }
        _log("[web] no clickable link found.");
        return false;
    }

    /// <summary>The browser window's current title right now (no waiting).</summary>
    private string CurrentTitle(List<int> pids)
    {
        string last = "";
        foreach (int pid in pids)
            foreach (var w in UiaHelpers.WindowsForPid(pid))
            {
                try { var n = w.Current.Name ?? ""; if (!string.IsNullOrWhiteSpace(n)) last = n; }
                catch { }
            }
        return last;
    }

    /// <summary>Wait until the title differs from <paramref name="before"/>; returns the new title, or `before` on timeout.</summary>
    private string WaitForTitleDifferent(List<int> pids, string before, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            string t = CurrentTitle(pids);
            if (!string.IsNullOrWhiteSpace(t) && !string.Equals(t, before, StringComparison.Ordinal)) return t;
            Thread.Sleep(400);
        }
        return before;
    }

    /// <summary>
    /// Dismiss the site's cookie/consent dialog (a centre-of-page DOM modal that otherwise intercepts
    /// our link clicks). Incognito, so we just accept-all — the most reliable way to clear it. UIA
    /// invoke (no mouse) so it can't miss the window.
    /// </summary>
    private void DismissCookieBanner(AutomationElement window, IntPtr hwnd)
    {
        // Search ONLY inside the web page (Document) — never the browser's own UI, or we'd be toggling
        // Chrome's cookie settings instead of clicking the website's consent dialog.
        var doc = UiaHelpers.FindByControlType(window, ControlType.Document, 2000);
        if (doc is null) return;

        string[] order =
        {
            "I accept all", "Accept all cookies", "Accept All Cookies", "Accept all", "Accept All",
            "I Accept", "I agree", "Agree and close", "Yes, I agree", "Allow all", "Accept",
            "Agree", "Got it", "Continue", "OK",
        };
        foreach (var label in order)
        {
            var btn = FindByNameContains(doc, new[] { label }, 500);
            if (btn is not null && UiaHelpers.TryInvoke(btn))
            {
                _log($"[web] accepted the site's cookie dialog via '{label}'.");
                Thread.Sleep(900); // let the modal close
                Focus(hwnd, window);
                return;
            }
        }
    }

    private static bool UrlLooksDeepWorthy(string url)
    {
        string[] deep = { "apnews", "bbc", "cnn", "arstechnica", "cnet", "amazon", "aliexpress", "booking", "bleacher" };
        return deep.Any(d => url.Contains(d, StringComparison.OrdinalIgnoreCase));
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? (s ?? "") : s[..n] + "…";

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
