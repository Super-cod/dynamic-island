namespace WindowsDynamicBar.Core.Telemetry;

public static class TelemetryFormat
{
    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double value = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit <= 1 ? $"{value:F0} {units[unit]}" : $"{value:F1} {units[unit]}";
    }

    public static string Frequency(double mhz)
    {
        if (mhz <= 0) return "";
        return mhz >= 1000 ? $"{mhz / 1000.0:F1} GHz" : $"{mhz:F0} MHz";
    }

    public static string Percent(double value) =>
        $"{Math.Clamp(value, 0, 100):F0}%";

    public static double ClampPercent(double value) => Math.Clamp(value, 0, 100);
}