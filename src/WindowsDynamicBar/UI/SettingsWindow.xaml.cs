using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Interop;
using Brush = System.Windows.Media.Brush;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Settings: General, Island, Features, Appearance, Privacy, About. Every change is written
/// (debounced 350 ms) through the one <see cref="SettingsService"/>, whose change event applies it
/// live to the island and services. Follows the Windows light/dark theme, also while open.
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    public static void ShowSingleton(string? initialPage = null)
    {
        if (_open is null)
        {
            _open = new SettingsWindow();
            _open.Closed += (_, _) => _open = null;
            if (!string.IsNullOrEmpty(initialPage)) _open.SelectPage(initialPage);
            _open.Show();
        }
        else
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            if (!string.IsNullOrEmpty(initialPage)) _open.SelectPage(initialPage);
        }
        _open.Activate();
    }

    private static readonly (string Key, string Title, string Glyph)[] Pages =
    {
        ("general", "General", ""),
        ("island", "Island", ""),
        ("features", "Features", ""),
        ("appearance", "Appearance", ""),
        ("privacy", "Privacy", ""),
        ("about", "About", ""),
    };

    private readonly List<Action<SettingsModel>> _pending = new();
    private readonly DispatcherTimerSink _debounce = new();
    private readonly List<RadioButton> _nav = new();
    private HwndSource? _src;
    private IntPtr _hwnd;
    private bool _loading;
    private string _page = "general";

    public SettingsWindow()
    {
        InitializeComponent();
        ApplyPalette();
        _debounce.Fired += Flush;
        AppIcon.Source = LoadIcon(20);
        foreach (var (key, title, glyph) in Pages)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            content.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
            var b = new RadioButton { Content = content, Tag = key, GroupName = "nav", Style = (Style)FindResource("NavButton") };
            b.Checked += OnNavChecked;
            _nav.Add(b);
            NavPanel.Children.Add(b);
        }
        SelectPage("general");
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key } && key != _page) SelectPage(key);
    }

    private void SelectPage(string key)
    {
        if (Array.FindIndex(Pages, p => p.Key == key) < 0) key = key == "widgets" ? "island" : "general";
        Flush();
        _page = key;
        foreach (var b in _nav) b.IsChecked = (string)b.Tag == key;
        PageTitle.Text = Array.Find(Pages, p => p.Key == key).Title;
        Populate();
    }

    // ---------------------------------------------------------------- theme

    private static bool SystemUsesLight()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v != 0;
    }

    /// <summary>Windows 11 light or dark palette (follows the app theme setting in Windows).</summary>
    private void ApplyPalette()
    {
        bool light = SystemUsesLight();
        Color accent = Color.FromRgb(0x4C, 0xC2, 0xFF);
        try
        {
            if (App.Settings?.Current.AccentColor is { } hex && ColorConverter.ConvertFromString(hex) is Color c) accent = c;
        }
        catch (FormatException) { /* keep the default accent */ }
        void Put(string k, Color c) { var b = new SolidColorBrush(c); b.Freeze(); Resources[k] = b; }
        Put("S.Bg", light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20));
        Put("S.Card", light ? Colors.White : Color.FromRgb(0x2B, 0x2B, 0x2B));
        Put("S.Control", light ? Color.FromRgb(0xFB, 0xFB, 0xFB) : Color.FromRgb(0x33, 0x33, 0x33));
        Put("S.Text", light ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Colors.White);
        Put("S.Muted", light ? Color.FromRgb(0x60, 0x60, 0x60) : Color.FromRgb(0xC5, 0xC5, 0xC5));
        Put("S.Hairline", light ? Color.FromArgb(0x1F, 0, 0, 0) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        Put("S.Hover", light ? Color.FromArgb(0x0A, 0, 0, 0) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        Put("S.Selected", light ? Color.FromArgb(0x10, 0, 0, 0) : Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        Put("S.Accent", accent);
        Put("S.OnAccent", (accent.R * 0.299 + accent.G * 0.587 + accent.B * 0.114) > 150 ? Colors.Black : Colors.White);
        Put("S.Warn", light ? Color.FromRgb(0xC4, 0x2B, 0x1C) : Color.FromRgb(0xFF, 0x99, 0xA4));
        if (_hwnd != IntPtr.Zero)
        {
            int dark = light ? 0 : 1;
            NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _src = HwndSource.FromHwnd(_hwnd);
        _src?.AddHook(WndProc);
        ApplyPalette();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            Dispatcher.BeginInvoke(ApplyPalette);
        return IntPtr.Zero;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Flush();
        App.Bar?.SetHotkeysSuspended(false);
        _src?.RemoveHook(WndProc);
        _debounce.Fired -= Flush;
        foreach (var b in _nav) b.Checked -= OnNavChecked;
    }

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
        App.Settings?.Update(s => { foreach (var a in batch) a(s); });
        StatusText.Text = "Saved · applied";
    }

    private static SettingsModel Current => App.Settings?.Current ?? new SettingsModel();

    // ---------------------------------------------------------------- builders

    private StackPanel _card = new();

    private void Card(string? title)
    {
        if (title is not null)
            Root.Children.Add(new TextBlock
            {
                Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 14, 0, 6),
            }.WithResource(TextBlock.ForegroundProperty, "S.Text"));
        _card = new StackPanel();
        var border = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(16, 4, 16, 4), Child = _card };
        border.SetResourceReference(Border.BackgroundProperty, "S.Card");
        border.SetResourceReference(Border.BorderBrushProperty, "S.Hairline");
        Root.Children.Add(border);
    }

    private void Row(string label, string? hint, UIElement? control, bool wrapBelow = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 9, 0, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap }.WithResource(TextBlock.ForegroundProperty, "S.Text"));
        if (!string.IsNullOrEmpty(hint))
            text.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 16, 0) }
                .WithResource(TextBlock.ForegroundProperty, "S.Muted"));
        grid.Children.Add(text);
        if (control is not null)
        {
            if (wrapBelow)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow((UIElement)control, 1);
                Grid.SetColumnSpan((UIElement)control, 2);
                if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0);
            }
            else
            {
                Grid.SetColumn(control, 1);
                if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            }
            grid.Children.Add(control);
        }
        if (_card.Children.Count > 0)
        {
            var line = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 0) };
            line.SetResourceReference(Border.BackgroundProperty, "S.Hairline");
            _card.Children.Add(line);
        }
        _card.Children.Add(grid);
    }

    private CheckBox Switch(bool value, Action<bool> apply)
    {
        var box = new CheckBox { IsChecked = value, Style = (Style)FindResource("Switch") };
        box.Checked += (_, _) => apply(true);
        box.Unchecked += (_, _) => apply(false);
        return box;
    }

    private void SwitchRow(string label, string? hint, bool value, Action<bool> apply) => Row(label, hint, Switch(value, apply));

    private StackPanel Segments(string[] items, int selected, Action<int> apply)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        string group = Guid.NewGuid().ToString("N");
        for (int i = 0; i < items.Length; i++)
        {
            int idx = i;
            var rb = new RadioButton { Content = items[i], GroupName = group, IsChecked = i == selected, Style = (Style)FindResource("Segment") };
            rb.Checked += (_, _) => { if (!_loading) apply(idx); };
            panel.Children.Add(rb);
        }
        return panel;
    }

    private UIElement Stepper(int value, int min, int max, int step, string suffix, Action<int> apply)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var val = new TextBlock { Width = 54, TextAlignment = TextAlignment.Center, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }
            .WithResource(TextBlock.ForegroundProperty, "S.Text");
        int cur = value;
        void Show() => val.Text = $"{cur}{suffix}";
        Show();
        Button Btn(string glyph, int delta)
        {
            var b = new Button { Content = glyph, Width = 30, Style = (Style)FindResource("Action"), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10 };
            b.Click += (_, _) => { cur = Math.Clamp(cur + delta, min, max); Show(); apply(cur); };
            return b;
        }
        panel.Children.Add(Btn("", -step));
        panel.Children.Add(val);
        panel.Children.Add(Btn("", step));
        return panel;
    }

    private Button ActionButton(string text, Action click, bool danger = false)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("Action"), Margin = new Thickness(6, 0, 0, 0) };
        if (danger) b.SetResourceReference(ForegroundProperty, "S.Warn");
        b.Click += (_, _) => click();
        return b;
    }

    private TextBox Field(string value, double width, Action<string> commit)
    {
        var box = new TextBox { Text = value, Width = width, Style = (Style)FindResource("Field") };
        box.LostKeyboardFocus += (_, _) => commit(box.Text.Trim());
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) commit(box.Text.Trim()); };
        return box;
    }

    // ---------------------------------------------------------------- pages

    private void Populate()
    {
        _loading = true;
        Root.Children.Clear();
        var s = Current;
        switch (_page)
        {
            case "general": PopulateGeneral(s); break;
            case "island": PopulateIsland(s); break;
            case "features": PopulateFeatures(s); break;
            case "appearance": PopulateAppearance(s); break;
            case "privacy": PopulatePrivacy(s); break;
            case "about": PopulateAbout(s); break;
        }
        _loading = false;
    }

    private void PopulateGeneral(SettingsModel s)
    {
        Card("Startup");
        SwitchRow("Start with Windows", "Starts quietly at sign-in; the island appears as usual.",
            StartupRegistration.IsEnabled(), v => { StartupRegistration.Apply(v); Set(x => x.EnableAtStartup = v); });

        Card("Keyboard shortcuts");
        HotkeyRow("Search", "Island search with Tab commands", s.HotkeySearch, (x, v) => x.HotkeySearch = v);
        HotkeyRow("Clipboard history", "Opens the island on the clipboard card", s.HotkeyClipboard, (x, v) => x.HotkeyClipboard = v);
        HotkeyRow("Toggle island", null, s.HotkeyToggle, (x, v) => x.HotkeyToggle = v);
        HotkeyRow("Media card", null, s.HotkeyMedia, (x, v) => x.HotkeyMedia = v);
        HotkeyRow("Timer", "Shows the running timer or starts 5 minutes", s.HotkeyTimer, (x, v) => x.HotkeyTimer = v);

        Card("Clock and units");
        Row("Clock", "System follows your Windows region format.",
            Segments(new[] { "System", "12-hour", "24-hour" }, (int)s.ClockFormat, i => Set(x => x.ClockFormat = (ClockFormat)i)));
        Row("Temperature", null,
            Segments(new[] { "°C", "°F" }, s.TemperatureFahrenheit ? 1 : 0, i => Set(x => x.TemperatureFahrenheit = i == 1)));
    }

    private void PopulateIsland(SettingsModel s)
    {
        Card("Rings");
        var (p1, p2) = IslandLayout.FromSettings(s.IslandPage1, s.IslandPage2);
        Row("Page 1", "Up to 6 rings, in this order.", PageEditor(p1, p2, 0), wrapBelow: true);
        Row("Page 2", "Scroll over the rings, click the dots or press Ctrl+Left/Right to switch pages.", PageEditor(p1, p2, 1), wrapBelow: true);

        Card("Warnings");
        Row("Memory", "Strip appears after 5 s at or above this level.",
            Stepper(s.WarnRamPercent, 50, 100, 5, "%", v => Set(x => x.WarnRamPercent = v)));
        Row("Processor", "Strip appears after 10 s at or above this level.",
            Stepper(s.WarnCpuPercent, 50, 100, 5, "%", v => Set(x => x.WarnCpuPercent = v)));
        Row("Battery", "On battery, at or below this level for 5 s.",
            Stepper(s.WarnBatteryPercent, 5, 50, 5, "%", v => Set(x => x.WarnBatteryPercent = v)));

        Card("Screen");
        var screens = new List<string> { "Primary", "With the mouse" };
        try { var mons = NativeMethods.GetMonitors(); for (int i = 0; i < mons.Count; i++) screens.Add($"Display {i + 1}"); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ExternalException) { Log.Debug("Monitor list failed", ("error", ex.Message)); }
        int sel = s.MonitorIndex switch { -1 => 0, -2 => 1, >= 0 => s.MonitorIndex + 2, _ => 0 };
        Row("Show the island on", null,
            Segments(screens.ToArray(), Math.Min(sel, screens.Count - 1), i => Set(x => x.MonitorIndex = i switch { 0 => -1, 1 => -2, _ => i - 2 })),
            wrapBelow: screens.Count > 3);
        SwitchRow("Hide over full-screen apps", "Games, videos and presentations. Incoming calls and the search hotkey still show.",
            s.AutoHideFullscreen, v => Set(x => x.AutoHideFullscreen = v));
    }

    /// <summary>Ordered ring list for one page: up / down / other page / remove, plus "add".</summary>
    private UIElement PageEditor(List<IslandMetric> p1, List<IslandMetric> p2, int page)
    {
        var list = page == 0 ? p1 : p2;
        var other = page == 0 ? p2 : p1;
        var panel = new StackPanel();
        void Save()
        {
            string a = IslandLayout.Format(p1), b = IslandLayout.Format(p2);
            Set(x => { x.IslandPage1 = a; x.IslandPage2 = b; });
            Dispatcher.BeginInvoke(Populate);
        }
        for (int i = 0; i < list.Count; i++)
        {
            int idx = i;
            var m = list[i];
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"{i + 1}.  {IslandLayout.DisplayName(m)}", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }
                .WithResource(TextBlock.ForegroundProperty, "S.Text"));
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            Button Icon(string glyph, string tip, bool enabled, Action act)
            {
                var b = new Button { Content = glyph, ToolTip = tip, IsEnabled = enabled, Width = 30, Margin = new Thickness(4, 0, 0, 0),
                    Style = (Style)FindResource("Action"), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11 };
                b.Click += (_, _) => { act(); Save(); };
                return b;
            }
            btns.Children.Add(Icon("", "Move up", idx > 0, () => (list[idx - 1], list[idx]) = (list[idx], list[idx - 1])));
            btns.Children.Add(Icon("", "Move down", idx < list.Count - 1, () => (list[idx + 1], list[idx]) = (list[idx], list[idx + 1])));
            btns.Children.Add(Icon(page == 0 ? "" : "", page == 0 ? "Move to page 2" : "Move to page 1", other.Count < 6,
                () => { list.RemoveAt(idx); other.Add(m); }));
            btns.Children.Add(Icon("", "Remove", true, () => list.RemoveAt(idx)));
            Grid.SetColumn(btns, 1);
            row.Children.Add(btns);
            panel.Children.Add(row);
        }
        var unused = IslandLayout.All.Where(m => !p1.Contains(m) && !p2.Contains(m)).ToList();
        if (unused.Count > 0 && list.Count < 6)
        {
            var add = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var m in unused)
            {
                var b = new Button { Content = "+ " + IslandLayout.DisplayName(m), Style = (Style)FindResource("Action"), Margin = new Thickness(0, 0, 6, 4) };
                b.Click += (_, _) => { list.Add(m); Save(); };
                add.Children.Add(b);
            }
            panel.Children.Add(add);
        }
        if (list.Count == 0)
            panel.Children.Add(new TextBlock { Text = "Empty", FontSize = 12 }.WithResource(TextBlock.ForegroundProperty, "S.Muted"));
        return panel;
    }

    private void PopulateFeatures(SettingsModel s)
    {
        Card(null);
        Row("Turn features on or off", "A feature that is off is not loaded at all: no timers, no hooks, no hotkey.", null);
        SwitchRow("Music", "Now playing, artwork, controls, lyrics", s.FeatureMedia, v => Set(x => x.FeatureMedia = v));
        SwitchRow("Idle dashboard", "Dot clock, greeting, focus button when nothing plays", s.FeatureIdleDashboard, v => Set(x => x.FeatureIdleDashboard = v));
        SwitchRow("Notifications", "Cards for incoming Windows notifications", s.FeatureNotifications, v => Set(x => x.FeatureNotifications = v));
        SwitchRow("Volume and brightness HUD", null, s.FeatureVolumeHud, v => Set(x => x.FeatureVolumeHud = v));
        SwitchRow("Search", "Island search bar and its hotkey", s.FeatureSearch, v => Set(x => x.FeatureSearch = v));
        SwitchRow("Mirror", "Camera mirror ring", s.FeatureMirror, v => Set(x => x.FeatureMirror = v));
        SwitchRow("Speed test", "Start button in the network card", s.FeatureSpeedTest, v => Set(x => x.FeatureSpeedTest = v));
        SwitchRow("Clipboard history", "Ring, card and hotkey", s.FeatureClipboardHistory, v => Set(x => x.FeatureClipboardHistory = v));
        SwitchRow("Quick toggles", "Wi-Fi, Bluetooth, mic, dark mode, do not disturb", s.FeatureToggles, v => Set(x => x.FeatureToggles = v));
        SwitchRow("Timer", "Timer ring, card, search command and hotkey", s.FeatureTimers, v => Set(x => x.FeatureTimers = v));
    }

    private static readonly string[] AccentPalette =
    {
        "#FF0A84FF", "#FFBF5AF2", "#FFFF375F", "#FF30D158", "#FFFF9F0A", "#FFFFD60A", "#FF64D2FF", "#FF98989D",
    };

    private void PopulateAppearance(SettingsModel s)
    {
        Card("Colour");
        Row("Accent colour", "Auto uses the album colour while music plays and the time of day otherwise.",
            Segments(new[] { "Auto", "Time of day", "Fixed" }, (int)s.AccentMode, i => { Set(x => x.AccentMode = (AccentMode)i); Dispatcher.BeginInvoke(Populate); }));
        if (s.AccentMode == AccentMode.Fixed)
        {
            var swatches = new WrapPanel();
            foreach (var hex in AccentPalette)
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var b = new Button { Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, ToolTip = hex,
                    Template = SwatchTemplate(c, string.Equals(hex, s.AccentColor, StringComparison.OrdinalIgnoreCase)) };
                b.Click += (_, _) => { Set(x => x.AccentColor = hex); Dispatcher.BeginInvoke(() => { ApplyPalette(); Populate(); }); };
                swatches.Children.Add(b);
            }
            var custom = Field(s.AccentColor, 100, v =>
            {
                string hex = v.StartsWith('#') ? v : "#" + v;
                if (hex.Length == 7) hex = "#FF" + hex[1..];
                try
                {
                    if (ColorConverter.ConvertFromString(hex) is Color) { Set(x => x.AccentColor = hex.ToUpperInvariant()); ApplyPalette(); }
                }
                catch (FormatException) { StatusText.Text = "Colour must look like #3399FF"; }
            });
            custom.ToolTip = "Custom colour (#RRGGBB)";
            swatches.Children.Add(custom);
            Row("Fixed colour", null, swatches, wrapBelow: true);
        }

        Card("Island");
        Row("Theme", null, Segments(new[] { "Dark", "Light", "Glass" }, (int)s.Theme, i => Set(x => x.Theme = (AppTheme)i)));
        var slider = new Slider { Minimum = 0.4, Maximum = 1.0, Value = s.Opacity, Width = 180, SmallChange = 0.02, LargeChange = 0.1 };
        var pct = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, Text = $"{Math.Round(s.Opacity * 100)}%", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }
            .WithResource(TextBlock.ForegroundProperty, "S.Muted");
        slider.ValueChanged += (_, e) => { pct.Text = $"{Math.Round(e.NewValue * 100)}%"; Set(x => x.Opacity = Math.Round(e.NewValue, 2)); };
        var op = new StackPanel { Orientation = Orientation.Horizontal };
        op.Children.Add(slider);
        op.Children.Add(pct);
        Row("Opacity", "How solid the island background is (100% = no transparency).", op);
        Row("Reduce motion", "System follows Windows > Accessibility > Visual effects > Animation effects.",
            Segments(new[] { "System", "Always", "Never" }, (int)s.ReduceMotionMode, i => Set(x => x.ReduceMotionMode = (ReduceMotionMode)i)));
    }

    private static ControlTemplate SwatchTemplate(Color c, bool selected)
    {
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        f.SetValue(Border.BackgroundProperty, new SolidColorBrush(c));
        f.SetValue(Border.BorderThicknessProperty, new Thickness(selected ? 3 : 0));
        f.SetResourceReference(Border.BorderBrushProperty, "S.Text");
        return new ControlTemplate(typeof(Button)) { VisualTree = f };
    }

    private void PopulatePrivacy(SettingsModel s)
    {
        Card("Clipboard history");
        SwitchRow("Remember history after restart", "Stored encrypted for your Windows account only (DPAPI). Off: kept in memory and gone when the app closes.",
            s.ClipboardPersist, v => Set(x => x.ClipboardPersist = v));
        Row("Items kept", "Pinned items are kept on top of this.",
            Stepper(s.ClipboardLimit, 1, 50, 1, "", v => Set(x => x.ClipboardLimit = v)));
        int[] hours = { 0, 1, 6, 24 };
        Row("Clear automatically", null,
            Segments(new[] { "Never", "1 hour", "6 hours", "24 hours" }, Math.Max(0, Array.IndexOf(hours, s.ClipboardAutoClearHours)),
                i => Set(x => x.ClipboardAutoClearHours = hours[i])));
        SwitchRow("Paste after choosing", "Choosing an item also pastes it into the window you were using.",
            s.ClipboardPasteOnSelect, v => Set(x => x.ClipboardPasteOnSelect = v));
        Row("Never record copies from", "Process names, comma separated. Password managers are also skipped by the formats they set.",
            Field(s.ClipboardIgnoredApps, 260, v => Set(x => x.ClipboardIgnoredApps = v)), wrapBelow: true);
        Row("Clipboard history", null, ActionButton("Clear history", () => { App.ClipHistory?.ForgetEverything(); StatusText.Text = "Clipboard history cleared"; }));

        Card("Camera");
        Row("Mirror", "The camera turns on only when you open the mirror and turns off as soon as it closes. Nothing is recorded or sent.", null);

        Card("Your data");
        Row("Everything stays on this PC", "No accounts, no analytics. Network use: weather, lyrics, speed test, and the update check you set up.", null);
        Row("Clear all data", "Clipboard history, timer and network counters, lyrics cache, search history and logs. Settings stay.",
            ActionButton("Clear all data", ClearAllData, danger: true));
    }

    private void ClearAllData()
    {
        if (MessageBox.Show(this, "Delete the clipboard history, cached data and logs?\nYour settings are kept.", "Clear all data",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        App.ClipHistory?.ForgetEverything();
        string dir = Path.GetDirectoryName(App.Settings?.SettingsPath ?? "") ?? "";
        foreach (var name in new[] { "state.json", "lyrics_cache.json", "search_stats.json", "clipboard.dat" })
        {
            string f = Path.Combine(dir, name);
            try { if (File.Exists(f)) File.Delete(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warning("Could not delete", ("file", name), ("error", ex.Message)); }
        }
        Log.ClearAll();
        StatusText.Text = "All data cleared";
    }

    private void PopulateAbout(SettingsModel s)
    {
        Card(null);
        Row($"{App.AppName} {App.InfoVersion}", "Local-only. No ads, no accounts.", null);
        Row("Updates", string.IsNullOrWhiteSpace(s.UpdateFeedUrl) ? "No update address is set (see below)." : s.UpdateFeedUrl,
            ActionButton("Check for updates", async () =>
            {
                StatusText.Text = "Checking…";
                StatusText.Text = await App.CheckForUpdatesAsync(interactive: true);
            }));
        Row("Update address", "Where releases are published (filled in by the packager).",
            Field(s.UpdateFeedUrl, 300, v => Set(x => x.UpdateFeedUrl = v)), wrapBelow: true);

        Card(null);
        Row("Logs", Log.LogDirectory, ActionButton("Open folder", () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Log.LogDirectory, UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception ex) { StatusText.Text = ex.Message; }
        }));
        Row("Diagnostics", "Version, Windows, monitors and service status (no personal data).",
            ActionButton("Copy", () =>
            {
                try { Clipboard.SetText(BuildDiagnosticsReport()); StatusText.Text = "Diagnostics copied"; }
                catch (ExternalException ex) { StatusText.Text = "Clipboard busy: " + ex.Message; }
            }));
        Row("First-time setup", "Walk through features, permissions and shortcuts again.",
            ActionButton("Run setup", () => FirstRunWindow.ShowSingleton()));
        Row("Reset all settings", "Back to the defaults. Your clipboard history and data are not touched.",
            ActionButton("Reset", ResetAll, danger: true));
    }

    internal static List<(string Label, string Value)> CollectDiagnostics()
    {
        string monitors;
        try { var mons = NativeMethods.GetMonitors(); monitors = mons.Count == 0 ? "none detected" : $"{mons.Count} detected"; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ExternalException) { monitors = "query failed"; }
        return new List<(string, string)>
        {
            ("App version", App.InfoVersion),
            ("OS", Environment.OSVersion.VersionString),
            (".NET runtime", Environment.Version.ToString()),
            ("Process arch", RuntimeInformation.ProcessArchitecture.ToString()),
            ("Monitors", monitors),
        };
    }

    internal static List<(string Label, string Value)> CollectServiceStatuses()
    {
        static string Ok(object? o) => o is not null ? "ready" : "off";
        return new List<(string, string)>
        {
            ("Volume", Ok(App.Volume)), ("Brightness", Ok(App.Brightness)), ("Media", Ok(App.Media)),
            ("Battery", Ok(App.Battery)), ("Network", Ok(App.Network)), ("Bluetooth", Ok(App.Bluetooth)),
            ("Timers", Ok(App.Timer)), ("System monitor", Ok(App.SysMon)), ("Toggles", Ok(App.Toggles)),
            ("Clipboard history", App.ClipHistory?.Enabled == true ? "ready" : "off"),
        };
    }

    internal static string BuildDiagnosticsReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{App.AppName} {App.InfoVersion}");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        foreach (var (l, v) in CollectDiagnostics()) sb.AppendLine($"{l}: {v}");
        sb.AppendLine();
        sb.AppendLine("Services:");
        foreach (var (l, v) in CollectServiceStatuses()) sb.AppendLine($"  {l}: {v}");
        sb.AppendLine();
        sb.AppendLine($"Settings: {App.Settings?.SettingsPath}");
        sb.AppendLine($"Logs: {Log.LogDirectory}");
        return sb.ToString();
    }

    private void ResetAll()
    {
        if (MessageBox.Show(this, "Reset every setting to its default value?", "Reset all settings",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _pending.Clear();
        App.Settings?.Save(new SettingsModel { FirstRunCompleted = true });
        StartupRegistration.Apply(new SettingsModel().EnableAtStartup);
        ApplyPalette();
        Populate();
        StatusText.Text = "Defaults restored";
    }

    // ---------------------------------------------------------------- hotkey recorder

    /// <summary>Click the box and press the keys. Esc cancels, Backspace turns the shortcut off.
    /// Conflicts: another action of this app, or a combination another app already owns.</summary>
    private void HotkeyRow(string label, string? hint, string value, Action<SettingsModel, string> apply)
    {
        var box = new TextBox { Text = value.Length == 0 ? "Off" : value, Width = 170, IsReadOnly = true, Cursor = Cursors.Hand,
            Style = (Style)FindResource("Field"), IsReadOnlyCaretVisible = false };
        var note = new TextBlock { FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 170 }
            .WithResource(TextBlock.ForegroundProperty, "S.Warn");
        string committed = value;
        box.GotKeyboardFocus += (_, _) => { App.Bar?.SetHotkeysSuspended(true); box.Text = "Press keys…"; note.Text = ""; };
        box.LostKeyboardFocus += (_, _) => { App.Bar?.SetHotkeysSuspended(false); box.Text = committed.Length == 0 ? "Off" : committed; };
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                return;
            if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
            if (key is Key.Back or Key.Delete)
            {
                committed = "";
                Set(x => apply(x, ""));
                note.Text = "";
                Keyboard.ClearFocus();
                return;
            }
            uint mods = 0;
            var m = Keyboard.Modifiers;
            if (m.HasFlag(ModifierKeys.Control)) mods |= Hotkeys.ModControl;
            if (m.HasFlag(ModifierKeys.Alt)) mods |= Hotkeys.ModAlt;
            if (m.HasFlag(ModifierKeys.Shift)) mods |= Hotkeys.ModShift;
            if (m.HasFlag(ModifierKeys.Windows)) mods |= Hotkeys.ModWin;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            string? combo = Hotkeys.Format(mods, vk);
            if (combo is null || mods == 0)
            {
                note.Text = "Use a modifier (Ctrl, Alt, Shift, Win) with a letter, digit, F-key or Space.";
                return;
            }
            var dupes = Hotkeys.DuplicatesOf(Current, label, combo);
            if (dupes.Count > 0) { note.Text = $"Already used for {string.Join(", ", dupes)}."; return; }
            if (!Hotkeys.Same(combo, committed) && !IsFree(mods, vk)) { note.Text = "Another app already uses this shortcut."; return; }
            committed = combo;
            Set(x => apply(x, combo));
            note.Text = "";
            Keyboard.ClearFocus();
        };
        var stack = new StackPanel();
        stack.Children.Add(box);
        stack.Children.Add(note);
        Row(label, hint, stack);
    }

    /// <summary>Trial registration on this window: succeeds only if no app owns the combination.</summary>
    private bool IsFree(uint mods, uint vk)
    {
        const int probeId = 0xBFFF;
        if (_hwnd == IntPtr.Zero) return true;
        if (!NativeMethods.RegisterHotKey(_hwnd, probeId, mods | NativeMethods.MOD_NOREPEAT, vk)) return false;
        NativeMethods.UnregisterHotKey(_hwnd, probeId);
        return true;
    }

    private static BitmapImage? LoadIcon(int size)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (!File.Exists(path)) return null;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri(path);
        bi.DecodePixelWidth = size;
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}

internal static class SettingsUiExtensions
{
    public static T WithResource<T>(this T element, DependencyProperty dp, string key) where T : FrameworkElement
    {
        element.SetResourceReference(dp, key);
        return element;
    }
}

/// <summary>One-shot debounce timer without a Dispatcher reference at construction.</summary>
internal sealed class DispatcherTimerSink
{
    private readonly System.Windows.Threading.DispatcherTimer _t;
    public event Action? Fired;
    public DispatcherTimerSink()
    {
        _t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(350) };
        _t.Tick += (_, _) => { _t.Stop(); Fired?.Invoke(); };
    }
    public void Kick(TimeSpan delay) { _t.Interval = delay; _t.Stop(); _t.Start(); }
}
