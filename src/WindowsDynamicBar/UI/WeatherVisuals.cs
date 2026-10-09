using System;
using System.Windows;
using System.Windows.Media;

namespace WindowsDynamicBar.UI;

/// <summary>
/// 24h temperature line/area graph with an inset precipitation-probability strip. Draws only the
/// values supplied by the host (nothing fabricated) and adapts to whatever height it is given so
/// the weather card can match the net card's footprint.
/// </summary>
public sealed class WeatherGraph : FrameworkElement
{
    private static readonly Brush GhostBrush = Frozen(Color.FromArgb(40, 128, 128, 128));
    private static readonly Brush RainBrush = Frozen(Color.FromArgb(0xCC, 0x4F, 0xC3, 0xD9));
    private static readonly Brush RainGhost = Frozen(Color.FromArgb(26, 79, 195, 217));
    private static readonly Brush NowBrush = Frozen(Color.FromArgb(210, 0xE8, 0xE8, 0xDC));

    private double[] _temps = Array.Empty<double>();
    private double[] _rain = Array.Empty<double>();
    private int _now = -1;

    public void SetData(double[] temps, double[] rain, int now)
    {
        _temps = temps ?? Array.Empty<double>();
        _rain = rain ?? Array.Empty<double>();
        _now = now;
        InvalidateVisual();
    }

    public void Clear()
    {
        _temps = Array.Empty<double>();
        _rain = Array.Empty<double>();
        _now = -1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        int len = Math.Min(_temps.Length, _rain.Length);
        if (len < 2)
        {
            dc.DrawRectangle(GhostBrush, null, new Rect(0, h - 1, w, 1));
            return;
        }

        double cell = w / len;
        double tempH = Math.Max(6, h * 0.66);
        double rainTop = tempH + 3;
        double rainH = Math.Max(2, h - rainTop);

        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = 0; i < len; i++) { lo = Math.Min(lo, _temps[i]); hi = Math.Max(hi, _temps[i]); }
        if (hi - lo < 1) { hi += 0.5; lo -= 0.5; }

        // rain strip background + bars
        dc.DrawRectangle(RainGhost, null, new Rect(0, rainTop, w, rainH));
        for (int i = 0; i < len; i++)
        {
            double p = Math.Clamp(_rain[i] / 100.0, 0, 1);
            if (p <= 0.01) continue;
            double bx = i * cell + cell * 0.15;
            double bw = Math.Max(1, cell * 0.7);
            double bh = Math.Max(1, p * rainH);
            dc.DrawRectangle(RainBrush, null, new Rect(bx, rainTop + (rainH - bh), bw, bh));
        }

        // temperature points
        var pts = new Point[len];
        for (int i = 0; i < len; i++)
        {
            double norm = (_temps[i] - lo) / (hi - lo);
            pts[i] = new Point(i * cell + cell / 2.0, 2 + (1 - norm) * (tempH - 5));
        }

        var accent = TempColor(_temps[0]);
        var fill = new LinearGradientBrush(
            Color.FromArgb(96, accent.R, accent.G, accent.B),
            Color.FromArgb(4, accent.R, accent.G, accent.B), 90);
        fill.Freeze();
        var line = new Pen(Frozen(accent), 1.6) { LineJoin = PenLineJoin.Round };

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(pts[0].X, tempH), true, true);
            g.LineTo(pts[0], true, false);
            for (int i = 1; i < len; i++) g.LineTo(pts[i], true, false);
            g.LineTo(new Point(pts[len - 1].X, tempH), true, false);
        }
        area.Freeze();
        dc.DrawGeometry(fill, null, area);

        var path = new StreamGeometry();
        using (var g = path.Open())
        {
            g.BeginFigure(pts[0], false, false);
            for (int i = 1; i < len; i++) g.LineTo(pts[i], true, false);
        }
        path.Freeze();
        dc.DrawGeometry(null, line, path);

        // "now" anchor
        if (_now >= 0 && _now < len)
        {
            double nx = pts[_now].X;
            dc.DrawRectangle(NowBrush, null, new Rect(nx - 0.75, 0, 1.5, tempH));
            dc.DrawEllipse(NowBrush, null, pts[_now], 2.4, 2.4);
        }
    }

    private static Color TempColor(double t)
    {
        double n = Math.Clamp((t + 10) / 50.0, 0, 1);
        var cold = Color.FromRgb(0x6F, 0xA8, 0xDC);
        var warm = Color.FromRgb(0xE8, 0xB0, 0x4B);
        var hot = Color.FromRgb(0xC9, 0x60, 0x4A);
        return n < 0.5 ? Lerp(cold, warm, n * 2) : Lerp(warm, hot, (n - 0.5) * 2);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
