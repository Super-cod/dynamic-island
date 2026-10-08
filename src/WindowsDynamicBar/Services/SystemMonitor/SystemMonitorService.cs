using System.Diagnostics;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.Services.SystemMonitor;

public sealed record SystemStats(double Cpu, double Gpu, double Ram, long AvailableBytes);

/// <summary>
/// Throttled system monitor:
///  - CPU: PDH counter \Processor(_Total)\% Processor Time — read on interval, not per-frame.
///  - RAM: GlobalMemoryStatusEx (instant, no cost).
///  - GPU: PDH "\GPU Engine(*)\Utilization Percentage" summed per process, falls back to
///    disabled if counters are unavailable (documented limitation — GPU counters need
///    Windows Performance Counter infrastructure; gracefully hidden otherwise).
/// Intervals adapt to PerformanceMode: 2s (Performance), 3s (Balanced), 6s (LowPower).
/// Monitoring pauses entirely when no one is watching (bar idle + not pinned).
/// </summary>
public sealed class SystemMonitorService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private readonly object _sync = new();

    private IntPtr _cpuQuery = IntPtr.Zero;
    private IntPtr _cpuCounter = IntPtr.Zero;
    private IntPtr _gpuQuery = IntPtr.Zero;
    private IntPtr _gpuCounter = IntPtr.Zero;
    private bool _gpuAvailable;
    private bool _cpuAvailable;

    private System.Threading.Timer? _timer;
    private double _cpu, _gpu, _ram;
    private long _totalPhys, _availPhys;
    private bool _active;

    public event Action<SystemStats>? StatsChanged;
    public SystemStats Current => new(_cpu, _gpu, _ram, _availPhys);

    public SystemMonitorService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
        var mi = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mi)) { _totalPhys = (long)mi.ullTotalPhys; }
    }

    public void StartCounters()
    {
        if (_cpuQuery != IntPtr.Zero) return;
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _cpuQuery) == 0 &&
                PdhAddEnglishCounterW(_cpuQuery, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpuCounter) == 0)
            {
                PdhCollectQueryData(_cpuQuery); // prime (first read is always 0)
                _cpuAvailable = true;
            }
            else { _cpuAvailable = false; }

            if (PdhOpenQueryW(null, IntPtr.Zero, out _gpuQuery) == 0 &&
                PdhAddEnglishCounterW(_gpuQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) == 0)
            {
                PdhCollectQueryData(_gpuQuery);
                _gpuAvailable = true;
            }
            else { _gpuAvailable = false; Log.Info("GPU counters unavailable"); }
        }
        catch (Exception ex)
        {
            _cpuAvailable = false; _gpuAvailable = false;
            Log.Info("System counters unavailable", ("error", ex.Message));
        }
    }

    /// <summary>Begin periodic sampling. Safe to call repeatedly.</summary>
    public void Activate()
    {
        if (_active) return;
        _active = true;
        StartCounters();
        int intervalMs = _settings.Current.PerformanceMode switch
        {
            PerformanceMode.LowPower => 6000,
            PerformanceMode.Performance => 1500,
            _ => 3000
        };
        _timer = new System.Threading.Timer(_ => Sample(), null,
            TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(intervalMs));
    }

    public void Deactivate()
    {
        _active = false;
        _timer?.Dispose();
        _timer = null;
    }

    private void Sample()
    {
        try
        {
            if (!_cpuAvailable && !_gpuAvailable && _totalPhys == 0) return;

            double cpu = _cpu;
            if (_cpuAvailable)
            {
                PdhCollectQueryData(_cpuQuery);
                var buf = new PDH_FMT_COUNTERVALUE_DOUBLE[1];
                if (PdhGetFormattedCounterValueDouble(_cpuCounter, out _, buf) == 0)
                    cpu = Math.Clamp(buf[0].doubleValue, 0, 100);
            }

            double gpu = _gpu;
            if (_gpuAvailable)
            {
                PdhCollectQueryData(_gpuQuery);
                // Sum across all GPU engine instances: query raw list.
                gpu = SumGpuUtilization();
            }

            double ram = _ram;
            long avail = _availPhys;
            var mi = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mi))
            {
                avail = (long)mi.ullAvailPhys;
                ram = _totalPhys > 0 ? (1.0 - (double)mi.ullAvailPhys / _totalPhys) * 100.0 : 0;
            }

            // Round to 1 decimal to avoid raising change events for noise.
            cpu = Math.Round(cpu, 1); gpu = Math.Round(gpu, 1); ram = Math.Round(ram, 1);
            bool changed = cpu != _cpu || gpu != _gpu || ram != _ram;
            _cpu = cpu; _gpu = gpu; _ram = ram; _availPhys = avail;
            if (!changed) return;

            var stats = new SystemStats(cpu, gpu, ram, avail);
            StatsChanged?.Invoke(stats);

            if (_settings.Current.FeatureSystemMonitor && _settings.Current.SystemMonitorInBar)
            {
                _bus.Raise(BarEvent.Create(BarEventKind.SystemMonitor, "System",
                    $"CPU {cpu:F0}% · GPU {gpu:F0}% · RAM {ram:F0}%",
                    value: cpu / 100.0, valueText: $"{cpu:F0}%", payload: stats,
                    priority: 5, displayDuration: TimeSpan.FromSeconds(2)));
            }
        }
        catch (Exception ex) { Log.Debug("Sysmon sample failed", ("error", ex.Message)); }
    }

    private double SumGpuUtilization()
    {
        // PdhGetFormattedCounterArray returns PDH_FMT_COUNTERVALUE[] (24 bytes each on x64:
        // CStatus(4) + pad(4) + union(8) where doubleValue sits at offset 8 ... actually
        // union is RAW (8) then DOUBLE (8) in the 24-byte layout: status(4)+pad(4)+raw(8)+double(8)).
        try
        {
            uint bufferSize = 0, itemCount = 0;
            int status = PdhGetFormattedCounterArrayW(_gpuCounter, 0x00000200 /*PDH_FMT_DOUBLE*/,
                ref bufferSize, ref itemCount, IntPtr.Zero);
            if (status != 0x0000124 /*PDH_MORE_DATA*/ || itemCount == 0) return _gpu;
            IntPtr pBuf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                status = PdhGetFormattedCounterArrayW(_gpuCounter, 0x00000200,
                    ref bufferSize, ref itemCount, pBuf);
                if (status != 0) return _gpu;
                double sum = 0;
                int elemSize = 24;
                for (int i = 0; i < itemCount; i++)
                {
                    long bits = System.Runtime.InteropServices.Marshal.ReadInt64(pBuf, i * elemSize + 16);
                    sum += BitConverter.Int64BitsToDouble(bits);
                }
                return Math.Min(100, Math.Round(sum, 1));
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pBuf); }
        }
        catch { return _gpu; }
    }

    // ---- PDH interop ----
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_DOUBLE { public int status; public double doubleValue; }

    [System.Runtime.InteropServices.DllImport("pdh.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);
    [System.Runtime.InteropServices.DllImport("pdh.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);
    [System.Runtime.InteropServices.DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);
    [System.Runtime.InteropServices.DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValueDouble(IntPtr counter, out IntPtr type, [System.Runtime.InteropServices.Out] PDH_FMT_COUNTERVALUE_DOUBLE[] value);
    [System.Runtime.InteropServices.DllImport("pdh.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, int format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);
    [System.Runtime.InteropServices.DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public void Dispose()
    {
        Deactivate();
        if (_cpuQuery != IntPtr.Zero) { PdhCloseQuery(_cpuQuery); _cpuQuery = IntPtr.Zero; }
        if (_gpuQuery != IntPtr.Zero) { PdhCloseQuery(_gpuQuery); _gpuQuery = IntPtr.Zero; }
    }
}
