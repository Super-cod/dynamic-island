using System.Globalization;
using System.Text.RegularExpressions;

namespace WindowsDynamicBar.Core.Timers;

/// <summary>Parses durations typed by the user: "timer 10 min", "timer 90s", "1h 30m", "25", "1:30".</summary>
public static partial class TimerCommand
{
    public static readonly TimeSpan Max = TimeSpan.FromHours(24);

    /// <summary>"timer &lt;duration&gt;" in the search bar. Returns false for anything else.</summary>
    public static bool TryParseSearch(string? query, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(query)) return false;
        string q = query.Trim();
        if (!q.StartsWith("timer", StringComparison.OrdinalIgnoreCase)) return false;
        string rest = q[5..].Trim();
        return rest.Length > 0 && TryParseDuration(rest, out duration);
    }

    /// <summary>A duration on its own. A bare number means minutes; "m:ss" means minutes:seconds.</summary>
    public static bool TryParseDuration(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim().ToLowerInvariant();

        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double bare))
            return Accept(TimeSpan.FromMinutes(bare), out duration);

        var colon = ColonRegex().Match(t);
        if (colon.Success)
        {
            int a = int.Parse(colon.Groups[1].Value, CultureInfo.InvariantCulture);
            int b = int.Parse(colon.Groups[2].Value, CultureInfo.InvariantCulture);
            return b < 60 && Accept(TimeSpan.FromMinutes(a) + TimeSpan.FromSeconds(b), out duration);
        }

        var parts = UnitRegex().Matches(t);
        if (parts.Count == 0) return false;
        // Every character must belong to a "<number><unit>" part (rejects "10 min please").
        int covered = 0;
        foreach (Match m in parts) covered += m.Length;
        if (covered != t.Length) return false;   // the pattern also consumes the spaces around each part

        TimeSpan total = TimeSpan.Zero;
        foreach (Match m in parts)
        {
            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string unit = m.Groups[2].Value;
            total += unit[0] switch
            {
                'h' => TimeSpan.FromHours(n),
                's' => TimeSpan.FromSeconds(n),
                _ => TimeSpan.FromMinutes(n),
            };
        }
        return Accept(total, out duration);
    }

    private static bool Accept(TimeSpan d, out TimeSpan duration)
    {
        duration = d;
        return d >= TimeSpan.FromSeconds(1) && d <= Max;
    }

    /// <summary>"10:00" for under an hour, "1:30:00" above.</summary>
    public static string Format(TimeSpan d) => d.TotalHours >= 1
        ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}"
        : $"{(int)d.TotalMinutes:D2}:{d.Seconds:D2}";

    /// <summary>Text for the dot-matrix countdown (4 digits + colon): "mm:ss", or "h:mm" from 1 hour.</summary>
    public static string DotText(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var r = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        return r.TotalHours >= 1 ? $"{(int)r.TotalHours}:{r.Minutes:D2}" : $"{(int)r.TotalMinutes:D2}:{r.Seconds:D2}";
    }

    [GeneratedRegex(@"^(\d{1,3}):(\d{1,2})$")]
    private static partial Regex ColonRegex();

    // "(?![a-z])" instead of "\b" so "1h30m" (unit followed by a digit) also parses.
    [GeneratedRegex(@"\s*(\d+(?:\.\d+)?)\s*(hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)(?![a-z])\s*")]
    private static partial Regex UnitRegex();
}
