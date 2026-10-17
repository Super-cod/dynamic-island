using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Interop;
using WindowsDynamicBar.Services.Settings;

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

        var now = DateTime.UtcNow;
        // Suppress duplicate identical updates within 500ms
        if (Math.Abs(level - _lastLevel) < 0.001 && muted == _lastMute && now - _lastEvent < TimeSpan.FromMilliseconds(500)) return;
        _lastEvent = now;
        _lastLevel = level;
        _lastMute = muted;

        if (!_settings.Current.FeatureVolume) return;
        string text = muted ? "Muted" : $"{Math.Round(level * 100)}%";
        string icon = muted ? "Muted" : level <= 0 ? "Silent" : level < 0.5 ? "Low" : "High";
        _bus.Raise(BarEvent.Create(BarEventKind.Volume, icon, text,
            value: muted ? 0 : level, valueText: text, payload: (level, muted, DeviceName),
            priority: 70, displayDuration: TimeSpan.FromSeconds(2.5), preempt: true));
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
                payload: (Level, Muted, DeviceName), priority: 70,
                displayDuration: TimeSpan.FromSeconds(3), preempt: true));
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
