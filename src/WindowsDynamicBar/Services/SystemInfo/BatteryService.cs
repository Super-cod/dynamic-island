using System.Runtime.InteropServices;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Interop;

namespace WindowsDynamicBar.Services.SystemInfo;

public sealed record BatteryInfo(double Percent, bool Charging, bool EnergySaver, string? TimeRemaining, bool HasBattery = true);

public sealed class BatteryService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private readonly object _stateLock = new();
    private BatteryInfo _last = new(0, false, false, null, false);
    private System.Threading.Timer? _pollTimer;

    public event Action<BatteryInfo>? BatteryChanged;
    public BatteryInfo Current
    {
        get
        {
            lock (_stateLock)
            {
                return _last;
            }
        }
    }

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

        var initial = Query();
        lock (_stateLock)
        {
            _last = initial;
        }
        BatteryChanged?.Invoke(initial);

        int pollMs = _settings.Current.PerformanceMode switch
        {
            PerformanceMode.LowPower => 120_000,
            PerformanceMode.Performance => 20_000,
            _ => 45_000
        };
        _pollTimer = new System.Threading.Timer(_ =>
        {
            var cur = Query();
            BatteryInfo? toNotify = null;
            BatteryInfo? alertPrev = null;
            lock (_stateLock)
            {
                var prev = _last;
                if (cur.HasBattery != prev.HasBattery ||
                    Math.Abs(cur.Percent - prev.Percent) >= 1 ||
                    cur.Charging != prev.Charging ||
                    cur.EnergySaver != prev.EnergySaver)
                {
                    _last = cur;
                    toNotify = cur;
                    alertPrev = prev;
                }
            }
            if (toNotify != null)
            {
                BatteryChanged?.Invoke(toNotify);
                RaiseBatteryEvent(alertPrev!, toNotify);
            }
        }, null, pollMs, pollMs);
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        // Re-query on any power event (plug/unplug surfaces as StatusChange, wake as Resume)
        // so charging flips pop the bar immediately instead of at the next poll.
        if (e.Mode is not (Microsoft.Win32.PowerModes.Resume or Microsoft.Win32.PowerModes.StatusChange))
            return;
        var cur = Query();
        BatteryInfo? toNotify = null;
        BatteryInfo? alertPrev = null;
        lock (_stateLock)
        {
            var prev = _last;
            if (cur.HasBattery != prev.HasBattery ||
                cur.Percent != prev.Percent ||
                cur.Charging != prev.Charging ||
                cur.EnergySaver != prev.EnergySaver)
            {
                _last = cur;
                toNotify = cur;
                alertPrev = prev;
            }
        }
        if (toNotify != null)
        {
            BatteryChanged?.Invoke(toNotify);
            RaiseBatteryEvent(alertPrev!, toNotify);
            Log.Info("Power state change", ("percent", toNotify.Percent), ("charging", toNotify.Charging));
        }
    }

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason != Microsoft.Win32.SessionSwitchReason.SessionUnlock) return;
        var cur = Query();
        BatteryInfo? toNotify = null;
        lock (_stateLock)
        {
            _last = cur;
            toNotify = cur;
        }
        BatteryChanged?.Invoke(toNotify);
    }

    internal static bool ShouldRaiseBatteryAlert(BatteryInfo prev, BatteryInfo cur)
    {
        if (!cur.HasBattery) return false;

        bool chargingFlipped = prev.HasBattery && cur.Charging != prev.Charging;
        bool saverFlipped = prev.HasBattery && cur.EnergySaver != prev.EnergySaver;
        bool lowNow = cur.Percent <= 20 && !cur.Charging;
        bool lowBefore = prev.HasBattery && prev.Percent <= 20 && !prev.Charging;

        // Routine percent drift is not worth interrupting the user for — only
        // charging/saver flips and the one-shot low-battery crossing pop the bar.
        return chargingFlipped || saverFlipped || (lowNow && !lowBefore);
    }

    internal void RaiseBatteryEvent(BatteryInfo prev, BatteryInfo cur)
    {
        if (!_settings.Current.FeatureBattery) return;
        if (!ShouldRaiseBatteryAlert(prev, cur)) return;

        bool lowNow = cur.Percent <= 20 && !cur.Charging;
        bool lowBefore = prev.HasBattery && prev.Percent <= 20 && !prev.Charging;
        bool low = lowNow && !lowBefore;

        string title = cur.Charging ? "Charging" : cur.EnergySaver ? "Battery saver" : "Battery";
        string text = $"{Math.Round(cur.Percent)}%" + (cur.TimeRemaining is null ? "" : $" · {cur.TimeRemaining}");
        _bus.Raise(BarEvent.Create(BarEventKind.Battery, title, text,
            value: cur.Percent / 100.0, valueText: text, payload: cur,
            priority: low ? 95 : 40, displayDuration: TimeSpan.FromSeconds(low ? 6 : 3)));
    }

    internal static BatteryInfo FromPowerStatus(NativeMethods.SYSTEM_POWER_STATUS spsi, bool energySaver)
    {
        // BatteryFlag 128 = no system battery, 255 = unknown status.
        // BatteryLifePercent 255 = unknown status.
        bool hasBattery = spsi.BatteryFlag != 128 && spsi.BatteryFlag != 255 && spsi.BatteryLifePercent != 255;
        double percent = hasBattery ? Math.Clamp((double)spsi.BatteryLifePercent, 0, 100) : 0;
        bool charging = (spsi.ACLineStatus == 1);
        string? remaining = null;
        if (hasBattery && !charging && spsi.BatteryLifeTime != -1 && spsi.BatteryLifeTime > 0)
        {
            var ts = TimeSpan.FromSeconds(spsi.BatteryLifeTime);
            remaining = $"{(int)ts.TotalHours}h {ts.Minutes:D2}m left";
        }
        return new BatteryInfo(percent, charging, energySaver, remaining, hasBattery);
    }

    public static BatteryInfo Query()
    {
        try
        {
            if (NativeMethods.GetSystemPowerStatus(out var spsi))
            {
                bool saver = false;
                try { saver = Windows.System.Power.PowerManager.EnergySaverStatus ==
                             Windows.System.Power.EnergySaverStatus.On; } catch { }
                return FromPowerStatus(spsi, saver);
            }
        }
        catch (Exception ex) { Log.Debug("Battery query failed", ("error", ex.Message)); }
        return new BatteryInfo(0, false, false, null, false);
    }

    public void Dispose()
    {
        try { _pollTimer?.Dispose(); } catch { }
        try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        try { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
    }
}
