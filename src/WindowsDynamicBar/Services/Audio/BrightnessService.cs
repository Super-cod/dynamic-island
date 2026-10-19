using System.Management;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Services.Settings;

namespace WindowsDynamicBar.Services.Audio;

internal interface IBrightnessWatcher : IDisposable
{
    event Action<float>? BrightnessArrived;
    void Start();
    void Stop();
}

internal sealed class WmiBrightnessWatcher : IBrightnessWatcher
{
    private ManagementEventWatcher? _watcher;
    public event Action<float>? BrightnessArrived;

    public void Start()
    {
        var scope = new ManagementScope(@"\\.\root\wmi");
        scope.Connect();

        try
        {
            var query = new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent");
            var w = new ManagementEventWatcher(scope, query);
            try
            {
                w.EventArrived += OnEventArrived;
                w.Start();
                _watcher = w;
                Log.Info("Brightness service started (instant ACPI events)");
                return;
            }
            catch
            {
                try { w.EventArrived -= OnEventArrived; w.Dispose(); } catch { }
                throw;
            }
        }
        catch
        {
            var query = new WqlEventQuery(
                "SELECT * FROM __InstanceModificationEvent WITHIN 1 WHERE TargetInstance ISA 'WmiMonitorBrightness'");
            var w = new ManagementEventWatcher(scope, query);
            try
            {
                w.EventArrived += OnEventArrived;
                w.Start();
                _watcher = w;
                Log.Info("Brightness service started (polling fallback)");
            }
            catch
            {
                try { w.EventArrived -= OnEventArrived; w.Dispose(); } catch { }
                throw;
            }
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        float val = BrightnessService.ExtractBrightnessValue(e.NewEvent);
        if (val >= 0)
        {
            BrightnessArrived?.Invoke(val);
        }
    }

    public void Stop()
    {
        if (_watcher is not null)
        {
            try
            {
                _watcher.EventArrived -= OnEventArrived;
                _watcher.Stop();
            }
            catch { }
        }
    }

    public void Dispose()
    {
        Stop();
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
    }
}

/// <summary>
/// Brightness via WMI (WmiMonitorBrightnessEvent / WmiMonitorBrightnessMethods).
/// LIMITATION (documented): this API only controls internal laptop panels / monitors
/// exposing WMI brightness. External monitors via DDC/CI are not supported; we fail
/// gracefully and disable the feature.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private readonly Func<float> _queryBrightness;
    private readonly Func<float, bool> _setBrightness;
    private readonly Func<IBrightnessWatcher> _watcherFactory;
    private readonly object _sync = new();

    private IBrightnessWatcher? _watcher;
    private float _lastValue = -1;
    private DateTime _lastEvent = DateTime.MinValue;
    private float _level;
    private bool _isAvailable;
    private bool _started;
    private bool _disposed;

    public event Action<float>? BrightnessChanged;

    public bool IsAvailable
    {
        get { lock (_sync) return _isAvailable; }
    }

    public float Level
    {
        get { lock (_sync) return _level; }
    }

    public bool IsStarted
    {
        get { lock (_sync) return _started; }
    }

    public bool IsDisposed
    {
        get { lock (_sync) return _disposed; }
    }

    internal bool HasWatcher
    {
        get { lock (_sync) return _watcher is not null; }
    }

    public BrightnessService(EventBus bus, SettingsService settings)
        : this(bus, settings, queryBrightness: null, setBrightness: null, watcherFactory: null)
    {
    }

