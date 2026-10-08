using System.Windows.Controls;

namespace WindowsDynamicBar.UI;

public partial class TelemetryIconRow : UserControl
{
    public TelemetryIconRow()
    {
        InitializeComponent();
    }

    /// <summary>Update icon gauge values from telemetry snapshot.</summary>
    public void UpdateValues(double cpu, double gpu, double ram)
    {
        CpuText.Text = FormatPercent(cpu);
        GpuText.Text = FormatPercent(gpu);
        RamText.Text = FormatPercent(ram);
    }

    private static string FormatPercent(double value) =>
        value < 0 ? "n/a" : $"{Math.Round(value)}%";

    private void OnIconClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Placeholder for icon click handler (will be wired from BarWindow)
        e.Handled = true;
    }
}
