using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WindowsDynamicBar.Core.Telemetry;

namespace WindowsDynamicBar.UI;

public partial class TelemetryIconRow : UserControl
{
    private sealed record Icon(
        FrameworkElement Cell, Border Border, RingGauge Ring, TextBlock Value,
        IslandMetric Metric);

    private readonly Icon[] _icons;

    private bool _cpuAvail = true;
    private bool _gpuAvail = true;
    private bool _ramAvail = true;
    private bool _netAvail = true;

    public TelemetryIconRow()
    {
        InitializeComponent();
        _icons = new[]
        {
            new Icon(CpuCell, CpuBorder, CpuRing, CpuPct, IslandMetric.Cpu),
            new Icon(GpuCell, GpuBorder, GpuRing, GpuPct, IslandMetric.Gpu),
            new Icon(RamCell, RamBorder, RamRing, RamPct, IslandMetric.Ram),
            new Icon(MirrorCell, MirrorBorder, MirrorRing, MirrorPct, IslandMetric.Mirror),
            new Icon(NetCell, NetBorder, NetRing, NetPct, IslandMetric.Net),
        };
        foreach (var ic in _icons)
        {
            var metric = ic.Metric;
            ic.Cell.MouseEnter += (_, _) => { if (Available(metric)) MetricHovered?.Invoke(metric); };
            ic.Cell.MouseLeave += (_, _) => { if (Available(metric)) MetricUnhovered?.Invoke(); };
            ic.Cell.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (Available(metric)) MetricClicked?.Invoke(metric);
            };
        }
        SetActive(IslandMetric.None);
    }

    /// <summary>Pointer entered the metric icon.</summary>
    public event Action<IslandMetric>? MetricHovered;
    /// <summary>Pointer left the metric icon.</summary>
    public event Action? MetricUnhovered;
    /// <summary>Metric icon clicked.</summary>
    public event Action<IslandMetric>? MetricClicked;

    public void UpdateValues(double cpu, double gpu, double ram)
    {
        Apply(CpuRing, CpuPct, cpu);
        Apply(GpuRing, GpuPct, gpu);
        Apply(RamRing, RamPct, ram);
    }

    /// <summary>Updates the Net gauge ring (0..100) and its small label (e.g. "42%", "187M", "—").</summary>
    public void UpdateNet(double ringPercent, string label)
    {
        NetRing.Value = ringPercent < 0 ? -1 : Math.Clamp(ringPercent, 0, 100);
        NetRing.RingColor = Color.FromRgb(0x6F, 0xA8, 0xDC);
        NetPct.Text = label;
    }

    /// <summary>Mirror camera state: a full red ring reads "LIVE" while capturing, an empty ring
    /// reads "off". The panel still opens when the camera is blocked/unavailable.</summary>
    public void UpdateMirror(bool live, string label)
    {
        MirrorRing.Value = live ? 100 : -1;
        MirrorRing.RingColor = Color.FromRgb(0xE5, 0x48, 0x4D);
        MirrorPct.Text = label;
    }

    public void SetAvailability(bool cpu, bool gpu, bool ram)
    {
        _cpuAvail = cpu;
        _gpuAvail = gpu;
        _ramAvail = ram;
        ApplyAvail(CpuCell, CpuBorder, cpu);
        ApplyAvail(GpuCell, GpuBorder, gpu);
        ApplyAvail(RamCell, RamBorder, ram);
        ApplyAvail(MirrorCell, MirrorBorder, true);
        ApplyAvail(NetCell, NetBorder, _netAvail);
    }

    public void SetActive(IslandMetric metric)
    {
        foreach (var ic in _icons)
            Highlight(ic.Border, ic.Metric == metric);
    }

    private bool Available(IslandMetric m) => m switch
    {
        IslandMetric.Cpu => _cpuAvail,
        IslandMetric.Gpu => _gpuAvail,
        IslandMetric.Ram => _ramAvail,
        IslandMetric.Mirror => true,
        IslandMetric.Net => _netAvail,
        _ => false,
    };

    private static void Apply(RingGauge ring, TextBlock value, double percent)
    {
        if (percent < 0)
        {
            ring.Value = -1;
            value.Text = "n/a";
            return;
        }
        ring.Value = Math.Clamp(percent, 0, 100);
        ring.RingColor = DotBar.ValueColor(percent);
        value.Text = $"{Math.Round(percent):F0}%";
    }

    private static void ApplyAvail(FrameworkElement cell, Border border, bool available)
    {
        cell.Cursor = available ? Cursors.Hand : Cursors.Arrow;
        border.ToolTip = available ? null : "Not available";
    }

    private void Highlight(Border border, bool active)
    {
        if (active)
        {
            var accent = TryFindResource("WdbAccentBrush") as SolidColorBrush;
            if (accent is not null)
            {
                border.BorderBrush = accent;
                var c = accent.Color;
                var tint = new SolidColorBrush(Color.FromArgb(46, c.R, c.G, c.B));
                tint.Freeze();
                border.Background = tint;
                border.BorderThickness = new Thickness(2);
                return;
            }
        }
        border.SetResourceReference(Border.BorderBrushProperty, "WdbHairlineBrush");
        border.SetResourceReference(Border.BackgroundProperty, "WdbDimBrush");
        border.BorderThickness = new Thickness(1);
    }
}
