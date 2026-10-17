using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.Services.Timers;

public enum TimerState { Idle, Running, Paused, Finished }

public sealed record TimerInfo(string Label, TimeSpan Remaining, TimerState State, double Progress);

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
        _tick.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
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
        _tick.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
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
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        var r = TimeSpan.FromSeconds(Math.Ceiling(ts.TotalSeconds));
        if (r.TotalHours >= 1) return $"{(int)r.TotalHours:D2}:{r.Minutes:D2}:{r.Seconds:D2}";
        return $"{(int)r.TotalMinutes:D2}:{r.Seconds:D2}";
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
