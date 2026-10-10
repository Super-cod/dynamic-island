using System.Globalization;
using System.Windows.Media;

namespace WindowsDynamicBar.Core.Idle;

/// <summary>Time-of-day period: drives the idle island's accent hue.</summary>
public readonly record struct DayPeriod(string Name, double Hue);

/// <summary>
/// Pure helpers for the idle (no media session) island: time-of-day colour, greeting,
/// day progress and the human "in 25 min" strings. No UI, no clock reads - callers pass time in.
/// </summary>
public static class IdleClock
{
    public static readonly DayPeriod Dawn = new("dawn", 22);
    public static readonly DayPeriod Day = new("day", 200);
    public static readonly DayPeriod Dusk = new("dusk", 285);
    public static readonly DayPeriod Night = new("night", 238);

    /// <summary>5-8 dawn, 8-17 day, 17-20 dusk, 20-5 night.</summary>
    public static DayPeriod PeriodFor(int hour) => hour switch
    {
        >= 5 and < 8 => Dawn,
        >= 8 and < 17 => Day,
        >= 17 and < 20 => Dusk,
        _ => Night,
    };

    public static string Greeting(int hour) => hour switch
    {
        < 5 => "Still up?",
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        < 21 => "Good evening",
        _ => "Good night",
    };

    /// <summary>Fraction (0..1) of the local day that has passed.</summary>
    public static double DayFraction(DateTime now) => Math.Clamp(now.TimeOfDay.TotalSeconds / 86400.0, 0, 1);

    /// <summary>Accent colour for a hue: hsl(H 70% 62%).</summary>
    public static Color Accent(double hue) => Hsl(hue, 0.70, 0.62);

    /// <summary>Signed shortest angular distance from <paramref name="from"/> to <paramref name="to"/> (-180..180].</summary>
    public static double HueDelta(double from, double to)
    {
        double d = (to - from) % 360;
        if (d > 180) d -= 360;
        if (d <= -180) d += 360;
        return d;
    }

    public static double NormalizeHue(double h) => ((h % 360) + 360) % 360;

    public static Color Hsl(double h, double s, double l, double a = 1.0)
    {
        h = NormalizeHue(h) / 360.0;
        double r, g, b;
        if (s <= 0) r = g = b = l;
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3);
        }
        return Color.FromArgb(ToByte(a), ToByte(r), ToByte(g), ToByte(b));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    /// <summary>True when the culture's short time pattern is 12-hour (h, not H).</summary>
    public static bool Uses12Hour(CultureInfo culture)
    {
        string p = culture.DateTimeFormat.ShortTimePattern;
        return p.Contains('h') && !p.Contains('H');
    }

    /// <summary>"HH:mm" or "h:mm" per the system clock setting (no AM/PM - the pill is compact).</summary>
    public static string FormatTime(DateTime now, bool twelveHour) =>
        twelveHour ? now.ToString("h:mm", CultureInfo.InvariantCulture) : now.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"25 min", "1h 5m", "2h", or "now" for under a minute.</summary>
    public static string FormatUntil(TimeSpan until)
    {
        int mins = (int)Math.Ceiling(Math.Max(0, until.TotalMinutes));
        if (until.TotalSeconds < 60) return "now";
        if (mins < 60) return $"{mins} min";
        int h = mins / 60, m = mins % 60;
        return m == 0 ? $"{h}h" : $"{h}h {m}m";
    }

    /// <summary>"in 25 min" / "in 1h 5m" / "now".</summary>
    public static string FormatIn(TimeSpan until)
    {
        string s = FormatUntil(until);
        return s == "now" ? "now" : $"in {s}";
    }

    /// <summary>Countdown text for the focus button / chip: "24:31".</summary>
    public static string FormatCountdown(TimeSpan t)
    {
        int secs = (int)Math.Ceiling(Math.Max(0, t.TotalSeconds));
        return $"{secs / 60}:{secs % 60:00}";
    }
}

/// <summary>
/// 5x7 dot font for the idle dot-matrix clock. Layout of "HH:mm": each digit is 5 columns + 1
/// spacing column; the colon is 1 column + 2 spacing columns with dots at rows 2 and 4.
/// Total width: 6 + 6 + 3 + 6 + 6 = 27 columns.
/// </summary>
public static class DotFont
{
    public const int Rows = 7;
    public const int Columns = 27;
    public const int ColonColumn = 12;

    // Rows top->bottom, 5 bits each (MSB = leftmost column).
    private static readonly byte[][] Digits =
    {
        new byte[] { 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110 }, // 0
        new byte[] { 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 }, // 1
        new byte[] { 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111 }, // 2
        new byte[] { 0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110 }, // 3
        new byte[] { 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010 }, // 4
        new byte[] { 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110 }, // 5
        new byte[] { 0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110 }, // 6
        new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000 }, // 7
        new byte[] { 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110 }, // 8
        new byte[] { 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100 }, // 9
    };

    private static readonly int[] DigitStart = { 0, 6, 15, 21 };

    /// <summary>
    /// Render "HH:mm" (or "H:mm") into a [row, column] lit map. A missing leading digit
    /// (12-hour clock before 10) leaves its cells unlit so the clock never shifts sideways.
    /// </summary>
    public static bool[,] Render(string hhmm, bool colonOn)
    {
        var map = new bool[Rows, Columns];
        string[] parts = hhmm.Split(':');
        string h = parts.Length > 0 ? parts[0] : "";
        string m = parts.Length > 1 ? parts[1] : "";
        h = h.Length >= 2 ? h[^2..] : h.PadLeft(2, ' ');
        m = m.Length >= 2 ? m[^2..] : m.PadLeft(2, '0');
        char[] chars = { h[0], h[1], m[0], m[1] };

        for (int i = 0; i < 4; i++)
        {
            if (chars[i] < '0' || chars[i] > '9') continue;
            byte[] glyph = Digits[chars[i] - '0'];
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < 5; c++)
                    map[r, DigitStart[i] + c] = (glyph[r] & (1 << (4 - c))) != 0;
        }
        if (colonOn)
        {
            map[2, ColonColumn] = true;
            map[4, ColonColumn] = true;
        }
        return map;
    }
}
