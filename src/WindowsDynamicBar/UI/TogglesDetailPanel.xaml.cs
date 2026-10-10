using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WindowsDynamicBar.Core.Animation;
using WindowsDynamicBar.Services.Toggles;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Quick toggles card: round 46px buttons (ON = accent-filled ring with a white icon, OFF = dim
/// ring), a 10px monospace label under each, and a status line. Only toggles the service reports
/// as supported are shown. A click pulses the button (150 ms) and asks the host to change it;
/// the button only flips when the real state comes back.
/// </summary>
public partial class TogglesDetailPanel : UserControl
{
    private const string Mono = "Cascadia Mono, Consolas, Courier New";
    private static readonly Brush NameBrush = Frozen(Color.FromRgb(0xE6, 0xE0, 0xA8));
    private static readonly Brush MetricBrush = Frozen(Color.FromRgb(0x8A, 0xB4, 0x78));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x9A, 0xA0, 0x88));
    private static readonly Brush IconOn = Frozen(Colors.White);
    private static readonly Brush IconOff = Frozen(Color.FromArgb(0xB3, 0xE8, 0xE8, 0xDC));

    private sealed class Button
    {
        public required ToggleId Id;
        public required FrameworkElement Root;
        public required Border Ring;
        public required TextBlock Glyph;
        public required TextBlock Label;
        public required ScaleTransform Scale;
        public ToggleState? State;
    }

    private readonly Dictionary<ToggleId, Button> _buttons = new();
    private readonly List<TweenAnimation> _pulses = new();

    public TogglesDetailPanel()
    {
        InitializeComponent();
        TitleText.Foreground = NameBrush;
        Unloaded += (_, _) => { foreach (var p in _pulses) p.Stop(); };
    }

    public bool ReduceMotion { get; set; }

    /// <summary>User clicked a toggle: (id, requested new state).</summary>
    public event Action<ToggleId, bool>? ToggleRequested;

    public void Render(IReadOnlyList<ToggleState> states, string status)
    {
        // Rebuild only when the set of supported toggles changed (no per-update element churn).
        bool sameSet = states.Count == _buttons.Count;
        if (sameSet) foreach (var s in states) if (!_buttons.ContainsKey(s.Id)) { sameSet = false; break; }
        if (!sameSet) Rebuild(states);

        int on = 0;
        foreach (var s in states)
        {
            var b = _buttons[s.Id];
            b.State = s;
            if (s.On) on++;
            b.Glyph.Text = s.On ? s.GlyphOn : s.GlyphOff;
            b.Label.Text = s.Label;
            if (s.On)
            {
                b.Ring.SetResourceReference(Border.BackgroundProperty, "WdbAccentBrush");
                b.Ring.SetResourceReference(Border.BorderBrushProperty, "WdbAccentBrush");
                b.Glyph.Foreground = IconOn;
            }
            else
            {
                b.Ring.SetResourceReference(Border.BackgroundProperty, "WdbDimBrush");
                b.Ring.SetResourceReference(Border.BorderBrushProperty, "WdbHairlineBrush");
                b.Glyph.Foreground = IconOff;
            }
            b.Root.ToolTip = $"{s.Label}: {(s.On ? "on" : "off")}";
        }

        StatusText.Text = status;
        HintText.Text = states.Count == 0 ? "No toggle can be controlled on this PC." : "";

        TitleText.Inlines.Clear();
        TitleText.Inlines.Add(new Run("┤") { Foreground = NameBrush });
        TitleText.Inlines.Add(new Run("toggles") { Foreground = MetricBrush });
        TitleText.Inlines.Add(new Run($"├┤{on} on├") { Foreground = NameBrush });
    }

    private void Rebuild(IReadOnlyList<ToggleState> states)
    {
        foreach (var b in _buttons.Values) b.Root.MouseLeftButtonUp -= OnButtonUp;
        _buttons.Clear();
        ToggleGrid.Children.Clear();
        foreach (var s in states)
        {
            var scale = new ScaleTransform(1, 1);
            var ring = new Border
            {
                Width = 46, Height = 46, CornerRadius = new CornerRadius(23), BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var glyph = new TextBlock
            {
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            ring.Child = glyph;
            // 46 ring + 1 gap + 11 label line = 58 per row, so two rows fit under the title tag.
            var label = new TextBlock
            {
                FontFamily = new FontFamily(Mono), FontSize = 10, Foreground = LabelBrush,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
                LineHeight = 11, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var stack = new StackPanel
            {
                Width = 62, Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = s.Id,
                RenderTransform = scale, RenderTransformOrigin = new Point(0.5, 0.4),
            };
            stack.Children.Add(ring);
            stack.Children.Add(label);
            stack.MouseLeftButtonUp += OnButtonUp;
            ToggleGrid.Children.Add(stack);
            _buttons[s.Id] = new Button { Id = s.Id, Root = stack, Ring = ring, Glyph = glyph, Label = label, Scale = scale };
        }
        ToggleGrid.Rows = states.Count > 4 ? 2 : 1;
        ToggleGrid.Columns = Math.Clamp(states.Count, 1, 4);
        ToggleGrid.Margin = new Thickness(0, states.Count > 4 ? 8 : 0, 0, 0);
    }

    private void OnButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: ToggleId id } || !_buttons.TryGetValue(id, out var b) || b.State is null) return;
        Pulse(b);
        ToggleRequested?.Invoke(id, !b.State.On);
    }

    /// <summary>150 ms press pulse (scale 1 -> 0.92 -> 1) on the shared tween utility.</summary>
    private void Pulse(Button b)
    {
        if (ReduceMotion) return;
        var tw = new TweenAnimation();
        _pulses.Add(tw);
        tw.Completed += () => { b.Scale.ScaleX = b.Scale.ScaleY = 1; _pulses.Remove(tw); tw.Dispose(); };
        tw.Run(0, 1, TimeSpan.FromMilliseconds(150), t =>
        {
            double s = 1 - 0.08 * Math.Sin(Math.PI * t);
            b.Scale.ScaleX = s;
            b.Scale.ScaleY = s;
        }, t => t);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
