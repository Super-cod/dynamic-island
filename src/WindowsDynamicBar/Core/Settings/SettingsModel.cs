using WindowsDynamicBar.Core.Buddy;
using System.Text.Json.Serialization;

namespace WindowsDynamicBar.Core.Settings;

public enum AppTheme { Dark, Light, Translucent }
public enum PerformanceMode { Balanced, LowPower, Performance }
public enum BarPosition { TopCenter, TopLeft, TopRight, Custom }
public enum ClockFormat { System, Hours12, Hours24 }
/// <summary>Auto = album colour while music plays, time-of-day colour otherwise.</summary>
public enum AccentMode { Auto, TimeOfDay, Fixed }
public enum ReduceMotionMode { FollowSystem, Always, Never }

public sealed class SettingsModel
{
    /// <summary>Schema version of settings.json (bumped when fields change meaning). A file without
    /// the field is version 1; Save always writes the current version.</summary>
    public int SettingsVersion { get; set; } = 1;

    // ---- General ----
    public bool EnableAtStartup { get; set; } = true;
    public bool BarEnabled { get; set; } = true;
    public BarPosition Position { get; set; } = BarPosition.TopCenter;
    public int MonitorIndex { get; set; } = -1;          // -1 = primary, -2 = screen with the mouse, 0.. = monitor
    public double CustomOffsetX { get; set; } = 0;
    public bool AlwaysOnTop { get; set; } = true;
    public bool AutoHideFullscreen { get; set; } = true;
    public double AnimationSpeed { get; set; } = 1.0;    // multiplier
    public PerformanceMode PerformanceMode { get; set; } = PerformanceMode.Balanced;
    public bool FirstRunCompleted { get; set; } = false;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool FirstRunDone
    {
        get => FirstRunCompleted;
        set => FirstRunCompleted = value;
    }
    public bool SingleClickExpands { get; set; } = false;
    public bool HoverPreview { get; set; } = false;
    public int CollapseDelayMs { get; set; } = 3500;

    // ---- Appearance ----
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public double Opacity { get; set; } = 0.92;
    public bool BlurEnabled { get; set; } = false;
    public double CornerRadius { get; set; } = 26;
    public double BarWidth { get; set; } = 260;
    public double BarHeight { get; set; } = 40;
    public bool ShadowEnabled { get; set; } = false;
    public string AccentColor { get; set; } = "#FF0A84FF";

    // ---- Animations ----
    public double AnimationDurationMs { get; set; } = 320;
    public double SpringStiffness { get; set; } = 220;
    public double SpringDamping { get; set; } = 24;
    public double ExpansionScale { get; set; } = 1.0;
    public bool ReduceMotion { get; set; } = false;        // effective value (derived from ReduceMotionMode)
    public ReduceMotionMode ReduceMotionMode { get; set; } = ReduceMotionMode.FollowSystem;
    public AccentMode AccentMode { get; set; } = AccentMode.Auto;
    public ClockFormat ClockFormat { get; set; } = ClockFormat.System;

    // ---- Features (on/off) ----
    public bool FeatureMedia { get; set; } = true;
    public bool FeatureVolume { get; set; } = true;
    public bool FeatureBrightness { get; set; } = true;
    public bool FeatureBattery { get; set; } = true;
    public bool FeatureNetwork { get; set; } = true;
    public bool FeatureBluetooth { get; set; } = true;
    public bool FeatureClipboard { get; set; } = false;    // off by default (privacy)
    public bool FeatureNotifications { get; set; } = false; // requires notification access
    public bool FeatureTimers { get; set; } = true;
    public bool FeatureSystemMonitor { get; set; } = false;
    public bool FeaturePrivacyIndicators { get; set; } = true;
    public bool FeatureDownloads { get; set; } = true;
    public bool FeatureUsb { get; set; } = true;
    public bool FeatureScreenshots { get; set; } = true;
    public bool CollapsedMediaArt { get; set; } = true;
    public bool FeatureIdleDashboard { get; set; } = true;
    // ---- Island Buddy ----
    public bool FeatureBuddy { get; set; } = true;
    public bool FeatureBuddyCard { get; set; } = true;
    public string BuddyName { get; set; } = "Dot";
    public BuddyPersonality BuddyPersonality { get; set; } = BuddyPersonality.Playful;
    public bool BuddyAnimationEnabled { get; set; } = true;
    public bool BuddySpeechEnabled { get; set; } = true;
    public double BuddyReactionIntensity { get; set; } = 1.0;
    public double BuddyRandomActionFrequency { get; set; } = 1.0;
    public bool BuddyQuiet { get; set; } = false;
    public DateTime? BuddyQuietUntil { get; set; } = null;
    public BuddyTalkFrequency BuddyTalkFrequency { get; set; } = BuddyTalkFrequency.Normal;
    public bool BuddyWeatherComments { get; set; } = true;
    public bool BuddySystemComments { get; set; } = true;
    public bool BuddyLateNightMessages { get; set; } = true;
    public BuddyOutfit BuddyOutfit { get; set; } = BuddyOutfit.None;
    public bool BuddyRemindWater { get; set; } = false;
    public int BuddyRemindWaterInterval { get; set; } = 45;
    public bool BuddyRemindEyes { get; set; } = false;
    public int BuddyRemindEyesInterval { get; set; } = 20;
    public int BuddyPets { get; set; } = 0;
    public int BuddyStreak { get; set; } = 1;
    public string BuddyLastUsedDate { get; set; } = "";
    public int BuddyFocusSessions { get; set; } = 0;
    public bool BuddyDailyGreeting { get; set; } = true;
    public bool BuddyShopDemoMode { get; set; } = true;
    public List<string> BuddyOwnedPacks { get; set; } = new();
    public bool BuddyCommentFocus { get; set; } = true;
    public bool BuddyCommentNotif { get; set; } = true;
    public bool BuddyCommentSpeed { get; set; } = true;
    public bool BuddyCommentScreenshot { get; set; } = true;
    public bool BuddyCommentClipboard { get; set; } = true;
    public bool BuddyCommentMirror { get; set; } = true;
    public bool BuddyCommentSearch { get; set; } = true;
    public bool BuddyCommentCalendar { get; set; } = true;
    public bool BuddyCommentMusic { get; set; } = true;

