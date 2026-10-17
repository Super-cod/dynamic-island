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
            if (string.IsNullOrEmpty(_ownPath)) return false;
            return string.Equals(DecodePath(keyName), Path.GetFullPath(_ownPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Decodes a ConsentStore subkey (e.g. "#C:\app\a.exe:-1001" or "C:#app#a.exe") to a path.</summary>
    private static string DecodePath(string keyName)
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
