using System.IO;
using WindowsDynamicBar.Core.Clipboard;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Services.Media;

namespace WindowsDynamicBar.Tests;

/// <summary>Tests for the polish / feature phases (kept apart from the original suite).</summary>
internal static partial class Program
{
    private static void RunFeatureTests()
    {
        Run("LoadAverages: windows appear only when fully covered", TestLoadAveragesCoverage);
        Run("LoadAverages: ring buffer stays bounded", TestLoadAveragesBounded);
        Run("TelemetryFormat: short GPU names and temperatures", TestGpuNamesAndTemps);
        Run("Media poll: only meaningful changes are forwarded", TestMediaMeaningfulChange);
        Run("Clipboard: limit, pins outside the limit, 200 copies stay bounded", TestClipLimit);
        Run("Clipboard: duplicates skipped / moved to top", TestClipDuplicates);
        Run("Clipboard: full image only for the newest 3 images", TestClipImages);
        Run("Clipboard: filter, links, one-line preview, ages, auto-clear", TestClipHelpers);
        Run("Clipboard: DPAPI round-trip (CurrentUser)", TestClipDpapi);
        Run("Island layout: parse, dedupe, fallback", TestIslandLayout);
        Run("Timer command parsing (timer 10 min, 90s, 1h30m, 1:30)", TestTimerCommand);
        Run("TimerService counts down in real time (regression)", TestTimerAccuracy);
        Run("TimerService end time persisted and restored", TestTimerPersistence);
        Run("Battery warning: 5 s at <=15% on battery, released by charger or >20%", TestBatteryWarning);
        Run("Battery glyphs: levels and charging variants", TestBatteryGlyphs);
        Run("LruCache never exceeds its limit and evicts least recently used", TestLruCache);
        Run("Settings: validation clamps bad values and broken hotkeys", TestSettingsValidation);
        Run("Settings: corrupt file is moved aside and defaults load", TestSettingsCorruption);
        Run("Settings: v1 file migrates Reduce Motion", TestSettingsMigration);
        Run("Hotkeys: format, compare, duplicates", TestHotkeyHelpers);
        Run("SemVer comparison for updates", TestSemVer);
    }

    private static string ScratchDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"wdb-settings-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TestSettingsValidation()
    {
        var m = new WindowsDynamicBar.Core.Settings.SettingsModel
        {
            Opacity = 7, BarWidth = double.NaN, ClipboardLimit = 999, ClipboardAutoClearHours = 5,
            WarnBatteryPercent = 1, MonitorIndex = -9, HotkeySearch = "Ctrl+Banana", HotkeyMedia = "",
            AccentColor = "blue", ClockFormat = (WindowsDynamicBar.Core.Settings.ClockFormat)42,
        };
        WindowsDynamicBar.Core.Settings.SettingsService.Validate(m);
        AssertEq(1.0, m.Opacity, "opacity clamped");
        AssertEq(260.0, m.BarWidth, "NaN width -> default");
        AssertEq(50, m.ClipboardLimit, "clipboard limit clamped");
        AssertEq(0, m.ClipboardAutoClearHours, "auto-clear must be 0/1/6/24");
        AssertEq(5, m.WarnBatteryPercent, "battery level clamped");
        AssertEq(-2, m.MonitorIndex, "monitor index clamped");
        AssertEq("Ctrl+Space", m.HotkeySearch, "broken hotkey -> default");
        AssertEq("", m.HotkeyMedia, "empty hotkey = disabled, kept");
        AssertEq("#FF0A84FF", m.AccentColor, "bad colour -> default");
        AssertEq(WindowsDynamicBar.Core.Settings.ClockFormat.System, m.ClockFormat, "unknown enum -> default");
    }

    private static void TestSettingsCorruption()
    {
        string dir = ScratchDir();
        string path = Path.Combine(dir, "settings.json");
        var svc = WindowsDynamicBar.Core.Settings.SettingsService.Instance;
        try
        {
            File.WriteAllText(path, "{ this is not json ]");
            var loaded = svc.Load(path);
            AssertEq(new WindowsDynamicBar.Core.Settings.SettingsModel().HotkeySearch, loaded.HotkeySearch, "defaults loaded");
            Assert(svc.LoadProblem is { Length: > 0 }, "one-time message prepared");
            Assert(!File.Exists(path), "damaged file moved away");
            Assert(Directory.GetFiles(dir, "settings.bad-*.json").Length == 1, "backup settings.bad-<date>.json kept");
            svc.ClearLoadProblem();
            Assert(svc.LoadProblem is null, "message shown once");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            svc.Load();   // back to the real file for the rest of the run
        }
    }

