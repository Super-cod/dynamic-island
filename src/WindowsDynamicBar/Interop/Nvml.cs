using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Interop;

/// <summary>Sensor values from NVML for one NVIDIA GPU; null = that sensor could not be read.</summary>
public sealed record NvmlReading(double? TempC, double? PowerW, double? ClockMhz, long? MemUsedBytes, long? MemTotalBytes);

/// <summary>
/// Minimal NVML binding (nvml.dll ships with the NVIDIA driver in System32). Loaded on first use
/// only; if the DLL or a function is missing every call returns null and the UI hides the rows.
/// Call from one background thread only (the telemetry sampler). Querying an NVIDIA laptop GPU
/// can wake it from its low-power state, so callers only read while that GPU's panel is open.
/// </summary>
public sealed unsafe class Nvml : IDisposable
{
    private const int NVML_SUCCESS = 0;
    private const int NVML_TEMPERATURE_GPU = 0;
    private const int NVML_CLOCK_GRAPHICS = 0;

    private IntPtr _lib;
    private bool _initTried;
    private bool _ready;

    private delegate* unmanaged<int> _init;
    private delegate* unmanaged<int> _shutdown;
    private delegate* unmanaged<uint*, int> _getCount;
    private delegate* unmanaged<uint, IntPtr*, int> _getHandle;
    private delegate* unmanaged<IntPtr, byte*, uint, int> _getName;
    private delegate* unmanaged<IntPtr, int, uint*, int> _getTemp;
    private delegate* unmanaged<IntPtr, uint*, int> _getPower;
    private delegate* unmanaged<IntPtr, int, uint*, int> _getClock;
    private delegate* unmanaged<IntPtr, NvmlMemory*, int> _getMemory;

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory { public ulong Total, Free, Used; }

    private readonly Dictionary<string, IntPtr> _handleByName = new(StringComparer.OrdinalIgnoreCase);

    private bool EnsureReady()
    {
        if (_initTried) return _ready;
        _initTried = true;
        string path = Path.Combine(Environment.SystemDirectory, "nvml.dll");
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out _lib)) return false;
        try
        {
            _init = (delegate* unmanaged<int>)Export("nvmlInit_v2");
            _shutdown = (delegate* unmanaged<int>)Export("nvmlShutdown");
            _getCount = (delegate* unmanaged<uint*, int>)Export("nvmlDeviceGetCount_v2");
            _getHandle = (delegate* unmanaged<uint, IntPtr*, int>)Export("nvmlDeviceGetHandleByIndex_v2");
            _getName = (delegate* unmanaged<IntPtr, byte*, uint, int>)Export("nvmlDeviceGetName");
            _getTemp = (delegate* unmanaged<IntPtr, int, uint*, int>)Export("nvmlDeviceGetTemperature");
            _getPower = (delegate* unmanaged<IntPtr, uint*, int>)Export("nvmlDeviceGetPowerUsage");
            _getClock = (delegate* unmanaged<IntPtr, int, uint*, int>)Export("nvmlDeviceGetClockInfo");
            _getMemory = (delegate* unmanaged<IntPtr, NvmlMemory*, int>)Export("nvmlDeviceGetMemoryInfo");
        }
        catch (EntryPointNotFoundException ex)
        {
            Log.Info("NVML incomplete, GPU sensors hidden", ("error", ex.Message));
            NativeLibrary.Free(_lib);
            _lib = IntPtr.Zero;
            return false;
        }
        int rc = _init();
        if (rc != NVML_SUCCESS)
        {
            Log.Info("NVML init failed, GPU sensors hidden", ("rc", rc));
            NativeLibrary.Free(_lib);
            _lib = IntPtr.Zero;
            return false;
        }

        uint count = 0;
        byte* buf = stackalloc byte[96];
        if (_getCount(&count) == NVML_SUCCESS)
        {
            for (uint i = 0; i < count; i++)
            {
                IntPtr h;
                if (_getHandle(i, &h) != NVML_SUCCESS) continue;
                if (_getName(h, buf, 96) != NVML_SUCCESS) continue;
                string name = Encoding.UTF8.GetString(buf, IndexOfNul(buf, 96)).Trim();
                _handleByName.TryAdd(name, h);   // identical twin GPUs: first one wins (documented limitation)
            }
        }
        _ready = true;
        Log.Info("NVML ready", ("devices", _handleByName.Count));
        return true;
    }

    private IntPtr Export(string name) => NativeLibrary.GetExport(_lib, name);

    private static int IndexOfNul(byte* p, int max)
    {
        for (int i = 0; i < max; i++) if (p[i] == 0) return i;
        return max;
    }

    /// <summary>Read the NVIDIA GPU whose NVML name equals the DXGI adapter name.</summary>
    public NvmlReading? Read(string adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName) || !EnsureReady()) return null;
        if (!_handleByName.TryGetValue(adapterName.Trim(), out IntPtr h)) return null;

        uint v;
        double? temp = _getTemp(h, NVML_TEMPERATURE_GPU, &v) == NVML_SUCCESS ? v : null;
        double? power = _getPower(h, &v) == NVML_SUCCESS ? v / 1000.0 : null;      // mW -> W
        double? clock = _getClock(h, NVML_CLOCK_GRAPHICS, &v) == NVML_SUCCESS ? v : null;
        NvmlMemory mem;
        bool memOk = _getMemory(h, &mem) == NVML_SUCCESS && mem.Total > 0;
        return new NvmlReading(temp, power, clock,
            memOk ? (long)mem.Used : null, memOk ? (long)mem.Total : null);
    }

    public void Dispose()
    {
        if (_ready) _shutdown();
        _ready = false;
        if (_lib != IntPtr.Zero) { NativeLibrary.Free(_lib); _lib = IntPtr.Zero; }
        _handleByName.Clear();
    }
}
