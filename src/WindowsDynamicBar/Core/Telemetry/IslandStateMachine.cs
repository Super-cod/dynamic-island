namespace WindowsDynamicBar.Core.Telemetry;

/// <summary>Visual state of the dynamic island. Collapsed = pill; Expanded = hover; Detail = metric panel; Search = search bar.</summary>
public enum IslandVisualState { Collapsed, Expanded, Detail, Search }

/// <summary>Which telemetry metric the detail panel shows.</summary>
public enum IslandMetric { None, Cpu, Gpu, Ram, Mirror, Net, Weather, Clipboard, Toggles, Timer }

/// <summary>
/// Pure island transition logic (no WPF dependency) so it can be unit-tested and so that
/// exactly one method in the UI owns the state.
/// </summary>
public static class IslandStateMachine
{
    /// <param name="current">Current state.</param>
    /// <param name="pointerInside">True while the pointer is anywhere in the island hover region.</param>
    /// <param name="metricUnderPointer">Metric whose icon is hovered, or None.</param>
    /// <param name="collapseElapsed">True once the leave-delay has elapsed (pointer has left the region).</param>
    /// <param name="hoverPreview">True if hover preview is enabled; false if click mode is active.</param>
    public static (IslandVisualState State, IslandMetric Metric) Resolve(
        IslandVisualState current, IslandMetric currentMetric,
        bool pointerInside, IslandMetric metricUnderPointer, bool collapseElapsed,
        bool hoverPreview = true)
    {
        if (!pointerInside)
        {
            // Leaving the whole region: only collapse once the grace delay has elapsed.
            if (collapseElapsed) return (IslandVisualState.Collapsed, IslandMetric.None);
            return (current, current is IslandVisualState.Detail ? currentMetric : IslandMetric.None);
        }

        if (metricUnderPointer != IslandMetric.None && hoverPreview)
        {
            // Hovering an icon opens the detail panel only when button hover preview is enabled.
            return (IslandVisualState.Detail, metricUnderPointer);
        }

        // Inside the region: while a panel is open, keep the panel open.
        if (current == IslandVisualState.Detail)
            return (IslandVisualState.Detail, currentMetric);

        // Hovering inside the dynamic island expands it to show the icon row.
        return (IslandVisualState.Expanded, IslandMetric.None);
    }

    public static string MetricKey(IslandMetric m) => m switch
    {
        IslandMetric.Cpu => "cpu",
        IslandMetric.Gpu => "gpu",
        IslandMetric.Ram => "ram",
        IslandMetric.Mirror => "mir",
        IslandMetric.Net => "net",
        IslandMetric.Weather => "wx",
        IslandMetric.Clipboard => "clip",
        IslandMetric.Toggles => "tog",
        IslandMetric.Timer => "tmr",
        _ => "",
    };

    public static IslandMetric ParseMetric(string? key) => key switch
    {
        "cpu" => IslandMetric.Cpu,
        "gpu" => IslandMetric.Gpu,
        "ram" => IslandMetric.Ram,
        "mir" => IslandMetric.Mirror,
        "net" => IslandMetric.Net,
        "wx" => IslandMetric.Weather,
        "clip" => IslandMetric.Clipboard,
        "tog" => IslandMetric.Toggles,
        "tmr" => IslandMetric.Timer,
        _ => IslandMetric.None,
    };
}
