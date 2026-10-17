using System.Runtime.InteropServices;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Interop;

namespace WindowsDynamicBar.Services.SystemInfo;

public sealed record BatteryInfo(double Percent, bool Charging, bool EnergySaver, string? TimeRemaining);

public sealed class BatteryService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private BatteryInfo _last = new(0, false, false, null);
    private System.Threading.Timer? _pollTimer;

    public event Action<BatteryInfo>? BatteryChanged;

    public BatteryService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public void Start()
    {
        // Event-driven: PowerModeChanged for AC plug/unplug, plus low-frequency poll
        // (battery percentage has no public change event on all SKUs).
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;

        _last = Query();
        BatteryChanged?.Invoke(_last);

        int pollMs = _settings.Current.PerformanceMode switch
        {
            PerformanceMode.LowPower => 120_000,
            PerformanceMode.Performance => 20_000,
            _ => 45_000
        };
        _pollTimer = new System.Threading.Timer(_ =>
        {
            var prev = _last;
            var cur = Query();
            if (Math.Abs(cur.Percent - prev.Percent) >= 1 || cur.Charging != prev.Charging ||
                cur.EnergySaver != prev.EnergySaver)
            {
                _last = cur;
                BatteryChanged?.Invoke(cur);
                RaiseBatteryEvent(prev, cur);
            }
        }, null, pollMs, pollMs);
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        // Re-query on any power event (plug/unplug surfaces as StatusChange, wake as Resume)
        // so charging flips pop the bar immediately instead of at the next poll.
        if (e.Mode is not (Microsoft.Win32.PowerModes.Resume or Microsoft.Win32.PowerModes.StatusChange))
            return;
        var prev = _last;
        var cur = Query();
        _last = cur;
        if (cur.Charging != prev.Charging || cur.EnergySaver != prev.EnergySaver)
        {
            BatteryChanged?.Invoke(cur);
            RaiseBatteryEvent(prev, cur);
            Log.Info("Power state change", ("percent", cur.Percent), ("charging", cur.Charging));
        }
    }

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason != Microsoft.Win32.SessionSwitchReason.SessionUnlock) return;
        var cur = Query();
        _last = cur;
        BatteryChanged?.Invoke(cur);
    }

    private void RaiseBatteryEvent(BatteryInfo prev, BatteryInfo cur)
    {
        if (!_settings.Current.FeatureBattery) return;

        bool chargingFlipped = cur.Charging != prev.Charging;
        bool saverFlipped = cur.EnergySaver != prev.EnergySaver;
        bool lowNow = cur.Percent <= 20 && !cur.Charging;
        bool lowBefore = prev.Percent <= 20 && !prev.Charging;

        // Routine percent drift is not worth interrupting the user for — only
        // charging/saver flips and the one-shot low-battery crossing pop the bar.
        if (!chargingFlipped && !saverFlipped && !(lowNow && !lowBefore)) return;

        bool low = lowNow && !lowBefore;
        string title = cur.Charging ? "Charging" : cur.EnergySaver ? "Battery saver" : "Battery";
        string text = $"{Math.Round(cur.Percent)}%" + (cur.TimeRemaining is null ? "" : $" · {cur.TimeRemaining}");
        _bus.Raise(BarEvent.Create(BarEventKind.Battery, title, text,
            value: cur.Percent / 100.0, valueText: text, payload: cur,
            priority: low ? 95 : 40, displayDuration: TimeSpan.FromSeconds(low ? 6 : 3)));
    }

    public static BatteryInfo Query()
    {
        try
        {
            if (NativeMethods.GetSystemPowerStatus(out var spsi))
            {
                double percent = spsi.BatteryLifePercent == 255 ? 0 : spsi.BatteryLifePercent;
                bool charging = (spsi.ACLineStatus == 1);
                bool saver = false;
                try { saver = Windows.System.Power.PowerManager.EnergySaverStatus ==
                             Windows.System.Power.EnergySaverStatus.On; } catch { }

                string? remaining = null;
                if (!charging && spsi.BatteryLifeTime != -1 && spsi.BatteryLifeTime > 0)
                {
                    var ts = TimeSpan.FromSeconds(spsi.BatteryLifeTime);
                    remaining = $"{(int)ts.TotalHours}h {ts.Minutes:D2}m left";
                }
                return new BatteryInfo(percent, charging, saver, remaining);
            }
        }
        catch (Exception ex) { Log.Debug("Battery query failed", ("error", ex.Message)); }
        return new BatteryInfo(0, false, false, null);
    }

    public void Dispose()
    {
        try { _pollTimer?.Dispose(); } catch { }
        try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        try { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
    }
}
