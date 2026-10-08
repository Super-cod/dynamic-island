using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Interop;

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
    public TelemetryHistory History { get; } = new(60);
}

public sealed class SystemTelemetryService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly object _sync = new();

    private IntPtr _cpuQuery = IntPtr.Zero;
    private IntPtr _cpuCounter = IntPtr.Zero;
    private IntPtr _engineQuery = IntPtr.Zero;
    private IntPtr _gpuEngineCounter = IntPtr.Zero;
    private bool _engineExpanded;

    private IntPtr _memQuery = IntPtr.Zero;
    private readonly List<VramCounter> _vramCounters = new();
    private bool _vramAvailable;

    private System.Threading.Timer? _timer;
    private bool _active;

    private double _cpu;
    private double _ramPercent;
    private long _ramUsed;
    private long _ramTotal;
    private bool _cpuAvailable;
    private bool _memoryAvailable;

    private CpuInfo? _cpuInfo;
    private readonly List<GpuTrack> _gpuTracks = new();
    private readonly Dictionary<long, int> _dxgiIndexByLuid = new();
    private readonly Dictionary<long, string> _dxgiNameByLuid = new();
    private int _samplesSinceAdapterRefresh;

    public event Action<TelemetrySnapshot>? TelemetryUpdated;

    public TelemetryHistory CpuHistory { get; } = new(60);
    public TelemetryHistory RamHistory { get; } = new(60);
    public bool IsActive { get { lock (_sync) return _active; } }

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
                foreach (var mo in searcher.Get())
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
                if (PdhOpenQueryW(null, IntPtr.Zero, out _cpuQuery) == 0 &&
                    PdhAddEnglishCounterW(_cpuQuery, @"\Processor(_Total)\% Processor Time",
                        IntPtr.Zero, out _cpuCounter) == 0)
                {
                    PdhCollectQueryData(_cpuQuery);
                    _cpuAvailable = true;
                }
                else _cpuAvailable = false;
            }

            if (!_engineExpanded)
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out _engineQuery) == 0 &&
                    PdhAddEnglishCounterW(_engineQuery, @"\GPU Engine(*)\Utilization Percentage",
                        IntPtr.Zero, out _gpuEngineCounter) == 0)
                {
                    PdhCollectQueryData(_engineQuery);
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
                if (memInstances.Count > 0 && PdhOpenQueryW(null, IntPtr.Zero, out _memQuery) == 0)
                {
                    foreach (string inst in memInstances)
                    {
                        if (PdhAddEnglishCounterW(_memQuery, $@"\GPU Adapter Memory({inst})\Dedicated Usage",
                                IntPtr.Zero, out IntPtr u) == 0 &&
                            PdhAddEnglishCounterW(_memQuery, $@"\GPU Adapter Memory({inst})\Dedicated Limit",
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
                        PdhCollectQueryData(_memQuery);
                        _vramAvailable = true;
                    }
                }
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
                            VramTotal = a.DedicatedVideoMemoryBytes,
                            VramAvailable = a.DedicatedVideoMemoryBytes > 0
                        });
                    }
                }
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
                    else if (string.IsNullOrEmpty(track.Name))
                    {
                        track.Name = $"GPU {track.Luid & 0xFF}";
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
                SampleGpu();
                SampleMemory();
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
        PdhCollectQueryData(_cpuQuery);
        var buf = new PDH_FMT_COUNTERVALUE_DOUBLE[1];
        if (PdhGetFormattedCounterValueDouble(_cpuCounter, out _, buf) == 0)
            _cpu = Math.Round(Math.Clamp(buf[0].doubleValue, 0, 100), 1);
    }

    private void SampleGpu()
    {
        var sums = new Dictionary<long, double>();
        if (_engineQuery != IntPtr.Zero && _gpuEngineCounter != IntPtr.Zero)
        {
            PdhCollectQueryData(_engineQuery);
            uint bufferSize = 0, itemCount = 0;
            int status = PdhGetFormattedCounterArrayW(_gpuEngineCounter, 0x00000200 /*PDH_FMT_DOUBLE*/,
                ref bufferSize, ref itemCount, IntPtr.Zero);
            if (status == unchecked((int)0x800007D2) && itemCount > 0)
            {
                IntPtr pBuf = Marshal.AllocHGlobal((int)bufferSize);
                try
                {
                    status = PdhGetFormattedCounterArrayW(_gpuEngineCounter, 0x00000200,
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
            if (track.VramTotal > 0 && !track.VramAvailable) track.VramAvailable = true;
        }

        foreach (var luid in sums.Keys.ToList())
        {
            if (!_gpuTracks.Any(t => t.Luid == luid))
            {
                _gpuTracks.Add(new GpuTrack
                {
                    Luid = luid,
                    AdapterIndex = _dxgiIndexByLuid.TryGetValue(luid, out int ai) ? ai : -1,
                    Name = _dxgiNameByLuid.TryGetValue(luid, out string? n) ? n : $"GPU {luid & 0xFF}",
                    Utilization = Math.Round(sums[luid], 1)
                });
            }
        }

        if (_vramAvailable && _memQuery != IntPtr.Zero && _vramCounters.Count > 0)
        {
            PdhCollectQueryData(_memQuery);
            for (int i = 0; i < _vramCounters.Count; i++)
            {
                var vc = _vramCounters[i];
                var ub = new PDH_FMT_COUNTERVALUE_DOUBLE[1];
                var lb = new PDH_FMT_COUNTERVALUE_DOUBLE[1];
                if (PdhGetFormattedCounterValueDouble(vc.Usage, out _, ub) != 0) continue;
                if (PdhGetFormattedCounterValueDouble(vc.Limit, out _, lb) != 0) continue;
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
        var mi = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };
        if (GlobalMemoryStatusEx(ref mi))
        {
            _ramTotal = (long)mi.ullTotalPhys;
            long avail = (long)mi.ullAvailPhys;
            _ramUsed = Math.Max(0, _ramTotal - avail);
            _ramPercent = _ramTotal > 0
                ? Math.Round(Math.Clamp((double)_ramUsed / _ramTotal * 100, 0, 100), 1)
                : 0;
            _memoryAvailable = true;
        }
        else _memoryAvailable = false;
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
        return new TelemetrySnapshot(cpu, gpus, memory, DateTime.UtcNow);
    }

    private static List<string> ExpandInstances(string wildcard)
    {
        var list = new List<string>();
        try
        {
            uint size = 0;
            int status = PdhExpandWildCardPathW(null, wildcard, IntPtr.Zero, ref size, 0);
            if (status != 0 && status != PDH_MORE_DATA) return list;
            if (size == 0) return list;
            IntPtr p = Marshal.AllocHGlobal((int)size * 2 + 8);
            try
            {
                status = PdhExpandWildCardPathW(null, wildcard, p, ref size, 0);
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

    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    public void Dispose()
    {
        Deactivate();
        if (_cpuQuery != IntPtr.Zero) { PdhCloseQuery(_cpuQuery); _cpuQuery = IntPtr.Zero; }
        if (_engineQuery != IntPtr.Zero) { PdhCloseQuery(_engineQuery); _engineQuery = IntPtr.Zero; }
        if (_memQuery != IntPtr.Zero) { PdhCloseQuery(_memQuery); _memQuery = IntPtr.Zero; }
        _vramCounters.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_DOUBLE { public int status; public double doubleValue; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValueDouble(IntPtr counter, out IntPtr type,
        [Out] PDH_FMT_COUNTERVALUE_DOUBLE[] value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, int format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhExpandWildCardPathW(string? szDataSource, string szWildCardPath,
        IntPtr mszExpandedPathList, ref uint pcchPathListLength, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}