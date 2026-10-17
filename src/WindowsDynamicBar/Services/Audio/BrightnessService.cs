using System.Management;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.Services.Audio;

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

            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();

            // Try the native WmiMonitorBrightnessEvent first: fired directly by the ACPI display driver
            // with 0 delay (no polling loop).
            try
            {
                var query = new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent");
                _watcher = new ManagementEventWatcher(scope, query);
                _watcher.EventArrived += OnBrightnessEvent;
                _watcher.Start();
                Log.Info("Brightness service started (instant ACPI events)", ("level", Level));
            }
            catch
            {
                // Fallback to intrinsic modification polling if driver does not support extrinsic event
                var query = new WqlEventQuery(
                    "SELECT * FROM __InstanceModificationEvent WITHIN 1 WHERE TargetInstance ISA 'WmiMonitorBrightness'");
                _watcher = new ManagementEventWatcher(scope, query);
                _watcher.EventArrived += OnBrightnessEvent;
                _watcher.Start();
                Log.Info("Brightness service started (polling fallback)", ("level", Level));
            }
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
            float value = -1;
            // Native WmiMonitorBrightnessEvent carries Brightness directly
            if (e.NewEvent.Properties["Brightness"]?.Value is not null)
            {
                value = Convert.ToSingle(e.NewEvent["Brightness"]);
            }
            // Fallback modification event carries TargetInstance["CurrentBrightness"]
            else if (e.NewEvent["TargetInstance"] is ManagementBaseObject target)
            {
                value = Convert.ToSingle(target["CurrentBrightness"] ?? -1);
            }

            if (value < 0) return;

            var now = DateTime.UtcNow;
            if (now - _lastEvent < TimeSpan.FromMilliseconds(40) || Math.Abs(value - _lastValue) < 0.5f) return;
            _lastEvent = now;
            _lastValue = value;
            Level = value / 100f;
            BrightnessChanged?.Invoke(Level);

            if (!_settings.Current.FeatureBrightness) return;
            string text = $"{Math.Round(value)}%";
            _bus.Raise(BarEvent.Create(BarEventKind.Brightness, "Brightness", text,
                value: Level, valueText: text, priority: 70,
                displayDuration: TimeSpan.FromSeconds(2.5), preempt: true));
        }
        catch (Exception ex) { Log.Debug("Brightness event error", ("error", ex.Message)); }
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

    public bool SetLevel(float level01)
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
