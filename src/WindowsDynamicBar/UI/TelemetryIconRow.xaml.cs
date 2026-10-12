using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WindowsDynamicBar.Core.Animation;
using WindowsDynamicBar.Core.Telemetry;

namespace WindowsDynamicBar.UI;

/// <summary>
/// The ring grid on the right of the open island: pages of up to 6 ring cells (3x2) with a pager.
/// Cells are built from the page layout (settings) and look exactly like the original XAML cells.
/// Page changes slide the grid by its own width with a fade (300 ms, CubicOut, Reduce Motion =
/// instant) using the shared <see cref="TweenAnimation"/>.
/// </summary>
public partial class TelemetryIconRow : UserControl
{
    private const string Mono = "Cascadia Mono, Consolas";
    private const double PageWidth = 175;
    private const int PageSlideMs = 300;

    private sealed record Cell(FrameworkElement Root, Border Border, RingGauge Ring, TextBlock Value, IslandMetric Metric);

    private readonly Dictionary<IslandMetric, Cell> _cells = new();
    private readonly List<IslandMetric>[] _pages = { new(), new() };
    private readonly HashSet<IslandMetric> _available = new();
    private readonly TweenAnimation _slide = new();
    private readonly List<Border> _dots = new();
    private IslandMetric _active = IslandMetric.None;
    private int _page;
    private double _slidePos;   // 0 = page 0 shown, 1 = page 1 shown

    public TelemetryIconRow()
    {
        InitializeComponent();
        SetLayout(IslandLayout.DefaultPage1, IslandLayout.DefaultPage2);
        Unloaded += (_, _) => _slide.Stop();
    }

    /// <summary>Pointer entered a ring.</summary>
    public event Action<IslandMetric>? MetricHovered;
    /// <summary>Pointer left a ring.</summary>
    public event Action? MetricUnhovered;
    /// <summary>Ring clicked.</summary>
    public event Action<IslandMetric>? MetricClicked;
    /// <summary>The visible page changed (0-based).</summary>
    public event Action<int>? PageChanged;

    public static readonly DependencyProperty ReduceMotionProperty =
        DependencyProperty.Register(nameof(ReduceMotion), typeof(bool), typeof(TelemetryIconRow),
            new PropertyMetadata(false));

