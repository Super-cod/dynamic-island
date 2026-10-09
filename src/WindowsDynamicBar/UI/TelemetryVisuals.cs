using System;
using System.Windows;
using System.Windows.Media;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Circular progress ring around an icon: faint full track plus a value arc drawn from the top
/// clockwise. Used for the CPU/GPU/RAM icon gauges (single OnRender pass, frozen brushes/pens).
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    private static readonly Pen TrackPen = FrozenPen(Color.FromArgb(40, 128, 128, 128), 2.5);

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingColorProperty =
        DependencyProperty.Register(nameof(RingColor), typeof(Color), typeof(RingGauge),
            new FrameworkPropertyMetadata(Color.FromRgb(0x7C, 0x8C, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty =
        DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(2.5, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0..100 progress, or negative for "no data" (track only).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Color RingColor
    {
        get => (Color)GetValue(RingColorProperty);
        set => SetValue(RingColorProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double r = Math.Min(w, h) / 2 - Thickness / 2;
        if (r <= 0.5) return;

        var center = new Point(w / 2, h / 2);
        dc.DrawEllipse(null, TrackPen, center, r, r);

        double v = Value;
        if (v <= 0) return;
        double angle = Math.Clamp(v, 0, 100) / 100.0 * 360.0;
        if (angle >= 359.999) angle = 359.999;

        Point start = PointAt(center, r, -90);
        Point end = PointAt(center, r, -90 + angle);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, angle > 180,
                SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();

        var pen = FrozenPen(RingColor, Thickness);
        dc.DrawGeometry(null, pen, geo);
    }

    private static Point PointAt(Point c, double r, double deg)
    {
        double rad = deg * Math.PI / 180.0;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    private static Pen FrozenPen(Color c, double thickness)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        var p = new Pen(b, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }
}

/// <summary>
/// btop-style dot-matrix history graph. One column per sample, filled from the bottom.
/// Rendered entirely in a single <see cref="OnRender"/> pass (no per-cell elements).
/// Brushes are cached and frozen; ghost cells are faint white.
/// </summary>
public sealed class DotMatrixGraph : FrameworkElement
{
    private const double Cell = 4.0;
    private const double Dot = 3.0;

    // Theme-neutral ghost dots (visible on both dark and light pill backgrounds).
    private static readonly Brush GhostBrush = Frozen(Color.FromArgb(34, 128, 128, 128));

    private Brush[] _rowBrushes = Array.Empty<Brush>();
    private int _rowBrushCount = -1;

    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(double[]), typeof(DotMatrixGraph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScrollProperty =
        DependencyProperty.Register(nameof(Scroll), typeof(double), typeof(DotMatrixGraph),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Fraction (0..1) of a cell the graph has scrolled since the newest sample.</summary>
    public double Scroll
    {
        get => (double)GetValue(ScrollProperty);
        set => SetValue(ScrollProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;

        int rows = (int)Math.Floor(h / Cell);
        if (rows < 1) return;
        EnsureRowBrushes(rows);

        int cols = (int)Math.Floor(w / Cell) + 2;
        var values = Values;
        int n = values?.Length ?? 0;
        double shift = Math.Clamp(Scroll, 0, 1) * Cell;

        for (int c = 0; c < cols; c++)
        {
            int idx = n - cols + c;              // column c maps to sample index (from the right)
            double v = idx >= 0 && idx < n ? values![idx] : double.NaN;
            double x = w - (cols - c) * Cell + Cell - shift;

            int filled = double.IsNaN(v) ? 0 : (int)Math.Round(Math.Clamp(v, 0, 100) / 100.0 * rows);
            for (int r = 0; r < rows; r++)
            {
                double y = h - (r + 1) * Cell;
                var brush = r < filled ? _rowBrushes[r] : GhostBrush;
                dc.DrawRectangle(brush, null, new Rect(x, y, Dot, Dot));
            }
        }
    }

    private void EnsureRowBrushes(int rows)
    {
        if (rows == _rowBrushCount) return;
        _rowBrushCount = rows;
        var arr = new Brush[rows];
        for (int r = 0; r < rows; r++)
            arr[r] = Frozen(HeightColor(rows <= 1 ? 0 : (double)r / (rows - 1)));
        _rowBrushes = arr;
    }

    /// <summary>bottom green → middle yellow → top red (matches the btop reference hsl ramp).</summary>
    private static Color HeightColor(double t)
    {
        // hue 110 -> 0, saturation 55%, lightness 58%
        double hue = (1.0 - Math.Clamp(t, 0, 1)) * 110.0;
        return HslToRgb(hue, 0.55, 0.58);
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>btop-style dotted horizontal bar: faint ghost ticks with a colored fill.</summary>
public sealed class DotBar : FrameworkElement
{
    private const double Tick = 2.0;
    private const double Gap = 1.0;

    private static readonly Brush GhostBrush = Frozen(Color.FromArgb(40, 128, 128, 128));

    /// <summary>Fill fraction, 0..1. Negative means "no data" (shows only ghost ticks).</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(DotBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillColorProperty =
        DependencyProperty.Register(nameof(FillColor), typeof(Color), typeof(DotBar),
            new FrameworkPropertyMetadata(Color.FromRgb(0x8A, 0xB4, 0x78), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Color FillColor
    {
        get => (Color)GetValue(FillColorProperty);
        set => SetValue(FillColorProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;

        double step = Tick + Gap;
        int ticks = (int)Math.Floor(w / step);
        if (ticks < 1) return;

        bool hasData = Value >= 0;
        double frac = hasData ? Math.Clamp(Value, 0, 1) : 0;
        int filled = (int)Math.Round(frac * ticks);
        var fill = Frozen(FillColor);

        for (int i = 0; i < ticks; i++)
        {
            double x = i * step;
            var brush = hasData && i < filled ? fill : GhostBrush;
            dc.DrawRectangle(brush, null, new Rect(x, 0, Tick, h));
        }
    }

    public static Color ValueColor(double percent)
    {
        double t = Math.Clamp(percent, 0, 100) / 100.0;
        double hue = (1.0 - t) * 110.0;
        return Hsl(hue, 0.55, 0.58);
    }

    private static Color Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Decorative "now playing" equalizer: a row of thin bars that undulate while audio plays.
/// Time-driven and deterministic (no captured audio, no fabricated data values); it is pure
/// ornamentation and renders a flat baseline when <see cref="Running"/> is false.
/// </summary>
public sealed class Equalizer : FrameworkElement
{
    private const double BarWidth = 3.0;
    private const double BarGap = 1.0;
    private readonly DateTime _t0 = DateTime.UtcNow;
    private bool _hooked;

    public static readonly DependencyProperty RunningProperty =
        DependencyProperty.Register(nameof(Running), typeof(bool), typeof(Equalizer),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnRunningChanged));

    public static readonly DependencyProperty ReduceMotionProperty =
        DependencyProperty.Register(nameof(ReduceMotion), typeof(bool), typeof(Equalizer),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnRunningChanged));

    public bool Running
    {
        get => (bool)GetValue(RunningProperty);
        set => SetValue(RunningProperty, value);
    }

    public bool ReduceMotion
    {
        get => (bool)GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    private static void OnRunningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Equalizer)d).SyncLoop();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double step = BarWidth + BarGap;
        int bars = (int)(w / step);
        if (bars < 1) return;

        double t = (DateTime.UtcNow - _t0).TotalSeconds;
        bool animate = Running && !ReduceMotion;

        for (int i = 0; i < bars; i++)
        {
            double ratio = animate ? BarHeightAt(t, i, bars) : 0.06;
            double barH = Math.Max(2, ratio * h);
            double x = i * step;
            var brush = Frozen(Hsl(235 - ratio * 14, 0.70, 0.62 + ratio * 0.03));
            dc.DrawRectangle(brush, null, new Rect(x, h - barH, BarWidth, barH));
        }
    }

    private static double BarHeightAt(double t, int i, int bars)
    {
        double p = i / (double)Math.Max(1, bars - 1);
        double v = 0.5
            + 0.30 * Math.Sin(t * 3.1 + p * 9.0)
            + 0.16 * Math.Sin(t * 5.7 - p * 5.0 + 1.3)
            + 0.10 * Math.Sin(t * 2.3 + p * 17.0);
        return Math.Clamp(v, 0.05, 1.0);
    }

    private void SyncLoop()
    {
        bool want = IsVisible && Running && !ReduceMotion;
        if (want && !_hooked)
        {
            _hooked = true;
            CompositionTarget.Rendering += OnFrame;
        }
        else if (!want && _hooked)
        {
            _hooked = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        SyncLoop();
    }

    private static Color Hsl(double h, double s, double l)
    {
        h = (h % 360 + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Single-pass canvas for the big detail panel: a full-bleed dot-matrix history graph with a
/// faint 1px divider on its left edge and a shorter companion graph strip along the bottom edge.
/// The main graph always spans the whole control (including the area later covered by the stats
/// overlay) so the trace stays continuous behind the text.
/// </summary>
public sealed class DetailGraphCanvas : FrameworkElement
{
    private const double Cell = 4.0;
    private const double Dot = 3.0;

    private static readonly Brush GhostBrush = Frozen(Color.FromArgb(34, 128, 128, 128));
    private static readonly Pen DividerPen = FrozenPen(Color.FromArgb(48, 128, 128, 128), 1);
    private static readonly Brush CompanionScrim = Frozen(Color.FromArgb(120, 0, 0, 0));

    private Brush[] _mainBrushes = Array.Empty<Brush>();
    private int _mainRows = -1;
    private Brush[] _compBrushes = Array.Empty<Brush>();
    private int _compRows = -1;

    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(double[]), typeof(DetailGraphCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompanionValuesProperty =
        DependencyProperty.Register(nameof(CompanionValues), typeof(double[]), typeof(DetailGraphCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScrollProperty =
        DependencyProperty.Register(nameof(Scroll), typeof(double), typeof(DetailGraphCanvas),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompanionHeightProperty =
        DependencyProperty.Register(nameof(CompanionHeight), typeof(double), typeof(DetailGraphCanvas),
            new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double[]? CompanionValues
    {
        get => (double[]?)GetValue(CompanionValuesProperty);
        set => SetValue(CompanionValuesProperty, value);
    }

    public double Scroll
    {
        get => (double)GetValue(ScrollProperty);
        set => SetValue(ScrollProperty, value);
    }

    public double CompanionHeight
    {
        get => (double)GetValue(CompanionHeightProperty);
        set => SetValue(CompanionHeightProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;

        DrawGraph(dc, 0, 0, w, h, Values, Scroll, ref _mainBrushes, ref _mainRows);

        double ch = Math.Clamp(CompanionHeight, 8, h / 2);
        double cy = h - ch;
        dc.DrawRectangle(CompanionScrim, null, new Rect(0, cy, w, ch));
        DrawGraph(dc, 0, cy, w, ch, CompanionValues, Scroll, ref _compBrushes, ref _compRows);

        dc.DrawLine(DividerPen, new Point(0.5, 0), new Point(0.5, h));
    }

    private static void DrawGraph(DrawingContext dc, double x0, double y0, double w, double h,
        double[]? values, double scroll, ref Brush[] brushes, ref int cachedRows)
    {
        int rows = (int)Math.Floor(h / Cell);
        if (rows < 1) return;
        if (rows != cachedRows)
        {
            cachedRows = rows;
            var arr = new Brush[rows];
            for (int r = 0; r < rows; r++)
                arr[r] = Frozen(RowColor(rows <= 1 ? 0 : (double)r / (rows - 1)));
            brushes = arr;
        }

        int cols = (int)Math.Floor(w / Cell) + 2;
        int n = values?.Length ?? 0;
        double shift = Math.Clamp(scroll, 0, 1) * Cell;

        for (int c = 0; c < cols; c++)
        {
            int idx = n - cols + c;
            double v = idx >= 0 && idx < n ? values![idx] : double.NaN;
            double x = x0 + w - (cols - c) * Cell + Cell - shift;
            int filled = double.IsNaN(v) ? 0 : (int)Math.Round(Math.Clamp(v, 0, 100) / 100.0 * rows);
            for (int r = 0; r < rows; r++)
            {
                double y = y0 + h - (r + 1) * Cell;
                var brush = r < filled ? brushes[r] : GhostBrush;
                dc.DrawRectangle(brush, null, new Rect(x, y, Dot, Dot));
            }
        }
    }

    private static Color RowColor(double t)
    {
        double hue = (1.0 - Math.Clamp(t, 0, 1)) * 110.0;
        return Hsl(hue, 0.55, 0.58);
    }

    private static Color Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c, double thickness)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        var p = new Pen(b, thickness);
        p.Freeze();
        return p;
    }
}
