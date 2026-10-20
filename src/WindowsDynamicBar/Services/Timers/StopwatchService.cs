using WindowsDynamicBar.Core.Events;

namespace WindowsDynamicBar.Services.Timers;

public sealed record StopwatchInfo(TimeSpan Elapsed, bool Running, IReadOnlyList<TimeSpan> Laps);

public sealed class StopwatchService : IDisposable
{
    private readonly EventBus _bus;
    private readonly System.Threading.Timer _tick;
    private readonly object _sync = new();
    private readonly List<TimeSpan> _laps = new();

    private TimeSpan _elapsed;
    private DateTime _runningSince;
    private bool _running;

    public event Action<StopwatchInfo>? StopwatchChanged;

    public StopwatchService(EventBus bus)
    {
        _bus = bus;
        _tick = new System.Threading.Timer(_ => Emit(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public StopwatchInfo Current
    {
        get
        {
            lock (_sync)
            {
                var e = _running ? _elapsed + (DateTime.UtcNow - _runningSince) : _elapsed;
                return new StopwatchInfo(e, _running, _laps.ToArray());
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_running) return;
            _runningSince = DateTime.UtcNow;
            _running = true;
        }
        _tick.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        Emit();
        // One card on start; the 100 ms ticks then drive the collapsed pill / open card.
        _bus.Raise(BarEvent.Create(BarEventKind.Stopwatch, "Stopwatch", FormatMs(Current.Elapsed),
            valueText: FormatMs(Current.Elapsed), payload: Current, priority: 56,
            displayDuration: TimeSpan.FromSeconds(3)));
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (!_running) return;
            _elapsed += DateTime.UtcNow - _runningSince;
            _running = false;
        }
        _tick.Change(Timeout.Infinite, Timeout.Infinite);
        Emit();
    }

    public void Reset()
    {
        lock (_sync) { _elapsed = TimeSpan.Zero; _running = false; _laps.Clear(); }
        _tick.Change(Timeout.Infinite, Timeout.Infinite);
        Emit();
    }

    public void Lap()
    {
        lock (_sync)
        {
            var e = _running ? _elapsed + (DateTime.UtcNow - _runningSince) : _elapsed;
            _laps.Add(e);
        }
        Emit();
    }

    private void Emit()
    {
        var info = Current;
        StopwatchChanged?.Invoke(info);
    }

    public static string FormatMs(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
    }

    public void Dispose() => _tick.Dispose();
}
