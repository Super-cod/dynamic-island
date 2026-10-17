using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.Services.Privacy;

/// <summary>
/// Clipboard-change detection. Reads ONLY format availability (no content) unless the
/// user explicitly enables content preview; sensitive content is never logged/stored.
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private IntPtr _hwnd;
    private bool _attached;

    public event Action<string>? ClipboardChanged; // format description only

    public ClipboardService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        try
        {
            _attached = NativeMethods_AddClipboardFormatListener(hwnd);
            if (!_attached) Log.Warning("Clipboard listener not supported");
        }
        catch (Exception ex) { Log.Warning("Clipboard attach failed", ("error", ex.Message)); }
    }

    private static bool NativeMethods_AddClipboardFormatListener(IntPtr hwnd)
        => Interop.NativeMethods.AddClipboardFormatListener(hwnd);

    /// <summary>Called from the window WndProc on WM_CLIPBOARDUPDATE.</summary>
    public void OnClipboardUpdate()
    {
        if (!_settings.Current.ClipboardMonitoring || !_settings.Current.FeatureClipboard) return;
        try
        {
            string format = DescribeFormats();
            if (format.Length == 0) return;
            ClipboardChanged?.Invoke(format);
            _bus.Raise(BarEvent.Create(BarEventKind.Clipboard, "Copied", format,
                priority: 15, displayDuration: TimeSpan.FromSeconds(1.5)));
        }
        catch (Exception ex) { Log.Debug("Clipboard read failed", ("error", ex.Message)); }
    }

    private static string DescribeFormats()
    {
        // Format-presence check only — no content read by default.
        try
        {
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                int n = System.Windows.Clipboard.GetFileDropList().Count;
                return n == 1 ? "1 file" : $"{n} files";
            }
            if (System.Windows.Clipboard.ContainsImage()) return "Image";
            if (System.Windows.Clipboard.ContainsText()) return "Text";
        }
        catch { }
        return "";
    }

    public void Dispose()
    {
        if (_attached && _hwnd != IntPtr.Zero)
        {
            try { Interop.NativeMethods.RemoveClipboardFormatListener(_hwnd); } catch { }
        }
    }
}
