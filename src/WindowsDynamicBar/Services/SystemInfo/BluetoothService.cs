using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.Services.SystemInfo;

public sealed record BluetoothDeviceInfo(string Name, string Kind, bool Connected, string Id);

public sealed class BluetoothService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private Windows.Devices.Enumeration.DeviceWatcher? _watcher;
    private readonly Dictionary<string, BluetoothDeviceInfo> _devices = new();
    private bool _enumerated;

    public event Action<BluetoothDeviceInfo, bool>? DeviceChanged; // device, connected
    public IReadOnlyCollection<BluetoothDeviceInfo> KnownDevices { get { lock (_devices) return _devices.Values.ToList(); } }

    public BluetoothService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";

    public void Start()
    {
        try
        {
            // The watcher lists PAIRED devices: Added/Removed mean paired/unpaired. Connecting or
            // disconnecting a paired headset only changes System.Devices.Aep.IsConnected (Updated).
            string aqs = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            _watcher = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(aqs, new[] { IsConnectedKey },
                Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += OnDeviceAdded;
            _watcher.Removed += OnDeviceRemoved;
            _watcher.Updated += OnDeviceUpdated;
            // The first Added sweep is just "these devices exist" — populate silently.
            _watcher.EnumerationCompleted += (_, _) =>
            {
                _enumerated = true;
                lock (_devices)
                    Log.Info("Bluetooth devices", ("paired", _devices.Count), ("connected", _devices.Values.Count(d => d.Connected)));
            };
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
        var dev = ToInfo(info.Name, info.Id, IsConnected(info.Properties) ?? false);
        if (dev is null) return;
        lock (_devices) _devices[info.Id] = dev;
        // A newly paired device usually connects at once; that is the moment worth showing.
        if (_enumerated && dev.Connected) Announce(dev);
    }

    private void OnDeviceRemoved(Windows.Devices.Enumeration.DeviceWatcher sender,
        Windows.Devices.Enumeration.DeviceInformationUpdate info)
    {
        BluetoothDeviceInfo? dev;
        lock (_devices) { if (!_devices.Remove(info.Id, out dev)) return; }
        if (dev.Connected) Announce(dev with { Connected = false });
    }

    private void OnDeviceUpdated(Windows.Devices.Enumeration.DeviceWatcher sender,
        Windows.Devices.Enumeration.DeviceInformationUpdate info)
    {
        if (IsConnected(info.Properties) is not bool connected) return;   // name/icon updates only
        BluetoothDeviceInfo? next;
        lock (_devices)
        {
            if (!_devices.TryGetValue(info.Id, out var dev) || dev.Connected == connected) return;
            next = dev with { Connected = connected };
            _devices[info.Id] = next;
        }
        if (_enumerated) Announce(next);
    }

    private void Announce(BluetoothDeviceInfo dev)
    {
        DeviceChanged?.Invoke(dev, dev.Connected);
        if (_settings.Current.FeatureBluetooth)
            _bus.Raise(BarEvent.Create(BarEventKind.Bluetooth, dev.Name, dev.Connected ? "Connected" : "Disconnected",
                payload: dev, priority: 45, displayDuration: TimeSpan.FromSeconds(3)));
    }

    private static bool? IsConnected(IReadOnlyDictionary<string, object> props) =>
        props.TryGetValue(IsConnectedKey, out var v) && v is bool b ? b : null;

    private static BluetoothDeviceInfo? ToInfo(string? rawName, string id, bool connected)
    {
        string name = string.IsNullOrWhiteSpace(rawName) ? "Bluetooth device" : rawName;
        return new BluetoothDeviceInfo(name, Classify(name), connected, id);
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
