using System.Runtime.InteropServices;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Interop;

namespace WindowsDynamicBar.Services.SystemMonitor;

/// <summary>Samples whole-machine CPU busy time with GetSystemTimes every 5 s (one cheap system
/// call) and feeds <see cref="LoadAverages"/>. Started the first time telemetry is used.</summary>
internal sealed class CpuLoadAverager : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private readonly object _sync = new();
    private readonly LoadAverages _avg = new(Interval);
    private Timer? _timer;
    private ulong _prevIdle, _prevTotal;
    private bool _havePrev;

    public void Start()
    {
        lock (_sync)
        {
            if (_timer is not null) return;
            _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, Interval);
        }
    }

    public (double? M1, double? M5, double? M15) Averages()
    {
        lock (_sync) return _avg.Averages();
    }

    private void Sample()
    {
        if (!NativeMethods.GetSystemTimes(out long idle, out long kernel, out long user)) return;
        ulong total = (ulong)kernel + (ulong)user;   // kernel time includes idle time
        lock (_sync)
        {
            if (_havePrev && total > _prevTotal)
            {
                double dTotal = total - _prevTotal;
                double dIdle = (ulong)idle - _prevIdle;
                _avg.Push((1 - dIdle / dTotal) * 100);
            }
            _prevIdle = (ulong)idle;
            _prevTotal = total;
            _havePrev = true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
