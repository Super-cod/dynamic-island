using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsDynamicBar.Core.Media;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Services.Privacy;
using WindowsDynamicBar.Services.SystemMonitor;

namespace WindowsDynamicBar.Tests;

/// <summary>Regression tests for defects found in the full-codebase audit.</summary>
internal static partial class Program
{
    private static void RunAuditTests()
    {
        Run("SystemMonitorService produces real samples (PDH entry point exists)", TestSysMonSamples);
        Run("NetworkMonitor.Dispose does not throw and persists totals", TestNetworkMonitorDispose);
        Run("Privacy: packaged app family name -> friendly name", TestPackageFriendlyName);
        Run("App icons keep their transparent corners", TestIconTransparency);
    }

    private static void TestSysMonSamples()
    {
        using var mon = new SystemMonitorService(SettingsService.Instance);
        SystemStats? got = null;
        using var gotOne = new ManualResetEventSlim(false);
        mon.StatsChanged += s => { got = s; gotOne.Set(); };
        mon.Activate();
        Assert(gotOne.Wait(TimeSpan.FromSeconds(8)), "no StatsChanged within 8 s (sampling throws?)");
        Assert(got!.Ram > 0 && got.Ram <= 100, $"RAM % plausible (got {got.Ram})");
        Assert(got.CpuAvailable, "CPU counter available");
    }

    private static void TestNetworkMonitorDispose()
    {
        string path = Path.Combine(Path.GetTempPath(), "wdb-audit-state-" + Guid.NewGuid().ToString("N") + ".json");
        RuntimeState.UseForTests(path);
        try
        {
            var net = new NetworkMonitor(SettingsService.Instance);
            net.Start();
            net.Dispose();   // Stop() runs after the timer is disposed: Change() must stay harmless
            Assert(File.Exists(path), "daily totals persisted on dispose");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static void TestPackageFriendlyName()
    {
        AssertEq("WhatsAppDesktop", PrivacyService.PackageFriendlyName("5319275A.WhatsAppDesktop_cv1g1gvanyjgm"), "WhatsApp");
        AssertEq("WindowsCamera", PrivacyService.PackageFriendlyName("Microsoft.WindowsCamera_8wekyb3d8bbwe"), "Camera");
        AssertEq("MSTeams", PrivacyService.PackageFriendlyName("MSTeams_8wekyb3d8bbwe"), "Teams");
    }

    private static void TestIconTransparency()
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        Assert(File.Exists(exe), "notepad.exe present");
        var icon = AppIconHelper.ExtractIconFromFile(exe);
        Assert(icon is not null, "icon extracted");
        var bgra = new FormatConvertedBitmap(icon!, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        bgra.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), px, 4, 0);
        Assert(px[3] == 0, $"top-left corner is transparent (alpha {px[3]}, rgb {px[2]},{px[1]},{px[0]}; source format {icon!.Format})");
    }
}
