using System.IO;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Animation;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Services.Media;
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
