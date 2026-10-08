using System.Diagnostics;
using System.Management;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Interop;

namespace WindowsDynamicBar.Services.Audio;

public sealed class VolumeService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private float _lastLevel = -1;
    private bool _lastMute;
    private DateTime _lastEvent = DateTime.MinValue;
    private bool _available;

    public bool IsAvailable => _available;
    public string? DeviceName { get; private set; }
    public float Level { get; private set; }
    public bool Muted { get; private set; }

    public event Action<float, bool>? VolumeChanged;

    public VolumeService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public void Start()
    {
        _available = CoreAudio.TryInitialize(OnVolumeNotification, OnDefaultDeviceChanged, out var error);
        if (!_available)
            Log.Warning("Volume service unavailable", ("reason", error ?? "unknown"));
        else
        {
            var cur = CoreAudio.GetCurrent();
            if (cur is not null) { Level = cur.Value.Level; Muted = cur.Value.Muted; }
            DeviceName = CoreAudio.GetDefaultDeviceName();
        }
    }

    // Called on a COM thread.
    private void OnVolumeNotification(float level, bool muted)
    {
        Level = level;
        Muted = muted;
        VolumeChanged?.Invoke(level, muted);

        // Debounce rapid events (scroll-wheel volume ramps).
        var now = DateTime.UtcNow;
        if (now - _lastEvent < TimeSpan.FromMilliseconds(60)) return;
        if (Math.Abs(level - _lastLevel) < 0.001 && muted == _lastMute) return;
        _lastEvent = now;
        _lastLevel = level;
        _lastMute = muted;

        if (!_settings.Current.FeatureVolume) return;
        string text = muted ? "Muted" : $"{Math.Round(level * 100)}%";
        string icon = muted ? "Muted" : level <= 0 ? "Silent" : level < 0.5 ? "Low" : "High";
        _bus.Raise(BarEvent.Create(BarEventKind.Volume, icon, text,
            value: muted ? 0 : level, valueText: text, payload: (level, muted, DeviceName),
            priority: 70, displayDuration: TimeSpan.FromSeconds(2.5)));
    }

    private void OnDefaultDeviceChanged()
    {
        DeviceName = CoreAudio.GetDefaultDeviceName();
        var cur = CoreAudio.GetCurrent();
        if (cur is not null) { Level = cur.Value.Level; Muted = cur.Value.Muted; }
        VolumeChanged?.Invoke(Level, Muted);
        if (_settings.Current.FeatureVolume)
            _bus.Raise(BarEvent.Create(BarEventKind.Volume, "Output device", DeviceName ?? "Default",
                value: Muted ? 0 : Level, valueText: $"{Math.Round(Level * 100)}%",
                payload: (Level, Muted, DeviceName), priority: 55,
                displayDuration: TimeSpan.FromSeconds(3)));
    }

    public void SetLevel(float level01)
    {
        CoreAudio.SetLevel(level01);
        Level = Math.Clamp(level01, 0f, 1f);
        VolumeChanged?.Invoke(Level, Muted);
    }

    public void ToggleMute()
    {
        CoreAudio.SetMute(!Muted);
        Muted = !Muted;
        VolumeChanged?.Invoke(Level, Muted);
    }

    public void Dispose() => CoreAudio.Shutdown();
}

/// <summary>
/// Brightness via WMI (WmiMonitorBrightness / WmiMonitorBrightnessMethods).
/// LIMITATION (documented): this API only controls internal laptop panels / monitors
/// exposing WMI brightness. External monitors via DDC/CI are not supported; we fail
/// gracefully and disable the feature.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private ManagementEventWatcher? _watcher;
    private float _lastValue = -1;
    private DateTime _lastEvent = DateTime.MinValue;

    public bool IsAvailable { get; private set; }
    public float Level { get; private set; }

    public event Action<float>? BrightnessChanged;

    public BrightnessService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public void Start()
    {
        try
        {
            Level = QueryBrightness();
            IsAvailable = Level >= 0;
            if (!IsAvailable)
            {
                Log.Info("Brightness API not available on this display");
                return;
            }

            // Event-driven: WMI modification events on WmiMonitorBrightness (poll-free).
            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();
            var query = new WqlEventQuery(
                "SELECT * FROM __InstanceModificationEvent WITHIN 1 WHERE TargetInstance ISA 'WmiMonitorBrightness'");
            _watcher = new ManagementEventWatcher(scope, query);
            _watcher.EventArrived += OnBrightnessEvent;
            _watcher.Start();
            Log.Info("Brightness service started", ("level", Level));
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            Log.Info("Brightness unavailable", ("reason", ex.Message));
        }
    }

    private void OnBrightnessEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var target = e.NewEvent["TargetInstance"] as ManagementBaseObject;
            float value = Convert.ToSingle(target?["CurrentBrightness"] ?? -1);
            if (value < 0) return;

            var now = DateTime.UtcNow;
            if (now - _lastEvent < TimeSpan.FromMilliseconds(300) || Math.Abs(value - _lastValue) < 0.5f) return;
            _lastEvent = now;
            _lastValue = value;
            Level = value / 100f;
            BrightnessChanged?.Invoke(Level);

            if (!_settings.Current.FeatureBrightness) return;
            string text = $"{Math.Round(value)}%";
            _bus.Raise(BarEvent.Create(BarEventKind.Brightness, "Brightness", text,
                value: Level, valueText: text, priority: 70,
                displayDuration: TimeSpan.FromSeconds(2.5)));
        }
        catch (Exception ex) { Log.Debug("Brightness event error", ("error", ex.Message)); }
    }

    public static float QueryBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"\\.\root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject obj in searcher.Get())
                return Convert.ToSingle(obj["CurrentBrightness"]) / 100f;
        }
        catch { }
        return -1;
    }

    public bool SetLevel(float level01)
    {
        try
        {
            byte target = (byte)Math.Clamp(Math.Round(level01 * 100), 0, 100);
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            bool ok = false;
            foreach (ManagementObject obj in searcher.Get())
            {
                obj.InvokeMethod("WmiSetBrightness", new object[] { uint.MaxValue, target });
                ok = true;
            }
            if (ok) { Level = level01; BrightnessChanged?.Invoke(Level); }
            return ok;
        }
        catch (Exception ex)
        {
            Log.Debug("Brightness set failed", ("error", ex.Message));
            return false;
        }
    }

    public void Dispose()
    {
        try { _watcher?.Stop(); _watcher?.Dispose(); } catch { }
    }
}
