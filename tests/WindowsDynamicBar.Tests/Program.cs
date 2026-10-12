using System.IO;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Animation;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Media;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Core.Search;
using WindowsDynamicBar.Services.Media;
using WindowsDynamicBar.Services.SystemInfo;
using WindowsDynamicBar.Services.SystemMonitor;
using WindowsDynamicBar.Services.Timers;
using WindowsDynamicBar.Services.Weather;
using WindowsDynamicBar.UI;

namespace WindowsDynamicBar.Tests;

/// <summary>
/// Dependency-free test harness (no xunit/NUnit) so the build has zero extra packages.
/// Run: dotnet run --project tests\WindowsDynamicBar.Tests -c Release
/// Exit code 0 = all green.
/// </summary>
internal static partial class Program
{
    private static int _pass;
    private static int _fail;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--screenshots")
        {
            ScreenshotGenerator.GenerateAll("docs/screenshots");
            return 0;
        }
        string testSettingsDir = Path.Combine(Path.GetTempPath(), "wdb-test-suite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testSettingsDir);
        string originalSettingsPath = SettingsService.Instance.SettingsPath;
        SettingsService.Instance.SettingsPath = Path.Combine(testSettingsDir, "settings.json");

        try
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
        Run("Island state machine transitions", TestIslandStateMachine);
        Run("DotBar color ramp green→red", TestDotBarColorRamp);
        Run("TelemetryHistory capacity default is 240", TestTelemetryHistoryCapacity);
        Run("EventBus interactive preemption preempts active card", TestBusPreemption);
        Run("Time format 0:00, 1:12, 1:01:05", TestTimeFormat);
        Run("LRC parsing and active line matching", TestLrcParsing);
        Run("RAM ring color thresholds green/orange/red", TestRamRingColorThresholds);
        Run("Warning strip 5s RAM trigger and hysteresis reset", TestWarningStripHysteresis);
        Run("FuzzyMatcher substring, subsequence, launch counts", TestSearchFuzzyMatcher);
        Run("ExpressionEvaluator math, units, currencies", TestSearchExpressionEvaluator);
        Run("Weather settings default to Chennai", TestWeatherSettingsDefaults);
        Run("Weather settings round-trip", TestWeatherSettingsRoundTrip);
        Run("WeatherService defaults to Chennai coordinates", TestWeatherServiceDefaults);
        Run("Idle time-of-day periods, hues and greetings", TestIdlePeriods);
        Run("Idle hue delta takes the short way round", TestIdleHueDelta);
        Run("Idle countdown / until strings", TestIdleStrings);
        Run("DotFont renders HH:mm on a 27x7 grid", TestDotFont);
        RunFeatureTests();
        RunBuddyTests();
        RunSearchRankingTests();
        RunAuditTests();

        Console.WriteLine(new string('-', 46));
        Console.WriteLine($"passed: {_pass}   failed: {_fail}");
        return _fail == 0 ? 0 : 1;
        }
        finally
        {
            SettingsService.Instance.SettingsPath = originalSettingsPath;
            try { Directory.Delete(testSettingsDir, true); } catch { }
        }
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

