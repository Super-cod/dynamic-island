using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Interop;
using WindowsDynamicBar.Services.Settings;

namespace WindowsDynamicBar.Services.SystemInfo;

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
        if (_eventHooked)
        {
            try { Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged; } catch { }
        }
    }
}