    internal BrightnessService(
        EventBus bus,
        SettingsService settings,
        Func<float>? queryBrightness,
        Func<float, bool>? setBrightness,
        Func<IBrightnessWatcher>? watcherFactory)
    {
        _bus = bus;
        _settings = settings;
        _queryBrightness = queryBrightness ?? QueryBrightness;
        _setBrightness = setBrightness ?? SetLevelWmi;
        _watcherFactory = watcherFactory ?? (() => new WmiBrightnessWatcher());
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _started) return;
            _started = true;
        }

        IBrightnessWatcher? watcher = null;
        try
        {
            float initial = _queryBrightness();
            bool available = initial >= 0;

            lock (_sync)
            {
                if (_disposed) return;
                _level = initial >= 0 ? initial : 0;
                _lastValue = initial >= 0 ? initial * 100f : -1;
                _isAvailable = available;
                if (!_isAvailable)
                {
                    _started = false;
                    Log.Info("Brightness API not available on this display");
                    return;
                }
            }

            watcher = _watcherFactory();
            watcher.BrightnessArrived += OnBrightnessArrived;

            lock (_sync)
            {
                if (_disposed)
                {
                    CleanupWatcher(watcher);
                    return;
                }
            }

            watcher.Start();

            lock (_sync)
            {
                if (_disposed)
                {
                    CleanupWatcher(watcher);
                    return;
                }
                _watcher = watcher;
            }

            Log.Info("Brightness service started", ("level", Level));
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _started = false;
                _isAvailable = false;
                _watcher = null;
            }
            CleanupWatcher(watcher);
            Log.Info("Brightness unavailable", ("reason", ex.Message));
        }
    }

    private void OnBrightnessArrived(float value) => OnBrightnessArrivedCore(value, null);

    internal void OnBrightnessArrivedCore(float value, DateTime? nowUtc = null)
    {
        try
        {
            if (value < 0) return;

            Action<float>? notify = null;
            BarEvent? eventToRaise = null;
            float levelToNotify = 0;

            lock (_sync)
            {
                if (_disposed) return;

                var now = nowUtc ?? DateTime.UtcNow;
                if (now - _lastEvent < TimeSpan.FromMilliseconds(40) || Math.Abs(value - _lastValue) < 0.5f)
                    return;

                _lastEvent = now;
                _lastValue = value;
                _level = Math.Clamp(value / 100f, 0f, 1f);
                _isAvailable = true;
                levelToNotify = _level;
                notify = BrightnessChanged;

                if (_settings.Current.FeatureBrightness)
                {
                    eventToRaise = CreateBarEvent(_level, value);
                }
            }

            // Gated dispatch outside locks
            lock (_sync)
            {
                if (_disposed) return;
            }
            notify?.Invoke(levelToNotify);

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
            Log.Debug("Brightness event error", ("error", ex.Message));
        }
    }

    internal static BarEvent CreateBarEvent(float level, float rawValue)
    {
        string text = $"{Math.Round(rawValue)}%";
        return BarEvent.Create(BarEventKind.Brightness, "Brightness", text,
            value: level, valueText: text, priority: 70,
            displayDuration: TimeSpan.FromSeconds(2.5), preempt: true);
    }

    public static float QueryBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"\\.\root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                using (obj)
                    return Convert.ToSingle(obj["CurrentBrightness"]) / 100f;
            }
        }
        catch { }
        return -1;
    }

    internal static bool SetLevelWmi(float level01)
    {
        try
        {
            byte target = (byte)Math.Clamp(Math.Round(level01 * 100), 0, 100);
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            using var results = searcher.Get();
            bool ok = false;
            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    obj.InvokeMethod("WmiSetBrightness", new object[] { uint.MaxValue, target });
                    ok = true;
                }
            }
            return ok;
        }
        catch (Exception ex)
        {
            Log.Debug("Brightness set failed", ("error", ex.Message));
            return false;
        }
    }

    public bool SetLevel(float level01)
    {
        lock (_sync)
        {
            if (_disposed) return false;
        }

        float clamped = Math.Clamp(level01, 0f, 1f);
        bool ok = _setBrightness(clamped);
        if (!ok) return false;

        Action<float>? notify = null;
        lock (_sync)
        {
            if (_disposed) return false;
            _level = clamped;
            _lastValue = clamped * 100f;
            _lastEvent = DateTime.UtcNow;
            _isAvailable = true;
            notify = BrightnessChanged;
        }

        lock (_sync)
        {
            if (_disposed) return ok;
        }
        notify?.Invoke(clamped);
        return ok;
    }

    internal static float ExtractBrightnessCore(object? brightnessProp, object? targetCurrentBrightnessProp)
    {
        try
        {
            if (brightnessProp is not null)
            {
                return Convert.ToSingle(brightnessProp);
            }
            if (targetCurrentBrightnessProp is not null)
            {
                return Convert.ToSingle(targetCurrentBrightnessProp);
            }
        }
        catch { }
        return -1;
    }

    internal static float ExtractBrightnessValue(ManagementBaseObject? newEvent)
    {
        if (newEvent is null) return -1;
        try
        {
            object? brightness = null;
            try
            {
                if (newEvent.Properties["Brightness"]?.Value is not null)
                {
                    brightness = newEvent["Brightness"];
                }
            }
            catch { }

            object? targetProp = null;
            try
            {
                if (newEvent["TargetInstance"] is ManagementBaseObject target)
                {
                    targetProp = target["CurrentBrightness"];
                }
            }
            catch { }

            return ExtractBrightnessCore(brightness, targetProp);
        }
        catch { }
        return -1;
    }

    public void Dispose()
    {
        IBrightnessWatcher? watcherToDispose;

        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;

            watcherToDispose = _watcher;
            _watcher = null;
        }

        CleanupWatcher(watcherToDispose);
    }

    private void CleanupWatcher(IBrightnessWatcher? watcher)
    {
        if (watcher is null) return;
        try
        {
            watcher.BrightnessArrived -= OnBrightnessArrived;
            watcher.Stop();
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug("Error disposing brightness watcher", ("error", ex.Message));
        }
    }
}
