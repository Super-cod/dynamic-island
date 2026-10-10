using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsDynamicBar.Core.Logging;

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
    public bool BlurEnabled { get; set; } = true;
    public double CornerRadius { get; set; } = 26;
    public double BarWidth { get; set; } = 260;
    public double BarHeight { get; set; } = 40;
    public bool ShadowEnabled { get; set; } = true;
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

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lazy<SettingsService> Lazy = new(() => new SettingsService());
    public static SettingsService Instance => Lazy.Value;

    public const int CurrentVersion = 2;

    private readonly object _sync = new();
    private SettingsModel _current = new();

    public event Action<SettingsModel>? SettingsChanged;

    /// <summary>Set when settings.json could not be read and was moved aside; shown once, then cleared.</summary>
    public string? LoadProblem { get; private set; }

    public void ClearLoadProblem() => LoadProblem = null;

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WindowsDynamicBar", "settings.json");

    public SettingsModel Current { get { lock (_sync) return Clone(_current); } }

    private SettingsService() { }

    public SettingsModel Load() => Load(SettingsPath);

    /// <summary>Read, migrate and validate. A file that cannot be parsed is renamed to
    /// settings.bad-&lt;date&gt;.json and defaults are used (LoadProblem explains it once).</summary>
    internal SettingsModel Load(string path)
    {
        lock (_sync)
        {
            _current = new SettingsModel();
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    _current = JsonSerializer.Deserialize<SettingsModel>(json, JsonOpts)
                               ?? throw new JsonException("settings.json is empty");
                    Migrate(_current);
                    Validate(_current);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    _current = new SettingsModel();
                    string bad = Path.Combine(Path.GetDirectoryName(path)!, $"settings.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                    try
                    {
                        File.Move(path, bad, overwrite: true);
                        LoadProblem = $"Your settings file was damaged and has been reset. The old file was kept as {Path.GetFileName(bad)}.";
                    }
                    catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
                    {
                        LoadProblem = "Your settings file could not be read; defaults are in use.";
                    }
                    Log.Warning("Settings file unreadable; defaults loaded", ("error", ex.GetType().Name), ("backup", bad));
                }
            }
            return Clone(_current);
        }
    }

    /// <summary>Older files: carry old meanings over to new fields.</summary>
    private static void Migrate(SettingsModel m)
    {
        if (m.SettingsVersion < 2)
        {
            // v1 had a plain Reduce Motion switch.
            m.ReduceMotionMode = m.ReduceMotion ? ReduceMotionMode.Always : ReduceMotionMode.FollowSystem;
        }
        m.SettingsVersion = CurrentVersion;
    }

    /// <summary>Clamp every value into its valid range; unknown enum numbers and broken hotkeys fall
    /// back to defaults so a hand-edited file can never break the app.</summary>
    internal static void Validate(SettingsModel m)
    {
        var d = new SettingsModel();
        m.MonitorIndex = Math.Clamp(m.MonitorIndex, -2, 15);
        m.Opacity = double.IsFinite(m.Opacity) ? Math.Clamp(m.Opacity, 0.4, 1.0) : d.Opacity;
        m.CornerRadius = double.IsFinite(m.CornerRadius) ? Math.Clamp(m.CornerRadius, 8, 40) : d.CornerRadius;
        m.BarWidth = double.IsFinite(m.BarWidth) ? Math.Clamp(m.BarWidth, 160, 760) : d.BarWidth;
        m.BarHeight = double.IsFinite(m.BarHeight) ? Math.Clamp(m.BarHeight, 28, 80) : d.BarHeight;
        m.AnimationSpeed = double.IsFinite(m.AnimationSpeed) ? Math.Clamp(m.AnimationSpeed, 0.3, 2.0) : d.AnimationSpeed;
        m.SpringStiffness = double.IsFinite(m.SpringStiffness) ? Math.Clamp(m.SpringStiffness, 60, 400) : d.SpringStiffness;
        m.SpringDamping = double.IsFinite(m.SpringDamping) ? Math.Clamp(m.SpringDamping, 4, 60) : d.SpringDamping;
        m.CollapseDelayMs = Math.Clamp(m.CollapseDelayMs, 300, 10000);
        m.ClipboardLimit = Math.Clamp(m.ClipboardLimit, 1, 50);
        m.ClipboardAutoClearHours = m.ClipboardAutoClearHours is 0 or 1 or 6 or 24 ? m.ClipboardAutoClearHours : 0;
        m.WarnRamPercent = Math.Clamp(m.WarnRamPercent, 50, 100);
        m.WarnCpuPercent = Math.Clamp(m.WarnCpuPercent, 50, 100);
        m.WarnBatteryPercent = Math.Clamp(m.WarnBatteryPercent, 5, 50);
        m.LogLevel = Math.Clamp(m.LogLevel, 0, 4);
        if (!Enum.IsDefined(m.Theme)) m.Theme = d.Theme;
        if (!Enum.IsDefined(m.PerformanceMode)) m.PerformanceMode = d.PerformanceMode;
        if (!Enum.IsDefined(m.Position)) m.Position = d.Position;
        if (!Enum.IsDefined(m.ClockFormat)) m.ClockFormat = d.ClockFormat;
        if (!Enum.IsDefined(m.AccentMode)) m.AccentMode = d.AccentMode;
        if (!Enum.IsDefined(m.ReduceMotionMode)) m.ReduceMotionMode = d.ReduceMotionMode;
        if (!IsColor(m.AccentColor)) m.AccentColor = d.AccentColor;
        m.HotkeyToggle = ValidHotkey(m.HotkeyToggle, d.HotkeyToggle);
        m.HotkeyMedia = ValidHotkey(m.HotkeyMedia, d.HotkeyMedia);
        m.HotkeyTimer = ValidHotkey(m.HotkeyTimer, d.HotkeyTimer);
        m.HotkeySearch = ValidHotkey(m.HotkeySearch, d.HotkeySearch);
        m.HotkeyClipboard = ValidHotkey(m.HotkeyClipboard, d.HotkeyClipboard);
        m.IslandPage1 ??= d.IslandPage1;
        m.IslandPage2 ??= d.IslandPage2;
        m.ClipboardIgnoredApps ??= "";
        m.UpdateFeedUrl ??= "";
        m.WeatherLocation ??= d.WeatherLocation;
    }

    /// <summary>Empty = disabled (allowed); otherwise it must parse.</summary>
    private static string ValidHotkey(string? value, string fallback) =>
        value is null ? fallback : value.Length == 0 || Hotkeys.TryParse(value, out _, out _) ? value : fallback;

    private static bool IsColor(string? hex) =>
        hex is { Length: 9 } && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _);

    public void Save(SettingsModel model)
    {
        lock (_sync)
        {
            _current = Clone(model);
            _current.SettingsVersion = CurrentVersion;
            Validate(_current);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                var json = JsonSerializer.Serialize(_current, JsonOpts);
                var tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, SettingsPath, overwrite: true);
            }
            catch (Exception ex) { Log.Error("Settings save failed", ex); }
        }
        SettingsChanged?.Invoke(Current);
    }

    public void Update(Action<SettingsModel> mutate)
    {
        var m = Current;
        mutate(m);
        Save(m);
    }

    public static SettingsModel Clone(SettingsModel m) =>
        JsonSerializer.Deserialize<SettingsModel>(JsonSerializer.Serialize(m, JsonOpts), JsonOpts)!;
}
