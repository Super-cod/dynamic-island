using System.Runtime.InteropServices;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Interop;
using WindowsDynamicBar.Services.Settings;

namespace WindowsDynamicBar.Services.SystemInfo;

public sealed record BatteryInfo(double Percent, bool Charging, bool EnergySaver, string? TimeRemaining, bool HasBattery = true);

internal interface IBatteryWatcher : IDisposable
{
    event Action? PowerStatusChanged;
    event Action? SessionUnlocked;
    void Start();
    void Stop();
}

internal sealed class SystemEventsBatteryWatcher : IBatteryWatcher
{
    private readonly object _gate = new();
    private Microsoft.Win32.PowerModeChangedEventHandler? _powerHandler;
    private Microsoft.Win32.SessionSwitchEventHandler? _sessionHandler;
    private bool _hooked;

    private readonly Action<Microsoft.Win32.PowerModeChangedEventHandler> _addPower;
    private readonly Action<Microsoft.Win32.PowerModeChangedEventHandler> _removePower;
    private readonly Action<Microsoft.Win32.SessionSwitchEventHandler> _addSession;
    private readonly Action<Microsoft.Win32.SessionSwitchEventHandler> _removeSession;

    public event Action? PowerStatusChanged;
    public event Action? SessionUnlocked;

    internal bool IsHooked
    {
        get { lock (_gate) return _hooked; }
    }

    public SystemEventsBatteryWatcher()
        : this(null, null, null, null)
    {
    }

    internal SystemEventsBatteryWatcher(
        Action<Microsoft.Win32.PowerModeChangedEventHandler>? addPower = null,
        Action<Microsoft.Win32.PowerModeChangedEventHandler>? removePower = null,
        Action<Microsoft.Win32.SessionSwitchEventHandler>? addSession = null,
        Action<Microsoft.Win32.SessionSwitchEventHandler>? removeSession = null)
    {
        _addPower = addPower ?? (h => Microsoft.Win32.SystemEvents.PowerModeChanged += h);
        _removePower = removePower ?? (h => Microsoft.Win32.SystemEvents.PowerModeChanged -= h);
        _addSession = addSession ?? (h => Microsoft.Win32.SystemEvents.SessionSwitch += h);
        _removeSession = removeSession ?? (h => Microsoft.Win32.SystemEvents.SessionSwitch -= h);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_hooked) return;
            Microsoft.Win32.PowerModeChangedEventHandler pHandler = (_, e) =>
            {
                if (e.Mode is Microsoft.Win32.PowerModes.Resume or Microsoft.Win32.PowerModes.StatusChange)
                {
                    PowerStatusChanged?.Invoke();
                }
            };
            Microsoft.Win32.SessionSwitchEventHandler sHandler = (_, e) =>
            {
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock)
                {
                    SessionUnlocked?.Invoke();
                }
            };

            try
            {
                _addPower(pHandler);
                _powerHandler = pHandler;

                _addSession(sHandler);
                _sessionHandler = sHandler;

                _hooked = true;
            }
            catch (Exception ex)
            {
                UnsubscribeAllLocked();
                Log.Warning("SystemEvents subscription failed", ("error", ex.Message));
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            UnsubscribeAllLocked();
        }
    }

    private void UnsubscribeAllLocked()
    {
        if (!_hooked && _powerHandler is null && _sessionHandler is null) return;

        try
        {
            if (_powerHandler is not null)
            {
                try
                {
                    _removePower(_powerHandler);
                }
                catch (Exception ex)
                {
                    Log.Debug("Error unhooking PowerModeChanged", ("error", ex.Message));
                }
            }
            if (_sessionHandler is not null)
            {
                try
                {
                    _removeSession(_sessionHandler);
                }
                catch (Exception ex)
                {
                    Log.Debug("Error unhooking SessionSwitch", ("error", ex.Message));
                }
            }
        }
        finally
        {
            _powerHandler = null;
            _sessionHandler = null;
            _hooked = false;
        }
    }

    public void Dispose() => Stop();
}

