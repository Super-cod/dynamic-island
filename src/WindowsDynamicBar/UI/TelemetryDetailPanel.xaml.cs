using System.Windows.Controls;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Services.SystemMonitor;

namespace WindowsDynamicBar.UI;

public partial class TelemetryDetailPanel : UserControl
{
    public TelemetryDetailPanel()
    {
        InitializeComponent();
    }

    /// <summary>Update the detail panel with CPU metrics and history.</summary>
    public void ShowCpu(CpuStats cpu, TelemetryHistory? history)
    {
        MetricName.Text = "CPU";
        MetricPercent.Text = cpu.Available ? $"{Math.Round(cpu.Utilization)}%" : "n/a";

        var info = cpu.Available
            ? $"{cpu.Name}\n{cpu.LogicalThreads}T @ {cpu.FrequencyMhz:F0} MHz"
            : "CPU data unavailable";
        MetricInfo.Text = info;

        if (history?.Snapshot() is { Length: > 0 } snapshot)
            Graph.Snapshot = snapshot;
    }

    /// <summary>Update the detail panel with GPU metrics and history.</summary>
    public void ShowGpu(GpuAdapterStats gpu, TelemetryHistory? history)
    {
        MetricName.Text = "GPU";
        MetricPercent.Text = $"{Math.Round(gpu.Utilization)}%";

        string vramStr = gpu.VramAvailable && gpu.VramTotalBytes > 0
            ? $"{FormatBytes(gpu.VramUsedBytes)} / {FormatBytes(gpu.VramTotalBytes)}"
            : "VRAM n/a";
        MetricInfo.Text = $"{gpu.Name}\n{vramStr}";

        if (history?.Snapshot() is { Length: > 0 } snapshot)
            Graph.Snapshot = snapshot;
    }

    /// <summary>Update the detail panel with RAM metrics and history.</summary>
    public void ShowRam(MemoryStats ram, TelemetryHistory? history)
    {
        MetricName.Text = "RAM";
        MetricPercent.Text = $"{Math.Round(ram.UsedPercent)}%";

        var info = $"{FormatBytes(ram.UsedBytes)} / {FormatBytes(ram.TotalBytes)}";
        MetricInfo.Text = info;

        if (history?.Snapshot() is { Length: > 0 } snapshot)
            Graph.Snapshot = snapshot;
    }

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;

        return bytes switch
        {
            >= gb => $"{bytes / (double)gb:F1} GB",
            >= mb => $"{bytes / (double)mb:F1} MB",
            >= kb => $"{bytes / (double)kb:F1} KB",
            _ => $"{bytes} B"
        };
    }
}
