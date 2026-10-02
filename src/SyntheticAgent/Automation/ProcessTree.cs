using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SyntheticAgent.Automation;

/// <summary>
/// Read-only inspection of the live process table via the Toolhelp snapshot API.
/// Used by the M1 launch-ancestry check and later by the self-audit's parent-process check.
/// No external dependencies, no WMI.
/// </summary>
public static class ProcessTree
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public readonly record struct Entry(int Pid, int ParentPid, string Name);

    /// <summary>Snapshot of every process: pid, parent pid, and image name (e.g. "notepad.exe").</summary>
    public static List<Entry> Snapshot()
    {
        var results = new List<Entry>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
            throw new InvalidOperationException("CreateToolhelp32Snapshot failed.");

        try
        {
            var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref pe))
                return results;

            do
            {
                results.Add(new Entry((int)pe.th32ProcessID, (int)pe.th32ParentProcessID, pe.szExeFile));
            } while (Process32NextW(snap, ref pe));
        }
        finally
        {
            CloseHandle(snap);
        }

        return results;
    }

    /// <summary>PIDs whose image name matches (case-insensitive, e.g. "notepad.exe").</summary>
    public static HashSet<int> PidsByName(string imageName) =>
        Snapshot().Where(e => string.Equals(e.Name, imageName, StringComparison.OrdinalIgnoreCase))
                  .Select(e => e.Pid).ToHashSet();

    /// <summary>Parent pid + parent image name for a given pid, or null if the pid is gone.</summary>
    public static (int ParentPid, string ParentName)? ParentOf(int pid)
    {
        var all = Snapshot();
        var self = all.FirstOrDefault(e => e.Pid == pid);
        if (self.Pid == 0 && self.Name is null) return null;
        var parent = all.FirstOrDefault(e => e.Pid == self.ParentPid);
        return (self.ParentPid, parent.Name ?? "<unknown>");
    }

    /// <summary>
    /// Safely terminate a pid ONLY if its current image name still matches the expected name
    /// (guards against PID reuse). Returns true if it killed exactly that process.
    /// Never kills by name — caller must pass a specific pid it recorded as test-started.
    /// </summary>
    public static bool KillIfMatches(int pid, string expectedImageName)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // ProcessName has no ".exe"; normalise both sides.
            string expected = expectedImageName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(p.ProcessName, expected, StringComparison.OrdinalIgnoreCase))
                return false;
            p.Kill();
            p.WaitForExit(5000);
            return true;
        }
        catch (ArgumentException)
        {
            return false; // process already gone
        }
    }
}
