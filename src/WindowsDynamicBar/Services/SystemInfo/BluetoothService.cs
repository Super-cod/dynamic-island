using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Services.Settings;

namespace WindowsDynamicBar.Services.SystemInfo;

public sealed record BluetoothDeviceInfo(string Name, string Kind, bool Connected, string Id);

internal interface IBluetoothWatcher
{
    Windows.Devices.Enumeration.DeviceWatcherStatus Status { get; }
    event Action<string, string?, bool>? DeviceAdded;
    event Action<string>? DeviceRemoved;
    event Action<string, bool?>? DeviceUpdated;
    event Action? EnumerationCompleted;
    void Start();
    void Stop();
}

internal sealed class WinRtBluetoothWatcher : IBluetoothWatcher
{
    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    private readonly Windows.Devices.Enumeration.DeviceWatcher _watcher;

    public Windows.Devices.Enumeration.DeviceWatcherStatus Status => _watcher.Status;

    public event Action<string, string?, bool>? DeviceAdded;
    public event Action<string>? DeviceRemoved;
    public event Action<string, bool?>? DeviceUpdated;
    public event Action? EnumerationCompleted;

    public WinRtBluetoothWatcher()
    {
        string aqs = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        _watcher = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(aqs, new[] { IsConnectedKey },
            Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint);

        _watcher.Added += OnAdded;
        _watcher.Removed += OnRemoved;
        _watcher.Updated += OnUpdated;
        _watcher.EnumerationCompleted += OnEnumerationCompleted;
    }

    private void OnAdded(Windows.Devices.Enumeration.DeviceWatcher s, Windows.Devices.Enumeration.DeviceInformation info) =>
        DeviceAdded?.Invoke(info.Id, info.Name, IsConnected(info.Properties) ?? false);

    private void OnRemoved(Windows.Devices.Enumeration.DeviceWatcher s, Windows.Devices.Enumeration.DeviceInformationUpdate info) =>
        DeviceRemoved?.Invoke(info.Id);

    private void OnUpdated(Windows.Devices.Enumeration.DeviceWatcher s, Windows.Devices.Enumeration.DeviceInformationUpdate info) =>
        DeviceUpdated?.Invoke(info.Id, IsConnected(info.Properties));

    private void OnEnumerationCompleted(Windows.Devices.Enumeration.DeviceWatcher s, object e) =>
        EnumerationCompleted?.Invoke();

    public void Start() => _watcher.Start();

    public void Stop()
    {
        try
        {
            _watcher.Added -= OnAdded;
            _watcher.Removed -= OnRemoved;
            _watcher.Updated -= OnUpdated;
            _watcher.EnumerationCompleted -= OnEnumerationCompleted;
            _watcher.Stop();
        }
        catch { }
    }

    private static bool? IsConnected(IReadOnlyDictionary<string, object> props) =>
        props.TryGetValue(IsConnectedKey, out var v) && v is bool b ? b : null;
}