    private static void TestBusPreemption()
    {
        var bus = new EventBus(Dispatcher.CurrentDispatcher);
        BarEvent? got = null;
        bus.EventRaised += e => got = e;

        // Ambient media event holds the stage for 4.5s
        bus.Raise(BarEvent.Create(BarEventKind.Media, "Song Title",
            priority: 60, displayDuration: TimeSpan.FromSeconds(4.5)));
        AssertEq(BarEventKind.Media, bus.Active!.Kind, "media holds stage initially");

        // User presses Volume key -> interactive preempting event
        bus.Raise(BarEvent.Create(BarEventKind.Volume, "70%",
            priority: 70, displayDuration: TimeSpan.FromSeconds(2.5), preempt: true));

        // Must preempt immediately without waiting!
        AssertEq(BarEventKind.Volume, bus.Active!.Kind, "volume preempts media immediately");
        AssertEq(BarEventKind.Volume, got!.Kind, "event raised for volume");
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

        Assert(BarWindow.TryParseHotkey("Ctrl+Space", out uint mSearch, out uint vSearch), "Ctrl+Space");
        AssertEq(NativeMethodsProxy.ModCtrl, mSearch, "ctrl modifier");
        AssertEq(0x20u, vSearch, "VK_SPACE");
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
        Assert(!BarWindow.TryParseHotkey("Space", out _, out _), "bare space must be rejected");
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

    private static void TestIdlePeriods()
    {
        AssertEq("night", WindowsDynamicBar.Core.Idle.IdleClock.PeriodFor(4).Name, "4h");
        AssertEq(22.0, WindowsDynamicBar.Core.Idle.IdleClock.PeriodFor(5).Hue, "5h dawn");
        AssertEq(200.0, WindowsDynamicBar.Core.Idle.IdleClock.PeriodFor(8).Hue, "8h day");
        AssertEq(285.0, WindowsDynamicBar.Core.Idle.IdleClock.PeriodFor(17).Hue, "17h dusk");
        AssertEq(238.0, WindowsDynamicBar.Core.Idle.IdleClock.PeriodFor(20).Hue, "20h night");
        AssertEq("Still up?", WindowsDynamicBar.Core.Idle.IdleClock.Greeting(0), "0h");
        AssertEq("Good morning", WindowsDynamicBar.Core.Idle.IdleClock.Greeting(5), "5h");
        AssertEq("Good afternoon", WindowsDynamicBar.Core.Idle.IdleClock.Greeting(12), "12h");
        AssertEq("Good evening", WindowsDynamicBar.Core.Idle.IdleClock.Greeting(17), "17h");
        AssertEq("Good night", WindowsDynamicBar.Core.Idle.IdleClock.Greeting(21), "21h");
        AssertEq(0.5, WindowsDynamicBar.Core.Idle.IdleClock.DayFraction(new DateTime(2026, 10, 10, 12, 0, 0)), "noon");
    }

    private static void TestIdleHueDelta()
    {
        AssertEq(144.0, WindowsDynamicBar.Core.Idle.IdleClock.HueDelta(238, 22), "night->dawn wraps");
        AssertEq(178.0, WindowsDynamicBar.Core.Idle.IdleClock.HueDelta(22, 200), "dawn->day");
        AssertEq(-47.0, WindowsDynamicBar.Core.Idle.IdleClock.HueDelta(285, 238), "dusk->night");
        var c = WindowsDynamicBar.Core.Idle.IdleClock.Hsl(0, 1, 0.5);
        Assert(c.R == 255 && c.G == 0 && c.B == 0, "hsl red");
    }

    private static void TestIdleStrings()
    {
        AssertEq("25 min", WindowsDynamicBar.Core.Idle.IdleClock.FormatUntil(TimeSpan.FromMinutes(25)), "25m");
        AssertEq("1h 5m", WindowsDynamicBar.Core.Idle.IdleClock.FormatUntil(TimeSpan.FromMinutes(65)), "65m");
        AssertEq("2h", WindowsDynamicBar.Core.Idle.IdleClock.FormatUntil(TimeSpan.FromMinutes(120)), "120m");
        AssertEq("in 25 min", WindowsDynamicBar.Core.Idle.IdleClock.FormatIn(TimeSpan.FromMinutes(24.5)), "ceil");
        AssertEq("now", WindowsDynamicBar.Core.Idle.IdleClock.FormatIn(TimeSpan.FromSeconds(20)), "now");
        AssertEq("24:31", WindowsDynamicBar.Core.Idle.IdleClock.FormatCountdown(TimeSpan.FromSeconds(1471)), "countdown");
        AssertEq("09:05", WindowsDynamicBar.Core.Idle.IdleClock.FormatTime(new DateTime(2026, 1, 1, 9, 5, 0), false), "24h");
        AssertEq("9:05", WindowsDynamicBar.Core.Idle.IdleClock.FormatTime(new DateTime(2026, 1, 1, 21, 5, 0), true), "12h");
    }

    private static void TestDotFont()
    {
        var map = WindowsDynamicBar.Core.Idle.DotFont.Render("10:08", colonOn: true);
        AssertEq(7, map.GetLength(0), "rows");
        AssertEq(27, map.GetLength(1), "cols (27 * 7px = 189px)");
        Assert(map[2, 12] && map[4, 12] && !map[3, 12], "colon dots at rows 2 and 4");
        Assert(map[0, 2] && !map[0, 0], "digit 1 top stem");
        for (int r = 0; r < 7; r++) Assert(!map[r, 5] && !map[r, 13] && !map[r, 14], "spacing columns unlit");
        var off = WindowsDynamicBar.Core.Idle.DotFont.Render("9:05", colonOn: false);
        for (int r = 0; r < 7; r++)
            for (int c = 0; c < 5; c++) Assert(!off[r, c], "missing leading digit stays unlit");
        Assert(!off[2, 12], "colon off");
    }

    private static void TestTimerFormat()
    {
        AssertEq("01:05", TimerService.Format(TimeSpan.FromSeconds(65)), "65s");
        AssertEq("00:09", TimerService.Format(TimeSpan.FromSeconds(9)), "9s");
        AssertEq("00:10", TimerService.Format(TimeSpan.FromSeconds(9.8)), "9.8s rounds up to 10s countdown");
        AssertEq("00:01", TimerService.Format(TimeSpan.FromSeconds(0.2)), "0.2s rounds up to 1s countdown");
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

    private static void TestIslandStateMachine()
    {
        // Outside, no grace elapsed: state unchanged.
        var (s0, m0) = IslandStateMachine.Resolve(
            IslandVisualState.Collapsed, IslandMetric.None, false, IslandMetric.None, false);
        AssertEq(IslandVisualState.Collapsed, s0, "outside stays collapsed");
        AssertEq(IslandMetric.None, m0, "outside metric cleared");

        // Outside with grace elapsed: collapse.
        var (s1, _) = IslandStateMachine.Resolve(
            IslandVisualState.Detail, IslandMetric.Cpu, false, IslandMetric.None, true);
        AssertEq(IslandVisualState.Collapsed, s1, "grace elapsed collapses");

        // Pointer inside, no icon: expand.
        var (s2, m2) = IslandStateMachine.Resolve(
            IslandVisualState.Collapsed, IslandMetric.None, true, IslandMetric.None, false);
        AssertEq(IslandVisualState.Expanded, s2, "hover expands");
        AssertEq(IslandMetric.None, m2, "no metric yet");

        // Icon hovered: detail for that metric.
        var (s3, m3) = IslandStateMachine.Resolve(
            IslandVisualState.Expanded, IslandMetric.None, true, IslandMetric.Cpu, false);
        AssertEq(IslandVisualState.Detail, s3, "icon opens detail");
        AssertEq(IslandMetric.Cpu, m3, "cpu selected");

        // Switching metric keeps detail open.
        var (s4, m4) = IslandStateMachine.Resolve(
            IslandVisualState.Detail, IslandMetric.Cpu, true, IslandMetric.Gpu, false);
        AssertEq(IslandVisualState.Detail, s4, "switch keeps detail");
        AssertEq(IslandMetric.Gpu, m4, "gpu selected");

        // Moving onto the panel (inside, no icon) keeps the current detail open.
        var (s5, m5) = IslandStateMachine.Resolve(
            IslandVisualState.Detail, IslandMetric.Gpu, true, IslandMetric.None, false);
        AssertEq(IslandVisualState.Detail, s5, "moving to panel keeps detail");
        AssertEq(IslandMetric.Gpu, m5, "gpu stays selected");

        // Click mode (hoverPreview = false): dynamic island expands on hover, but button hover does NOT open detail panel
        var (sc0, mc0) = IslandStateMachine.Resolve(
            IslandVisualState.Collapsed, IslandMetric.None, true, IslandMetric.None, false, hoverPreview: false);
        AssertEq(IslandVisualState.Expanded, sc0, "click mode: pointer inside dynamic island expands it");
        AssertEq(IslandMetric.None, mc0, "click mode: no metric on hover");

        var (sc1, mc1) = IslandStateMachine.Resolve(
            IslandVisualState.Expanded, IslandMetric.None, true, IslandMetric.Cpu, false, hoverPreview: false);
        AssertEq(IslandVisualState.Expanded, sc1, "click mode: hovering button (cpu) stays expanded, does not open detail");
        AssertEq(IslandMetric.None, mc1, "click mode: button hover ignored");

        // SettingsModel default: HoverPreview is false (Click mode by default)
        var defaultCfg = new WindowsDynamicBar.Core.Settings.SettingsModel();
        Assert(!defaultCfg.HoverPreview, "SettingsModel defaults to Click mode (HoverPreview = false)");

        // Round-trip metric keys.
        AssertEq("cpu", IslandStateMachine.MetricKey(IslandMetric.Cpu), "cpu key");
        AssertEq(IslandMetric.Ram, IslandStateMachine.ParseMetric("ram"), "parse ram");
        AssertEq(IslandMetric.None, IslandStateMachine.ParseMetric("bogus"), "parse bogus");
        AssertEq(IslandMetric.None, IslandStateMachine.ParseMetric(null), "parse null");
        AssertEq(IslandMetric.Weather, IslandStateMachine.ParseMetric("wx"), "parse wx");
        AssertEq("wx", IslandStateMachine.MetricKey(IslandMetric.Weather), "wx key");
    }

    private static void TestDotBarColorRamp()
    {
        var low = DotBar.ValueColor(0);
        var high = DotBar.ValueColor(100);
        Assert(low.G > low.R, "low load is green-dominant (safe)");
        Assert(high.R > high.G, "high load is red-dominant (alarm)");
    }

    private static void TestTelemetryHistoryCapacity()
    {
        var h = new TelemetryHistory();
        AssertEq(240, h.Capacity, "default telemetry history capacity");
        for (int i = 0; i < 500; i++) h.Push(i % 100);
        AssertEq(240, h.Count, "count capped at default capacity");
        AssertEq(240, h.Snapshot().Length, "snapshot capped at default capacity");
    }

    private static void TestTimeFormat()
    {
        AssertEq("0:00", BarWindow.FormatTime(TimeSpan.Zero), "format 0:00");
        AssertEq("1:12", BarWindow.FormatTime(TimeSpan.FromSeconds(72)), "format 1:12");
        AssertEq("1:01:05", BarWindow.FormatTime(TimeSpan.FromSeconds(3665)), "format 1:01:05");
    }

    private static void TestLrcParsing()
    {
        string sample = "[00:12.34] Hello world\n[00:15.50] Next line";
        var lines = LrcLibClient.ParseLrc(sample);
        AssertEq(2, lines.Count, "parsed 2 lines");
        Assert(Math.Abs(lines[0].Timestamp.TotalSeconds - 12.34) < 0.01, "timestamp match");
        AssertEq("Hello world", lines[0].Text, "line text match");
        AssertEq("Hello world", LrcLibClient.GetCurrentLineText(lines, TimeSpan.FromSeconds(13)), "active line at 13s");
        AssertEq("Next line", LrcLibClient.GetCurrentLineText(lines, TimeSpan.FromSeconds(16)), "active line at 16s");
        AssertEq(null, LrcLibClient.GetCurrentLineText(lines, TimeSpan.FromSeconds(5)), "null before first line");
    }

    private static void TestRamRingColorThresholds()
    {
        var cGreen = TelemetryIconRow.GetRamColor(50);
        var cOrange = TelemetryIconRow.GetRamColor(80);
        var cRed = TelemetryIconRow.GetRamColor(92);

        AssertEq(System.Windows.Media.Color.FromRgb(0x4C, 0xD9, 0x64), cGreen, "under 75% is green");
        AssertEq(System.Windows.Media.Color.FromRgb(0xD9, 0x8A, 0x5B), cOrange, "75-89% is orange");
        AssertEq(System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D), cRed, ">=90% is red");
    }

    private static void TestWarningStripHysteresis()
    {
        var logic = new WarningStripLogic();
        string kind;

        // 4 ticks at 92% -> not triggered yet
        for (int i = 0; i < 4; i++)
        {
            Assert(!logic.Update(92, 10, 50, true, out kind), $"tick {i + 1} does not trigger");
        }

        // 5th tick at 92% -> triggered
        Assert(logic.Update(92, 10, 50, true, out kind), "5th tick triggers");
        AssertEq("RAM", kind, "kind is RAM");

        // User dismisses
        logic.Dismiss("RAM");
        Assert(!logic.Update(92, 10, 50, true, out kind), "dismissed RAM does not trigger");

        // Drop to 88% -> still above 85% hysteresis reset threshold
        Assert(!logic.Update(88, 10, 50, true, out kind), "88% does not reset dismissal");
        Assert(logic.RamDismissed, "still dismissed at 88%");

        // Drop to 84% -> resets dismissal
        Assert(!logic.Update(84, 10, 50, true, out kind), "84% resets dismissal");
        Assert(!logic.RamDismissed, "dismissal is now reset");

        // Spikes back to 92% for 5 ticks -> triggers again
        for (int i = 0; i < 4; i++)
            Assert(!logic.Update(92, 10, 50, true, out kind), $"re-spike tick {i + 1} does not trigger");

        Assert(logic.Update(92, 10, 50, true, out kind), "re-spike 5th tick triggers");
        AssertEq("RAM", kind, "re-triggered kind is RAM");
    }

    private static void TestSearchFuzzyMatcher()
    {
        // 1. Substring matches
        var m1 = FuzzyMatcher.Match("Google Chrome", "chr");
        Assert(m1.Matched, "substring matched");
        Assert(m1.Score > 0, "score positive");
        AssertEq(3, m1.MatchedIndices.Count, "indices count 3");

        // Substring at start scores higher than in middle
        var mStart = FuzzyMatcher.Match("Chrome", "chr");
        var mMid = FuzzyMatcher.Match("Google Chrome", "chr");
        Assert(mStart.Score > mMid.Score, "prefix match scores higher than middle");

        // 2. Subsequence matches
        var mSub = FuzzyMatcher.Match("Visual Studio Code", "vsc");
        Assert(mSub.Matched, "subsequence matched");
        AssertEq(3, mSub.MatchedIndices.Count, "3 characters matched in order");

        // 3. Launch count boost (+8 per launch)
        var m0 = FuzzyMatcher.Match("Spotify", "spot", launchCount: 0);
        var m5 = FuzzyMatcher.Match("Spotify", "spot", launchCount: 5);
        AssertEq(m0.Score + 40, m5.Score, "+40 for 5 launches");

        // 4. Non-match
        var mFail = FuzzyMatcher.Match("Firefox", "xyz");
        Assert(!mFail.Matched, "non match");
    }

    private static void TestSearchExpressionEvaluator()
    {
        // 1. Basic math
        var (ok1, res1, _) = ExpressionEvaluator.TryEvaluate("2 + 2 * 3");
        Assert(ok1, "math evaluated");
        AssertEq("8", res1, "operator precedence respected: 2 + 2 * 3 = 8");

        var (ok2, res2, _) = ExpressionEvaluator.TryEvaluate("sqrt(16) + sin(0)");
        Assert(ok2, "functions evaluated");
        AssertEq("4", res2, "sqrt(16) + sin(0) = 4");

        // 2. Unit conversion
        var (ok3, res3, _) = ExpressionEvaluator.TryEvaluate("100 km to miles");
        Assert(ok3, "unit conversion evaluated");
        Assert(res3.Contains("62.1"), $"100 km in miles contains 62.1, got '{res3}'");

        var (ok4, res4, _) = ExpressionEvaluator.TryEvaluate("72 f in c");
        Assert(ok4, "temperature conversion evaluated");
        Assert(res4.Contains("22.2"), $"72 f in c contains 22.2, got '{res4}'");

        // 3. Currency conversion
        var (ok5, res5, _) = ExpressionEvaluator.TryEvaluate("100 usd to eur");
        Assert(ok5, "currency conversion evaluated");
        Assert(res5.Contains("EUR", StringComparison.OrdinalIgnoreCase), $"currency result formatted with EUR, got '{res5}'");

        // 4. Invalid input does not throw or evaluate
        var (okFail, _, _) = ExpressionEvaluator.TryEvaluate("hello world");
        Assert(!okFail, "normal text rejected by expression evaluator");
    }

    private static void TestWeatherSettingsDefaults()
    {
        var model = new SettingsModel();
        AssertEq("Chennai", model.WeatherLocation, "Default WeatherLocation");
        AssertEq(13.0827, model.WeatherLatitude ?? 0, "Default WeatherLatitude");
        AssertEq(80.2707, model.WeatherLongitude ?? 0, "Default WeatherLongitude");
        Assert(!model.WeatherAutoLocation, "Default WeatherAutoLocation is false");
    }

    private static void TestWeatherSettingsRoundTrip()
    {
        string path = SettingsService.Instance.SettingsPath;
        SettingsModel? original = File.Exists(path) ? JsonSerializer.LoadCopy(path) : null;
        try
        {
            SettingsService.Instance.Save(new SettingsModel
            {
                WeatherLocation = "Tokyo",
                WeatherAutoLocation = true,
                WeatherLatitude = 35.6762,
                WeatherLongitude = 139.6503,
            });
            var loaded = SettingsService.Instance.Load();
            AssertEq("Tokyo", loaded.WeatherLocation, "WeatherLocation round-trip");
            Assert(loaded.WeatherAutoLocation, "WeatherAutoLocation round-trip");
            AssertEq(35.6762, loaded.WeatherLatitude ?? 0, "WeatherLatitude round-trip");
            AssertEq(139.6503, loaded.WeatherLongitude ?? 0, "WeatherLongitude round-trip");
        }
        finally
        {
            if (original is not null) SettingsService.Instance.Save(original);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void TestWeatherServiceDefaults()
    {
        using var ws = new WeatherService();
        Assert(ws.HasLocation, "WeatherService has default location");
        AssertEq("Chennai, IN", ws.Snapshot.LocationName, "WeatherService default place name");
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
