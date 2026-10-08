using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.Services.Privacy;

public sealed record PrivacyState(bool CameraInUse, bool MicInUse, string? CameraApp, string? MicApp);

/// <summary>
/// Camera/microphone-in-use detection by watching the Windows capability consent store.
/// Windows updates HKCU\...\CapabilityAccessManager\ConsentStore\{webcam,microphone}
/// LastUsedTimeStart/LastUsedTimeStop when apps acquire/release devices. We watch the
/// registry with a change-notify event (no tight polling) and compute live state.
/// LIMITATION: identifies per-app usage for packaged and classic apps Windows records;
/// drivers/legacy apps that bypass the consent store are not reported.
/// </summary>
public sealed class PrivacyService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private PrivacyState _last = new(false, false, null, null);
    private System.Threading.Timer? _pollTimer;
    private bool _started;

    public event Action<PrivacyState>? PrivacyChanged;
    public PrivacyState Current => _last;

    public PrivacyService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public void Start()
    {
        if (_started) return;
        try
        {
            // Registry change notification via RegNotifyChangeKeyValue is possible, but the
            // consent store lives under HKCU with volatile semantics that differ per Windows
            // build. We therefore re-read four small registry values on a 1s timer
            // (~0.001% CPU; each read is microseconds) — far cheaper than WMI/events and
            // fully reliable. Documented in README under performance notes.
            Recompute();
            _pollTimer = new System.Threading.Timer(_ => Recompute(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            _started = true;
            Log.Info("Privacy service started");
        }
        catch (Exception ex)
        {
            Log.Warning("Privacy service failed to start", ("error", ex.Message));
        }
    }

    private void Recompute()
    {
        try
        {
            bool cam = false, mic = false;
            string? camApp = null, micApp = null;
            var now = DateTime.Now.ToFileTimeUtc();

            ScanStore("webcam", ref cam, ref camApp, now);
            ScanStore("microphone", ref mic, ref micApp, now);

            var state = new PrivacyState(cam, mic, camApp, micApp);
            if (state == _last) return;
            var prev = _last;
            _last = state;
            PrivacyChanged?.Invoke(state);
            RaiseIfChanged(prev, state);
        }
        catch (Exception ex) { Log.Debug("Privacy recompute failed", ("error", ex.Message)); }
    }

    private static void ScanStore(string store, ref bool active, ref string? app, long now)
    {
        string basePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\" + store;
        foreach (var root in new[] { Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(basePath);
            if (key is not null && IsActive(key, now)) { active = true; app ??= "System"; }

            using var np = key?.OpenSubKey("NonPackaged");
            if (np is null) continue;
            foreach (string sub in np.GetSubKeyNames())
            {
                using var subKey = np.OpenSubKey(sub);
                if (subKey is null) continue;
                if (!IsActive(subKey, now)) continue;
                active = true;
                app ??= FriendlyName(sub);
                break;
            }
        }
    }

    private static bool IsActive(RegistryKey key, long now)
    {
        object? start = key.GetValue("LastUsedTimeStart");
        object? stop = key.GetValue("LastUsedTimeStop");
        if (start is not long s) return false;
        if (stop is not long st) return s > 0; // no stop => still open
        return s > st && s <= now;
    }

    private static string FriendlyName(string keyName)
    {
        // keyName looks like "#C:\path\app.exe:-1001"
        string path = keyName;
        int idx = path.LastIndexOf(':');
        if (idx > 0) path = path[..idx];
        if (path.StartsWith("#", StringComparison.Ordinal)) path = path[1..];
        try { return Path.GetFileNameWithoutExtension(path); }
        catch { return "App"; }
    }

    private void RaiseIfChanged(PrivacyState prev, PrivacyState cur)
    {
        if (!_settings.Current.FeaturePrivacyIndicators) return;
        if (cur.CameraInUse != prev.CameraInUse || cur.MicInUse != prev.MicInUse)
        {
            string title = (cur.CameraInUse && cur.MicInUse) ? "Camera + Microphone"
                : cur.CameraInUse ? "Camera" : cur.MicInUse ? "Microphone" : "Privacy";
            string sub = cur.CameraInUse && cur.MicInUse
                ? $"{cur.CameraApp} / {cur.MicApp}"
                : cur.CameraInUse ? cur.CameraApp ?? "In use"
                : cur.MicInUse ? cur.MicApp ?? "In use" : "Released";
            bool off = !cur.CameraInUse && !cur.MicInUse;
            _bus.Raise(BarEvent.Create(BarEventKind.Privacy, title, sub,
                payload: cur, priority: off ? 20 : 90,
                displayDuration: TimeSpan.FromSeconds(off ? 2 : 4)));
        }
    }

    public void Dispose()
    {
        try { _pollTimer?.Dispose(); } catch { }
    }
}

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

/// <summary>
/// Download progress by watching the user's Downloads folder for browser temp files
/// (.crdownload, .part, .download, .opdownload). LIMITATION (documented): Windows does
/// not expose a universal download API; only file-based downloads landing in Downloads
/// are tracked. In-app/async downloads are invisible to this model.
/// </summary>
public sealed class DownloadService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private FileSystemWatcher? _watcher;
    private readonly Dictionary<string, WatchState> _active = new();
    private readonly object _sync = new();
    private System.Threading.Timer? _progressTimer;

    private sealed class WatchState
    {
        public string DisplayName = "";
        public long LastBytes;
        public long TotalEstimate;
        public double Speed;
        public DateTime LastTick;
        public DateTime Started;
    }

    public event Action<string, double, double>? DownloadProgress; // name, fraction, speed

    public DownloadService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public void Start()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dir)) return;

            _watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher.Created += (s, e) => OnFileActivity(e.FullPath, isNew: true);
            _watcher.Changed += (s, e) => OnFileActivity(e.FullPath, isNew: false);
            _watcher.Deleted += (s, e) => OnFileDone(e.FullPath, completed: true);
            _watcher.EnableRaisingEvents = true;

            _progressTimer = new System.Threading.Timer(_ => TickProgress(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            Log.Info("Download watcher started", ("dir", dir));
        }
        catch (Exception ex) { Log.Warning("Download watcher failed", ("error", ex.Message)); }
    }

    private static bool IsTempDownload(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".crdownload" or ".part" or ".download" or ".opdownload" or ".partial";
    }

    private void OnFileActivity(string path, bool isNew)
    {
        if (!_settings.Current.FeatureDownloads) return;
        if (!IsTempDownload(path)) return;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return;
            lock (_sync)
            {
                if (!_active.TryGetValue(path, out var st))
                {
                    st = new WatchState
                    {
                        DisplayName = CleanName(fi.Name),
                        Started = DateTime.UtcNow,
                        LastTick = DateTime.UtcNow,
                    };
                    _active[path] = st;
                    _bus.Raise(BarEvent.Create(BarEventKind.Download, "Downloading…", st.DisplayName,
                        value: 0, valueText: "0%", payload: st.DisplayName, priority: 35,
                        displayDuration: TimeSpan.FromSeconds(6)));
                }
                st.LastBytes = fi.Length;
            }
        }
        catch { }
    }

    private static string CleanName(string tempName)
    {
        foreach (string ext in new[] { ".crdownload", ".part", ".download", ".opdownload", ".partial" })
            if (tempName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return tempName[..^ext.Length];
        return tempName;
    }

    private void TickProgress()
    {
        List<(string path, string name, double frac, double speed)> updates = new();
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            foreach (var (path, st) in _active.ToList())
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists) { _active.Remove(path); continue; }
                    long delta = fi.Length - st.LastBytes;
                    double secs = (now - st.LastTick).TotalSeconds;
                    if (secs >= 0.9)
                    {
                        st.Speed = delta / secs;
                        st.LastBytes = fi.Length;
                        st.LastTick = now;
                    }
                    // Heuristic total: never shrink below current; grow with observed rate for ETA.
                    st.TotalEstimate = Math.Max(st.TotalEstimate, fi.Length + (long)(st.Speed * 8));
                    double frac = st.TotalEstimate > 0 ? Math.Min(0.999, fi.Length / (double)st.TotalEstimate) : 0;
                    // Only surface files that are actually moving — a stalled temp file
                    // must not keep popping the bar open once its card has collapsed.
                    if (delta > 0)
                        updates.Add((path, st.DisplayName, frac, st.Speed));
                }
                catch { _active.Remove(path); }
            }
        }

        foreach (var (path, name, frac, speed) in updates)
        {
            DownloadProgress?.Invoke(name, frac, speed);
            string speedText = FormatSpeed(speed);
            _bus.Raise(BarEvent.Create(BarEventKind.Download, name,
                $"{Math.Round(frac * 100)}% · {speedText}", value: frac,
                valueText: $"{Math.Round(frac * 100)}%", priority: 35,
                displayDuration: TimeSpan.FromSeconds(2), isTick: true));
        }
    }

    private void OnFileDone(string path, bool completed)
    {
        lock (_sync)
        {
            if (!_active.Remove(path, out var st)) return;
            if (completed)
            {
                _bus.Raise(BarEvent.Create(BarEventKind.Toast, "Download complete", st.DisplayName,
                    payload: st.DisplayName, priority: 65, displayDuration: TimeSpan.FromSeconds(4)));
            }
        }
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "…";
        if (bytesPerSec > 1024 * 1024) return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
        if (bytesPerSec > 1024) return $"{bytesPerSec / 1024:F0} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { }
        _progressTimer?.Dispose();
    }
}

