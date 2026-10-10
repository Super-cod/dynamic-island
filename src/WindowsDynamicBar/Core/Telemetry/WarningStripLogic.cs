namespace WindowsDynamicBar.Core.Telemetry;

/// <summary>
/// When the island's warning strip shows. Called once per second.
///  RAM: at/above <see cref="RamPercent"/> for 5 ticks (dismissal resets below RamPercent - 5).
///  CPU: at/above <see cref="CpuPercent"/> for 10 ticks (dismissal resets below CpuPercent - 5).
///  Battery: at/below <see cref="BatteryPercent"/> and not charging for 5 ticks; once shown it stays
///  until charging or above BatteryPercent + 5 (so it does not flicker around the threshold).
/// Negative inputs mean "unknown" and never trigger.
/// </summary>
public sealed class WarningStripLogic
{
    public double RamPercent { get; set; } = 90;
    public double CpuPercent { get; set; } = 95;
    public double BatteryPercent { get; set; } = 15;

    public int RamHighCount { get; private set; }
    public int CpuHighCount { get; private set; }
    public int BatteryLowCount { get; private set; }
    public bool RamDismissed { get; private set; }
    public bool CpuDismissed { get; private set; }
    public bool BatteryDismissed { get; private set; }
    public bool BatteryWarning { get; private set; }

    public void Dismiss(string kind)
    {
        if (kind == "RAM") RamDismissed = true;
        else if (kind == "CPU") CpuDismissed = true;
        else if (kind == "Battery") BatteryDismissed = true;
    }

    public bool Update(double ram, double cpu, float batteryPct, bool isCharging, out string kind)
    {
        if (ram >= 0 && ram < RamPercent - 5) RamDismissed = false;
        if (cpu >= 0 && cpu < CpuPercent - 5) CpuDismissed = false;

        RamHighCount = ram >= RamPercent ? RamHighCount + 1 : 0;
        CpuHighCount = cpu >= CpuPercent ? CpuHighCount + 1 : 0;

        bool batteryKnown = batteryPct >= 0;
        bool releases = !batteryKnown || isCharging || batteryPct > BatteryPercent + 5;
        if (releases)
        {
            BatteryWarning = false;
            BatteryDismissed = false;
            BatteryLowCount = 0;
        }
        else if (batteryPct <= BatteryPercent)
        {
            if (++BatteryLowCount >= 5) BatteryWarning = true;
        }
        else BatteryLowCount = 0;   // between the two thresholds: keep the current state

        bool ramTrigger = RamHighCount >= 5 && !RamDismissed;
        bool cpuTrigger = CpuHighCount >= 10 && !CpuDismissed;
        bool batteryTrigger = BatteryWarning && !BatteryDismissed;

        if (ramTrigger) { kind = "RAM"; return true; }
        if (cpuTrigger) { kind = "CPU"; return true; }
        if (batteryTrigger) { kind = "Battery"; return true; }
        kind = "";
        return false;
    }
}
