using System.Diagnostics;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.Services.Timers;

public enum TimerState { Idle, Running, Paused, Finished }

public sealed record TimerInfo(string Label, TimeSpan Remaining, TimerState State, double Progress);
public sealed record StopwatchInfo(TimeSpan Elapsed, bool Running, IReadOnlyList<TimeSpan> Laps);
public sealed record FocusInfo(TimeSpan Remaining, bool Running, string Phase);

/// <summary>The one countdown timer (timer panel, search "timer 10 min", idle Focus button).
/// Pause/resume/cancel and a completion event. Its end time is kept in state.json so a running
/// timer survives an app restart.</summary>
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

    public void Start(TimeSpan duration, string label = "Timer") => Start(duration, duration, label, announce: true);

    private void Start(TimeSpan remaining, TimeSpan total, string label, bool announce)
    {
        lock (_sync)
        {
            _total = total;
            _remaining = remaining;
            _state = TimerState.Running;
            _resumeAtUtc = DateTime.UtcNow;
            _label = label;
            _notified = false;
        }
        Persist();
        _tick.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
        Emit();
        if (!announce) return;
        var duration = remaining;
        // One card on start; after it expires the collapsed pill carries the countdown.
        _bus.Raise(BarEvent.Create(BarEventKind.Timer, label,
            Format(duration), value: 0, valueText: Format(duration),
            payload: Current, priority: 58, displayDuration: TimeSpan.FromSeconds(4)));
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
        Persist();
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
        Persist();
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
        Persist();
        Emit();
    }

    private void OnTick()
    {
        TimerInfo info;
        bool finished = false;
        lock (_sync)
        {
            if (_state != TimerState.Running) return;
            // _remaining is the time left at _resumeAtUtc; it only changes on pause/finish.
            // (Writing the live value back every tick subtracted the elapsed time again and again,
            // so timers ended far too early.)
            var remaining = _remaining - (DateTime.UtcNow - _resumeAtUtc);
            if (remaining <= TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
                _remaining = TimeSpan.Zero;
                _state = TimerState.Finished;
                finished = !_notified;
                _notified = true;
            }
            double prog = _total > TimeSpan.Zero
                ? Math.Clamp(1.0 - (remaining.Ticks / (double)_total.Ticks), 0, 1)
                : 0;
            info = new TimerInfo(_label, remaining, _state, prog);
        }
        TimerChanged?.Invoke(info);

        // Running ticks are ambient: the collapsed pill and the open card refresh via
        // TimerChanged; only the completion is worth popping the bar for.
        if (finished)
        {
            _tick.Change(Timeout.Infinite, Timeout.Infinite);
            Persist();
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

    /// <summary>Write the end time (running), the remaining time (paused) or nothing (idle/finished).</summary>
    private void Persist()
    {
        TimerState state; TimeSpan remaining; TimeSpan total; string label; DateTime resumeAt;
        lock (_sync) { state = _state; remaining = _remaining; total = _total; label = _label; resumeAt = _resumeAtUtc; }
        RuntimeState.Instance.Update(s =>
        {
            s.TimerLabel = label;
            s.TimerTotalSeconds = total.TotalSeconds;
            s.TimerEndUtc = state == TimerState.Running ? resumeAt + remaining : null;
            s.TimerPausedSeconds = state == TimerState.Paused ? Math.Max(0, remaining.TotalSeconds) : 0;
        });
    }

    /// <summary>App start: continue a timer that was running (or paused) when the app last closed.
    /// One that ran out while the app was closed raises its "Timer finished" card once.</summary>
    public void RestoreFromState()
    {
        var s = RuntimeState.Instance.Current;
        var total = TimeSpan.FromSeconds(Math.Max(0, s.TimerTotalSeconds));
        string label = string.IsNullOrWhiteSpace(s.TimerLabel) ? "Timer" : s.TimerLabel;
        if (s.TimerEndUtc is DateTime end)
        {
            var left = end - DateTime.UtcNow;
            if (left > TimeSpan.FromSeconds(1))
            {
                Start(left, total > left ? total : left, label, announce: false);
                Log.Info("Timer restored", ("remainingSec", (int)left.TotalSeconds));
                return;
            }
            RuntimeState.Instance.Update(x => { x.TimerEndUtc = null; x.TimerPausedSeconds = 0; });
            if (left > TimeSpan.FromHours(-12))
                _bus.Raise(BarEvent.Create(BarEventKind.Toast, "Timer finished", label,
                    payload: label, priority: 99, displayDuration: TimeSpan.FromSeconds(8)));
            return;
        }
        if (s.TimerPausedSeconds > 0)
        {
            lock (_sync)
            {
                _total = total > TimeSpan.Zero ? total : TimeSpan.FromSeconds(s.TimerPausedSeconds);
                _remaining = TimeSpan.FromSeconds(s.TimerPausedSeconds);
                _state = TimerState.Paused;
                _label = label;
            }
            Emit();
        }
    }

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
            _bus.Raise(BarEvent.Create(BarEventKind.Toast, "Focus session finished",
                "Take a break", priority: 99, displayDuration: TimeSpan.FromSeconds(8)));
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        }
    }

    private void Emit() => FocusChanged?.Invoke(Current);

    public void Dispose() => _tick.Dispose();
}