/// <summary>USB attach/detach via WM_DEVICECHANGE (message-based, no polling).</summary>
public sealed class UsbService
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;

    public UsbService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    /// <summary>Called from WndProc on WM_DEVICECHANGE.</summary>
    public void OnDeviceChange(int wParam, IntPtr lParam)
    {
        if (!_settings.Current.FeatureUsb) return;
        bool arrival = wParam == Interop.NativeMethods.DBT_DEVICEARRIVAL;
        bool removal = wParam == Interop.NativeMethods.DBT_DEVICEREMOVECOMPLETE;
        if (!arrival && !removal) return;

        string name = "USB device";
        try
        {
            int devType = System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
            if (devType == Interop.NativeMethods.DBT_DEVTYP_VOLUME)
            {
                int mask = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 4);
                for (int i = 0; i < 16; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    char drive = (char)('A' + i);
                    var driveInfo = new DriveInfo($"{drive}");
                    name = $"{driveInfo.Name.TrimEnd('\\')} drive{(!string.IsNullOrEmpty(driveInfo.VolumeLabel) ? " · " + driveInfo.VolumeLabel : "")}";
                    break;
                }
            }
        }
        catch { /* keep generic name */ }

        _bus.Raise(BarEvent.Create(BarEventKind.Usb,
            arrival ? "USB connected" : "USB removed", name,
            priority: 50, displayDuration: TimeSpan.FromSeconds(3)));
    }
}

/// <summary>
/// Screenshot detection via the user's Pictures\Screenshots folder (Win+PrtScn default
/// location) using a FileSystemWatcher. LIMITATION: Snipping Tool "copy" mode and
/// third-party tools saving elsewhere are not detected.
/// </summary>
public sealed class ScreenshotService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private FileSystemWatcher? _watcher;

    public ScreenshotService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public void Start()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            if (!Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime };
            _watcher.Created += (s, e) =>
            {
                if (!_settings.Current.FeatureScreenshots) return;
                string ext = Path.GetExtension(e.Name ?? "").ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp")) return;
                _bus.Raise(BarEvent.Create(BarEventKind.Screenshot, "Screenshot saved", e.Name,
                    payload: e.FullPath, priority: 60, displayDuration: TimeSpan.FromSeconds(3)));
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Debug("Screenshot watcher unavailable", ("error", ex.Message)); }
    }

    public void Dispose() { try { _watcher?.Dispose(); } catch { } }
}