public sealed class BatteryService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private readonly Func<BatteryInfo> _queryBattery;
    private readonly Func<IBatteryWatcher> _watcherFactory;
    private readonly Func<Action, TimeSpan, IDisposable> _timerFactory;
    private readonly object _sync = new();

    private BatteryInfo _last = new(0, false, false, null, false);
    private IDisposable? _pollTimer;
    private IBatteryWatcher? _watcher;
    private bool _started;
    private bool _disposed;

    public event Action<BatteryInfo>? BatteryChanged;

    public BatteryInfo Current
    {
        get { lock (_sync) return _last; }
    }

    public bool IsStarted
    {
        get { lock (_sync) return _started; }
    }

    public bool IsDisposed
    {
        get { lock (_sync) return _disposed; }
    }

    internal bool HasTimer
    {
        get { lock (_sync) return _pollTimer is not null; }
    }

    internal bool HasWatcher
    {
        get { lock (_sync) return _watcher is not null; }
    }

    public BatteryService(EventBus bus, SettingsService settings)
        : this(bus, settings, queryBattery: null, watcherFactory: null, timerFactory: null)
    {
    }

    internal BatteryService(
        EventBus bus,
        SettingsService settings,
        Func<BatteryInfo>? queryBattery,
        Func<IBatteryWatcher>? watcherFactory = null,
        Func<Action, TimeSpan, IDisposable>? timerFactory = null)
    {
        _bus = bus;
        _settings = settings;
        _queryBattery = queryBattery ?? Query;
        _watcherFactory = watcherFactory ?? (() => new SystemEventsBatteryWatcher());
        _timerFactory = timerFactory ?? ((callback, interval) => new System.Threading.Timer(_ => callback(), null, interval, interval));
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _started) return;
            _started = true;
        }

        IBatteryWatcher? watcher = null;
        IDisposable? timer = null;
        try
        {
            var initial = _queryBattery();

            Action<BatteryInfo>? initialNotify = null;
            lock (_sync)
            {
                if (_disposed) return;
                _last = initial;
                initialNotify = BatteryChanged;
            }

            // Gated dispatch outside locks
            lock (_sync)
            {
                if (_disposed) return;
            }
            initialNotify?.Invoke(initial);

            watcher = _watcherFactory();
            watcher.PowerStatusChanged += OnPowerStatusChanged;
            watcher.SessionUnlocked += OnSessionUnlocked;

            lock (_sync)
            {
                if (_disposed)
                {
                    CleanupResources(watcher, null);
                    return;
                }
            }

            watcher.Start();

            lock (_sync)
            {
                if (_disposed)
                {
                    CleanupResources(watcher, null);
                    return;
                }
            }

            int pollMs = _settings.Current.PerformanceMode switch
            {
                PerformanceMode.LowPower => 120_000,
                PerformanceMode.Performance => 20_000,
                _ => 45_000
            };

            timer = _timerFactory(OnPollTick, TimeSpan.FromMilliseconds(pollMs));

            // Deferred publication pattern: assign _watcher and _pollTimer only after
            // successful initialization and verify that disposal did not race.
            lock (_sync)
            {
                if (_disposed)
                {
                    CleanupResources(watcher, timer);
                    return;
                }
                _watcher = watcher;
                _pollTimer = timer;
            }

            Log.Info("Battery service started", ("percent", initial.Percent), ("charging", initial.Charging));
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _started = false;
                _watcher = null;
                _pollTimer = null;
            }
            CleanupResources(watcher, timer);
            Log.Warning("Battery service failed to start", ("error", ex.Message));
        }
    }

    private void OnPollTick() => ProcessUpdateCore(isPoll: true, manualSnapshot: null);
    private void OnPowerStatusChanged() => ProcessUpdateCore(isPoll: false, manualSnapshot: null);
    private void OnSessionUnlocked() => ProcessSessionUnlockCore(manualSnapshot: null);

    internal void OnPollTickCore(BatteryInfo? manualSnapshot = null)
        => ProcessUpdateCore(isPoll: true, manualSnapshot: manualSnapshot);

    internal void OnPowerStatusChangedCore(BatteryInfo? manualSnapshot = null)
        => ProcessUpdateCore(isPoll: false, manualSnapshot: manualSnapshot);

    internal void OnSessionUnlockedCore(BatteryInfo? manualSnapshot = null)
        => ProcessSessionUnlockCore(manualSnapshot: manualSnapshot);

    private void ProcessUpdateCore(bool isPoll, BatteryInfo? manualSnapshot)
    {
        try
        {
            lock (_sync)
            {
                if (_disposed) return;
            }

            var cur = manualSnapshot ?? _queryBattery();

            Action<BatteryInfo>? notify = null;
            BarEvent? eventToRaise = null;
            BatteryInfo? stateToNotify = null;

            lock (_sync)
            {
                if (_disposed) return;

                var prev = _last;
                bool changed;
                if (isPoll)
                {
                    changed = cur.HasBattery != prev.HasBattery ||
                              Math.Abs(cur.Percent - prev.Percent) >= 1 ||
                              cur.Charging != prev.Charging ||
                              cur.EnergySaver != prev.EnergySaver;
                }
                else
                {
                    changed = cur.HasBattery != prev.HasBattery ||
                              cur.Percent != prev.Percent ||
                              cur.Charging != prev.Charging ||
                              cur.EnergySaver != prev.EnergySaver;
                }

                if (!changed) return;

                _last = cur;
                stateToNotify = cur;
                notify = BatteryChanged;

                if (_settings.Current.FeatureBattery)
                {
                    eventToRaise = CreateBarEvent(prev, cur);
                }
            }

            // Gated dispatch outside locks.
            // Publication guarantee: A check immediately before an external invocation
            // does not eliminate the race between that check and the invocation (if Dispose()
            // is called concurrently on another thread after the check passes). However,
            // this check guarantees that any callback reaching this point after Dispose() has
            // acquired the lock and set _disposed = true is suppressed from notifying or raising events.
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

            if (!isPoll)
            {
                Log.Info("Power state change", ("percent", cur.Percent), ("charging", cur.Charging));
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Battery status change error", ("error", ex.Message));
        }
    }

    private void ProcessSessionUnlockCore(BatteryInfo? manualSnapshot)
    {
        try
        {
            lock (_sync)
            {
                if (_disposed) return;
            }

            var cur = manualSnapshot ?? _queryBattery();

            Action<BatteryInfo>? notify = null;
            BatteryInfo? stateToNotify = null;

            lock (_sync)
            {
                if (_disposed) return;

                _last = cur;
                stateToNotify = cur;
                notify = BatteryChanged;
            }

            lock (_sync)
            {
                if (_disposed) return;
            }
            notify?.Invoke(stateToNotify!);
        }
        catch (Exception ex)
        {
            Log.Debug("Battery session unlock error", ("error", ex.Message));
        }
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

    internal static BarEvent? CreateBarEvent(BatteryInfo prev, BatteryInfo cur)
    {
        if (!ShouldRaiseBatteryAlert(prev, cur)) return null;

        bool lowNow = cur.Percent <= 20 && !cur.Charging;
        bool lowBefore = prev.HasBattery && prev.Percent <= 20 && !prev.Charging;
        bool low = lowNow && !lowBefore;

        string title = cur.Charging ? "Charging" : cur.EnergySaver ? "Battery saver" : "Battery";
        string text = $"{Math.Round(cur.Percent)}%" + (cur.TimeRemaining is null ? "" : $" · {cur.TimeRemaining}");
        return BarEvent.Create(BarEventKind.Battery, title, text,
            value: cur.Percent / 100.0, valueText: text, payload: cur,
            priority: low ? 95 : 40, displayDuration: TimeSpan.FromSeconds(low ? 6 : 3));
    }

    internal void RaiseBatteryEvent(BatteryInfo prev, BatteryInfo cur)
    {
        if (!_settings.Current.FeatureBattery) return;
        var barEvent = CreateBarEvent(prev, cur);
        if (barEvent is not null)
        {
            _bus.Raise(barEvent);
        }
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
        IBatteryWatcher? watcherToDispose;
        IDisposable? timerToDispose;

        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;

            watcherToDispose = _watcher;
            timerToDispose = _pollTimer;
            _watcher = null;
            _pollTimer = null;
        }

        CleanupResources(watcherToDispose, timerToDispose);
    }

    private void CleanupResources(IBatteryWatcher? watcher, IDisposable? timer)
    {
        if (timer is not null)
        {
            try { timer.Dispose(); } catch (Exception ex) { Log.Debug("Error disposing battery timer", ("error", ex.Message)); }
        }
        if (watcher is not null)
        {
            try
            {
                watcher.PowerStatusChanged -= OnPowerStatusChanged;
                watcher.SessionUnlocked -= OnSessionUnlocked;
                watcher.Stop();
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug("Error disposing battery watcher", ("error", ex.Message));
            }
        }
    }
}