    private static void TestSettingsMigration()
    {
        string dir = ScratchDir();
        string path = Path.Combine(dir, "settings.json");
        var svc = WindowsDynamicBar.Core.Settings.SettingsService.Instance;
        try
        {
            File.WriteAllText(path, "{ \"ReduceMotion\": true, \"Opacity\": 0.8 }");   // v1 file: no SettingsVersion
            var m = svc.Load(path);
            AssertEq(WindowsDynamicBar.Core.Settings.ReduceMotionMode.Always, m.ReduceMotionMode, "v1 Reduce Motion -> Always");
            AssertEq(WindowsDynamicBar.Core.Settings.SettingsService.CurrentVersion, m.SettingsVersion, "version bumped");
            AssertEq(0.8, m.Opacity, "other values kept");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            svc.Load();
        }
    }

    private static void TestHotkeyHelpers()
    {
        AssertEq("Ctrl+Alt+V", WindowsDynamicBar.Core.Settings.Hotkeys.Format(0x2 | 0x1, 'V'), "format order");
        AssertEq("Ctrl+F5", WindowsDynamicBar.Core.Settings.Hotkeys.Format(0x2, 0x74), "F-key");
        Assert(WindowsDynamicBar.Core.Settings.Hotkeys.Format(0, 0x20) is null, "Space needs a modifier");
        Assert(WindowsDynamicBar.Core.Settings.Hotkeys.Format(0x2, 0xBA) is null, "unsupported key rejected");
        Assert(WindowsDynamicBar.Core.Settings.Hotkeys.Same("alt+ctrl+v", "Ctrl+Alt+V"), "same combo, other spelling");
        var s = new WindowsDynamicBar.Core.Settings.SettingsModel { HotkeyClipboard = "Ctrl+Space" };
        var d = WindowsDynamicBar.Core.Settings.Hotkeys.DuplicatesOf(s, "Clipboard", "Ctrl+Space");
        Assert(d.Count == 1 && d[0] == "Search", "duplicate with Search detected");
    }

    private static void TestSemVer()
    {
        static (int, int, int, string) V(string t) => WindowsDynamicBar.Services.Updates.SemVer.TryParse(t, out var v) ? v : throw new Exception(t);
        Assert(WindowsDynamicBar.Services.Updates.SemVer.Compare(V("1.2.0"), V("1.1.9")) > 0, "minor bump");
        Assert(WindowsDynamicBar.Services.Updates.SemVer.Compare(V("v1.2.0"), V("1.2.0")) == 0, "v prefix");
        Assert(WindowsDynamicBar.Services.Updates.SemVer.Compare(V("1.2.0-beta"), V("1.2.0")) < 0, "prerelease is lower");
        Assert(WindowsDynamicBar.Services.Updates.SemVer.Compare(V("1.10.0"), V("1.9.0")) > 0, "numeric, not text");
        Assert(!WindowsDynamicBar.Services.Updates.SemVer.TryParse("one.two", out _), "garbage rejected");
    }

    private static void TestLruCache()
    {
        var evicted = new List<int>();
        var lru = new WindowsDynamicBar.Core.Search.LruCache<string, int>(64);
        lru.Evicted += v => evicted.Add(v);
        for (int i = 0; i < 1000; i++)
        {
            lru.Set($"k{i}", i);
            Assert(lru.Count <= 64, "never above capacity");
        }
        AssertEq(64, lru.Count, "full at capacity");
        AssertEq(1000 - 64, evicted.Count, "every overflow evicted exactly once (handles can be released)");
        Assert(!lru.TryGet("k0", out _), "oldest gone");
        Assert(lru.TryGet("k999", out int v999) && v999 == 999, "newest kept");

        var small = new WindowsDynamicBar.Core.Search.LruCache<string, int>(2);
        small.Set("a", 1);
        small.Set("b", 2);
        small.TryGet("a", out _);   // touch a -> b is now least recently used
        small.Set("c", 3);
        Assert(small.TryGet("a", out _) && !small.TryGet("b", out _) && small.TryGet("c", out _), "LRU order respected");
        small.Set("a", 10);
        Assert(small.TryGet("a", out int a2) && a2 == 10 && small.Count == 2, "update keeps one entry");
    }

