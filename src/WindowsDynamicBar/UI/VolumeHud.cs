using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Dot-matrix level meter for the volume / brightness HUD: a rising ramp of 3px cells
/// (up to 8 rows) whose lit region tracks the level. Teal for volume, amber for brightness,
/// red when muted - matching the reference HUD. The bar is also scrub-able so it doubles as a
/// slider (drag to set the level); it never fabricates a value, it only reflects what the OS
/// reports.
/// </summary>
public sealed class VolumeLevelBar : FrameworkElement
{
    private const double Cell = 4.0;
    private const double Dot = 3.0;
    private const int Rows = 8;

    private static readonly Brush OffBrush = Frozen(Color.FromArgb(20, 255, 255, 255));
    private static readonly Brush EdgeBrush = Frozen(Color.FromArgb(242, 255, 255, 255));

    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double _display;
    private bool _dragging;

    private Brush[] _lit = Array.Empty<Brush>();
    private int _litN = -1;
    private bool _litBright;
    private bool _litMuted;

    public VolumeLevelBar()
    {
        Height = Rows * Cell;
        MinWidth = 120;
        Focusable = false;
        _settle.Tick += (_, _) => Settle();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        // Capture can be lost without a button-up (the HUD collapses mid-drag): end the drag, or a
        // later plain hover over the bar would keep changing the volume.
        LostMouseCapture += (_, _) => _dragging = false;
    }

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(VolumeLevelBar),
        new FrameworkPropertyMetadata(0.0, OnLevelChanged));

    public static readonly DependencyProperty MutedProperty = DependencyProperty.Register(
        nameof(Muted), typeof(bool), typeof(VolumeLevelBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrightProperty = DependencyProperty.Register(
        nameof(Bright), typeof(bool), typeof(VolumeLevelBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(VolumeLevelBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool Muted { get => (bool)GetValue(MutedProperty); set => SetValue(MutedProperty, value); }
    public bool Bright { get => (bool)GetValue(BrightProperty); set => SetValue(BrightProperty, value); }
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }

    /// <summary>Raised while the user scrubs (0..1); the host applies it to the real system level.</summary>
    public event Action<double>? Scrub;

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (VolumeLevelBar)d;
        double val = (double)e.NewValue;
        if (bar.ReduceMotion || !bar.IsVisible || bar.ActualWidth <= 0)
        {
            bar._display = val;
            bar.InvalidateVisual();
            return;
        }
        if (!bar._settle.IsEnabled) bar._settle.Start();
        bar.InvalidateVisual();
    }

    private void Settle()
    {
        double target = Math.Clamp(Level, 0, 1);
        _display += (target - _display) * 0.24;
        if (Math.Abs(target - _display) < 0.0015) { _display = target; _settle.Stop(); }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        int n = (int)Math.Floor(w / Cell);
        if (n <= 0) return;

        double level = Math.Clamp(_display, 0, 1);
        EnsureLit(n);
        double edgeStep = 1.0 / n * 1.8;

        for (int i = 0; i < n; i++)
        {
            double fr = (i + 0.5) / n;
            bool on = fr <= level;
            Brush brush = !on ? OffBrush
                : fr > level - edgeStep ? EdgeBrush
                : _lit[i];

            int ch = 2 + (int)Math.Round((n <= 1 ? 0.0 : (double)i / (n - 1)) * (Rows - 2));
            double x = i * Cell;
            for (int r = 0; r < ch; r++)
                dc.DrawRectangle(brush, null, new Rect(x, Rows * Cell - (r + 1) * Cell, Dot, Dot));
        }
    }

    private void EnsureLit(int n)
    {
        if (n == _litN && Bright == _litBright && Muted == _litMuted) return;
        _litN = n;
        _litBright = Bright;
        _litMuted = Muted;
        _lit = new Brush[n];
        for (int i = 0; i < n; i++)
        {
            double t = n <= 1 ? 0.0 : (double)i / (n - 1);
            Color c = Muted ? Hsl(5, 0.70, 0.58)
                : Bright ? Hsl(50 - t * 14, 0.96, 0.58 + t * 0.08)
                : Hsl(172 + t * 28, 0.68, 0.56 + t * 0.12);
            _lit[i] = Frozen(c);
        }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        ScrubTo(e);
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragging = false; return; }
        ScrubTo(e);
        e.Handled = true;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ScrubTo(MouseEventArgs e)
    {
        double v = Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);
        _display = v;
        Level = v;
        InvalidateVisual();
        Scrub?.Invoke(v);
    }

    private static Color Hsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(((h / 60) % 2) - 1));
        double m = l - c / 2;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
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
