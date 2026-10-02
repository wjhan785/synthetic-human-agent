using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SyntheticAgent.Automation;

/// <summary>
/// Launches apps the way a human's double-click does: by asking the already-running explorer.exe
/// to perform the launch, so the new process parents under explorer.exe with NO process-tree link
/// to our agent. This is the core of the process-authenticity story.
///
/// Mechanism (the documented "ExecInExplorer" technique): the ShellWindows COM singleton is a proxy
/// to the object living inside explorer.exe. We walk Desktop shell view -> IShellFolderViewDual ->
/// Application (IShellDispatch2) -> ShellExecute. Because that dispatch object lives in explorer.exe,
/// explorer performs the CreateProcess, so explorer becomes the parent.
///
/// Dispatch interfaces (IShellWindows / IShellFolderViewDual / IShellDispatch2) are declared with
/// only the members we use; the CLR invokes them by name via IDispatch, so vtable order is irrelevant.
/// IServiceProvider / IShellBrowser / IShellView are IUnknown-based, so every method is declared in
/// exact vtable order (unused slots are placeholders occupying their slot).
/// </summary>
public static class AppLauncher
{
    private const int SWC_DESKTOP = 0x08;
    private const int SWFO_NEEDDISPATCH = 0x01;
    private const uint SVGIO_BACKGROUND = 0x00000000;

    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");

    /// <summary>
    /// Launch via explorer. Returns the PID of the newly created process named
    /// <paramref name="expectedImageName"/> (e.g. "notepad.exe"), found by diffing the process table
    /// before/after, or null if none appeared within the timeout.
    /// </summary>
    public static int? LaunchViaShell(string file, string expectedImageName,
                                      string args = "", string workingDir = "",
                                      int timeoutMs = 10000)
    {
        var before = ProcessTree.PidsByName(expectedImageName);

        ExplorerShellExecute(file, args, workingDir);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var after = ProcessTree.PidsByName(expectedImageName);
            after.ExceptWith(before);
            if (after.Count > 0)
            {
                int pid = after.Min();
                Control.LaunchedProcesses.Register(pid, expectedImageName);
                return pid;
            }
            Thread.Sleep(100);
        }
        return null;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    /// <summary>
    /// Wait for the given PID's main window to exist and become ready. Returns its HWND, or
    /// IntPtr.Zero on timeout.
    /// </summary>
    public static IntPtr WaitForWindow(int pid, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Refresh();
                IntPtr h = p.MainWindowHandle;
                if (h != IntPtr.Zero && IsWindowVisible(h))
                    return h;
            }
            catch (ArgumentException)
            {
                return IntPtr.Zero; // process gone
            }
            Thread.Sleep(150);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Bring a window to the foreground reliably. Plain SetForegroundWindow is blocked by Windows'
    /// foreground-lock rules, so we attach our input thread to the current foreground thread first
    /// (the standard technique), then restore + raise + activate. Returns true once it holds focus.
    /// </summary>
    public static bool BringToForeground(IntPtr hwnd, int attempts = 8)
    {
        for (int i = 0; i < attempts; i++)
        {
            IntPtr fg = Input.NativeInput.ForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint cur = GetCurrentThreadId();

            bool attached = fgThread != 0 && fgThread != cur && AttachThreadInput(cur, fgThread, true);
            try
            {
                ShowWindow(hwnd, SW_RESTORE);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(cur, fgThread, false);
            }

            Thread.Sleep(150);
            if (Input.NativeInput.ForegroundWindow() == hwnd) return true;
        }
        return Input.NativeInput.ForegroundWindow() == hwnd;
    }

    private static void ExplorerShellExecute(string file, string args, string workingDir)
    {
        object? shellWindowsObj = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows)!);
        var shellWindows = (IShellWindows)shellWindowsObj!;

        object loc = null!;        // VT_EMPTY — ignored for SWC_DESKTOP
        object locRoot = null!;
        object desktopDisp = shellWindows.FindWindowSW(ref loc, ref locRoot,
                                                       SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);

        var sp = (IServiceProvider)desktopDisp;
        Guid sid = SID_STopLevelBrowser, iid = IID_IShellBrowser;
        object browserObj = sp.QueryService(ref sid, ref iid);
        var shellBrowser = (IShellBrowser)browserObj;

        shellBrowser.QueryActiveShellView(out IShellView shellView);

        Guid dispIid = IID_IDispatch;
        shellView.GetItemObject(SVGIO_BACKGROUND, ref dispIid, out object bgObj);

        var folderView = (IShellFolderViewDual)bgObj;
        var shellDispatch = (IShellDispatch2)folderView.Application;

        // Late-bound IDispatch marshals the string as a BSTR VARIANT; empty strings => VT_BSTR "".
        shellDispatch.ShellExecute(file, args, workingDir, "", 1 /* SW_SHOWNORMAL */);

        Marshal.ReleaseComObject(shellWindowsObj!);
    }

    // ---- Dispatch interfaces: declare only what we call; CLR invokes by name. ----

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"),
     InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(
            [MarshalAs(UnmanagedType.Struct)] ref object pvarLoc,
            [MarshalAs(UnmanagedType.Struct)] ref object pvarLocRoot,
            int swClass, out int pHWND, int swfwOptions);
    }

    [ComImport, Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85"),
     InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellFolderViewDual
    {
        object Application { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    }

    [ComImport, Guid("A4C6892C-3BA9-11D2-9DEA-00C04FB16162"),
     InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellDispatch2
    {
        void ShellExecute(
            [MarshalAs(UnmanagedType.BStr)] string File,
            [Optional, MarshalAs(UnmanagedType.Struct)] object vArgs,
            [Optional, MarshalAs(UnmanagedType.Struct)] object vDir,
            [Optional, MarshalAs(UnmanagedType.Struct)] object vOperation,
            [Optional, MarshalAs(UnmanagedType.Struct)] object vShow);
    }

    // ---- IUnknown-based interfaces: full vtable order; unused slots are placeholders. ----

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid guidService, ref Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB(IntPtr pszStatusText);
        void EnableModelessSB(bool fEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, IntPtr ppStrm);
        void GetControlWindow(uint id, out IntPtr lphwnd);
        void SendControlMsg(uint id, uint uMsg, uint wParam, uint lParam, IntPtr pret);
        void QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out IShellView ppshv);
        void OnViewWindowActive([MarshalAs(UnmanagedType.Interface)] IShellView ppshv);
        void SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void TranslateAccelerator(IntPtr pmsg);
        void EnableModeless(bool fEnable);
        void UIActivate(uint uState);
        void Refresh();
        void CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr pfs);
        void AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
        void SaveViewState();
        void SelectItem(IntPtr pidlItem, uint uFlags);
        void GetItemObject(uint uItem, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }
}
