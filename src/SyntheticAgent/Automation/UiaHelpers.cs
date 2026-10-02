using System.Windows.Automation;

namespace SyntheticAgent.Automation;

/// <summary>
/// Thin wrapper over UI Automation. It FINDS elements and returns click coordinates — it never clicks
/// (that's <see cref="Input.HumanMouse"/>'s job). This is the deliberate anti-GHOSTS choice: locate
/// controls via UIA, then deliver real SendInput at the returned point, so no automation driver or
/// injected automation flags appear in the telemetry.
/// </summary>
public static class UiaHelpers
{
    /// <summary>Top-level window AutomationElement for a process id (retries until it appears).</summary>
    public static AutomationElement? WindowForPid(int pid, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cond = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var el = AutomationElement.RootElement.FindFirst(TreeScope.Children, cond);
                if (el is not null) return el;
            }
            catch (ElementNotAvailableException) { /* transient during startup */ }
            Thread.Sleep(150);
        }
        return null;
    }

    /// <summary>Top-level window for a pid whose Name contains <paramref name="nameContains"/> (e.g. a dialog).</summary>
    public static AutomationElement? WindowForPidByName(int pid, string nameContains, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pidCond = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, pidCond);
                foreach (AutomationElement w in windows)
                {
                    string n = w.Current.Name ?? "";
                    if (n.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) return w;
                }
            }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(150);
        }
        return null;
    }

    /// <summary>All top-level window elements for a pid.</summary>
    public static List<AutomationElement> WindowsForPid(int pid)
    {
        var results = new List<AutomationElement>();
        try
        {
            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
            foreach (AutomationElement w in windows) results.Add(w);
        }
        catch (ElementNotAvailableException) { }
        return results;
    }

    /// <summary>Invoke an element via its UIA pattern (Invoke, else SelectionItem.Select). No coordinates.</summary>
    public static bool TryInvoke(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out object inv))
            {
                ((InvokePattern)inv).Invoke();
                return true;
            }
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object sel))
            {
                ((SelectionItemPattern)sel).Select();
                return true;
            }
        }
        catch (Exception) { /* pattern may be unavailable */ }
        return false;
    }

    /// <summary>All top-level windows (optionally filtered to a pid) as "name | pid" strings, for debugging.</summary>
    public static List<string> ListTopLevelWindows(int? pid = null)
    {
        var results = new List<string>();
        try
        {
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement w in windows)
            {
                try
                {
                    var c = w.Current;
                    if (pid is not null && c.ProcessId != pid) continue;
                    results.Add($"'{c.Name}' [{c.ControlType.ProgrammaticName.Replace("ControlType.", "")}] pid={c.ProcessId}");
                }
                catch (ElementNotAvailableException) { }
            }
        }
        catch (ElementNotAvailableException) { }
        return results;
    }

    public static AutomationElement? FindByName(AutomationElement root, string name,
                                                ControlType? type = null, int timeoutMs = 5000)
    {
        Condition cond = new PropertyCondition(AutomationElement.NameProperty, name);
        if (type is not null)
            cond = new AndCondition(cond, new PropertyCondition(AutomationElement.ControlTypeProperty, type));
        return FindFirstRetry(root, cond, timeoutMs);
    }

    public static AutomationElement? FindByAutomationId(AutomationElement root, string id, int timeoutMs = 5000) =>
        FindFirstRetry(root, new PropertyCondition(AutomationElement.AutomationIdProperty, id), timeoutMs);

    public static AutomationElement? FindByControlType(AutomationElement root, ControlType type, int timeoutMs = 5000) =>
        FindFirstRetry(root, new PropertyCondition(AutomationElement.ControlTypeProperty, type), timeoutMs);

    private static AutomationElement? FindFirstRetry(AutomationElement root, Condition cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var el = root.FindFirst(TreeScope.Descendants, cond);
                if (el is not null) return el;
            }
            catch (ElementNotAvailableException) { /* tree changed mid-search; retry */ }
            Thread.Sleep(150);
        }
        return null;
    }

    /// <summary>
    /// A point to click for an element: prefer the UIA "clickable point", fall back to the centre of
    /// the bounding rectangle. Returns null if the element is offscreen / has no usable geometry.
    /// </summary>
    public static (int X, int Y)? GetClickPoint(AutomationElement el)
    {
        try
        {
            if (el.TryGetClickablePoint(out System.Windows.Point p))
                return ((int)p.X, (int)p.Y);
        }
        catch (NoClickablePointException) { /* fall through to bounding rect */ }

        try
        {
            var r = el.Current.BoundingRectangle;
            if (r.IsEmpty || double.IsInfinity(r.X) || r.Width <= 0 || r.Height <= 0) return null;
            return ((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        }
        catch (ElementNotAvailableException) { return null; }
    }

    /// <summary>Set keyboard focus to an element via UIA (reliable regardless of coordinates).</summary>
    public static bool SetFocus(AutomationElement el)
    {
        try { el.SetFocus(); return true; }
        catch (Exception) { return false; }
    }

    /// <summary>Read an element's ValuePattern text (e.g. the Excel formula bar), or null.</summary>
    public static string? GetValue(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object v))
                return ((ValuePattern)v).Current.Value;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Convenience describe for logging/debugging a found element.</summary>
    public static string Describe(AutomationElement el)
    {
        try
        {
            var c = el.Current;
            return $"name='{c.Name}' type={c.ControlType.ProgrammaticName} autoId='{c.AutomationId}'";
        }
        catch (ElementNotAvailableException) { return "<unavailable>"; }
    }
}