public sealed class BluetoothService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private readonly Func<IBluetoothWatcher> _watcherFactory;
    private readonly object _stateLock = new();

    private IBluetoothWatcher? _watcher;
    private readonly Dictionary<string, BluetoothDeviceInfo> _devices = new();
    private bool _enumerated;
    private bool _started;
    private bool _disposed;

    public event Action<BluetoothDeviceInfo, bool>? DeviceChanged; // device, connected
    public IReadOnlyCollection<BluetoothDeviceInfo> KnownDevices
    {
        get
        {
            lock (_stateLock)
            {
                return _devices.Values.ToList();
            }
        }
    }

    public BluetoothService(EventBus bus, SettingsService settings)
        : this(bus, settings, () => new WinRtBluetoothWatcher())
    {
    }

    internal BluetoothService(EventBus bus, SettingsService settings, Func<IBluetoothWatcher> watcherFactory)
    {
        _bus = bus;
        _settings = settings;
        _watcherFactory = watcherFactory;
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_disposed || _started) return;
            _started = true;
        }

        try
        {
            var watcher = _watcherFactory();
            lock (_stateLock)
            {
                if (_disposed)
                {
                    TryStopWatcher(watcher);
                    return;
                }
                _watcher = watcher;
                _watcher.DeviceAdded += OnDeviceAdded;
                _watcher.DeviceRemoved += OnDeviceRemoved;
                _watcher.DeviceUpdated += OnDeviceUpdated;
                _watcher.EnumerationCompleted += OnEnumerationCompleted;
            }

            watcher.Start();
            Log.Info("Bluetooth watcher started");
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _started = false;
                CleanupWatcherUnsafe();
            }
            Log.Warning("Bluetooth unavailable", ("error", ex.Message));
        }
    }

    private void OnDeviceAdded(string id, string? name, bool connected)
    {
        BluetoothDeviceInfo? dev = null;
        bool shouldAnnounce = false;

        lock (_stateLock)
        {
            if (_disposed) return;
            dev = ToInfo(name, id, connected);
            if (dev is null) return;
            _devices[id] = dev;
            shouldAnnounce = _enumerated && dev.Connected;
        }

        if (shouldAnnounce && dev is not null)
        {
            Announce(dev);
        }
    }

    private void OnDeviceRemoved(string id)
    {
        BluetoothDeviceInfo? dev = null;

        lock (_stateLock)
        {
            if (_disposed) return;
            if (!_devices.Remove(id, out dev)) return;
        }

        if (dev is not null && dev.Connected)
        {
            Announce(dev with { Connected = false });
        }
    }

    private void OnDeviceUpdated(string id, bool? connected)
    {
        if (connected is not bool isConnected) return; // name/icon updates only

        BluetoothDeviceInfo? next = null;
        bool shouldAnnounce = false;

        lock (_stateLock)
        {
            if (_disposed) return;
            if (!_devices.TryGetValue(id, out var dev) || dev.Connected == isConnected) return;
            next = dev with { Connected = isConnected };
            _devices[id] = next;
            shouldAnnounce = _enumerated;
        }

        if (shouldAnnounce && next is not null)
        {
            Announce(next);
        }
    }

    private void OnEnumerationCompleted()
    {
        int pairedCount = 0;
        int connectedCount = 0;

        lock (_stateLock)
        {
            if (_disposed) return;
            _enumerated = true;
            pairedCount = _devices.Count;
            connectedCount = _devices.Values.Count(d => d.Connected);
        }

        Log.Info("Bluetooth devices", ("paired", pairedCount), ("connected", connectedCount));
    }

    private void Announce(BluetoothDeviceInfo dev)
    {
        DeviceChanged?.Invoke(dev, dev.Connected);
        if (_settings.Current.FeatureBluetooth)
        {
            _bus.Raise(BarEvent.Create(BarEventKind.Bluetooth, dev.Name, dev.Connected ? "Connected" : "Disconnected",
                payload: dev, priority: 45, displayDuration: TimeSpan.FromSeconds(3)));
        }
    }

    internal static BluetoothDeviceInfo? ToInfo(string? rawName, string id, bool connected)
    {
        string name = string.IsNullOrWhiteSpace(rawName) ? "Bluetooth device" : rawName;
        return new BluetoothDeviceInfo(name, Classify(name), connected, id);
    }

    internal static string Classify(string name)
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
        IBluetoothWatcher? watcherToStop;
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;
            watcherToStop = CleanupWatcherUnsafe();
        }

        TryStopWatcher(watcherToStop);
    }

    private IBluetoothWatcher? CleanupWatcherUnsafe()
    {
        if (_watcher is null) return null;

        var watcher = _watcher;
        _watcher = null;

        watcher.DeviceAdded -= OnDeviceAdded;
        watcher.DeviceRemoved -= OnDeviceRemoved;
        watcher.DeviceUpdated -= OnDeviceUpdated;
        watcher.EnumerationCompleted -= OnEnumerationCompleted;

        return watcher;
    }

    private static void TryStopWatcher(IBluetoothWatcher? watcher)
    {
        if (watcher is null) return;
        try
        {
            var status = watcher.Status;
            if (status == Windows.Devices.Enumeration.DeviceWatcherStatus.Started ||
                status == Windows.Devices.Enumeration.DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }
        catch { }
    }
}