    // ---- Buddy System Reactions (Status Events) ----
    public bool BuddyReactionsEnabled { get; set; } = true;
    public bool BuddyQuietDuringCalls { get; set; } = true;
    public bool BuddyReactScreenshot { get; set; } = true;
    public bool BuddyReactWifi { get; set; } = true;
    public bool BuddyReactBluetooth { get; set; } = true;
    public bool BuddyReactVpn { get; set; } = true;
    public bool BuddyReactPower { get; set; } = true;
    public bool BuddyReactAudioOutput { get; set; } = true;
    public bool BuddyReactPrivacy { get; set; } = true;
    public bool BuddyReactCapsLock { get; set; } = true;
    public bool BuddyReactAirplane { get; set; } = true;
    public bool BuddyReactDnd { get; set; } = true;
    public bool BuddyReactClipboard { get; set; } = true;
    public bool FeatureSearch { get; set; } = true;
    public bool FeatureMirror { get; set; } = true;
    public bool FeatureSpeedTest { get; set; } = true;
    public bool FeatureToggles { get; set; } = true;
    public bool FeatureVolumeHud { get; set; } = true;
    public bool SystemMonitorInBar { get; set; } = false;

    // ---- Net panel daily transfer totals (persisted so "today" survives restarts) ----
    public string NetDailyDate { get; set; } = "";
    public long NetDailyRxBytes { get; set; }
    public long NetDailyTxBytes { get; set; }  // show CPU/RAM in collapsed bar

    // ---- Privacy ----
    public bool ClipboardMonitoring { get; set; } = false;
    public bool NotificationAccess { get; set; } = false;
    public bool TelemetryEnabled { get; set; } = false;    // no telemetry is ever sent; kept for completeness
    public bool LocalOnlyMode { get; set; } = true;

    // ---- Notifications ----
    /// <summary>Island Do Not Disturb: notification cards do not open the island.</summary>
    public bool DoNotDisturb { get; set; } = false;

    // ---- Shortcuts ----
    public string HotkeyToggle { get; set; } = "Ctrl+Alt+Space";
    public string HotkeyMedia { get; set; } = "Ctrl+Alt+M";
    public string HotkeyTimer { get; set; } = "Ctrl+Alt+T";
    public string HotkeySearch { get; set; } = "Ctrl+Space";
    public string HotkeyClipboard { get; set; } = "Ctrl+Alt+V";

    // ---- Clipboard history ----
    public bool FeatureClipboardHistory { get; set; } = true;
    public int ClipboardLimit { get; set; } = 10;                 // 1..50 unpinned items
    public bool ClipboardPersist { get; set; } = false;           // "Remember history after restart" (DPAPI)
    public int ClipboardAutoClearHours { get; set; } = 0;         // 0 = never, else 1 / 6 / 24
    public bool ClipboardPasteOnSelect { get; set; } = false;
    public string ClipboardIgnoredApps { get; set; } =
        "KeePass,KeePassXC,1Password,Bitwarden,LastPass,Dashlane,Enpass,ProtonPass";

    // ---- Island ring pages (comma-separated keys: cpu,gpu,ram,mir,net,wx,clip,tog,tmr) ----
    public string IslandPage1 { get; set; } = "cpu,gpu,ram,mir,net,wx";
    public string IslandPage2 { get; set; } = "clip,tog,tmr";

    // ---- Warning strip levels ----
    public int WarnRamPercent { get; set; } = 90;
    public int WarnCpuPercent { get; set; } = 95;
    public int WarnBatteryPercent { get; set; } = 15;

    // ---- Units ----
    public bool TemperatureFahrenheit { get; set; } = false;

    // ---- AI (disabled by default) ----
    public bool AiEnabled { get; set; } = false;
    public string AiProvider { get; set; } = "openai-compatible";
    public string AiEndpoint { get; set; } = "http://localhost:11434/v1";
    public string AiModel { get; set; } = "llama3";
    public string AiKeyDPAPI { get; set; } = "";          // DPAPI-protected, never plaintext

    // ---- Weather ----
    public string WeatherLocation { get; set; } = "Chennai";
    public double? WeatherLatitude { get; set; } = 13.0827;
    public double? WeatherLongitude { get; set; } = 80.2707;
    public bool WeatherAutoLocation { get; set; } = false;

    // ---- Advanced ----
    public bool CheckUpdatesOnStart { get; set; } = false;
    public string UpdateFeedUrl { get; set; } = "";
    public int LogLevel { get; set; } = 1;
}
