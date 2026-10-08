namespace WindowsDynamicBar.Services.SystemMonitor;

public readonly record struct GpuEngineInfo(long Luid, int Phys, int Eng, string EngType);

public static class GpuEngineParser
{
    public static bool TryParse(string instanceName, out GpuEngineInfo info)
    {
        info = default;
        if (string.IsNullOrEmpty(instanceName)) return false;

        var parts = instanceName.Split('_',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!TrySegment(parts, "luid", out int luidIdx) || luidIdx + 2 >= parts.Length) return false;
        if (!TrySegment(parts, "phys", out int physIdx) || physIdx + 1 >= parts.Length) return false;
        if (!TrySegment(parts, "eng", out int engIdx) || engIdx + 1 >= parts.Length) return false;
        if (!TrySegment(parts, "engtype", out int typeIdx) || typeIdx + 1 >= parts.Length) return false;

        if (!TryHex(parts[luidIdx + 1], out long hi)) return false;
        if (!TryHex(parts[luidIdx + 2], out long lo)) return false;
        long luid = unchecked(lo | (hi << 32));
        if (!int.TryParse(parts[physIdx + 1], out int phys)) return false;
        if (!int.TryParse(parts[engIdx + 1], out int eng)) return false;

        info = new GpuEngineInfo(luid, phys, eng, parts[typeIdx + 1]);
        return true;
    }

    public static Dictionary<long, double> GroupUtilization(
        IReadOnlyCollection<(string Instance, double Value)> samples)
    {
        var grouped = new Dictionary<long, double>();
        foreach (var (instance, value) in samples)
        {
            if (!TryParse(instance, out var info)) continue;
            grouped.TryGetValue(info.Luid, out double current);
            grouped[info.Luid] = Math.Min(100, current + value);
        }
        return grouped;
    }

    private static bool TrySegment(string[] parts, string name, out int index)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], name, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }
        index = -1;
        return false;
    }

    private static bool TryHex(string token, out long value)
    {
        value = 0;
        if (token.Length < 2) return false;
        ReadOnlySpan<char> span = token.AsSpan();
        bool hex = span[0] == '0' && (span[1] == 'x' || span[1] == 'X');
        if (!hex) return long.TryParse(token, out value);
        return long.TryParse(span[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
    }
}