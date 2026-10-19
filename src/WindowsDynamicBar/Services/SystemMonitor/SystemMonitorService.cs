using System.Diagnostics;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Interop;
using WindowsDynamicBar.Services.Settings;

namespace WindowsDynamicBar.Services.SystemMonitor;

public sealed record SystemStats(double Cpu, double Gpu, double Ram, long AvailableBytes,
    bool CpuAvailable = true, bool GpuAvailable = true);

/// <summary>
/// Throttled system monitor:
///  - CPU: PDH counter \Processor(_Total)\% Processor Time — read on interval, not per-frame.
///  - RAM: GlobalMemoryStatusEx (instant, no cost).
///  - GPU: PDH "\GPU Engine(*)\Utilization Percentage" summed per process, falls back to
///    disabled if counters are unavailable (documented limitation — GPU counters need
///    Windows Performance Counter infrastructure; gracefully hidden otherwise).
/// Intervals adapt to PerformanceMode: 2s (Performance), 3s (Balanced), 6s (LowPower).
/// Monitoring pauses entirely when no one is watching (bar idle + not pinned).
/// Samples are ambient state, NOT bar events: they update the collapsed pill and the
/// open card via StatsChanged; they never pop the bar open.
/// </summary>
public sealed class SystemMonitorService : IDisposable
{
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
    private bool _disposed;

    public event Action<SystemStats>? StatsChanged;

    public SystemStats Current
    {
        get
        {
            lock (_sync)
            {
                return new(_cpu, _gpu, _ram, _availPhys, _cpuAvailable, _gpuAvailable);
            }
        }
    }

    public bool GpuAvailable
    {
        get
        {
            lock (_sync)
            {
                return _gpuAvailable;
            }
        }
    }

    public bool CpuAvailable
    {
        get
        {
            lock (_sync)
            {
                return _cpuAvailable;
            }
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    public SystemMonitorService(SettingsService settings)
    {
        _settings = settings;
        var mi = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref mi)) { _totalPhys = (long)mi.ullTotalPhys; }
    }

    public void StartCounters()
    {
        lock (_sync)
        {
            if (_disposed) return;
            StartCountersUnsafe();
        }
    }

    private void StartCountersUnsafe()
    {
        if (_cpuQuery != IntPtr.Zero) return;
        try
        {
            if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _cpuQuery) == 0 &&
                NativeMethods.PdhAddEnglishCounterW(_cpuQuery, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpuCounter) == 0)
            {
                NativeMethods.PdhCollectQueryData(_cpuQuery); // prime (first read is always 0)
                _cpuAvailable = true;
            }
            else { _cpuAvailable = false; }

