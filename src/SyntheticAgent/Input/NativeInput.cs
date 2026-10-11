using System.Runtime.InteropServices;

namespace SyntheticAgent.Input;

/// <summary>
/// Thin P/Invoke layer over user32 SendInput. Dumb primitives only — no timing, no human modelling
/// (that lives in HumanTyping/HumanMouse on top of this). Everything goes through the real input
/// queue, so downstream telemetry is genuine by construction.
/// </summary>
public static class NativeInput
{
    // --- INPUT union plumbing (x64 layout: INPUT = 40 bytes, union = 32). ---

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    // Virtual-screen metrics for absolute-coordinate normalisation.
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool KeyHeld(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>True while the emergency-stop chord Ctrl+Alt+Shift+Q is held.</summary>
    public static bool EmergencyStopHeld() =>
        KeyHeld(Vk.Control) && KeyHeld(Vk.Menu) && KeyHeld(Vk.Shift) && KeyHeld(Vk.Q);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>Current cursor position in screen pixels.</summary>
    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out POINT p);
        return (p.X, p.Y);
    }

    /// <summary>Virtual-screen rectangle (x, y, width, height) spanning all monitors.</summary>
    public static (int X, int Y, int W, int H) VirtualScreen() =>
        (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
         GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    private static void Send(params INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent != inputs.Length)
            throw new InvalidOperationException(
                $"SendInput injected {sent}/{inputs.Length} events (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    // --- Keyboard ---

    /// <summary>Type one Unicode character (layout-independent) as a down+up pair.</summary>
    public static void UnicodeChar(char ch)
    {
        Send(
            KeyboardInput(0, ch, KEYEVENTF_UNICODE),
            KeyboardInput(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
    }

    public static void KeyDown(ushort vk) => Send(KeyboardInput(vk, 0, 0));
    public static void KeyUp(ushort vk) => Send(KeyboardInput(vk, 0, KEYEVENTF_KEYUP));

    /// <summary>Press and release a virtual-key (e.g. Enter, Backspace).</summary>
    public static void KeyPress(ushort vk)
    {
        KeyDown(vk);
        KeyUp(vk);
    }

    /// <summary>Press <paramref name="vk"/> while <paramref name="modifier"/> is held (e.g. Ctrl+A).</summary>
    public static void KeyCombo(ushort modifier, ushort vk)
    {
        KeyDown(modifier);
        KeyPress(vk);
        KeyUp(modifier);
    }

    private static INPUT KeyboardInput(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = scan,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = UIntPtr.Zero,
            }
        }
    };

    // --- Mouse ---

    /// <summary>Move the cursor to an absolute screen pixel, normalised across the virtual desktop.</summary>
    public static void MouseMoveAbsolute(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        // Normalise to 0..65535 across the virtual-screen rectangle.
        int nx = (int)((x - vx) * 65535.0 / Math.Max(1, vw - 1));
        int ny = (int)((y - vy) * 65535.0 / Math.Max(1, vh - 1));
        Send(MouseInput(nx, ny, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK));
    }

    public static void LeftButton(bool down) =>
        Send(MouseInput(0, 0, 0, down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP));

    public static void RightButton(bool down) =>
        Send(MouseInput(0, 0, 0, down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP));

    /// <summary>Scroll the wheel. One notch = 120; positive = up, negative = down.</summary>
    public static void Scroll(int delta) =>
        Send(MouseInput(0, 0, (uint)delta, MOUSEEVENTF_WHEEL));

    private static INPUT MouseInput(int dx, int dy, uint mouseData, uint flags) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion
        {
            mi = new MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = mouseData,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = UIntPtr.Zero,
            }
        }
    };

    // --- Safety ---

    /// <summary>Handle of the window currently receiving input. Used by the foreground guard.</summary>
    public static IntPtr ForegroundWindow() => GetForegroundWindow();
}

/// <summary>Virtual-key codes used by the agent.</summary>
public static class Vk
{
    public const ushort Back = 0x08;
    public const ushort Tab = 0x09;
    public const ushort Return = 0x0D;
    public const ushort Shift = 0x10;
    public const ushort Control = 0x11;
    public const ushort Menu = 0x12; // Alt
    public const ushort Escape = 0x1B;
    public const ushort Space = 0x20;
    public const ushort Home = 0x24;
    public const ushort Left = 0x25;
    public const ushort Up = 0x26;
    public const ushort Right = 0x27;
    public const ushort Down = 0x28;
    public const ushort Delete = 0x2E;
    public const ushort LWin = 0x5B;
    public const ushort A = 0x41;
    public const ushort C = 0x43;
    public const ushort Q = 0x51;
    public const ushort D = 0x44;
    public const ushort L = 0x4C;
    public const ushort S = 0x53;
    public const ushort W = 0x57;
    public const ushort F4 = 0x73;
    public const ushort F12 = 0x7B;
}
