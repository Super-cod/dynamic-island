using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Core.Media;

public sealed record AppIdentity(
    string DisplayName,
    BitmapSource? Icon,
    byte[]? IconBytes,
    Color DominantColor);

public static class AppIconHelper
{
    private static readonly ConcurrentDictionary<string, AppIdentity> _identityCache = new(StringComparer.OrdinalIgnoreCase);

    public static AppIdentity ResolveAppIdentity(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
            return new AppIdentity("Music", null, null, Color.FromRgb(0x0A, 0x84, 0xFF));

        if (_identityCache.TryGetValue(aumid, out var cached))
            return cached;

        string displayName = CleanDisplayName(aumid);
        BitmapSource? icon = null;
        byte[]? iconBytes = null;
        Color dominant = Color.FromRgb(0x0A, 0x84, 0xFF);

        try
        {
            // 1. Try Shell AppsFolder via IShellItemImageFactory (handles both packaged AUMIDs and registered Win32)
            icon = TryGetShellAppIcon(aumid, 64);

            // 2. If failed, try resolving via running process or executable path
            if (icon is null)
            {
                string? exePath = FindExePath(aumid);
                if (exePath is not null && File.Exists(exePath))
                {
                    try
                    {
                        var info = FileVersionInfo.GetVersionInfo(exePath);
                        if (!string.IsNullOrWhiteSpace(info.FileDescription))
                            displayName = info.FileDescription;
                        else if (!string.IsNullOrWhiteSpace(info.ProductName))
                            displayName = info.ProductName;
                    }
                    catch { }

                    using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (ico is not null)
                    {
                        using var bmp = ico.ToBitmap();
                        icon = BitmapToBitmapSource(bmp);
                    }
                }
            }

            if (icon is not null)
            {
                iconBytes = BitmapSourceToBytes(icon);
                dominant = ExtractDominantColor(icon);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("App identity lookup failed", ("aumid", aumid), ("error", ex.Message));
        }

        var identity = new AppIdentity(displayName, icon, iconBytes, dominant);
        _identityCache[aumid] = identity;
        return identity;
    }

    private static string CleanDisplayName(string aumid)
    {
        // SpotifyAB.SpotifyMusic_... -> Spotify
        if (aumid.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (aumid.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (aumid.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (aumid.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "Brave";
        if (aumid.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (aumid.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase)) return "Media Player";
        if (aumid.Contains("iTunes", StringComparison.OrdinalIgnoreCase)) return "iTunes";
        if (aumid.Contains("VLC", StringComparison.OrdinalIgnoreCase)) return "VLC";

        string name = aumid;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name);

        int excl = name.IndexOf('!');
        if (excl > 0) name = name[(excl + 1)..];

        int under = name.IndexOf('_');
        if (under > 0 && under < name.Length - 1)
        {
            // like SpotifyAB.SpotifyMusic_zpdnekdrzrea0 -> take before underscore
            string prefix = name[..under];
            int dot = prefix.LastIndexOf('.');
            name = dot >= 0 ? prefix[(dot + 1)..] : prefix;
        }

        return string.IsNullOrWhiteSpace(name) ? "Music" : name;
    }

    private static string? FindExePath(string aumid)
    {
        if (File.Exists(aumid)) return aumid;

        string name = Path.GetFileNameWithoutExtension(aumid);

        // Check running processes
        try
        {
            var procs = Process.GetProcessesByName(name);
            foreach (var p in procs)
            {
                try
                {
                    string? fn = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(fn) && File.Exists(fn))
                        return fn;
                }
                catch { }
            }
        }
        catch { }

        // Check App Paths in registry
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name + ".exe");
            if (key?.GetValue("") is string path && File.Exists(path))
                return path;
        }
        catch { }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name + ".exe");
            if (key?.GetValue("") is string path && File.Exists(path))
                return path;
        }
        catch { }

