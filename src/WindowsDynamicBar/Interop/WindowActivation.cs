using System.Runtime.InteropServices;

namespace WindowsDynamicBar.Interop;

/// <summary>
/// Bring another app's window to the front reliably. Windows only lets the foreground process
/// change the foreground window, so the standard technique is used: attach this thread's input
/// to the current foreground thread (and the target's) for the call; if that is still refused,
/// a synthetic Alt key press (which Windows treats as user input) unlocks SetForegroundWindow.
/// </summary>
internal static class WindowActivation
{
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    public static bool BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        else ShowWindow(hwnd, SW_SHOW);

        IntPtr fg = GetForegroundWindow();
        if (fg == hwnd) return true;

        uint self = GetCurrentThreadId();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint target = GetWindowThreadProcessId(hwnd, out _);
        bool attachedFg = fgThread != 0 && fgThread != self && AttachThreadInput(self, fgThread, true);
        bool attachedTarget = target != 0 && target != self && target != fgThread && AttachThreadInput(self, target, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(self, target, false);
            if (attachedFg) AttachThreadInput(self, fgThread, false);
        }

        if (GetForegroundWindow() != hwnd)
        {
            // Alt down/up counts as user input and lifts the foreground lock for this process.
            SendKeys(new[] { (VK_MENU, false), (VK_MENU, true) });
            SetForegroundWindow(hwnd);
        }
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>Ctrl+V into whatever window has the keyboard focus.</summary>
    public static void SendPaste() =>
        SendKeys(new[] { (VK_CONTROL, false), ((ushort)'V', false), ((ushort)'V', true), (VK_CONTROL, true) });

    private const ushort VK_MENU = 0x12;
    private const ushort VK_CONTROL = 0x11;

    private static void SendKeys((ushort Vk, bool Up)[] keys)
    {
        var inputs = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[i].type = 1; // INPUT_KEYBOARD
            inputs[i].U.ki.wVk = keys[i].Vk;
            inputs[i].U.ki.dwFlags = keys[i].Up ? 0x0002u : 0u; // KEYEVENTF_KEYUP
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
