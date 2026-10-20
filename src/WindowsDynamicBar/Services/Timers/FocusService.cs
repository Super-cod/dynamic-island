using WindowsDynamicBar.Core.Events;

namespace WindowsDynamicBar.Services.Timers;

public sealed record FocusInfo(TimeSpan Remaining, bool Running, string Phase);

/// <summary>Focus session (Pomodoro-style) with presets and completion notification.</summary>
public sealed class FocusService : IDisposable
{
    private readonly EventBus _bus;
    private readonly System.Threading.Timer _tick;
    private readonly object _sync = new();

    private TimeSpan _duration = TimeSpan.FromMinutes(25);
    private TimeSpan _remaining;
    private DateTime _resumeAt;
    private bool _running;
    private bool _notified;

    public event Action<FocusInfo>? FocusChanged;

    public FocusService(EventBus bus)
    {
        _bus = bus;
        _tick = new System.Threading.Timer(_ => OnTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public FocusInfo Current
    {
        get
        {
            lock (_sync)
            {
                long ticks = _running
                    ? (_remaining - (DateTime.UtcNow - _resumeAt)).Ticks
                    : _remaining.Ticks;
                var rem = TimeSpan.FromTicks(Math.Max(0, ticks));
                string phase = _running ? "Focusing" : rem <= TimeSpan.Zero && _duration > TimeSpan.Zero ? "Done" : "Ready";
                return new FocusInfo(rem, _running, phase);
            }
        }
    }

    public void Start(TimeSpan duration)
    {
        lock (_sync)
        {
            _duration = duration;
            _remaining = duration;
            _resumeAt = DateTime.UtcNow;
            _running = true;
            _notified = false;
        }
        _tick.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        Emit();
        // One card on start; ticks then refresh the collapsed pill / open card quietly.
        _bus.Raise(BarEvent.Create(BarEventKind.Focus, "Focus", TimerService.Format(duration),
            value: 0, valueText: TimerService.Format(duration), payload: Current,
            priority: 57, displayDuration: TimeSpan.FromSeconds(4)));
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (!_running) return;
            _remaining -= DateTime.UtcNow - _resumeAt;
            _running = false;
        }
        _tick.Change(Timeout.Infinite, Timeout.Infinite);
        Emit();
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_running || _remaining <= TimeSpan.Zero) return;
            _resumeAt = DateTime.UtcNow;
            _running = true;
        }
        _tick.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        Emit();
    }

    public void Stop()
    {
        lock (_sync) { _running = false; _remaining = TimeSpan.Zero; }
        _tick.Change(Timeout.Infinite, Timeout.Infinite);
        Emit();
    }

    private void OnTick()
    {
        bool done = false;
        lock (_sync)
        {
            if (!_running) return;
            _remaining -= DateTime.UtcNow - _resumeAt;
            _resumeAt = DateTime.UtcNow;
            if (_remaining <= TimeSpan.Zero)
            {
                _remaining = TimeSpan.Zero;
                _running = false;
                done = !_notified;
                _notified = true;
            }
        }
        Emit();
        if (done)
        {
            _tick.Change(Timeout.Infinite, Timeout.Infinite);
            _bus.Raise(BarEvent.Create(BarEventKind.Toast, "Focus session finished",
                "Take a break", priority: 99, displayDuration: TimeSpan.FromSeconds(8)));
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        }
    }

    private void Emit() => FocusChanged?.Invoke(Current);

    public void Dispose() => _tick.Dispose();
}
