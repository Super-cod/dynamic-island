using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Interop;
using WindowsDynamicBar.Services.Settings;

namespace WindowsDynamicBar.Services.SystemInfo;

/// <summary>SignalBars: 0–4 from real RSSI, or -1 = signal unknown (no bars shown).</summary>
public sealed record NetworkInfo(string Kind, string? Name, int SignalBars, bool Connected, bool Internet);

internal interface INetworkWatcher : IDisposable
{
    event Action? StatusChanged;
    void Start();
    void Stop();
}

internal sealed class WinRtNetworkWatcher : INetworkWatcher
{
    private Windows.Networking.Connectivity.NetworkStatusChangedEventHandler? _handler;
    private readonly object _gate = new();
    private bool _hooked;
    public event Action? StatusChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_hooked) return;
            Windows.Networking.Connectivity.NetworkStatusChangedEventHandler handler = _ => StatusChanged?.Invoke();
            try
            {
                Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += handler;
                _handler = handler;
                _hooked = true;
            }
            catch (Exception ex)
            {
                Log.Warning("WinRT NetworkStatusChanged failed", ("error", ex.Message));
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_hooked || _handler is null) return;
            try
            {
                Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= _handler;
            }
            catch (Exception ex)
            {
                Log.Debug("Error unhooking NetworkStatusChanged", ("error", ex.Message));
            }
            finally
            {
                _handler = null;
                _hooked = false;
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

public sealed class NetworkService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private readonly Func<NetworkInfo> _queryNetwork;
    private readonly Func<INetworkWatcher> _watcherFactory;
    private readonly object _sync = new();

    private INetworkWatcher? _watcher;
    private NetworkInfo _last = new("None", null, 0, false, false);
    private bool _started;
    private bool _disposed;

    public event Action<NetworkInfo>? NetworkChanged;

    public NetworkInfo Current
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

    internal bool HasWatcher
    {
        get { lock (_sync) return _watcher is not null; }
    }

    public NetworkService(EventBus bus, SettingsService settings)
        : this(bus, settings, queryNetwork: null, watcherFactory: null)
    {
    }

    internal NetworkService(
        EventBus bus,
        SettingsService settings,
        Func<NetworkInfo>? queryNetwork,
        Func<INetworkWatcher>? watcherFactory)
    {
        _bus = bus;
        _settings = settings;
        _queryNetwork = queryNetwork ?? Query;
        _watcherFactory = watcherFactory ?? (() => new WinRtNetworkWatcher());
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _started) return;
            _started = true;
        }

        INetworkWatcher? watcher = null;
        try
        {
            var cur = _queryNetwork();

            Action<NetworkInfo>? initialNotify = null;
            lock (_sync)
            {
                if (_disposed) return;
                _last = cur;
                initialNotify = NetworkChanged;
            }

            // Gated dispatch outside locks
            lock (_sync)
            {
                if (_disposed) return;
            }
            initialNotify?.Invoke(cur);

            watcher = _watcherFactory();
            watcher.StatusChanged += OnNetworkStatusChanged;

            lock (_sync)
            {
                if (_disposed)
                {
                    CleanupWatcher(watcher);
                    return;
                }
            }

            watcher.Start();

            // Deferred publication pattern: assign _watcher only after watcher.Start()
            // succeeds and verify that disposal did not race with startup.
            lock (_sync)
            {
                if (_disposed)
                {
                    CleanupWatcher(watcher);
                    return;
                }
                _watcher = watcher;
            }

            Log.Info("Network service started", ("kind", cur.Kind), ("connected", cur.Connected));
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _started = false;
                _watcher = null;
            }
            CleanupWatcher(watcher);
            Log.Warning("Network service failed to start", ("error", ex.Message));
        }
    }

    private void OnNetworkStatusChanged() => OnNetworkStatusChangedCore(null);

    internal void OnNetworkStatusChangedCore(NetworkInfo? manualSnapshot = null)
    {
        try
        {
            lock (_sync)
            {
                if (_disposed) return;
            }

            var cur = manualSnapshot ?? _queryNetwork();

            Action<NetworkInfo>? notify = null;
            BarEvent? eventToRaise = null;
            NetworkInfo? stateToNotify = null;

            lock (_sync)
            {
                if (_disposed) return;

                bool meaningful = cur.Kind != _last.Kind || cur.Name != _last.Name ||
                    cur.Connected != _last.Connected || cur.Internet != _last.Internet;

                if (!meaningful)
                {
                    // Signal-strength jitter only: refresh state quietly, never pop the bar.
                    if (cur.SignalBars != _last.SignalBars)
                    {
                        _last = cur;
                        stateToNotify = cur;
                        notify = NetworkChanged;
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    _last = cur;
                    stateToNotify = cur;
                    notify = NetworkChanged;

                    if (_settings.Current.FeatureNetwork)
                    {
                        eventToRaise = CreateBarEvent(cur);
                    }
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
        }
        catch (Exception ex)
        {
            Log.Debug("Network status change error", ("error", ex.Message));
        }
    }

    internal static BarEvent CreateBarEvent(NetworkInfo info)
    {
        string title;
        string text = string.IsNullOrEmpty(info.Name)
            ? (info.Connected ? "Connected" : "Disconnected")
            : info.Name;
        int priority = 30;
        if (!info.Connected)
        {
            title = "Network";
            text = "Disconnected";
            priority = 85;
        }
        else
        {
            title = info.Kind;
        }

        return BarEvent.Create(BarEventKind.Network, title, text,
            value: info.SignalBars / 4.0, valueText: info.Kind, payload: info,
            priority: priority, displayDuration: TimeSpan.FromSeconds(3));
    }

    public static NetworkInfo Query()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return new NetworkInfo("None", null, 0, false, false);

            var level = profile.GetNetworkConnectivityLevel();
            bool internet = level == Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess;
            string name = profile.ProfileName ?? "";
            bool isWlan = profile.IsWlanConnectionProfile;
            string kind = isWlan ? "Wi-Fi"
                : profile.IsWwanConnectionProfile ? "Cellular"
                : name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ? "Ethernet"
                : "Network";

            int bars = isWlan ? RssiToBars(QueryWifiRssi()) : -1;
            return new NetworkInfo(kind, name, bars, level != Windows.Networking.Connectivity.NetworkConnectivityLevel.None, internet);
        }
        catch (Exception ex)
        {
            Log.Debug("Network query failed", ("error", ex.Message));
            return new NetworkInfo("None", null, 0, false, false);
        }
    }

    internal static int? QueryWifiRssi()
    {
        // Real RSSI via wlanapi.dll (wlan_intf_opcode_rssi, dBm). null = unknown —
        // never fabricate a signal level.
        try
        {
            if (NativeMethods.WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr handle) != 0) return null;
            try
            {
                if (NativeMethods.WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr list) != 0 || list == IntPtr.Zero)
                    return null;
                try
                {
                    uint count = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(list);
                    // WLAN_INTERFACE_INFO_LIST: two DWORDs, then WLAN_INTERFACE_INFO[]
                    // (GUID 16 + WCHAR[256] 512 + state 4 = 532 bytes each).
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr entry = IntPtr.Add(list, 8 + i * 532);
                        int state = System.Runtime.InteropServices.Marshal.ReadInt32(entry, 16 + 512);
                        if (state != 1 /* wlan_interface_state_connected */) continue;
                        Guid guid = System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(entry);
                        if (NativeMethods.WlanQueryInterface(handle, ref guid, 9 /* wlan_intf_opcode_rssi */,
                                IntPtr.Zero, out uint size, out IntPtr data, IntPtr.Zero) == 0
                            && data != IntPtr.Zero)
                        {
                            try { return System.Runtime.InteropServices.Marshal.ReadInt32(data); }
                            finally { NativeMethods.WlanFreeMemory(data); }
                        }
                    }
                    return null;
                }
                finally { NativeMethods.WlanFreeMemory(list); }
            }
            finally { NativeMethods.WlanCloseHandle(handle, IntPtr.Zero); }
        }
        catch { return null; }
    }

    /// <summary>dBm → 0–4 bars; -1 = unknown.</summary>
    internal static int RssiToBars(int? rssiDbm) => rssiDbm switch
    {
        null => -1,
        >= -50 => 4,
        >= -60 => 3,
        >= -70 => 2,
        >= -80 => 1,
        _ => 0,
    };

    public void Dispose()
    {
        INetworkWatcher? watcherToDispose;

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

    private void CleanupWatcher(INetworkWatcher? watcher)
    {
        if (watcher is null) return;
        try
        {
            watcher.StatusChanged -= OnNetworkStatusChanged;
            watcher.Stop();
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug("Error disposing network watcher", ("error", ex.Message));
        }
    }
}
