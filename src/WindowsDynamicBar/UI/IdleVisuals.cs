using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using WindowsDynamicBar.Core.Idle;

namespace WindowsDynamicBar.UI;

/// <summary>Small ring that fills once per minute (collapsed idle pill). Redraws only when set.</summary>
public sealed class SecondsRing : FrameworkElement
{
    private static readonly Brush TrackBrush = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private double _fraction;
    private Pen? _arcPen;
    private Pen? _trackPen;

    public double Thickness { get; set; } = 3;

    public SecondsRing() => IsHitTestVisible = false;

    public double Fraction
    {
        get => _fraction;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(value - _fraction) < 0.0001) return;
            _fraction = value;
            InvalidateVisual();
        }
    }

    public Color Color
    {
        set
        {
            _arcPen = new Pen(Frozen(value), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _arcPen.Freeze();
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;
        double r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);

        _trackPen ??= FrozenPen(TrackBrush, Thickness);
        dc.DrawEllipse(null, _trackPen, c, r, r);
        if (_arcPen is null || _fraction <= 0.001) return;

        if (_fraction >= 0.999)
        {
            dc.DrawEllipse(null, _arcPen, c, r, r);
            return;
        }
        double a = _fraction * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, _fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, _arcPen, g);
    }

    private static Pen FrozenPen(Brush b, double t) { var p = new Pen(b, t); p.Freeze(); return p; }
    internal static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>Dotted progress bar (2 px dots, 2 px gaps); filled columns use the accent colour.</summary>
public sealed class DotProgressBar : FrameworkElement
{
    private static readonly Brush EmptyBrush = SecondsRing.Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private double _fraction;
    private Brush _fill = SecondsRing.Frozen(Colors.White);

    public double DotSize { get; set; } = 2;
    public double Gap { get; set; } = 2;

    public DotProgressBar() => IsHitTestVisible = false;

    public double Fraction
    {
        get => _fraction;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(value - _fraction) < 0.0005) return;
            _fraction = value;
            InvalidateVisual();
        }
    }

    public Color Color
    {
        set { _fill = SecondsRing.Frozen(value); InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double step = DotSize + Gap;
        int cols = (int)Math.Floor((w + Gap) / step);
        int rows = Math.Max(1, (int)Math.Floor((h + Gap) / step));
        if (cols <= 0) return;
        double y0 = (h - (rows * step - Gap)) / 2;
        int filled = (int)Math.Round(_fraction * cols);
        for (int c = 0; c < cols; c++)
        {
            Brush b = c < filled ? _fill : EmptyBrush;
            for (int r = 0; r < rows; r++)
                dc.DrawRectangle(b, null, new Rect(c * step, y0 + r * step, DotSize, DotSize));
        }
    }
}

/// <summary>
/// 5x7 dot-matrix "HH:mm" clock drawn in one OnRender (no per-dot elements). When a digit changes
/// each dot eases to its new state (exponential, factor dt*11) after a delay of column*34 ms +
/// row*6 ms, so the change ripples left to right; lit dots also scale 70%..100%. A frame loop runs
/// only while digit dots are moving and unhooks itself once everything settles. The colon blink
/// switches in one frame: on a layered window every animated frame is costly, and a per-second
/// eased blink would keep the render loop busy ~40% of the time.
/// </summary>
public sealed class DotMatrixClock : FrameworkElement
{
    private const double Cell = 7;
    private const double Dot = 6;
    private const int Levels = 24;     // quantised brightness levels per row for brush caching

