using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Vector-based headphones icon for the Windows Dynamic Bar UI.
/// Renders crisp curved headband, gimbal hinges, visible cushioned ear cups, and the
/// signature colorful gradient and glow effect from the Dynamic Bar design.
/// Supports both collapsed and expanded states with high-DPI scaling.
/// </summary>
public sealed class HeadphonesVectorIcon : FrameworkElement
{
    private static readonly Brush MetalBrush = Frozen(Color.FromRgb(0xD2, 0xD8, 0xE4));
    private static readonly Brush CushionBrush = Frozen(Color.FromRgb(0x1A, 0x22, 0x54));
    private static readonly Brush SheenBrush = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

    private readonly DispatcherTimer _pulseTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _pulse;

    public static readonly DependencyProperty IsPulsingProperty = DependencyProperty.Register(
        nameof(IsPulsing), typeof(bool), typeof(HeadphonesVectorIcon),
        new FrameworkPropertyMetadata(true, OnIsPulsingChanged));

    public static readonly DependencyProperty GlowOpacityProperty = DependencyProperty.Register(
        nameof(GlowOpacity), typeof(double), typeof(HeadphonesVectorIcon),
        new FrameworkPropertyMetadata(0.85, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsPulsing
    {
        get => (bool)GetValue(IsPulsingProperty);
        set => SetValue(IsPulsingProperty, value);
    }

    public double GlowOpacity
    {
        get => (double)GetValue(GlowOpacityProperty);
        set => SetValue(GlowOpacityProperty, value);
    }

    public HeadphonesVectorIcon()
    {
        Focusable = false;
        IsHitTestVisible = false;
        _pulseTimer.Tick += (_, _) =>
        {
            _pulse = !_pulse;
            InvalidateVisual();
        };
        Loaded += (_, _) => { if (IsPulsing && !_pulseTimer.IsEnabled) _pulseTimer.Start(); };
        Unloaded += (_, _) => _pulseTimer.Stop();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue && IsPulsing) { if (!_pulseTimer.IsEnabled) _pulseTimer.Start(); }
            else _pulseTimer.Stop();
        };
    }

