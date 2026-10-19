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
using WindowsDynamicBar.Services.Privacy;
using WindowsDynamicBar.Services.Settings;
using WindowsDynamicBar.Services.SystemInfo;
using WindowsDynamicBar.Services.SystemMonitor;
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
        Run("SystemMonitorService: repeated activation, deactivation, and disposal are idempotent", TestSystemMonitorService_LifecycleAndIdempotency);
        Run("SystemMonitorService: Current returns consistent snapshot under concurrent reads", TestSystemMonitorService_CurrentConsistentSnapshot);
        Run("SystemMonitorService: sample and dispose coordination prevents overlapping access and deadlock", TestSystemMonitorService_SampleAndDisposeCoordination);
        Run("BluetoothService: Start is idempotent and handles watcher failure gracefully", TestBluetoothService_StartIdempotencyAndFailureHandling);
        Run("BluetoothService: Dispose stops watcher when EnumerationCompleted and detaches all handlers", TestBluetoothService_Dispose_StopsWatcherWhenEnumerationCompleted);
        Run("BluetoothService: device classification and metadata mapping", TestBluetoothService_DeviceEnumerationAndClassification);
        Run("BluetoothService: event publishing, FeatureBluetooth toggle, and post-disposal suppression", TestBluetoothService_EventsAndPostDisposalSuppression);
        Run("DownloadService: Start and Dispose are idempotent and handle invalid directories gracefully", TestDownloadService_StartAndDisposeIdempotency);
        Run("DownloadService: event publication occurs outside lock without lock inversion or deadlock", TestDownloadService_LockInversionAndReentrancy);
        Run("DownloadService: callbacks and event publications are completely suppressed after disposal", TestDownloadService_PostDisposalCallbackSuppression);
        Run("DownloadService: temporary-extension filtering, name cleaning, and speed formatting", TestDownloadService_ExtensionFilteringAndCleanName);
        Run("DownloadService: rename transitions and completion announcements respect feature settings", TestDownloadService_RenameAndCompletion);
        Run("DownloadService: progress ticks compute speed, estimate ETA, and suppress stalled updates", TestDownloadService_ProgressSpeedAndETA);
        Run("PrivacyService: Start and Dispose are idempotent, thread-safe, and reject calls after disposal", TestPrivacyService_StartAndDisposeIdempotency);
        Run("PrivacyService: Current returns consistent thread-safe snapshots under concurrent recompute", TestPrivacyService_CurrentThreadSafeSnapshot);
        Run("PrivacyService: in-flight scan racing with Dispose suppresses state mutation and event dispatch", TestPrivacyService_InFlightScanVsDisposeRace);
        Run("PrivacyService: post-disposal callbacks and BarEvents are suppressed", TestPrivacyService_PostDisposalSuppression);
        Run("PrivacyService: consent-store subkey parsing, path decoding, and active calculation", TestPrivacyService_ParsingAndDecoding);
        Run("PrivacyService: state transitions and BarEvent publishing respect feature settings", TestPrivacyService_StateTransitionsAndBarEvents);
        Run("BrightnessService: Start and Dispose are idempotent, thread-safe, and reject calls after disposal", TestBrightnessService_StartAndDisposeIdempotency);
        Run("BrightnessService: Level returns consistent thread-safe snapshots under concurrent reads and writes", TestBrightnessService_LevelThreadSafeSnapshot);
        Run("BrightnessService: in-flight callback racing with Dispose suppresses state mutation and deadlock", TestBrightnessService_InFlightCallbackVsDisposeRace);
        Run("BrightnessService: post-disposal callbacks, SetLevel, and BarEvents are suppressed", TestBrightnessService_PostDisposalSuppression);
        Run("BrightnessService: deduplication window, delta thresholds, and property extraction", TestBrightnessService_DeduplicationAndThresholds);
        Run("BrightnessService: SetLevel clamping, BarEvent metadata, and FeatureBrightness toggle", TestBrightnessService_SetLevelAndBarEvents);
        Run("BrightnessService: concurrent Start vs Dispose race stops watcher and prevents leak", TestBrightnessService_StartVsDisposeRace);
        Run("NetworkService: Start and Dispose are idempotent, thread-safe, and reject calls after disposal", TestNetworkService_StartAndDisposeIdempotency);
        Run("NetworkService: subscription ownership enforces exactly one subscription and detachment", TestNetworkService_SubscriptionOwnership);
        Run("NetworkService: Current returns consistent thread-safe snapshots under concurrent updates", TestNetworkService_CurrentThreadSafeSnapshot);
        Run("NetworkService: in-flight callback racing with Dispose suppresses state mutation and event dispatch", TestNetworkService_InFlightCallbackVsDisposeRace);
        Run("NetworkService: post-disposal callbacks and BarEvents are suppressed", TestNetworkService_PostDisposalSuppression);
        Run("NetworkService: signal jitter vs meaningful transitions, BarEvent metadata, and feature toggle", TestNetworkService_SignalJitterVsMeaningfulTransitions);
        Run("NetworkService: concurrent Start vs Dispose race stops watcher and prevents leak", TestNetworkService_StartVsDisposeRace);
        Run("BatteryService: Start and Dispose are idempotent, thread-safe, and reject calls after disposal", TestBatteryService_StartAndDisposeIdempotency);
        Run("BatteryService: subscription ownership enforces exactly one subscription and detachment", TestBatteryService_SubscriptionOwnership);
        Run("BatteryService: Current returns consistent thread-safe snapshots under concurrent updates", TestBatteryService_CurrentThreadSafeSnapshot);
        Run("BatteryService: in-flight callback racing with Dispose suppresses state mutation and event dispatch", TestBatteryService_InFlightCallbackVsDisposeRace);
        Run("BatteryService: post-disposal callbacks and BarEvents are suppressed", TestBatteryService_PostDisposalSuppression);
        Run("BatteryService: alert transitions, routine drift suppression, and FeatureBattery setting", TestBatteryService_AlertTransitionsAndFeatureToggle);
        Run("BatteryService: concurrent Start vs Dispose race stops watcher and prevents leak", TestBatteryService_StartVsDisposeRace);
        Run("BatteryService: concurrent Start vs Dispose during timer creation prevents leak", TestBatteryService_StartVsDisposeDuringTimerCreation);
        Run("BatteryService: startup failure safely resets state and allows clean retry", TestBatteryService_StartupFailureAndRetry);
        Run("SystemEventsBatteryWatcher: partial subscription rollback and safe retry without leaks", TestSystemEventsBatteryWatcher_PartialSubscriptionRollbackAndRetry);
        Run("ScreenshotService: sequential and concurrent Start are idempotent without duplicate watchers", TestScreenshotService_StartIdempotency);
        Run("ScreenshotService: repeated Dispose is idempotent and Start after Dispose is rejected", TestScreenshotService_DisposeIdempotencyAndRejection);
        Run("ScreenshotService: watcher ownership enforces exactly one subscription, stop, and disposal", TestScreenshotService_WatcherOwnershipAndCleanup);
        Run("ScreenshotService: in-flight callback racing with Dispose suppresses event dispatch", TestScreenshotService_InFlightCallbackVsDisposeRace);
        Run("ScreenshotService: late callbacks after disposal are completely suppressed", TestScreenshotService_LateCallbacksAfterDisposal);
        Run("ScreenshotService: extension filtering and FeatureScreenshots toggle behavior", TestScreenshotService_ExtensionFilteringAndFeatureToggle);
        Run("ScreenshotService: missing screenshot directory and watcher startup failure safely reset state", TestScreenshotService_MissingDirectoryAndStartupFailure);
        Run("ScreenshotService: concurrent Start vs Dispose race stops watcher and prevents leak", TestScreenshotService_StartVsDisposeRace);
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

    private static void TestSystemMonitorService_LifecycleAndIdempotency()
    {
        var settings = SettingsService.Instance;
        using var svc = new SystemMonitorService(settings);

        Assert(!svc.IsActive, "SystemMonitorService is inactive initially");

        // Repeated Activate is idempotent
        svc.Activate();
        Assert(svc.IsActive, "Service is active after Activate()");
        svc.Activate();
        Assert(svc.IsActive, "Service remains active on redundant Activate()");

        // Repeated Deactivate is idempotent
        svc.Deactivate();
        Assert(!svc.IsActive, "Service is inactive after Deactivate()");
        svc.Deactivate();
        Assert(!svc.IsActive, "Service remains inactive on redundant Deactivate()");

        // Re-activation works
        svc.Activate();
        Assert(svc.IsActive, "Service can be reactivated");

        // Dispose stops activity
        svc.Dispose();
        Assert(!svc.IsActive, "Service is inactive after Dispose()");

        // Repeated Dispose is safe and idempotent
        svc.Dispose();
        Assert(!svc.IsActive, "Service remains inactive on redundant Dispose()");

        // Activate after Dispose is safely ignored and does not reactivate
        svc.Activate();
        Assert(!svc.IsActive, "Activate after Dispose does not reactivate service");

        // Deactivate after Dispose is safe
        svc.Deactivate();
        Assert(!svc.IsActive, "Deactivate after Dispose is safe");
    }

    private static void TestSystemMonitorService_CurrentConsistentSnapshot()
    {
        var settings = SettingsService.Instance;
        using var svc = new SystemMonitorService(settings);

        var snap = svc.Current;
        Assert(snap is not null, "Current snapshot is non-null");
        Assert(snap!.Cpu >= 0 && snap.Cpu <= 100, "CPU is within 0..100");
        Assert(snap.Gpu >= 0 && snap.Gpu <= 100, "GPU is within 0..100");
        Assert(snap.Ram >= 0 && snap.Ram <= 100, "RAM is within 0..100");
        Assert(snap.AvailableBytes >= 0, "AvailableBytes is non-negative");

        // Verify concurrent reads of Current under sample execution produce valid snapshots without throwing
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var token = cts.Token;
        int exceptions = 0;

        var readers = new Task[4];
        for (int i = 0; i < readers.Length; i++)
        {
            readers[i] = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var s = svc.Current;
                        if (s.Cpu < 0 || s.Cpu > 100 || s.Ram < 0 || s.Ram > 100)
                        {
                            Interlocked.Increment(ref exceptions);
                        }
                        _ = svc.IsActive;
                        _ = svc.CpuAvailable;
                        _ = svc.GpuAvailable;
                    }
                    catch
                    {
                        Interlocked.Increment(ref exceptions);
                    }
                    Thread.Sleep(1);
                }
            });
        }

        var sampler = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    svc.Sample();
                }
                catch
                {
                    Interlocked.Increment(ref exceptions);
                }
                Thread.Sleep(5);
            }
        });

        var allTasks = new Task[readers.Length + 1];
        Array.Copy(readers, allTasks, readers.Length);
        allTasks[readers.Length] = sampler;
        var all = Task.WhenAll(allTasks);

        while (!all.IsCompleted)
        {
            Pump(10);
        }

        AssertEq(0, exceptions, "Concurrent Current reads and Sample() executed without exceptions or invalid state");
    }

    private static void TestSystemMonitorService_SampleAndDisposeCoordination()
    {
        var settings = SettingsService.Instance;
        var svc = new SystemMonitorService(settings);
        svc.Activate();

        int statsChangedCount = 0;
        bool reentrantReadSucceeded = false;
        svc.StatsChanged += stats =>
        {
            Interlocked.Increment(ref statsChangedCount);
            // Verify lock inversion prevention: calling Current or IsActive from within StatsChanged callback
            // (which acquires _sync) does NOT deadlock or block because StatsChanged was invoked outside _sync.
            try
            {
                _ = svc.Current;
                _ = svc.IsActive;
                reentrantReadSucceeded = true;
            }
            catch { }
        };

        // Calling Sample while active invokes StatsChanged (if values change or priming)
        svc.Sample();

        // Verify multi-threaded race between Sample, Activate/Deactivate, and Dispose
        int raceExceptions = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var token = cts.Token;

        var sampleWorkers = new Task[3];
        for (int i = 0; i < sampleWorkers.Length; i++)
        {
            sampleWorkers[i] = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        svc.Sample();
                    }
                    catch
                    {
                        Interlocked.Increment(ref raceExceptions);
                    }
                    Thread.Sleep(1);
                }
            });
        }

        var toggleWorker = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    svc.Activate();
                    Thread.Sleep(2);
                    svc.Deactivate();
                }
                catch
                {
                    Interlocked.Increment(ref raceExceptions);
                }
                Thread.Sleep(1);
            }
        });

        // Let the workers run for a bit, then dispose the service in the middle of sampling
        Thread.Sleep(50);
        svc.Dispose();

        var allRaceTasks = new Task[sampleWorkers.Length + 1];
        Array.Copy(sampleWorkers, allRaceTasks, sampleWorkers.Length);
        allRaceTasks[sampleWorkers.Length] = toggleWorker;
        var raceAll = Task.WhenAll(allRaceTasks);

        while (!raceAll.IsCompleted)
        {
            Pump(10);
        }

        // After disposal, calling Sample is a safe no-op
        svc.Sample();

        if (statsChangedCount > 0)
        {
            Assert(reentrantReadSucceeded, "Re-entrant read of Current inside StatsChanged callback succeeded without deadlock");
        }

        AssertEq(0, raceExceptions, "Sample and Dispose coordination ran without exceptions");
        Assert(!svc.IsActive, "Service remains inactive and disposed");
    }

    private sealed class FakeBluetoothWatcher : IBluetoothWatcher
    {
        public Windows.Devices.Enumeration.DeviceWatcherStatus Status { get; set; } = Windows.Devices.Enumeration.DeviceWatcherStatus.Created;
        public int StartCallCount { get; private set; }
        public int StopCallCount { get; private set; }

        public event Action<string, string?, bool>? DeviceAdded;
        public event Action<string>? DeviceRemoved;
        public event Action<string, bool?>? DeviceUpdated;
        public event Action? EnumerationCompleted;

        public void Start()
        {
            StartCallCount++;
            Status = Windows.Devices.Enumeration.DeviceWatcherStatus.Started;
        }

        public void Stop()
        {
            StopCallCount++;
            Status = Windows.Devices.Enumeration.DeviceWatcherStatus.Stopped;
        }

        public void FireDeviceAdded(string id, string? name, bool connected) => DeviceAdded?.Invoke(id, name, connected);
        public void FireDeviceRemoved(string id) => DeviceRemoved?.Invoke(id);
        public void FireDeviceUpdated(string id, bool? connected) => DeviceUpdated?.Invoke(id, connected);
        public void FireEnumerationCompleted()
        {
            Status = Windows.Devices.Enumeration.DeviceWatcherStatus.EnumerationCompleted;
            EnumerationCompleted?.Invoke();
        }

        public bool HasSubscribers => DeviceAdded != null || DeviceRemoved != null || DeviceUpdated != null || EnumerationCompleted != null;
    }

    private static void TestBluetoothService_StartIdempotencyAndFailureHandling()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var fakeWatcher = new FakeBluetoothWatcher();
        int factoryCalls = 0;
        using (var svc = new BluetoothService(bus, settings, () =>
        {
            factoryCalls++;
            return fakeWatcher;
        }))
        {
            // First start creates watcher and starts it
            svc.Start();
            AssertEq(1, factoryCalls, "Watcher factory called on first Start");
            AssertEq(1, fakeWatcher.StartCallCount, "Watcher Start called once");
            AssertEq(Windows.Devices.Enumeration.DeviceWatcherStatus.Started, fakeWatcher.Status, "Watcher status is Started");

            // Redundant start is idempotent
            svc.Start();
            AssertEq(1, factoryCalls, "Watcher factory not called again on second Start");
            AssertEq(1, fakeWatcher.StartCallCount, "Watcher Start not called again on second Start");
        }
        AssertEq(1, fakeWatcher.StopCallCount, "Disposal stopped the watcher");

        // Failure handling: factory throws exception
        int failFactoryCalls = 0;
        using (var failSvc = new BluetoothService(bus, settings, () =>
        {
            failFactoryCalls++;
            throw new InvalidOperationException("Bluetooth radio is powered off or disabled");
        }))
        {
            failSvc.Start();
            AssertEq(1, failFactoryCalls, "Watcher factory called");
            AssertEq(0, failSvc.KnownDevices.Count, "Known devices empty on failed start");

            // Verify safe disposal after failed start
            failSvc.Dispose();
        }
    }

    private static void TestBluetoothService_Dispose_StopsWatcherWhenEnumerationCompleted()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        // 1. Stopped when status is EnumerationCompleted
        var watcher1 = new FakeBluetoothWatcher();
        using (var svc1 = new BluetoothService(bus, settings, () => watcher1))
        {
            svc1.Start();
            AssertEq(Windows.Devices.Enumeration.DeviceWatcherStatus.Started, watcher1.Status, "Status is Started");
            Assert(watcher1.HasSubscribers, "Watcher has event subscribers");

            // Simulate completed enumeration
            watcher1.FireEnumerationCompleted();
            AssertEq(Windows.Devices.Enumeration.DeviceWatcherStatus.EnumerationCompleted, watcher1.Status, "Status is EnumerationCompleted");

            // Dispose must stop the watcher even when status is EnumerationCompleted!
            svc1.Dispose();
            AssertEq(1, watcher1.StopCallCount, "Stop called when status was EnumerationCompleted");
            AssertEq(Windows.Devices.Enumeration.DeviceWatcherStatus.Stopped, watcher1.Status, "Status is Stopped");
            Assert(!watcher1.HasSubscribers, "All event handlers detached on Dispose");
        }

        // 2. Stopped when status is Started
        var watcher2 = new FakeBluetoothWatcher();
        using (var svc2 = new BluetoothService(bus, settings, () => watcher2))
        {
            svc2.Start();
            AssertEq(Windows.Devices.Enumeration.DeviceWatcherStatus.Started, watcher2.Status, "Status is Started");

            svc2.Dispose();
            AssertEq(1, watcher2.StopCallCount, "Stop called when status was Started");
            AssertEq(Windows.Devices.Enumeration.DeviceWatcherStatus.Stopped, watcher2.Status, "Status is Stopped");
            Assert(!watcher2.HasSubscribers, "All event handlers detached on Dispose");
        }

        // 3. Not stopped if status was Created (never started)
        var watcher3 = new FakeBluetoothWatcher();
        using (var svc3 = new BluetoothService(bus, settings, () => watcher3))
        {
            // Do not call Start()
            svc3.Dispose();
            AssertEq(0, watcher3.StopCallCount, "Stop not called when status was Created");
            AssertEq(Windows.Devices.Enumeration.DeviceWatcherStatus.Created, watcher3.Status, "Status remained Created");
        }
    }

    private static void TestBluetoothService_DeviceEnumerationAndClassification()
    {
        // 1. Classification tests
        AssertEq("Audio", BluetoothService.Classify("Apple AirPods Pro"), "AirPods is Audio");
        AssertEq("Audio", BluetoothService.Classify("Sony WH-1000XM5 Headphone"), "Headphone is Audio");
        AssertEq("Audio", BluetoothService.Classify("Bose QuietComfort Earphones"), "Earphone is Audio");
        AssertEq("Audio", BluetoothService.Classify("Galaxy Buds Live"), "Buds is Audio");
        AssertEq("Audio", BluetoothService.Classify("Jabra Evolve Headset"), "Headset is Audio");
        AssertEq("Audio", BluetoothService.Classify("JBL Flip 6 Bluetooth Speaker"), "Speaker is Audio");
        AssertEq("Gamepad", BluetoothService.Classify("Xbox Wireless Controller"), "Xbox is Gamepad");
        AssertEq("Gamepad", BluetoothService.Classify("DualSense Wireless Controller"), "Controller is Gamepad");
        AssertEq("Gamepad", BluetoothService.Classify("Generic Bluetooth Gamepad"), "Gamepad is Gamepad");
        AssertEq("Mouse", BluetoothService.Classify("Logitech MX Master 3S Mouse"), "Mouse is Mouse");
        AssertEq("Keyboard", BluetoothService.Classify("Keychron K2 Bluetooth Keyboard"), "Keyboard is Keyboard");
        AssertEq("Watch", BluetoothService.Classify("Galaxy Watch 5 Pro"), "Watch is Watch");
        AssertEq("Device", BluetoothService.Classify("Unknown Peripheral 42"), "Generic device fallback is Device");

        // 2. ToInfo mapping
        var info1 = BluetoothService.ToInfo("Logitech Mouse", "dev-mouse-1", true);
        Assert(info1 is not null, "ToInfo returned non-null");
        AssertEq("Logitech Mouse", info1!.Name, "Name matches");
        AssertEq("Mouse", info1.Kind, "Kind is Mouse");
        AssertEq(true, info1.Connected, "Connected is true");
        AssertEq("dev-mouse-1", info1.Id, "Id matches");

        // 3. Fallback for null or whitespace name
        var infoNull = BluetoothService.ToInfo(null, "dev-null-id", false);
        AssertEq("Bluetooth device", infoNull!.Name, "Null name falls back to 'Bluetooth device'");
        AssertEq("Device", infoNull.Kind, "Fallback name classified as Device");
        AssertEq(false, infoNull.Connected, "Connected is false");

        var infoEmpty = BluetoothService.ToInfo("   ", "dev-empty-id", true);
        AssertEq("Bluetooth device", infoEmpty!.Name, "Whitespace name falls back to 'Bluetooth device'");
    }

    private static void TestBluetoothService_EventsAndPostDisposalSuppression()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureBluetooth = true);

        var watcher = new FakeBluetoothWatcher();
        var svc = new BluetoothService(bus, settings, () => watcher);
        svc.Start();

        var deviceChangedEvents = new List<(BluetoothDeviceInfo Info, bool Connected)>();
        svc.DeviceChanged += (dev, conn) => deviceChangedEvents.Add((dev, conn));

        var barEvents = new List<BarEvent>();
        bus.EventRaised += e =>
        {
            if (e.Kind == BarEventKind.Bluetooth)
            {
                barEvents.Add(e);
            }
        };

        // 1. Before enumeration completed: devices added during initial scan populate KnownDevices
        // but do not announce events (avoiding alert spam on app launch)
        watcher.FireDeviceAdded("id-airpods", "AirPods", true);
        AssertEq(1, svc.KnownDevices.Count, "Known devices count is 1");
        AssertEq(0, deviceChangedEvents.Count, "No DeviceChanged during initial enumeration");
        AssertEq(0, barEvents.Count, "No BarEvent during initial enumeration");

        // 2. Fire enumeration completed
        watcher.FireEnumerationCompleted();

        // 3. Device added after enumeration completes: raises DeviceChanged and BarEvent
        watcher.FireDeviceAdded("id-mouse", "Logitech Mouse", true);
        AssertEq(2, svc.KnownDevices.Count, "Known devices count is 2");
        AssertEq(1, deviceChangedEvents.Count, "DeviceChanged raised for new device");
        AssertEq("Logitech Mouse", deviceChangedEvents[0].Info.Name, "Device name matches");
        AssertEq(true, deviceChangedEvents[0].Connected, "Device connected is true");
        AssertEq(1, barEvents.Count, "BarEvent raised on bus");
        AssertEq("Logitech Mouse", barEvents[0].Title, "BarEvent title is device name");
        AssertEq("Connected", barEvents[0].Subtitle, "BarEvent subtitle is Connected");
        AssertEq(45, barEvents[0].Priority, "Bluetooth priority is 45");

        // 4. Device updated: disconnect mouse
        watcher.FireDeviceUpdated("id-mouse", false);
        AssertEq(2, deviceChangedEvents.Count, "DeviceChanged raised for update");
        AssertEq(false, deviceChangedEvents[1].Connected, "Updated connected is false");
        AssertEq(2, barEvents.Count, "BarEvent raised for disconnect");
        AssertEq("Disconnected", barEvents[1].Subtitle, "BarEvent subtitle is Disconnected");

        // Redundant update with same connected state does NOT re-announce
        watcher.FireDeviceUpdated("id-mouse", false);
        AssertEq(2, deviceChangedEvents.Count, "Redundant update suppressed");
        AssertEq(2, barEvents.Count, "Redundant BarEvent suppressed");

        // 5. Device removed
        watcher.FireDeviceAdded("id-kb", "Keychron Keyboard", true);
        AssertEq(3, svc.KnownDevices.Count, "Known devices count is 3");
        watcher.FireDeviceRemoved("id-kb");
        AssertEq(2, svc.KnownDevices.Count, "Device removed from KnownDevices");

        // 6. FeatureBluetooth = false: DeviceChanged still fires, but bus event is suppressed
        settings.Update(s => s.FeatureBluetooth = false);
        int busCountBefore = barEvents.Count;
        int devCountBefore = deviceChangedEvents.Count;

        watcher.FireDeviceUpdated("id-mouse", true);
        AssertEq(devCountBefore + 1, deviceChangedEvents.Count, "DeviceChanged still fires when feature disabled");
        AssertEq(busCountBefore, barEvents.Count, "BarEvent suppressed when FeatureBluetooth is false");

        // 7. Post-disposal suppression: after Dispose, late callbacks must be completely suppressed
        svc.Dispose();
        int devCountAfterDispose = deviceChangedEvents.Count;
        int knownCountAfterDispose = svc.KnownDevices.Count;

        watcher.FireDeviceAdded("id-late", "Late Speaker", true);
        watcher.FireDeviceUpdated("id-mouse", false);
        watcher.FireDeviceRemoved("id-airpods");

        AssertEq(devCountAfterDispose, deviceChangedEvents.Count, "No DeviceChanged callbacks invoked after disposal");
        AssertEq(knownCountAfterDispose, svc.KnownDevices.Count, "Known devices not mutated after disposal");
    }

    private static void TestDownloadService_StartAndDisposeIdempotency()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        string tempDir = Path.Combine(Path.GetTempPath(), $"wdb-dl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var svc = new DownloadService(bus, settings, tempDir);
            Assert(!svc.IsStarted, "Not started before Start()");
            Assert(!svc.IsDisposed, "Not disposed before Dispose()");
            AssertEq(0, svc.ActiveCount, "ActiveCount starts at 0");

            // 1. Initial Start succeeds
            svc.Start();
            Assert(svc.IsStarted, "IsStarted true after Start()");
            Assert(!svc.IsDisposed, "IsDisposed false while running");

            // 2. Redundant Start is idempotent
            svc.Start();
            Assert(svc.IsStarted, "IsStarted remains true after redundant Start()");

            // 3. Initial Dispose succeeds
            svc.Dispose();
            Assert(svc.IsDisposed, "IsDisposed true after Dispose()");
            Assert(!svc.IsStarted, "IsStarted false after Dispose()");

            // 4. Redundant Dispose is idempotent
            svc.Dispose();
            Assert(svc.IsDisposed, "IsDisposed remains true after redundant Dispose()");

            // 5. Start after Dispose is rejected
            svc.Start();
            Assert(!svc.IsStarted, "Cannot start after service is disposed");
            Assert(svc.IsDisposed, "Service remains disposed");

            // 6. Graceful failure on non-existent directory
            string nonExistentDir = Path.Combine(Path.GetTempPath(), $"wdb-nonexistent-{Guid.NewGuid():N}");
            using (var failSvc = new DownloadService(bus, settings, nonExistentDir))
            {
                failSvc.Start();
                Assert(!failSvc.IsStarted, "Start on non-existent directory does not mark service started");
                Assert(!failSvc.IsDisposed, "Service is not marked disposed on failed start");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void TestDownloadService_LockInversionAndReentrancy()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureDownloads = true);

        using var svc = new DownloadService(bus, settings, customDirectory: null);

        var eventRaisedGate = new ManualResetEventSlim(false);
        var reentrancySucceeded = false;
        var threadLock = new object();
        var lockInversionResolved = false;

        // When bus raises the download event, simulate an external subscriber that locks threadLock
        // and re-entrantly queries DownloadService.ActiveCount
        bus.EventRaised += evt =>
        {
            if (evt.Kind == BarEventKind.Download && evt.Title == "Downloading…")
            {
                lock (threadLock)
                {
                    // Re-entrantly query ActiveCount while holding threadLock
                    int active = svc.ActiveCount;
                    if (active >= 1) reentrancySucceeded = true;
                }
                eventRaisedGate.Set();
            }
        };

        // In a separate thread, hold threadLock and query ActiveCount (which requires _sync)
        // If OnFileActivity held _sync while calling _bus.Raise, lock inversion would cause DEADLOCK.
        var crossThreadDone = new ManualResetEventSlim(false);
        var backgroundThread = new Thread(() =>
        {
            lock (threadLock)
            {
                // Verify background thread can read ActiveCount under threadLock
                int count = svc.ActiveCount;
                if (count == 0) lockInversionResolved = true;
            }
            crossThreadDone.Set();
        });
        backgroundThread.IsBackground = true;
        backgroundThread.Start();
        Assert(crossThreadDone.Wait(2000), "Background thread acquired lock and queried ActiveCount without deadlock");

        // Now process file activity on main thread
        svc.ProcessFileActivityCore("C:\\Downloads\\testfile.crdownload", "testfile.crdownload", 1024, isNew: true);

        Assert(eventRaisedGate.Wait(2000), "EventBus raised Download event");
        Assert(reentrancySucceeded, "Re-entrant query of ActiveCount succeeded inside EventRaised callback");
        Assert(lockInversionResolved, "Lock inversion safety verified");
        AssertEq(1, svc.ActiveCount, "Active count is 1 after file activity");
    }

    private static void TestDownloadService_PostDisposalCallbackSuppression()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureDownloads = true);

        var progressCalls = 0;
        var busEvents = new List<BarEvent>();
        bus.EventRaised += e => busEvents.Add(e);

        var svc = new DownloadService(bus, settings, customDirectory: null);
        svc.DownloadProgress += (name, frac, speed) => progressCalls++;

        // Track a file before disposal
        svc.ProcessFileActivityCore("C:\\Downloads\\active.crdownload", "active.crdownload", 2048, isNew: true);
        AssertEq(1, svc.ActiveCount, "Active count is 1");
        AssertEq(1, busEvents.Count, "1 initial bus event raised");

        // Dispose the service
        svc.Dispose();
        Assert(svc.IsDisposed, "Service is disposed");
        AssertEq(0, svc.ActiveCount, "Active items cleared on disposal");

        int busEventsBeforeLate = busEvents.Count;

        // Fire all callback paths post-disposal: must be completely suppressed
        svc.ProcessFileActivityCore("C:\\Downloads\\late.crdownload", "late.crdownload", 4096, isNew: true);
        svc.OnFileActivity("C:\\Downloads\\late2.crdownload", isNew: true);
        svc.OnFileRenamed("C:\\Downloads\\active.crdownload", "C:\\Downloads\\active.zip");
        svc.OnFileDone("C:\\Downloads\\active.crdownload", completed: true);
        svc.TickProgress(path => 8192, DateTime.UtcNow);
        svc.AnnounceComplete("late.zip");

        AssertEq(0, svc.ActiveCount, "Active count remains 0 after post-disposal calls");
        AssertEq(0, progressCalls, "Zero DownloadProgress callbacks fired after disposal");
        AssertEq(busEventsBeforeLate, busEvents.Count, "Zero BarEvents published after disposal");
    }

    private static void TestDownloadService_ExtensionFilteringAndCleanName()
    {
        // 1. IsTempDownload: recognizes known browser extensions
        Assert(DownloadService.IsTempDownload("file.crdownload"), ".crdownload is temp");
        Assert(DownloadService.IsTempDownload("file.part"), ".part is temp");
        Assert(DownloadService.IsTempDownload("file.download"), ".download is temp");
        Assert(DownloadService.IsTempDownload("file.opdownload"), ".opdownload is temp");
        Assert(DownloadService.IsTempDownload("file.partial"), ".partial is temp");
        Assert(DownloadService.IsTempDownload("FILE.CRDOWNLOAD"), "Uppercase .CRDOWNLOAD is temp");
        Assert(DownloadService.IsTempDownload("FILE.PART"), "Uppercase .PART is temp");

        // Non-temp extensions
        Assert(!DownloadService.IsTempDownload("file.zip"), ".zip is not temp");
        Assert(!DownloadService.IsTempDownload("file.exe"), ".exe is not temp");
        Assert(!DownloadService.IsTempDownload("file.tmp"), ".tmp is not temp");
        Assert(!DownloadService.IsTempDownload("file.downloading"), ".downloading is not temp");
        Assert(!DownloadService.IsTempDownload(""), "empty string is not temp");

        // 2. CleanName: strips temp extension properly
        AssertEq("video.mp4", DownloadService.CleanName("video.mp4.crdownload"), "CleanName .crdownload");
        AssertEq("archive.tar.gz", DownloadService.CleanName("archive.tar.gz.part"), "CleanName .part");
        AssertEq("installer.exe", DownloadService.CleanName("installer.exe.download"), "CleanName .download");
        AssertEq("browser", DownloadService.CleanName("browser.opdownload"), "CleanName .opdownload");
        AssertEq("data.csv", DownloadService.CleanName("data.csv.partial"), "CleanName .partial");
        AssertEq("normal.txt", DownloadService.CleanName("normal.txt"), "CleanName without temp ext unchanged");

        // 3. FormatSpeed: formatting thresholds
        AssertEq("…", DownloadService.FormatSpeed(0), "Speed 0 formatted as ellipsis");
        AssertEq("…", DownloadService.FormatSpeed(-5), "Negative speed formatted as ellipsis");
        AssertEq("500 B/s", DownloadService.FormatSpeed(500), "Bytes/sec formatting");
        AssertEq("1024 B/s", DownloadService.FormatSpeed(1024), "1024 B/s formatting");
        AssertEq("10 KB/s", DownloadService.FormatSpeed(10 * 1024), "10 KB/s formatting");
        AssertEq("1.5 MB/s", DownloadService.FormatSpeed(1.5 * 1024 * 1024), "1.5 MB/s formatting");
    }

    private static void TestDownloadService_RenameAndCompletion()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureDownloads = true);

        var events = new List<BarEvent>();
        bus.EventRaised += e => events.Add(e);

        using var svc = new DownloadService(bus, settings, customDirectory: null);

        // Track a download
        string tempPath = "C:\\Downloads\\Unconfirmed 12345.crdownload";
        svc.ProcessFileActivityCore(tempPath, "Unconfirmed 12345.crdownload", 1000, isNew: true);
        AssertEq(1, svc.ActiveCount, "1 active download");
        AssertEq(1, events.Count, "1 download event raised");
        AssertEq(BarEventKind.Download, events[0].Kind, "Kind is Download");
        AssertEq("Downloading…", events[0].Title, "Title is Downloading…");
        AssertEq("Unconfirmed 12345", events[0].Subtitle, "Subtitle is sanitized name");

        // Scenario A: Rename temp to temp (Chrome renaming Unconfirmed -> actual.crdownload)
        string renamedTemp = "C:\\Downloads\\actual.crdownload";
        svc.OnFileRenamed(tempPath, renamedTemp);
        AssertEq(1, svc.ActiveCount, "Active count is still 1 after temp-to-temp rename");
        AssertEq(1, events.Count, "No new BarEvent for temp-to-temp rename");

        // Scenario B: Rename temp to final (download completed: actual.crdownload -> actual.zip)
        string finalPath = "C:\\Downloads\\actual.zip";
        bus.Dismiss();
        svc.OnFileRenamed(renamedTemp, finalPath);
        AssertEq(0, svc.ActiveCount, "Active count is 0 after completion rename");
        AssertEq(2, events.Count, "Completion toast event raised");
        AssertEq(BarEventKind.Toast, events[1].Kind, "Event kind is Toast");
        AssertEq("Download complete", events[1].Title, "Toast title is Download complete");
        AssertEq("actual.zip", events[1].Subtitle, "Toast subtitle is filename");
        AssertEq(65, events[1].Priority, "Priority is 65");

        // Scenario C: OnFileDone with completed=true
        bus.Dismiss();
        string docTemp = "C:\\Downloads\\document.part";
        svc.ProcessFileActivityCore(docTemp, "document.part", 5000, isNew: true);
        AssertEq(1, svc.ActiveCount, "1 active download");
        bus.Dismiss();
        svc.OnFileDone(docTemp, completed: true);
        AssertEq(0, svc.ActiveCount, "Active count is 0 after OnFileDone");
        AssertEq(4, events.Count, "Completion toast raised for OnFileDone (1 initial + 1 completion)");
        AssertEq("document", events[3].Subtitle, "Toast subtitle is document");

        // Scenario D: FeatureDownloads = false suppresses completion announcements
        settings.Update(s => s.FeatureDownloads = false);
        int eventCountBefore = events.Count;
        svc.AnnounceComplete("silent.zip");
        AssertEq(eventCountBefore, events.Count, "Completion announcement suppressed when FeatureDownloads is false");
    }

    private static void TestDownloadService_ProgressSpeedAndETA()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureDownloads = true);

        var busEvents = new List<BarEvent>();
        bus.EventRaised += e => busEvents.Add(e);

        var progressUpdates = new List<(string name, double frac, double speed)>();
        using var svc = new DownloadService(bus, settings, customDirectory: null);
        svc.DownloadProgress += (name, frac, speed) => progressUpdates.Add((name, frac, speed));

        string path = "C:\\Downloads\\bigfile.crdownload";
        var t0 = DateTime.UtcNow;
        svc.ProcessFileActivityCore(path, "bigfile.crdownload", 1000, isNew: true);

        // 1. Advance time by 2 seconds with 4000 bytes added (total 5000, speed = 4000/2 = 2000 B/s)
        var t1 = t0.AddSeconds(2);
        svc.TickProgress(p => p == path ? 5000 : null, t1);

        AssertEq(1, progressUpdates.Count, "Progress callback invoked on moving download");
        AssertEq("bigfile", progressUpdates[0].name, "Progress name sanitized");
        Assert(progressUpdates[0].speed >= 1999 && progressUpdates[0].speed <= 2001, "Speed computed at ~2000 B/s");
        Assert(progressUpdates[0].frac > 0 && progressUpdates[0].frac <= 1.0, "Fraction bounded between 0 and 1");

        // Verify BarEvent was published with tick semantics
        var tickEvent = busEvents.LastOrDefault(e => e.Kind == BarEventKind.Download && e.IsTick);
        Assert(tickEvent is not null, "Tick BarEvent was published");
        AssertEq("bigfile", tickEvent!.Title, "Event title is filename");
        AssertEq(35, tickEvent.Priority, "Tick priority is 35");

        // 2. Stalled download (no bytes added): progress is not emitted as a bar event
        int progressCountBeforeStall = progressUpdates.Count;
        int busCountBeforeStall = busEvents.Count;
        var t2 = t1.AddSeconds(2);
        svc.TickProgress(p => p == path ? 5000 : null, t2);

        AssertEq(progressCountBeforeStall, progressUpdates.Count, "Stalled download does not invoke progress callback");
        AssertEq(busCountBeforeStall, busEvents.Count, "Stalled download does not publish BarEvent");

        // 3. File disappears (lengthProvider returns null): removed from active tracking
        var t3 = t2.AddSeconds(1);
        svc.TickProgress(p => null, t3);
        AssertEq(0, svc.ActiveCount, "Disappeared file removed from active downloads");
    }

    private static void TestPrivacyService_StartAndDisposeIdempotency()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        // 1. Repeated sequential Start()
        var svc = new PrivacyService(bus, settings, now => new PrivacyState(false, false, null, null));
        Assert(!svc.IsStarted, "Initially not started");
        Assert(!svc.IsDisposed, "Initially not disposed");
        Assert(!svc.HasTimer, "Initially has no timer");

        svc.Start();
        Assert(svc.IsStarted, "Started after Start()");
        Assert(svc.HasTimer, "Has timer after Start()");

        svc.Start(); // Idempotent repeat
        Assert(svc.IsStarted, "Still started after repeat Start()");
        Assert(svc.HasTimer, "Timer retained after repeat Start()");

        // 2. Repeated sequential Dispose()
        svc.Dispose();
        Assert(svc.IsDisposed, "Disposed after Dispose()");
        Assert(!svc.IsStarted, "Not started after Dispose()");
        Assert(!svc.HasTimer, "Timer nulled after Dispose()");

        svc.Dispose(); // Idempotent repeat
        Assert(svc.IsDisposed, "Still disposed after repeat Dispose()");

        // 3. Start() after Dispose() is rejected
        svc.Start();
        Assert(!svc.IsStarted, "Start after Dispose is rejected (IsStarted is false)");
        Assert(!svc.HasTimer, "Start after Dispose creates no timer");
        Assert(svc.IsDisposed, "IsDisposed remains true");

        // 4. Concurrent Start() calls from multiple threads
        var concurrentSvc = new PrivacyService(bus, settings, now => new PrivacyState(false, false, null, null));
        const int threadCount = 10;
        var startBarrier = new Barrier(threadCount);
        var threads = new List<Thread>();
        var errors = new List<Exception>();

        for (int i = 0; i < threadCount; i++)
        {
            var t = new Thread(() =>
            {
                try
                {
                    startBarrier.SignalAndWait();
                    concurrentSvc.Start();
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add(ex);
                }
            });
            threads.Add(t);
            t.Start();
        }

        foreach (var t in threads) t.Join();

        AssertEq(0, errors.Count, "Zero errors during concurrent Start()");
        Assert(concurrentSvc.IsStarted, "Service started after concurrent Start()");
        Assert(concurrentSvc.HasTimer, "Single timer owned after concurrent Start()");

        concurrentSvc.Dispose();
        Assert(concurrentSvc.IsDisposed, "Concurrent service disposed");
    }

    private static void TestPrivacyService_CurrentThreadSafeSnapshot()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        // Toggle state between CameraOnly ("Teams") and MicOnly ("Discord")
        int toggle = 0;
        var svc = new PrivacyService(bus, settings, now =>
        {
            int v = Interlocked.Increment(ref toggle);
            return (v % 2 == 0)
                ? new PrivacyState(true, false, "Teams", null)
                : new PrivacyState(false, true, null, "Discord");
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var readerErrors = new List<string>();
        const int readerCount = 8;
        var threads = new List<Thread>();

        for (int i = 0; i < readerCount; i++)
        {
            var t = new Thread(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var cur = svc.Current;
                    // Invariant check: snapshot must be coherent
                    if (cur.CameraInUse && (cur.MicInUse || cur.CameraApp != "Teams" || cur.MicApp is not null))
                    {
                        lock (readerErrors) readerErrors.Add($"Torn camera state: {cur}");
                        break;
                    }
                    if (cur.MicInUse && (cur.CameraInUse || cur.MicApp != "Discord" || cur.CameraApp is not null))
                    {
                        lock (readerErrors) readerErrors.Add($"Torn mic state: {cur}");
                        break;
                    }
                    if (!cur.CameraInUse && !cur.MicInUse && (cur.CameraApp is not null || cur.MicApp is not null))
                    {
                        lock (readerErrors) readerErrors.Add($"Torn idle state: {cur}");
                        break;
                    }
                }
            });
            threads.Add(t);
            t.Start();
        }

        // Run recompute loop on main test thread for 200 iterations
        for (int i = 0; i < 200; i++)
        {
            svc.RecomputeCore();
        }

        cts.Cancel();
        foreach (var t in threads) t.Join();

        AssertEq(0, readerErrors.Count, "Zero torn snapshots observed across concurrent readers");
        svc.Dispose();
    }

    private static void TestPrivacyService_InFlightScanVsDisposeRace()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var scanStarted = new ManualResetEventSlim(false);
        var allowScanToFinish = new ManualResetEventSlim(false);
        int privacyChangedFired = 0;
        var busEvents = new List<BarEvent>();

        bus.EventRaised += evt =>
        {
            if (evt.Kind == BarEventKind.Privacy) busEvents.Add(evt);
        };

        var svc = new PrivacyService(bus, settings, now =>
        {
            scanStarted.Set();
            allowScanToFinish.Wait(TimeSpan.FromSeconds(5));
            return new PrivacyState(true, true, "CameraInFlight", "MicInFlight");
        });

        svc.PrivacyChanged += _ => Interlocked.Increment(ref privacyChangedFired);

        // Start in-flight scan on background thread
        var scanTask = Task.Run(() => svc.RecomputeCore());

        // Wait until scan has started and is actively inside the scanner seam
        bool started = scanStarted.Wait(TimeSpan.FromSeconds(3));
        Assert(started, "Scan started signal received");

        // Dispose service while scan is paused in-flight
        svc.Dispose();
        Assert(svc.IsDisposed, "Service is disposed immediately without deadlock");

        // Unblock scan
        allowScanToFinish.Set();
        scanTask.Wait(TimeSpan.FromSeconds(3));

        // State mutation and event dispatch from in-flight scan must be completely suppressed
        AssertEq(new PrivacyState(false, false, null, null), svc.Current, "Current state was not mutated by in-flight scan");
        AssertEq(0, privacyChangedFired, "PrivacyChanged was not fired for discarded in-flight scan");
        AssertEq(0, busEvents.Count, "BarEvent was not published for discarded in-flight scan");
    }

    private static void TestPrivacyService_PostDisposalSuppression()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        var busEvents = new List<BarEvent>();
        bus.EventRaised += evt =>
        {
            if (evt.Kind == BarEventKind.Privacy) busEvents.Add(evt);
        };

        var curState = new PrivacyState(true, false, "CameraApp", null);
        var svc = new PrivacyService(bus, settings, now => curState);

        int callbacks = 0;
        svc.PrivacyChanged += _ => callbacks++;

        // 1. Initial pre-disposal recompute
        svc.RecomputeCore();
        AssertEq(1, callbacks, "PrivacyChanged fired once pre-disposal");
        AssertEq(1, busEvents.Count, "1 BarEvent published pre-disposal");
        AssertEq("Camera", busEvents[0].Title, "Pre-disposal event title is Camera");

        // 2. Dispose
        svc.Dispose();
        Assert(svc.IsDisposed, "Service is disposed");

        // 3. Attempt post-disposal recomputes
        curState = new PrivacyState(false, true, null, "MicApp");
        svc.RecomputeCore();
        svc.RecomputeCore();

        AssertEq(1, callbacks, "Zero additional PrivacyChanged callbacks fired after disposal");
        AssertEq(1, busEvents.Count, "Zero additional BarEvents published after disposal");
        AssertEq(new PrivacyState(true, false, "CameraApp", null), svc.Current, "Current was not mutated after disposal");
    }

    private static void TestPrivacyService_ParsingAndDecoding()
    {
        // 1. PackageFriendlyName
        AssertEq("WhatsAppDesktop", PrivacyService.PackageFriendlyName("5319275A.WhatsAppDesktop_cv1g1gvanyjgm"), "PackageFriendlyName WhatsApp");
        AssertEq("WindowsCamera", PrivacyService.PackageFriendlyName("Microsoft.WindowsCamera_8wekyb3d8bbwe"), "PackageFriendlyName Camera");
        AssertEq("MSTeams", PrivacyService.PackageFriendlyName("MSTeams_8wekyb3d8bbwe"), "PackageFriendlyName Teams");
        AssertEq("App", PrivacyService.PackageFriendlyName(""), "PackageFriendlyName empty fallback");

        // 2. DecodePath
        AssertEq(@"C:\Program Files\App\app.exe", PrivacyService.DecodePath(@"#C:\Program Files\App\app.exe:-1001"), "DecodePath leading # and trailing id");
        AssertEq(Path.GetFullPath(@"C:\Users\User\AppData\app.exe"), PrivacyService.DecodePath(@"C:#Users#User#AppData#app.exe"), "DecodePath # replaced by separator");

        // 3. FriendlyName
        AssertEq("zoom", PrivacyService.FriendlyName(@"#C:\Program Files\Zoom\bin\zoom.exe:-1001"), "FriendlyName zoom");
        AssertEq("notepad", PrivacyService.FriendlyName(@"#C:\Windows\System32\notepad.exe"), "FriendlyName notepad");

        // 4. IsActiveCore
        long now = 1000000;
        Assert(PrivacyService.IsActiveCore(500000, null, now), "Active when start > 0 and stop is null");
        Assert(PrivacyService.IsActiveCore(500000, 0, now), "Active when start > 0 and stop is 0");
        Assert(PrivacyService.IsActiveCore(500000, 400000, now), "Active when start > stop and start <= now");
        Assert(!PrivacyService.IsActiveCore(500000, 600000, now), "Inactive when start <= stop");
        Assert(!PrivacyService.IsActiveCore(1500000, 400000, now), "Inactive when start > now (future drift)");
        Assert(!PrivacyService.IsActiveCore(0, null, now), "Inactive when start is 0");
        Assert(!PrivacyService.IsActiveCore(null, null, now), "Inactive when start is null");

        // 5. IsOwnProcessCore
        string ownExe = @"C:\App\WindowsDynamicBar.exe";
        Assert(PrivacyService.IsOwnProcessCore(@"#C:\App\WindowsDynamicBar.exe:-1001", ownExe), "Matches own process executable");
        Assert(!PrivacyService.IsOwnProcessCore(@"#C:\Other\Other.exe:-1001", ownExe), "Does not match other executable");
        Assert(!PrivacyService.IsOwnProcessCore(@"#C:\App\WindowsDynamicBar.exe:-1001", null), "Returns false for null own path");
    }

    private static void TestPrivacyService_StateTransitionsAndBarEvents()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeaturePrivacyIndicators = true);

        var busEvents = new List<BarEvent>();
        bus.EventRaised += evt =>
        {
            if (evt.Kind == BarEventKind.Privacy) busEvents.Add(evt);
        };

        PrivacyState state = new(false, false, null, null);
        var svc = new PrivacyService(bus, settings, now => state);

        // 1. Camera active
        state = new PrivacyState(true, false, "CameraApp", null);
        svc.RecomputeCore();
        AssertEq(1, busEvents.Count, "1 event after camera active");
        AssertEq("Camera", busEvents[0].Title, "Title is Camera");
        AssertEq("CameraApp", busEvents[0].Subtitle, "Subtitle is CameraApp");
        AssertEq(90, busEvents[0].Priority, "Priority is 90");
        AssertEq(TimeSpan.FromSeconds(4), busEvents[0].DisplayDuration, "Duration is 4s");
        Assert(busEvents[0].Payload is PrivacyState, "Payload is PrivacyState");

        // Dismiss active card on bus so subsequent events fire deterministically
        bus.Dismiss();

        // 2. Both Camera and Mic active
        state = new PrivacyState(true, true, "CameraApp", "MicApp");
        svc.RecomputeCore();
        AssertEq(2, busEvents.Count, "2 events after both active");
        AssertEq("Camera + Microphone", busEvents[1].Title, "Title is Camera + Microphone");
        AssertEq("CameraApp / MicApp", busEvents[1].Subtitle, "Subtitle is CameraApp / MicApp");
        AssertEq(90, busEvents[1].Priority, "Priority is 90");
        AssertEq(TimeSpan.FromSeconds(4), busEvents[1].DisplayDuration, "Duration is 4s");

        bus.Dismiss();

        // 3. Camera released, Mic stays active
        state = new PrivacyState(false, true, null, "MicApp");
        svc.RecomputeCore();
        AssertEq(3, busEvents.Count, "3 events after mic only active");
        AssertEq("Microphone", busEvents[2].Title, "Title is Microphone");
        AssertEq("MicApp", busEvents[2].Subtitle, "Subtitle is MicApp");
        AssertEq(90, busEvents[2].Priority, "Priority is 90");

        bus.Dismiss();

        // 4. All released
        state = new PrivacyState(false, false, null, null);
        svc.RecomputeCore();
        AssertEq(4, busEvents.Count, "4 events after all released");
        AssertEq("Privacy", busEvents[3].Title, "Title is Privacy");
        AssertEq("Released", busEvents[3].Subtitle, "Subtitle is Released");
        AssertEq(20, busEvents[3].Priority, "Off priority is 20");
        AssertEq(TimeSpan.FromSeconds(2), busEvents[3].DisplayDuration, "Off duration is 2s");

        // 5. Duplicate tick with no state change
        svc.RecomputeCore();
        AssertEq(4, busEvents.Count, "Zero events for duplicate unchanged tick");

        // 6. FeaturePrivacyIndicators disabled
        settings.Update(s => s.FeaturePrivacyIndicators = false);
        state = new PrivacyState(true, false, "CameraApp2", null);
        svc.RecomputeCore();
        AssertEq(4, busEvents.Count, "Zero events when FeaturePrivacyIndicators is false");
        AssertEq("CameraApp2", svc.Current.CameraApp, "Current state was still updated when feature toggle is off");

        settings.Update(s => s.FeaturePrivacyIndicators = true);
        svc.Dispose();
    }

    private sealed class FakeBrightnessWatcher : IBrightnessWatcher
    {
        public event Action<float>? BrightnessArrived;
        public int StartCount;
        public int StopCount;
        public int DisposeCount;
        private int _isStarted;
        public Action? OnStart;

        public bool IsStarted => Volatile.Read(ref _isStarted) == 1;

        public void Start()
        {
            OnStart?.Invoke();
            Interlocked.Increment(ref StartCount);
            Volatile.Write(ref _isStarted, 1);
        }

        public void Stop()
        {
            Interlocked.Increment(ref StopCount);
            Volatile.Write(ref _isStarted, 0);
        }

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            Stop();
        }

        public void Emit(float value) => BrightnessArrived?.Invoke(value);
    }

    private static void TestBrightnessService_StartAndDisposeIdempotency()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        // 1. Repeated sequential Start()
        var fakeWatcher = new FakeBrightnessWatcher();
        var svc = new BrightnessService(bus, settings,
            queryBrightness: () => 0.5f,
            setBrightness: _ => true,
            watcherFactory: () => fakeWatcher);

        Assert(!svc.IsStarted, "Not started initially");
        Assert(!svc.IsDisposed, "Not disposed initially");
        Assert(!svc.HasWatcher, "No watcher initially");
        AssertEq(0.0f, svc.Level, "Level initially 0");

        svc.Start();
        Assert(svc.IsStarted, "Started after Start()");
        Assert(svc.IsAvailable, "Available after Start()");
        AssertEq(0.5f, svc.Level, "Level initialized from query");
        Assert(svc.HasWatcher, "Watcher attached");
        AssertEq(1, fakeWatcher.StartCount, "Watcher started once");

        svc.Start();
        AssertEq(1, fakeWatcher.StartCount, "Sequential Start() is idempotent; watcher not restarted");

        // 2. Concurrent Start() on 10 threads
        var fakeWatcherConcurrent = new FakeBrightnessWatcher();
        int watcherFactoryCalls = 0;
        var svcConcurrent = new BrightnessService(bus, settings,
            queryBrightness: () => 0.7f,
            setBrightness: _ => true,
            watcherFactory: () =>
            {
                Interlocked.Increment(ref watcherFactoryCalls);
                return fakeWatcherConcurrent;
            });

        int threadCount = 10;
        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                svcConcurrent.Start();
            });
        }
        Task.WaitAll(tasks);

        AssertEq(1, watcherFactoryCalls, "Watcher factory called exactly once under concurrent Start()");
        AssertEq(1, fakeWatcherConcurrent.StartCount, "Watcher started exactly once under concurrent Start()");
        Assert(svcConcurrent.IsStarted, "Concurrent service is started");
        Assert(svcConcurrent.HasWatcher, "Concurrent service has watcher");
        AssertEq(0.7f, svcConcurrent.Level, "Level is 0.7");

        // 3. Repeated Dispose()
        svcConcurrent.Dispose();
        Assert(svcConcurrent.IsDisposed, "Disposed after Dispose()");
        Assert(!svcConcurrent.IsStarted, "Not started after Dispose()");
        Assert(!svcConcurrent.HasWatcher, "Watcher detached after Dispose()");
        AssertEq(1, fakeWatcherConcurrent.DisposeCount, "Watcher disposed");

        svcConcurrent.Dispose();
        Assert(svcConcurrent.IsDisposed, "Remains disposed on second Dispose()");
        AssertEq(1, fakeWatcherConcurrent.DisposeCount, "Watcher not double-disposed on second Dispose()");

        // 4. Start() after Dispose()
        svcConcurrent.Start();
        Assert(!svcConcurrent.IsStarted, "Start() after Dispose() is rejected");
        AssertEq(1, watcherFactoryCalls, "Zero new watchers created on Start() after Dispose()");

        svc.Dispose();
    }

    private static void TestBrightnessService_LevelThreadSafeSnapshot()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        var fakeWatcher = new FakeBrightnessWatcher();
        var svc = new BrightnessService(bus, settings,
            queryBrightness: () => 0.0f,
            setBrightness: _ => true,
            watcherFactory: () => fakeWatcher);
        svc.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int readerCount = 8;
        var threads = new List<Thread>();
        int anomalies = 0;

        for (int i = 0; i < readerCount; i++)
        {
            var t = new Thread(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    float lvl = svc.Level;
                    if (float.IsNaN(lvl) || float.IsInfinity(lvl) || lvl < 0.0f || lvl > 1.0f)
                    {
                        Interlocked.Increment(ref anomalies);
                        break;
                    }
                }
            });
            threads.Add(t);
            t.Start();
        }

        var baseTime = DateTime.UtcNow;
        for (int w = 0; w < 100; w++)
        {
            float val = (w % 100);
            svc.OnBrightnessArrivedCore(val, baseTime.AddMilliseconds(w * 50));
        }

        cts.Cancel();
        foreach (var t in threads) t.Join();

        AssertEq(0, anomalies, "Zero torn, NaN, or out-of-range snapshots observed across concurrent readers");
        Assert(svc.Level >= 0.0f && svc.Level <= 1.0f, "Final Level is valid");
        svc.Dispose();
    }

    private static void TestBrightnessService_InFlightCallbackVsDisposeRace()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureBrightness = true);

        using var enteredCallback = new ManualResetEventSlim(false);
        using var releaseCallback = new ManualResetEventSlim(false);

        int callbackCount = 0;
        var fakeWatcher = new FakeBrightnessWatcher();
        var svc = new BrightnessService(bus, settings,
            queryBrightness: () => 0.5f,
            setBrightness: _ => true,
            watcherFactory: () => fakeWatcher);

        svc.BrightnessChanged += level =>
        {
            Interlocked.Increment(ref callbackCount);
            enteredCallback.Set();
            releaseCallback.Wait();
        };

        svc.Start();

        // 1. Fire an event on a background thread that will pause inside BrightnessChanged
        var inFlightTask = Task.Run(() => svc.OnBrightnessArrivedCore(80f));

        // 2. Wait until the in-flight event enters BrightnessChanged
        Assert(enteredCallback.Wait(TimeSpan.FromSeconds(2)), "In-flight callback entered subscriber");

        // 3. Concurrently invoke Dispose() on the main thread
        // Dispose() must return without blocking on releaseCallback
        var sw = System.Diagnostics.Stopwatch.StartNew();
        svc.Dispose();
        sw.Stop();

        Assert(sw.ElapsedMilliseconds < 500, "Dispose() returned immediately without waiting for in-flight subscriber");
        Assert(svc.IsDisposed, "Service is disposed");
        Assert(!svc.IsStarted, "Service is not started");

        // 4. Release the in-flight callback
        releaseCallback.Set();
        Assert(inFlightTask.Wait(TimeSpan.FromSeconds(2)), "In-flight task completed");

        // 5. Subsequent events must be dropped: zero state mutation, zero callbacks
        svc.OnBrightnessArrivedCore(95f);
        AssertEq(1, Volatile.Read(ref callbackCount), "Zero additional callbacks invoked after Dispose()");
        AssertEq(0.8f, svc.Level, "Level was not mutated by post-disposal call");
    }

    private static void TestBrightnessService_PostDisposalSuppression()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureBrightness = true);

        var busEvents = new List<BarEvent>();
        bus.EventRaised += e =>
        {
            if (e.Kind == BarEventKind.Brightness) busEvents.Add(e);
        };

        int callbacks = 0;
        var fakeWatcher = new FakeBrightnessWatcher();
        var svc = new BrightnessService(bus, settings,
            queryBrightness: () => 0.4f,
            setBrightness: _ => true,
            watcherFactory: () => fakeWatcher);

        svc.BrightnessChanged += lvl => callbacks++;
        svc.Start();

        // Initial event before disposal
        fakeWatcher.Emit(75f);
        AssertEq(1, callbacks, "Initial callback fired");
        AssertEq(1, busEvents.Count, "Initial BarEvent published");
        AssertEq(0.75f, svc.Level, "Level updated to 0.75");

        svc.Dispose();

        // 1. Event from watcher after disposal
        fakeWatcher.Emit(90f);
        AssertEq(1, callbacks, "Zero callbacks after Dispose()");
        AssertEq(1, busEvents.Count, "Zero BarEvents after Dispose()");
        AssertEq(0.75f, svc.Level, "Level not mutated after Dispose()");

        // 2. Direct OnBrightnessArrivedCore after disposal
        svc.OnBrightnessArrivedCore(95f);
        AssertEq(1, callbacks, "Zero callbacks after direct OnBrightnessArrivedCore post-dispose");
        AssertEq(1, busEvents.Count, "Zero BarEvents after direct OnBrightnessArrivedCore post-dispose");
        AssertEq(0.75f, svc.Level, "Level not mutated by OnBrightnessArrivedCore");

        // 3. SetLevel after disposal
        bool setRes = svc.SetLevel(0.99f);
        Assert(!setRes, "SetLevel returns false after Dispose()");
        AssertEq(1, callbacks, "Zero callbacks from SetLevel after Dispose()");
        AssertEq(0.75f, svc.Level, "Level not mutated from SetLevel");
    }

    private static void TestBrightnessService_DeduplicationAndThresholds()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        int callbacks = 0;
        var svc = new BrightnessService(bus, settings,
            queryBrightness: () => 0.5f,
            setBrightness: _ => true,
            watcherFactory: () => new FakeBrightnessWatcher());

        svc.BrightnessChanged += lvl => callbacks++;
        svc.Start();

        var t0 = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        // 1. First event succeeds
        svc.OnBrightnessArrivedCore(60f, t0);
        AssertEq(1, callbacks, "First event fired");
        AssertEq(0.6f, svc.Level, "Level 0.6");

        // 2. Event within 40ms debounce window is dropped even with large delta
        svc.OnBrightnessArrivedCore(80f, t0.AddMilliseconds(20));
        AssertEq(1, callbacks, "Suppressed by 40ms debounce window");
        AssertEq(0.6f, svc.Level, "Level unchanged");

        // 3. Event outside 40ms window but with delta < 0.5% is dropped (60.3% vs 60.0% -> delta = 0.3%)
        svc.OnBrightnessArrivedCore(60.3f, t0.AddMilliseconds(50));
        AssertEq(1, callbacks, "Suppressed by < 0.5% delta threshold");
        AssertEq(0.6f, svc.Level, "Level unchanged");

        // 4. Event outside 40ms window with delta >= 0.5% succeeds (62.0% vs 60.0% -> delta = 2.0%)
        svc.OnBrightnessArrivedCore(62.0f, t0.AddMilliseconds(100));
        AssertEq(2, callbacks, "Fired after 100ms with >= 0.5% delta");
        AssertEq(0.62f, svc.Level, "Level updated to 0.62");

        // 5. Negative raw value is dropped
        svc.OnBrightnessArrivedCore(-5f, t0.AddMilliseconds(200));
        AssertEq(2, callbacks, "Negative value dropped");
        svc.Dispose();

        // 6. ExtractBrightnessCore tests
        AssertEq(85f, BrightnessService.ExtractBrightnessCore(85, null), "Extrinsic property extracted");
        AssertEq(42f, BrightnessService.ExtractBrightnessCore(null, 42), "Intrinsic TargetInstance property extracted");
        AssertEq(90f, BrightnessService.ExtractBrightnessCore(90, 30), "Extrinsic takes precedence over intrinsic");
        AssertEq(-1f, BrightnessService.ExtractBrightnessCore(null, null), "Null properties return -1");
        AssertEq(-1f, BrightnessService.ExtractBrightnessCore("bad", null), "Unconvertible property returns -1");
    }

    private static void TestBrightnessService_SetLevelAndBarEvents()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureBrightness = true);

        var busEvents = new List<BarEvent>();
        bus.EventRaised += e =>
        {
            if (e.Kind == BarEventKind.Brightness) busEvents.Add(e);
        };

        float lastSet = -1;
        bool shouldSucceed = true;
        var svc = new BrightnessService(bus, settings,
            queryBrightness: () => 0.5f,
            setBrightness: val => { lastSet = val; return shouldSucceed; },
            watcherFactory: () => new FakeBrightnessWatcher());

        svc.Start();

        // 1. SetLevel within valid range
        bool ok = svc.SetLevel(0.85f);
        Assert(ok, "SetLevel succeeds");
        AssertEq(0.85f, lastSet, "Injected setter received clamped 0.85f");
        AssertEq(0.85f, svc.Level, "Level updated to 0.85f");

        // 2. SetLevel clamps out of range values
        svc.SetLevel(1.5f);
        AssertEq(1.0f, lastSet, "Clamped 1.5 to 1.0f");
        AssertEq(1.0f, svc.Level, "Level is 1.0f");

        svc.SetLevel(-0.2f);
        AssertEq(0.0f, lastSet, "Clamped -0.2 to 0.0f");
        AssertEq(0.0f, svc.Level, "Level is 0.0f");

        // 3. SetLevel failure handling
        shouldSucceed = false;
        bool failRes = svc.SetLevel(0.5f);
        Assert(!failRes, "SetLevel returns false when setter fails");
        AssertEq(0.0f, svc.Level, "Level not mutated when setter fails");
        shouldSucceed = true;

        // 4. BarEvent publishing & metadata
        bus.Dismiss();
        svc.OnBrightnessArrivedCore(75f, DateTime.UtcNow.AddMilliseconds(50));
        AssertEq(1, busEvents.Count, "1 BarEvent published");
        var evt = busEvents[0];
        AssertEq(BarEventKind.Brightness, evt.Kind, "Kind is Brightness");
        AssertEq("Brightness", evt.Title, "Title is Brightness");
        AssertEq("75%", evt.Subtitle, "Subtitle is 75%");
        AssertEq("75%", evt.ValueText, "ValueText is 75%");
        AssertEq(0.75f, Convert.ToSingle(evt.Value), "Value is 0.75f");
        AssertEq(70, evt.Priority, "Priority is 70");
        AssertEq(TimeSpan.FromSeconds(2.5), evt.DisplayDuration, "Duration is 2.5s");
        Assert(evt.Preempt, "Preempt is true");

        // 5. Feature toggle suppression
        bus.Dismiss();
        settings.Update(s => s.FeatureBrightness = false);
        svc.OnBrightnessArrivedCore(90f, DateTime.UtcNow.AddSeconds(1));
        AssertEq(1, busEvents.Count, "Zero new BarEvents when FeatureBrightness is false");
        AssertEq(0.9f, svc.Level, "Level was still updated when FeatureBrightness is false");

        settings.Update(s => s.FeatureBrightness = true);
        svc.Dispose();
    }

    private static void TestBrightnessService_StartVsDisposeRace()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureBrightness = true);

        using var reachedStartGate = new ManualResetEventSlim(false);
        using var releaseStartGate = new ManualResetEventSlim(false);

        var fakeWatcher = new FakeBrightnessWatcher();
        fakeWatcher.OnStart = () =>
        {
            reachedStartGate.Set();
            releaseStartGate.Wait();
        };

        int callbackCount = 0;
        var svc = new BrightnessService(bus, settings,
            queryBrightness: () => 0.5f,
            setBrightness: _ => true,
            watcherFactory: () => fakeWatcher);

        svc.BrightnessChanged += lvl => Interlocked.Increment(ref callbackCount);

        // 1. Run Start() on a background task until it enters watcher.Start()
        var startTask = Task.Run(() => svc.Start());
        Assert(reachedStartGate.Wait(TimeSpan.FromSeconds(2)), "Start reached watcher startup gate");

        // 2. Concurrently call Dispose() on the main thread while Start() is paused in watcher.Start()
        var sw = System.Diagnostics.Stopwatch.StartNew();
        svc.Dispose();
        sw.Stop();

        Assert(sw.ElapsedMilliseconds < 500, "Dispose() returned immediately without deadlock");
        Assert(svc.IsDisposed, "Service is disposed");
        Assert(!svc.IsStarted, "Service is not started");

        // 3. Resume startup by releasing the gate
        releaseStartGate.Set();
        Assert(startTask.Wait(TimeSpan.FromSeconds(2)), "Start task completed cleanly");

        // 4. Assert watcher was stopped and disposed, no active watcher remains, and is not running
        Assert(!svc.HasWatcher, "No active watcher retained in service");
        AssertEq(1, fakeWatcher.StartCount, "Watcher Start() was called once");
        Assert(fakeWatcher.StopCount >= 1, "Watcher was stopped when Start() detected disposal");
        AssertEq(1, fakeWatcher.DisposeCount, "Watcher was disposed when Start() detected disposal");
        Assert(!fakeWatcher.IsStarted, "Watcher is not started");

        // 5. Assert post-disposal callbacks and state mutations are suppressed
        fakeWatcher.Emit(90f);
        svc.OnBrightnessArrivedCore(90f);
        AssertEq(0.5f, svc.Level, "Level not mutated after disposal");
        AssertEq(0, Volatile.Read(ref callbackCount), "Zero callbacks invoked after disposal");
    }

    private sealed class FakeNetworkWatcher : INetworkWatcher
    {
        private Action? _statusChanged;
        public int StartCount;
        public int StopCount;
        public int DisposeCount;
        private int _isStarted;
        private int _subscriberCount;
        public Action? OnStart;

        public bool IsStarted => Volatile.Read(ref _isStarted) == 1;
        public int SubscriberCount => Volatile.Read(ref _subscriberCount);

        public event Action? StatusChanged
        {
            add
            {
                lock (this)
                {
                    _statusChanged += value;
                    Interlocked.Increment(ref _subscriberCount);
                }
            }
            remove
            {
                lock (this)
                {
                    _statusChanged -= value;
                    Interlocked.Decrement(ref _subscriberCount);
                }
            }
        }

        public void Start()
        {
            OnStart?.Invoke();
            Interlocked.Increment(ref StartCount);
            Volatile.Write(ref _isStarted, 1);
        }

        public void Stop()
        {
            Interlocked.Increment(ref StopCount);
            Volatile.Write(ref _isStarted, 0);
        }

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            Stop();
        }

        public void Trigger()
        {
            Action? handler;
            lock (this) { handler = _statusChanged; }
            handler?.Invoke();
        }
    }

    private static void TestNetworkService_StartAndDisposeIdempotency()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var initialInfo = new NetworkInfo("Wi-Fi", "Home", 4, true, true);
        var fakeWatcher = new FakeNetworkWatcher();
        var svc = new NetworkService(bus, settings,
            queryNetwork: () => initialInfo,
            watcherFactory: () => fakeWatcher);

        Assert(!svc.IsStarted, "Not started initially");
        Assert(!svc.IsDisposed, "Not disposed initially");
        Assert(!svc.HasWatcher, "No watcher initially");
        AssertEq("None", svc.Current.Kind, "Kind initially None");
        Assert(!svc.Current.Connected, "Initially not connected");

        svc.Start();
        Assert(svc.IsStarted, "Started after Start()");
        Assert(!svc.IsDisposed, "Not disposed after Start()");
        Assert(svc.HasWatcher, "Has watcher after Start()");
        AssertEq(initialInfo, svc.Current, "Current initialized from query");
        AssertEq(1, fakeWatcher.StartCount, "Watcher started once");

        svc.Start();
        AssertEq(1, fakeWatcher.StartCount, "Sequential Start() is idempotent");

        // Concurrent Start() on 10 threads
        var fakeWatcherConcurrent = new FakeNetworkWatcher();
        int watcherFactoryCalls = 0;
        var svcConcurrent = new NetworkService(bus, settings,
            queryNetwork: () => initialInfo,
            watcherFactory: () =>
            {
                Interlocked.Increment(ref watcherFactoryCalls);
                return fakeWatcherConcurrent;
            });

        int threadCount = 10;
        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                svcConcurrent.Start();
            });
        }
        Task.WaitAll(tasks);

        AssertEq(1, watcherFactoryCalls, "Watcher factory called once under concurrent Start()");
        AssertEq(1, fakeWatcherConcurrent.StartCount, "Watcher started once under concurrent Start()");
        Assert(svcConcurrent.IsStarted, "Concurrent service is started");
        Assert(svcConcurrent.HasWatcher, "Concurrent service has watcher");

        // Repeated Dispose()
        svcConcurrent.Dispose();
        Assert(svcConcurrent.IsDisposed, "Disposed after Dispose()");
        Assert(!svcConcurrent.IsStarted, "Not started after Dispose()");
        Assert(!svcConcurrent.HasWatcher, "Watcher detached after Dispose()");
        AssertEq(1, fakeWatcherConcurrent.DisposeCount, "Watcher disposed once");

        svcConcurrent.Dispose();
        Assert(svcConcurrent.IsDisposed, "Remains disposed on second Dispose()");
        AssertEq(1, fakeWatcherConcurrent.DisposeCount, "Watcher not double-disposed on second Dispose()");

        // Start() after Dispose()
        svcConcurrent.Start();
        Assert(!svcConcurrent.IsStarted, "Start() after Dispose() is rejected");
        AssertEq(1, watcherFactoryCalls, "No new watchers created on Start() after Dispose()");

        svc.Dispose();
    }

    private static void TestNetworkService_SubscriptionOwnership()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var initialInfo = new NetworkInfo("Wi-Fi", "Home", 4, true, true);
        var fakeWatcher = new FakeNetworkWatcher();
        var svc = new NetworkService(bus, settings,
            queryNetwork: () => initialInfo,
            watcherFactory: () => fakeWatcher);

        AssertEq(0, fakeWatcher.SubscriberCount, "Zero subscribers before Start()");

        svc.Start();
        AssertEq(1, fakeWatcher.SubscriberCount, "Exactly one subscriber after Start()");

        int callbackCount = 0;
        svc.NetworkChanged += _ => Interlocked.Increment(ref callbackCount);

        // Triggering watcher invokes callback
        fakeWatcher.Trigger();
        AssertEq(0, callbackCount, "Same state does not trigger redundant callback");

        svc.Dispose();
        AssertEq(0, fakeWatcher.SubscriberCount, "Zero subscribers after Dispose()");

        // Triggering after disposal does nothing
        fakeWatcher.Trigger();
        AssertEq(0, callbackCount, "Trigger after Dispose does not invoke callback");
    }

    private static void TestNetworkService_CurrentThreadSafeSnapshot()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var initialInfo = new NetworkInfo("Wi-Fi", "Home", 4, true, true);
        var fakeWatcher = new FakeNetworkWatcher();
        var svc = new NetworkService(bus, settings,
            queryNetwork: () => initialInfo,
            watcherFactory: () => fakeWatcher);
        svc.Start();

        var states = new[]
        {
            new NetworkInfo("Wi-Fi", "Home", 4, true, true),
            new NetworkInfo("Ethernet", "vEthernet", -1, true, true),
            new NetworkInfo("Cellular", "LTE", 2, true, true),
            new NetworkInfo("Network", null, 0, false, false),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int readerCount = 8;
        var threads = new List<Thread>();
        int anomalies = 0;

        for (int i = 0; i < readerCount; i++)
        {
            var t = new Thread(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var cur = svc.Current;
                    if (cur == null || string.IsNullOrEmpty(cur.Kind))
                    {
                        Interlocked.Increment(ref anomalies);
                        break;
                    }
                }
            });
            threads.Add(t);
            t.Start();
        }

        // Writer loop runs on main dispatcher thread
        for (int i = 0; i < 200; i++)
        {
            var st = states[i % states.Length];
            svc.OnNetworkStatusChangedCore(st);
        }

        cts.Cancel();
        foreach (var t in threads) t.Join();

        AssertEq(0, anomalies, "Zero torn reads or anomalies observed across concurrent readers");
        Assert(svc.Current != null && !string.IsNullOrEmpty(svc.Current.Kind), "Current is valid");
        svc.Dispose();
    }

    private static void TestNetworkService_InFlightCallbackVsDisposeRace()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        using var callbackStartedGate = new ManualResetEventSlim(false);
        using var callbackHoldGate = new ManualResetEventSlim(false);

        var initialInfo = new NetworkInfo("Wi-Fi", "Office", 4, true, true);
        var updatedInfo = new NetworkInfo("Ethernet", "vEthernet", -1, true, true);

        int queryCalls = 0;
        Func<NetworkInfo> queryNetwork = () =>
        {
            int call = Interlocked.Increment(ref queryCalls);
            if (call > 1)
            {
                callbackStartedGate.Set();
                callbackHoldGate.Wait(TimeSpan.FromSeconds(5));
                return updatedInfo;
            }
            return initialInfo;
        };

        var fakeWatcher = new FakeNetworkWatcher();
        var svc = new NetworkService(bus, settings,
            queryNetwork: queryNetwork,
            watcherFactory: () => fakeWatcher);

        int callbackCount = 0;
        svc.NetworkChanged += _ => Interlocked.Increment(ref callbackCount);

        svc.Start();
        AssertEq(1, callbackCount, "Initial callback fired on Start");
        AssertEq(initialInfo, svc.Current, "Current is initialInfo");

        var busEvents = new List<BarEvent>();
        bus.EventRaised += e =>
        {
            if (e.Kind == BarEventKind.Network) busEvents.Add(e);
        };

        // Trigger callback on background thread so it pauses at callbackStartedGate
        var triggerTask = Task.Run(() => fakeWatcher.Trigger());
        Assert(callbackStartedGate.Wait(TimeSpan.FromSeconds(2)), "Callback reached gate");

        // Concurrently Dispose() on main thread
        var sw = System.Diagnostics.Stopwatch.StartNew();
        svc.Dispose();
        sw.Stop();

        Assert(sw.ElapsedMilliseconds < 500, "Dispose() returned without deadlock");
        Assert(svc.IsDisposed, "Service is disposed");

        // Release callback
        callbackHoldGate.Set();
        Assert(triggerTask.Wait(TimeSpan.FromSeconds(2)), "Trigger task completed");

        // Assert post-disposal state mutation was suppressed
        AssertEq(initialInfo, svc.Current, "Current was not mutated by in-flight callback after disposal");
        AssertEq(1, Volatile.Read(ref callbackCount), "No callbacks invoked after disposal");
        AssertEq(0, busEvents.Count, "No BarEvents raised after disposal");
    }

    private static void TestNetworkService_PostDisposalSuppression()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var initialInfo = new NetworkInfo("Wi-Fi", "Home", 4, true, true);
        var fakeWatcher = new FakeNetworkWatcher();
        var svc = new NetworkService(bus, settings,
            queryNetwork: () => initialInfo,
            watcherFactory: () => fakeWatcher);

        int callbackCount = 0;
        svc.NetworkChanged += _ => Interlocked.Increment(ref callbackCount);

        var busEvents = new List<BarEvent>();
        bus.EventRaised += e =>
        {
            if (e.Kind == BarEventKind.Network) busEvents.Add(e);
        };

        svc.Start();
        AssertEq(1, callbackCount, "Initial callback on Start");

        svc.Dispose();

        // Late callbacks after disposal
        svc.OnNetworkStatusChangedCore(new NetworkInfo("Network", null, 0, false, false));
        fakeWatcher.Trigger();

        AssertEq(initialInfo, svc.Current, "Current not mutated after disposal");
        AssertEq(1, Volatile.Read(ref callbackCount), "Zero callbacks invoked after disposal");
        AssertEq(0, busEvents.Count, "Zero BarEvents published after disposal");
    }

    private static void TestNetworkService_SignalJitterVsMeaningfulTransitions()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureNetwork = true);

        var initialInfo = new NetworkInfo("Wi-Fi", "Office", 4, true, true);
        var fakeWatcher = new FakeNetworkWatcher();
        var svc = new NetworkService(bus, settings,
            queryNetwork: () => initialInfo,
            watcherFactory: () => fakeWatcher);

        int callbackCount = 0;
        svc.NetworkChanged += _ => Interlocked.Increment(ref callbackCount);

        var busEvents = new List<BarEvent>();
        bus.EventRaised += e =>
        {
            if (e.Kind == BarEventKind.Network) busEvents.Add(e);
        };

        svc.Start();
        AssertEq(1, callbackCount, "Initial callback fired on Start");
        AssertEq(0, busEvents.Count, "Zero BarEvents on initial Start");

        // 1. Signal jitter only (4 bars -> 3 bars)
        bus.Dismiss();
        busEvents.Clear();
        svc.OnNetworkStatusChangedCore(new NetworkInfo("Wi-Fi", "Office", 3, true, true));
        AssertEq(2, callbackCount, "Callback fired on signal jitter");
        AssertEq(3, svc.Current.SignalBars, "SignalBars updated to 3");
        AssertEq(0, busEvents.Count, "Signal jitter does not raise BarEvent (no pop)");

        // 2. Meaningful transition: Disconnect
        bus.Dismiss();
        busEvents.Clear();
        svc.OnNetworkStatusChangedCore(new NetworkInfo("Network", null, 0, false, false));
        AssertEq(3, callbackCount, "Callback fired on disconnect");
        Assert(!svc.Current.Connected, "Connected is false");
        AssertEq(1, busEvents.Count, "BarEvent raised on disconnect");

        var disconnEvt = busEvents[0];
        AssertEq(BarEventKind.Network, disconnEvt.Kind, "Kind is Network");
        AssertEq("Network", disconnEvt.Title, "Title is Network");
        AssertEq("Disconnected", disconnEvt.Subtitle, "Subtitle is Disconnected");
        AssertEq(85, disconnEvt.Priority, "Priority is 85 on disconnect");
        AssertEq(TimeSpan.FromSeconds(3), disconnEvt.DisplayDuration, "Duration is 3s");

        // 3. Meaningful transition: Reconnect
        bus.Dismiss();
        busEvents.Clear();
        svc.OnNetworkStatusChangedCore(new NetworkInfo("Wi-Fi", "Office", 4, true, true));
        AssertEq(4, callbackCount, "Callback fired on reconnect");
        Assert(svc.Current.Connected, "Connected is true");
        AssertEq(1, busEvents.Count, "BarEvent raised on reconnect");

        var connEvt = busEvents[0];
        AssertEq(BarEventKind.Network, connEvt.Kind, "Kind is Network");
        AssertEq("Wi-Fi", connEvt.Title, "Title is Wi-Fi");
        AssertEq("Office", connEvt.Subtitle, "Subtitle is Office");
        AssertEq(30, connEvt.Priority, "Priority is 30 on reconnect");

        // 4. Feature toggle suppression
        bus.Dismiss();
        busEvents.Clear();
        settings.Update(s => s.FeatureNetwork = false);
        svc.OnNetworkStatusChangedCore(new NetworkInfo("Network", null, 0, false, false));
        AssertEq(5, callbackCount, "Callback fired even when FeatureNetwork is false");
        AssertEq(0, busEvents.Count, "Zero BarEvents when FeatureNetwork is false");

        settings.Update(s => s.FeatureNetwork = true);
        svc.Dispose();
    }

    private static void TestNetworkService_StartVsDisposeRace()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        using var reachedStartGate = new ManualResetEventSlim(false);
        using var releaseStartGate = new ManualResetEventSlim(false);

        var fakeWatcher = new FakeNetworkWatcher();
        fakeWatcher.OnStart = () =>
        {
            reachedStartGate.Set();
            releaseStartGate.Wait(TimeSpan.FromSeconds(5));
        };

        var initialInfo = new NetworkInfo("Wi-Fi", "Home", 4, true, true);
        var svc = new NetworkService(bus, settings,
            queryNetwork: () => initialInfo,
            watcherFactory: () => fakeWatcher);

        int callbackCount = 0;
        svc.NetworkChanged += _ => Interlocked.Increment(ref callbackCount);

        // 1. Run Start() on a background task until it enters watcher.Start()
        var startTask = Task.Run(() => svc.Start());
        Assert(reachedStartGate.Wait(TimeSpan.FromSeconds(2)), "Start reached watcher startup gate");

        // 2. Concurrently call Dispose() on the main thread while Start() is paused in watcher.Start()
        var sw = System.Diagnostics.Stopwatch.StartNew();
        svc.Dispose();
        sw.Stop();

        Assert(sw.ElapsedMilliseconds < 500, "Dispose() returned immediately without deadlock");
        Assert(svc.IsDisposed, "Service is disposed");
        Assert(!svc.IsStarted, "Service is not started");

        // 3. Resume startup by releasing the gate
        releaseStartGate.Set();
        Assert(startTask.Wait(TimeSpan.FromSeconds(2)), "Start task completed cleanly");

        // 4. Assert watcher was stopped and disposed, no active watcher remains, and is not running
        Assert(!svc.HasWatcher, "No active watcher retained in service");
        AssertEq(1, fakeWatcher.StartCount, "Watcher Start() was called once");
        Assert(fakeWatcher.StopCount >= 1, "Watcher was stopped when Start() detected disposal");
        AssertEq(1, fakeWatcher.DisposeCount, "Watcher was disposed when Start() detected disposal");
        Assert(!fakeWatcher.IsStarted, "Watcher is not started");

        // 5. Assert post-disposal callbacks and state mutations are suppressed
        svc.OnNetworkStatusChangedCore(new NetworkInfo("Cellular", "5G", 3, true, true));
        AssertEq(initialInfo, svc.Current, "Current not mutated after disposal");
        AssertEq(1, Volatile.Read(ref callbackCount), "Only initial start callback was invoked, zero post-disposal");
    }

    private sealed class FakeBatteryWatcher : IBatteryWatcher
    {
        private Action? _powerChanged;
        private Action? _sessionUnlocked;
        public int StartCount;
        public int StopCount;
        public int DisposeCount;
        private int _isStarted;
        private int _powerSubscribers;
        private int _sessionSubscribers;
        public Action? OnStart;

        public bool IsStarted => Volatile.Read(ref _isStarted) == 1;
        public int TotalSubscribers => Volatile.Read(ref _powerSubscribers) + Volatile.Read(ref _sessionSubscribers);

        public event Action? PowerStatusChanged
        {
            add
            {
                lock (this)
                {
                    _powerChanged += value;
                    Interlocked.Increment(ref _powerSubscribers);
                }
            }
            remove
            {
                lock (this)
                {
                    _powerChanged -= value;
                    Interlocked.Decrement(ref _powerSubscribers);
                }
            }
        }

        public event Action? SessionUnlocked
        {
            add
            {
                lock (this)
                {
                    _sessionUnlocked += value;
                    Interlocked.Increment(ref _sessionSubscribers);
                }
            }
            remove
            {
                lock (this)
                {
                    _sessionUnlocked -= value;
                    Interlocked.Decrement(ref _sessionSubscribers);
                }
            }
        }

        public void Start()
        {
            OnStart?.Invoke();
            Interlocked.Increment(ref StartCount);
            Volatile.Write(ref _isStarted, 1);
        }

        public void Stop()
        {
            Interlocked.Increment(ref StopCount);
            Volatile.Write(ref _isStarted, 0);
        }

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            Stop();
        }

        public void TriggerPower()
        {
            Action? handler;
            lock (this) { handler = _powerChanged; }
            handler?.Invoke();
        }

        public void TriggerSessionUnlock()
        {
            Action? handler;
            lock (this) { handler = _sessionUnlocked; }
            handler?.Invoke();
        }
    }

    private sealed class FakeBatteryTimer : IDisposable
    {
        public Action Callback { get; }
        public TimeSpan Interval { get; }
        public int DisposeCount;
        private int _isDisposed;

        public bool IsDisposed => Volatile.Read(ref _isDisposed) == 1;

        public FakeBatteryTimer(Action callback, TimeSpan interval)
        {
            Callback = callback;
            Interval = interval;
        }

        public void Tick()
        {
            if (!IsDisposed)
            {
                Callback();
            }
        }

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            Volatile.Write(ref _isDisposed, 1);
        }
    }

    private static void TestBatteryService_StartAndDisposeIdempotency()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var initialInfo = new BatteryInfo(80, false, false, "3h left", true);
        var fakeWatcher = new FakeBatteryWatcher();
        FakeBatteryTimer? createdTimer = null;
        var svc = new BatteryService(bus, settings,
            queryBattery: () => initialInfo,
            watcherFactory: () => fakeWatcher,
            timerFactory: (cb, interval) => (createdTimer = new FakeBatteryTimer(cb, interval)));

        Assert(!svc.IsStarted, "Not started initially");
        Assert(!svc.IsDisposed, "Not disposed initially");
        Assert(!svc.HasWatcher, "No watcher initially");
        Assert(!svc.HasTimer, "No timer initially");
        AssertEq(0.0, svc.Current.Percent, "Percent initially 0");
        Assert(!svc.Current.HasBattery, "HasBattery initially false");

        svc.Start();
        Assert(svc.IsStarted, "Started after Start()");
        Assert(!svc.IsDisposed, "Not disposed after Start()");
        Assert(svc.HasWatcher, "Has watcher after Start()");
        Assert(svc.HasTimer, "Has timer after Start()");
        AssertEq(initialInfo, svc.Current, "Current initialized from query");
        AssertEq(1, fakeWatcher.StartCount, "Watcher started once");
        Assert(createdTimer is not null, "Timer was created");

        svc.Start();
        AssertEq(1, fakeWatcher.StartCount, "Sequential Start() is idempotent");

        // Concurrent Start() on 10 threads
        var fakeWatcherConcurrent = new FakeBatteryWatcher();
        int watcherFactoryCalls = 0;
        int timerFactoryCalls = 0;
        FakeBatteryTimer? concurrentTimer = null;
        var svcConcurrent = new BatteryService(bus, settings,
            queryBattery: () => initialInfo,
            watcherFactory: () =>
            {
                Interlocked.Increment(ref watcherFactoryCalls);
                return fakeWatcherConcurrent;
            },
            timerFactory: (cb, interval) =>
            {
                Interlocked.Increment(ref timerFactoryCalls);
                return (concurrentTimer = new FakeBatteryTimer(cb, interval));
            });

        int threadCount = 10;
        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                svcConcurrent.Start();
            });
        }
        Task.WaitAll(tasks);

        AssertEq(1, watcherFactoryCalls, "Watcher factory called once under concurrent Start()");
        AssertEq(1, timerFactoryCalls, "Timer factory called once under concurrent Start()");
        AssertEq(1, fakeWatcherConcurrent.StartCount, "Watcher started once under concurrent Start()");
        Assert(svcConcurrent.IsStarted, "Concurrent service is started");
        Assert(svcConcurrent.HasWatcher, "Concurrent service has watcher");
        Assert(svcConcurrent.HasTimer, "Concurrent service has timer");

        // Repeated Dispose()
        svcConcurrent.Dispose();
        Assert(svcConcurrent.IsDisposed, "Disposed after Dispose()");
        Assert(!svcConcurrent.IsStarted, "Not started after Dispose()");
        Assert(!svcConcurrent.HasWatcher, "Watcher detached after Dispose()");
        Assert(!svcConcurrent.HasTimer, "Timer detached after Dispose()");
        AssertEq(1, fakeWatcherConcurrent.DisposeCount, "Watcher disposed once");
        AssertEq(1, concurrentTimer!.DisposeCount, "Timer disposed once");

        svcConcurrent.Dispose();
        Assert(svcConcurrent.IsDisposed, "Remains disposed on second Dispose()");
        AssertEq(1, fakeWatcherConcurrent.DisposeCount, "Watcher not double-disposed on second Dispose()");
        AssertEq(1, concurrentTimer.DisposeCount, "Timer not double-disposed on second Dispose()");

        // Start() after Dispose()
        svcConcurrent.Start();
        Assert(!svcConcurrent.IsStarted, "Start() after Dispose() is rejected");
        AssertEq(1, watcherFactoryCalls, "No new watchers created on Start() after Dispose()");
        AssertEq(1, timerFactoryCalls, "No new timers created on Start() after Dispose()");

        svc.Dispose();
    }

    private static void TestBatteryService_SubscriptionOwnership()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var initialInfo = new BatteryInfo(80, false, false, "3h left", true);
        var fakeWatcher = new FakeBatteryWatcher();
        var svc = new BatteryService(bus, settings,
            queryBattery: () => initialInfo,
            watcherFactory: () => fakeWatcher,
            timerFactory: (cb, interval) => new FakeBatteryTimer(cb, interval));

        AssertEq(0, fakeWatcher.TotalSubscribers, "Zero subscribers before Start()");

        svc.Start();
        AssertEq(2, fakeWatcher.TotalSubscribers, "Exactly two subscribers after Start() (PowerStatusChanged and SessionUnlocked)");

        int callbackCount = 0;
        svc.BatteryChanged += _ => Interlocked.Increment(ref callbackCount);

        // Triggering watcher with identical state does not fire redundant callback
        fakeWatcher.TriggerPower();
        AssertEq(0, callbackCount, "Same state does not trigger redundant callback");

        svc.Dispose();
        AssertEq(0, fakeWatcher.TotalSubscribers, "Zero subscribers after Dispose()");
        Assert(fakeWatcher.StopCount >= 1, "Watcher stopped on dispose");
        AssertEq(1, fakeWatcher.DisposeCount, "Watcher disposed on dispose");
    }

    private static void TestBatteryService_CurrentThreadSafeSnapshot()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var initialInfo = new BatteryInfo(80, false, false, "3h left", true);
        var fakeWatcher = new FakeBatteryWatcher();
        var svc = new BatteryService(bus, settings,
            queryBattery: () => initialInfo,
            watcherFactory: () => fakeWatcher,
            timerFactory: (cb, interval) => new FakeBatteryTimer(cb, interval));
        svc.Start();

        var states = new[]
        {
            new BatteryInfo(80, false, false, "3h left", true),
            new BatteryInfo(80, true, false, null, true),
            new BatteryInfo(20, false, false, "30m left", true),
            new BatteryInfo(0, false, false, null, false),
            new BatteryInfo(50, false, true, "2h left", true),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int readerCount = 8;
        var threads = new List<Thread>();
        int anomalies = 0;

        for (int i = 0; i < readerCount; i++)
        {
            var t = new Thread(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var cur = svc.Current;
                    if (cur == null || cur.Percent < 0 || cur.Percent > 100)
                    {
                        Interlocked.Increment(ref anomalies);
                        break;
                    }
                }
            });
            threads.Add(t);
            t.Start();
        }

        // Writer loop runs on main dispatcher thread
        for (int i = 0; i < 200; i++)
        {
            var st = states[i % states.Length];
            svc.OnPowerStatusChangedCore(st);
        }

        cts.Cancel();
        foreach (var t in threads) t.Join();

        AssertEq(0, anomalies, "Zero torn reads or anomalies observed across concurrent readers");
        Assert(svc.Current != null, "Current is valid");
        svc.Dispose();
    }

    private static void TestBatteryService_InFlightCallbackVsDisposeRace()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        using var callbackStartedGate = new ManualResetEventSlim(false);
        using var callbackHoldGate = new ManualResetEventSlim(false);

        var initialInfo = new BatteryInfo(80, false, false, "3h left", true);
        var updatedInfo = new BatteryInfo(20, false, false, "30m left", true);

        int queryCalls = 0;
        Func<BatteryInfo> queryBattery = () =>
        {
            int call = Interlocked.Increment(ref queryCalls);
            if (call > 1)
            {
                callbackStartedGate.Set();
                callbackHoldGate.Wait(TimeSpan.FromSeconds(5));
                return updatedInfo;
            }
            return initialInfo;
        };

        var fakeWatcher = new FakeBatteryWatcher();
        var svc = new BatteryService(bus, settings,
            queryBattery: queryBattery,
            watcherFactory: () => fakeWatcher,
            timerFactory: (cb, interval) => new FakeBatteryTimer(cb, interval));

        int callbackCount = 0;
        svc.BatteryChanged += _ => Interlocked.Increment(ref callbackCount);

        svc.Start();
        AssertEq(1, callbackCount, "Initial callback fired on Start");
        AssertEq(initialInfo, svc.Current, "Current is initialInfo");

        var busEvents = new List<BarEvent>();
        bus.EventRaised += e =>
        {
            if (e.Kind == BarEventKind.Battery) busEvents.Add(e);
        };

        // Trigger callback on background thread so it pauses at callbackStartedGate
        var triggerTask = Task.Run(() => fakeWatcher.TriggerPower());
        Assert(callbackStartedGate.Wait(TimeSpan.FromSeconds(2)), "Callback reached gate");

        // Concurrently Dispose() on main thread
        var sw = System.Diagnostics.Stopwatch.StartNew();
        svc.Dispose();
        sw.Stop();

        Assert(sw.ElapsedMilliseconds < 500, "Dispose() returned without deadlock");
        Assert(svc.IsDisposed, "Service is disposed");

        // Release callback
        callbackHoldGate.Set();
        Assert(triggerTask.Wait(TimeSpan.FromSeconds(2)), "Trigger task completed");

        // Assert post-disposal state mutation was suppressed
        AssertEq(initialInfo, svc.Current, "Current was not mutated by in-flight callback after disposal");
        AssertEq(1, Volatile.Read(ref callbackCount), "No callbacks invoked after disposal");
        AssertEq(0, busEvents.Count, "No BarEvents raised after disposal");
    }

    private static void TestBatteryService_PostDisposalSuppression()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var initialInfo = new BatteryInfo(80, false, false, "3h left", true);
        var fakeWatcher = new FakeBatteryWatcher();
        FakeBatteryTimer? timer = null;
        var svc = new BatteryService(bus, settings,
            queryBattery: () => initialInfo,
            watcherFactory: () => fakeWatcher,
            timerFactory: (cb, interval) => (timer = new FakeBatteryTimer(cb, interval)));

        int callbackCount = 0;
        svc.BatteryChanged += _ => Interlocked.Increment(ref callbackCount);

        var busEvents = new List<BarEvent>();
        bus.EventRaised += e =>
        {
            if (e.Kind == BarEventKind.Battery) busEvents.Add(e);
        };

        svc.Start();
        AssertEq(1, callbackCount, "Initial callback on Start");

        svc.Dispose();

        // Late callbacks after disposal
        svc.OnPollTickCore(new BatteryInfo(20, false, false, "30m left", true));
        svc.OnPowerStatusChangedCore(new BatteryInfo(20, false, false, "30m left", true));
        svc.OnSessionUnlockedCore(new BatteryInfo(20, false, false, "30m left", true));
        fakeWatcher.TriggerPower();
        fakeWatcher.TriggerSessionUnlock();
        timer?.Tick();

        AssertEq(initialInfo, svc.Current, "Current not mutated after disposal");
        AssertEq(1, Volatile.Read(ref callbackCount), "Zero callbacks invoked after disposal");
        AssertEq(0, busEvents.Count, "Zero BarEvents published after disposal");
    }

    private static void TestBatteryService_AlertTransitionsAndFeatureToggle()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureBattery = true);

        var initialInfo = new BatteryInfo(80, false, false, "3h left", true);
        var fakeWatcher = new FakeBatteryWatcher();
        var svc = new BatteryService(bus, settings,
            queryBattery: () => initialInfo,
            watcherFactory: () => fakeWatcher,
            timerFactory: (cb, interval) => new FakeBatteryTimer(cb, interval));

        int callbackCount = 0;
        svc.BatteryChanged += _ => Interlocked.Increment(ref callbackCount);

        var busEvents = new List<BarEvent>();
        bus.EventRaised += e =>
        {
            if (e.Kind == BarEventKind.Battery) busEvents.Add(e);
        };

        svc.Start();
        AssertEq(1, callbackCount, "Initial callback on start");
        AssertEq(0, busEvents.Count, "No initial BarEvent on start");

        // 1. Routine poll drift (80% -> 79%): updates Current & fires callback, but NO BarEvent
        svc.OnPollTickCore(new BatteryInfo(79, false, false, "2h 58m left", true));
        AssertEq(2, callbackCount, "Callback fires on >=1% poll drift");
        AssertEq(0, busEvents.Count, "Zero BarEvents for routine drift");
        AssertEq(79.0, svc.Current.Percent, "Current updated to 79%");

        // 2. Sub-1% poll drift (79% -> 78.6%): completely suppressed
        svc.OnPollTickCore(new BatteryInfo(78.6, false, false, "2h 55m left", true));
        AssertEq(2, callbackCount, "Callback suppressed on <1% poll drift");
        AssertEq(0, busEvents.Count, "Zero BarEvents for sub-1% drift");
        AssertEq(79.0, svc.Current.Percent, "Current remains 79%");

        // 3. Charger connected (79% discharging -> 79% charging): raises alert
        svc.OnPowerStatusChangedCore(new BatteryInfo(79, true, false, null, true));
        AssertEq(3, callbackCount, "Callback fired on charger connect");
        AssertEq(1, busEvents.Count, "One BarEvent raised for charger connect");
        AssertEq("Charging", busEvents[0].Title, "Title is Charging");
        AssertEq(40, busEvents[0].Priority, "Charging alert priority is 40");
        AssertEq(TimeSpan.FromSeconds(3), busEvents[0].DisplayDuration, "Charging duration is 3s");
        Assert(svc.Current.Charging, "Current is charging");

        // 4. Charger disconnected (79% charging -> 79% discharging): raises alert
        busEvents.Clear();
        svc.OnPowerStatusChangedCore(new BatteryInfo(79, false, false, "2h 50m left", true));
        AssertEq(4, callbackCount, "Callback fired on charger disconnect");
        AssertEq(1, busEvents.Count, "One BarEvent raised for charger disconnect");
        AssertEq("Battery", busEvents[0].Title, "Title is Battery on unplug");
        AssertEq(40, busEvents[0].Priority, "Unplug priority is 40");
        AssertEq(TimeSpan.FromSeconds(3), busEvents[0].DisplayDuration, "Unplug duration is 3s");

        // 5. Battery saver toggled on: raises alert
        busEvents.Clear();
        svc.OnPowerStatusChangedCore(new BatteryInfo(79, false, true, "3h 10m left", true));
        AssertEq(5, callbackCount, "Callback fired on battery saver on");
        AssertEq(1, busEvents.Count, "One BarEvent raised for battery saver");
        AssertEq("Battery saver", busEvents[0].Title, "Title is Battery saver");
        AssertEq(40, busEvents[0].Priority, "Battery saver priority is 40");
        AssertEq(TimeSpan.FromSeconds(3), busEvents[0].DisplayDuration, "Battery saver duration is 3s");

        // 6. Low-battery crossing (21% -> 20% discharging): raises alert with priority 95, duration 6s
        busEvents.Clear();
        svc.OnPowerStatusChangedCore(new BatteryInfo(21, false, false, "35m left", true));
        busEvents.Clear(); // clear the transition to 21%
        svc.OnPowerStatusChangedCore(new BatteryInfo(20, false, false, "30m left", true));
        AssertEq(1, busEvents.Count, "One BarEvent raised for low battery crossing");
        AssertEq("Battery", busEvents[0].Title, "Title is Battery on low alert");
        AssertEq(95, busEvents[0].Priority, "Low battery priority is 95");
        AssertEq(TimeSpan.FromSeconds(6), busEvents[0].DisplayDuration, "Low battery duration is 6s");

        // 7. Duplicate low-battery alert (20% -> 19% discharging): suppressed!
        busEvents.Clear();
        svc.OnPowerStatusChangedCore(new BatteryInfo(19, false, false, "28m left", true));
        AssertEq(0, busEvents.Count, "Zero BarEvents for duplicate low battery alert");
        AssertEq(19.0, svc.Current.Percent, "Current updated to 19%");

        // 8. Session unlocked: updates Current & fires callback, but no BarEvent
        busEvents.Clear();
        svc.OnSessionUnlockedCore(new BatteryInfo(18, false, false, "25m left", true));
        AssertEq(0, busEvents.Count, "Zero BarEvents on session unlock");
        AssertEq(18.0, svc.Current.Percent, "Current updated on session unlock");

        // 9. FeatureBattery toggle suppression
        bus.Dismiss();
        busEvents.Clear();
        int cbBefore = callbackCount;
        settings.Update(s => s.FeatureBattery = false);
        svc.OnPowerStatusChangedCore(new BatteryInfo(18, true, false, null, true));
        AssertEq(cbBefore + 1, callbackCount, "Callback fired even when FeatureBattery is false");
        AssertEq(0, busEvents.Count, "Zero BarEvents when FeatureBattery is false");

        settings.Update(s => s.FeatureBattery = true);
        svc.Dispose();
    }

    private static void TestBatteryService_StartVsDisposeRace()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        using var reachedStartGate = new ManualResetEventSlim(false);
        using var releaseStartGate = new ManualResetEventSlim(false);

        var fakeWatcher = new FakeBatteryWatcher();
        fakeWatcher.OnStart = () =>
        {
            reachedStartGate.Set();
            releaseStartGate.Wait(TimeSpan.FromSeconds(5));
        };

        var initialInfo = new BatteryInfo(80, false, false, "3h left", true);
        var svc = new BatteryService(bus, settings,
            queryBattery: () => initialInfo,
            watcherFactory: () => fakeWatcher,
            timerFactory: (cb, interval) => new FakeBatteryTimer(cb, interval));

        int callbackCount = 0;
        svc.BatteryChanged += _ => Interlocked.Increment(ref callbackCount);

        // 1. Run Start() on a background task until it enters watcher.Start()
        var startTask = Task.Run(() => svc.Start());
        Assert(reachedStartGate.Wait(TimeSpan.FromSeconds(2)), "Start reached watcher startup gate");

        // 2. Concurrently call Dispose() on the main thread while Start() is paused in watcher.Start()
        var sw = System.Diagnostics.Stopwatch.StartNew();
        svc.Dispose();
        sw.Stop();

        Assert(sw.ElapsedMilliseconds < 500, "Dispose() returned immediately without deadlock");
        Assert(svc.IsDisposed, "Service is disposed");
        Assert(!svc.IsStarted, "Service is not started");

        // 3. Resume startup by releasing the gate
        releaseStartGate.Set();
        Assert(startTask.Wait(TimeSpan.FromSeconds(2)), "Start task completed cleanly");

        // 4. Assert watcher was stopped and disposed, no active watcher or timer remains
        Assert(!svc.HasWatcher, "No active watcher retained in service");
        Assert(!svc.HasTimer, "No active timer retained in service");
        AssertEq(1, fakeWatcher.StartCount, "Watcher Start() was called once");
        Assert(fakeWatcher.StopCount >= 1, "Watcher was stopped when Start() detected disposal");
        AssertEq(1, fakeWatcher.DisposeCount, "Watcher was disposed when Start() detected disposal");
        Assert(!fakeWatcher.IsStarted, "Watcher is not started");

        // 5. Assert post-disposal callbacks and state mutations are suppressed
        svc.OnPowerStatusChangedCore(new BatteryInfo(50, true, false, null, true));
        AssertEq(initialInfo, svc.Current, "Current not mutated after disposal");
        AssertEq(1, Volatile.Read(ref callbackCount), "Only initial start callback was invoked, zero post-disposal");
    }

    private static void TestBatteryService_StartVsDisposeDuringTimerCreation()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        using var reachedTimerGate = new ManualResetEventSlim(false);
        using var releaseTimerGate = new ManualResetEventSlim(false);

        var fakeWatcher = new FakeBatteryWatcher();
        FakeBatteryTimer? createdTimer = null;

        var initialInfo = new BatteryInfo(80, false, false, "3h left", true);
        var svc = new BatteryService(bus, settings,
            queryBattery: () => initialInfo,
            watcherFactory: () => fakeWatcher,
            timerFactory: (cb, interval) =>
            {
                reachedTimerGate.Set();
                releaseTimerGate.Wait(TimeSpan.FromSeconds(5));
                return (createdTimer = new FakeBatteryTimer(cb, interval));
            });

        // 1. Run Start() on background task until it reaches timerFactory
        var startTask = Task.Run(() => svc.Start());
        Assert(reachedTimerGate.Wait(TimeSpan.FromSeconds(2)), "Start reached timer factory gate");

        // 2. Concurrently call Dispose()
        svc.Dispose();
        Assert(svc.IsDisposed, "Service is disposed");

        // 3. Release timer creation
        releaseTimerGate.Set();
        Assert(startTask.Wait(TimeSpan.FromSeconds(2)), "Start task completed cleanly");

        // 4. Verify neither watcher nor timer remained published in service, and both cleaned up
        Assert(!svc.HasWatcher, "No watcher retained");
        Assert(!svc.HasTimer, "No timer retained");
        Assert(!svc.IsStarted, "Service is not started");
        AssertEq(1, fakeWatcher.DisposeCount, "Watcher disposed");
        Assert(createdTimer is not null, "Timer was created");
        AssertEq(1, createdTimer!.DisposeCount, "Timer disposed immediately by deferred cleanup");
    }

    private static void TestBatteryService_StartupFailureAndRetry()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var initialInfo = new BatteryInfo(80, false, false, "3h left", true);
        bool shouldThrow = true;
        var fakeWatcher1 = new FakeBatteryWatcher();
        fakeWatcher1.OnStart = () =>
        {
            if (shouldThrow) throw new InvalidOperationException("Simulated hardware failure");
        };

        var fakeWatcher2 = new FakeBatteryWatcher();
        int watcherCount = 0;

        var svc = new BatteryService(bus, settings,
            queryBattery: () => initialInfo,
            watcherFactory: () =>
            {
                int count = Interlocked.Increment(ref watcherCount);
                return count == 1 ? fakeWatcher1 : fakeWatcher2;
            },
            timerFactory: (cb, interval) => new FakeBatteryTimer(cb, interval));

        // 1. Initial Start() fails
        svc.Start();
        Assert(!svc.IsStarted, "Service not started after failure");
        Assert(!svc.HasWatcher, "No watcher published on failure");
        Assert(!svc.HasTimer, "No timer published on failure");
        Assert(!svc.IsDisposed, "Service not disposed on recoverable failure");
        AssertEq(1, fakeWatcher1.DisposeCount, "Failed watcher was cleaned up and disposed");

        // 2. Retry Start() succeeds
        shouldThrow = false;
        svc.Start();
        Assert(svc.IsStarted, "Service started successfully on retry");
        Assert(svc.HasWatcher, "Watcher published on successful retry");
        Assert(svc.HasTimer, "Timer published on successful retry");
        AssertEq(1, fakeWatcher2.StartCount, "Second watcher started once");
        AssertEq(2, fakeWatcher2.TotalSubscribers, "Second watcher has 2 event subscribers");

        svc.Dispose();
        Assert(svc.IsDisposed, "Service disposed cleanly");
        AssertEq(1, fakeWatcher2.DisposeCount, "Second watcher disposed on service dispose");
    }

    private static void TestSystemEventsBatteryWatcher_PartialSubscriptionRollbackAndRetry()
    {
        var powerSubscriptions = new List<Microsoft.Win32.PowerModeChangedEventHandler>();
        var powerUnsubscriptions = new List<Microsoft.Win32.PowerModeChangedEventHandler>();
        var sessionSubscriptions = new List<Microsoft.Win32.SessionSwitchEventHandler>();
        var sessionUnsubscriptions = new List<Microsoft.Win32.SessionSwitchEventHandler>();

        bool throwOnSessionSubscribe = true;

        var watcher = new SystemEventsBatteryWatcher(
            addPower: h => powerSubscriptions.Add(h),
            removePower: h => powerUnsubscriptions.Add(h),
            addSession: h =>
            {
                if (throwOnSessionSubscribe)
                {
                    throw new InvalidOperationException("Simulated SessionSwitch registration failure");
                }
                sessionSubscriptions.Add(h);
            },
            removeSession: h => sessionUnsubscriptions.Add(h));

        Assert(!watcher.IsHooked, "Watcher is initially not hooked");

        // 1. Initial Start() fails on the second subscription (SessionSwitch).
        // Rollback must immediately remove the first subscription (PowerModeChanged).
        watcher.Start();

        Assert(!watcher.IsHooked, "Watcher is not hooked after partial registration failure");
        AssertEq(1, powerSubscriptions.Count, "PowerModeChanged was subscribed once");
        AssertEq(1, powerUnsubscriptions.Count, "PowerModeChanged was rolled back immediately");
        Assert(ReferenceEquals(powerSubscriptions[0], powerUnsubscriptions[0]), "The exact registered PowerModeChanged handler was unhooked");
        AssertEq(0, sessionSubscriptions.Count, "SessionSwitch was never successfully registered");
        AssertEq(0, sessionUnsubscriptions.Count, "SessionSwitch unsubscription was not invoked");

        // Calling Stop() or Dispose() while in failed state is a safe no-op
        watcher.Stop();
        AssertEq(1, powerUnsubscriptions.Count, "Stop() after failure does not duplicate unsubscriptions");

        // 2. Retry Start() after resolving the issue.
        throwOnSessionSubscribe = false;
        watcher.Start();

        Assert(watcher.IsHooked, "Watcher is hooked after successful retry");
        AssertEq(2, powerSubscriptions.Count, "PowerModeChanged was subscribed again on retry");
        AssertEq(1, powerUnsubscriptions.Count, "PowerModeChanged unsubscriptions unchanged on successful start");
        AssertEq(1, sessionSubscriptions.Count, "SessionSwitch was subscribed on retry");
        AssertEq(0, sessionUnsubscriptions.Count, "SessionSwitch unsubscriptions unchanged");

        // Repeated Start() while hooked is idempotent
        watcher.Start();
        AssertEq(2, powerSubscriptions.Count, "Repeated Start() does not duplicate PowerModeChanged subscriptions");
        AssertEq(1, sessionSubscriptions.Count, "Repeated Start() does not duplicate SessionSwitch subscriptions");

        // 3. Stop() detaches both active handlers cleanly
        watcher.Stop();

        Assert(!watcher.IsHooked, "Watcher is not hooked after Stop()");
        AssertEq(2, powerUnsubscriptions.Count, "PowerModeChanged was unsubscribed on Stop()");
        Assert(ReferenceEquals(powerSubscriptions[1], powerUnsubscriptions[1]), "The active PowerModeChanged handler was unhooked");
        AssertEq(1, sessionUnsubscriptions.Count, "SessionSwitch was unsubscribed on Stop()");
        Assert(ReferenceEquals(sessionSubscriptions[0], sessionUnsubscriptions[0]), "The active SessionSwitch handler was unhooked");

        // 4. Repeated Stop() and Dispose() are idempotent
        watcher.Stop();
        watcher.Dispose();
        AssertEq(2, powerUnsubscriptions.Count, "Subsequent Stop()/Dispose() do not call removePower again");
        AssertEq(1, sessionUnsubscriptions.Count, "Subsequent Stop()/Dispose() do not call removeSession again");

        // 5. Verify exception resilience during unsubscription
        bool powerRemovalThrew = false;
        var throwingWatcher = new SystemEventsBatteryWatcher(
            addPower: h => { },
            removePower: h => { powerRemovalThrew = true; throw new InvalidOperationException("Simulated unhook error"); },
            addSession: h => { },
            removeSession: h => { });

        throwingWatcher.Start();
        Assert(throwingWatcher.IsHooked, "Throwing watcher is hooked");
        throwingWatcher.Stop();
        Assert(powerRemovalThrew, "removePower threw as expected");
        Assert(!throwingWatcher.IsHooked, "Throwing watcher is unhooked even when removePower threw");
    }

    private sealed class FakeScreenshotWatcher : IScreenshotWatcher
    {
        private Action<string, string>? _created;
        public int StartCount;
        public int StopCount;
        public int DisposeCount;
        private int _isStarted;
        private int _subscriberCount;
        public Action? OnStart;
        public Action? OnStop;
        public Action? OnDispose;

        public bool IsStarted => Volatile.Read(ref _isStarted) == 1;
        public int SubscriberCount => Volatile.Read(ref _subscriberCount);

        public event Action<string, string>? Created
        {
            add
            {
                lock (this)
                {
                    _created += value;
                    Interlocked.Increment(ref _subscriberCount);
                }
            }
            remove
            {
                lock (this)
                {
                    _created -= value;
                    Interlocked.Decrement(ref _subscriberCount);
                }
            }
        }

        public void Start()
        {
            OnStart?.Invoke();
            Interlocked.Increment(ref StartCount);
            Volatile.Write(ref _isStarted, 1);
        }

        public void Stop()
        {
            OnStop?.Invoke();
            Interlocked.Increment(ref StopCount);
            Volatile.Write(ref _isStarted, 0);
        }

        public void Dispose()
        {
            OnDispose?.Invoke();
            Interlocked.Increment(ref DisposeCount);
            Stop();
        }

        public void Trigger(string name, string fullPath)
        {
            Action<string, string>? handler;
            lock (this) { handler = _created; }
            handler?.Invoke(name, fullPath);
        }
    }

    private static void TestScreenshotService_StartIdempotency()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var fakeWatcher = new FakeScreenshotWatcher();
        int factoryCalls = 0;
        var svc = new ScreenshotService(bus, settings,
            watcherFactory: dir =>
            {
                Interlocked.Increment(ref factoryCalls);
                return fakeWatcher;
            },
            directoryResolver: () => @"C:\Users\Fake\Pictures\Screenshots");

        Assert(!svc.IsStarted, "Not started initially");
        Assert(!svc.IsDisposed, "Not disposed initially");
        Assert(!svc.HasWatcher, "No watcher initially");

        // Sequential Start()
        svc.Start();
        Assert(svc.IsStarted, "Started after Start()");
        Assert(!svc.IsDisposed, "Not disposed after Start()");
        Assert(svc.HasWatcher, "Has watcher after Start()");
        AssertEq(1, fakeWatcher.StartCount, "Watcher started once");
        AssertEq(1, factoryCalls, "Factory called once");

        svc.Start();
        AssertEq(1, fakeWatcher.StartCount, "Sequential Start() is idempotent");
        AssertEq(1, factoryCalls, "Factory not called again on sequential Start()");

        svc.Dispose();

        // Concurrent Start() on 10 threads
        var fakeWatcherConcurrent = new FakeScreenshotWatcher();
        int concurrentFactoryCalls = 0;
        var svcConcurrent = new ScreenshotService(bus, settings,
            watcherFactory: dir =>
            {
                Interlocked.Increment(ref concurrentFactoryCalls);
                return fakeWatcherConcurrent;
            },
            directoryResolver: () => @"C:\Users\Fake\Pictures\Screenshots");

        int threadCount = 10;
        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                svcConcurrent.Start();
            });
        }
        Task.WaitAll(tasks);

        AssertEq(1, concurrentFactoryCalls, "Watcher factory called once under concurrent Start()");
        AssertEq(1, fakeWatcherConcurrent.StartCount, "Watcher started once under concurrent Start()");
        Assert(svcConcurrent.IsStarted, "Concurrent service is started");
        Assert(svcConcurrent.HasWatcher, "Concurrent service has watcher");

        svcConcurrent.Dispose();
    }

    private static void TestScreenshotService_DisposeIdempotencyAndRejection()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var fakeWatcher = new FakeScreenshotWatcher();
        int factoryCalls = 0;
        var svc = new ScreenshotService(bus, settings,
            watcherFactory: dir =>
            {
                Interlocked.Increment(ref factoryCalls);
                return fakeWatcher;
            },
            directoryResolver: () => @"C:\Users\Fake\Pictures\Screenshots");

        svc.Start();
        Assert(svc.IsStarted, "Service is started");
        Assert(svc.HasWatcher, "Service has watcher");

        // First Dispose()
        svc.Dispose();
        Assert(svc.IsDisposed, "Disposed after Dispose()");
        Assert(!svc.IsStarted, "Not started after Dispose()");
        Assert(!svc.HasWatcher, "Watcher detached after Dispose()");
        AssertEq(1, fakeWatcher.DisposeCount, "Watcher disposed once");

        // Repeated Dispose()
        svc.Dispose();
        Assert(svc.IsDisposed, "Remains disposed on second Dispose()");
        AssertEq(1, fakeWatcher.DisposeCount, "Watcher not double-disposed on second Dispose()");

        // Start() after Dispose() must be rejected
        svc.Start();
        Assert(!svc.IsStarted, "Start() after Dispose() is rejected");
        Assert(!svc.HasWatcher, "No watcher attached after rejected Start()");
        AssertEq(1, factoryCalls, "No new watcher created on rejected Start()");
    }

    private static void TestScreenshotService_WatcherOwnershipAndCleanup()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        var fakeWatcher = new FakeScreenshotWatcher();
        var svc = new ScreenshotService(bus, settings,
            watcherFactory: _ => fakeWatcher,
            directoryResolver: () => @"C:\Users\Fake\Pictures\Screenshots");

        AssertEq(0, fakeWatcher.SubscriberCount, "Zero subscribers initially");

        svc.Start();
        AssertEq(1, fakeWatcher.SubscriberCount, "Exactly one subscriber after Start()");
        Assert(fakeWatcher.IsStarted, "Watcher is started");

        svc.Dispose();
        AssertEq(0, fakeWatcher.SubscriberCount, "Subscriber detached on Dispose()");
        Assert(!fakeWatcher.IsStarted, "Watcher stopped on Dispose()");
        Assert(fakeWatcher.StopCount >= 1, "Watcher was stopped on Dispose()");
        AssertEq(1, fakeWatcher.DisposeCount, "Watcher Dispose called once");

        // Exception resilience in CleanupWatcher
        var throwingWatcher = new FakeScreenshotWatcher
        {
            OnStop = () => throw new InvalidOperationException("Simulated stop failure"),
            OnDispose = () => throw new InvalidOperationException("Simulated dispose failure")
        };
        var throwingSvc = new ScreenshotService(bus, settings,
            watcherFactory: _ => throwingWatcher,
            directoryResolver: () => @"C:\Users\Fake\Pictures\Screenshots");

        throwingSvc.Start();
        // Should not throw or crash
        throwingSvc.Dispose();
        Assert(throwingSvc.IsDisposed, "Service disposed cleanly despite watcher exceptions");
        AssertEq(0, throwingWatcher.SubscriberCount, "Subscriber detached despite stop/dispose exceptions");
    }

    private static void TestScreenshotService_InFlightCallbackVsDisposeRace()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureScreenshots = true);

        using var callbackStartedGate = new ManualResetEventSlim(false);
        using var callbackHoldGate = new ManualResetEventSlim(false);

        var fakeWatcher = new FakeScreenshotWatcher();
        var svc = new ScreenshotService(bus, settings,
            watcherFactory: _ => fakeWatcher,
            directoryResolver: () => @"C:\Users\Fake\Pictures\Screenshots",
            eventFactory: (name, path) =>
            {
                callbackStartedGate.Set();
                callbackHoldGate.Wait(TimeSpan.FromSeconds(5));
                return ScreenshotService.CreateBarEvent(name, path);
            });

        var busEvents = new List<BarEvent>();
        bus.EventRaised += e =>
        {
            if (e.Kind == BarEventKind.Screenshot) busEvents.Add(e);
        };

        svc.Start();

        // 1. Trigger screenshot callback on a background thread so it pauses in eventFactory
        var triggerTask = Task.Run(() => fakeWatcher.Trigger("screenshot.png", @"C:\Users\Fake\Pictures\Screenshots\screenshot.png"));
        Assert(callbackStartedGate.Wait(TimeSpan.FromSeconds(2)), "Callback reached gate inside eventFactory");

        // 2. Concurrently call Dispose() on the main thread
        var sw = System.Diagnostics.Stopwatch.StartNew();
        svc.Dispose();
        sw.Stop();

        Assert(sw.ElapsedMilliseconds < 500, "Dispose() returned immediately without deadlock");
        Assert(svc.IsDisposed, "Service is disposed");
        Assert(!svc.IsStarted, "Service is not started");

        // 3. Release in-flight callback
        callbackHoldGate.Set();
        Assert(triggerTask.Wait(TimeSpan.FromSeconds(2)), "Trigger task completed");

        // 4. Assert that the pre-dispatch check suppressed _bus.Raise because service was disposed
        AssertEq(0, busEvents.Count, "No BarEvents raised by in-flight callback racing with Dispose");
    }

    private static void TestScreenshotService_LateCallbacksAfterDisposal()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;
        settings.Update(s => s.FeatureScreenshots = true);

        var fakeWatcher = new FakeScreenshotWatcher();
        var svc = new ScreenshotService(bus, settings,
            watcherFactory: _ => fakeWatcher,
            directoryResolver: () => @"C:\Users\Fake\Pictures\Screenshots");

        var busEvents = new List<BarEvent>();
        bus.EventRaised += e =>
        {
            if (e.Kind == BarEventKind.Screenshot) busEvents.Add(e);
        };

        svc.Start();
        svc.Dispose();

        Assert(svc.IsDisposed, "Service is disposed");

        // Late callbacks via watcher and via direct core call
        fakeWatcher.Trigger("late.png", @"C:\Users\Fake\Pictures\Screenshots\late.png");
        svc.OnScreenshotCreatedCore("late_core.png", @"C:\Users\Fake\Pictures\Screenshots\late_core.png");

        AssertEq(0, busEvents.Count, "Zero BarEvents published after disposal");
    }

    private static void TestScreenshotService_ExtensionFilteringAndFeatureToggle()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        // 1. Pure extension helper verification
        Assert(ScreenshotService.IsScreenshotFile("screen.png"), ".png supported");
        Assert(ScreenshotService.IsScreenshotFile("screen.jpg"), ".jpg supported");
        Assert(ScreenshotService.IsScreenshotFile("screen.jpeg"), ".jpeg supported");
        Assert(ScreenshotService.IsScreenshotFile("screen.bmp"), ".bmp supported");
        Assert(ScreenshotService.IsScreenshotFile("SCREEN.PNG"), "uppercase .PNG supported");
        Assert(ScreenshotService.IsScreenshotFile("capture.JpEg"), "mixed case .JpEg supported");

        Assert(!ScreenshotService.IsScreenshotFile("image.gif"), ".gif not supported");
        Assert(!ScreenshotService.IsScreenshotFile("image.webp"), ".webp not supported");
        Assert(!ScreenshotService.IsScreenshotFile("notes.txt"), ".txt not supported");
        Assert(!ScreenshotService.IsScreenshotFile("app.exe"), ".exe not supported");
        Assert(!ScreenshotService.IsScreenshotFile("noext"), "no extension not supported");
        Assert(!ScreenshotService.IsScreenshotFile(""), "empty filename not supported");
        Assert(!ScreenshotService.IsScreenshotFile("   "), "whitespace filename not supported");
        Assert(!ScreenshotService.IsScreenshotFile(null), "null filename not supported");

        // 2. BarEvent factory verification
        var barEvent = ScreenshotService.CreateBarEvent("capture.png", @"C:\Screenshots\capture.png");
        AssertEq(BarEventKind.Screenshot, barEvent.Kind, "Kind is Screenshot");
        AssertEq("Screenshot saved", barEvent.Title, "Title is 'Screenshot saved'");
        AssertEq("capture.png", barEvent.Subtitle, "Subtitle is filename");
        AssertEq(@"C:\Screenshots\capture.png", barEvent.Payload as string, "Payload is fullPath");
        AssertEq(60, barEvent.Priority, "Priority is 60");
        AssertEq(TimeSpan.FromSeconds(3), barEvent.DisplayDuration, "Duration is 3s");

        // 3. FeatureScreenshots toggle and extension filtering in service
        var fakeWatcher = new FakeScreenshotWatcher();
        var svc = new ScreenshotService(bus, settings,
            watcherFactory: _ => fakeWatcher,
            directoryResolver: () => @"C:\Screenshots");

        var busEvents = new List<BarEvent>();
        bus.EventRaised += e =>
        {
            if (e.Kind == BarEventKind.Screenshot) busEvents.Add(e);
        };

        svc.Start();

        // When toggle is disabled
        settings.Update(s => s.FeatureScreenshots = false);
        fakeWatcher.Trigger("snap1.png", @"C:\Screenshots\snap1.png");
        AssertEq(0, busEvents.Count, "No event raised when FeatureScreenshots is disabled");

        // When toggle is enabled
        settings.Update(s => s.FeatureScreenshots = true);
        fakeWatcher.Trigger("snap2.txt", @"C:\Screenshots\snap2.txt");
        AssertEq(0, busEvents.Count, "No event raised for unsupported extension (.txt)");

        fakeWatcher.Trigger("snap3.png", @"C:\Screenshots\snap3.png");
        AssertEq(1, busEvents.Count, "Event raised for valid screenshot when enabled");
        AssertEq("snap3.png", busEvents[0].Subtitle, "Event subtitle matches filename");
        AssertEq(@"C:\Screenshots\snap3.png", busEvents[0].Payload as string, "Event payload matches fullPath");

        svc.Dispose();
    }

    private static void TestScreenshotService_MissingDirectoryAndStartupFailure()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        // 1. Missing screenshot directory (resolver returns null or whitespace)
        bool resolverCalled = false;
        var svcMissing = new ScreenshotService(bus, settings,
            watcherFactory: _ => new FakeScreenshotWatcher(),
            directoryResolver: () =>
            {
                resolverCalled = true;
                return null;
            });

        svcMissing.Start();
        Assert(resolverCalled, "Directory resolver was called");
        Assert(!svcMissing.IsStarted, "Service is not started when directory is missing");
        Assert(!svcMissing.HasWatcher, "No watcher when directory is missing");
        Assert(!svcMissing.IsDisposed, "Service is not disposed when directory is missing");

        svcMissing.Dispose();

        // 2. Watcher factory failure
        bool shouldThrow = true;
        var fakeWatcher2 = new FakeScreenshotWatcher();

        var svcFailing = new ScreenshotService(bus, settings,
            watcherFactory: _ =>
            {
                if (shouldThrow) throw new UnauthorizedAccessException("Simulated directory access failure");
                return fakeWatcher2;
            },
            directoryResolver: () => @"C:\Screenshots");

        svcFailing.Start();
        Assert(!svcFailing.IsStarted, "Service is not started when watcher creation throws");
        Assert(!svcFailing.HasWatcher, "No watcher retained after creation throws");
        Assert(!svcFailing.IsDisposed, "Service remains undisposed and recoverable");

        // Retry Start() after condition clears
        shouldThrow = false;
        svcFailing.Start();
        Assert(svcFailing.IsStarted, "Service started on retry");
        Assert(svcFailing.HasWatcher, "Watcher published on successful retry");
        AssertEq(1, fakeWatcher2.StartCount, "Watcher started on retry");

        svcFailing.Dispose();
        AssertEq(1, fakeWatcher2.DisposeCount, "Watcher disposed on service disposal");
    }

    private static void TestScreenshotService_StartVsDisposeRace()
    {
        var bus = new EventBus(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var settings = SettingsService.Instance;

        using var reachedStartGate = new ManualResetEventSlim(false);
        using var releaseStartGate = new ManualResetEventSlim(false);

        var fakeWatcher = new FakeScreenshotWatcher();
        fakeWatcher.OnStart = () =>
        {
            reachedStartGate.Set();
            releaseStartGate.Wait(TimeSpan.FromSeconds(5));
        };

        var svc = new ScreenshotService(bus, settings,
            watcherFactory: _ => fakeWatcher,
            directoryResolver: () => @"C:\Screenshots");

        // 1. Run Start() on background task until it enters watcher.Start()
        var startTask = Task.Run(() => svc.Start());
        Assert(reachedStartGate.Wait(TimeSpan.FromSeconds(2)), "Start reached watcher startup gate");

        // 2. Concurrently call Dispose() on main thread while Start() is paused in watcher.Start()
        var sw = System.Diagnostics.Stopwatch.StartNew();
        svc.Dispose();
        sw.Stop();

        Assert(sw.ElapsedMilliseconds < 500, "Dispose() returned immediately without deadlock");
        Assert(svc.IsDisposed, "Service is disposed");
        Assert(!svc.IsStarted, "Service is not started");

        // 3. Resume startup by releasing the gate
        releaseStartGate.Set();
        Assert(startTask.Wait(TimeSpan.FromSeconds(2)), "Start task completed cleanly");

        // 4. Deferred publication guarantee:
        // Watcher was stopped and disposed when Start() detected disposal.
        // No watcher is retained in the service.
        Assert(!svc.HasWatcher, "No active watcher retained in service");
        AssertEq(1, fakeWatcher.StartCount, "Watcher Start() was called once");
        Assert(fakeWatcher.StopCount >= 1, "Watcher was stopped when Start() detected disposal");
        AssertEq(1, fakeWatcher.DisposeCount, "Watcher was disposed when Start() detected disposal");
        Assert(!fakeWatcher.IsStarted, "Watcher is not started");
        AssertEq(0, fakeWatcher.SubscriberCount, "Created subscriber was detached during cleanup");
    }
}
