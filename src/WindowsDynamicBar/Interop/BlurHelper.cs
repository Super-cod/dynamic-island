using System.Runtime.InteropServices;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Interop;

/// <summary>
/// Applies a per-pill blurred backdrop behind the bar window.
/// Strategy: DwmEnableBlurBehindWindow with a rounded-rect region so blur is clipped
/// to the pill shape (not the whole window rect). Falls back gracefully:
///  - If the DWM call fails, the pill keeps its semi-transparent fill (still looks correct).
///  - The region is slightly inset so aliased region edges hide under the pill's opaque border.
/// Works on Windows 10 1809+ and Windows 11 (DWM blur-behind remains supported).
/// </summary>
public static class BlurHelper
{
    private static IntPtr _currentRegion = IntPtr.Zero;
    private static IntPtr _hwnd = IntPtr.Zero;

    public static void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        Disable();
    }

    /// <summary>
    /// On Windows 10 and 11, DwmEnableBlurBehindWindow is deprecated and creates an aliased dark
    /// translucent box artifact with GDI region borders behind transparent layered windows.
    /// Blur is disabled so the island floats cleanly with true alpha transparency.
    /// </summary>
    public static void UpdateRegion(int leftPx, int topPx, int widthPx, int heightPx, int radiusPx, bool enabled)
    {
        Disable();
    }

    public static void Disable()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            if (_currentRegion != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(_currentRegion);
                _currentRegion = IntPtr.Zero;
            }
            var bb = new NativeMethods.DWM_BLURBEHIND { dwFlags = NativeMethods.DWM_BB_ENABLE, fEnable = false };
            NativeMethods.DwmEnableBlurBehindWindow(_hwnd, ref bb);
        }
        catch { }
    }

    public static void Detach()
    {
        if (_currentRegion != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_currentRegion);
            _currentRegion = IntPtr.Zero;
        }
        _hwnd = IntPtr.Zero;
    }
}
