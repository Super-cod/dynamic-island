using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Interop;
using WindowsDynamicBar.Services.Settings;

namespace WindowsDynamicBar.Services.SystemMonitor;

public sealed record CpuInfo(string Name, double FrequencyMhz, int PhysicalCores, int LogicalThreads, bool Available);

public sealed class GpuTrack
{
    public long Luid;
    public string Name = "";
    public int AdapterIndex = -1;
    public double Utilization;
    public long VramUsed;
    public long VramTotal;
    public bool VramAvailable;
    public double Engine3D;
    public double EngineCopy;
    public double EngineEncode;
    public double EngineDecode;
    public bool EnginesAvailable;
    public uint VendorId;
    // NVML sensors (NVIDIA only, read while this GPU's panel is open); null = not readable.
    public double? TempC;
    public double? PowerW;
    public double? ClockMhz;
    public TelemetryHistory History { get; } = new(240);
}

public sealed class SystemTelemetryService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly object _sync = new();

    private IntPtr _cpuQuery = IntPtr.Zero;
    private IntPtr _cpuCounter = IntPtr.Zero;
    private IntPtr _cpuCoresQuery = IntPtr.Zero;
    private IntPtr _cpuCoresCounter = IntPtr.Zero;
    private bool _cpuCoresAvailable;
    private double[] _cpuCoreUsage = Array.Empty<double>();
    private IntPtr _engineQuery = IntPtr.Zero;
    private IntPtr _gpuEngineCounter = IntPtr.Zero;
    private bool _engineExpanded;

    private IntPtr _memQuery = IntPtr.Zero;
    private readonly List<VramCounter> _vramCounters = new();
    private bool _vramAvailable;

    private IntPtr _diskQuery = IntPtr.Zero;
    private IntPtr _diskReadCounter = IntPtr.Zero;
    private IntPtr _diskWriteCounter = IntPtr.Zero;
    private bool _diskAvailable;
    private long _diskRead;
    private long _diskWrite;

    private IntPtr _sysMemQuery = IntPtr.Zero;
    private IntPtr _cacheCounter = IntPtr.Zero;
    private bool _cacheAvailable;
    private long _ramCache;

    // ACPI thermal zones (Thermal Zone Information\Temperature, Kelvin): readable without admin.
    private IntPtr _thermalQuery = IntPtr.Zero;
    private readonly List<IntPtr> _thermalCounters = new();
    private double? _cpuTempC;

    private readonly Nvml _nvml = new();
    private readonly CpuLoadAverager _load = new();

    private System.Threading.Timer? _timer;
    private bool _active;

    private double _cpu;
    private double _ramPercent;
    private long _ramUsed;
    private long _ramTotal;
    private long _ramSwapUsed;
    private long _ramSwapTotal;
    private bool _cpuAvailable;
    private bool _memoryAvailable;

    private readonly Dictionary<int, TimeSpan> _procCpu = new();
    private readonly int[] _topProcPids = new int[3];
    private DateTime _lastProcSampleUtc;
    private IReadOnlyList<ProcessSample> _topProcesses = Array.Empty<ProcessSample>();

    private CpuInfo? _cpuInfo;
    private readonly List<GpuTrack> _gpuTracks = new();
    private readonly Dictionary<long, int> _dxgiIndexByLuid = new();
    private readonly Dictionary<long, string> _dxgiNameByLuid = new();
    private int _samplesSinceAdapterRefresh;

    public event Action<TelemetrySnapshot>? TelemetryUpdated;

    public TelemetryHistory CpuHistory { get; } = new(240);
    public TelemetryHistory RamHistory { get; } = new(240);
    public bool IsActive { get { lock (_sync) return _active; } }

    /// <summary>Per-logical-core CPU utilization (0..100), or empty when unavailable.</summary>
    public double[] CpuCoreUsage { get { lock (_sync) return (double[])_cpuCoreUsage.Clone(); } }
    public long RamSwapUsedBytes { get { lock (_sync) return _ramSwapUsed; } }
    public long RamSwapTotalBytes { get { lock (_sync) return _ramSwapTotal; } }
    public long RamCacheBytes { get { lock (_sync) return _cacheAvailable ? _ramCache : 0; } }
    public long DiskReadBytesPerSec { get { lock (_sync) return _diskAvailable ? _diskRead : -1; } }
    public long DiskWriteBytesPerSec { get { lock (_sync) return _diskAvailable ? _diskWrite : -1; } }

    /// <summary>Hottest ACPI thermal zone in degrees C, or null when no zone is readable.</summary>
    public double? CpuTemperatureC { get { lock (_sync) return _cpuTempC; } }

    /// <summary>Average CPU % over the last 1/5/15 minutes (null until that much data exists).</summary>
    public (double? M1, double? M5, double? M15) CpuLoad => _load.Averages();

    /// <summary>LUID of the GPU whose detail panel is open (NVML is only queried for it); 0 = none.</summary>
    public long SensorLuid { get; set; }

    /// <summary>Live history buffer for the GPU adapter at <paramref name="index"/> (creation order).</summary>
    public TelemetryHistory? GetGpuHistory(int index)
    {
        lock (_sync) return index >= 0 && index < _gpuTracks.Count ? _gpuTracks[index].History : null;
    }

    /// <summary>Top processes by CPU share for the most recent sample.</summary>
    public IReadOnlyList<ProcessSample> SnapshotTopProcesses()
    {
        lock (_sync) return _topProcesses;
    }

    public IReadOnlyList<GpuTrack> SnapshotGpuTracks()
    {
        lock (_sync)
        {
            return _gpuTracks.Select(t => new GpuTrack
            {
                Luid = t.Luid,
                Name = t.Name,
                Utilization = t.Utilization,
                VramUsed = t.VramUsed,
                VramTotal = t.VramTotal,
                VramAvailable = t.VramAvailable,
                Engine3D = t.Engine3D,
                EngineCopy = t.EngineCopy,
                EngineEncode = t.EngineEncode,
                EngineDecode = t.EngineDecode,
                EnginesAvailable = t.EnginesAvailable,
                VendorId = t.VendorId,
                TempC = t.TempC,
                PowerW = t.PowerW,
                ClockMhz = t.ClockMhz,
            }).ToList();
        }
    }

    private sealed class VramCounter
    {
        public long Luid;
        public IntPtr Usage;
        public IntPtr Limit;
    }

    public SystemTelemetryService(SettingsService settings)
    {
        _settings = settings;
    }

    public void Activate()
    {
        lock (_sync)
        {
            if (_active) return;
            _active = true;
        }
        _load.Start();   // cheap (one GetSystemTimes call every 5 s); keeps running for 5/15-min averages
        StartCounters();
        RefreshAdapters();
        int intervalMs = _settings.Current.PerformanceMode switch
        {
            PerformanceMode.LowPower => 1000,
            PerformanceMode.Performance => 500,
            _ => 800,
        };
        _timer = new System.Threading.Timer(_ => Sample(), null,
            TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(intervalMs));
        Log.Info("Telemetry sampling started", ("intervalMs", intervalMs));
    }

    public void Deactivate()
    {
        lock (_sync) _active = false;
        _timer?.Dispose();
        _timer = null;
        Log.Info("Telemetry sampling stopped");
    }

    public List<GpuTrack> GetGpuTracks()
    {
        lock (_sync) return _gpuTracks.ToList();
    }

    public static CpuInfo QueryCpuInfo()
    {
        try
        {
            string? name = null;
            double mhz = 0;
            int cores = 0;
            int threads = 0;
            bool seen = false;
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name, CurrentClockSpeed, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
            {
                using var results = searcher.Get();
                foreach (var mo in results)
                {
                    using (mo)
                    {
                        seen = true;
                        if (name is null && mo["Name"] is { } n)
                        {
                            string? ns = n.ToString();
                            if (!string.IsNullOrWhiteSpace(ns)) name = ns;
                        }
                        if (mo["CurrentClockSpeed"] is { } cc && cc != DBNull.Value)
                        {
                            double m = Convert.ToDouble(cc, CultureInfo.InvariantCulture);
                            mhz = Math.Max(mhz, m);
                        }
                        if (mo["NumberOfCores"] is { } nc && nc != DBNull.Value)
                            cores += Convert.ToInt32(nc, CultureInfo.InvariantCulture);
                        if (mo["NumberOfLogicalProcessors"] is { } nlp && nlp != DBNull.Value)
                            threads += Convert.ToInt32(nlp, CultureInfo.InvariantCulture);
                    }
                }
            }
            if (seen && threads == 0) threads = Environment.ProcessorCount;
            return new CpuInfo(name ?? "", mhz, cores, threads, seen);
        }
        catch (Exception ex)
        {
            Log.Debug("CPU metadata query failed", ("error", ex.Message));
            return new CpuInfo("", 0, 0, 0, false);
        }
    }

    private void StartCounters()
    {
        try
        {
            if (_cpuQuery == IntPtr.Zero)
            {
                if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _cpuQuery) == 0 &&
                    NativeMethods.PdhAddEnglishCounterW(_cpuQuery, @"\Processor(_Total)\% Processor Time",
                        IntPtr.Zero, out _cpuCounter) == 0)
                {
                    NativeMethods.PdhCollectQueryData(_cpuQuery);
                    _cpuAvailable = true;
                }
                else _cpuAvailable = false;
            }

            if (_cpuCoresQuery == IntPtr.Zero)
            {
                if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _cpuCoresQuery) == 0 &&
                    NativeMethods.PdhAddEnglishCounterW(_cpuCoresQuery, @"\Processor(*)\% Processor Time",
                        IntPtr.Zero, out _cpuCoresCounter) == 0)
                {
                    NativeMethods.PdhCollectQueryData(_cpuCoresQuery);
                    _cpuCoresAvailable = true;
                }
                else
                {
                    _cpuCoresAvailable = false;
                    Log.Info("Per-core CPU counter unavailable");
                }
            }

            if (!_engineExpanded)
            {
                if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _engineQuery) == 0 &&
                    NativeMethods.PdhAddEnglishCounterW(_engineQuery, @"\GPU Engine(*)\Utilization Percentage",
                        IntPtr.Zero, out _gpuEngineCounter) == 0)
                {
                    NativeMethods.PdhCollectQueryData(_engineQuery);
                    _engineExpanded = true;
                }
                else
                {
                    _engineExpanded = true;
                    Log.Info("GPU engine counter unavailable");
                }
            }

            if (_memQuery == IntPtr.Zero)
            {
                var memInstances = ExpandInstances(@"\GPU Adapter Memory(*)\Dedicated Usage");
                if (memInstances.Count > 0 && NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _memQuery) == 0)
                {
                    foreach (string inst in memInstances)
                    {
                        if (NativeMethods.PdhAddEnglishCounterW(_memQuery, $@"\GPU Adapter Memory({inst})\Dedicated Usage",
                                IntPtr.Zero, out IntPtr u) == 0 &&
                            NativeMethods.PdhAddEnglishCounterW(_memQuery, $@"\GPU Adapter Memory({inst})\Dedicated Limit",
                                IntPtr.Zero, out IntPtr l) == 0)
                        {
                            long luid = 0;
                            if (GpuEngineParser.TryParse(inst + "_eng_0_engtype_mem", out var info))
                                luid = info.Luid;
                            _vramCounters.Add(new VramCounter { Luid = luid, Usage = u, Limit = l });
                        }
                    }
                    if (_vramCounters.Count > 0)
                    {
                        NativeMethods.PdhCollectQueryData(_memQuery);
                        _vramAvailable = true;
                    }
                }
            }

            if (_diskQuery == IntPtr.Zero)
            {
                if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _diskQuery) == 0 &&
                    NativeMethods.PdhAddEnglishCounterW(_diskQuery, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec",
                        IntPtr.Zero, out _diskReadCounter) == 0 &&
                    NativeMethods.PdhAddEnglishCounterW(_diskQuery, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec",
                        IntPtr.Zero, out _diskWriteCounter) == 0)
                {
                    NativeMethods.PdhCollectQueryData(_diskQuery);
                    _diskAvailable = true;
                }
                else _diskAvailable = false;
            }

            if (_thermalQuery == IntPtr.Zero)
            {
                var zones = ExpandInstances(@"\Thermal Zone Information(*)\Temperature");
                if (zones.Count > 0 && NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _thermalQuery) == 0)
                {
                    foreach (string z in zones)
                        if (NativeMethods.PdhAddEnglishCounterW(_thermalQuery, $@"\Thermal Zone Information({z})\Temperature",
                                IntPtr.Zero, out IntPtr tc) == 0)
                            _thermalCounters.Add(tc);
                    if (_thermalCounters.Count > 0) NativeMethods.PdhCollectQueryData(_thermalQuery);
                }
            }

            if (_sysMemQuery == IntPtr.Zero)
            {
                if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _sysMemQuery) == 0 &&
                    NativeMethods.PdhAddEnglishCounterW(_sysMemQuery, @"\Memory\Cache Bytes",
                        IntPtr.Zero, out _cacheCounter) == 0)
                {
                    NativeMethods.PdhCollectQueryData(_sysMemQuery);
                    _cacheAvailable = true;
                }
                else _cacheAvailable = false;
            }
        }
        catch (Exception ex)
        {
            _cpuAvailable = false;
            Log.Info("Telemetry counters unavailable", ("error", ex.Message));
        }
    }

    private void RefreshAdapters()
    {
        try
        {
            var dxgi = DxgiService.EnumAdapters();
            lock (_sync)
            {
                _dxgiIndexByLuid.Clear();
                _dxgiNameByLuid.Clear();
                for (int i = 0; i < dxgi.Count; i++)
                {
                    var a = dxgi[i];
                    _dxgiIndexByLuid[a.Luid] = i;
                    _dxgiNameByLuid[a.Luid] = a.Name;
                    if (!_gpuTracks.Any(t => t.Luid == a.Luid))
                    {
                        _gpuTracks.Add(new GpuTrack
                        {
                            Luid = a.Luid,
                            AdapterIndex = i,
                            Name = a.Name,
                            VendorId = a.VendorId,
                            VramTotal = a.DedicatedVideoMemoryBytes,
                            VramAvailable = a.DedicatedVideoMemoryBytes > 0
                        });
                    }
                }
                // When DXGI answered it is the source of truth: drop tracks that only exist in PDH
                // (software rasterizer, stale LUIDs) so no nameless "GPU 221" entry is shown.
                if (dxgi.Count > 0) _gpuTracks.RemoveAll(t => !_dxgiIndexByLuid.ContainsKey(t.Luid));
                foreach (var track in _gpuTracks)
                {
                    if (_dxgiIndexByLuid.TryGetValue(track.Luid, out int idx))
                    {
                        track.AdapterIndex = idx;
                        track.Name = _dxgiNameByLuid[track.Luid];
                        if (track.VramTotal <= 0)
                        {
                            var meta = dxgi[idx];
                            track.VramTotal = meta.DedicatedVideoMemoryBytes;
                        }
                    }

                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("DXGI adapter refresh failed", ("error", ex.Message));
        }
    }

    private void Sample()
    {
        TelemetrySnapshot? snapshot = null;
        try
        {
            lock (_sync)
            {
                SampleCpu();
                SampleCpuCores();
                SampleGpu();
                SampleMemory();
                SampleDiskCache();
                SampleProcesses();
                PushHistories();
                snapshot = BuildSnapshot();
            }
            if (snapshot is not null) TelemetryUpdated?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Log.Debug("Telemetry sample failed", ("error", ex.Message));
        }
    }

    private void SampleCpu()
    {
        if (!_cpuAvailable || _cpuQuery == IntPtr.Zero) return;
        NativeMethods.PdhCollectQueryData(_cpuQuery);
        var buf = new NativeMethods.PDH_FMT_COUNTERVALUE_DOUBLE[1];
        if (NativeMethods.PdhGetFormattedCounterValue(_cpuCounter, NativeMethods.PDH_FMT_DOUBLE, out _, buf) == 0)
            _cpu = Math.Round(Math.Clamp(buf[0].doubleValue, 0, 100), 1);
    }

    private void SampleCpuCores()
    {
        if (!_cpuCoresAvailable || _cpuCoresQuery == IntPtr.Zero || _cpuCoresCounter == IntPtr.Zero)
            return;

        NativeMethods.PdhCollectQueryData(_cpuCoresQuery);
        uint bufferSize = 0, itemCount = 0;
        int status = NativeMethods.PdhGetFormattedCounterArrayW(_cpuCoresCounter, NativeMethods.PDH_FMT_DOUBLE,
            ref bufferSize, ref itemCount, IntPtr.Zero);
        if (status != NativeMethods.PDH_MORE_DATA || itemCount == 0) return;

        IntPtr pBuf = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = NativeMethods.PdhGetFormattedCounterArrayW(_cpuCoresCounter, NativeMethods.PDH_FMT_DOUBLE,
                ref bufferSize, ref itemCount, pBuf);
            if (status != 0) return;

            var cores = new List<(int Index, double Value)>((int)itemCount);
            for (int i = 0; i < itemCount; i++)
            {
                IntPtr szName = Marshal.ReadIntPtr(pBuf, i * 24);
                string? inst = szName != IntPtr.Zero ? Marshal.PtrToStringUni(szName) : null;
                if (string.IsNullOrEmpty(inst) || inst.StartsWith("_", StringComparison.Ordinal)) continue;
                if (!int.TryParse(inst, out int coreIndex)) continue;
                long bits = Marshal.ReadInt64(pBuf, i * 24 + 16);
                double val = Math.Clamp(BitConverter.Int64BitsToDouble(bits), 0, 100);
                cores.Add((coreIndex, Math.Round(val, 1)));
            }
            cores.Sort((a, b) => a.Index.CompareTo(b.Index));
            _cpuCoreUsage = cores.Select(c => c.Value).ToArray();
        }
        finally { Marshal.FreeHGlobal(pBuf); }
    }

    private void SampleGpu()
    {
        var sums = new Dictionary<long, double>();
        var engines = new Dictionary<long, GpuEngineParser.EngineBreakdown>();
        if (_engineQuery != IntPtr.Zero && _gpuEngineCounter != IntPtr.Zero)
        {
            NativeMethods.PdhCollectQueryData(_engineQuery);
            uint bufferSize = 0, itemCount = 0;
            int status = NativeMethods.PdhGetFormattedCounterArrayW(_gpuEngineCounter, NativeMethods.PDH_FMT_DOUBLE,
                ref bufferSize, ref itemCount, IntPtr.Zero);
            if (status == NativeMethods.PDH_MORE_DATA && itemCount > 0)
            {
                IntPtr pBuf = Marshal.AllocHGlobal((int)bufferSize);
                try
                {
                    status = NativeMethods.PdhGetFormattedCounterArrayW(_gpuEngineCounter, NativeMethods.PDH_FMT_DOUBLE,
                        ref bufferSize, ref itemCount, pBuf);
                    if (status == 0)
                    {
                        var samples = new List<(string Instance, double Value)>((int)itemCount);
                        int elemSize = 24;
                        for (int i = 0; i < itemCount; i++)
                        {
                            IntPtr szName = Marshal.ReadIntPtr(pBuf, i * elemSize);
                            string? inst = szName != IntPtr.Zero ? Marshal.PtrToStringUni(szName) : null;
                            long bits = Marshal.ReadInt64(pBuf, i * elemSize + 16);
                            double val = BitConverter.Int64BitsToDouble(bits);
                            if (!string.IsNullOrEmpty(inst) && val > 0)
                            {
                                samples.Add((inst, Math.Clamp(val, 0, 100)));
                            }
                        }
                        sums = GpuEngineParser.GroupUtilization(samples);
                        engines = GpuEngineParser.GroupByType(samples);
                    }
                }
                finally { Marshal.FreeHGlobal(pBuf); }
            }
        }

        foreach (var track in _gpuTracks)
        {
            if (sums.TryGetValue(track.Luid, out double util))
                track.Utilization = Math.Round(util, 1);
            else
                track.Utilization = 0;

            if (engines.TryGetValue(track.Luid, out var eb))
            {
                track.Engine3D = Math.Round(eb.D3, 1);
                track.EngineCopy = Math.Round(eb.Copy, 1);
                track.EngineEncode = Math.Round(eb.Enc, 1);
                track.EngineDecode = Math.Round(eb.Dec, 1);
                track.EnginesAvailable = true;
            }
            if (track.VramTotal > 0 && !track.VramAvailable) track.VramAvailable = true;
        }

        // Without DXGI (rare) fall back to the adapters PDH reports; they have no name.
        if (_dxgiIndexByLuid.Count == 0)
        {
            foreach (var luid in sums.Keys.ToList())
            {
                if (!_gpuTracks.Any(t => t.Luid == luid))
                    _gpuTracks.Add(new GpuTrack { Luid = luid, Utilization = Math.Round(sums[luid], 1) });
            }
        }

        // NVML sensors only for the GPU whose panel is open (reading can wake a laptop dGPU).
        long sensorLuid = SensorLuid;
        foreach (var track in _gpuTracks)
        {
            NvmlReading? r = track.Luid == sensorLuid && track.VendorId == DxgiAdapterInfo.VendorNvidia
                ? _nvml.Read(track.Name) : null;
            track.TempC = r?.TempC;
            track.PowerW = r?.PowerW;
            track.ClockMhz = r?.ClockMhz;
            if (r is { MemUsedBytes: long used, MemTotalBytes: long total })
            {
                track.VramUsed = used;
                track.VramTotal = total;
                track.VramAvailable = true;
            }
        }

        if (_vramAvailable && _memQuery != IntPtr.Zero && _vramCounters.Count > 0)
        {
            NativeMethods.PdhCollectQueryData(_memQuery);
            for (int i = 0; i < _vramCounters.Count; i++)
            {
                var vc = _vramCounters[i];
                var ub = new NativeMethods.PDH_FMT_COUNTERVALUE_DOUBLE[1];
                var lb = new NativeMethods.PDH_FMT_COUNTERVALUE_DOUBLE[1];
                if (NativeMethods.PdhGetFormattedCounterValue(vc.Usage, NativeMethods.PDH_FMT_DOUBLE, out _, ub) != 0) continue;
                if (NativeMethods.PdhGetFormattedCounterValue(vc.Limit, NativeMethods.PDH_FMT_DOUBLE, out _, lb) != 0) continue;
                var track = _gpuTracks.FirstOrDefault(t => t.Luid == vc.Luid)
                         ?? (i < _gpuTracks.Count ? _gpuTracks[i] : null);
                if (track != null)
                {
                    track.VramUsed = (long)Math.Max(0, ub[0].doubleValue);
                    track.VramTotal = Math.Max(track.VramTotal, (long)Math.Max(0, lb[0].doubleValue));
                    track.VramAvailable = true;
                }
            }
        }

        if (_gpuTracks.Count > 0 && _samplesSinceAdapterRefresh++ % 40 == 0)
        {
            RefreshAdapters();
            _samplesSinceAdapterRefresh = 0;
        }
    }

    private void SampleMemory()
    {
        var mi = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        if (NativeMethods.GlobalMemoryStatusEx(ref mi))
        {
            _ramTotal = (long)mi.ullTotalPhys;
            long avail = (long)mi.ullAvailPhys;
            _ramUsed = Math.Max(0, _ramTotal - avail);
            _ramPercent = _ramTotal > 0
                ? Math.Round(Math.Clamp((double)_ramUsed / _ramTotal * 100, 0, 100), 1)
                : 0;
            _ramSwapTotal = (long)mi.ullTotalPageFile;
            _ramSwapUsed = Math.Max(0, _ramSwapTotal - (long)mi.ullAvailPageFile);
            _memoryAvailable = true;
        }
        else _memoryAvailable = false;
    }

    private void SampleDiskCache()
    {
        if (_diskAvailable && _diskQuery != IntPtr.Zero)
        {
            NativeMethods.PdhCollectQueryData(_diskQuery);
            var rb = new NativeMethods.PDH_FMT_COUNTERVALUE_DOUBLE[1];
            if (NativeMethods.PdhGetFormattedCounterValue(_diskReadCounter, NativeMethods.PDH_FMT_DOUBLE, out _, rb) == 0)
                _diskRead = (long)Math.Max(0, rb[0].doubleValue);
            var wb = new NativeMethods.PDH_FMT_COUNTERVALUE_DOUBLE[1];
            if (NativeMethods.PdhGetFormattedCounterValue(_diskWriteCounter, NativeMethods.PDH_FMT_DOUBLE, out _, wb) == 0)
                _diskWrite = (long)Math.Max(0, wb[0].doubleValue);
        }
        if (_thermalCounters.Count > 0 && _thermalQuery != IntPtr.Zero)
        {
            NativeMethods.PdhCollectQueryData(_thermalQuery);
            double? hottest = null;
            foreach (IntPtr tc in _thermalCounters)
            {
                var tb = new NativeMethods.PDH_FMT_COUNTERVALUE_DOUBLE[1];
                if (NativeMethods.PdhGetFormattedCounterValue(tc, NativeMethods.PDH_FMT_DOUBLE, out _, tb) != 0) continue;
                double c = tb[0].doubleValue - 273.15;              // Kelvin -> Celsius
                if (c is > -40 and < 150) hottest = Math.Max(hottest ?? double.MinValue, c);
            }
            _cpuTempC = hottest is null ? null : Math.Round(hottest.Value, 1);
        }
        if (_cacheAvailable && _sysMemQuery != IntPtr.Zero)
        {
            NativeMethods.PdhCollectQueryData(_sysMemQuery);
            var cb = new NativeMethods.PDH_FMT_COUNTERVALUE_DOUBLE[1];
            if (NativeMethods.PdhGetFormattedCounterValue(_cacheCounter, NativeMethods.PDH_FMT_DOUBLE, out _, cb) == 0)
                _ramCache = (long)Math.Max(0, cb[0].doubleValue);
        }
    }

    /// <summary>Top-3 processes by CPU share, derived from TotalProcessorTime deltas.</summary>
    private void SampleProcesses()
    {
        var now = DateTime.UtcNow;
        double elapsed = (now - _lastProcSampleUtc).TotalSeconds;
        _lastProcSampleUtc = now;

        int ncpu = Math.Max(1, Environment.ProcessorCount);
        var next = new Dictionary<int, TimeSpan>(_procCpu.Count + 8);
        var cand = new List<(int Id, string Name, double Pct)>(_procCpu.Count + 16);

        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return; }

        foreach (var p in procs)
        {
            try
            {
                int id = p.Id;
                TimeSpan t;
                try { t = p.TotalProcessorTime; }
                catch { continue; }

                next[id] = t;
                if (elapsed > 0.05 && _procCpu.TryGetValue(id, out var prev))
                {
                    double dp = (t - prev).TotalSeconds;
                    double pct = Math.Clamp(dp / elapsed / ncpu * 100.0, 0, 100);
                    cand.Add((id, p.ProcessName, pct));
                }
            }
            catch { /* process exited mid-enumeration */ }
            finally { p.Dispose(); }
        }

        _procCpu.Clear();
        foreach (var kv in next) _procCpu[kv.Key] = kv.Value;

        var prevSet = new HashSet<int>(_topProcPids.Where(pid => pid > 0));
        var ranked = cand
            .OrderByDescending(c => c.Pct)
            .ThenBy(c => c.Pct < 0.05 && prevSet.Contains(c.Id) ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        var result = new List<ProcessSample>(ranked.Count);
        for (int i = 0; i < ranked.Count; i++)
        {
            _topProcPids[i] = ranked[i].Id;
            result.Add(new ProcessSample(ranked[i].Name, Math.Round(ranked[i].Pct, 1)));
        }
        _topProcesses = result;
    }

    private void PushHistories()
    {
        if (_cpuAvailable) CpuHistory.Push(_cpu);
        if (_memoryAvailable) RamHistory.Push(_ramPercent);
        foreach (var track in _gpuTracks) track.History.Push(track.Utilization);
    }

    private TelemetrySnapshot BuildSnapshot()
    {
        _cpuInfo ??= QueryCpuInfo();
        var cpu = new CpuStats(_cpu, _cpuInfo.Name, _cpuInfo.FrequencyMhz,
            _cpuInfo.PhysicalCores, _cpuInfo.LogicalThreads, _cpuAvailable && _cpuInfo.Available);
        var memory = new MemoryStats(_ramPercent, _ramUsed, _ramTotal, _memoryAvailable);
        var gpus = new List<GpuAdapterStats>();
        foreach (var track in _gpuTracks.OrderBy(t => t.Luid))
        {
            gpus.Add(new GpuAdapterStats(track.Luid, track.Name, track.Utilization,
                track.VramUsed, track.VramTotal, track.VramAvailable));
        }
        return new TelemetrySnapshot(cpu, gpus, memory, DateTime.UtcNow, _topProcesses);
    }

    private static List<string> ExpandInstances(string wildcard)
    {
        var list = new List<string>();
        try
        {
            uint size = 0;
            int status = NativeMethods.PdhExpandWildCardPathW(null, wildcard, IntPtr.Zero, ref size, 0);
            if (status != 0 && status != NativeMethods.PDH_MORE_DATA) return list;
            if (size == 0) return list;
            IntPtr p = Marshal.AllocHGlobal((int)size * 2 + 8);
            try
            {
                status = NativeMethods.PdhExpandWildCardPathW(null, wildcard, p, ref size, 0);
                if (status != 0) return list;
                int offset = 0;
                while (offset < (int)size * 2)
                {
                    string? s = Marshal.PtrToStringUni(IntPtr.Add(p, offset));
                    if (string.IsNullOrEmpty(s)) break;
                    int open = s.IndexOf('(');
                    int close = s.IndexOf(')');
                    if (open > 0 && close > open)
                        list.Add(s[(open + 1)..close]);
                    else
                        list.Add(s);
                    offset += (s.Length + 1) * 2;
                }
            }
            finally { Marshal.FreeHGlobal(p); }
        }
        catch { }
        return list;
    }

    public void Dispose()
    {
        Deactivate();
        lock (_sync)
        {
            if (_cpuQuery != IntPtr.Zero) { NativeMethods.PdhCloseQuery(_cpuQuery); _cpuQuery = IntPtr.Zero; }
            if (_cpuCoresQuery != IntPtr.Zero) { NativeMethods.PdhCloseQuery(_cpuCoresQuery); _cpuCoresQuery = IntPtr.Zero; }
            if (_engineQuery != IntPtr.Zero) { NativeMethods.PdhCloseQuery(_engineQuery); _engineQuery = IntPtr.Zero; }
            if (_memQuery != IntPtr.Zero) { NativeMethods.PdhCloseQuery(_memQuery); _memQuery = IntPtr.Zero; }
            if (_diskQuery != IntPtr.Zero) { NativeMethods.PdhCloseQuery(_diskQuery); _diskQuery = IntPtr.Zero; }
            if (_sysMemQuery != IntPtr.Zero) { NativeMethods.PdhCloseQuery(_sysMemQuery); _sysMemQuery = IntPtr.Zero; }
            if (_thermalQuery != IntPtr.Zero) { NativeMethods.PdhCloseQuery(_thermalQuery); _thermalQuery = IntPtr.Zero; }
            _thermalCounters.Clear();
            _vramCounters.Clear();
            _nvml.Dispose();
        }
        _load.Dispose();
    }
}