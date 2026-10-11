using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Automation;
using SyntheticAgent.Automation;
using SyntheticAgent.Input;
using SyntheticAgent.Timing;

namespace SyntheticAgent.Handlers;

/// <summary>
/// Locates the OpenOffice launcher (soffice.exe). OpenOffice 4.x is 32-bit, so it is normally under
/// "Program Files (x86)". soffice.exe is a thin launcher that starts the real process, soffice.bin —
/// which is what owns the window and what we track/clean up.
/// </summary>
public static class OpenOfficeLauncher
{
    public const string ProcessImage = "soffice.bin";

    public static string? FindSoffice()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            string p = Path.Combine(root, "OpenOffice 4", "program", "soffice.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}

/// <summary>
/// Minimal, valid empty ODF documents (.odt / .ods). Same rationale as the OOXML generators: the agent
/// OPENS an existing native file, so Ctrl+S saves silently to that path — no Save As dialog and no
/// "Keep Current Format?" prompt (which only appears for foreign formats like .docx/.xlsx).
/// The `mimetype` entry must be first and STORED (uncompressed) per the ODF package rules.
/// </summary>
public static class MinimalOdf
{
    private const string TextMime = "application/vnd.oasis.opendocument.text";
    private const string SheetMime = "application/vnd.oasis.opendocument.spreadsheet";

    // Writer REQUIRES a page-layout + master-page in styles.xml or it refuses the file (shows the Start
    // Center). Calc tolerates an empty styles.xml. These are trimmed from a real OpenOffice document.
    private const string TextStyles =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <office:document-styles xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" office:version="1.2"><office:styles><style:default-style style:family="paragraph"/><style:style style:name="Standard" style:family="paragraph" style:class="text"/></office:styles><office:automatic-styles><style:page-layout style:name="pm1"><style:page-layout-properties fo:page-width="20.999cm" fo:page-height="29.699cm" fo:margin-top="2cm" fo:margin-bottom="2cm" fo:margin-left="2cm" fo:margin-right="2cm"/></style:page-layout></office:automatic-styles><office:master-styles><style:master-page style:name="Standard" style:page-layout-name="pm1"/></office:master-styles></office:document-styles>
        """;

    private const string SheetStyles =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <office:document-styles xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" office:version="1.2"><office:styles/><office:automatic-styles/><office:master-styles/></office:document-styles>
        """;

    public static void EnsureOdt(string path, bool overwrite = true) =>
        Create(path, TextMime,
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" office:version="1.2"><office:scripts/><office:font-face-decls/><office:automatic-styles/><office:body><office:text><text:p text:style-name="Standard"/></office:text></office:body></office:document-content>
            """, TextStyles, overwrite);

    public static void EnsureOds(string path, bool overwrite = true) =>
        Create(path, SheetMime,
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" office:version="1.2"><office:scripts/><office:font-face-decls/><office:automatic-styles/><office:body><office:spreadsheet><table:table table:name="Sheet1"><table:table-row><table:table-cell/></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>
            """, SheetStyles, overwrite);

    private static void Create(string path, string mime, string contentXml, string stylesXml, bool overwrite)
    {
        if (File.Exists(path) && !overwrite) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);

        using var fs = new FileStream(path, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        // mimetype: FIRST entry, stored (uncompressed), exact bytes, no trailing newline.
        var mimeEntry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var s = mimeEntry.Open()) { var b = Encoding.ASCII.GetBytes(mime); s.Write(b, 0, b.Length); }

        Add(zip, "META-INF/manifest.xml",
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.2"><manifest:file-entry manifest:full-path="/" manifest:media-type="{mime}"/><manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/><manifest:file-entry manifest:full-path="styles.xml" manifest:media-type="text/xml"/></manifest:manifest>
            """);

        Add(zip, "content.xml", contentXml);
        Add(zip, "styles.xml", stylesXml);
    }

    private static void Add(ZipArchive zip, string entry, string content)
    {
        var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content.Trim());
    }
}

/// <summary>Shared helpers for driving an OpenOffice window by keyboard (its UIA control tree is unreliable).</summary>
internal static class OoCommon
{
    /// <summary>
    /// Wait for the real document window (title contains <paramref name="appTitle"/>, e.g. "OpenOffice
    /// Writer") and bring it to the foreground, retrying find+focus together so we skip the splash
    /// screen and win any activation race. Returns the focused HWND, or 0 if it never focuses.
    /// </summary>
    public static IntPtr OpenAndFocus(int pid, string appTitle, int timeoutMs, Action<string> log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (var w in UiaHelpers.WindowsForPid(pid))
            {
                string name; IntPtr h;
                try { name = w.Current.Name ?? ""; h = new IntPtr(w.Current.NativeWindowHandle); }
                catch { continue; }
                if (h == IntPtr.Zero || !name.Contains(appTitle, StringComparison.OrdinalIgnoreCase)) continue;

                AppLauncher.BringToForeground(h);
                Thread.Sleep(500);
                if (NativeInput.ForegroundWindow() == h) { log($"  window focused: '{name}'"); return h; }
            }
            Thread.Sleep(500);
        }
        return IntPtr.Zero;
    }

    /// <summary>Poll until the saved file's modified time advances past <paramref name="before"/>.</summary>
    public static bool WaitForSave(string path, DateTime before)
    {
        for (int i = 0; i < 15; i++)
        {
            Thread.Sleep(400);
            try { if (File.GetLastWriteTimeUtc(path) > before) return true; } catch { }
        }
        return false;
    }
}

/// <summary>Office-worker activity on OpenOffice Writer: open a blank .odt, type a memo, Ctrl+S, close.</summary>
public sealed class OoWriterHandler
{
    public const string Memo =
        "Team update - 2 October\n\n" +
        "Quick note: the Q3 numbers are in and they look solid. " +
        "I'll circulate the full deck tomorrow morning; shout if you need anything sooner.\n\n" +
        "Thanks,\nSam";

    private readonly DelayModel _timing;
    private readonly Action<string> _log;

    public OoWriterHandler(DelayModel timing, Action<string>? log = null)
    {
        _timing = timing;
        _log = log ?? Console.WriteLine;
    }

    public (int? Pid, bool Ok) Run(string sofficePath, string savePath)
    {
        MinimalOdf.EnsureOdt(savePath); // valid blank .odt (with page master) -> opens + Ctrl+S saves silently
        DateTime mtimeBefore = File.GetLastWriteTimeUtc(savePath);
        _log($"[oo-writer] prepared blank document: {savePath}");

        int? pid = AppLauncher.LaunchViaShell(sofficePath, OpenOfficeLauncher.ProcessImage, args: $"-norestore \"{savePath}\"", timeoutMs: 40000);
        if (pid is null) { _log("[oo-writer] FAIL: OpenOffice did not start a new process (quickstarter resident?)."); return (null, false); }
        _log($"[oo-writer] launched soffice.bin pid {pid}");

        IntPtr hwnd = OoCommon.OpenAndFocus(pid.Value, "OpenOffice Writer", 40000, _log);
        if (hwnd == IntPtr.Zero) { _log("[oo-writer] FAIL: could not open/focus the Writer window."); return (pid, false); }
        Thread.Sleep(1000); // settle before typing

        var typist = new HumanTyping(_timing, () => NativeInput.ForegroundWindow() == hwnd);
        try { typist.TypeText(Memo); }
        catch (FocusLostException) { _log("[oo-writer] ABORT: focus left mid-type."); return (pid, false); }

        Thread.Sleep(500);
        NativeInput.KeyCombo(Vk.Control, Vk.S); // native .odt -> silent overwrite to the same path
        bool saved = OoCommon.WaitForSave(savePath, mtimeBefore);
        _log(saved ? $"[oo-writer] saved to {savePath}" : "[oo-writer] WARN: save not confirmed via mtime.");

        NativeInput.KeyCombo(Vk.Menu, Vk.F4);
        Thread.Sleep(1500);
        return (pid, saved);
    }
}

/// <summary>Office-worker activity on OpenOffice Calc: open a blank .ods, enter a table + SUM, Ctrl+S, close.</summary>
public sealed class OoCalcHandler
{
    private readonly DelayModel _timing;
    private readonly Action<string> _log;
    private readonly Random _rng;

    public OoCalcHandler(DelayModel timing, Action<string>? log = null)
    {
        _timing = timing;
        _log = log ?? Console.WriteLine;
        _rng = new Random(unchecked(timing.Seed ^ 0x0ca1c));
    }

    public (int? Pid, bool Ok) Run(string sofficePath, string savePath)
    {
        MinimalOdf.EnsureOds(savePath);
        DateTime mtimeBefore = File.GetLastWriteTimeUtc(savePath);
        _log($"[oo-calc] prepared blank spreadsheet: {savePath}");

        int? pid = AppLauncher.LaunchViaShell(sofficePath, OpenOfficeLauncher.ProcessImage, args: $"-norestore \"{savePath}\"", timeoutMs: 40000);
        if (pid is null) { _log("[oo-calc] FAIL: OpenOffice did not start a new process (quickstarter resident?)."); return (null, false); }
        _log($"[oo-calc] launched soffice.bin pid {pid}");

        IntPtr hwnd = OoCommon.OpenAndFocus(pid.Value, "OpenOffice Calc", 40000, _log);
        if (hwnd == IntPtr.Zero) { _log("[oo-calc] FAIL: could not open/focus the Calc window."); return (pid, false); }
        Thread.Sleep(1000); // settle before typing

        // A1 is active on open. Text rows move cell-by-cell with ARROW keys (like a person); the formula
        // row uses Tab/Enter (arrows get absorbed into a formula as cell references).
        var typist = new HumanTyping(_timing, () => NativeInput.ForegroundWindow() == hwnd);
        try
        {
            EnterRow(typist, "Weekly Produce Inventory");          // A1 title
            EnterRow(typist, "Item", "Qty", "Price");              // header
            EnterRow(typist, "Apples", "10", "0.50");
            EnterRow(typist, "Oranges", "5", "0.75");
            EnterRow(typist, "Bananas", "12", "0.30");
            EnterRow(typist, "Grapes", "8", "1.20");
            EnterRow(typist, "Total", "=SUM(B3:B6)", "=SUM(C3:C6)"); // Qty + Price totals
        }
        catch (FocusLostException) { _log("[oo-calc] ABORT: focus left mid-entry."); return (pid, false); }

        Thread.Sleep(500);
        NativeInput.KeyCombo(Vk.Control, Vk.S); // native .ods -> silent save
        bool saved = OoCommon.WaitForSave(savePath, mtimeBefore);
        _log(saved ? $"[oo-calc] saved to {savePath}" : "[oo-calc] WARN: save not confirmed via mtime.");

        NativeInput.KeyCombo(Vk.Menu, Vk.F4);
        Thread.Sleep(1500);
        return (pid, saved);
    }

    // Enter one row of N cells starting at column A, then land on column A of the NEXT row.
    // Text-only rows move with ARROW keys (human-like). A row containing a FORMULA uses Tab between
    // cells and Enter at the end, because arrow keys inside a formula are absorbed as cell references
    // (which produced "=SUM(...)[.A5]" -> Err:509). Formulas must sit in the last column(s).
    private void EnterRow(HumanTyping typist, params string[] cells)
    {
        bool hasFormula = cells.Any(c => c.StartsWith("="));
        for (int c = 0; c < cells.Length; c++)
        {
            _pause();
            typist.TypeText(cells[c]);
            Thread.Sleep(120 + _rng.Next(180));
            bool last = c == cells.Length - 1;

            if (hasFormula)
            {
                NativeInput.KeyPress(last ? Vk.Return : Vk.Tab); // Tab/Enter are formula-safe; Enter returns to the row's start column
            }
            else if (!last)
            {
                NativeInput.KeyPress(Vk.Right);                  // next column (arrow, human-like)
            }
            else
            {
                NativeInput.KeyPress(Vk.Down);                   // drop to next row
                for (int k = 0; k < cells.Length - 1; k++)       // walk back to column A
                {
                    Thread.Sleep(60 + _rng.Next(90));
                    NativeInput.KeyPress(Vk.Left);
                }
            }
        }
    }

    private void _pause() => Thread.Sleep((int)Math.Min(2500, _timing.NextActionDelayMs()));
}
