using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WindowsDynamicBar.Services.Weather;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Live weather card: a 24h temperature line/area graph with an inset rain strip, a five-hour
/// forecast strip and a stats column. Same footprint as the net card. Every value is real (IP
/// geolocation + Open-Meteo) and is pushed in by the host through <see cref="Render"/>; missing
/// values render as "—". Owns no timers.
/// </summary>
public partial class WeatherDetailPanel : UserControl
{
    private const string Mono = "Cascadia Mono, Consolas, Courier New";
    private const string SymbolFont = "Segoe UI Symbol";

    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x9A, 0xA0, 0x88));
    private static readonly Brush ValueBrush = Frozen(Color.FromRgb(0xE8, 0xE8, 0xDC));
    private static readonly Brush NameBrush = Frozen(Color.FromRgb(0xE6, 0xE0, 0xA8));
    private static readonly Brush MetricBrush = Frozen(Color.FromRgb(0x8A, 0xB4, 0x78));
    private static readonly Brush RainBrush = Frozen(Color.FromRgb(0x4F, 0xC3, 0xD9));
    private static readonly Brush SunBrush = Frozen(Color.FromRgb(0xE8, 0xB0, 0x4B));
    private static readonly Brush MoonBrush = Frozen(Color.FromRgb(0x9A, 0xA0, 0xC8));
    private static readonly Brush HumBrush = Frozen(Color.FromRgb(0x6F, 0xA8, 0xDC));

    private sealed record Tile(TextBlock Hour, TextBlock Glyph, TextBlock Temp);
    private readonly List<Tile> _tiles = new();

    private DotBar _humBar = null!, _rainBar = null!, _uvBar = null!, _aqiBar = null!;
    private TextBlock _humVal = null!, _rainVal = null!, _uvVal = null!, _windVal = null!, _aqiVal = null!;

    public WeatherDetailPanel()
    {
        InitializeComponent();
        TitleText.Foreground = NameBrush;
        CityText.Cursor = Cursors.Hand;
        CityText.ToolTip = "Click to configure weather location in Settings";
        CityText.MouseLeftButtonUp += (_, _) => SettingsWindow.ShowSingleton("widgets");
        BuildTiles();
        BuildStats();
    }

    public bool ReduceMotion { get; set; }

    // ------------------------------------------------------------------ build

    private void BuildTiles()
    {
        for (int i = 0; i < 5; i++)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var hour = new TextBlock
            {
                Text = "", FontFamily = new FontFamily(Mono), FontSize = 9,
                Foreground = LabelBrush, HorizontalAlignment = HorizontalAlignment.Center,
            };
            var glyph = new TextBlock
            {
                Text = "", FontFamily = new FontFamily(SymbolFont), FontSize = 13,
                Foreground = ValueBrush, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            };
            var temp = new TextBlock
            {
                Text = "", FontFamily = new FontFamily(Mono), FontSize = 10,
                Foreground = ValueBrush, HorizontalAlignment = HorizontalAlignment.Center,
            };
            sp.Children.Add(hour);
            sp.Children.Add(glyph);
            sp.Children.Add(temp);
            TilesHost.Children.Add(sp);
            _tiles.Add(new Tile(hour, glyph, temp));
        }
    }

    private void BuildStats()
    {
        (_humBar, _humVal) = AddBarRow("hum", HumBrush);
        (_rainBar, _rainVal) = AddBarRow("rain", RainBrush);
        (_uvBar, _uvVal) = AddBarRow("uv", SunBrush);
        _windVal = AddRow("wind");
        (_aqiBar, _aqiVal) = AddBarRow("aqi", MetricBrush);
    }

    private TextBlock AddRow(string label)
    {
        var grid = RowGrid(label);
        var val = ValueText();
        Grid.SetColumn(val, 2);
        grid.Children.Add(val);
        StatsHost.Children.Add(grid);
        return val;
    }

    private (DotBar Bar, TextBlock Value) AddBarRow(string label, Brush fill)
    {
        var grid = RowGrid(label);
        var bar = new DotBar
        {
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Value = -1,
            FillColor = ((SolidColorBrush)fill).Color,
            Margin = new Thickness(0, 0, 5, 0),
        };
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);
        var val = ValueText();
        Grid.SetColumn(val, 2);
        grid.Children.Add(val);
        StatsHost.Children.Add(grid);
        return (bar, val);
    }

    private static Grid RowGrid(string label)
    {
        var grid = new Grid { Height = 11, Margin = new Thickness(0, 0, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lbl = new TextBlock
        {
            Text = label, FontFamily = new FontFamily(Mono), FontSize = 10,
            Foreground = LabelBrush, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);
        return grid;
    }

    private static TextBlock ValueText() => new()
    {
        Text = "", FontFamily = new FontFamily(Mono), FontSize = 10, Foreground = ValueBrush,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
    };

    // ---------------------------------------------------------------- render

    public void Render(WeatherSnapshot s)
    {
        SetTitle(s);

        if (!s.Available)
        {
            Graph.Clear();
            RangeText.Text = "";
            LblNow.Text = "offline";
            LblFeels.Text = "";
            CityText.Text = s.LocationName ?? "";
            TempText.Text = "";
            CondText.Text = "offline";
            LblSun.Text = "";
            LblUv.Text = "";
            foreach (var t in _tiles) ShowTile(t, false);
            HideRow(_humVal); HideRow(_rainVal); HideRow(_uvVal); HideRow(_windVal); HideRow(_aqiVal);
            return;
        }

        CityText.Text = s.LocationName ?? "";
        TempText.Text = $"{Math.Round(s.TempC):F0}\u00b0";
        CondText.Text = s.Condition;

        int now = s.NowIndex;
        int n = Math.Clamp(s.Hours.Length - now, 0, 24);
        var temps = new double[n];
        var rain = new double[n];
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            temps[i] = s.Hours[now + i].TempC;
            rain[i] = s.Hours[now + i].PrecipProb;
            lo = Math.Min(lo, temps[i]);
            hi = Math.Max(hi, temps[i]);
        }
        Graph.SetData(temps, rain, 0);

        RangeText.Text = n > 0 ? $"{Math.Round(lo)}\u00b0/{Math.Round(hi)}\u00b0" : "";
        string arrow = n > 1 && temps[1] - temps[0] > 0.5 ? "\u2191"
            : n > 1 && temps[1] - temps[0] < -0.5 ? "\u2193" : "\u2192";
        LblNow.Text = $"now {Math.Round(s.TempC)}\u00b0 {arrow}";
        LblFeels.Text = $"feels {Math.Round(s.ApparentC)}\u00b0  \u00b7  {Math.Round(s.Humidity)}% rh";

        for (int i = 0; i < _tiles.Count; i++)
        {
            int idx = now + i;
            if (idx < 0 || idx >= s.Hours.Length)
            {
                ShowTile(_tiles[i], false);
                continue;
            }
            ShowTile(_tiles[i], true);
            var h = s.Hours[idx];
            var kind = WeatherSnapshot.KindOf(h.WeatherCode, IsDay(s, h.Time));
            _tiles[i].Hour.Text = i == 0 ? "Now" : HourLabel(h.Time);
            _tiles[i].Glyph.Text = GlyphOf(kind);
            _tiles[i].Glyph.Foreground = KindBrush(kind);
            _tiles[i].Temp.Text = $"{Math.Round(h.TempC)}\u00b0";
        }

        string sunGlyph = s.IsDay ? "\u2600" : "\u263E";
        LblSun.Text = s.Sunrise != DateTime.MinValue && s.Sunset != DateTime.MinValue
            ? $"{sunGlyph} \u2191{s.Sunrise:HH:mm}  \u2193{s.Sunset:HH:mm}"
            : $"{GlyphOf(s.Kind)} {s.Condition.ToLowerInvariant()}";
        LblUv.Text = $"upd {s.UpdatedAt:HH:mm}";

        ShowRow(_humVal); ShowRow(_rainVal); ShowRow(_uvVal); ShowRow(_windVal);
        SetBar(_humBar, _humVal, s.Humidity / 100.0, $"{Math.Round(s.Humidity):F0}%");
        SetBar(_rainBar, _rainVal, s.CurrentPrecipProb / 100.0, $"{Math.Round(s.CurrentPrecipProb)}%");

        double uv = s.CurrentUvIndex;
        _uvBar.FillColor = ((SolidColorBrush)UvBrush(uv)).Color;
        SetBar(_uvBar, _uvVal, Math.Clamp(uv / 11.0, 0, 1), uv.ToString("0.#", CultureInfo.InvariantCulture));

        _windVal.Text = $"{WindDir(s.WindDirDeg)} {Math.Round(s.WindKmh):F0} km/h";

        if (s.UsAqi is double aqi)
        {
            ShowRow(_aqiVal);
            _aqiBar.FillColor = ((SolidColorBrush)AqiBrush(aqi)).Color;
            SetBar(_aqiBar, _aqiVal, Math.Clamp(aqi / 300.0, 0, 1), $"{Math.Round(aqi)}");
        }
        else HideRow(_aqiVal);
    }

    // ---------------------------------------------------------------- helpers

    private void SetTitle(WeatherSnapshot s)
    {
        string cond = s.Available ? s.Condition.ToLowerInvariant() : "offline";
        TitleText.Inlines.Clear();
        TitleText.Inlines.Add(Run("\u2524", NameBrush));
        TitleText.Inlines.Add(Run("wx", MetricBrush));
        TitleText.Inlines.Add(Run($"\u251C\u2524{cond}\u251C", NameBrush));
        if (s.Available) TitleText.Inlines.Add(Run($"\u2524{Math.Round(s.TempC):F0}\u00b0\u251C", NameBrush));
    }

    // Rows/tiles without data are hidden but keep their space (no layout jump when data arrives).
    private static void HideRow(FrameworkElement value) => ((FrameworkElement)value.Parent).Visibility = Visibility.Hidden;
    private static void ShowRow(FrameworkElement value) => ((FrameworkElement)value.Parent).Visibility = Visibility.Visible;

    private static void ShowTile(Tile t, bool show)
    {
        if (t.Hour.Parent is FrameworkElement host) host.Visibility = show ? Visibility.Visible : Visibility.Hidden;
    }

    private static bool IsDay(WeatherSnapshot s, DateTime time)
        => s.Sunrise != DateTime.MinValue && s.Sunset != DateTime.MinValue
            ? time >= s.Sunrise && time <= s.Sunset
            : s.IsDay;

    private static string HourLabel(DateTime t)
    {
        int h = t.Hour % 12;
        if (h == 0) h = 12;
        return h + (t.Hour < 12 ? "a" : "p");
    }

    private static string WindDir(double deg)
    {
        string[] dirs = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        int idx = (int)Math.Round(deg / 45.0) % 8;
        return dirs[(idx + 8) % 8];
    }

    private static string GlyphOf(WeatherKind kind) => kind switch
    {
        WeatherKind.Sun => "\u2600",
        WeatherKind.Moon => "\u263E",
        WeatherKind.Partly => "\u26C5",
        WeatherKind.Rain => "\u2614",
        WeatherKind.Snow => "\u2744",
        WeatherKind.Fog => "\u2601",
        WeatherKind.Storm => "\u26A1",
        _ => "\u2601",
    };

    private static Brush KindBrush(WeatherKind kind) => kind switch
    {
        WeatherKind.Sun => SunBrush,
        WeatherKind.Moon => MoonBrush,
        WeatherKind.Partly => Frozen(Color.FromRgb(0xB8, 0xC4, 0xB0)),
        WeatherKind.Rain => Frozen(Color.FromRgb(0x6F, 0xA8, 0xDC)),
        WeatherKind.Snow => Frozen(Color.FromRgb(0xCF, 0xE3, 0xEE)),
        WeatherKind.Fog => LabelBrush,
        _ => SunBrush,
    };

    private static Brush UvBrush(double uv)
    {
        Color c = uv <= 2 ? Color.FromRgb(0x8A, 0xB4, 0x78)
            : uv <= 5 ? Color.FromRgb(0xE8, 0xB0, 0x4B)
            : uv <= 7 ? Color.FromRgb(0xD9, 0x8C, 0x4B)
            : uv <= 10 ? Color.FromRgb(0xC9, 0x60, 0x4A)
            : Color.FromRgb(0xB0, 0x7C, 0xE0);
        return Frozen(c);
    }

    private static Brush AqiBrush(double aqi)
    {
        Color c = aqi <= 50 ? Color.FromRgb(0x8A, 0xB4, 0x78)
            : aqi <= 100 ? Color.FromRgb(0xE8, 0xB0, 0x4B)
            : aqi <= 150 ? Color.FromRgb(0xD9, 0x8C, 0x4B)
            : aqi <= 200 ? Color.FromRgb(0xC9, 0x60, 0x4A)
            : aqi <= 300 ? Color.FromRgb(0xB0, 0x7C, 0xE0)
            : Color.FromRgb(0x8A, 0x3A, 0x5A);
        return Frozen(c);
    }

    private static void SetBar(DotBar bar, TextBlock val, double frac, string text)
    {
        bar.Value = frac < 0 ? -1 : Math.Clamp(frac, 0, 1);
        val.Text = text;
    }

    private static Run Run(string text, Brush brush) => new(text) { Foreground = brush };

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