        return null;
    }

    private static BitmapSource? TryGetShellAppIcon(string aumid, int sizePx)
    {
        try
        {
            string parsingName = @"shell:AppsFolder\" + aumid;
            var guid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, guid, out object factoryObj);
            if (hr != 0 || factoryObj is not IShellItemImageFactory factory) return null;

            hr = factory.GetImage(new SIZE { cx = sizePx, cy = sizePx }, SIIGBF_RESIZETOFIT | SIIGBF_ICONONLY, out IntPtr hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource CreateGradientDisc(Color dominant, int size = 122)
    {
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            // Dark base
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)), null, new Rect(0, 0, size, size));

            // Radial gradient disc from dominant color to deep charcoal
            var grad = new RadialGradientBrush
            {
                Center = new Point(0.35, 0.35),
                GradientOrigin = new Point(0.35, 0.35),
                RadiusX = 0.7,
                RadiusY = 0.7
            };
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(240, dominant.R, dominant.G, dominant.B), 0.0));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(160, (byte)(dominant.R * 0.4), (byte)(dominant.G * 0.4), (byte)(dominant.B * 0.4)), 0.65));
            grad.GradientStops.Add(new GradientStop(Color.FromRgb(0x10, 0x10, 0x12), 1.0));

            ctx.DrawEllipse(grad, null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);

            // Subtle vinyl grooves
            var groovePen = new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 0.75);
            groovePen.Freeze();
            ctx.DrawEllipse(null, groovePen, new Point(size / 2.0, size / 2.0), size * 0.40, size * 0.40);
            ctx.DrawEllipse(null, groovePen, new Point(size / 2.0, size / 2.0), size * 0.32, size * 0.32);
            ctx.DrawEllipse(null, groovePen, new Point(size / 2.0, size / 2.0), size * 0.24, size * 0.24);
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public static Color ExtractDominantColor(BitmapSource bitmap)
    {
        try
        {
            var formatConverted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int w = Math.Min(32, formatConverted.PixelWidth);
            int h = Math.Min(32, formatConverted.PixelHeight);
            if (w <= 0 || h <= 0) return Color.FromRgb(0x0A, 0x84, 0xFF);

            var scaled = new TransformedBitmap(formatConverted, new ScaleTransform((double)w / formatConverted.PixelWidth, (double)h / formatConverted.PixelHeight));
            int stride = w * 4;
            byte[] pixels = new byte[h * stride];
            scaled.CopyPixels(pixels, stride, 0);

            long sumR = 0, sumG = 0, sumB = 0;
            int count = 0;
            double maxSat = 0;
            Color bestColor = Color.FromRgb(0x0A, 0x84, 0xFF);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];

                if (a < 128) continue; // skip transparent
                int brightness = (r + g + b) / 3;
                if (brightness < 20 || brightness > 240) continue; // skip pure black / white

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                double sat = max == 0 ? 0 : (double)(max - min) / max;

                if (sat > maxSat)
                {
                    maxSat = sat;
                    bestColor = Color.FromRgb(r, g, b);
                }

                sumR += r; sumG += g; sumB += b;
                count++;
            }

            if (maxSat >= 0.2) return bestColor;
            if (count > 0) return Color.FromRgb((byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count));
        }
        catch { }

        return Color.FromRgb(0x0A, 0x84, 0xFF);
    }

    public static bool IsArtworkTinyOrAppIcon(byte[]? artworkBytes, byte[]? appIconBytes, int minDimension = 64)
    {
        if (artworkBytes is null || artworkBytes.Length == 0) return true;

        try
        {
            using var ms = new MemoryStream(artworkBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth < minDimension || frame.PixelHeight < minDimension)
                return true;

            // A square image with fully transparent corners is an app logo, not cover art: browsers
            // (Chrome) hand out their own icon as the thumbnail until a video's artwork is published.
            if (frame.PixelWidth == frame.PixelHeight && HasTransparentCorners(frame))
                return true;

            // Check if identical to app icon
            if (appIconBytes is not null && appIconBytes.Length > 0)
            {
                if (artworkBytes.Length == appIconBytes.Length)
                {
                    bool match = true;
                    for (int i = 0; i < artworkBytes.Length; i++)
                    {
                        if (artworkBytes[i] != appIconBytes[i]) { match = false; break; }
                    }
                    if (match) return true;
                }
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private static bool HasTransparentCorners(BitmapSource frame)
    {
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[4];
        foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
        {
            bgra.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
            if (px[3] != 0) return false;
        }
        return true;
    }

    public static byte[]? BitmapSourceToBytes(BitmapSource bitmap)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    public static BitmapSource? BytesToBitmap(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public static BitmapSource? ExtractIconFromFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath) || Directory.Exists(filePath))
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
                if (ico is not null)
                {
                    using var bmp = ico.ToBitmap();
                    return BitmapToBitmapSource(bmp);
                }
            }
        }
        catch { }
        return null;
    }

    public static BitmapSource? BitmapToBitmapSource(System.Drawing.Bitmap bitmap)
    {
        IntPtr hBmp = bitmap.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                hBmp,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(hBmp);
        }
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    private const int SIIGBF_RESIZETOFIT = 0x00000000;
    private const int SIIGBF_ICONONLY = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            [In] SIZE size,
            [In] int flags,
            [Out] out IntPtr phbm);
    }
}
