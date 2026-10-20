using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Services.Settings;

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
    private readonly Func<long, PrivacyState>? _scanner;
    private readonly object _sync = new();

    private PrivacyState _last = new(false, false, null, null);
    private System.Threading.Timer? _pollTimer;
    private bool _started;
    private bool _disposed;

    public event Action<PrivacyState>? PrivacyChanged;

    public PrivacyState Current
    {
        get
        {
            lock (_sync) return _last;
        }
    }

    public bool IsStarted
    {
        get
        {
            lock (_sync) return _started;
        }
    }

    public bool IsDisposed
    {
        get
        {
            lock (_sync) return _disposed;
        }
    }

    internal bool HasTimer
    {
        get
        {
            lock (_sync) return _pollTimer is not null;
        }
    }

    public PrivacyService(EventBus bus, SettingsService settings)
        : this(bus, settings, scanner: null)
    {
    }

    internal PrivacyService(EventBus bus, SettingsService settings, Func<long, PrivacyState>? scanner)
    {
        _bus = bus;
        _settings = settings;
        _scanner = scanner;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _started) return;
            _started = true;
        }

        System.Threading.Timer? timer = null;
        try
        {
            // Re-read four small registry values on a 1s timer (~0.001% CPU; each read is microseconds)
            RecomputeCore();

            timer = new System.Threading.Timer(_ => RecomputeCore(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            lock (_sync)
            {
                if (_disposed)
                {
                    timer.Dispose();
                    return;
                }
                _pollTimer = timer;
            }

            Log.Info("Privacy service started");
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _started = false;
                _pollTimer = null;
            }
            try { timer?.Dispose(); } catch { }
            Log.Warning("Privacy service failed to start", ("error", ex.Message));
        }
    }

    internal void RecomputeCore(long? nowFileTimeUtc = null)
    {
        try
        {
            lock (_sync)
            {
                if (_disposed) return;
            }

            long now = nowFileTimeUtc ?? DateTime.Now.ToFileTimeUtc();
            PrivacyState state;

            if (_scanner is not null)
            {
                state = _scanner(now);
            }
            else
            {
                bool cam = false, mic = false;
                string? camApp = null, micApp = null;
                ScanStore("webcam", ref cam, ref camApp, now);
                ScanStore("microphone", ref mic, ref micApp, now);
                state = new PrivacyState(cam, mic, camApp, micApp);
            }

            Action<PrivacyState>? notify = null;
            BarEvent? eventToRaise = null;
            PrivacyState? stateToNotify = null;

            lock (_sync)
            {
                if (_disposed) return;
                if (state == _last) return;

                var prev = _last;
                _last = state;
                stateToNotify = state;
                notify = PrivacyChanged;

                if (_settings.Current.FeaturePrivacyIndicators &&
                    (state.CameraInUse != prev.CameraInUse || state.MicInUse != prev.MicInUse))
                {
                    eventToRaise = CreateBarEvent(prev, state);
                }
            }

            // Gated dispatch outside locks
            lock (_sync)
            {
                if (_disposed) return;
            }
            notify?.Invoke(stateToNotify!);

            if (eventToRaise is not null)
            {
                lock (_sync)
                {
                    if (_disposed) return;
                }
                _bus.Raise(eventToRaise);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Privacy recompute failed", ("error", ex.Message));
        }
    }

    private static void ScanStore(string store, ref bool active, ref string? app, long now)
    {
        string basePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\" + store;
        foreach (var root in new[] { Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(basePath);
            if (key is not null && IsActive(key, now)) { active = true; app ??= "System"; }
            if (key is null) continue;

            // Packaged (Store) apps - WhatsApp, Camera, Teams - record use on their own subkey
            // ("5319275A.WhatsAppDesktop_cv1g1gvanyjgm"); classic apps live under NonPackaged.
            foreach (string sub in key.GetSubKeyNames())
            {
                if (sub.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                using var subKey = key.OpenSubKey(sub);
                if (subKey is null || !IsActive(subKey, now)) continue;
                active = true;
                app ??= PackageFriendlyName(sub);
                break;
            }

            using var np = key.OpenSubKey("NonPackaged");
            if (np is null) continue;
            foreach (string sub in np.GetSubKeyNames())
            {
                using var subKey = np.OpenSubKey(sub);
                if (subKey is null) continue;
                if (!IsActive(subKey, now)) continue;
                // The mirror panel owns a camera/mic; never report the bar's own capture back to the
                // user as a privacy alert (that would immediately collapse the mirror it describes).
                if (IsOwnProcess(sub)) continue;
                active = true;
                app ??= FriendlyName(sub);
                break;
            }
        }
    }

    private static string? _ownPath;

    /// <summary>True when a consent-store subkey refers to this process' executable.</summary>
    private static bool IsOwnProcess(string keyName)
    {
        try
        {
            _ownPath ??= Environment.ProcessPath
                         ?? Process.GetCurrentProcess().MainModule?.FileName;
            return IsOwnProcessCore(keyName, _ownPath);
        }
        catch { return false; }
    }

    internal static bool IsOwnProcessCore(string keyName, string? ownExecutablePath)
    {
        if (string.IsNullOrEmpty(ownExecutablePath)) return false;
        try
        {
            return string.Equals(DecodePath(keyName), Path.GetFullPath(ownExecutablePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Decodes a ConsentStore subkey (e.g. "#C:\app\a.exe:-1001" or "C:#app#a.exe") to a path.</summary>
    internal static string DecodePath(string keyName)
    {
        string s = keyName;
        if (s.StartsWith("#", StringComparison.Ordinal)) s = s[1..];
        int last = s.LastIndexOf(':');
        if (last > 2 && int.TryParse(s[(last + 1)..], out _)) s = s[..last];
        s = s.Replace('#', Path.DirectorySeparatorChar);
        try { return Path.GetFullPath(s); } catch { return s; }
    }

    private static bool IsActive(RegistryKey key, long now)
    {
        object? startObj = key.GetValue("LastUsedTimeStart");
        object? stopObj = key.GetValue("LastUsedTimeStop");
        long? start = startObj is long s ? s : null;
        long? stop = stopObj is long st ? st : null;
        return IsActiveCore(start, stop, now);
    }

    internal static bool IsActiveCore(long? start, long? stop, long now)
    {
        if (start is not long s || s <= 0) return false;
        if (stop is not long st) return true; // no stop => still open
        return s > st && s <= now;
    }

    internal static string FriendlyName(string keyName)
    {
        try
        {
            string decoded = DecodePath(keyName);
            string name = Path.GetFileNameWithoutExtension(decoded);
            return string.IsNullOrEmpty(name) ? "App" : name;
        }
        catch { return "App"; }
    }

    /// <summary>"5319275A.WhatsAppDesktop_cv1g1gvanyjgm" -> "WhatsAppDesktop".</summary>
    internal static string PackageFriendlyName(string familyName)
    {
        string s = familyName;
        int us = s.LastIndexOf('_');
        if (us > 0) s = s[..us];
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        return s.Length > 0 ? s : "App";
    }

    internal static BarEvent CreateBarEvent(PrivacyState prev, PrivacyState cur)
    {
        string title = (cur.CameraInUse && cur.MicInUse) ? "Camera + Microphone"
            : cur.CameraInUse ? "Camera" : cur.MicInUse ? "Microphone" : "Privacy";
        string sub = cur.CameraInUse && cur.MicInUse
            ? $"{cur.CameraApp} / {cur.MicApp}"
            : cur.CameraInUse ? cur.CameraApp ?? "In use"
            : cur.MicInUse ? cur.MicApp ?? "In use" : "Released";
        bool off = !cur.CameraInUse && !cur.MicInUse;
        return BarEvent.Create(BarEventKind.Privacy, title, sub,
            payload: cur, priority: off ? 20 : 90,
            displayDuration: TimeSpan.FromSeconds(off ? 2 : 4));
    }

    public void Dispose()
    {
        System.Threading.Timer? timerToDispose;

        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;

            timerToDispose = _pollTimer;
            _pollTimer = null;
        }

        try { timerToDispose?.Dispose(); } catch { }
    }
}
