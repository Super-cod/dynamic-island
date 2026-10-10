using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WindowsDynamicBar.Core.Timers;
using WindowsDynamicBar.Services.Timers;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Timer card: a dot-matrix countdown in the idle clock's font (scaled down; updates in one frame
/// per second, no ripple), a dotted progress bar, presets 5/10/25/45 min, a custom duration field
/// and Start/Stop. The host owns the one <see cref="TimerService"/>.
/// </summary>
public partial class TimerDetailPanel : UserControl
{
    private const string Mono = "Cascadia Mono, Consolas, Courier New";
    private static readonly Brush NameBrush = Frozen(Color.FromRgb(0xE6, 0xE0, 0xA8));
    private static readonly Brush MetricBrush = Frozen(Color.FromRgb(0x8A, 0xB4, 0x78));
    private static readonly Brush WarnBrush = Frozen(Color.FromRgb(0xD9, 0x8A, 0x5B));
    private static readonly int[] PresetMinutes = { 5, 10, 25, 45 };
    private const double AmberHue = 38;

    private bool _running;

    public TimerDetailPanel()
    {
        InitializeComponent();
        TitleText.Foreground = NameBrush;
        Countdown.Hue = AmberHue;
        Progress.Color = Color.FromRgb(0xE8, 0xB0, 0x4B);
        foreach (int m in PresetMinutes)
        {
            var b = new Border
            {
                Height = 24, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Tag = m,
                BorderBrush = Frozen(Color.FromArgb(0x66, 0x8A, 0xB4, 0x78)), Background = Brushes.Black,
                Child = new TextBlock
                {
                    Text = $"{m} min", FontFamily = new FontFamily(Mono), FontSize = 10.5, Foreground = Frozen(Color.FromRgb(0xE8, 0xE8, 0xDC)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            b.MouseLeftButtonUp += OnPresetClick;
            Presets.Children.Add(b);
        }
    }

    /// <summary>Start a countdown of this length.</summary>
    public event Action<TimeSpan>? StartRequested;
    public event Action? StopRequested;

    public void Render(TimerInfo? info)
    {
        bool active = info is { State: TimerState.Running or TimerState.Paused } && info.Remaining > TimeSpan.Zero;
        _running = active;
        var remaining = active ? info!.Remaining : TimeSpan.Zero;
        Countdown.SetTime(TimerCommand.DotText(remaining), colonOn: true);
        Progress.Fraction = active ? info!.Progress : 0;
        StartStopText.Text = active ? "stop" : "start";
        StartStop.BorderBrush = active ? WarnBrush : MetricBrush;
        StartStopText.Foreground = active ? WarnBrush : MetricBrush;

        StatusText.Text = info?.State switch
        {
            TimerState.Running when active => $"{info.Label} · ends {DateTime.Now + remaining:HH:mm}",
            TimerState.Paused when active => $"{info.Label} · paused",
            TimerState.Finished => "finished",
            _ => "ready",
        };

        TitleText.Inlines.Clear();
        TitleText.Inlines.Add(new Run("┤") { Foreground = NameBrush });
        TitleText.Inlines.Add(new Run("timer") { Foreground = MetricBrush });
        TitleText.Inlines.Add(new Run($"├┤{(active ? (info!.State == TimerState.Paused ? "paused" : "running") : "off")}├")
            { Foreground = NameBrush });
        if (active)
            TitleText.Inlines.Add(new Run($"┤{TimerCommand.Format(remaining)}├") { Foreground = NameBrush });
    }

    private void OnPresetClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: int minutes }) StartRequested?.Invoke(TimeSpan.FromMinutes(minutes));
    }

    private void OnStartStopClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_running) { StopRequested?.Invoke(); return; }
        StartCustom();
    }

    private void StartCustom()
    {
        string text = CustomBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ErrorText.Text = "Pick a preset or type a duration.";
            return;
        }
        if (!TimerCommand.TryParseDuration(text, out var d))
        {
            ErrorText.Text = "Not a duration (try 90s, 12 min, 1h 30m).";
            return;
        }
        ErrorText.Text = "";
        StartRequested?.Invoke(d);
    }

    private void OnCustomChanged(object sender, TextChangedEventArgs e)
    {
        CustomHint.Visibility = CustomBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = "";
    }

    private void OnCustomKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        StartCustom();
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
