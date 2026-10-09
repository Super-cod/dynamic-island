using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Services.SystemMonitor;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Live network panel: mirrored throughput graph, current rates, latency probes and an on-demand
/// speed test. Everything shown is measured; unavailable values render as "—". Owns no timers -
/// the host pushes fresh samples into <see cref="Render"/>.
/// </summary>
public partial class NetDetailPanel : UserControl
{
    private const string Mono = "Cascadia Mono, Consolas, Courier New";
    private const double SampleSeconds = 0.5;

    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x9A, 0xA0, 0x88));
    private static readonly Brush ValueBrush = Frozen(Color.FromRgb(0xE8, 0xE8, 0xDC));
    private static readonly Brush NameBrush = Frozen(Color.FromRgb(0xE6, 0xE0, 0xA8));
    private static readonly Brush MetricBrush = Frozen(Color.FromRgb(0x8A, 0xB4, 0x78));
    private static readonly Brush GoodBrush = Frozen(Color.FromRgb(0x8A, 0xB4, 0x78));
    private static readonly Brush WarnBrush = Frozen(Color.FromRgb(0xD9, 0xA0, 0x5B));
    private static readonly Brush BadBrush = Frozen(Color.FromRgb(0xC9, 0x60, 0x4A));
    private static readonly Brush NoneBrush = Frozen(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly Brush DownBrush = Frozen(Color.FromRgb(0x6F, 0xA8, 0xDC));
    private static readonly Brush UpBrush = Frozen(Color.FromRgb(0xB0, 0x7C, 0xE0));

    private readonly Dictionary<NetTarget, TextBlock> _tileValue = new();
    private readonly Dictionary<NetTarget, Sparkline> _tileSpark = new();

    private TextBlock _pingVal = null!;
    private TextBlock _jitterVal = null!;
    private TextBlock _lossVal = null!;
    private DotBar _downBar = null!;
    private TextBlock _downVal = null!;
    private DotBar _upBar = null!;
    private TextBlock _upVal = null!;
    private TextBlock _statusVal = null!;

    private DateTime _lastSampleUtc = DateTime.UtcNow;
    private bool _rendering;

    public NetDetailPanel()
    {
        InitializeComponent();
        TitleText.Foreground = NameBrush;
        BuildTiles();
        BuildStats();
        IsVisibleChanged += OnIsVisibleChanged;
        Unloaded += (_, _) => StopRenderLoop();
    }

    public bool ReduceMotion { get; set; }

    /// <summary>Raised when the user clicks START / STOP / RUN AGAIN.</summary>
    public event Action? TestToggleRequested;

    // ------------------------------------------------------------------ build

    private void BuildTiles()
    {
        foreach (NetTarget t in Enum.GetValues<NetTarget>())
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 6, 0) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = NetworkMonitor.TargetLabel(t),
                FontFamily = new FontFamily(Mono),
                FontSize = 9.5,
                Foreground = LabelBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 0);
            head.Children.Add(name);

            var val = new TextBlock
            {
                Text = "\u2014",
                FontFamily = new FontFamily(Mono),
                FontSize = 9.5,
                Foreground = ValueBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(val, 1);
            head.Children.Add(val);

            Grid.SetRow(head, 0);
            grid.Children.Add(head);

            var spark = new Sparkline { Height = 9, Margin = new Thickness(0, 2, 0, 0) };
            Grid.SetRow(spark, 1);
            grid.Children.Add(spark);

            TilesHost.Children.Add(grid);
            _tileValue[t] = val;
            _tileSpark[t] = spark;
        }
    }

    private void BuildStats()
    {
        _pingVal = AddRow("ping");
        _jitterVal = AddRow("jitter");
        _lossVal = AddRow("loss");
        (_downBar, _downVal) = AddBarRow("\u2193 DL", DownBrush);
        (_upBar, _upVal) = AddBarRow("\u2191 UL", UpBrush);
        _statusVal = AddRow("state");
    }

    private TextBlock AddRow(string label)
    {
        var grid = new Grid { Height = 11, Margin = new Thickness(0, 0, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily(Mono),
            FontSize = 10,
            Foreground = LabelBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var val = new TextBlock
        {
            Text = "\u2014",
            FontFamily = new FontFamily(Mono),
            FontSize = 10,
            Foreground = ValueBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(val, 1);
        grid.Children.Add(val);

        StatsRows.Children.Add(grid);
        return val;
    }

    private (DotBar Bar, TextBlock Value) AddBarRow(string label, Brush fill)
    {
        var grid = new Grid { Height = 11, Margin = new Thickness(0, 0, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily(Mono),
            FontSize = 10,
            Foreground = LabelBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var bar = new DotBar
        {
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Value = -1,
            FillColor = ((SolidColorBrush)fill).Color,
        };
        bar.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0));
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);

        var val = new TextBlock
        {
            Text = "\u2014",
            FontFamily = new FontFamily(Mono),
            FontSize = 10,
            Foreground = ValueBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(val, 2);
        grid.Children.Add(val);

        StatsRows.Children.Add(grid);
        return (bar, val);
    }

    // ------------------------------------------------------------------ public render

    public void Render(NetworkMonitor net)
    {
        var snap = net.Snapshot;
        var test = net.TestState;
        bool running = test.Phase is NetTestPhase.Ping or NetTestPhase.Download or NetTestPhase.Upload;

        NetGraph.DownValues = net.DownSeries();
        NetGraph.UpValues = net.UpSeries();
        _lastSampleUtc = DateTime.UtcNow;

        SetTitle(test);

        LblSpeed.Text = snap.HasAdapterData
            ? $"\u2193 {FormatRate(snap.DownMbps)}  \u2191 {FormatRate(snap.UpMbps)}"
            : "\u2193 \u2014  \u2191 \u2014";
        SetLink(snap);

        foreach (NetTarget t in Enum.GetValues<NetTarget>())
        {
            double? ms = net.LastPing(t);
            _tileValue[t].Text = ms is null ? "\u2014" : $"{Math.Round(ms.Value):F0} ms";
            _tileSpark[t].Values = net.PingSeries(t);
        }

        SetQuality(snap, test);
        TodayText.Text = snap.HasAdapterData
            ? $"\u2193{TelemetryFormat.Bytes(snap.TodayRxBytes)} \u2191{TelemetryFormat.Bytes(snap.TodayTxBytes)}"
            : "";

        TestButton.Content = running ? "\u25A0 STOP"
            : test.Phase == NetTestPhase.Done ? "\u21BB RUN AGAIN"
            : "\u25B6 START";
        TestButton.Foreground = running ? WarnBrush : MetricBrush;

        _pingVal.Text = Fmt(test.PingMs, "F0", " ms");
        _jitterVal.Text = Fmt(test.JitterMs, "F0", " ms");
        _lossVal.Text = Fmt(test.LossPercent, "F0", "%");
        SetBar(_downBar, test.DownMbps);
        SetBar(_upBar, test.UpMbps);
        _downVal.Text = Fmt(test.DownMbps, "F1", " Mb/s");
        _upVal.Text = Fmt(test.UpMbps, "F1", " Mb/s");
        _statusVal.Text = StatusLabel(test);
    }

    // ------------------------------------------------------------------ small helpers

    private void SetTitle(NetSpeedState test)
    {
        string status = test.Phase switch
        {
            NetTestPhase.Idle => "ready",
            NetTestPhase.Ping => "ping",
            NetTestPhase.Download => "download",
            NetTestPhase.Upload => "upload",
            NetTestPhase.Done => "done",
            NetTestPhase.Offline => "offline",
            _ => "error",
        };
        bool running = test.Phase is NetTestPhase.Ping or NetTestPhase.Download or NetTestPhase.Upload;
        string p = running ? $"{Math.Round(test.ProgressPercent):F0}%" : "idle";

        TitleText.Inlines.Clear();
        TitleText.Inlines.Add(Run("\u2524", NameBrush));
        TitleText.Inlines.Add(Run("net", MetricBrush));
        TitleText.Inlines.Add(Run($"\u251C\u2524{status}\u251C\u2524{p}\u251C", NameBrush));
    }

    private void SetLink(NetSnapshot snap)
    {
        if (!snap.Connected)
        {
            LblLink.Text = "offline";
            return;
        }

        var parts = new List<string>();
        parts.Add(string.IsNullOrWhiteSpace(snap.Kind) ? "Network" : snap.Kind);
        if (snap.RssiDbm is int rssi) parts.Add($"{rssi} dBm");
        if (snap.LinkMbps is double link and > 0)
            parts.Add(link >= 1000 ? $"{link / 1000.0:F1} Gb/s" : $"{link:F0} Mb/s");

        LblLink.Text = string.Join(" \u00b7 ", parts);
    }

    private void SetQuality(NetSnapshot snap, NetSpeedState test)
    {
        bool hasTest = test.Phase == NetTestPhase.Done && test.DownMbps is not null;
        double down = test.DownMbps ?? 0;
        double up = test.UpMbps ?? 0;
        double ping = test.PingMs ?? double.NaN;
        double jitter = test.JitterMs ?? double.NaN;

        var sb = new List<Inline>();
        void Grade(string label, bool? pass)
        {
            var dot = pass switch { true => GoodBrush, false => BadBrush, null => NoneBrush };
            sb.Add(Run("\u25CF", dot));
            sb.Add(Run(" " + label + "  ", pass is null ? NoneBrush : ValueBrush));
        }

        Grade("4K", hasTest ? down >= 25 : null);
        Grade("Calls", hasTest && !double.IsNaN(ping) ? (ping < 120 && jitter < 40) : null);
        Grade("Gaming", hasTest && !double.IsNaN(ping) ? (ping < 60 && jitter < 20) : null);
        Grade("Cloud", hasTest ? up >= 5 : null);

        QualityText.Inlines.Clear();
        foreach (var inline in sb) QualityText.Inlines.Add(inline);
    }

    private static void SetBar(DotBar bar, double? mbps)
    {
        bar.Value = mbps is double v && v >= 0
            ? Math.Clamp(v / NetworkMonitor.GraphMaxMbps, 0, 1)
            : -1;
    }

    private static string Fmt(double? value, string format, string suffix)
        => value is double v ? v.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + suffix : "\u2014";

    private static string StatusLabel(NetSpeedState test) => test.Phase switch
    {
        NetTestPhase.Idle => "ready",
        NetTestPhase.Ping => "ping",
        NetTestPhase.Download => "download",
        NetTestPhase.Upload => "upload",
        NetTestPhase.Done => "done",
        NetTestPhase.Offline => "offline",
        _ => test.Error is { Length: > 0 } e ? $"error: {e}" : "error",
    };

    private static string FormatRate(double mbps)
    {
        if (mbps <= 0) return "0 B/s";
        long bytesPerSecond = (long)(mbps * 1e6 / 8.0);
        return TelemetryFormat.Bytes(bytesPerSecond) + "/s";
    }

    private static Run Run(string text, Brush brush) => new(text) { Foreground = brush };

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ------------------------------------------------------------------ events / render loop

    private void OnTestClick(object sender, RoutedEventArgs e) => TestToggleRequested?.Invoke();

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
        NetGraph.Scroll = scroll;
    }
}
