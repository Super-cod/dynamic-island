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
    private static bool _available = true;

    public static void Attach(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>Update the blurred region to a pill at (leftPx, topPx) of given size (device pixels).</summary>
    public static void UpdateRegion(int leftPx, int topPx, int widthPx, int heightPx, int radiusPx, bool enabled)
    {
        if (!_available || _hwnd == IntPtr.Zero) return;
        try
        {
            // Recreate region
            if (_currentRegion != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(_currentRegion);
                _currentRegion = IntPtr.Zero;
            }

            var bb = new NativeMethods.DWM_BLURBEHIND { dwFlags = NativeMethods.DWM_BB_ENABLE };
            if (!enabled)
            {
                bb.fEnable = false;
                NativeMethods.DwmEnableBlurBehindWindow(_hwnd, ref bb);
                return;
            }

            int w = Math.Max(4, widthPx), h = Math.Max(4, heightPx);
            int r = Math.Clamp(radiusPx, 2, Math.Min(w, h) / 2);
            // Inset 1px so GDI region aliasing hides under the pill border stroke.
            _currentRegion = NativeMethods.CreateRoundRectRgn(
                leftPx + 1, topPx + 1, leftPx + w - 1, topPx + h - 1, r * 2, r * 2);
            bb.fEnable = true;
            bb.hRgnBlur = _currentRegion;
            bb.fTransitionOnMaximized = false;
            int hr = NativeMethods.DwmEnableBlurBehindWindow(_hwnd, ref bb);
            if (hr != 0)
            {
                Log.Debug("DwmEnableBlurBehindWindow returned {hr}", ("hr", hr));
                // Non-fatal: pill still renders with translucent fill.
            }
        }
        catch (Exception ex)
        {
            _available = false;
            Log.Warning("Blur unavailable, continuing without", ("error", ex.Message));
        }
    }

    public static void Disable()
    {
        if (_hwnd == IntPtr.Zero) return;
        var bb = new NativeMethods.DWM_BLURBEHIND { dwFlags = NativeMethods.DWM_BB_ENABLE, fEnable = false };
        NativeMethods.DwmEnableBlurBehindWindow(_hwnd, ref bb);
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
