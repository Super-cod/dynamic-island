using System;
using System.Windows.Media;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.Core.Theme;

/// <summary>
/// Applies Dark / Light / Translucent palettes to the shared application brushes.
/// All consumers bind via DynamicResource, so a theme change repaints live.
/// The Background-opacity slider multiplies the base alpha of the surface color.
/// </summary>
public static class ThemeService
{
    public static void Apply(AppTheme theme, double opacity)
    {
        try
        {
            var res = System.Windows.Application.Current?.Resources;
            if (res is null) return;

            // Base surface RGB per theme; alpha = user opacity (Translucent is glassier).
            (byte R, byte G, byte B) surface = theme switch
            {
                AppTheme.Light => (0xF5, 0xF5, 0xF7),
                AppTheme.Translucent => (0x0A, 0x0A, 0x0C),
                _ => (0x14, 0x14, 0x17),
            };
            double mult = theme == AppTheme.Translucent ? 0.55 : 1.0;
            byte a = (byte)Math.Clamp((int)Math.Round(opacity * 255 * mult), 40, 255);
            byte aElevated = (byte)Math.Min(255, a + 12);

            Set(res, "WdbBgBrush", Color.FromArgb(a, surface.R, surface.G, surface.B));
            Set(res, "WdbBgElevatedBrush", Color.FromArgb(aElevated, surface.R, surface.G, surface.B));

            if (theme == AppTheme.Light)
            {
                Set(res, "WdbTextBrush", Color.FromRgb(0x1C, 0x1C, 0x1E));
                Set(res, "WdbTextMutedBrush", Color.FromArgb(0xB3, 0x1C, 0x1C, 0x1E));
                Set(res, "WdbHairlineBrush", Color.FromArgb(0x28, 0x00, 0x00, 0x00));
                Set(res, "WdbDimBrush", Color.FromArgb(0x14, 0x00, 0x00, 0x00));
                Set(res, "WdbTrackBrush", Color.FromArgb(0x28, 0x00, 0x00, 0x00));
            }
            else
            {
                Set(res, "WdbTextBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
                Set(res, "WdbTextMutedBrush", Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
                Set(res, "WdbHairlineBrush", Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
                Set(res, "WdbDimBrush", Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
                Set(res, "WdbTrackBrush", Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            }

            Log.Debug("Theme applied", ("theme", theme.ToString()), ("alpha", a));
        }
        catch (Exception ex) { Log.Error("Theme apply failed", ex); }
    }

    private static void Set(System.Windows.ResourceDictionary res, string key, Color color)
    {
        // Brushes compiled from App.xaml are frozen — mutate would throw
        // "read-only state". Replace the entry; DynamicResource consumers re-resolve.
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }
}
