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
    private int _gpuIndex;
    private IReadOnlyList<string> _gpuNames = Array.Empty<string>();

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

    /// <summary>CPU canvas. Every row is shown only when its value is real; nothing reads "N/A".</summary>
    public void ShowCpu(CpuStats cpu, IReadOnlyList<double>? cores, TelemetryHistory? history,
        IReadOnlyList<ProcessSample>? procs = null, double? tempC = null,
        (double? M1, double? M5, double? M15) load = default, bool fahrenheit = false)
    {
        _gpuCount = 0;
        Clear();
        double? pct = cpu.Available ? cpu.Utilization : null;

        if (!string.IsNullOrWhiteSpace(cpu.Name)) AddName(cpu.Name);
        if (pct is double p) AddBar("CPU", p, $"{p:F0}%");
        if (cpu.Available && cpu.FrequencyMhz > 0) AddLine("freq", TelemetryFormat.Frequency(cpu.FrequencyMhz));
        if (tempC is double t)
            AddLine("temp", TelemetryFormat.Temperature(t, fahrenheit), "Hottest ACPI thermal zone (no admin rights needed)");
        if (load.M1 is double m1)
        {
            string text = $"{m1:F0}";
            if (load.M5 is double m5) text += $"/{m5:F0}";
            if (load.M15 is double m15) text += $"/{m15:F0}";
            AddLine("load", text + "%", "Average CPU use over the last 1 / 5 / 15 minutes");
        }
        if (cores is { Count: > 0 }) AddHeatmap(cores);
        if (cpu.Available && cpu.LogicalThreads > 0) AddCaption($"{cpu.LogicalThreads} threads");

        Render(history, procs);
        SetTitle("cpu", pct, history);
    }

    /// <summary>GPU canvas for one adapter. <paramref name="names"/> lists every adapter (selector).</summary>
    public void ShowGpu(GpuTrack gpu, int index, IReadOnlyList<string> names, TelemetryHistory? history,
        IReadOnlyList<ProcessSample>? procs = null, bool fahrenheit = false)
    {
        _gpuCount = names.Count;
        Clear();
        double pct = gpu.Utilization;

        if (!string.IsNullOrWhiteSpace(gpu.Name)) AddName(gpu.Name);
        AddBar("GPU", pct, $"{Math.Round(pct)}%");
        if (gpu.VramAvailable && gpu.VramTotal > 0)
            AddBar("VRAM", (double)gpu.VramUsed / gpu.VramTotal * 100,
                $"{TelemetryFormat.Bytes(gpu.VramUsed)}/{TelemetryFormat.Bytes(gpu.VramTotal)}");
        if (gpu.PowerW is double w) AddLine("PWR", $"{w:F1} W");
        if (gpu.TempC is double t) AddLine("temp", TelemetryFormat.Temperature(t, fahrenheit));
        if (gpu.ClockMhz is double mhz) AddLine("clock", TelemetryFormat.Frequency(mhz));
        if (gpu.EnginesAvailable)
        {
            AddBar("3D", gpu.Engine3D, $"{Math.Round(gpu.Engine3D)}%");
            AddBar("Copy", gpu.EngineCopy, $"{Math.Round(gpu.EngineCopy)}%");
        }

        Render(history, procs);
        _gpuNames = names;
        _gpuIndex = index;
        SetTitle(TelemetryFormat.ShortGpuName(gpu.Name) is { Length: > 0 } shortName ? shortName : "gpu", pct, history);
    }

    public void ShowRam(MemoryStats ram, long swapUsed, long swapTotal,
        long cacheBytes, long diskRead, long diskWrite,
        TelemetryHistory? history, IReadOnlyList<ProcessSample>? procs = null)
    {
        _gpuCount = 0;
        Clear();
        double? pct = ram.Available ? ram.UsedPercent : null;

        if (ram.Available && ram.TotalBytes > 0)
        {
            AddName($"{TelemetryFormat.Bytes(ram.TotalBytes)} total");
            AddBar("Used", ram.UsedPercent, TelemetryFormat.Bytes(ram.UsedBytes));
            if (cacheBytes > 0)
                AddBar("Cache", (double)cacheBytes / ram.TotalBytes * 100, TelemetryFormat.Bytes(cacheBytes));
            long free = ram.TotalBytes - ram.UsedBytes;
            AddBar("Free", (double)free / ram.TotalBytes * 100, TelemetryFormat.Bytes(free));
        }
        if (swapTotal > 0)
            AddBar("Swap", (double)swapUsed / swapTotal * 100, TelemetryFormat.Bytes(swapUsed));

        if (diskRead >= 0 || diskWrite >= 0) AddSeparator();
        if (diskRead >= 0) AddLine("Disk R", $"{TelemetryFormat.Bytes(diskRead)}/s");
        if (diskWrite >= 0) AddLine("Disk W", $"{TelemetryFormat.Bytes(diskWrite)}/s");

        Render(history, procs);
        SetTitle("ram", pct, history);
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
        TitleText.Inlines.Clear();
        TitleText.Inlines.Add(Run("\u2524", NameBrush));
        TitleText.Inlines.Add(Run(metric, MetricBrush));
        TitleText.Inlines.Add(Run("\u251C", NameBrush));
        if (pct is double p) TitleText.Inlines.Add(Run($"\u2524{Math.Round(p):F0}%\u251C", NameBrush));
        if (peak is double pk) TitleText.Inlines.Add(Run($"\u2524peak {Math.Round(pk):F0}%\u251C", NameBrush));
        if (_gpuCount > 1)
        {
            // Selector: gpu0 gpu1 ... (active one highlighted); clicking the tag picks the next GPU.
            TitleText.Inlines.Add(Run("\u2524", NameBrush));
            for (int i = 0; i < _gpuCount; i++)
            {
                if (i > 0) TitleText.Inlines.Add(Run(" ", NameBrush));
                TitleText.Inlines.Add(Run($"gpu{i}", i == _gpuIndex ? MetricBrush : LabelBrush));
            }
            TitleText.Inlines.Add(Run("\u251C", NameBrush));
            TitleTag.ToolTip = string.Join("\n", _gpuNames.Select((n, i) => $"gpu{i}: {n}"));
        }
        else TitleTag.ToolTip = null;
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(97) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

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

    private const double StatRowHeight = 14;
    private const double StatFontSize = 10;
    private const double StatLabelWidth = 40;   // fits "Disk R" without touching its value
    private double _col0H, _col1H;

    private void Clear()
    {
        StatsCol0.Children.Clear();
        StatsCol1.Children.Clear();
        _col0H = 0;
        _col1H = 0;
        ProcsHost.Children.Clear();
        TitleTag.MouseLeftButtonDown -= OnTitleTagClick;
        TitleTag.MouseLeftButtonDown += OnTitleTagClick;
    }

    /// <summary>Append a stat to whichever sub-column is currently shorter, keeping the panel
    /// as short as possible while showing the same rows.</summary>
    private void Push(FrameworkElement el, double height)
    {
        if (_col0H <= _col1H) { StatsCol0.Children.Add(el); _col0H += height; }
        else { StatsCol1.Children.Add(el); _col1H += height; }
    }

    private void AddName(string name)
        => Push(new TextBlock
        {
            Text = name,
            FontFamily = new FontFamily(Mono),
            FontSize = StatFontSize,
            Foreground = NameBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 3),
        }, StatRowHeight + 3);

    private void AddCaption(string text)
        => Push(new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily(Mono),
            FontSize = StatFontSize,
            Foreground = LabelBrush,
            Margin = new Thickness(0, 2, 0, 0),
        }, StatRowHeight + 2);

    private void AddSeparator() => Push(new Border { Height = 4 }, 4);

    private void AddBar(string label, double percent, string value)
        => AddRow(label, new DotBar
        {
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            // DotBar.Value is a 0..1 fill fraction; -1 = no data (ghost ticks only).
            Value = percent >= 0 ? Math.Clamp(percent, 0, 100) / 100.0 : -1,
            FillColor = percent >= 0 ? DotBar.ValueColor(percent) : Color.FromRgb(0x8A, 0xB4, 0x78),
        }, value);

    private void AddLine(string label, string value, string? tooltip = null)
        => AddRow(label, new TextBlock
        {
            Text = value,
            FontFamily = new FontFamily(Mono),
            FontSize = StatFontSize,
            Foreground = ValueBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = tooltip,
        }, null);

    private void AddHeatmap(IReadOnlyList<double> cores)
    {
        const int columns = 8;
        var grid = new UniformGrid { Columns = columns, Margin = new Thickness(0, 3, 0, 0) };
        for (int i = 0; i < cores.Count; i++)
        {
            double v = Math.Clamp(cores[i], 0, 100);
            grid.Children.Add(new Border
            {
                Height = 8,
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(1),
                Background = new SolidColorBrush(DotBar.ValueColor(v)),
                ToolTip = $"core {i}: {Math.Round(v)}%",
            });
        }
        Push(grid, Math.Ceiling(cores.Count / (double)columns) * 10 + 3);
    }

    private void AddRow(string label, FrameworkElement middle, string? value)
    {
        var grid = new Grid { Height = StatRowHeight, Margin = new Thickness(0, 0, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StatLabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily(Mono),
            FontSize = StatFontSize,
            Foreground = LabelBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        Grid.SetColumn(middle, 1);
        middle.Margin = new Thickness(0, 0, 5, 0);
        grid.Children.Add(middle);

        if (value is not null)
        {
            var val = new TextBlock
            {
                Text = value,
                FontFamily = new FontFamily(Mono),
                FontSize = StatFontSize,
                Foreground = ValueBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(val, 2);
            grid.Children.Add(val);
        }

        Push(grid, StatRowHeight + 1);
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
        Graph.Scroll = GraphScroll.Quantize(scroll, Graph);
    }
}
