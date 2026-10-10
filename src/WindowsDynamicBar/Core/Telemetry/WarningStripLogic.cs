namespace WindowsDynamicBar.Core.Telemetry;

public sealed class WarningStripLogic
{
    public int RamHighCount { get; private set; }
    public int CpuHighCount { get; private set; }
    public bool RamDismissed { get; private set; }
    public bool CpuDismissed { get; private set; }
    public bool BatteryDismissed { get; private set; }

    public void Dismiss(string kind)
    {
        if (kind == "RAM") RamDismissed = true;
        else if (kind == "CPU") CpuDismissed = true;
        else if (kind == "Battery") BatteryDismissed = true;
    }

    public bool Update(double ram, double cpu, float batteryPct, bool isCharging, out string kind)
    {
        if (ram >= 0 && ram < 85) RamDismissed = false;
        if (cpu >= 0 && cpu < 90) CpuDismissed = false;
        if (batteryPct > 20 || isCharging) BatteryDismissed = false;

        if (ram >= 90) RamHighCount++;
        else RamHighCount = 0;

        if (cpu >= 95) CpuHighCount++;
        else CpuHighCount = 0;

        bool ramTrigger = RamHighCount >= 5 && !RamDismissed;
        bool cpuTrigger = CpuHighCount >= 10 && !CpuDismissed;
        bool batteryTrigger = (batteryPct >= 0 && batteryPct <= 15 && !isCharging) && !BatteryDismissed;

        if (ramTrigger)
        {
            kind = "RAM";
            return true;
        }
        if (cpuTrigger)
        {
            kind = "CPU";
            return true;
        }
        if (batteryTrigger)
        {
            kind = "Battery";
            return true;
        }

        kind = "";
        return false;
    }
}
