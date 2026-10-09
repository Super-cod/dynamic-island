using System.Net.NetworkInformation;
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
            var spsi = new SYSTEM_POWER_STATUS();
            if (GetSystemPowerStatus(ref spsi))
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS sps);

    public void Dispose()
    {
        try { _pollTimer?.Dispose(); } catch { }
        try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        try { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
    }
}

/// <summary>SignalBars: 0–4 from real RSSI, or -1 = signal unknown (no bars shown).</summary>
public sealed record NetworkInfo(string Kind, string? Name, int SignalBars, bool Connected, bool Internet);

public sealed class NetworkService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private NetworkInfo _last = new("None", null, 0, false, false);
    private bool _eventHooked;

    public event Action<NetworkInfo>? NetworkChanged;

    public NetworkService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public void Start()
    {
        try
        {
            // WinRT event-driven network status change — no polling.
            Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
            _eventHooked = true;
        }
        catch (Exception ex) { Log.Warning("WinRT NetworkStatusChanged failed", ("error", ex.Message)); }

        var cur = Query();
        _last = cur;
        NetworkChanged?.Invoke(cur);
    }

    private void OnNetworkStatusChanged(object sender)
    {
        var cur = Query();
        bool meaningful = cur.Kind != _last.Kind || cur.Name != _last.Name ||
            cur.Connected != _last.Connected || cur.Internet != _last.Internet;

        if (!meaningful)
        {
            // Signal-strength jitter only: refresh state quietly, never pop the bar.
            if (cur.SignalBars != _last.SignalBars)
            {
                _last = cur;
                NetworkChanged?.Invoke(cur);
            }
            return;
        }

        _last = cur;
        NetworkChanged?.Invoke(cur);
        RaiseEvent(cur, changed: true);
    }

    private void RaiseEvent(NetworkInfo info, bool changed)
    {
        if (!_settings.Current.FeatureNetwork || !changed) return;
        string title;
        string text = info.Name ?? (info.Connected ? "Connected" : "Disconnected");
        int priority = 30;
        if (!info.Connected) { title = "Network"; text = "Disconnected"; priority = 85; }
        else title = info.Kind;

        _bus.Raise(BarEvent.Create(BarEventKind.Network, title, text,
            value: info.SignalBars / 4.0, valueText: info.Kind, payload: info,
            priority: priority, displayDuration: TimeSpan.FromSeconds(3)));
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
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr handle) != 0) return null;
            try
            {
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr list) != 0 || list == IntPtr.Zero)
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
                        if (WlanQueryInterface(handle, ref guid, 9 /* wlan_intf_opcode_rssi */,
                                IntPtr.Zero, out uint size, out IntPtr data, IntPtr.Zero) == 0
                            && data != IntPtr.Zero)
                        {
                            try { return System.Runtime.InteropServices.Marshal.ReadInt32(data); }
                            finally { WlanFreeMemory(data); }
                        }
                    }
                    return null;
                }
                finally { WlanFreeMemory(list); }
            }
            finally { WlanCloseHandle(handle, IntPtr.Zero); }
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

    [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved,
        out uint negotiatedVersion, out IntPtr handle);
    [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr handle, ref Guid guid, int opcode,
        IntPtr reserved, out uint dataSize, out IntPtr data, IntPtr dataType);
    [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
    [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    public void Dispose()
    {
        if (_eventHooked)
        {
            try { Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged; } catch { }
        }
    }
}

public sealed record BluetoothDeviceInfo(string Name, string Kind, bool Connected, string Id);

public sealed class BluetoothService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private Windows.Devices.Enumeration.DeviceWatcher? _watcher;
    private readonly Dictionary<string, BluetoothDeviceInfo> _devices = new();
    private bool _enumerated;

    public event Action<BluetoothDeviceInfo, bool>? DeviceChanged; // device, connected
    public IReadOnlyCollection<BluetoothDeviceInfo> KnownDevices => _devices.Values;

    public BluetoothService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public void Start()
    {
        try
        {
            // DeviceWatcher is event-driven: fires on pairing, connect (Added/Removed), update.
            string aqs = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            _watcher = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(aqs);
            _watcher.Added += OnDeviceAdded;
            _watcher.Removed += OnDeviceRemoved;
            _watcher.Updated += OnDeviceUpdated;
            // The first Added sweep is just "these devices exist" — populate silently.
            _watcher.EnumerationCompleted += (_, _) => _enumerated = true;
            _watcher.Start();
            Log.Info("Bluetooth watcher started");
        }
        catch (Exception ex)
        {
            Log.Warning("Bluetooth unavailable", ("error", ex.Message));
        }
    }

    private void OnDeviceAdded(Windows.Devices.Enumeration.DeviceWatcher sender,
        Windows.Devices.Enumeration.DeviceInformation info)
    {
        var dev = ToInfo(info, connected: true);
        if (dev is null) return;
        _devices[info.Id] = dev;
        DeviceChanged?.Invoke(dev, true);
        if (_settings.Current.FeatureBluetooth && _enumerated)
            _bus.Raise(BarEvent.Create(BarEventKind.Bluetooth, dev.Name, "Connected",
                payload: dev, priority: 45, displayDuration: TimeSpan.FromSeconds(3)));
    }

    private void OnDeviceRemoved(Windows.Devices.Enumeration.DeviceWatcher sender,
        Windows.Devices.Enumeration.DeviceInformationUpdate info)
    {
        if (!_devices.Remove(info.Id, out var dev)) return;
        DeviceChanged?.Invoke(dev, false);
        if (_settings.Current.FeatureBluetooth)
            _bus.Raise(BarEvent.Create(BarEventKind.Bluetooth, dev.Name, "Disconnected",
                payload: dev, priority: 45, displayDuration: TimeSpan.FromSeconds(3)));
    }

    private async void OnDeviceUpdated(Windows.Devices.Enumeration.DeviceWatcher sender,
        Windows.Devices.Enumeration.DeviceInformationUpdate info)
    {
        // Connection state changes surface as Updated; refresh name/status cheaply.
        try
        {
            var current = await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(info.Id);
            var dev = ToInfo(current, connected: true);
            if (dev is null) return;
            bool isNew = !_devices.ContainsKey(info.Id);
            _devices[info.Id] = dev;
            if (isNew) { DeviceChanged?.Invoke(dev, true); }
        }
        catch { /* device may be gone */ }
    }

    private static BluetoothDeviceInfo? ToInfo(Windows.Devices.Enumeration.DeviceInformation info, bool connected)
    {
        try
        {
            string name = string.IsNullOrWhiteSpace(info.Name) ? "Bluetooth device" : info.Name;
            string kind = Classify(name);
            return new BluetoothDeviceInfo(name, kind, connected, info.Id);
        }
        catch { return null; }
    }

    private static string Classify(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("airpod") || n.Contains("headphone") || n.Contains("earphone") ||
            n.Contains("buds") || n.Contains("headset") || n.Contains("speaker")) return "Audio";
        if (n.Contains("xbox") || n.Contains("controller") || n.Contains("gamepad")) return "Gamepad";
        if (n.Contains("mouse")) return "Mouse";
        if (n.Contains("keyboard")) return "Keyboard";
        if (n.Contains("watch")) return "Watch";
        return "Device";
    }

    public void Dispose()
    {
        try
        {
            if (_watcher is not null)
            {
                _watcher.Added -= OnDeviceAdded;
                _watcher.Removed -= OnDeviceRemoved;
                _watcher.Updated -= OnDeviceUpdated;
                if (_watcher.Status == Windows.Devices.Enumeration.DeviceWatcherStatus.Started)
                    _watcher.Stop();
            }
        }
        catch { }
    }
}