    private static void TestBatteryWarning()
    {
        var w = new WarningStripLogic();
        string kind;
        for (int i = 0; i < 4; i++) Assert(!w.Update(-1, -1, 14, false, out kind), $"tick {i + 1}: not yet (needs 5 s)");
        Assert(w.Update(-1, -1, 14, false, out kind) && kind == "Battery", "5th second at 14% triggers");
        Assert(w.Update(-1, -1, 18, false, out kind), "18% keeps it (hysteresis up to 20%)");
        Assert(!w.Update(-1, -1, 21, false, out kind), "above 20% releases it");
        for (int i = 0; i < 5; i++) w.Update(-1, -1, 10, false, out kind);
        Assert(w.Update(-1, -1, 10, false, out kind), "triggers again");
        Assert(!w.Update(-1, -1, 10, true, out kind), "charging releases it at once");
        for (int i = 0; i < 10; i++) Assert(!w.Update(-1, -1, 10, true, out kind), "never while charging");
        Assert(!w.Update(-1, -1, -1, false, out kind), "unknown battery never triggers");
        var custom = new WarningStripLogic { BatteryPercent = 50 };
        for (int i = 0; i < 5; i++) custom.Update(-1, -1, 40, false, out kind);
        Assert(custom.Update(-1, -1, 40, false, out kind), "threshold comes from the setting");
    }

    private static void TestBatteryGlyphs()
    {
        AssertEq("\uE850", WindowsDynamicBar.UI.BarWindow.BatteryGlyph(2, false), "empty");
        AssertEq("\uE855", WindowsDynamicBar.UI.BarWindow.BatteryGlyph(50, false), "half");
        AssertEq("\uE83F", WindowsDynamicBar.UI.BarWindow.BatteryGlyph(100, false), "full");
        AssertEq("\uE83E", WindowsDynamicBar.UI.BarWindow.BatteryGlyph(98, true), "full + bolt");
        AssertEq("\uEA93", WindowsDynamicBar.UI.BarWindow.BatteryGlyph(91, true), "90% + bolt");
        AssertEq("\uE85F", WindowsDynamicBar.UI.BarWindow.BatteryGlyph(50, true), "half + bolt");
    }

    private static void TestTimerCommand()
    {
        static TimeSpan P(string s) => WindowsDynamicBar.Core.Timers.TimerCommand.TryParseDuration(s, out var d) ? d : TimeSpan.MinValue;
        AssertEq(TimeSpan.FromMinutes(10), P("10 min"), "10 min");
        AssertEq(TimeSpan.FromSeconds(90), P("90s"), "90s");
        AssertEq(TimeSpan.FromMinutes(90), P("1h30m"), "1h30m");
        AssertEq(TimeSpan.FromMinutes(90), P("1h 30m"), "1h 30m");
        AssertEq(TimeSpan.FromSeconds(90), P("1:30"), "m:ss");
        AssertEq(TimeSpan.FromMinutes(25), P("25"), "bare number = minutes");
        AssertEq(TimeSpan.MinValue, P("10 min please"), "trailing words rejected");
        AssertEq(TimeSpan.MinValue, P("0"), "zero rejected");
        AssertEq(TimeSpan.MinValue, P("48h"), "over 24 h rejected");
        Assert(WindowsDynamicBar.Core.Timers.TimerCommand.TryParseSearch("timer 10 min", out var t) && t == TimeSpan.FromMinutes(10), "search 'timer 10 min'");
        Assert(!WindowsDynamicBar.Core.Timers.TimerCommand.TryParseSearch("timeout", out _), "'timeout' is not a timer command");
        AssertEq("10:00", WindowsDynamicBar.Core.Timers.TimerCommand.Format(TimeSpan.FromMinutes(10)), "format mm:ss");
        AssertEq("1:30:00", WindowsDynamicBar.Core.Timers.TimerCommand.Format(TimeSpan.FromMinutes(90)), "format h:mm:ss");
        AssertEq("09:58", WindowsDynamicBar.Core.Timers.TimerCommand.DotText(TimeSpan.FromSeconds(597.2)), "dot text rounds up");
        AssertEq("1:30", WindowsDynamicBar.Core.Timers.TimerCommand.DotText(TimeSpan.FromMinutes(90)), "dot text h:mm");
    }