    public bool ReduceMotion
    {
        get => (bool)GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    public int Page => _page;
    public int PageCount => _pages[1].Count > 0 ? 2 : 1;

    /// <summary>True when <paramref name="m"/> has a ring on the page currently shown.</summary>
    public bool IsOnVisiblePage(IslandMetric m) => _pages[_page].Contains(m);

    /// <summary>0-based page that holds <paramref name="m"/>, or -1 when it has no ring.</summary>
    public int PageOf(IslandMetric m) => _pages[0].Contains(m) ? 0 : _pages[1].Contains(m) ? 1 : -1;

    // ------------------------------------------------------------------ layout

    /// <summary>(Re)build both pages from metric lists (max 6 each, no duplicates).</summary>
    public void SetLayout(IReadOnlyList<IslandMetric> page1, IReadOnlyList<IslandMetric> page2)
    {
        var seen = new HashSet<IslandMetric>();
        _pages[0] = page1.Where(m => m != IslandMetric.None && seen.Add(m)).Take(6).ToList();
        _pages[1] = page2.Where(m => m != IslandMetric.None && seen.Add(m)).Take(6).ToList();
        if (_pages[0].Count == 0 && _pages[1].Count > 0) (_pages[0], _pages[1]) = (_pages[1], _pages[0]);

        Page0.Children.Clear();
        Page1.Children.Clear();
        foreach (var cell in _cells.Values) DetachCell(cell);
        _cells.Clear();
        _ramPulsing = false;   // the pulse lived on the old RAM cell; the new one starts still
        for (int p = 0; p < 2; p++)
        {
            var host = p == 0 ? Page0 : Page1;
            var list = _pages[p];
            for (int i = 0; i < list.Count; i++)
            {
                var cell = CreateCell(list[i], lastCell: i == 5);
                _cells[list[i]] = cell;
                host.Children.Add(cell.Root);
            }
        }
        foreach (var m in _cells.Keys) _available.Add(m);

        BuildPager();
        SetPage(Math.Min(_page, PageCount - 1), instant: true);
        SetActive(_active);
    }

    private Cell CreateCell(IslandMetric metric, bool lastCell)
    {
        // Same geometry as the original XAML cells: 46px cell, 3px ring, 38px dim disc with a
        // hairline; every cell but the sixth keeps a 10.5 bottom margin (as before).
        var root = new Grid
        {
            Width = 46, Height = 46,
            Margin = new Thickness(0, 0, 10.5, lastCell ? 0 : 10.5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        var ring = new RingGauge { Thickness = 3.0 };
        if (IslandLayout.FixedRingColor(metric) is Color fixedColor) ring.RingColor = fixedColor;
        var border = new Border
        {
            Width = 38, Height = 38,
            CornerRadius = new CornerRadius(19),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
        };
        border.SetResourceReference(Border.BackgroundProperty, "WdbDimBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "WdbHairlineBrush");

        string label = IslandLayout.Label(metric);
        double labelSize = label.Length > 4 ? 8.5 : 9;
        var labelText = new TextBlock
        {
            Text = label, FontFamily = new FontFamily(Mono), FontSize = labelSize,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "WdbTextMutedBrush");
        var value = new TextBlock
        {
            Text = metric == IslandMetric.Mirror ? "off" : "",
            FontFamily = new FontFamily(Mono), FontSize = label.Length > 4 ? 8 : 8.5,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, "WdbTextBrush");
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(labelText);
        stack.Children.Add(value);
        border.Child = stack;
        root.Children.Add(ring);
        root.Children.Add(border);

        var cell = new Cell(root, border, ring, value, metric);
        root.MouseEnter += OnCellEnter;
        root.MouseLeave += OnCellLeave;
        root.MouseLeftButtonUp += OnCellUp;
        root.Tag = metric;
        return cell;
    }

    private void DetachCell(Cell c)
    {
        c.Root.MouseEnter -= OnCellEnter;
        c.Root.MouseLeave -= OnCellLeave;
        c.Root.MouseLeftButtonUp -= OnCellUp;
    }

    private void OnCellEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: IslandMetric m } && Available(m)) MetricHovered?.Invoke(m);
    }

    private void OnCellLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: IslandMetric m } && Available(m)) MetricUnhovered?.Invoke();
    }

    private void OnCellUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: IslandMetric m } && Available(m)) MetricClicked?.Invoke(m);
    }

    // ------------------------------------------------------------------ pager

    private void BuildPager()
    {
        foreach (var d in _dots) d.MouseLeftButtonUp -= OnDotUp;
        _dots.Clear();
        Pager.Children.Clear();
        Pager.Visibility = PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < PageCount; i++)
        {
            // 10x14 hit area around a 6px dot so it is easy to click.
            var dot = new Border
            {
                Width = 10, Height = 14, Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = i,
                Child = new Ellipse { Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                ToolTip = $"Page {i + 1}",
            };
            dot.MouseLeftButtonUp += OnDotUp;
            _dots.Add(dot);
            Pager.Children.Add(dot);
        }
        UpdateDots();
    }

    private void OnDotUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: int i }) SetPage(i);
    }

    private static readonly Brush DotOn = Frozen(Colors.White);
    private static readonly Brush DotOff = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    private void UpdateDots()
    {
        for (int i = 0; i < _dots.Count; i++)
            ((Ellipse)_dots[i].Child).Fill = i == _page ? DotOn : DotOff;
    }

    /// <summary>Show page <paramref name="page"/> (clamped). Slides with a fade unless instant / Reduce Motion.</summary>
    public void SetPage(int page, bool instant = false)
    {
        page = Math.Clamp(page, 0, PageCount - 1);
        bool changed = page != _page;
        _page = page;
        UpdateDots();
        Page0.IsHitTestVisible = page == 0;
        Page1.IsHitTestVisible = page == 1;

        double target = page;
        if (instant || ReduceMotion || !IsVisible)
        {
            _slide.Stop();
            ApplySlide(target);
        }
        else
        {
            // Restarting from the current position keeps rapid switches smooth (no snap, no stuck state).
            _slide.Run(_slidePos, target, TimeSpan.FromMilliseconds(PageSlideMs), ApplySlide, Easing.CubicOut);
        }
        if (changed) PageChanged?.Invoke(page);
    }

    public void NextPage() => SetPage(_page + 1 >= PageCount ? 0 : _page + 1);
    public void PreviousPage() => SetPage(_page - 1 < 0 ? PageCount - 1 : _page - 1);

    private void ApplySlide(double v)
    {
        _slidePos = v;
        Page0Shift.X = -PageWidth * v;
        Page1Shift.X = PageWidth * (1 - v);
        Page0.Opacity = 1 - v;
        Page1.Opacity = v;
    }

    // ------------------------------------------------------------------ values

    private bool _ramPulsing;

    public void UpdateValues(double cpu, double gpu, double ram)
    {
        Apply(IslandMetric.Cpu, cpu);
        Apply(IslandMetric.Gpu, gpu);
        ApplyRam(ram);
    }

    public static Color GetRamColor(double percent)
    {
        if (percent < 75) return Color.FromRgb(0x4C, 0xD9, 0x64);
        if (percent < 90) return Color.FromRgb(0xD9, 0x8A, 0x5B);
        return Color.FromRgb(0xE5, 0x48, 0x4D);
    }

    private void ApplyRam(double percent)
    {
        if (!_cells.TryGetValue(IslandMetric.Ram, out var c)) return;
        if (percent < 0)
        {
            c.Ring.Value = -1;
            c.Value.Text = "";   // unavailable: no value, no placeholder
            StopRamPulse(c);
            return;
        }

        c.Ring.Value = Math.Clamp(percent, 0, 100);
        c.Value.Text = $"{Math.Round(percent):F0}%";
        c.Ring.RingColor = GetRamColor(percent);

        if (percent >= 90 && !ReduceMotion && !_ramPulsing)
        {
            _ramPulsing = true;
            var anim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(500))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };
            c.Ring.BeginAnimation(UIElement.OpacityProperty, anim);
        }
        else if (percent < 90 || ReduceMotion) StopRamPulse(c);
    }

    private void StopRamPulse(Cell c)
    {
        if (!_ramPulsing) return;
        _ramPulsing = false;
        c.Ring.BeginAnimation(UIElement.OpacityProperty, null);
        c.Ring.Opacity = 1.0;
    }

    /// <summary>The RAM warning pulse is a forever animation; stop it while the grid is not on screen.</summary>
    public void SuspendPulse()
    {
        if (_cells.TryGetValue(IslandMetric.Ram, out var c)) StopRamPulse(c);
    }

    /// <summary>Net gauge ring (0..100) and its small label (e.g. "42%", "187M", "").</summary>
    public void UpdateNet(double ringPercent, string label)
        => SetRing(IslandMetric.Net, ringPercent, label, Color.FromRgb(0x6F, 0xA8, 0xDC));

    /// <summary>Mirror camera state: full red ring reads "LIVE" while capturing, empty ring "off".</summary>
    public void UpdateMirror(bool live, string label)
        => SetRing(IslandMetric.Mirror, live ? 100 : -1, label, Color.FromRgb(0xE5, 0x48, 0x4D));

    /// <summary>Weather gauge ring (0..100, or negative for none) with an accent colour and label.</summary>
    public void UpdateWeather(double ringPercent, string label, Color color)
        => SetRing(IslandMetric.Weather, ringPercent, label, color);

    /// <summary>Generic ring update (page-2 rings: clipboard fill, timer progress; -1 hides the arc).</summary>
    public void SetRing(IslandMetric metric, double ringPercent, string label, Color? color = null)
    {
        if (!_cells.TryGetValue(metric, out var c)) return;
        c.Ring.Value = ringPercent < 0 ? -1 : Math.Clamp(ringPercent, 0, 100);
        if (color is Color col) c.Ring.RingColor = col;
        c.Value.Text = label;
    }

    public void SetAvailability(bool cpu, bool gpu, bool ram)
    {
        SetAvailable(IslandMetric.Cpu, cpu);
        SetAvailable(IslandMetric.Gpu, gpu);
        SetAvailable(IslandMetric.Ram, ram);
    }

    public void SetAvailable(IslandMetric metric, bool available)
    {
        if (available) _available.Add(metric); else _available.Remove(metric);
        if (_cells.TryGetValue(metric, out var c))
        {
            c.Root.Cursor = available ? Cursors.Hand : Cursors.Arrow;
            c.Border.ToolTip = available ? null : "Not available";
        }
    }

    public void SetActive(IslandMetric metric)
    {
        _active = metric;
        foreach (var c in _cells.Values) Highlight(c.Border, c.Metric == metric);
    }

    private bool Available(IslandMetric m) => _available.Contains(m);

    private void Apply(IslandMetric metric, double percent)
    {
        if (!_cells.TryGetValue(metric, out var c)) return;
        if (percent < 0)
        {
            c.Ring.Value = -1;
            c.Value.Text = "";
            return;
        }
        c.Ring.Value = Math.Clamp(percent, 0, 100);
        c.Ring.RingColor = DotBar.ValueColor(percent);
        c.Value.Text = $"{Math.Round(percent):F0}%";
    }

    private void Highlight(Border border, bool active)
    {
        if (active && TryFindResource("WdbAccentBrush") is SolidColorBrush accent)
        {
            border.BorderBrush = accent;
            var c = accent.Color;
            var tint = new SolidColorBrush(Color.FromArgb(46, c.R, c.G, c.B));
            tint.Freeze();
            border.Background = tint;
            border.BorderThickness = new Thickness(2);
            return;
        }
        border.SetResourceReference(Border.BorderBrushProperty, "WdbHairlineBrush");
        border.SetResourceReference(Border.BackgroundProperty, "WdbDimBrush");
        border.BorderThickness = new Thickness(1);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
