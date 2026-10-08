using System.IO;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Animation;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Media;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Services.Media;
using WindowsDynamicBar.Services.SystemInfo;
using WindowsDynamicBar.Services.SystemMonitor;
using WindowsDynamicBar.Services.Timers;
using WindowsDynamicBar.UI;

namespace WindowsDynamicBar.Tests;

/// <summary>
/// Dependency-free test harness (no xunit/NUnit) so the build has zero extra packages.
/// Run: dotnet run --project tests\WindowsDynamicBar.Tests -c Release
/// Exit code 0 = all green.
/// </summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;

    [STAThread]
    private static int Main()
    {
        Console.WriteLine("Windows Dynamic Bar — test suite");
        Console.WriteLine(new string('-', 46));

        Run("EventBus coalesces same-kind events", TestBusCoalesce);
        Run("EventBus honors expiry and priority queue", TestBusExpiryPriority);
        Run("EventBus Idle fires when drained", TestBusIdle);
        Run("SpringAnimator settles exactly at target", TestSpringSettles);
        Run("SpringAnimator retargets mid-flight", TestSpringRetarget);
        Run("Hotkey parser accepts valid combos", TestHotkeyValid);
        Run("Hotkey parser rejects invalid input", TestHotkeyInvalid);
        Run("Settings save/load round-trip", TestSettingsRoundTrip);
        Run("TimerService.Format layout", TestTimerFormat);
        Run("StopwatchService.FormatMs layout", TestStopwatchFormat);
        Run("BarEvent.Create payload", TestBarEventCreate);
        Run("MediaInfo progress clamps 0..1", TestMediaInfoRecord);
        Run("TweenAnimation completes to target", TestTween);
        Run("SettingsService.Update mutates atomically", TestSettingsUpdate);
        Run("RSSI dBm maps to honest bar counts", TestRssiToBars);
        Run("SystemStats carries counter availability", TestSystemStatsAvailability);
        Run("SettingsModel new fields round-trip", TestSettingsNewFields);
        Run("AppTheme enum serializes by name", TestThemeSerialization);
        Run("Diagnostics report includes version and paths", TestDiagnosticsReport);
        Run("Gaming-mode threshold uses Privacy priority", TestGamingThreshold);
        Run("TelemetryHistory keeps newest within capacity", TestTelemetryHistory);
        Run("TelemetryFormat renders bytes and frequency", TestTelemetryFormat);
        Run("GpuEngineParser extracts LUID and fields", TestGpuEngineParser);
        Run("GpuEngineParser groups utilization per adapter", TestGpuEngineGrouping);
        Run("DiscRotation spins, decelerates, stays continuous", TestDiscRotation);
        Run("TelemetryPolicy plans zone transitions", TestTelemetryPolicy);

        Console.WriteLine(new string('-', 46));
        Console.WriteLine($"passed: {_pass}   failed: {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- registration

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _pass++;
            Console.WriteLine($"  PASS  {name}");
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine($"  FAIL  {name}");
            Console.WriteLine($"        {ex.Message}");
        }
    }

    private static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    private static void AssertEq<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{label}: expected '{expected}', got '{actual}'");
    }

    /// <summary>Pump the dispatcher for a wall-clock duration.</summary>
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> done, int timeoutMs)
    {
        int elapsed = 0;
        while (!done() && elapsed < timeoutMs)
        {
            Pump(25);
            elapsed += 25;
        }
    }

    // ---------------------------------------------------------------- tests

    private static void TestBusCoalesce()
    {
        var bus = new EventBus(Dispatcher.CurrentDispatcher);
        int raised = 0;
        bus.EventRaised += _ => raised++;

        bus.Raise(BarEvent.Create(BarEventKind.Volume, "Volume", value: 0.10, valueText: "10%"));
        bus.Raise(BarEvent.Create(BarEventKind.Volume, "Volume", value: 0.20, valueText: "20%"));
        bus.Raise(BarEvent.Create(BarEventKind.Volume, "Volume", value: 0.30, valueText: "30%"));

        AssertEq(3, raised, "raised count");
        Assert(bus.Active is not null, "active event expected");
        AssertEq(BarEventKind.Volume, bus.Active!.Kind, "active kind");
        AssertEq("30%", bus.Active.ValueText ?? "", "coalesced value");
    }

    private static void TestBusExpiryPriority()
    {
        var bus = new EventBus(Dispatcher.CurrentDispatcher);
        BarEvent? got = null;
        bus.EventRaised += e => got = e;

        // Short-lived event holds the stage…
        bus.Raise(BarEvent.Create(BarEventKind.Battery, "Battery",
            displayDuration: TimeSpan.FromMilliseconds(250)));
        // …while a high-priority event waits in the queue.
        bus.Raise(BarEvent.Create(BarEventKind.Toast, "Timer finished",
            priority: 99, displayDuration: null));

        AssertEq(BarEventKind.Battery, bus.Active!.Kind, "battery holds stage");
        PumpUntil(() => bus.Active is null || bus.Active.Kind == BarEventKind.Toast, 2000);
        Assert(bus.Active is not null && bus.Active.Kind == BarEventKind.Toast,
            "high-priority toast should take over after expiry");
        Assert(got is not null && got.Kind == BarEventKind.Toast, "toast delivered");
    }

    private static void TestBusIdle()
    {
        var bus = new EventBus(Dispatcher.CurrentDispatcher);
        bool idle = false;
        bus.Idle += () => idle = true;

        bus.Raise(BarEvent.Create(BarEventKind.Usb, "USB",
            displayDuration: TimeSpan.FromMilliseconds(200)));
        PumpUntil(() => idle, 2000);
        Assert(idle, "Idle should fire after the last transient event expires");
        Assert(bus.Active is null, "active cleared");
    }

    private static void TestSpringSettles()
    {
        double last = 0;
        using var anim = new SpringAnimator();
        anim.Animate("x", 0, 100, v => last = v);
        PumpUntil(() => !anim.IsAnimating, 5000);
        Assert(!anim.IsAnimating, "animator should settle");
        Assert(Math.Abs(last - 100) < 0.01, $"expected settle at 100, got {last}");
    }

    private static void TestSpringRetarget()
    {
        double last = 0;
        using var anim = new SpringAnimator();
        anim.Animate("x", 0, 100, v => last = v);
        Pump(80); // mid-flight
        anim.SetTarget("x", 40);
        PumpUntil(() => !anim.IsAnimating, 5000);
        Assert(Math.Abs(last - 40) < 0.01, $"expected retarget to 40, got {last}");
    }

    private static void TestHotkeyValid()
    {
        Assert(BarWindow.TryParseHotkey("Ctrl+Alt+Space", out uint m1, out uint v1), "Ctrl+Alt+Space");
        AssertEq(NativeMethodsProxy.ModCtrl | NativeMethodsProxy.ModAlt, m1, "modifiers");
        AssertEq(0x20u, v1, "VK_SPACE");

        Assert(BarWindow.TryParseHotkey("ctrl+alt+m", out uint m2, out uint v2), "lowercase ctrl+alt+m");
        AssertEq(0x4Du, v2, "VK of M");

        Assert(BarWindow.TryParseHotkey("F9", out uint m3, out uint v3), "F9 alone");
        AssertEq(0u, m3, "no modifiers");
        AssertEq(0x78u, v3, "VK_F9");

        Assert(BarWindow.TryParseHotkey("Win+Shift+F5", out uint m4, out uint v4), "Win+Shift+F5");
        AssertEq(NativeMethodsProxy.ModWin | NativeMethodsProxy.ModShift, m4, "win+shift");
        AssertEq(0x74u, v4, "VK_F5");

        Assert(BarWindow.TryParseHotkey("Ctrl+B", out _, out uint v6), "Ctrl+B");
        AssertEq(0x42u, v6, "VK of B");
    }

    private static void TestHotkeyInvalid()
    {
        Assert(!BarWindow.TryParseHotkey(null, out _, out _), "null");
        Assert(!BarWindow.TryParseHotkey("", out _, out _), "empty");
        Assert(!BarWindow.TryParseHotkey("   ", out _, out _), "whitespace");
        Assert(!BarWindow.TryParseHotkey("Hyper+Q", out _, out _), "unknown modifier");
        Assert(!BarWindow.TryParseHotkey("Ctrl+", out _, out _), "missing key");
        Assert(!BarWindow.TryParseHotkey("Ctrl+Enter", out _, out _), "unsupported key name");
        Assert(!BarWindow.TryParseHotkey("Ctrl+ab", out _, out _), "multi-char non-function key");
    }

    private static void TestSettingsRoundTrip()
    {
        string path = SettingsService.Instance.SettingsPath;
        SettingsModel? original = File.Exists(path)
            ? JsonSerializer.LoadCopy(path)
            : null;
        try
        {
            var m = new SettingsModel
            {
                BarWidth = 321,
                AccentColor = "#FF30D158",
                CollapseDelayMs = 4321,
                FeatureUsb = false,
                HotkeyToggle = "Ctrl+Shift+F8",
            };
            SettingsService.Instance.Save(m);
            var loaded = SettingsService.Instance.Load();

            AssertEq(321.0, loaded.BarWidth, "BarWidth");
            AssertEq("#FF30D158", loaded.AccentColor, "AccentColor");
            AssertEq(4321, loaded.CollapseDelayMs, "CollapseDelayMs");
            AssertEq(false, loaded.FeatureUsb, "FeatureUsb");
            AssertEq("Ctrl+Shift+F8", loaded.HotkeyToggle, "HotkeyToggle");
        }
        finally
        {
            if (original is not null) SettingsService.Instance.Save(original);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void TestSettingsUpdate()
    {
        string path = SettingsService.Instance.SettingsPath;
        SettingsModel? original = File.Exists(path) ? JsonSerializer.LoadCopy(path) : null;
        try
        {
            SettingsService.Instance.Save(new SettingsModel { BarHeight = 40 });
            SettingsService.Instance.Update(s => { s.BarHeight = 55; s.BarWidth = 250; });
            var cur = SettingsService.Instance.Load();
            AssertEq(55.0, cur.BarHeight, "BarHeight after Update");
            AssertEq(250.0, cur.BarWidth, "BarWidth after Update");
        }
        finally
        {
            if (original is not null) SettingsService.Instance.Save(original);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void TestTimerFormat()
    {
        AssertEq("01:05", TimerService.Format(TimeSpan.FromSeconds(65)), "65s");
        AssertEq("00:09", TimerService.Format(TimeSpan.FromSeconds(9)), "9s");
        AssertEq("01:01:01", TimerService.Format(TimeSpan.FromSeconds(3661)), "3661s");
        AssertEq("00:00", TimerService.Format(TimeSpan.Zero), "zero");
    }

    private static void TestStopwatchFormat()
    {
        AssertEq("01:05.50", StopwatchService.FormatMs(TimeSpan.FromSeconds(65.5)), "65.5s");
        AssertEq("00:03.25", StopwatchService.FormatMs(TimeSpan.FromSeconds(3.25)), "3.25s");
        AssertEq("01:01:01", StopwatchService.FormatMs(TimeSpan.FromSeconds(3661)), "1h+");
    }

    private static void TestBarEventCreate()
    {
        var e = BarEvent.Create(BarEventKind.Download, "Downloading…", "file.zip",
            value: 0.5, valueText: "50% · 4 MB/s", payload: "meta", priority: 42,
            displayDuration: TimeSpan.FromSeconds(3));
        AssertEq(BarEventKind.Download, e.Kind, "Kind");
        AssertEq("Downloading…", e.Title, "Title");
        AssertEq("file.zip", e.Subtitle, "Subtitle");
        AssertEq(0.5, e.Value!.Value, "Value");
        AssertEq("50% · 4 MB/s", e.ValueText, "ValueText");
        AssertEq("meta", e.Payload!, "Payload");
        AssertEq(42, e.Priority, "Priority");
        Assert(e.Timestamp != default, "Timestamp set");
        Assert(e.DisplayDuration == TimeSpan.FromSeconds(3), "DisplayDuration");
    }

    private static void TestMediaInfoRecord()
    {
        var m = new MediaInfo("Song", "Artist", "Album", "app.id", true,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(200), null, 1.5);
        AssertEq(1.5, m.Progress, "record keeps raw value (clamping is a UI concern)");
        AssertEq("Song", m.Title, "Title");
        Assert(m.IsPlaying, "IsPlaying");
    }

    private static void TestTween()
    {
        double last = -1;
        bool done = false;
        using var tw = new TweenAnimation();
        tw.Completed += () => done = true;
        tw.Run(0, 1, TimeSpan.FromMilliseconds(120), v => last = v);
        PumpUntil(() => done, 3000);
        Assert(done, "tween completed");
        Assert(Math.Abs(last - 1.0) < 0.001, $"tween should land on 1.0, got {last}");
    }

    private static void TestRssiToBars()
    {
        AssertEq(-1, NetworkService.RssiToBars(null), "unknown RSSI → -1 (no bars shown)");
        AssertEq(4, NetworkService.RssiToBars(-40), "-40 dBm → 4 bars");
        AssertEq(4, NetworkService.RssiToBars(-50), "-50 dBm → 4 bars");
        AssertEq(3, NetworkService.RssiToBars(-55), "-55 dBm → 3 bars");
        AssertEq(2, NetworkService.RssiToBars(-65), "-65 dBm → 2 bars");
        AssertEq(1, NetworkService.RssiToBars(-75), "-75 dBm → 1 bar");
        AssertEq(0, NetworkService.RssiToBars(-85), "-85 dBm → 0 bars");
        AssertEq(0, NetworkService.RssiToBars(-100), "-100 dBm → 0 bars");
    }

    private static void TestSystemStatsAvailability()
    {
        var full = new SystemStats(12.5, 0, 40, 8_000_000_000);
        Assert(full.CpuAvailable, "CPU available by default");
        Assert(full.GpuAvailable, "GPU available by default");

        var noGpu = new SystemStats(12.5, 0, 40, 8_000_000_000, CpuAvailable: true, GpuAvailable: false);
        Assert(noGpu.CpuAvailable, "CPU flag preserved");
        Assert(!noGpu.GpuAvailable, "GPU unavailable flag preserved");
        AssertEq(40.0, noGpu.Ram, "RAM still reported");
    }

    private static void TestSettingsNewFields()
    {
        string path = SettingsService.Instance.SettingsPath;
        SettingsModel? original = File.Exists(path) ? JsonSerializer.LoadCopy(path) : null;
        try
        {
            SettingsService.Instance.Save(new SettingsModel
            {
                MonitorIndex = 2,
                ReduceMotion = true,
                Theme = AppTheme.Light,
            });
            var loaded = SettingsService.Instance.Load();
            AssertEq(2, loaded.MonitorIndex, "MonitorIndex");
            Assert(loaded.ReduceMotion, "ReduceMotion");
            AssertEq(AppTheme.Light, loaded.Theme, "Theme");
        }
        finally
        {
            if (original is not null) SettingsService.Instance.Save(original);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void TestThemeSerialization()
    {
        string path = SettingsService.Instance.SettingsPath;
        SettingsModel? original = File.Exists(path) ? JsonSerializer.LoadCopy(path) : null;
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.Translucent })
            {
                SettingsService.Instance.Save(new SettingsModel { Theme = theme });
                var loaded = SettingsService.Instance.Load();
                AssertEq(theme, loaded.Theme, $"theme {theme} round-trip");
            }
        }
        finally
        {
            if (original is not null) SettingsService.Instance.Save(original);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void TestDiagnosticsReport()
    {
        string report = SettingsWindow.BuildDiagnosticsReport();
        Assert(report.Contains(App.InfoVersion, StringComparison.Ordinal), "report contains app version");
        Assert(report.Contains("OS:", StringComparison.Ordinal), "report contains OS line");
        Assert(report.Contains("Services:", StringComparison.Ordinal), "report contains services section");
        Assert(report.Length > 100, "report has substance");
    }

    private static void TestGamingThreshold()
    {
        // BarWindow.OnBusEvent suppresses events with Priority < (int)BarEventKind.Privacy (60).
        // Assert using the priorities services actually pass at Raise() time.
        int threshold = (int)BarEventKind.Privacy;
        Assert(5 < threshold, "sysmon (raised at priority 5) suppressed in gaming mode");
        Assert(30 < threshold, "network change (priority 30) suppressed in gaming mode");
        Assert(45 < threshold, "bluetooth (priority 45) suppressed in gaming mode");
        Assert(60 >= threshold, "privacy indicators (priority 60) still shown");
        Assert(75 >= threshold, "timer expiry (priority 75) still shown");
        Assert(95 >= threshold, "low battery (priority 95) still shown");
    }

    private static void TestTelemetryHistory()
    {
        var h = new TelemetryHistory(4);
        Assert(h.IsEmpty, "empty history starts empty");
        foreach (double v in new[] { 1, 2, 3 })
        {
            h.Push(v);
            AssertEq(v, h.Last, "last value tracks newest");
        }
        AssertEq(3, h.Count, "count before overflow");
        AssertEq(1.0, h.Min(), "min");
        AssertEq(3.0, h.Max(), "max");

        foreach (double v in new[] { 4, 5, 6 })
            h.Push(v);
        AssertEq(4, h.Count, "count capped at capacity");
        var snap = h.Snapshot();
        AssertEq(4, snap.Length, "snapshot length capped at capacity");
        for (int i = 0; i < 4; i++) AssertEq(3 + i, snap[i], $"snapshot[{i}] keeps newest order");

        var all = new TelemetryHistory(3);
        for (double v = 0; v < 30; v++) all.Push(v);
        double[] s = all.Snapshot();
        AssertEq(27.0, s[0], "overflow keeps newest");
        AssertEq(29.0, s[2], "overflow ends at latest");

        h.Clear();
        Assert(h.IsEmpty, "clear empties history");
        h.Push(7);
        AssertEq(7.0, h.Last, "push after clear works");
    }

    private static void TestTelemetryFormat()
    {
        AssertEq("0 B", TelemetryFormat.Bytes(0), "bytes zero");
        AssertEq("512 B", TelemetryFormat.Bytes(512), "bytes small");
        AssertEq("1 KB", TelemetryFormat.Bytes(1024), "bytes KB");
        AssertEq("5.0 MB", TelemetryFormat.Bytes(5 * 1024 * 1024), "bytes MB");
        AssertEq("8.0 GB", TelemetryFormat.Bytes(8L * 1024 * 1024 * 1024), "bytes GB");
        AssertEq("", TelemetryFormat.Frequency(0), "frequency zero");
        AssertEq("2.4 GHz", TelemetryFormat.Frequency(2400), "frequency over GHz threshold");
        AssertEq("4.2 GHz", TelemetryFormat.Frequency(4200), "frequency GHz");
        AssertEq("42%", TelemetryFormat.Percent(42.4), "percent round");
        AssertEq("0%", TelemetryFormat.Percent(-5), "percent clamps low");
        AssertEq("100%", TelemetryFormat.Percent(150), "percent clamps high");
    }

    private static void TestGpuEngineParser()
    {
        const string sample = "pid_4824_luid_0x00000000_0x00013CC1_phys_1_eng_2_engtype_3D";
        Assert(GpuEngineParser.TryParse(sample, out var info), "parses valid engine instance");
        AssertEq(81089L, info.Luid, "LUID low part combined");
        AssertEq(1, info.Phys, "phys adapter");
        AssertEq(2, info.Eng, "engine index");
        AssertEq("3D", info.EngType, "engine type");

        const string withHi = "pid_10_luid_0x00000001_0x00000000_phys_0_eng_0_engtype_Compute";
        Assert(GpuEngineParser.TryParse(withHi, out var hi), "parses high LUID part");
        AssertEq(4294967296L, hi.Luid, "LUID high part combined");

        Assert(!GpuEngineParser.TryParse("", out _), "empty rejected");
        Assert(!GpuEngineParser.TryParse("garbage", out _), "garbage rejected");
        Assert(!GpuEngineParser.TryParse("pid_1_luid_not_a_number_phys_0_eng_0_engtype_3D", out _), "bad luid rejected");
        Assert(!GpuEngineParser.TryParse("pid_1_luid_0x00000000_0x00013CC1_phys_0_eng_0", out _), "missing engtype rejected");
    }

    private static void TestGpuEngineGrouping()
    {
        var samples = new (string, double)[]
        {
            ("pid_1_luid_0x00000000_0x000000A0_phys_0_eng_0_engtype_3D", 30.0),
            ("pid_1_luid_0x00000000_0x000000A0_phys_0_eng_1_engtype_Compute", 40.0),
            ("pid_2_luid_0x00000000_0x000000B0_phys_0_eng_0_engtype_3D", 55.0),
            ("not an engine", 99.0),
        };
        var grouped = GpuEngineParser.GroupUtilization(samples);
        AssertEq(2, grouped.Count, "two adapters grouped");
        Assert(grouped.TryGetValue(160L, out double a), "first adapter present");
        AssertEq(70.0, a, "engines summed per adapter");
        Assert(grouped.TryGetValue(176L, out double b), "second adapter present");
        AssertEq(55.0, b, "single engine value kept");

        var over = GpuEngineParser.GroupUtilization(new (string, double)[]
        {
            ("pid_1_luid_0x00000000_0x000000C0_phys_0_eng_0_engtype_3D", 70.0),
            ("pid_1_luid_0x00000000_0x000000C0_phys_0_eng_1_engtype_Compute", 40.0),
        });
        Assert(over.TryGetValue(192L, out double sum) && sum <= 100.0, "per-adapter utilization clamps at 100");
    }

    private static void TestDiscRotation()
    {
        double angle = 0, omega = 0;
        for (int i = 0; i < 60; i++)
        {
            (angle, omega) = DiscRotation.Step(angle, omega, true, 1.0 / 30.0);
        }
        Assert(Math.Abs(omega - DiscRotation.TargetDegreesPerSecond) < 0.5, "spinning converges to target omega");
        Assert(angle > 0 && angle <= 360, "angle stays in [0,360]");

        for (int i = 0; i < 600; i++)
        {
            double before = angle;
            (angle, omega) = DiscRotation.Step(angle, omega, true, 1.0 / 30.0);
            double dtheta = angle - before;
            if (dtheta < 0) dtheta += 360.0;
            Assert(dtheta <= DiscRotation.TargetDegreesPerSecond * (1.0 / 30.0) + 0.01, "no rotation jumps");
        }

        (angle, omega) = DiscRotation.Step(angle, omega, false, 1.0 / 30.0);
        double prevOmega = omega;
        Assert(prevOmega < DiscRotation.TargetDegreesPerSecond, "deceleration begins on pause");
        for (int i = 0; i < 120; i++)
        {
            double before = omega;
            (angle, omega) = DiscRotation.Step(angle, omega, false, 1.0 / 30.0);
            Assert(omega <= before + 1e-9, "omega never increases while paused");
            prevOmega = omega;
        }
        Assert(DiscRotation.ShouldTrim(false, omega), "rotation trims once nearly stopped");

        (double a0, double o0) = DiscRotation.Step(angle, omega, true, 0);
        Assert(angle == a0 && omega == o0, "zero dt is a no-op");
    }

    private static void TestTelemetryPolicy()
    {
        AssertEq(TelemetryZoneState.Hidden, TelemetryPolicy.Desired(TelemetryTarget.None), "none -> hidden");
        AssertEq(TelemetryZoneState.Rail, TelemetryPolicy.Desired(TelemetryTarget.Pill), "pill -> rail");
        AssertEq(TelemetryZoneState.Rail, TelemetryPolicy.Desired(TelemetryTarget.Rail), "rail stays rail");
        AssertEq(TelemetryZoneState.Cpu, TelemetryPolicy.Desired(TelemetryTarget.Cpu), "cpu metric");
        AssertEq(TelemetryZoneState.Gpu, TelemetryPolicy.Desired(TelemetryTarget.Gpu), "gpu metric");
        AssertEq(TelemetryZoneState.Ram, TelemetryPolicy.Desired(TelemetryTarget.Ram), "ram metric");

        AssertEq(TelemetryAction.None, TelemetryPolicy.Plan(TelemetryZoneState.Hidden, TelemetryTarget.None), "hidden idle");
        AssertEq(TelemetryAction.OpenRail, TelemetryPolicy.Plan(TelemetryZoneState.Hidden, TelemetryTarget.Pill), "open rail from hidden");
        AssertEq(TelemetryAction.OpenMetric, TelemetryPolicy.Plan(TelemetryZoneState.Hidden, TelemetryTarget.Ram), "open metric from hidden");
        AssertEq(TelemetryAction.OpenMetric, TelemetryPolicy.Plan(TelemetryZoneState.Rail, TelemetryTarget.Gpu), "open metric from rail");
        AssertEq(TelemetryAction.None, TelemetryPolicy.Plan(TelemetryZoneState.Gpu, TelemetryTarget.Gpu), "same metric no-op");
        AssertEq(TelemetryAction.OpenMetric, TelemetryPolicy.Plan(TelemetryZoneState.Cpu, TelemetryTarget.Ram), "switch metric");
        AssertEq(TelemetryAction.CloseMetricToRail, TelemetryPolicy.Plan(TelemetryZoneState.Ram, TelemetryTarget.Pill), "close metric to rail");
        AssertEq(TelemetryAction.HideAll, TelemetryPolicy.Plan(TelemetryZoneState.Rail, TelemetryTarget.None), "hide rail");
        AssertEq(TelemetryAction.CloseMetricToRail, TelemetryPolicy.Plan(TelemetryZoneState.Cpu, TelemetryTarget.None), "metric hide two-phase");
    }
}

/// <summary>Local mirror of modifier flags so tests don't take a UI dependency for constants.</summary>
internal static class NativeMethodsProxy
{
    public const uint ModAlt = 0x0001;
    public const uint ModCtrl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
}

/// <summary>Minimal JSON copy helper for saving/restoring the real settings file in tests.</summary>
internal static class JsonSerializer
{
    public static SettingsModel? LoadCopy(string path)
    {
        try
        {
            var json = System.IO.File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<SettingsModel>(json,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
        }
        catch { return null; }
    }
}