            if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _gpuQuery) == 0 &&
                NativeMethods.PdhAddEnglishCounterW(_gpuQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) == 0)
            {
                NativeMethods.PdhCollectQueryData(_gpuQuery);
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
        lock (_sync)
        {
            if (_disposed || _active) return;
            _active = true;
            StartCountersUnsafe();
            int intervalMs = _settings.Current.PerformanceMode switch
            {
                PerformanceMode.LowPower => 6000,
                PerformanceMode.Performance => 1500,
                _ => 3000
            };
            _timer?.Dispose();
            _timer = new System.Threading.Timer(_ => Sample(), null,
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(intervalMs));
        }
    }

    public void Deactivate()
    {
        System.Threading.Timer? timerToDispose;
        lock (_sync)
        {
            if (!_active) return;
            _active = false;
            timerToDispose = _timer;
            _timer = null;
        }
        timerToDispose?.Dispose();
    }

    internal void Sample()
    {
        SystemStats? statsToNotify = null;
        if (!Monitor.TryEnter(_sync))
        {
            // Drop overlapping tick if a sample or lifecycle transition is already holding the lock
            return;
        }

        try
        {
            if (_disposed || !_active) return;
            if (!_cpuAvailable && !_gpuAvailable && _totalPhys == 0) return;

            double cpu = _cpu;
            if (_cpuAvailable && _cpuQuery != IntPtr.Zero && _cpuCounter != IntPtr.Zero)
            {
                NativeMethods.PdhCollectQueryData(_cpuQuery);
                var buf = new NativeMethods.PDH_FMT_COUNTERVALUE_DOUBLE[1];
                if (NativeMethods.PdhGetFormattedCounterValue(_cpuCounter, NativeMethods.PDH_FMT_DOUBLE, out _, buf) == 0)
                    cpu = Math.Clamp(buf[0].doubleValue, 0, 100);
            }

            double gpu = _gpu;
            if (_gpuAvailable && _gpuQuery != IntPtr.Zero && _gpuCounter != IntPtr.Zero)
            {
                NativeMethods.PdhCollectQueryData(_gpuQuery);
                // Sum across all GPU engine instances: query raw list.
                gpu = SumGpuUtilizationUnsafe();
            }

            double ram = _ram;
            long avail = _availPhys;
            var mi = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
            if (NativeMethods.GlobalMemoryStatusEx(ref mi))
            {
                avail = (long)mi.ullAvailPhys;
                ram = _totalPhys > 0 ? (1.0 - (double)mi.ullAvailPhys / _totalPhys) * 100.0 : 0;
            }

            // Round to 1 decimal to avoid raising change events for noise.
            cpu = Math.Round(cpu, 1); gpu = Math.Round(gpu, 1); ram = Math.Round(ram, 1);
            bool changed = cpu != _cpu || gpu != _gpu || ram != _ram;
            _cpu = cpu; _gpu = gpu; _ram = ram; _availPhys = avail;
            if (changed)
            {
                statsToNotify = new SystemStats(cpu, gpu, ram, avail, _cpuAvailable, _gpuAvailable);
            }
        }
        catch (Exception ex) { Log.Debug("Sysmon sample failed", ("error", ex.Message)); }
        finally
        {
            Monitor.Exit(_sync);
        }

        if (statsToNotify is not null)
        {
            StatsChanged?.Invoke(statsToNotify);
        }
    }

    private double SumGpuUtilizationUnsafe()
    {
        try
        {
            if (_gpuCounter == IntPtr.Zero) return _gpu;
            uint bufferSize = 0, itemCount = 0;
            int status = NativeMethods.PdhGetFormattedCounterArrayW(_gpuCounter, NativeMethods.PDH_FMT_DOUBLE,
                ref bufferSize, ref itemCount, IntPtr.Zero);
            if (status != NativeMethods.PDH_MORE_DATA || itemCount == 0) return _gpu;
            IntPtr pBuf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                status = NativeMethods.PdhGetFormattedCounterArrayW(_gpuCounter, NativeMethods.PDH_FMT_DOUBLE,
                    ref bufferSize, ref itemCount, pBuf);
                if (status != 0) return _gpu;
                var samples = new List<(string Instance, double Value)>((int)itemCount);
                int elemSize = 24;
                for (int i = 0; i < itemCount; i++)
                {
                    IntPtr szName = System.Runtime.InteropServices.Marshal.ReadIntPtr(pBuf, i * elemSize);
                    string? inst = szName != IntPtr.Zero ? System.Runtime.InteropServices.Marshal.PtrToStringUni(szName) : null;
                    long bits = System.Runtime.InteropServices.Marshal.ReadInt64(pBuf, i * elemSize + 16);
                    double val = BitConverter.Int64BitsToDouble(bits);
                    if (!string.IsNullOrEmpty(inst) && val > 0)
                    {
                        samples.Add((inst, Math.Clamp(val, 0, 100)));
                    }
                }
                var grouped = GpuEngineParser.GroupUtilization(samples);
                double max = grouped.Count > 0 ? grouped.Values.Max() : 0;
                return Math.Min(100, Math.Round(max, 1));
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pBuf); }
        }
        catch { return _gpu; }
    }

    public void Dispose()
    {
        System.Threading.Timer? timerToDispose;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _active = false;
            timerToDispose = _timer;
            _timer = null;

            if (_cpuQuery != IntPtr.Zero)
            {
                try { NativeMethods.PdhCloseQuery(_cpuQuery); } catch { }
                _cpuQuery = IntPtr.Zero;
                _cpuCounter = IntPtr.Zero;
                _cpuAvailable = false;
            }
            if (_gpuQuery != IntPtr.Zero)
            {
                try { NativeMethods.PdhCloseQuery(_gpuQuery); } catch { }
                _gpuQuery = IntPtr.Zero;
                _gpuCounter = IntPtr.Zero;
                _gpuAvailable = false;
            }
        }
        timerToDispose?.Dispose();
    }
}
