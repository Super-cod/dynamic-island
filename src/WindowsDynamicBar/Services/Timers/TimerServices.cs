using System.Diagnostics;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.Services.Timers;

public enum TimerState { Idle, Running, Paused, Finished }

public sealed record TimerInfo(string Label, TimeSpan Remaining, TimerState State, double Progress);
public sealed record StopwatchInfo(TimeSpan Elapsed, bool Running, IReadOnlyList<TimeSpan> Laps);
public sealed record FocusInfo(TimeSpan Remaining, bool Running, string Phase);

/// <summary>Integrated countdown timer with presets, pause/resume/cancel, and completion event.</summary>
public sealed class TimerService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private readonly System.Threading.Timer _tick;
    private readonly object _sync = new();

    private TimeSpan _total = TimeSpan.Zero;
    private TimeSpan _remaining = TimeSpan.Zero;
    private DateTime _resumeAtUtc;
    private TimerState _state = TimerState.Idle;
    private string _label = "Timer";
    private bool _notified;

    public event Action<TimerInfo>? TimerChanged;

    public TimerService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
        _tick = new System.Threading.Timer(_ => OnTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public TimerInfo Current
    {
        get
        {
            lock (_sync)
            {
                var remaining = _state == TimerState.Running
                    ? _remaining - (DateTime.UtcNow - _resumeAtUtc)
                    : _remaining;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                double progress = _total.TotalSeconds <= 0
                    ? 0
                    : Math.Clamp(1 - remaining.TotalSeconds / _total.TotalSeconds, 0, 1);
                return new TimerInfo(_label, remaining, _state, progress);
            }
        }
    }

    public void Start(TimeSpan duration, string label = "Timer")
    {
        lock (_sync)
        {
            _total = duration;
            _remaining = duration;
            _state = TimerState.Running;
            _resumeAtUtc = DateTime.UtcNow;
            _label = label;
            _notified = false;
        }
        _tick.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
        Emit();
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_state != TimerState.Running) return;
            _remaining -= DateTime.UtcNow - _resumeAtUtc;
            _state = TimerState.Paused;
        }
        _tick.Change(Timeout.Infinite, Timeout.Infinite);
        Emit();
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_state != TimerState.Paused) return;
            _resumeAtUtc = DateTime.UtcNow;
            _state = TimerState.Running;
        }
        _tick.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
        Emit();
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _state = TimerState.Idle;
            _remaining = TimeSpan.Zero;
            _total = TimeSpan.Zero;
        }
        _tick.Change(Timeout.Infinite, Timeout.Infinite);
        Emit();
    }

    private void OnTick()
    {
        TimerInfo info;
        bool finished = false;
        lock (_sync)
        {
            if (_state != TimerState.Running) return;
            var remaining = _remaining - (DateTime.UtcNow - _resumeAtUtc);
            if (remaining <= TimeSpan.Zero)
            {
                _remaining = TimeSpan.Zero;
                _state = TimerState.Finished;
                finished = !_notified;
                _notified = true;
            }
            else _remaining = remaining;
            double prog = _total > TimeSpan.Zero
                ? Math.Clamp(1.0 - (_remaining.Ticks / (double)_total.Ticks), 0, 1)
                : 0;
            info = new TimerInfo(_label, _remaining, _state, prog);
        }
        TimerChanged?.Invoke(info);

        // Show in bar every second while running (coalesced by EventBus kind).
        if (info.State == TimerState.Running)
        {
            _bus.Raise(BarEvent.Create(BarEventKind.Timer, _label,
                Format(info.Remaining), value: info.Progress, valueText: Format(info.Remaining),
                payload: info, priority: 58, displayDuration: TimeSpan.FromSeconds(2)));
        }
        else if (finished)
        {
            _bus.Raise(BarEvent.Create(BarEventKind.Toast, "Timer finished", _label,
                payload: _label, priority: 99, displayDuration: TimeSpan.FromSeconds(8)));
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        }
    }

    public static string Format(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }

    private void Emit() => TimerChanged?.Invoke(Current);

    public void Dispose() => _tick.Dispose();
}

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
        if (info.Running)
            _bus.Raise(BarEvent.Create(BarEventKind.Stopwatch, "Stopwatch", FormatMs(info.Elapsed),
                valueText: FormatMs(info.Elapsed), payload: info, priority: 56,
                displayDuration: TimeSpan.FromSeconds(2)));
    }

    public static string FormatMs(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
    }

    public void Dispose() => _tick.Dispose();
}

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
        FocusInfo info;
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
            info = Current;
        }
        Emit();
        if (done)
        {
            _bus.Raise(BarEvent.Create(BarEventKind.Toast, "Focus session complete",
                "Take a break", priority: 99, displayDuration: TimeSpan.FromSeconds(8)));
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        }
        else if (info.Running)
        {
            _bus.Raise(BarEvent.Create(BarEventKind.Focus, "Focus", TimerService.Format(info.Remaining),
                value: _duration.TotalSeconds > 0 ? 1 - info.Remaining.TotalSeconds / _duration.TotalSeconds : 0,
                valueText: TimerService.Format(info.Remaining), payload: info, priority: 57,
                displayDuration: TimeSpan.FromSeconds(2)));
        }
    }

    private void Emit() => FocusChanged?.Invoke(Current);

    public void Dispose() => _tick.Dispose();
}
