namespace WindowsDynamicBar.Core.Telemetry;

/// <summary>Visual state of the dynamic island. Collapsed = pill; Expanded = hover; Detail = metric panel.</summary>
public enum IslandVisualState { Collapsed, Expanded, Detail }

/// <summary>Which telemetry metric the detail panel shows.</summary>
public enum IslandMetric { None, Cpu, Gpu, Ram, Mirror, Net, Weather }

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
    public static (IslandVisualState State, IslandMetric Metric) Resolve(
        IslandVisualState current, IslandMetric currentMetric,
        bool pointerInside, IslandMetric metricUnderPointer, bool collapseElapsed)
    {
        if (!pointerInside)
        {
            // Leaving the whole region: only collapse once the grace delay has elapsed.
            if (collapseElapsed) return (IslandVisualState.Collapsed, IslandMetric.None);
            return (current, current is IslandVisualState.Detail ? currentMetric : IslandMetric.None);
        }

        if (metricUnderPointer != IslandMetric.None)
        {
            // Hovering an icon always opens (or switches) the detail panel for that metric.
            return (IslandVisualState.Detail, metricUnderPointer);
        }

        // Inside the region but not on an icon: while a panel is open the pointer may be over
        // the graph/stats — keep the panel open (moving icon -> panel must not close it).
        if (current == IslandVisualState.Detail)
            return (IslandVisualState.Detail, currentMetric);

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
        _ => IslandMetric.None,
    };
}
