using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Services.SystemMonitor;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Reusable btop-style telemetry detail panel: a single dot-matrix history graph, a top-process
/// list and a per-core heatmap, with a monospace stats column and a title tag. Each metric gets
/// its own canvas showing only that metric's graph. Drives its own smooth-scroll render loop
/// only while visible (unsubscribes when hidden).
/// </summary>
public partial class TelemetryDetailPanel : UserControl
{
    private const string Mono = "Cascadia Mono, Consolas, Courier New";
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x9A, 0xA0, 0x88));
    private static readonly Brush ValueBrush = Frozen(Color.FromRgb(0xE8, 0xE8, 0xDC));
    private static readonly Brush NameBrush = Frozen(Color.FromRgb(0xE6, 0xE0, 0xA8));
    private static readonly Brush MetricBrush = Frozen(Color.FromRgb(0x8A, 0xB4, 0x78));

    private const double SampleSeconds = 0.5;
    private DateTime _lastSampleUtc = DateTime.UtcNow;
    private bool _rendering;
    private int _gpuCount;

    public TelemetryDetailPanel()
    {
        InitializeComponent();
        TitleText.Foreground = NameBrush;
        IsVisibleChanged += OnIsVisibleChanged;
        Unloaded += (_, _) => StopRenderLoop();
    }

    public bool ReduceMotion { get; set; }

    public event Action? GpuCycleRequested;

    // ------------------------------------------------------------------ public API

    public void ShowCpu(CpuStats cpu, IReadOnlyList<double>? cores, TelemetryHistory? history,
        IReadOnlyList<ProcessSample>? procs = null)
    {
        _gpuCount = 0;
        Clear();
        double pct = cpu.Available ? cpu.Utilization : -1;

        if (!string.IsNullOrWhiteSpace(cpu.Name)) AddName(cpu.Name);
        AddBar("CPU", pct, cpu.Available ? $"{pct:F0}%" : "N/A");
        AddLine("freq", cpu.Available ? TelemetryFormat.Frequency(cpu.FrequencyMhz) : "N/A");
        AddLine("temp", "N/A");
        AddLine("load", "N/A");
        if (cores is { Count: > 0 }) AddHeatmap(cores);
        else AddLine("cores", "N/A");
        if (cpu.Available && cpu.LogicalThreads > 0) AddCaption($"{cpu.LogicalThreads} threads");

        Render(history, procs);
        SetTitle("cpu", pct, history);
    }

    public void ShowGpu(GpuTrack gpu, int index, int count, TelemetryHistory? history,
        IReadOnlyList<ProcessSample>? procs = null)
    {
        _gpuCount = count;
        Clear();
        double pct = gpu.Utilization;

        if (!string.IsNullOrWhiteSpace(gpu.Name)) AddName(gpu.Name);
        AddBar("GPU", pct, $"{Math.Round(pct)}%");
        AddBar("PWR", -1, "N/A");

        double vramPct = gpu.VramAvailable && gpu.VramTotal > 0
            ? (double)gpu.VramUsed / gpu.VramTotal * 100 : -1;
        AddBar("VRAM", vramPct,
            gpu.VramAvailable && gpu.VramTotal > 0
                ? $"{TelemetryFormat.Bytes(gpu.VramUsed)}/{TelemetryFormat.Bytes(gpu.VramTotal)}"
                : "N/A");

        if (gpu.EnginesAvailable)
        {
            AddBar("3D", gpu.Engine3D, $"{Math.Round(gpu.Engine3D)}%");
            AddBar("Copy", gpu.EngineCopy, $"{Math.Round(gpu.EngineCopy)}%");
            AddBar("Enc", gpu.EngineEncode, $"{Math.Round(gpu.EngineEncode)}%");
            AddBar("Dec", gpu.EngineDecode, $"{Math.Round(gpu.EngineDecode)}%");
        }
        AddLine("temp", "N/A");
        AddLine("clock", "N/A");
        AddLine("TX/RX", "N/A");

        Render(history, procs);
        SetTitle(count > 1 ? $"gpu{index}" : "gpu", pct, history);
    }

    public void ShowRam(MemoryStats ram, long swapUsed, long swapTotal,
        long cacheBytes, long diskRead, long diskWrite,
        TelemetryHistory? history, IReadOnlyList<ProcessSample>? procs = null)
    {
        _gpuCount = 0;
        Clear();
        double pct = ram.Available ? ram.UsedPercent : -1;

        AddName(ram.Available ? $"{TelemetryFormat.Bytes(ram.TotalBytes)} total" : "Memory");
        AddBar("Used", pct, ram.Available ? TelemetryFormat.Bytes(ram.UsedBytes) : "N/A");
        AddBar("Cache", cacheBytes > 0 ? (double)cacheBytes / Math.Max(1, ram.TotalBytes) * 100 : -1,
            cacheBytes > 0 ? TelemetryFormat.Bytes(cacheBytes) : "N/A");

        long free = ram.TotalBytes - ram.UsedBytes;
        AddBar("Free", ram.Available && ram.TotalBytes > 0 ? (double)free / ram.TotalBytes * 100 : -1,
            ram.Available ? TelemetryFormat.Bytes(free) : "N/A");
        AddBar("Swap", swapTotal > 0 ? (double)swapUsed / swapTotal * 100 : -1,
            swapTotal > 0 ? TelemetryFormat.Bytes(swapUsed) : "N/A");

        AddSeparator();
        AddLine("Disk R", diskRead >= 0 ? $"{TelemetryFormat.Bytes(diskRead)}/s" : "N/A");
        AddLine("Disk W", diskWrite >= 0 ? $"{TelemetryFormat.Bytes(diskWrite)}/s" : "N/A");

        Render(history, procs);
        SetTitle("ram", ram.Available ? pct : (double?)null, history);
    }

    // ------------------------------------------------------------------ title / graph

    private void SetTitle(string metric, double? pct, TelemetryHistory? history)
    {
        double? peak = null;
        if (history is { IsEmpty: false })
        {
            double max = 0; bool any = false;
            foreach (var v in history.Snapshot())
                if (!double.IsNaN(v)) { max = Math.Max(max, v); any = true; }
            if (any) peak = max;
        }
        string p = pct is null ? "N/A" : $"{Math.Round(pct.Value):F0}%";
        string pk = peak is null ? "N/A" : $"{Math.Round(peak.Value):F0}%";

        TitleText.Inlines.Clear();
        TitleText.Inlines.Add(Run("\u2524", NameBrush));
        TitleText.Inlines.Add(Run(metric, MetricBrush));
        TitleText.Inlines.Add(Run($"\u251C\u2524{p}\u251C\u2524peak {pk}\u251C", NameBrush));
        TitleTag.Cursor = _gpuCount > 1 ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
    }

    private void Render(TelemetryHistory? history, IReadOnlyList<ProcessSample>? procs)
    {
        Graph.Values = history is { IsEmpty: false } ? history.Snapshot() : Array.Empty<double>();
        _lastSampleUtc = DateTime.UtcNow;

        ProcsHost.Children.Clear();
        if (procs is { Count: > 0 })
            foreach (var p in procs)
                ProcsHost.Children.Add(ProcRow(p.Name, p.CpuPercent, $"{p.CpuPercent:F1}%"));
    }

    private static FrameworkElement ProcRow(string name, double percent, string value)
    {
        var grid = new Grid { Height = 14, Margin = new Thickness(0, 0, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

        var lbl = new TextBlock
        {
            Text = name,
            FontFamily = new FontFamily(Mono),
            FontSize = 10.5,
            Foreground = ValueBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var bar = new DotBar
        {
            Height = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Value = Math.Clamp(percent, 0, 100) / 100.0,
            FillColor = DotBar.ValueColor(percent),
        };
        bar.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);

        var val = new TextBlock
        {
            Text = value,
            FontFamily = new FontFamily(Mono),
            FontSize = 10.5,
            Foreground = ValueBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(val, 2);
        grid.Children.Add(val);

        return grid;
    }

    private void OnTitleTagClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_gpuCount > 1) GpuCycleRequested?.Invoke();
    }

    private static Run Run(string text, Brush brush) => new(text) { Foreground = brush };

    // ------------------------------------------------------------------ stats column

    private void Clear()
    {
        StatsCol.Children.Clear();
        ProcsHost.Children.Clear();
        TitleTag.MouseLeftButtonDown -= OnTitleTagClick;
        TitleTag.MouseLeftButtonDown += OnTitleTagClick;
    }

    private void AddName(string name)
        => StatsCol.Children.Add(new TextBlock
        {
            Text = name,
            FontFamily = new FontFamily(Mono),
            FontSize = 10.5,
            Foreground = NameBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 3),
        });

    private void AddCaption(string text)
        => StatsCol.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily(Mono),
            FontSize = 10,
            Foreground = LabelBrush,
            Margin = new Thickness(0, 2, 0, 0),
        });

    private void AddSeparator() => StatsCol.Children.Add(new Border { Height = 6 });

    private void AddBar(string label, double percent, string value)
        => AddRow(label, new DotBar
        {
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center,
            // DotBar.Value is a 0..1 fill fraction; -1 = no data (ghost ticks only).
            Value = percent >= 0 ? Math.Clamp(percent, 0, 100) / 100.0 : -1,
            FillColor = percent >= 0 ? DotBar.ValueColor(percent) : Color.FromRgb(0x8A, 0xB4, 0x78),
        }, value);

    private void AddLine(string label, string value)
        => AddRow(label, new TextBlock
        {
            Text = value,
            FontFamily = new FontFamily(Mono),
            FontSize = 10.5,
            Foreground = ValueBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }, null);

    private void AddHeatmap(IReadOnlyList<double> cores)
    {
        var grid = new UniformGrid { Columns = 8, Margin = new Thickness(0, 4, 0, 0) };
        for (int i = 0; i < cores.Count; i++)
        {
            double v = Math.Clamp(cores[i], 0, 100);
            grid.Children.Add(new Border
            {
                Height = 10,
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(1),
                Background = new SolidColorBrush(DotBar.ValueColor(v)),
                ToolTip = $"core {i}: {Math.Round(v)}%",
            });
        }
        StatsCol.Children.Add(grid);
    }

    private void AddRow(string label, FrameworkElement middle, string? value)
    {
        var grid = new Grid { Height = 15, Margin = new Thickness(0, 0, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily(Mono),
            FontSize = 10.5,
            Foreground = LabelBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        Grid.SetColumn(middle, 1);
        middle.Margin = new Thickness(0, 0, 6, 0);
        grid.Children.Add(middle);

        if (value is not null)
        {
            var val = new TextBlock
            {
                Text = value,
                FontFamily = new FontFamily(Mono),
                FontSize = 10.5,
                Foreground = ValueBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(val, 2);
            grid.Children.Add(val);
        }

        StatsCol.Children.Add(grid);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ------------------------------------------------------------------ smooth scroll loop

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) StartRenderLoop();
        else StopRenderLoop();
    }

    private void StartRenderLoop()
    {
        if (_rendering || ReduceMotion) return;
        _rendering = true;
        System.Windows.Media.CompositionTarget.Rendering += OnRendering;
    }

    private void StopRenderLoop()
    {
        if (!_rendering) return;
        _rendering = false;
        System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double scroll = Math.Clamp((DateTime.UtcNow - _lastSampleUtc).TotalSeconds / SampleSeconds, 0, 1);
        Graph.Scroll = scroll;
    }
}
