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
    private readonly ICoreAudioController _controller;
    private readonly object _stateLock = new();

    private float _lastLevel = -1;
    private bool _lastMute;
    private DateTime _lastEvent = DateTime.MinValue;
    private bool _available;
    private bool _disposed;

    private float _level;
    private bool _muted;
    private string? _deviceName;

    public bool IsAvailable
    {
        get
        {
            lock (_stateLock)
            {
                return !_disposed && _available;
            }
        }
    }

    public string? DeviceName
    {
        get
        {
            lock (_stateLock)
            {
                return _deviceName;
            }
        }
    }

    public float Level
    {
        get
        {
            lock (_stateLock)
            {
                return _level;
            }
        }
    }

    public bool Muted
    {
        get
        {
            lock (_stateLock)
            {
                return _muted;
            }
        }
    }

    public event Action<float, bool>? VolumeChanged;

    public VolumeService(EventBus bus, SettingsService settings)
        : this(bus, settings, CoreAudioController.Instance)
    {
    }

    internal VolumeService(EventBus bus, SettingsService settings, ICoreAudioController controller)
    {
        _bus = bus;
        _settings = settings;
        _controller = controller;
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
        }

        bool ok = _controller.TryInitialize(OnVolumeNotification, OnDefaultDeviceChanged, out var error);
        string? devName = null;
        var cur = ok ? _controller.GetCurrent() : null;
        if (ok) devName = _controller.GetDefaultDeviceName();

        lock (_stateLock)
        {
            if (_disposed) return;
            _available = ok;
            if (!_available)
            {
                Log.Warning("Volume service unavailable", ("reason", error ?? "unknown"));
            }
            else
            {
                if (cur is not null)
                {
                    _level = cur.Value.Level;
                    _muted = cur.Value.Muted;
                }
                _deviceName = devName;
            }
        }
    }

    // Called on a COM thread.
    internal void OnVolumeNotification(float level, bool muted)
    {
        Action<float, bool>? notify = null;
        BarEvent? evt = null;

        lock (_stateLock)
        {
            if (_disposed) return;

            _level = level;
            _muted = muted;
            notify = VolumeChanged;

            var now = DateTime.UtcNow;
            // Suppress duplicate identical updates within 500ms
            if (!ShouldSuppressNotification(level, muted, _lastLevel, _lastMute, _lastEvent, now))
            {
                _lastEvent = now;
                _lastLevel = level;
                _lastMute = muted;

                if (_settings.Current.FeatureVolume)
                {
                    string text = muted ? "Muted" : $"{Math.Round(level * 100)}%";
                    string icon = muted ? "Muted" : level <= 0 ? "Silent" : level < 0.5 ? "Low" : "High";
                    evt = BarEvent.Create(BarEventKind.Volume, icon, text,
                        value: muted ? 0 : level, valueText: text, payload: (level, muted, _deviceName),
                        priority: 70, displayDuration: TimeSpan.FromSeconds(2.5), preempt: true);
                }
            }
        }

        notify?.Invoke(level, muted);
        if (evt is not null)
        {
            _bus.Raise(evt);
        }
    }

    internal static bool ShouldSuppressNotification(float level, bool muted, float lastLevel, bool lastMute, DateTime lastEvent, DateTime now)
    {
        return Math.Abs(level - lastLevel) < 0.001f && muted == lastMute && (now - lastEvent) < TimeSpan.FromMilliseconds(500);
    }

    internal void OnDefaultDeviceChanged()
    {
        string? devName = _controller.GetDefaultDeviceName();
        var cur = _controller.GetCurrent();

        Action<float, bool>? notify = null;
        BarEvent? evt = null;
        float currentLevel;
        bool currentMuted;

        lock (_stateLock)
        {
            if (_disposed) return;

            _available = true;
            _deviceName = devName;
            if (cur is not null)
            {
                _level = cur.Value.Level;
                _muted = cur.Value.Muted;
            }

            currentLevel = _level;
            currentMuted = _muted;
            notify = VolumeChanged;

            if (_settings.Current.FeatureVolume)
            {
                evt = BarEvent.Create(BarEventKind.Volume, "Output device", _deviceName ?? "Default",
                    value: currentMuted ? 0 : currentLevel, valueText: $"{Math.Round(currentLevel * 100)}%",
                    payload: (currentLevel, currentMuted, _deviceName), priority: 70,
                    displayDuration: TimeSpan.FromSeconds(3), preempt: true);
            }
        }

        notify?.Invoke(currentLevel, currentMuted);
        if (evt is not null)
        {
            _bus.Raise(evt);
        }
    }

    public void SetLevel(float level01)
    {
        float clamped = Math.Clamp(level01, 0f, 1f);
        Action<float, bool>? notify = null;
        bool muted;

        lock (_stateLock)
        {
            if (_disposed) return;
            _level = clamped;
            muted = _muted;
            notify = VolumeChanged;
        }

        _controller.SetLevel(clamped);
        notify?.Invoke(clamped, muted);
    }

    public void ToggleMute()
    {
        bool nextMuted;
        float level;
        Action<float, bool>? notify = null;

        lock (_stateLock)
        {
            if (_disposed) return;
            nextMuted = !_muted;
            _muted = nextMuted;
            level = _level;
            notify = VolumeChanged;
        }

        _controller.SetMute(nextMuted);
        notify?.Invoke(level, nextMuted);
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _available = false;
        }

        _controller.Shutdown();
    }
}
