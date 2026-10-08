namespace WindowsDynamicBar.Core.Telemetry;

public enum TelemetryTarget { None, Pill, Rail, Cpu, Gpu, Ram }

public enum TelemetryZoneState { Hidden, Rail, Cpu, Gpu, Ram }

public enum TelemetryAction { None, OpenRail, OpenMetric, CloseMetricToRail, HideAll }

public static class TelemetryPolicy
{
    public const int OpenRailMs = 130;
    public const int OpenMetricMs = 120;
    public const int CloseMetricMs = 200;
    public const int CloseRailMs = 180;

    public static TelemetryZoneState Desired(TelemetryTarget target) => target switch
    {
        TelemetryTarget.None => TelemetryZoneState.Hidden,
        TelemetryTarget.Pill or TelemetryTarget.Rail => TelemetryZoneState.Rail,
        TelemetryTarget.Cpu => TelemetryZoneState.Cpu,
        TelemetryTarget.Gpu => TelemetryZoneState.Gpu,
        TelemetryTarget.Ram => TelemetryZoneState.Ram,
        _ => TelemetryZoneState.Hidden,
    };

    public static TelemetryAction Plan(TelemetryZoneState current, TelemetryTarget target)
    {
        var desired = Desired(target);
        if (desired == current) return TelemetryAction.None;
        if (current == TelemetryZoneState.Hidden) return desired == TelemetryZoneState.Rail ? TelemetryAction.OpenRail : TelemetryAction.OpenMetric;
        if (desired == TelemetryZoneState.Hidden) return current == TelemetryZoneState.Rail ? TelemetryAction.HideAll : TelemetryAction.CloseMetricToRail;
        if (current == TelemetryZoneState.Rail) return TelemetryAction.OpenMetric;
        if (desired == TelemetryZoneState.Rail) return TelemetryAction.CloseMetricToRail;
        return TelemetryAction.OpenMetric;
    }
}