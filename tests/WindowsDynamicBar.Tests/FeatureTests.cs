using System.Collections.Generic;
using System.IO;
using System.Threading;
using WindowsDynamicBar.Core.Clipboard;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Net;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Interop;
using WindowsDynamicBar.Services.Audio;
using WindowsDynamicBar.Services.Media;
using WindowsDynamicBar.Services.Settings;
using WindowsDynamicBar.Services.SystemInfo;
using WindowsDynamicBar.UI.Media;
using Windows.Storage.Streams;

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
        Run("TimerService pause, resume and cancel behavior", TestTimerPauseResume);
        Run("TimerService end time persisted and restored", TestTimerPersistence);
        Run("Battery warning: 5 s at <=15% on battery, released by charger or >20%", TestBatteryWarning);
        Run("Battery glyphs: levels and charging variants", TestBatteryGlyphs);
        Run("BatteryInfo: desktop without battery, unknown flags, and remaining time parsing", TestBatteryInfoFromPowerStatus);
        Run("BatteryService: alert transitions and routine drift suppression", TestBatteryAlertTransitions);
        Run("BatteryService: alert publishing, priorities, durations, and FeatureBattery setting", TestBatteryAlertPublishing);
        Run("SharedHttp: client configuration, pooling, and deadline cancellation", TestSharedHttpConfiguration);
        Run("LruCache never exceeds its limit and evicts least recently used", TestLruCache);
        Run("Settings: validation clamps bad values and broken hotkeys", TestSettingsValidation);
        Run("Settings: corrupt file is moved aside and defaults load", TestSettingsCorruption);
        Run("Settings: persistent read failure preserves file on disk", TestSettingsLoad_PersistentReadFailure_PreservesFile);
        Run("Settings: transient read failure recovers via retry", TestSettingsLoad_TransientReadFailure_Recovers);
        Run("Settings: save failure preserves previous valid file", TestSettingsSave_Failure_PreservesPreviousValidFile);
        Run("Settings: concurrent readers and atomic save coexist safely", TestSettingsSave_ConcurrentReaders_DoesNotThrow);
        Run("Settings: v1 file migrates Reduce Motion", TestSettingsMigration);
        Run("Hotkeys: format, compare, duplicates", TestHotkeyHelpers);
        Run("SemVer comparison for updates", TestSemVer);
        Run("FirstRunDone: property alias keeps backward compatibility", TestFirstRunDoneAlias);
        Run("WindowSwitcher: fuzzy title and process matching", TestWindowSwitcherMatching);
        Run("Fullscreen: invalid handles return false safely", TestFullscreenCheck);
        Run("MediaService: concurrent StartAsync calls are idempotent without duplicate requests", TestMediaLifecycle_ConcurrentStart_IsIdempotent);
        Run("MediaService: Stop during in-flight startup cancels startup and does not start", TestMediaLifecycle_StopDuringStartup_CancelsAndDoesNotStart);
        Run("MediaService: Dispose during in-flight startup cancels startup and suppresses MediaChanged", TestMediaLifecycle_DisposeDuringStartup_CancelsAndSuppressesMediaChanged);
        Run("MediaService: runtime Stop emits ClearToNoMedia", TestMediaLifecycle_RuntimeStop_EmitsNoMedia);
        Run("MediaService: Dispose marks service disposed and suppresses MediaChanged", TestMediaLifecycle_Dispose_SuppressesMediaChanged);
        Run("MediaDisc: Unloaded stops spin timer, clears tweens, and suppresses SyncSpin until Loaded", TestMediaDisc_Lifecycle);
        Run("MediaService: TryGetArtworkAsync bounds timeout, reads bytes, and disposes streams safely", TestMediaArtwork_BoundedTimeout_RecoversAndDisposesStream);
        Run("MediaService: Stop vs startup completion race keeps service stopped without attached handlers", TestMediaLifecycle_StopVsStartupCompletion_RaceCondition);
        Run("MediaService: pending artwork async operation bounds timeout and disposes resources safely", TestMediaArtwork_PendingAsyncOperation_SafeDisposal);
        Run("VolumeService: initialization handles success and failure gracefully", TestVolumeService_Initialization_FailureAndSuccess);
        Run("VolumeService: SetLevel and ToggleMute update state and controller", TestVolumeService_SetLevelAndToggleMute);
        Run("VolumeService: notification deduplication suppresses duplicates within 500ms", TestVolumeService_NotificationDeduplication);
        Run("VolumeService: event publishing formats metadata and respects FeatureVolume toggle", TestVolumeService_EventPublishing_MetadataAndFeatureToggle);
        Run("VolumeService: default device change updates device name and publishes event", TestVolumeService_DefaultDeviceChanged);
        Run("VolumeService: Dispose shuts down controller and suppresses late callbacks", TestVolumeService_Dispose_SuppressesLateCallbacks);
        Run("VolumeService: concurrent access across threads is thread-safe", TestVolumeService_ConcurrentAccess);
        Run("CoreAudio: Shutdown versus AttachDefaultEndpoint race terminates safely", TestCoreAudio_ShutdownVsReattachmentRace);
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
        SettingsService.Validate(m);
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
        var svc = SettingsService.Instance;
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

    private static void TestSettingsLoad_PersistentReadFailure_PreservesFile()
    {
        string dir = ScratchDir();
        string path = Path.Combine(dir, "settings.json");
        var svc = SettingsService.Instance;
        try
        {
            File.WriteAllText(path, "{ \"MonitorIndex\": 3, \"Opacity\": 0.9 }");
            svc.ClearLoadProblem();

            // Lock file exclusively to simulate an unreadable file due to persistent lock
            using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var loaded = svc.Load(path);
                Assert(File.Exists(path), "locked file is preserved on disk (not deleted)");
                AssertEq(0, Directory.GetFiles(dir, "settings.bad-*.json").Length, "locked file is NOT moved to .bad");
                Assert(svc.LoadProblem is not null && svc.LoadProblem.Contains("could not be read"), "LoadProblem reports unreadable file");
            }

            svc.ClearLoadProblem();
            Assert(svc.LoadProblem is null, "LoadProblem cleared");
        }
        finally
        {
            svc.ClearLoadProblem();
            Directory.Delete(dir, recursive: true);
            svc.Load();
        }
    }

    private static void TestSettingsLoad_TransientReadFailure_Recovers()
    {
        string dir = ScratchDir();
        string path = Path.Combine(dir, "settings.json");
        var svc = SettingsService.Instance;
        try
        {
            File.WriteAllText(path, "{ \"MonitorIndex\": 5, \"Opacity\": 0.95 }");
            svc.ClearLoadProblem();

            int attempts = 0;
            SettingsService.StreamOpenerForTesting = p =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new IOException("Simulated transient file sharing violation");
                }
                return new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            };

            var loaded = svc.Load(path);
            AssertEq(2, attempts, "retried after transient failure and succeeded on attempt 2");
            AssertEq(5, loaded.MonitorIndex, "loaded valid settings after retry");
            AssertEq(0.95, loaded.Opacity, "opacity loaded after retry");
            Assert(svc.LoadProblem is null, "no LoadProblem reported for recovered transient error");
            Assert(File.Exists(path), "file remains intact on disk");
            AssertEq(0, Directory.GetFiles(dir, "settings.bad-*.json").Length, "not treated as corrupt");
        }
        finally
        {
            SettingsService.StreamOpenerForTesting = null;
            Directory.Delete(dir, recursive: true);
            svc.Load();
        }
    }

    private static void TestSettingsSave_Failure_PreservesPreviousValidFile()
    {
        string dir = ScratchDir();
        string path = Path.Combine(dir, "settings.json");
        var svc = SettingsService.Instance;
        string originalPath = svc.SettingsPath;
        try
        {
            svc.SettingsPath = path;
            var initial = new WindowsDynamicBar.Core.Settings.SettingsModel { MonitorIndex = 1, Opacity = 0.75 };
            svc.Save(initial);
            Assert(File.Exists(path), "initial settings saved");

            // Exclusively lock destination file with FileShare.None so File.Replace cannot replace it
            using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var failedUpdate = new WindowsDynamicBar.Core.Settings.SettingsModel { MonitorIndex = 9, Opacity = 1.0 };
                svc.Save(failedUpdate);
            }

            // Verify original file is intact and not truncated or corrupted
            Assert(File.Exists(path), "original settings file preserved on disk");
            string contents = File.ReadAllText(path);
            Assert(contents.Contains("\"MonitorIndex\": 1"), "original settings values preserved intact");
            Assert(!contents.Contains("\"MonitorIndex\": 9"), "failed update was not written into file");

            // Verify temporary files cleaned up in finally
            string[] tmpFiles = Directory.GetFiles(dir, "*.tmp");
            AssertEq(0, tmpFiles.Length, "temporary files cleaned up in finally block");
        }
        finally
        {
            svc.SettingsPath = originalPath;
            Directory.Delete(dir, recursive: true);
            svc.Load();
        }
    }

    private static void TestSettingsSave_ConcurrentReaders_DoesNotThrow()
    {
        string dir = ScratchDir();
        string path = Path.Combine(dir, "settings.json");
        var svc = SettingsService.Instance;
        string originalPath = svc.SettingsPath;
        try
        {
            svc.SettingsPath = path;
            var initial = new WindowsDynamicBar.Core.Settings.SettingsModel { MonitorIndex = 2 };
            svc.Save(initial);

            // Open reader stream with FileShare.ReadWrite | FileShare.Delete
            using (var readerStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(readerStream))
            {
                string json = reader.ReadToEnd();
                Assert(json.Contains("\"MonitorIndex\": 2"), "reader read initial data");

                // Save while reader stream handle is held open
                var updated = new WindowsDynamicBar.Core.Settings.SettingsModel { MonitorIndex = 4 };
                svc.Save(updated);
            }

            var loaded = svc.Load(path);
            AssertEq(4, loaded.MonitorIndex, "atomic replace succeeded alongside concurrent reader");

            // Multi-threaded concurrent reads and updates
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var threads = new List<Thread>();
            for (int i = 0; i < 4; i++)
            {
                int threadId = i;
                threads.Add(new Thread(() =>
                {
                    try
                    {
                        for (int j = 0; j < 10; j++)
                        {
                            svc.Load(path);
                            if (threadId % 2 == 0)
                            {
                                svc.Save(new WindowsDynamicBar.Core.Settings.SettingsModel { MonitorIndex = (threadId + j) % 5 });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }));
            }
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            AssertEq(0, exceptions.Count, "no unhandled exceptions during concurrent read/write operations");
        }
        finally
        {
            svc.SettingsPath = originalPath;
            Directory.Delete(dir, recursive: true);
            svc.Load();
        }
    }

    private static void TestSettingsMigration()
    {
        string dir = ScratchDir();
        string path = Path.Combine(dir, "settings.json");
        var svc = SettingsService.Instance;
        try
        {
            File.WriteAllText(path, "{ \"ReduceMotion\": true, \"Opacity\": 0.8 }");   // v1 file: no SettingsVersion
            var m = svc.Load(path);
            AssertEq(WindowsDynamicBar.Core.Settings.ReduceMotionMode.Always, m.ReduceMotionMode, "v1 Reduce Motion -> Always");
            AssertEq(SettingsService.CurrentVersion, m.SettingsVersion, "version bumped");
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

    private static void TestBatteryInfoFromPowerStatus()
    {
        // 1. Desktop PC with no system battery (BatteryFlag = 128)
        var desktopStatus = new NativeMethods.SYSTEM_POWER_STATUS
        {
            ACLineStatus = 1,
            BatteryFlag = 128,
            BatteryLifePercent = 255,
            BatteryLifeTime = -1,
            BatteryFullLifeTime = -1
        };
        var desktopInfo = BatteryService.FromPowerStatus(desktopStatus, energySaver: false);
        Assert(!desktopInfo.HasBattery, "desktop has no battery");
        AssertEq(0.0, desktopInfo.Percent, "desktop percent clamped to 0");
        Assert(desktopInfo.Charging, "desktop is on AC line");
        Assert(desktopInfo.TimeRemaining is null, "desktop has no remaining time");

        // 2. Unknown battery status (BatteryFlag = 255)
        var unknownStatus = new NativeMethods.SYSTEM_POWER_STATUS
        {
            ACLineStatus = 255,
            BatteryFlag = 255,
            BatteryLifePercent = 255
        };
        var unknownInfo = BatteryService.FromPowerStatus(unknownStatus, energySaver: false);
        Assert(!unknownInfo.HasBattery, "unknown battery flag marks HasBattery false");

        // 3. Unknown battery percent (BatteryLifePercent = 255)
        var unknownPctStatus = new NativeMethods.SYSTEM_POWER_STATUS
        {
            ACLineStatus = 0,
            BatteryFlag = 0,
            BatteryLifePercent = 255
        };
        var unknownPctInfo = BatteryService.FromPowerStatus(unknownPctStatus, energySaver: false);
        Assert(!unknownPctInfo.HasBattery, "unknown battery life percent marks HasBattery false");

        // 4. Normal discharging battery (75%, 2h 15m remaining)
        var dischargingStatus = new NativeMethods.SYSTEM_POWER_STATUS
        {
            ACLineStatus = 0,
            BatteryFlag = 1,
            BatteryLifePercent = 75,
            BatteryLifeTime = 8100 // 2 hours 15 minutes
        };
        var dischargingInfo = BatteryService.FromPowerStatus(dischargingStatus, energySaver: false);
        Assert(dischargingInfo.HasBattery, "laptop has battery");
        AssertEq(75.0, dischargingInfo.Percent, "laptop percent is 75");
        Assert(!dischargingInfo.Charging, "discharging");
        AssertEq("2h 15m left", dischargingInfo.TimeRemaining, "time remaining parsed correctly");
        Assert(!dischargingInfo.EnergySaver, "energy saver false");

        // 5. Normal charging battery (90%, AC online, energy saver on)
        var chargingStatus = new NativeMethods.SYSTEM_POWER_STATUS
        {
            ACLineStatus = 1,
            BatteryFlag = 9, // High + Charging
            BatteryLifePercent = 90,
            BatteryLifeTime = -1
        };
        var chargingInfo = BatteryService.FromPowerStatus(chargingStatus, energySaver: true);
        Assert(chargingInfo.HasBattery, "laptop has battery");
        AssertEq(90.0, chargingInfo.Percent, "laptop percent is 90");
        Assert(chargingInfo.Charging, "charging");
        Assert(chargingInfo.TimeRemaining is null, "no time remaining when charging");
        Assert(chargingInfo.EnergySaver, "energy saver true");
    }

    private static void TestBatteryAlertTransitions()
    {
        // Desktop PC (no battery) -> never raises alert
        var desktopCur = new BatteryInfo(0, true, false, null, HasBattery: false);
        var desktopPrev = new BatteryInfo(0, true, false, null, HasBattery: false);
        Assert(!BatteryService.ShouldRaiseBatteryAlert(desktopPrev, desktopCur), "desktop without battery never alerts");

        // Routine battery drift (discharging 80% -> 79%) -> suppressed
        var b80 = new BatteryInfo(80, false, false, "3h 00m left", HasBattery: true);
        var b79 = new BatteryInfo(79, false, false, "2h 58m left", HasBattery: true);
        Assert(!BatteryService.ShouldRaiseBatteryAlert(b80, b79), "routine percent drift is suppressed");

        // Routine battery drift while charging (50% -> 51%) -> suppressed
        var c50 = new BatteryInfo(50, true, false, null, HasBattery: true);
        var c51 = new BatteryInfo(51, true, false, null, HasBattery: true);
        Assert(!BatteryService.ShouldRaiseBatteryAlert(c50, c51), "routine charging percent drift is suppressed");

        // Charger connected (80% discharging -> 80% charging) -> raises alert
        Assert(BatteryService.ShouldRaiseBatteryAlert(b80, new BatteryInfo(80, true, false, null, HasBattery: true)), "charger plug-in alerts");

        // Charger disconnected (80% charging -> 80% discharging) -> raises alert
        Assert(BatteryService.ShouldRaiseBatteryAlert(new BatteryInfo(80, true, false, null, HasBattery: true), b80), "charger unplug alerts");

        // Battery saver toggled on -> raises alert
        var saverOff = new BatteryInfo(60, false, false, "2h left", HasBattery: true);
        var saverOn = new BatteryInfo(60, false, true, "2h 30m left", HasBattery: true);
        Assert(BatteryService.ShouldRaiseBatteryAlert(saverOff, saverOn), "battery saver toggle alerts");

        // Low-battery crossing: 21% -> 20% on discharge -> raises alert
        var b21 = new BatteryInfo(21, false, false, "35m left", HasBattery: true);
        var b20 = new BatteryInfo(20, false, false, "30m left", HasBattery: true);
        Assert(BatteryService.ShouldRaiseBatteryAlert(b21, b20), "crossing <=20% raises alert");

        // Duplicate low-battery alert: 20% -> 19% on discharge -> suppressed!
        var b19 = new BatteryInfo(19, false, false, "28m left", HasBattery: true);
        Assert(!BatteryService.ShouldRaiseBatteryAlert(b20, b19), "subsequent discharge at <=20% is suppressed");

        // Continued discharge: 19% -> 15% -> suppressed!
        var b15 = new BatteryInfo(15, false, false, "20m left", HasBattery: true);
        Assert(!BatteryService.ShouldRaiseBatteryAlert(b19, b15), "further low discharge is suppressed");

        // Low battery charging: 15% discharging -> 15% charging -> raises alert (plugged in)
        var c15 = new BatteryInfo(15, true, false, null, HasBattery: true);
        Assert(BatteryService.ShouldRaiseBatteryAlert(b15, c15), "charging at low battery raises alert");

        // Unplugging while still low: 15% charging -> 15% discharging -> raises alert
        Assert(BatteryService.ShouldRaiseBatteryAlert(c15, b15), "unplugging at low battery raises alert");
    }

    private static void TestBatteryAlertPublishing()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        bool origFeatureBattery = settings.Current.FeatureBattery;
        try
        {
            var service = new BatteryService(bus, settings);
            BarEvent? lastEvent = null;
            bus.EventRaised += e => lastEvent = e;

            // 1. FeatureBattery = false -> alert is suppressed even on transition
            settings.Update(s => s.FeatureBattery = false);
            var b21 = new BatteryInfo(21, false, false, "35m left", HasBattery: true);
            var b20 = new BatteryInfo(20, false, false, "30m left", HasBattery: true);
            service.RaiseBatteryEvent(b21, b20);
            Assert(lastEvent == null, "alert suppressed when FeatureBattery is false");

            // 2. FeatureBattery = true -> low battery alert (priority 95, duration 6s)
            settings.Update(s => s.FeatureBattery = true);
            service.RaiseBatteryEvent(b21, b20);
            Assert(lastEvent != null, "alert raised when FeatureBattery is true");
            AssertEq(BarEventKind.Battery, lastEvent!.Kind, "kind is Battery");
            AssertEq("Battery", lastEvent.Title, "title is Battery");
            AssertEq(95, lastEvent.Priority, "low battery priority is 95");
            AssertEq(TimeSpan.FromSeconds(6), lastEvent.DisplayDuration, "low battery duration is 6s");

            // 3. Low battery duplicate (20% -> 19%) -> no new event
            lastEvent = null;
            var b19 = new BatteryInfo(19, false, false, "28m left", HasBattery: true);
            service.RaiseBatteryEvent(b20, b19);
            Assert(lastEvent == null, "duplicate low battery alert suppressed");

            // 4. Charger connected (80% dc -> 80% ac) -> standard alert (priority 40, duration 3s)
            var b80dc = new BatteryInfo(80, false, false, "3h left", HasBattery: true);
            var b80ac = new BatteryInfo(80, true, false, null, HasBattery: true);
            service.RaiseBatteryEvent(b80dc, b80ac);
            Assert(lastEvent != null, "charger connected raises alert");
            AssertEq("Charging", lastEvent!.Title, "title is Charging");
            AssertEq(40, lastEvent.Priority, "charging alert priority is 40");
            AssertEq(TimeSpan.FromSeconds(3), lastEvent.DisplayDuration, "charging alert duration is 3s");

            // 5. Battery saver toggled on (60% saver off -> 60% saver on) -> standard alert
            lastEvent = null;
            var saverOff = new BatteryInfo(60, false, false, "2h left", HasBattery: true);
            var saverOn = new BatteryInfo(60, false, true, "2h 30m left", HasBattery: true);
            service.RaiseBatteryEvent(saverOff, saverOn);
            Assert(lastEvent != null, "battery saver raises alert");
            AssertEq("Battery saver", lastEvent!.Title, "title is Battery saver");
            AssertEq(40, lastEvent.Priority, "battery saver alert priority is 40");
            AssertEq(TimeSpan.FromSeconds(3), lastEvent.DisplayDuration, "battery saver alert duration is 3s");
        }
        finally
        {
            settings.Update(s => s.FeatureBattery = origFeatureBattery);
        }
    }

    private static void TestSharedHttpConfiguration()
    {
        var client = SharedHttp.Client;
        Assert(client != null, "SharedHttp.Client is initialized");
        AssertEq(Timeout.InfiniteTimeSpan, client!.Timeout, "SharedHttp.Client has infinite timeout");
        string ua = client.DefaultRequestHeaders.TryGetValues("User-Agent", out var values)
            ? string.Join(" ", values)
            : "";
        Assert(ua.Contains("WindowsDynamicBar"), "User-Agent contains WindowsDynamicBar");
        Assert(ua.Contains("github.com/dynamic-bar"), "User-Agent contains project repo link");

        // Test deadline cancellation:
        using var linkedCts = new CancellationTokenSource();
        using var deadline = SharedHttp.Deadline(TimeSpan.FromMilliseconds(50), linkedCts.Token);
        Assert(!deadline.Token.IsCancellationRequested, "deadline not cancelled initially");
        Thread.Sleep(120);
        Assert(deadline.Token.IsCancellationRequested, "deadline cancelled after timeout");

        // Test linked outer cancellation:
        using var outerCts = new CancellationTokenSource();
        using var deadline2 = SharedHttp.Deadline(TimeSpan.FromMinutes(10), outerCts.Token);
        Assert(!deadline2.Token.IsCancellationRequested, "deadline2 not cancelled initially");
        outerCts.Cancel();
        Assert(deadline2.Token.IsCancellationRequested, "deadline2 cancelled immediately on outer token cancellation");
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
            using var timer = new WindowsDynamicBar.Services.Timers.TimerService(bus, SettingsService.Instance);
            timer.Start(TimeSpan.FromSeconds(3), "t");
            Pump(1000);
            var info = timer.Current;
            AssertEq(WindowsDynamicBar.Services.Timers.TimerState.Running, info.State, "still running after 1 s");
            Assert(Math.Abs(info.Remaining.TotalSeconds - 2.0) < 0.35, $"about 2 s left after 1 s (was {info.Remaining.TotalSeconds:F2})");
            timer.Cancel();
        }
        finally { File.Delete(path); }
    }

    private static void TestTimerPauseResume()
    {
        string path = UseScratchState();
        try
        {
            var bus = new WindowsDynamicBar.Core.Events.EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            using var timer = new WindowsDynamicBar.Services.Timers.TimerService(bus, SettingsService.Instance);
            timer.Start(TimeSpan.FromSeconds(5), "PauseTest");
            Pump(250);
            timer.Pause();
            var paused = timer.Current;
            AssertEq(WindowsDynamicBar.Services.Timers.TimerState.Paused, paused.State, "timer is paused");
            var remPaused = paused.Remaining;
            Pump(250);
            AssertEq(remPaused, timer.Current.Remaining, "remaining time does not decrement while paused");
            timer.Resume();
            AssertEq(WindowsDynamicBar.Services.Timers.TimerState.Running, timer.Current.State, "timer resumed to running");
            Pump(250);
            Assert(timer.Current.Remaining < remPaused, "remaining time decrements after resume");
            timer.Cancel();
            AssertEq(WindowsDynamicBar.Services.Timers.TimerState.Idle, timer.Current.State, "cancel sets idle");
        }
        finally { File.Delete(path); }
    }

    private static void TestTimerPersistence()
    {
        string path = UseScratchState();
        try
        {
            var bus = new WindowsDynamicBar.Core.Events.EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            using (var timer = new WindowsDynamicBar.Services.Timers.TimerService(bus, SettingsService.Instance))
            {
                timer.Start(TimeSpan.FromMinutes(10), "Tea");
                var st = WindowsDynamicBar.Core.Settings.RuntimeState.Instance.Current;
                Assert(st.TimerEndUtc is DateTime end && Math.Abs((end - DateTime.UtcNow).TotalMinutes - 10) < 0.05, "end time written");
            }
            using (var restored = new WindowsDynamicBar.Services.Timers.TimerService(bus, SettingsService.Instance))
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

    private static void TestFirstRunDoneAlias()
    {
        var model = new WindowsDynamicBar.Core.Settings.SettingsModel { FirstRunCompleted = false };
        AssertEq(false, model.FirstRunDone, "FirstRunDone mirrors FirstRunCompleted false");
        model.FirstRunDone = true;
        AssertEq(true, model.FirstRunCompleted, "Setting FirstRunDone sets FirstRunCompleted true");
    }

    private static void TestWindowSwitcherMatching()
    {
        var w1 = new WindowsDynamicBar.Services.Search.WindowEntry(new IntPtr(1), "Visual Studio Code", "Code", "C:\\Code\\Code.exe", 0);
        var w2 = new WindowsDynamicBar.Services.Search.WindowEntry(new IntPtr(2), "Windows Terminal", "wt", "C:\\wt.exe", 1);
        var w3 = new WindowsDynamicBar.Services.Search.WindowEntry(new IntPtr(3), "Spotify Free", "Spotify", "C:\\Spotify.exe", 2);

        // FuzzyMatcher scoring against title & process
        var mCode = WindowsDynamicBar.Core.Search.FuzzyMatcher.Match(w1.Title, "code");
        Assert(mCode.Matched, "code matches Visual Studio Code");

        var mTerminal = WindowsDynamicBar.Core.Search.FuzzyMatcher.Match(w2.Title, "term");
        Assert(mTerminal.Matched, "term matches Windows Terminal");

        var mSpotify = WindowsDynamicBar.Core.Search.FuzzyMatcher.Match(w3.ProcessName, "spot");
        Assert(mSpotify.Matched, "spot matches Spotify process name");
    }

    private static void TestFullscreenCheck()
    {
        Assert(!WindowsDynamicBar.Interop.NativeMethods.IsWindowFullscreen(IntPtr.Zero), "IntPtr.Zero is not fullscreen");
    }

    private static void TestMediaLifecycle_ConcurrentStart_IsIdempotent()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        using var svc = new MediaService(bus);

        int requestCount = 0;
        var tcs = new TaskCompletionSource<Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager?>();
        MediaService.ManagerRequesterForTesting = ct =>
        {
            Interlocked.Increment(ref requestCount);
            return tcs.Task;
        };

        try
        {
            var task1 = svc.StartAsync();
            var task2 = svc.StartAsync();

            Assert(ReferenceEquals(task1, task2), "Concurrent StartAsync calls must return the same in-flight task");
            AssertEq(1, Volatile.Read(ref requestCount), "Manager requester invoked exactly once for concurrent starts");

            tcs.SetResult(null);
            Pump(50);
            Assert(task1.IsCompleted, "Task completed after manager provider finished");

            var task3 = svc.StartAsync();
            Pump(20);
            Assert(task3.IsCompleted, "Subsequent StartAsync completes safely");
        }
        finally
        {
            MediaService.ManagerRequesterForTesting = null;
        }
    }

    private static void TestMediaLifecycle_StopDuringStartup_CancelsAndDoesNotStart()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        using var svc = new MediaService(bus);

        var cancellationObserved = new ManualResetEventSlim(false);
        var tcs = new TaskCompletionSource<Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager?>();
        MediaService.ManagerRequesterForTesting = async ct =>
        {
            using (ct.Register(() =>
            {
                cancellationObserved.Set();
                tcs.TrySetCanceled(ct);
            }))
            {
                return await tcs.Task;
            }
        };

        try
        {
            var startTask = svc.StartAsync();
            Assert(!svc.IsRunning, "Not running while startup is in-flight");

            svc.Stop();
            Assert(cancellationObserved.Wait(TimeSpan.FromSeconds(2)), "In-flight startup cancellation token signaled on Stop");
            Pump(50);

            Assert(!svc.IsRunning, "Service must not be running after stop during in-flight start");
        }
        finally
        {
            MediaService.ManagerRequesterForTesting = null;
        }
    }

    private static void TestMediaLifecycle_DisposeDuringStartup_CancelsAndSuppressesMediaChanged()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var svc = new MediaService(bus);

        bool mediaChangedFired = false;
        svc.MediaChanged += _ => mediaChangedFired = true;

        var cancellationObserved = new ManualResetEventSlim(false);
        var tcs = new TaskCompletionSource<Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager?>();
        MediaService.ManagerRequesterForTesting = async ct =>
        {
            using (ct.Register(() =>
            {
                cancellationObserved.Set();
                tcs.TrySetCanceled(ct);
            }))
            {
                return await tcs.Task;
            }
        };

        try
        {
            var startTask = svc.StartAsync();
            svc.Dispose();

            Assert(cancellationObserved.Wait(TimeSpan.FromSeconds(2)), "In-flight startup cancellation token signaled on Dispose");
            Pump(50);

            Assert(!mediaChangedFired, "MediaChanged must be suppressed on Dispose");
            Assert(!svc.IsRunning, "Service must not be running after Dispose");

            var followUp = svc.StartAsync();
            Assert(followUp.IsCompleted, "StartAsync on disposed service returns immediately");
            Assert(!svc.IsRunning, "Service remains stopped when disposed");
        }
        finally
        {
            MediaService.ManagerRequesterForTesting = null;
        }
    }

    private static void TestMediaLifecycle_RuntimeStop_EmitsNoMedia()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        using var svc = new MediaService(bus);

        svc.SetStartedForTesting(true);
        Assert(svc.IsRunning, "Service is marked running");

        MediaInfo? emitted = null;
        svc.MediaChanged += m => emitted = m;

        svc.Stop();

        Assert(!svc.IsRunning, "Service is stopped");
        Assert(emitted is not null, "Runtime Stop must emit ClearToNoMedia");
        AssertEq("", emitted!.Title, "Empty title emitted");
        AssertEq("", emitted.Artist, "Empty artist emitted");
        AssertEq(false, emitted.IsPlaying, "IsPlaying false");
    }

    private static void TestMediaLifecycle_Dispose_SuppressesMediaChanged()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var svc = new MediaService(bus);

        svc.SetStartedForTesting(true);
        Assert(svc.IsRunning, "Service is running");

        bool mediaChangedFired = false;
        svc.MediaChanged += _ => mediaChangedFired = true;

        svc.Dispose();

        Assert(!svc.IsRunning, "Service stopped on dispose");
        Assert(!mediaChangedFired, "MediaChanged must be suppressed on Dispose");

        // Multiple calls to Dispose / Stop are safe and still emit nothing
        svc.Stop();
        svc.Dispose();
        Assert(!mediaChangedFired, "Repeated Stop/Dispose after Dispose still suppresses MediaChanged");
    }

    private static void TestMediaDisc_Lifecycle()
    {
        var win = new System.Windows.Window
        {
            Width = 100,
            Height = 100,
            ShowInTaskbar = false,
            WindowStyle = System.Windows.WindowStyle.None
        };
        var disc = new MediaDisc();
        win.Content = disc;

        try
        {
            // 1. Initially before window is shown (unloaded): SetPlaying(true) must NOT run the timer
            disc.SetPlaying(true);
            Assert(!disc.IsSpinTimerRunningForTesting, "Spin timer must not run while unloaded");

            // 2. When window is shown (Loaded & Visible): spin timer starts
            win.Show();
            Pump(50);
            Assert(disc.IsSpinTimerRunningForTesting, "Spin timer starts on Loaded when playing and visible");

            // 3. Remove from window (Unloaded): timer stops and tweens are cleared
            win.Content = null;
            Pump(50);
            Assert(!disc.IsSpinTimerRunningForTesting, "Spin timer stops on Unloaded");
            AssertEq(0, disc.ActiveTweenCountForTesting, "Tweens cleared on Unloaded");

            // 4. While unloaded, SetPlaying or SyncSpin must NOT start timer
            disc.SetPlaying(true);
            Assert(!disc.IsSpinTimerRunningForTesting, "SyncSpin must not start spin timer while unloaded");

            // 5. Put back in window when NOT playing must NOT start timer
            disc.SetPlaying(false);
            win.Content = disc;
            Pump(50);
            Assert(!disc.IsSpinTimerRunningForTesting, "Spin timer does not start on Loaded when not playing");

            // 6. SetPlaying(true) while loaded starts timer
            disc.SetPlaying(true);
            Assert(disc.IsSpinTimerRunningForTesting, "Spin timer starts on SetPlaying when loaded");
        }
        finally
        {
            win.Close();
            Pump(50);
        }
    }

    private static void TestMediaArtwork_BoundedTimeout_RecoversAndDisposesStream()
    {
        // 1. Null stream returns null immediately
        var nullTask = MediaService.TryGetArtworkAsync(null, TimeSpan.FromSeconds(2.5));
        Assert(nullTask.Result is null, "Null stream reference returns null");

        // 2. Empty stream returns null
        using (var emptyStream = new InMemoryRandomAccessStream())
        {
            var streamRef = RandomAccessStreamReference.CreateFromStream(emptyStream);
            var emptyTask = MediaService.TryGetArtworkAsync(streamRef, TimeSpan.FromSeconds(2.5));
            Assert(emptyTask.Result is null, "Empty stream returns null");
        }

        // 3. Valid stream returns byte content
        byte[] expected = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        using (var validStream = new InMemoryRandomAccessStream())
        {
            using (var writer = new DataWriter(validStream))
            {
                writer.WriteBytes(expected);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            validStream.Seek(0);
            var streamRef = RandomAccessStreamReference.CreateFromStream(validStream);
            var validTask = MediaService.TryGetArtworkAsync(streamRef, TimeSpan.FromSeconds(2.5));
            var bytes = validTask.Result;
            Assert(bytes is not null, "Artwork bytes retrieved from valid stream");
            AssertEq(expected.Length, bytes!.Length, "Byte count matches");
            AssertEq(expected[0], bytes[0], "First byte matches PNG header");
        }

        // 4. Zero/expired timeout handles timeout gracefully and returns null
        using (var slowStream = new InMemoryRandomAccessStream())
        {
            var streamRef = RandomAccessStreamReference.CreateFromStream(slowStream);
            var timedOutTask = MediaService.TryGetArtworkAsync(streamRef, TimeSpan.FromMilliseconds(0));
            Assert(timedOutTask.Result is null, "Zero timeout returns null safely without throwing");
        }
    }

    private static void TestMediaLifecycle_StopVsStartupCompletion_RaceCondition()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        using var svc = new MediaService(bus);

        int subscribeCount = 0;
        int unsubscribeCount = 0;
        MediaService.OnManagerSubscribedForTesting = () => Interlocked.Increment(ref subscribeCount);
        MediaService.OnManagerUnsubscribedForTesting = () => Interlocked.Increment(ref unsubscribeCount);

        // Try getting a real SMTC manager if available on the system, or null
        Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager? manager = null;
        try
        {
            manager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
        }
        catch { }

        var startupCompletedManagerAcquisition = new ManualResetEventSlim(false);
        MediaService.ManagerRequesterForTesting = ct =>
        {
            startupCompletedManagerAcquisition.Set();
            return Task.FromResult(manager);
        };

        // When StartCoreAsync finishes requesting manager and is about to acquire the lifecycle lock,
        // force Stop() to execute and acquire the lock first.
        MediaService.BeforeStartupLockAcquiredForTesting = () =>
        {
            svc.Stop();
        };

        try
        {
            var startTask = svc.StartAsync();
            Assert(startupCompletedManagerAcquisition.Wait(TimeSpan.FromSeconds(2)), "Manager request completed");
            Pump(50);
            startTask.GetAwaiter().GetResult();

            Assert(!svc.IsRunning, "Service must remain stopped when Stop races with startup completion");
            Assert(!svc.HasManagerForTesting, "Manager must not be attached to service");
            Assert(!svc.IsDisposedForTesting, "Service must not be marked disposed");
            Assert(!svc.HasManagerSubscriptionsForTesting, "Service must report no active manager subscriptions");
            AssertEq(0, Volatile.Read(ref subscribeCount), "Manager event subscriptions must not be attached during stopped race");

            // Verify the service can start cleanly after the race condition was handled
            MediaService.BeforeStartupLockAcquiredForTesting = null;
            var restartTask = svc.StartAsync();
            Pump(50);
            restartTask.GetAwaiter().GetResult();

            if (manager is not null)
            {
                Assert(svc.IsRunning, "Service started successfully on subsequent StartAsync");
                Assert(svc.HasManagerForTesting, "Manager attached on restart");
                Assert(svc.HasManagerSubscriptionsForTesting, "Service has active manager subscriptions on restart");
                AssertEq(1, Volatile.Read(ref subscribeCount), "Manager event handlers subscribed on restart");

                svc.Stop();
                Assert(!svc.IsRunning, "Service stopped cleanly after restart");
                Assert(!svc.HasManagerSubscriptionsForTesting, "Manager subscriptions cleared after stop");
                AssertEq(1, Volatile.Read(ref unsubscribeCount), "Manager event handlers unsubscribed on stop");
            }
        }
        finally
        {
            MediaService.ManagerRequesterForTesting = null;
            MediaService.BeforeStartupLockAcquiredForTesting = null;
            MediaService.OnManagerSubscribedForTesting = null;
            MediaService.OnManagerUnsubscribedForTesting = null;
        }
    }

    private sealed class MockPendingAsyncOperation<T> : Windows.Foundation.IAsyncOperation<T>
    {
        private readonly TaskCompletionSource<T> _tcs = new();
        private Windows.Foundation.AsyncOperationCompletedHandler<T>? _completed;
        private Windows.Foundation.AsyncStatus _status = Windows.Foundation.AsyncStatus.Started;

        public bool CancelInvoked { get; private set; }
        public bool CloseInvoked { get; private set; }

        public void Cancel()
        {
            CancelInvoked = true;
        }

        public void Close()
        {
            CloseInvoked = true;
        }

        public Windows.Foundation.AsyncOperationCompletedHandler<T>? Completed
        {
            get => _completed;
            set
            {
                _completed = value;
                if (_status != Windows.Foundation.AsyncStatus.Started)
                {
                    _completed?.Invoke(this, _status);
                }
            }
        }

        public Exception? ErrorCode { get; private set; }
        public uint Id => 1;
        public Windows.Foundation.AsyncStatus Status => _status;

        public T GetResults() => _tcs.Task.GetAwaiter().GetResult();

        public void CompleteSuccess(T result)
        {
            _status = Windows.Foundation.AsyncStatus.Completed;
            _tcs.TrySetResult(result);
            _completed?.Invoke(this, _status);
        }

        public void CompleteCanceled()
        {
            _status = Windows.Foundation.AsyncStatus.Canceled;
            _tcs.TrySetCanceled();
            _completed?.Invoke(this, _status);
        }

        public void CompleteError(Exception ex)
        {
            _status = Windows.Foundation.AsyncStatus.Error;
            ErrorCode = ex;
            _tcs.TrySetException(ex);
            _completed?.Invoke(this, _status);
        }
    }

    private sealed class MockPendingStreamReference : IRandomAccessStreamReference
    {
        private readonly Windows.Foundation.IAsyncOperation<IRandomAccessStreamWithContentType> _op;

        public MockPendingStreamReference(Windows.Foundation.IAsyncOperation<IRandomAccessStreamWithContentType> op)
        {
            _op = op;
        }

        public Windows.Foundation.IAsyncOperation<IRandomAccessStreamWithContentType> OpenReadAsync() => _op;
    }

    private sealed class StreamWithContentType : IRandomAccessStreamWithContentType
    {
        private readonly IRandomAccessStream _inner;
        public string ContentType { get; }
        public bool IsDisposed { get; private set; }

        public StreamWithContentType(IRandomAccessStream inner, string contentType = "image/png")
        {
            _inner = inner;
            ContentType = contentType;
        }

        public bool CanRead => _inner.CanRead;
        public bool CanWrite => _inner.CanWrite;
        public ulong Position => _inner.Position;
        public ulong Size { get => _inner.Size; set => _inner.Size = value; }

        public IRandomAccessStream CloneStream() => _inner.CloneStream();
        public void Dispose()
        {
            IsDisposed = true;
            _inner.Dispose();
        }
        public void Seek(ulong position) => _inner.Seek(position);
        public Windows.Foundation.IAsyncOperation<bool> FlushAsync() => _inner.FlushAsync();
        public IInputStream GetInputStreamAt(ulong position) => _inner.GetInputStreamAt(position);
        public IOutputStream GetOutputStreamAt(ulong position) => _inner.GetOutputStreamAt(position);
        public Windows.Foundation.IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options) => _inner.ReadAsync(buffer, count, options);
        public Windows.Foundation.IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer) => _inner.WriteAsync(buffer);
    }

    private static void TestMediaArtwork_PendingAsyncOperation_SafeDisposal()
    {
        try
        {
            // 1. OpenReadAsync remains pending beyond timeout:
            // Verify public method returns null within configured deadline, cancellation is signaled on op,
            // stream is not disposed prematurely, and late-arriving stream is explicitly disposed afterward.
            var mockOpenOp = new MockPendingAsyncOperation<IRandomAccessStreamWithContentType>();
            var streamRef = new MockPendingStreamReference(mockOpenOp);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var task = MediaService.TryGetArtworkAsync(streamRef, TimeSpan.FromMilliseconds(50));
            var result = task.GetAwaiter().GetResult();
            sw.Stop();

            Assert(result is null, "Pending thumbnail operation reports null artwork on timeout");
            Assert(sw.Elapsed < TimeSpan.FromSeconds(1), $"Timeout returned within bounded time: {sw.ElapsedMilliseconds}ms");
            Assert(mockOpenOp.CancelInvoked, "Cancellation was requested on the underlying WinRT open operation");
            Assert(!mockOpenOp.CloseInvoked, "Underlying operation Close was not prematurely called while pending");

            // Complete the abandoned open operation with a stream; verify deferred disposal explicitly cleans it up
            using var lateInnerStream = new InMemoryRandomAccessStream();
            var lateStreamWrapper = new StreamWithContentType(lateInnerStream);
            Assert(!lateStreamWrapper.IsDisposed, "Late stream wrapper is initially undisposed");

            mockOpenOp.CompleteSuccess(lateStreamWrapper);
            PumpUntil(() => lateStreamWrapper.IsDisposed, 1000);

            Assert(lateStreamWrapper.IsDisposed, "Late-arriving stream was explicitly disposed after timed-out operation completed");

            // 2. Pending LoadAsync operation:
            // Verify caller returns null within deadline, cancellation is requested, reader and stream remain alive
            // while pending, and both are explicitly disposed once the underlying load operation reaches terminal state.
            using var loadInnerStream = new InMemoryRandomAccessStream();
            var loadTrackableStream = new StreamWithContentType(loadInnerStream);

            var promptOpenOp = new MockPendingAsyncOperation<IRandomAccessStreamWithContentType>();
            var loadStreamRef = new MockPendingStreamReference(promptOpenOp);
            promptOpenOp.CompleteSuccess(loadTrackableStream);

            var mockLoadOp = new MockPendingAsyncOperation<uint>();
            Windows.Storage.Streams.DataReader? capturedReader = null;
            MediaService.LoadOperationProviderForTesting = (reader, stream, count) =>
            {
                if (ReferenceEquals(stream, loadTrackableStream))
                {
                    capturedReader = reader;
                    return mockLoadOp;
                }
                return reader.LoadAsync(count);
            };

            var loadSw = System.Diagnostics.Stopwatch.StartNew();
            var loadTask = MediaService.TryGetArtworkAsync(loadStreamRef, TimeSpan.FromMilliseconds(50));
            var loadResult = loadTask.GetAwaiter().GetResult();
            loadSw.Stop();

            Assert(loadResult is null, "Pending load operation reports null artwork on timeout");
            Assert(loadSw.Elapsed < TimeSpan.FromSeconds(1), $"Load timeout returned within bounded time: {loadSw.ElapsedMilliseconds}ms");
            Assert(mockLoadOp.CancelInvoked, "Cancellation was requested on the underlying WinRT load operation");
            Assert(mockLoadOp.Status == Windows.Foundation.AsyncStatus.Started, "Underlying load operation is still in pending/started state");
            Assert(!loadTrackableStream.IsDisposed, "Stream must NOT be prematurely disposed while load operation is still in flight");
            Assert(capturedReader is not null, "DataReader was instantiated for stream");

            bool readerDisposedEarly = false;
            try { _ = capturedReader!.UnconsumedBufferLength; }
            catch (Exception) { readerDisposedEarly = true; }
            Assert(!readerDisposedEarly, "DataReader must NOT be prematurely disposed while load operation is still in flight");

            // Now signal terminal completion on the underlying load operation (e.g. cancellation completed)
            mockLoadOp.CompleteCanceled();
            PumpUntil(() => loadTrackableStream.IsDisposed, 1000);

            Assert(loadTrackableStream.IsDisposed, "Stream is explicitly disposed after pending load operation reaches terminal state");
            bool readerDisposedTerminal = false;
            try { _ = capturedReader!.UnconsumedBufferLength; }
            catch (Exception) { readerDisposedTerminal = true; }
            Assert(readerDisposedTerminal, "DataReader is explicitly disposed after pending load operation reaches terminal state");

            MediaService.LoadOperationProviderForTesting = null;

            // 3. Operation completes before timeout: extracts bytes normally
            byte[] expected = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            using var validStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(validStream))
            {
                writer.WriteBytes(expected);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            validStream.Seek(0);

            MediaService.LoadOperationProviderForTesting = null;
            var successOp = new MockPendingAsyncOperation<IRandomAccessStreamWithContentType>();
            var successRef = new MockPendingStreamReference(successOp);
            var successTask = MediaService.TryGetArtworkAsync(successRef, TimeSpan.FromSeconds(2.5));
            successOp.CompleteSuccess(new StreamWithContentType(validStream));

            var successBytes = successTask.GetAwaiter().GetResult();
            Assert(successBytes is not null, "Artwork bytes retrieved on prompt completion");
            AssertEq(expected.Length, successBytes!.Length, "Byte length matches expected");
            AssertEq(expected[0], successBytes[0], "First byte matches expected");
        }
        finally
        {
            MediaService.LoadOperationProviderForTesting = null;
        }
    }

    private sealed class FakeCoreAudioController : ICoreAudioController
    {
        public bool ShouldFailInit { get; set; }
        public string? InitError { get; set; }
        public bool IsAvailable { get; set; } = true;
        public float CurrentLevel { get; set; } = 0.5f;
        public bool CurrentMuted { get; set; }
        public string? DefaultDeviceName { get; set; } = "Headphones (Test Device)";

        public Action<float, bool>? CapturedOnVolume { get; private set; }
        public Action? CapturedOnDefaultDevice { get; private set; }

        public int SetLevelCallCount { get; private set; }
        public float LastSetLevel { get; private set; }

        public int SetMuteCallCount { get; private set; }
        public bool LastSetMute { get; private set; }

        public int ShutdownCallCount { get; private set; }

        public bool TryInitialize(Action<float, bool> onVolumeChanged, Action onDefaultDeviceChanged, out string? error)
        {
            if (ShouldFailInit)
            {
                error = InitError ?? "Audio initialization failed";
                return false;
            }
            error = null;
            CapturedOnVolume = onVolumeChanged;
            CapturedOnDefaultDevice = onDefaultDeviceChanged;
            return true;
        }

        public (float Level, bool Muted)? GetCurrent() => (CurrentLevel, CurrentMuted);
        public string? GetDefaultDeviceName() => DefaultDeviceName;

        public void SetLevel(float level01)
        {
            SetLevelCallCount++;
            LastSetLevel = level01;
            CurrentLevel = level01;
        }

        public void SetMute(bool mute)
        {
            SetMuteCallCount++;
            LastSetMute = mute;
            CurrentMuted = mute;
        }

        public void StepUp() { }
        public void StepDown() { }

        public void Shutdown()
        {
            ShutdownCallCount++;
        }
    }

    private static void TestVolumeService_Initialization_FailureAndSuccess()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        // 1. Failure case
        var failCtrl = new FakeCoreAudioController { ShouldFailInit = true, InitError = "No audio endpoints" };
        using (var failService = new VolumeService(bus, settings, failCtrl))
        {
            failService.Start();
            Assert(!failService.IsAvailable, "Service is unavailable when controller fails init");
            AssertEq(0f, failService.Level, "Default level is 0 when uninitialized");
            Assert(!failService.Muted, "Default mute is false");
            Assert(failService.DeviceName is null, "Device name is null");
        }
        AssertEq(1, failCtrl.ShutdownCallCount, "Disposal shuts down controller");

        // 2. Success case
        var okCtrl = new FakeCoreAudioController
        {
            CurrentLevel = 0.65f,
            CurrentMuted = false,
            DefaultDeviceName = "Speakers (Realtek)"
        };
        using (var okService = new VolumeService(bus, settings, okCtrl))
        {
            okService.Start();
            Assert(okService.IsAvailable, "Service is available when controller succeeds init");
            AssertEq(0.65f, okService.Level, "Initial level populated from controller");
            Assert(!okService.Muted, "Initial mute populated from controller");
            AssertEq("Speakers (Realtek)", okService.DeviceName, "Device name populated from controller");
        }
        AssertEq(1, okCtrl.ShutdownCallCount, "Disposal shuts down controller");
    }

    private static void TestVolumeService_SetLevelAndToggleMute()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        var ctrl = new FakeCoreAudioController { CurrentLevel = 0.4f, CurrentMuted = false };
        using var svc = new VolumeService(bus, settings, ctrl);
        svc.Start();

        (float Level, bool Muted)? lastChanged = null;
        svc.VolumeChanged += (lvl, m) => lastChanged = (lvl, m);

        // SetLevel normal
        svc.SetLevel(0.8f);
        AssertEq(0.8f, svc.Level, "Level updated to 0.8");
        AssertEq(0.8f, ctrl.LastSetLevel, "Controller received 0.8");
        AssertEq(1, ctrl.SetLevelCallCount, "SetLevel called once");
        Assert(lastChanged.HasValue && Math.Abs(lastChanged.Value.Level - 0.8f) < 0.0001f, "VolumeChanged raised for SetLevel");

        // SetLevel clamped high
        svc.SetLevel(1.5f);
        AssertEq(1.0f, svc.Level, "Level clamped to 1.0");
        AssertEq(1.0f, ctrl.LastSetLevel, "Controller received 1.0");

        // SetLevel clamped low
        svc.SetLevel(-0.5f);
        AssertEq(0.0f, svc.Level, "Level clamped to 0.0");
        AssertEq(0.0f, ctrl.LastSetLevel, "Controller received 0.0");

        // ToggleMute to true
        lastChanged = null;
        svc.ToggleMute();
        Assert(svc.Muted, "Service is muted");
        Assert(ctrl.LastSetMute, "Controller received mute=true");
        AssertEq(1, ctrl.SetMuteCallCount, "SetMute called once");
        Assert(lastChanged.HasValue && lastChanged.Value.Muted, "VolumeChanged raised for mute=true");

        // ToggleMute back to false
        lastChanged = null;
        svc.ToggleMute();
        Assert(!svc.Muted, "Service is unmuted");
        Assert(!ctrl.LastSetMute, "Controller received mute=false");
        AssertEq(2, ctrl.SetMuteCallCount, "SetMute called twice");
        Assert(lastChanged.HasValue && !lastChanged.Value.Muted, "VolumeChanged raised for mute=false");
    }

    private static void TestVolumeService_NotificationDeduplication()
    {
        // Pure helper tests
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // Initial notification (lastEvent is MinValue) -> NOT suppressed
        Assert(!VolumeService.ShouldSuppressNotification(0.5f, false, -1f, false, DateTime.MinValue, t0),
            "Initial notification is not suppressed");

        // Exact same level and mute within 200ms -> SUPPRESSED
        Assert(VolumeService.ShouldSuppressNotification(0.5f, false, 0.5f, false, t0, t0.AddMilliseconds(200)),
            "Duplicate within 500ms is suppressed");

        // Exact same level and mute after 600ms -> NOT suppressed
        Assert(!VolumeService.ShouldSuppressNotification(0.5f, false, 0.5f, false, t0, t0.AddMilliseconds(600)),
            "Duplicate after 500ms is not suppressed");

        // Delta > 0.001 within 50ms -> NOT suppressed
        Assert(!VolumeService.ShouldSuppressNotification(0.51f, false, 0.5f, false, t0, t0.AddMilliseconds(50)),
            "Volume change > 0.001 is not suppressed");

        // Mute change within 50ms -> NOT suppressed
        Assert(!VolumeService.ShouldSuppressNotification(0.5f, true, 0.5f, false, t0, t0.AddMilliseconds(50)),
            "Mute state change is not suppressed");

        // End-to-end service test
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureVolume = true);

        var ctrl = new FakeCoreAudioController { CurrentLevel = 0.5f, CurrentMuted = false };
        using var svc = new VolumeService(bus, settings, ctrl);
        svc.Start();

        int eventsRaised = 0;
        BarEvent? lastEvent = null;
        bus.EventRaised += e =>
        {
            if (e.Kind == BarEventKind.Volume)
            {
                eventsRaised++;
                lastEvent = e;
            }
        };

        // First notification -> raises event
        ctrl.CapturedOnVolume?.Invoke(0.5f, false);
        AssertEq(1, eventsRaised, "First notification raises event");
        Assert(lastEvent is not null, "Event is non-null");

        // Duplicate notification within 100ms -> suppressed!
        ctrl.CapturedOnVolume?.Invoke(0.5f, false);
        AssertEq(1, eventsRaised, "Duplicate notification within 500ms does not raise second event");

        // Substantial change -> raises event
        ctrl.CapturedOnVolume?.Invoke(0.7f, false);
        AssertEq(2, eventsRaised, "Volume change raises event");
        Assert(lastEvent is not null && lastEvent.Value.HasValue, "Event value is present");
        Assert(Math.Abs(lastEvent!.Value!.Value - 0.7) < 0.01, "Event value matches updated volume");

        // Mute flip -> raises event
        ctrl.CapturedOnVolume?.Invoke(0.7f, true);
        AssertEq(3, eventsRaised, "Mute toggle raises event");
        Assert(lastEvent is not null && lastEvent.Value.HasValue, "Event value is present");
        AssertEq(0.0, lastEvent!.Value!.Value, "Muted event value is 0");
    }

    private static void TestVolumeService_EventPublishing_MetadataAndFeatureToggle()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        var ctrl = new FakeCoreAudioController { CurrentLevel = 0.2f, CurrentMuted = false, DefaultDeviceName = "Studio Monitors" };
        using var svc = new VolumeService(bus, settings, ctrl);
        svc.Start();

        BarEvent? lastEvent = null;
        bus.EventRaised += e => lastEvent = e;

        // 1. FeatureVolume = false -> bus event is suppressed, VolumeChanged still fires
        settings.Update(s => s.FeatureVolume = false);
        bool volumeChangedFired = false;
        svc.VolumeChanged += (_, _) => volumeChangedFired = true;

        ctrl.CapturedOnVolume?.Invoke(0.9f, false);
        Assert(volumeChangedFired, "VolumeChanged fires even when FeatureVolume is false");
        Assert(lastEvent is null, "EventBus event suppressed when FeatureVolume is false");

        // 2. FeatureVolume = true -> verify icons and metadata
        settings.Update(s => s.FeatureVolume = true);

        // Silent icon at 0.0
        ctrl.CapturedOnVolume?.Invoke(0.0f, false);
        Assert(lastEvent is not null, "Event raised for 0.0");
        AssertEq("Silent", lastEvent!.Title, "Title is Silent for 0 level");
        AssertEq(70, lastEvent.Priority, "Volume priority is 70");
        AssertEq(TimeSpan.FromSeconds(2.5), lastEvent.DisplayDuration, "Display duration is 2.5s");

        // Low icon at 0.3
        ctrl.CapturedOnVolume?.Invoke(0.3f, false);
        AssertEq("Low", lastEvent!.Title, "Title is Low for level < 0.5");

        // High icon at 0.8
        ctrl.CapturedOnVolume?.Invoke(0.8f, false);
        AssertEq("High", lastEvent!.Title, "Title is High for level >= 0.5");

        // Muted icon and text
        ctrl.CapturedOnVolume?.Invoke(0.8f, true);
        AssertEq("Muted", lastEvent!.Title, "Title is Muted when muted");
        Assert(lastEvent.Value.HasValue && lastEvent.Value.Value == 0.0, "Value is 0 when muted");
    }

    private static void TestVolumeService_DefaultDeviceChanged()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureVolume = true);

        var ctrl = new FakeCoreAudioController
        {
            CurrentLevel = 0.5f,
            CurrentMuted = false,
            DefaultDeviceName = "Built-in Speakers"
        };
        using var svc = new VolumeService(bus, settings, ctrl);
        svc.Start();

        BarEvent? lastEvent = null;
        bus.EventRaised += e => lastEvent = e;

        // Change device in controller and fire notification
        ctrl.DefaultDeviceName = "USB Audio Headset";
        ctrl.CurrentLevel = 0.72f;
        ctrl.CurrentMuted = false;

        ctrl.CapturedOnDefaultDevice?.Invoke();

        AssertEq("USB Audio Headset", svc.DeviceName, "Service updated device name");
        AssertEq(0.72f, svc.Level, "Service updated level for new device");
        Assert(lastEvent is not null, "Device change raised event on bus");
        AssertEq("Output device", lastEvent!.Title, "Title is Output device");
        AssertEq("USB Audio Headset", lastEvent.Subtitle, "Subtitle contains device name");
        AssertEq(TimeSpan.FromSeconds(3), lastEvent.DisplayDuration, "Device change duration is 3s");
        AssertEq(70, lastEvent.Priority, "Device change priority is 70");
    }

    private static void TestVolumeService_Dispose_SuppressesLateCallbacks()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        var ctrl = new FakeCoreAudioController { CurrentLevel = 0.4f, CurrentMuted = false };
        var svc = new VolumeService(bus, settings, ctrl);
        svc.Start();

        int busEvents = 0;
        bus.EventRaised += _ => busEvents++;
        int volumeChangedCalls = 0;
        svc.VolumeChanged += (_, _) => volumeChangedCalls++;

        // Dispose service
        svc.Dispose();
        Assert(!svc.IsAvailable, "Disposed service is unavailable");
        AssertEq(1, ctrl.ShutdownCallCount, "Controller shutdown invoked on dispose");

        // Redundant dispose is safe and idempotent
        svc.Dispose();
        AssertEq(1, ctrl.ShutdownCallCount, "Duplicate dispose is idempotent and does not re-invoke shutdown");

        // Late callbacks arriving from COM or ThreadPool after disposal
        ctrl.CapturedOnVolume?.Invoke(0.95f, false);
        ctrl.CapturedOnDefaultDevice?.Invoke();

        // Mutating methods after disposal
        svc.SetLevel(0.5f);
        svc.ToggleMute();

        AssertEq(0, busEvents, "No bus events raised after disposal");
        AssertEq(0, volumeChangedCalls, "No VolumeChanged callbacks invoked after disposal");
        AssertEq(0.4f, svc.Level, "Level did not mutate after disposal");
    }

    private static void TestVolumeService_ConcurrentAccess()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        var ctrl = new FakeCoreAudioController { CurrentLevel = 0.5f, CurrentMuted = false };
        using var svc = new VolumeService(bus, settings, ctrl);
        svc.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var token = cts.Token;
        int workerCount = 6;
        var tasks = new Task[workerCount];
        int exceptions = 0;

        for (int i = 0; i < workerCount; i++)
        {
            int threadId = i;
            tasks[i] = Task.Run(() =>
            {
                try
                {
                    float localLevel = 0f;
                    while (!token.IsCancellationRequested)
                    {
                        switch (threadId % 3)
                        {
                            case 0:
                                localLevel = (localLevel + 0.05f) % 1.0f;
                                svc.SetLevel(localLevel);
                                break;
                            case 1:
                                svc.ToggleMute();
                                break;
                            case 2:
                                ctrl.CapturedOnVolume?.Invoke(0.6f, false);
                                _ = svc.Level;
                                _ = svc.Muted;
                                _ = svc.DeviceName;
                                break;
                        }
                        Thread.Sleep(1);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref exceptions);
                }
            });
        }

        var allTask = Task.WhenAll(tasks);
        while (!allTask.IsCompleted)
        {
            Pump(10);
        }
        AssertEq(0, exceptions, "Concurrent access produced zero exceptions");
        Assert(svc.Level >= 0f && svc.Level <= 1.0f, "Final volume level is safely clamped");
    }

    private static void TestCoreAudio_ShutdownVsReattachmentRace()
    {
        // 1. Verify CoreAudio.Shutdown resets IsAvailable and causes subsequent AttachDefaultEndpoint to safely abort
        CoreAudio.Shutdown();
        Assert(!CoreAudio.IsAvailable, "CoreAudio is not available after Shutdown()");

        bool attachResult = CoreAudio.AttachDefaultEndpoint(out string? _);
        Assert(!attachResult, "AttachDefaultEndpoint returns false after Shutdown()");
        Assert(!CoreAudio.IsAvailable, "CoreAudio remains unavailable after aborted attach");

        // 2. Multi-threaded race test between AttachDefaultEndpoint and Shutdown
        int raceExceptions = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var token = cts.Token;

        var t1 = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    CoreAudio.AttachDefaultEndpoint();
                }
                catch
                {
                    Interlocked.Increment(ref raceExceptions);
                }
                Thread.Sleep(1);
            }
        });

        var t2 = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    CoreAudio.Shutdown();
                    _ = CoreAudio.GetCurrent();
                    _ = CoreAudio.GetDefaultDeviceName();
                    CoreAudio.SetLevel(0.5f);
                    CoreAudio.SetMute(false);
                }
                catch
                {
                    Interlocked.Increment(ref raceExceptions);
                }
                Thread.Sleep(1);
            }
        });

        var raceAll = Task.WhenAll(t1, t2);
        while (!raceAll.IsCompleted)
        {
            Pump(10);
        }
        CoreAudio.Shutdown();

        AssertEq(0, raceExceptions, "Shutdown versus attach race completed with zero exceptions");
    }
}
