using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Interop;
using Brush = System.Windows.Media.Brush;

namespace WindowsDynamicBar.UI;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    public static void ShowSingleton()
    {
        if (_open is null)
        {
            _open = new SettingsWindow();
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        else if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
        _open.Activate();
    }

    private static readonly Brush White = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Faint = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Hairline = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

    private readonly List<Action<SettingsModel>> _pending = new();
    private readonly DispatcherTimerSink _debounce = new();
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();
        _debounce.Fired += Flush;
        Populate();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref on, sizeof(int));
        }
        catch { }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) => Flush();

    // ---------------------------------------------------------------- save plumbing

    private void Set(Action<SettingsModel> mutate)
    {
        if (_loading) return;
        _pending.Add(mutate);
        _debounce.Kick(TimeSpan.FromMilliseconds(350));
        StatusText.Text = "Saving…";
    }

    private void Flush()
    {
        if (_pending.Count == 0) return;
        var batch = _pending.ToArray();
        _pending.Clear();
        try
        {
            App.Settings?.Update(s => { foreach (var a in batch) a(s); });
            StatusText.Text = "Saved";
        }
        catch (Exception ex)
        {
            Log.Error("Settings save failed", ex);
            StatusText.Text = "Save failed — see logs";
        }
    }

    // ---------------------------------------------------------------- builders

    private TextBlock SectionHeader(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
        Margin = new Thickness(0, 20, 0, 10),
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
    };

    private UIElement Row(string label, string? hint, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 13) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12.5, Foreground = White,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        });
        if (!string.IsNullOrEmpty(hint))
            text.Children.Add(new TextBlock
            {
                Text = hint, FontSize = 10.5, Foreground = Faint,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 12, 0),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private UIElement ToggleRow(string label, string? hint, bool value, Action<bool> apply)
    {
        var box = new CheckBox
        {
            IsChecked = value,
            MinWidth = 40,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Foreground = White,
        };
        box.Checked += (_, _) => apply(true);
        box.Unchecked += (_, _) => apply(false);
        return Row(label, hint, box);
    }

    private UIElement SliderRow(string label, string? hint, double min, double max, double value,
        Action<double> apply, Func<double, string> fmt)
    {
        var val = new TextBlock
        {
            Width = 56, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11.5, Foreground = Muted, Margin = new Thickness(10, 0, 0, 0),
            Text = fmt(value),
        };
        var sl = new Slider
        {
            Minimum = min, Maximum = max, Value = value, Width = 170,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sl.ValueChanged += (_, e) => val.Text = fmt(e.NewValue);
        sl.PreviewMouseLeftButtonUp += (_, _) => apply(sl.Value);
        sl.KeyUp += (_, _) => apply(sl.Value);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(sl);
        panel.Children.Add(val);
        return Row(label, hint, panel);
    }

    private UIElement ComboRow(string label, string? hint, string[] items, int selected, Action<int> apply)
    {
        var combo = new ComboBox
        {
            MinWidth = 170, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var i in items) combo.Items.Add(i);
        combo.SelectedIndex = Math.Clamp(selected, 0, items.Length - 1);
        combo.SelectionChanged += (_, _) => { if (!_loading) apply(combo.SelectedIndex); };
        return Row(label, hint, combo);
    }

    private UIElement TextRow(string label, string? hint, string value, Action<string> apply)
    {
        var box = new TextBox
        {
            Text = value, Width = 180, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Foreground = White, BorderBrush = Hairline, Padding = new Thickness(6, 3, 6, 3),
        };
        void Commit()
        {
            if (BarWindow.TryParseHotkey(box.Text, out _, out _))
            {
                box.BorderBrush = Hairline;
                apply(box.Text.Trim());
            }
            else
            {
                box.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            }
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyUp += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        return Row(label, hint, box);
    }

    private UIElement ButtonRow(params (string Label, Action Click)[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        foreach (var (label, click) in buttons)
        {
            var b = new Button
            {
                Content = label, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5),
                FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            };
            b.Click += (_, _) => click();
            panel.Children.Add(b);
        }
        return Row(null!, null, panel);
    }

    // ---------------------------------------------------------------- content

    private void Populate()
    {
        _loading = true;
        var root = Root;
        root.Children.Clear();
        var s = App.Settings?.Current ?? new SettingsModel();

        // ---- General ----
        root.Children.Add(SectionHeader("General"));
        root.Children.Add(ToggleRow("Start with Windows",
            "Runs at sign-in (HKCU Run key)",
            s.EnableAtStartup, v => { Set(x => x.EnableAtStartup = v); StartupRegistration.Apply(v); }));
        root.Children.Add(ToggleRow("Show the bar", null, s.BarEnabled,
            v => { Set(x => x.BarEnabled = v); if (v) App.Bar?.Show(); else App.Bar?.Hide(); }));
        root.Children.Add(ComboRow("Monitor", "Where the pill is anchored",
            BuildMonitorItems(), s.MonitorIndex + 1,
            i => Set(x => x.MonitorIndex = i - 1)));
        root.Children.Add(ToggleRow("Hover to expand",
            "Preview the active card when the cursor is over the pill",
            s.HoverPreview, v => Set(x => x.HoverPreview = v)));
        root.Children.Add(SliderRow("Auto-collapse delay", "Idle card returns to the pill after this",
            600, 8000, s.CollapseDelayMs, v => Set(x => x.CollapseDelayMs = (int)v),
            v => $"{Math.Round(v / 1000.0 * 10) / 10:0.0} s"));
        root.Children.Add(ToggleRow("Hide over fullscreen apps",
            "The bar steps out of the way of games and video",
            s.AutoHideFullscreen, v => Set(x => x.AutoHideFullscreen = v)));
        root.Children.Add(ToggleRow("Draggable position", "Drag the pill horizontally to reposition it",
            true, _ => { }));

        // ---- Hotkeys ----
        root.Children.Add(SectionHeader("Global hotkeys"));
        root.Children.Add(TextRow("Toggle bar", "Examples: Ctrl+Alt+Space, Ctrl+Shift+B, F9",
            s.HotkeyToggle, v => Set(x => x.HotkeyToggle = v)));
        root.Children.Add(TextRow("Media card", "Show the media player",
            s.HotkeyMedia, v => Set(x => x.HotkeyMedia = v)));
        root.Children.Add(TextRow("Timer", "Start or show a 5-minute timer",
            s.HotkeyTimer, v => Set(x => x.HotkeyTimer = v)));

        // ---- Appearance ----
        root.Children.Add(SectionHeader("Appearance"));
        root.Children.Add(ComboRow("Accent color", null,
            new[] { "Blue", "Purple", "Pink", "Green", "Orange", "Yellow", "Red", "Sky", "Gray" },
            AccentIndex(s.AccentColor),
            i => Set(x => x.AccentColor = AccentPalette[i])));
        root.Children.Add(SliderRow("Background opacity", null,
            0.4, 1.0, s.Opacity, v => Set(x => x.Opacity = v),
            v => $"{Math.Round(v * 100)}%"));
        root.Children.Add(SliderRow("Corner radius", null,
            8, 40, s.CornerRadius, v => Set(x => x.CornerRadius = v),
            v => $"{Math.Round(v)}"));
        root.Children.Add(SliderRow("Collapsed pill width", null,
            160, 380, s.BarWidth, v => Set(x => x.BarWidth = v),
            v => $"{Math.Round(v)} px"));
        root.Children.Add(SliderRow("Collapsed pill height", null,
            28, 64, s.BarHeight, v => Set(x => x.BarHeight = v),
            v => $"{Math.Round(v)} px"));
        root.Children.Add(ToggleRow("Backdrop blur", "DWM blur clipped to the pill shape",
            s.BlurEnabled, v => Set(x => x.BlurEnabled = v)));
        root.Children.Add(ToggleRow("Drop shadow", null, s.ShadowEnabled,
            v => Set(x => x.ShadowEnabled = v)));

        // ---- Animations ----
        root.Children.Add(SectionHeader("Animations"));
        root.Children.Add(SliderRow("Animation speed", "1.0 = default spring feel",
            0.3, 2.0, s.AnimationSpeed, v => Set(x => x.AnimationSpeed = v),
            v => $"{v:0.0}×"));
        root.Children.Add(SliderRow("Spring stiffness", "Higher = snappier expansion",
            60, 400, s.SpringStiffness, v => Set(x => x.SpringStiffness = v),
            v => $"{Math.Round(v)}"));
        root.Children.Add(SliderRow("Spring damping", "Higher = less bounce",
            4, 60, s.SpringDamping, v => Set(x => x.SpringDamping = v),
            v => $"{Math.Round(v)}"));
        root.Children.Add(ToggleRow("Reduce motion", "Shortens all transitions",
            s.ReduceMotion, v => Set(x => x.ReduceMotion = v)));

        // ---- Features ----
        root.Children.Add(SectionHeader("Features"));
        root.Children.Add(ToggleRow("Media player", "Album art, transport, progress",
            s.FeatureMedia, v => Set(x => x.FeatureMedia = v)));
        root.Children.Add(ToggleRow("Volume", null, s.FeatureVolume,
            v => Set(x => x.FeatureVolume = v)));
        root.Children.Add(ToggleRow("Brightness",
            "Internal panels only (WMI); external monitors without DDC are skipped",
            s.FeatureBrightness, v => Set(x => x.FeatureBrightness = v)));
        root.Children.Add(ToggleRow("Battery", null, s.FeatureBattery,
            v => Set(x => x.FeatureBattery = v)));
        root.Children.Add(ToggleRow("Network", null, s.FeatureNetwork,
            v => Set(x => x.FeatureNetwork = v)));
        root.Children.Add(ToggleRow("Bluetooth", null, s.FeatureBluetooth,
            v => Set(x => x.FeatureBluetooth = v)));
        root.Children.Add(ToggleRow("Timers, stopwatch, focus", null, s.FeatureTimers,
            v => Set(x => x.FeatureTimers = v)));
        root.Children.Add(ToggleRow("Downloads",
            "Watches the Downloads folder for in-progress files",
            s.FeatureDownloads, v => Set(x => x.FeatureDownloads = v)));
        root.Children.Add(ToggleRow("USB drive alerts", null, s.FeatureUsb,
            v => Set(x => x.FeatureUsb = v)));
        root.Children.Add(ToggleRow("Screenshot alerts",
            "Watches Pictures\\Screenshots for new files",
            s.FeatureScreenshots, v => Set(x => x.FeatureScreenshots = v)));
        root.Children.Add(ToggleRow("Privacy indicators", "Camera / microphone in use",
            s.FeaturePrivacyIndicators, v => Set(x => x.FeaturePrivacyIndicators = v)));
        root.Children.Add(ToggleRow("System monitor",
            "CPU / GPU / RAM card; also adds a tray toggle",
            s.FeatureSystemMonitor, v =>
            {
                Set(x => x.FeatureSystemMonitor = v);
                try { if (v) App.SysMon?.Activate(); else App.SysMon?.Deactivate(); } catch { }
            }));
        root.Children.Add(ToggleRow("Notifications",
            "Needs packaged (MSIX) notification access — gracefully disabled in .exe builds",
            s.FeatureNotifications, v => Set(x => x.FeatureNotifications = v)));
        root.Children.Add(ToggleRow("Show album art on collapsed pill",
            null, s.CollapsedMediaArt, v => Set(x => x.CollapsedMediaArt = v)));
        root.Children.Add(ToggleRow("CPU / RAM on collapsed pill",
            null, s.SystemMonitorInBar, v => Set(x => x.SystemMonitorInBar = v)));

        // ---- Privacy ----
        root.Children.Add(SectionHeader("Privacy"));
        root.Children.Add(ToggleRow("Clipboard format monitor",
            "Detects that a copy happened and its format only — contents are never read or stored (off by default)",
            s.ClipboardMonitoring, v => Set(x => x.ClipboardMonitoring = v)));
        root.Children.Add(ToggleRow("Notification access",
            "Windows requires a packaged app for notification listening; available when installed via the installer",
            s.NotificationAccess, v => Set(x => x.NotificationAccess = v)));
        root.Children.Add(ToggleRow("Local-only mode",
            "All processing stays on this PC (permanently on)",
            s.LocalOnlyMode, _ => { }));
        root.Children.Add(ToggleRow("Anonymous telemetry",
            "Never sent — the app contains no network reporting code (permanently off)",
            false, _ => { }));

        // ---- About ----
        root.Children.Add(SectionHeader("About"));
        root.Children.Add(Row(App.AppName + " " + App.InfoVersion,
            $"Settings: {App.Settings?.SettingsPath}",
            new TextBlock
            {
                Text = "Open-source style build · no ads · no accounts",
                FontSize = 11, Foreground = Faint, VerticalAlignment = VerticalAlignment.Center,
            }));
        root.Children.Add(ButtonRow(
            ("Open logs", () =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe", Arguments = Log.LogDirectory, UseShellExecute = true,
                    });
                }
                catch (Exception ex) { Log.Error("Open logs", ex); }
            }),
            ("Clear logs", () => { Log.ClearAll(); StatusText.Text = "Logs cleared"; }),
            ("Open settings file", () =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = App.Settings?.SettingsPath, UseShellExecute = true,
                    });
                }
                catch (Exception ex) { Log.Error("Open settings file", ex); }
            })));

        _loading = false;
    }

    private static string[] BuildMonitorItems()
    {
        var items = new List<string> { "Primary (default)" };
        try
        {
            var mons = NativeMethods.GetMonitors();
            for (int i = 0; i < mons.Count; i++)
                items.Add($"Display {i + 1}  ({mons[i].Bounds.Right - mons[i].Bounds.Left}" +
                          $"×{mons[i].Bounds.Bottom - mons[i].Bounds.Top})");
        }
        catch { }
        return items.ToArray();
    }

    private static readonly string[] AccentPalette =
    {
        "#FF0A84FF", "#FFBF5AF2", "#FFFF375F", "#FF30D158", "#FFFF9F0A",
        "#FFFFD60A", "#FFFF453A", "#FF64D2FF", "#FF98989D",
    };

    private static int AccentIndex(string hex)
    {
        for (int i = 0; i < AccentPalette.Length; i++)
            if (AccentPalette[i].Equals(hex, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    // ---------------------------------------------------------------- footer buttons

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Reset every setting to its default value?\n\nYour hotkeys, theme and feature choices will be lost.",
            "Reset defaults", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            App.Settings?.Save(new SettingsModel());
            StartupRegistration.Apply(true);
            Populate();
            StatusText.Text = "Defaults restored";
        }
        catch (Exception ex) { Log.Error("Reset failed", ex); }
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        Flush();
        Close();
    }
}

/// <summary>One-shot debounce timer without a Dispatcher reference at construction.</summary>
internal sealed class DispatcherTimerSink
{
    private readonly System.Windows.Threading.DispatcherTimer _t;
    public event Action? Fired;
    public DispatcherTimerSink()
    {
        _t = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(350) };
        _t.Tick += (_, _) => { _t.Stop(); Fired?.Invoke(); };
    }
    public void Kick(TimeSpan delay) { _t.Interval = delay; _t.Stop(); _t.Start(); }
}
