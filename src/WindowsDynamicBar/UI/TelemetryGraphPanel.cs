using System;
using System.Windows;
using System.Windows.Media;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Custom drawing surface for rendering telemetry sparkline graphs (CPU/GPU/RAM).
/// Draws a polyline with area fill and subtle gridlines.
/// </summary>
public class TelemetryGraphPanel : FrameworkElement
{
    public static readonly DependencyProperty SnapshotProperty =
        DependencyProperty.Register(nameof(Snapshot), typeof(double[]), typeof(TelemetryGraphPanel),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeColorProperty =
        DependencyProperty.Register(nameof(StrokeColor), typeof(Color), typeof(TelemetryGraphPanel),
            new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillColorProperty =
        DependencyProperty.Register(nameof(FillColor), typeof(Color), typeof(TelemetryGraphPanel),
            new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridColorProperty =
        DependencyProperty.Register(nameof(GridColor), typeof(Color), typeof(TelemetryGraphPanel),
            new FrameworkPropertyMetadata(Color.FromArgb(26, 255, 255, 255), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Atomic snapshot of 60 telemetry values (0-100%).</summary>
    public double[]? Snapshot
    {
        get => (double[]?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    /// <summary>Stroke color for the polyline.</summary>
    public Color StrokeColor
    {
        get => (Color)GetValue(StrokeColorProperty);
        set => SetValue(StrokeColorProperty, value);
    }

    /// <summary>Fill color for area below polyline (uses alpha for translucency).</summary>
    public Color FillColor
    {
        get => (Color)GetValue(FillColorProperty);
        set => SetValue(FillColorProperty, value);
    }

    /// <summary>Grid line color (translucent).</summary>
    public Color GridColor
    {
        get => (Color)GetValue(GridColorProperty);
        set => SetValue(GridColorProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 1 || h < 1 || Snapshot is not { Length: > 1 }) return;

        var snapshot = Snapshot;
        int n = snapshot.Length;

        // ---- Draw gridlines (5 horizontal: 0%, 25%, 50%, 75%, 100%) ----
        var gridPen = new Pen(new SolidColorBrush(GridColor), 0.8);
        gridPen.Freeze();

        for (int i = 0; i <= 4; i++)
        {
            double y = h - (i * h / 4.0);
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        // ---- Draw polyline connecting snapshot points ----
        double xStep = n > 1 ? w / (n - 1) : w;
        var points = new PointCollection();

        for (int i = 0; i < n; i++)
        {
            double x = i * xStep;
            double val = Math.Clamp(snapshot[i], 0, 100);
            double y = h - (val / 100.0 * h);
            points.Add(new Point(x, y));
        }

        if (points.Count > 1)
        {
            // Stroke polyline
            var strokePen = new Pen(new SolidColorBrush(StrokeColor), 1.6);
            strokePen.LineJoin = PenLineJoin.Round;
            strokePen.Freeze();

            for (int i = 0; i < points.Count - 1; i++)
                dc.DrawLine(strokePen, points[i], points[i + 1]);

            // Fill area below polyline with gradient
            var fillColor = FillColor;
            fillColor.A = (byte)(fillColor.A * 0.14); // 14% opacity
            var fillBrush = new SolidColorBrush(fillColor);
            fillBrush.Freeze();

            var segments = new PathSegmentCollection();
            for (int i = 0; i < points.Count; i++)
                segments.Add(new LineSegment(points[i], true));
            segments.Add(new LineSegment(new Point(points[points.Count - 1].X, h), true));
            segments.Add(new LineSegment(new Point(points[0].X, h), true));

            var pathFig = new PathFigure(new Point(points[0].X, h), segments, true);

            var pathGeom = new PathGeometry { Figures = { pathFig } };
            pathGeom.Freeze();

            dc.DrawGeometry(fillBrush, null, pathGeom);
        }
    }
}
