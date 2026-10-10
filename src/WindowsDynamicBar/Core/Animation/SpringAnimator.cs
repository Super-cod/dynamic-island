using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace WindowsDynamicBar.Core.Animation;

/// <summary>
/// Physics-based spring animation engine running on the UI dispatcher at display refresh rate.
/// Uses critically-damped / under-damped spring integration (semi-implicit Euler) so motion
/// matches the fluid feel of iOS/MacOS springs rather than linear/eased interpolation.
/// Animates Width/Height/Opacity/CornerRadius/TranslateScale of target elements.
/// </summary>
public sealed class SpringAnimator : IDisposable
{
    public double Stiffness { get; set; } = 220;
    public double Damping { get; set; } = 24;
    public double Mass { get; set; } = 1.0;
    public double Epsilon { get; set; } = 0.02;

    private sealed class Track
    {
        public double Current;
        public double Target;
        public double Velocity;
        public Action<double>? Apply;
        public bool Active = true;
    }

    private readonly Dictionary<string, Track> _tracks = new();
    private readonly DispatcherTimer _timer;
    private readonly double _speedMultiplier;
    private bool _disposed;

    public event Action? AnimationCompleted;

    public SpringAnimator(double speedMultiplier = 1.0)
    {
        _speedMultiplier = Math.Max(0.1, speedMultiplier);
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0)
        };
        _timer.Tick += OnTick;
    }

    public bool IsAnimating => _tracks.Values.Any(t => t.Active);

    public void Animate(string key, double from, double to, Action<double> apply, double? velocity = null)
    {
        if (_disposed) return;
        _tracks.TryGetValue(key, out var track);
        if (track is null)
        {
            track = new Track { Apply = apply };
            _tracks[key] = track;
        }
        track.Current = from;
        track.Target = to;
        track.Velocity = velocity ?? track.Velocity;
        track.Active = true;
        track.Apply = apply;
        apply(from);
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void SetTarget(string key, double to)
    {
        if (_tracks.TryGetValue(key, out var t) && t.Active)
        {
            t.Target = to;
            if (!_timer.IsEnabled) _timer.Start();
        }
    }

    public void Jump(string key, double value, Action<double> apply)
    {
        _tracks[key] = new Track { Current = value, Target = value, Apply = apply, Active = false };
        apply(value);
    }

    public void Stop(string key)
    {
        if (_tracks.TryGetValue(key, out var t)) t.Active = false;
    }

    public void StopAll()
    {
        foreach (var t in _tracks.Values) t.Active = false;
        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Fixed-ish timestep with clamp; scaled by speed setting (faster = stiffer springs).
        double dt = (1.0 / 60.0) * _speedMultiplier;
        double k = Stiffness * _speedMultiplier * _speedMultiplier;
        double c = Damping * _speedMultiplier;
        bool any = false;

        foreach (var track in _tracks.Values)
        {
            if (!track.Active) continue;

            double displacement = track.Current - track.Target;
            // semi-implicit Euler spring integration
            double acceleration = (-k * displacement - c * track.Velocity) / Mass;
            track.Velocity += acceleration * dt;
            track.Current += track.Velocity * dt;

            bool settled = Math.Abs(track.Velocity) < Epsilon && Math.Abs(displacement) < 0.15;
            if (settled)
            {
                track.Current = track.Target;
                track.Velocity = 0;
                track.Active = false;
            }

            track.Apply?.Invoke(track.Current);
            any |= track.Active;
        }

        if (!any)
        {
            _timer.Stop();
            AnimationCompleted?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _tracks.Clear();
    }
}

/// <summary>Cubic-bezier easing for fade/scale transitions (opacity cannot be springed meaningfully).</summary>
public static class Easing
{
    public static double CubicOut(double t) => 1.0 - Math.Pow(1.0 - t, 3);
    public static double QuintOut(double t) => 1.0 - Math.Pow(1.0 - t, 5);
    public static double ExpoOut(double t) => t >= 1.0 ? 1.0 : 1.0 - Math.Pow(2.0, -10.0 * t);
    public static double BackOut(double t)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }
}

/// <summary>Drives a value from → to over duration with easing, via render-priority dispatcher timer.</summary>
public sealed class TweenAnimation : IDisposable
{
    private readonly DispatcherTimer _timer;
    private DateTime _start;
    private TimeSpan _duration;
    private double _from, _to;
    private Func<double, double> _easing;
    private Action<double>? _apply;
    private bool _disposed;

    public event Action? Completed;

    public TweenAnimation()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 120.0) };
        _timer.Tick += OnTick;
        _easing = Easing.QuintOut;
    }

    public void Run(double from, double to, TimeSpan duration, Action<double> apply, Func<double, double>? easing = null)
    {
        if (_disposed) return;
        _from = from; _to = to;
        _duration = duration <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : duration;
        _apply = apply;
        _easing = easing ?? Easing.QuintOut;
        _start = DateTime.UtcNow;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public bool IsRunning => _timer.IsEnabled;

    private void OnTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _start;
        double t = Math.Min(1.0, elapsed.TotalMilliseconds / _duration.TotalMilliseconds);
        _apply?.Invoke(_from + (_to - _from) * _easing(t));
        if (t >= 1.0)
        {
            _timer.Stop();
            Completed?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _apply = null;
    }
}
