using System.Windows.Media;

namespace WindowsDynamicBar.Core.Telemetry;

/// <summary>Which rings appear on which page of the open island, and their fixed labels/colours.
/// Pages are stored in settings as comma-separated keys ("cpu,gpu,ram,mir,net,wx").</summary>
public static class IslandLayout
{
    public static readonly IslandMetric[] DefaultPage1 =
        { IslandMetric.Cpu, IslandMetric.Gpu, IslandMetric.Ram, IslandMetric.Mirror, IslandMetric.Net, IslandMetric.Weather };

    public static readonly IslandMetric[] DefaultPage2 =
        { IslandMetric.Clipboard, IslandMetric.Toggles, IslandMetric.Timer };

    /// <summary>Every metric that can be placed on a page.</summary>
    public static readonly IslandMetric[] All =
    {
        IslandMetric.Cpu, IslandMetric.Gpu, IslandMetric.Ram, IslandMetric.Mirror, IslandMetric.Net,
        IslandMetric.Weather, IslandMetric.Clipboard, IslandMetric.Toggles, IslandMetric.Timer,
    };

    public const string DefaultPage1Keys = "cpu,gpu,ram,mir,net,wx";
    public const string DefaultPage2Keys = "clip,tog,tmr";

    public static string Label(IslandMetric m) => m switch
    {
        IslandMetric.Cpu => "CPU",
        IslandMetric.Gpu => "GPU",
        IslandMetric.Ram => "RAM",
        IslandMetric.Mirror => "Mirror",
        IslandMetric.Net => "NET",
        IslandMetric.Weather => "WX",
        IslandMetric.Clipboard => "Clip",
        IslandMetric.Toggles => "Toggle",
        IslandMetric.Timer => "Timer",
        _ => "",
    };

    /// <summary>Human name for settings lists.</summary>
    public static string DisplayName(IslandMetric m) => m switch
    {
        IslandMetric.Cpu => "CPU",
        IslandMetric.Gpu => "GPU",
        IslandMetric.Ram => "Memory",
        IslandMetric.Mirror => "Mirror (camera)",
        IslandMetric.Net => "Network",
        IslandMetric.Weather => "Weather",
        IslandMetric.Clipboard => "Clipboard history",
        IslandMetric.Toggles => "Quick toggles",
        IslandMetric.Timer => "Timer",
        _ => m.ToString(),
    };

    /// <summary>Rings with an identity colour (the others colour by value).</summary>
    public static Color? FixedRingColor(IslandMetric m) => m switch
    {
        IslandMetric.Mirror => Color.FromRgb(0xE5, 0x48, 0x4D),
        IslandMetric.Net => Color.FromRgb(0x6F, 0xA8, 0xDC),
        IslandMetric.Weather => Color.FromRgb(0xE8, 0xB0, 0x4B),
        IslandMetric.Clipboard => Color.FromRgb(0x8A, 0xB4, 0x78),
        IslandMetric.Timer => Color.FromRgb(0xE8, 0xB0, 0x4B),
        _ => null,
    };

    /// <summary>Parse "cpu,gpu,..." (unknown keys and duplicates dropped, max 6).</summary>
    public static List<IslandMetric> Parse(string? keys)
    {
        var list = new List<IslandMetric>();
        if (string.IsNullOrWhiteSpace(keys)) return list;
        foreach (var k in keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = IslandStateMachine.ParseMetric(k.ToLowerInvariant());
            if (m != IslandMetric.None && !list.Contains(m) && list.Count < 6) list.Add(m);
        }
        return list;
    }

    public static string Format(IEnumerable<IslandMetric> metrics) =>
        string.Join(",", metrics.Select(IslandStateMachine.MetricKey).Where(k => k.Length > 0));

    /// <summary>Settings -> two pages; a metric on page 1 is removed from page 2; an empty
    /// layout falls back to the defaults so the island never ends up without rings.</summary>
    public static (List<IslandMetric> Page1, List<IslandMetric> Page2) FromSettings(string? page1, string? page2)
    {
        var p1 = Parse(page1);
        var p2 = Parse(page2).Where(m => !p1.Contains(m)).ToList();
        if (p1.Count == 0 && p2.Count == 0) return (DefaultPage1.ToList(), DefaultPage2.ToList());
        return (p1, p2);
    }
}
