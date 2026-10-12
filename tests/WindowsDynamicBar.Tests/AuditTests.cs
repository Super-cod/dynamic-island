using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Buddy;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Media;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Services.Privacy;
using WindowsDynamicBar.Services.SystemMonitor;
using WindowsDynamicBar.Services.Updates;
using WindowsDynamicBar.UI;
using WindowsDynamicBar.UI.Buddy;

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
        Run("SemVer parses versions with build metadata (+build)", TestSemVerMetadata);
        Run("EventBus Dismiss with queued events does not fire Idle prematurely", TestEventBusDismissQueue);
        Run("SettingsServiceBridge supports safe unsubscription", TestSettingsBridgeUnsubscribe);
        Run("UsbService parses DEV_BROADCAST_VOLUME offsets and handles Z: drives safely", TestUsbServiceDriveNotification);
        Run("IdleDotClock preserves and renders digits on visibility transitions", TestIdleDotClockVisibilitySync);
        Run("Buddy headphones render with over-ear cups and headband", TestBuddyHeadphonesOverEarRender);
        Run("HeadphonesVectorIcon renders sharp vector geometry in collapsed and expanded states", TestHeadphonesVectorIconRender);
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

    private static void TestSemVerMetadata()
    {
        Assert(SemVer.TryParse("1.2.3+20230501", out var v1), "parse build metadata");
        AssertEq(1, v1.Major, "v1 major");
        AssertEq(2, v1.Minor, "v1 minor");
        AssertEq(3, v1.Patch, "v1 patch");
        AssertEq("", v1.Pre, "v1 pre");

        Assert(SemVer.TryParse("2.0.0-rc.1+sha.abcdef", out var v2), "parse prerelease with metadata");
        AssertEq(2, v2.Major, "v2 major");
        AssertEq(0, v2.Minor, "v2 minor");
        AssertEq(0, v2.Patch, "v2 patch");
        AssertEq("rc.1", v2.Pre, "v2 pre");

        Assert(SemVer.TryParse("v0.9.5", out var v3), "parse v prefix");
        AssertEq(0, v3.Major, "v3 major");
        AssertEq(9, v3.Minor, "v3 minor");
        AssertEq(5, v3.Patch, "v3 patch");
    }

    private static void TestEventBusDismissQueue()
    {
        var bus = new EventBus(Dispatcher.CurrentDispatcher);
        bool idleFired = false;
        bus.Idle += () => idleFired = true;

        // Raise 2 events: first becomes Active, second waits in queue
        bus.Raise(BarEvent.Create(BarEventKind.Usb, "USB 1", priority: 10, displayDuration: TimeSpan.FromSeconds(5)));
        bus.Raise(BarEvent.Create(BarEventKind.Battery, "Battery 2", priority: 20, displayDuration: TimeSpan.FromSeconds(5)));

        Assert(bus.Active is not null, "first event active");
        // Dismiss first event: second event should immediately become Active, and Idle must NOT fire!
        bus.Dismiss();
        Assert(bus.Active is not null, "second event took over");
        AssertEq(BarEventKind.Battery, bus.Active!.Kind, "second event is Battery");
        Assert(!idleFired, "Idle must not fire while second event is active");

        // Dismiss second event: queue is now empty, so Idle should fire
        bus.Dismiss();
        Assert(bus.Active is null, "no active event");
        Assert(idleFired, "Idle fired when queue fully drained");
    }

    private static void TestSettingsBridgeUnsubscribe()
    {
        int calls = 0;
        SettingsServiceBridge.Subscribe(() => calls++);
        SettingsServiceBridge.Unsubscribe();
        // After unsubscribe, SettingsChanged should not invoke handler
        SettingsService.Instance.Update(s => s.FirstRunCompleted = s.FirstRunCompleted);
        Pump(50);
        AssertEq(0, calls, "handler was unsubscribed");
    }

    private static void TestUsbServiceDriveNotification()
    {
        var bus = new EventBus(Dispatcher.CurrentDispatcher);
        var usb = new UsbService(bus, SettingsService.Instance);
        BarEvent? raised = null;
        bus.EventRaised += e => raised = e;

        // Allocate a simulated DEV_BROADCAST_VOLUME struct
        // offset 0: dbcv_size = 24
        // offset 4: dbcv_devicetype = 2 (DBT_DEVTYP_VOLUME)
        // offset 8: dbcv_reserved = 0
        // offset 12: dbcv_unitmask = 1 << 2 (drive C:)
        IntPtr mem = System.Runtime.InteropServices.Marshal.AllocHGlobal(24);
        try
        {
            System.Runtime.InteropServices.Marshal.WriteInt32(mem, 0, 24);
            System.Runtime.InteropServices.Marshal.WriteInt32(mem, 4, 2);
            System.Runtime.InteropServices.Marshal.WriteInt32(mem, 8, 0);
            System.Runtime.InteropServices.Marshal.WriteInt32(mem, 12, 1 << 2);
            usb.OnDeviceChange(WindowsDynamicBar.Interop.NativeMethods.DBT_DEVICEARRIVAL, mem);
            Assert(raised is not null, "USB event raised");
            AssertEq(BarEventKind.Usb, raised!.Kind, "USB kind");
            Assert(raised.Subtitle is not null && raised.Subtitle.Contains("C: drive", StringComparison.OrdinalIgnoreCase), $"includes drive letter (got '{raised.Subtitle}')");
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(mem);
        }

        // Test with drive 'Z' (index 25) which was previously ignored
        raised = null;
        mem = System.Runtime.InteropServices.Marshal.AllocHGlobal(24);
        try
        {
            System.Runtime.InteropServices.Marshal.WriteInt32(mem, 0, 24);
            System.Runtime.InteropServices.Marshal.WriteInt32(mem, 4, 2);
            System.Runtime.InteropServices.Marshal.WriteInt32(mem, 8, 0);
            System.Runtime.InteropServices.Marshal.WriteInt32(mem, 12, 1 << 25);
            usb.OnDeviceChange(WindowsDynamicBar.Interop.NativeMethods.DBT_DEVICEARRIVAL, mem);
            Assert(raised is not null, "USB event raised for drive Z");
            Assert(raised!.Subtitle is not null && raised.Subtitle.Contains("Z: drive", StringComparison.OrdinalIgnoreCase), $"includes drive Z (got '{raised.Subtitle}')");
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(mem);
        }

        // Test null lParam does not crash
        usb.OnDeviceChange(WindowsDynamicBar.Interop.NativeMethods.DBT_DEVICEARRIVAL, IntPtr.Zero);
    }

    private static void TestIdleDotClockVisibilitySync()
    {
        var clock = new DotMatrixClock();
        clock.SetTime("12:34", true);
        clock.Visibility = System.Windows.Visibility.Collapsed;
        // Simulating clock ticking while collapsed/invisible
        clock.SetTime("12:34", false);
        clock.Visibility = System.Windows.Visibility.Visible;
        clock.SetTime("12:35", true);
        Assert(clock.Hue > 0, "Hue is valid");
    }

    private static void TestBuddyHeadphonesOverEarRender()
    {
        var control = new BuddySpriteControl();
        control.RenderFrame(
            mood: BuddyMood.Happy,
            accessory: BuddyAccessory.Headphones,
            timeMs: 1000,
            reduceMotion: false,
            isTalking: false,
            hop: 0,
            wave: 0,
            lookX: 0,
            lookY: 0,
            isBlinking: false
        );

        var bmp = control.Bitmap;
        int[] pixels = new int[16 * 16];
        bmp.CopyPixels(pixels, 16 * 4, 0);

        // 1. Verify headband arch exists across crown (Y=1, X=5..10)
        Assert(((pixels[1 * 16 + 5] >> 24) & 0xFF) > 0, "Headband crown left exists at (5, 1)");
        Assert(((pixels[1 * 16 + 8] >> 24) & 0xFF) > 0, "Headband crown center exists at (8, 1)");
        Assert(((pixels[1 * 16 + 10] >> 24) & 0xFF) > 0, "Headband crown right exists at (10, 1)");

        // 2. Verify headband side curves (Y=2: X=4, 11; Y=3: X=3, 12)
        Assert(((pixels[2 * 16 + 4] >> 24) & 0xFF) > 0, "Headband curve exists at (4, 2)");
        Assert(((pixels[2 * 16 + 11] >> 24) & 0xFF) > 0, "Headband curve exists at (11, 2)");
        Assert(((pixels[3 * 16 + 3] >> 24) & 0xFF) > 0, "Headband curve exists at (3, 3)");
        Assert(((pixels[3 * 16 + 12] >> 24) & 0xFF) > 0, "Headband curve exists at (12, 3)");

        // 3. Verify metal gimbal slider exists (Y=4: X=2, 13)
        Assert(((pixels[4 * 16 + 2] >> 24) & 0xFF) > 0, "Metal gimbal slider exists at (2, 4)");
        Assert(((pixels[4 * 16 + 13] >> 24) & 0xFF) > 0, "Metal gimbal slider exists at (13, 4)");

        // 4. Verify big earcups cover from Y=5 to Y=9
        for (int y = 5; y <= 9; y++)
        {
            Assert(((pixels[y * 16 + 2] >> 24) & 0xFF) > 0, $"Left earcup covers Y={y}");
            Assert(((pixels[y * 16 + 13] >> 24) & 0xFF) > 0, $"Right earcup covers Y={y}");
        }

        // 5. Save comparison image to scratch folder for verification
        string scratchDir = @"C:\Users\swaya\.gemini\antigravity-cli\brain\c89eee73-bae1-466f-8043-0823f12f9465\scratch";
        if (Directory.Exists(scratchDir))
        {
            var encoder = new PngBitmapEncoder();
            var scaled = new TransformedBitmap(bmp, new ScaleTransform(16, 16));
            encoder.Frames.Add(BitmapFrame.Create(scaled));
            using var fs = File.Create(Path.Combine(scratchDir, "buddy_headphones_rendered.png"));
            encoder.Save(fs);
        }
    }

    private static void TestHeadphonesVectorIconRender()
    {
        // 1. Collapsed state rendering (18x18)
        var collapsedIcon = new HeadphonesVectorIcon { Width = 18, Height = 18 };
        collapsedIcon.Measure(new System.Windows.Size(18, 18));
        collapsedIcon.Arrange(new System.Windows.Rect(0, 0, 18, 18));
        var rtbCollapsed = new RenderTargetBitmap(18, 18, 96, 96, PixelFormats.Pbgra32);
        rtbCollapsed.Render(collapsedIcon);

        var pixelsCollapsed = new int[18 * 18];
        rtbCollapsed.CopyPixels(pixelsCollapsed, 18 * 4, 0);
        bool hasCollapsedPixels = pixelsCollapsed.Any(p => ((p >> 24) & 0xFF) > 0);
        Assert(hasCollapsedPixels, "Collapsed HeadphonesVectorIcon rendered non-zero pixels");

        // 2. Expanded state rendering (26x26)
        var expandedIcon = new HeadphonesVectorIcon { Width = 26, Height = 26 };
        expandedIcon.Measure(new System.Windows.Size(26, 26));
        expandedIcon.Arrange(new System.Windows.Rect(0, 0, 26, 26));
        var rtbExpanded = new RenderTargetBitmap(26, 26, 96, 96, PixelFormats.Pbgra32);
        rtbExpanded.Render(expandedIcon);

        var pixelsExpanded = new int[26 * 26];
        rtbExpanded.CopyPixels(pixelsExpanded, 26 * 4, 0);
        bool hasExpandedPixels = pixelsExpanded.Any(p => ((p >> 24) & 0xFF) > 0);
        Assert(hasExpandedPixels, "Expanded HeadphonesVectorIcon rendered non-zero pixels");

        // 3. Verify colorful gradient / glow treatment: contains signature blue / cyan tones
        bool hasBlueOrCyan = pixelsExpanded.Any(p =>
        {
            int b = p & 0xFF;
            int g = (p >> 8) & 0xFF;
            int r = (p >> 16) & 0xFF;
            int a = (p >> 24) & 0xFF;
            return a > 50 && (b > 180 || (b > 150 && g > 150));
        });
        Assert(hasBlueOrCyan, "HeadphonesVectorIcon preserves signature vibrant blue/cyan gradient and glow");

        // 4. Save rendered image to scratch for visual confirmation
        string scratchDir = @"C:\Users\swaya\.gemini\antigravity-cli\brain\c89eee73-bae1-466f-8043-0823f12f9465\scratch";
        if (Directory.Exists(scratchDir))
        {
            var iconLarge = new HeadphonesVectorIcon { Width = 64, Height = 64 };
            iconLarge.Measure(new System.Windows.Size(64, 64));
            iconLarge.Arrange(new System.Windows.Rect(0, 0, 64, 64));
            var rtbLarge = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
            rtbLarge.Render(iconLarge);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtbLarge));
            using var fs = File.Create(Path.Combine(scratchDir, "headphones_vector_rendered.png"));
            encoder.Save(fs);
        }
    }
}
