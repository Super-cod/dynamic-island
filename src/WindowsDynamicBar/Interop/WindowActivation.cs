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
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, SW_RESTORE);
        else NativeMethods.ShowWindow(hwnd, SW_SHOW);

        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == hwnd) return true;

        uint self = NativeMethods.GetCurrentThreadId();
        uint fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
        uint target = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        bool attachedFg = fgThread != 0 && fgThread != self && NativeMethods.AttachThreadInput(self, fgThread, true);
        bool attachedTarget = target != 0 && target != self && target != fgThread && NativeMethods.AttachThreadInput(self, target, true);
        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedTarget) NativeMethods.AttachThreadInput(self, target, false);
            if (attachedFg) NativeMethods.AttachThreadInput(self, fgThread, false);
        }

        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            // Alt down/up counts as user input and lifts the foreground lock for this process.
            SendKeys(new[] { (VK_MENU, false), (VK_MENU, true) });
            NativeMethods.SetForegroundWindow(hwnd);
        }
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    /// <summary>Ctrl+V into whatever window has the keyboard focus.</summary>
    public static void SendPaste() =>
        SendKeys(new[] { (VK_CONTROL, false), ((ushort)'V', false), ((ushort)'V', true), (VK_CONTROL, true) });

    private const ushort VK_MENU = 0x12;
    private const ushort VK_CONTROL = 0x11;

    private static void SendKeys((ushort Vk, bool Up)[] keys)
    {
        var inputs = new NativeMethods.INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[i].type = 1; // INPUT_KEYBOARD
            inputs[i].U.ki.wVk = keys[i].Vk;
            inputs[i].U.ki.dwFlags = keys[i].Up ? 0x0002u : 0u; // KEYEVENTF_KEYUP
        }
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