    private static string UseScratchState()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wdb-state-test-{Guid.NewGuid():N}.json");
        WindowsDynamicBar.Core.Settings.RuntimeState.UseForTests(path);
        return path;
    }

    private static void TestTimerAccuracy()
    {
        string path = UseScratchState();
        try
        {
            var bus = new WindowsDynamicBar.Core.Events.EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            using var timer = new WindowsDynamicBar.Services.Timers.TimerService(bus, WindowsDynamicBar.Core.Settings.SettingsService.Instance);
            timer.Start(TimeSpan.FromSeconds(3), "t");
            Pump(1000);
            var info = timer.Current;
            AssertEq(WindowsDynamicBar.Services.Timers.TimerState.Running, info.State, "still running after 1 s");
            Assert(Math.Abs(info.Remaining.TotalSeconds - 2.0) < 0.35, $"about 2 s left after 1 s (was {info.Remaining.TotalSeconds:F2})");
            timer.Cancel();
        }
        finally { File.Delete(path); }
    }

    private static void TestTimerPersistence()
    {
        string path = UseScratchState();
        try
        {
            var bus = new WindowsDynamicBar.Core.Events.EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            using (var timer = new WindowsDynamicBar.Services.Timers.TimerService(bus, WindowsDynamicBar.Core.Settings.SettingsService.Instance))
            {
                timer.Start(TimeSpan.FromMinutes(10), "Tea");
                var st = WindowsDynamicBar.Core.Settings.RuntimeState.Instance.Current;
                Assert(st.TimerEndUtc is DateTime end && Math.Abs((end - DateTime.UtcNow).TotalMinutes - 10) < 0.05, "end time written");
            }
            using (var restored = new WindowsDynamicBar.Services.Timers.TimerService(bus, WindowsDynamicBar.Core.Settings.SettingsService.Instance))
            {
                restored.RestoreFromState();     // "app restart"
                var info = restored.Current;
                AssertEq(WindowsDynamicBar.Services.Timers.TimerState.Running, info.State, "running again after restart");
                AssertEq("Tea", info.Label, "label restored");
                Assert(info.Remaining > TimeSpan.FromMinutes(9.9), "remaining time continues from the stored end");
                restored.Cancel();
                Assert(WindowsDynamicBar.Core.Settings.RuntimeState.Instance.Current.TimerEndUtc is null, "cancel clears the end time");
            }
        }
        finally { File.Delete(path); }
    }

    private static ClipItem Txt(ClipboardHistory h, string text, DateTime? at = null) => new()
    {
        Id = h.NewId(), Kind = ClipboardHistory.IsLink(text) ? ClipKind.Link : ClipKind.Text, Text = text,
        Hash = ClipboardHistory.HashText("t", text), CreatedUtc = at ?? DateTime.UtcNow,
    };

    private static void TestClipLimit()
    {
        var h = new ClipboardHistory { Limit = 10 };
        for (int i = 0; i < 200; i++) h.Add(Txt(h, $"item {i}"));
        AssertEq(10, h.Count, "200 copies -> 10 kept");
        AssertEq("item 199", h.Snapshot()[0].Text, "newest first");
        var pinned = h.Snapshot()[9];
        h.SetPinned(pinned.Id, true);
        for (int i = 0; i < 50; i++) h.Add(Txt(h, $"more {i}"));
        AssertEq(11, h.Count, "pinned item kept on top of the 10-item limit");
        Assert(h.Snapshot().Any(i => i.Id == pinned.Id && i.Pinned), "pinned item survived");
        AssertEq(10, h.UnpinnedCount, "unpinned count equals limit");
        h.Limit = 500;
        AssertEq(ClipboardHistory.MaxLimit, h.Limit, "limit clamps to 50");
        h.ClearUnpinned();
        AssertEq(1, h.Count, "clear all keeps pinned items");
    }

    private static void TestClipDuplicates()
    {
        var h = new ClipboardHistory();
        Assert(h.Add(Txt(h, "a")), "first add");
        Assert(!h.Add(Txt(h, "a")), "consecutive duplicate skipped");
        h.Add(Txt(h, "b"));
        h.Add(Txt(h, "c"));
        Assert(h.Add(Txt(h, "a")), "older duplicate re-added");
        AssertEq(3, h.Count, "no second copy of 'a'");
        AssertEq("a", h.Snapshot()[0].Text, "'a' moved to the top");
    }

    private static void TestClipImages()
    {
        var h = new ClipboardHistory { Limit = 10 };
        for (int i = 0; i < 5; i++)
        {
            byte[] png = { (byte)i, 1, 2, 3 };
            h.Add(new ClipItem
            {
                Id = h.NewId(), Kind = ClipKind.Image, Hash = ClipboardHistory.Hash("i", png),
                FullPng = png, ThumbPng = png, ImageWidth = 10, ImageHeight = 10, CreatedUtc = DateTime.UtcNow,
            });
        }
        var items = h.Snapshot();
        AssertEq(3, items.Count(i => i.FullPng is not null), "only the newest 3 keep the full image");
        Assert(items.Take(3).All(i => i.CanRestore) && items.Skip(3).All(i => !i.CanRestore), "older images are thumbnail-only");
        Assert(items.All(i => i.ThumbPng is not null), "every image keeps its thumbnail");
    }

    private static void TestClipHelpers()
    {
        var h = new ClipboardHistory();
        var now = DateTime.UtcNow;
        h.Add(Txt(h, "old note", now.AddHours(-3)));
        h.Add(Txt(h, "https://example.com/x", now));
        h.Add(new ClipItem { Id = h.NewId(), Kind = ClipKind.Files, Files = new[] { @"C:\work\report.pdf" },
            Hash = "f:1", CreatedUtc = now });
        AssertEq(1, ClipboardHistory.Filter(h.Snapshot(), "REPORT").Count, "filter matches file names, case-insensitive");
        AssertEq(3, ClipboardHistory.Filter(h.Snapshot(), "  ").Count, "empty query = everything");
        Assert(ClipboardHistory.IsLink("https://example.com/x"), "url is a link");
        Assert(!ClipboardHistory.IsLink("see https://example.com"), "sentence is not a link");
        AssertEq("a b c", ClipboardHistory.OneLine("  a\r\n  b\tc ", 60), "whitespace collapsed");
        AssertEq(60, ClipboardHistory.OneLine(new string('x', 500), 60).Length, "preview capped at 60 chars");
        AssertEq("now", ClipboardHistory.Age(TimeSpan.FromSeconds(20)), "age now");
        AssertEq("3 min", ClipboardHistory.Age(TimeSpan.FromMinutes(3.5)), "age minutes");
        AssertEq("2 h", ClipboardHistory.Age(TimeSpan.FromHours(2.2)), "age hours");
        AssertEq(1, h.AutoClear(TimeSpan.FromHours(1), now), "auto-clear removes only items older than 1 h");
        AssertEq(2, h.Count, "two recent items left");
    }

    private static void TestClipDpapi()
    {
        byte[] plain = System.Text.Encoding.UTF8.GetBytes("secret clipboard text");
        byte[] cipher = WindowsDynamicBar.Services.Clipboard.Dpapi.Protect(plain);
        Assert(!System.Text.Encoding.UTF8.GetString(cipher).Contains("secret"), "cipher text is not plain");
        AssertEq("secret clipboard text",
            System.Text.Encoding.UTF8.GetString(WindowsDynamicBar.Services.Clipboard.Dpapi.Unprotect(cipher)), "round trip");
    }

    private static void TestIslandLayout()
    {
        var (p1, p2) = IslandLayout.FromSettings("cpu, GPU,ram,cpu,bogus", "ram,clip,tmr");
        AssertEq("cpu,gpu,ram", IslandLayout.Format(p1), "page 1 parsed, duplicates/unknown dropped");
        AssertEq("clip,tmr", IslandLayout.Format(p2), "metric already on page 1 removed from page 2");
        var (d1, d2) = IslandLayout.FromSettings("", "");
        AssertEq(IslandLayout.DefaultPage1Keys, IslandLayout.Format(d1), "empty layout falls back to defaults");
        AssertEq(IslandLayout.DefaultPage2Keys, IslandLayout.Format(d2), "default page 2");
        AssertEq(6, IslandLayout.Parse("cpu,gpu,ram,mir,net,wx,clip,tog").Count, "max 6 per page");
    }

    private static void TestLoadAveragesCoverage()
    {
        var la = new LoadAverages(TimeSpan.FromSeconds(5));   // 12 samples per minute
        for (int i = 0; i < 11; i++) la.Push(50);
        var a = la.Averages();
        Assert(a.M1 is null && a.M5 is null && a.M15 is null, "no value before one full minute");
        la.Push(50);
        a = la.Averages();
        AssertEq(50.0, a.M1, "1-min average after 12 samples");
        Assert(a.M5 is null, "5-min still unknown");
        for (int i = 0; i < 48; i++) la.Push(i < 36 ? 0 : 100);   // last 12 = 100, earlier = 0/50
        a = la.Averages();
        AssertEq(100.0, a.M1, "1-min average uses only the newest 12 samples");
        Assert(a.M5 is double m5 && Math.Abs(m5 - (12 * 50 + 36 * 0 + 12 * 100) / 60.0) < 1e-9, "5-min average");
    }

    private static void TestLoadAveragesBounded()
    {
        var la = new LoadAverages(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 10_000; i++) la.Push(i % 2 == 0 ? 0 : 100);
        AssertEq(180, la.Capacity, "15 min of 5 s samples");
        AssertEq(180, la.Count, "count never exceeds capacity");
        AssertEq(50.0, la.Averages().M15, "15-min average of alternating samples");
        la.Push(500);   // out-of-range input is clamped
        Assert(la.Averages().M1 <= 100, "values are clamped to 0..100");
    }

    private static void TestGpuNamesAndTemps()
    {
        AssertEq("RTX 3050 Laptop GPU", TelemetryFormat.ShortGpuName("NVIDIA GeForce RTX 3050 Laptop GPU"), "nvidia");
        AssertEq("UHD Graphics", TelemetryFormat.ShortGpuName("Intel(R) UHD Graphics"), "intel");
        AssertEq("RX 7800 XT", TelemetryFormat.ShortGpuName("AMD Radeon RX 7800 XT"), "amd");
        AssertEq("", TelemetryFormat.ShortGpuName(null), "unknown name stays empty (no invented label)");
        AssertEq("47°C", TelemetryFormat.Temperature(46.85, false), "celsius");
        AssertEq("116°F", TelemetryFormat.Temperature(46.85, true), "fahrenheit");
    }

    private static void TestMediaMeaningfulChange()
    {
        var t0 = DateTimeOffset.UtcNow;
        var a = new MediaInfo("Song", "Artist", "Album", "app", true, TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(3), null, 0.05, LastUpdatedTime: t0);
        Assert(MediaService.IsMeaningfulChange(null, a), "first snapshot is a change");
        Assert(!MediaService.IsMeaningfulChange(a, a), "identical snapshot is not");
        // Same track, position advanced by exactly the elapsed time: extrapolated by the UI, not a change.
        var b = a with { Position = TimeSpan.FromSeconds(12), LastUpdatedTime = t0.AddSeconds(2) };
        Assert(!MediaService.IsMeaningfulChange(a, b), "normal playback progress is not a change");
        var seek = a with { Position = TimeSpan.FromSeconds(90) };
        Assert(MediaService.IsMeaningfulChange(a, seek), "a seek is a change");
        Assert(MediaService.IsMeaningfulChange(a, a with { IsPlaying = false }), "pause is a change");
        Assert(MediaService.IsMeaningfulChange(a, a with { Title = "Other" }), "new title is a change");
        Assert(MediaService.IsMeaningfulChange(a, a with { ArtworkBytes = new byte[] { 1 } }), "new artwork is a change");
    }
}
