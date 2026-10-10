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

    public static string Temperature(double celsius, bool fahrenheit) => fahrenheit
        ? $"{celsius * 9 / 5 + 32:F0}°F"
        : $"{celsius:F0}°C";

    /// <summary>"NVIDIA GeForce RTX 3050 Laptop GPU" -> "RTX 3050 Laptop GPU",
    /// "Intel(R) UHD Graphics" -> "UHD Graphics" (vendor words dropped for the title tag).</summary>
    public static string ShortGpuName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string s = name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase);
        foreach (string prefix in new[] { "NVIDIA ", "GeForce ", "AMD ", "Radeon ", "Intel ", "Qualcomm " })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}