    private static void OnIsPulsingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (HeadphonesVectorIcon)d;
        if ((bool)e.NewValue && icon.IsVisible)
        {
            if (!icon._pulseTimer.IsEnabled) icon._pulseTimer.Start();
        }
        else
        {
            icon._pulseTimer.Stop();
            icon._pulse = false;
            icon.InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? (Width > 0 ? Width : 24.0) : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) || availableSize.Height <= 0 ? (Height > 0 ? Height : 24.0) : availableSize.Height;
        double sz = Math.Min(w, h);
        return new Size(sz, sz);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : 24.0);
        double h = ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : 24.0);
        if (w < 4 || h < 4) return;

        double sz = Math.Min(w, h);
        double s = sz / 24.0;
        double ox = (w - 24.0 * s) / 2.0;
        double oy = (h - 24.0 * s) / 2.0;

        // 1. Colorful atmospheric radial glow behind the headphones (matches Dynamic Bar glow aesthetic)
        if (GlowOpacity > 0.01)
        {
            var glowCenter = new Point(ox + 12.0 * s, oy + 12.5 * s);
            var glow = new RadialGradientBrush
            {
                Center = glowCenter,
                GradientOrigin = glowCenter,
                RadiusX = 11.5 * s,
                RadiusY = 11.5 * s,
                MappingMode = BrushMappingMode.Absolute
            };
            byte alphaOuter = (byte)Math.Clamp(GlowOpacity * 60, 0, 255);
            byte alphaInner = (byte)Math.Clamp(GlowOpacity * 35, 0, 255);
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(alphaOuter, 0x0A, 0x84, 0xFF), 0.0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(alphaInner, 0x00, 0xE5, 0xFF), 0.55));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x0A, 0x84, 0xFF), 1.0));
            glow.Freeze();
            dc.DrawEllipse(glow, null, glowCenter, 11.5 * s, 11.5 * s);
        }

        // 2. Curved headband: smooth parabolic arch across crown
        var headbandGeom = new StreamGeometry();
        using (var ctx = headbandGeom.Open())
        {
            ctx.BeginFigure(new Point(ox + 4.8 * s, oy + 10.6 * s), false, false);
            ctx.BezierTo(
                new Point(ox + 6.2 * s, oy + 3.2 * s),
                new Point(ox + 17.8 * s, oy + 3.2 * s),
                new Point(ox + 19.2 * s, oy + 10.6 * s),
                true, true);
        }
        headbandGeom.Freeze();

        var headbandBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };
        headbandBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x00, 0xE5, 0xFF), 0.0));
        headbandBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x5B, 0x6C, 0xFF), 0.35));
        headbandBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x0A, 0x84, 0xFF), 0.70));
        headbandBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x00, 0xE5, 0xFF), 1.0));
        headbandBrush.Freeze();

        var headbandPen = new Pen(headbandBrush, Math.Max(1.4, 2.0 * s))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        headbandPen.Freeze();
        dc.DrawGeometry(null, headbandPen, headbandGeom);

        // 3. Headband specular sheen highlight (glossy top curve reflection)
        var sheenGeom = new StreamGeometry();
        using (var ctx = sheenGeom.Open())
        {
            ctx.BeginFigure(new Point(ox + 8.5 * s, oy + 4.4 * s), false, false);
            ctx.BezierTo(
                new Point(ox + 10.5 * s, oy + 3.7 * s),
                new Point(ox + 13.5 * s, oy + 3.7 * s),
                new Point(ox + 15.5 * s, oy + 4.4 * s),
                true, true);
        }
        sheenGeom.Freeze();
        var sheenPen = new Pen(SheenBrush, Math.Max(0.7, 0.9 * s))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        sheenPen.Freeze();
        dc.DrawGeometry(null, sheenPen, sheenGeom);

        // 4. Polished metal gimbal yokes connecting headband into the ear cups
        var metalPen = new Pen(MetalBrush, Math.Max(1.0, 1.5 * s))
        {
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat
        };
        metalPen.Freeze();
        dc.DrawLine(metalPen, new Point(ox + 4.8 * s, oy + 9.8 * s), new Point(ox + 4.8 * s, oy + 12.0 * s));
        dc.DrawLine(metalPen, new Point(ox + 19.2 * s, oy + 9.8 * s), new Point(ox + 19.2 * s, oy + 12.0 * s));

        // 5. Left & Right Ear Cups (visible cushioned over-ear cans)
        var cupBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        cupBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x5B, 0x6C, 0xFF), 0.0));
        cupBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x3D, 0x5A, 0xFE), 1.0));
        cupBrush.Freeze();

        var cupPen = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0xE5, 0xFF)), Math.Max(0.5, 0.75 * s));
        cupPen.Freeze();

        // Left outer can
        dc.DrawRoundedRectangle(cupBrush, cupPen,
            new Rect(ox + 2.5 * s, oy + 11.4 * s, 4.4 * s, 8.4 * s), 2.2 * s, 2.2 * s);
        // Left inner cushion
        dc.DrawRoundedRectangle(CushionBrush, null,
            new Rect(ox + 5.3 * s, oy + 12.0 * s, 1.5 * s, 7.2 * s), 0.75 * s, 0.75 * s);

        // Right outer can
        dc.DrawRoundedRectangle(cupBrush, cupPen,
            new Rect(ox + 17.1 * s, oy + 11.4 * s, 4.4 * s, 8.4 * s), 2.2 * s, 2.2 * s);
        // Right inner cushion
        dc.DrawRoundedRectangle(CushionBrush, null,
            new Rect(ox + 17.2 * s, oy + 12.0 * s, 1.5 * s, 7.2 * s), 0.75 * s, 0.75 * s);

        // 6. Center acoustic disc / active audio pulse LED
        var ledColor = _pulse ? Color.FromRgb(0x00, 0xE5, 0xFF) : Color.FromRgb(0xC8, 0xE2, 0xFF);
        var ledBrush = new SolidColorBrush(ledColor);
        ledBrush.Freeze();
        dc.DrawEllipse(ledBrush, null, new Point(ox + 4.2 * s, oy + 15.6 * s), 1.2 * s, 1.2 * s);
        dc.DrawEllipse(ledBrush, null, new Point(ox + 19.8 * s, oy + 15.6 * s), 1.2 * s, 1.2 * s);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