    private static readonly Brush UnlitBrush = SecondsRing.Frozen(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));

    private readonly double[] _value = new double[DotFont.Rows * DotFont.Columns];
    private readonly bool[] _target = new bool[DotFont.Rows * DotFont.Columns];
    private readonly double[] _startAt = new double[DotFont.Rows * DotFont.Columns];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Brush[,]? _lit;
    private double _hue = 200;
    private bool _initialized;
    private bool _looping;
    private double _lastFrameMs;

    public bool ReduceMotion { get; set; }

    public DotMatrixClock()
    {
        IsHitTestVisible = false;
        Width = DotFont.Columns * Cell;
        Height = DotFont.Rows * Cell;
        IsVisibleChanged += (_, _) => { if (!IsVisible) SnapToTargets(); };
    }

    public double Hue
    {
        get => _hue;
        set
        {
            if (Math.Abs(value - _hue) < 0.05 && _lit is not null) return;
            _hue = value;
            _lit = null;
            InvalidateVisual();
        }
    }

    /// <summary>Show a new time. Unchanged dots are untouched; changed dots ripple (or snap with Reduce Motion).</summary>
    public void SetTime(string hhmm, bool colonOn)
    {
        bool[,] map = DotFont.Render(hhmm, colonOn);
        double now = _clock.Elapsed.TotalMilliseconds;
        bool instant = !_initialized || ReduceMotion || !IsVisible;

        int firstChangedCol = -1;
        for (int c = 0; c < DotFont.Columns && firstChangedCol < 0; c++)
            for (int r = 0; r < DotFont.Rows; r++)
                if (c != DotFont.ColonColumn && map[r, c] != _target[r * DotFont.Columns + c]) { firstChangedCol = c; break; }

        bool any = false;
        for (int r = 0; r < DotFont.Rows; r++)
        {
            for (int c = 0; c < DotFont.Columns; c++)
            {
                int i = r * DotFont.Columns + c;
                if (map[r, c] == _target[i]) continue;
                _target[i] = map[r, c];
                any = true;
                if (instant || c == DotFont.ColonColumn) { _value[i] = map[r, c] ? 1 : 0; continue; }
                // Digit dots ripple from the first changed column rightwards.
                _startAt[i] = now + (c - firstChangedCol) * 34 + r * 6;
            }
        }
        _initialized = true;
        if (!any) return;
        if (instant || firstChangedCol < 0) { InvalidateVisual(); return; }   // colon only: one frame
        StartLoop();
    }

    private void StartLoop()
    {
        if (_looping) return;
        _looping = true;
        _lastFrameMs = _clock.Elapsed.TotalMilliseconds;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopLoop()
    {
        if (!_looping) return;
        _looping = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double dt = Math.Clamp((now - _lastFrameMs) / 1000.0, 0, 0.1);
        _lastFrameMs = now;
        double k = Math.Min(1, dt * 11);

        bool moving = false;
        for (int i = 0; i < _value.Length; i++)
        {
            double t = _target[i] ? 1 : 0;
            double v = _value[i];
            if (v == t) continue;
            moving = true;
            if (now < _startAt[i]) continue;
            v += (t - v) * k;
            if (Math.Abs(t - v) < 0.01) v = t;
            _value[i] = v;
        }
        InvalidateVisual();
        if (!moving) StopLoop();
    }

    private void SnapToTargets()
    {
        StopLoop();
        for (int i = 0; i < _value.Length; i++) _value[i] = _target[i] ? 1 : 0;
        InvalidateVisual();
    }

    private Brush LitBrush(int row, double v)
    {
        if (_lit is null)
        {
            _lit = new Brush[DotFont.Rows, Levels + 1];
            for (int r = 0; r < DotFont.Rows; r++)
                for (int l = 0; l <= Levels; l++)
                {
                    double q = (double)l / Levels;
                    // hsla(H + row*5, 72%, 60..66%, 0.14..1)
                    _lit[r, l] = SecondsRing.Frozen(IdleClock.Hsl(_hue + r * 5, 0.72, 0.60 + 0.06 * q, 0.14 + 0.86 * q));
                }
        }
        return _lit[row, (int)Math.Round(Math.Clamp(v, 0, 1) * Levels)];
    }

    protected override void OnRender(DrawingContext dc)
    {
        const double baseR = Dot / 2;
        for (int r = 0; r < DotFont.Rows; r++)
        {
            for (int c = 0; c < DotFont.Columns; c++)
            {
                var center = new Point(c * Cell + Cell / 2, r * Cell + Cell / 2);
                dc.DrawEllipse(UnlitBrush, null, center, baseR, baseR);
                double v = _value[r * DotFont.Columns + c];
                if (v <= 0.02) continue;
                double rad = baseR * (0.7 + 0.3 * v);
                dc.DrawEllipse(LitBrush(r, v), null, center, rad, rad);
            }
        }
    }
}
