using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Core.Settings;

public enum AppTheme { Dark, Light, Translucent }
public enum PerformanceMode { Balanced, LowPower, Performance }
public enum BarPosition { TopCenter, TopLeft, TopRight, Custom }

public sealed class SettingsModel
{
    // ---- General ----
    public bool EnableAtStartup { get; set; } = true;
    public bool BarEnabled { get; set; } = true;
    public BarPosition Position { get; set; } = BarPosition.TopCenter;
    public int MonitorIndex { get; set; } = -1;          // -1 = primary
    public double CustomOffsetX { get; set; } = 0;
    public bool AlwaysOnTop { get; set; } = true;
    public bool AutoHideFullscreen { get; set; } = true;
    public double AnimationSpeed { get; set; } = 1.0;    // multiplier
    public PerformanceMode PerformanceMode { get; set; } = PerformanceMode.Balanced;
    public bool FirstRunCompleted { get; set; } = false;
    public bool SingleClickExpands { get; set; } = false;
    public bool HoverPreview { get; set; } = true;
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
    public bool ReduceMotion { get; set; } = false;

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

    // ---- Shortcuts ----
    public string HotkeyToggle { get; set; } = "Ctrl+Alt+Space";
    public string HotkeyMedia { get; set; } = "Ctrl+Alt+M";
    public string HotkeyTimer { get; set; } = "Ctrl+Alt+T";
    public string HotkeySearch { get; set; } = "Ctrl+Space";

    // ---- AI (disabled by default) ----
    public bool AiEnabled { get; set; } = false;
    public string AiProvider { get; set; } = "openai-compatible";
    public string AiEndpoint { get; set; } = "http://localhost:11434/v1";
    public string AiModel { get; set; } = "llama3";
    public string AiKeyDPAPI { get; set; } = "";          // DPAPI-protected, never plaintext

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

    private readonly object _sync = new();
    private SettingsModel _current = new();

    public event Action<SettingsModel>? SettingsChanged;

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WindowsDynamicBar", "settings.json");

    public SettingsModel Current { get { lock (_sync) return Clone(_current); } }

    private SettingsService() { }

    public SettingsModel Load()
    {
        lock (_sync)
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    _current = JsonSerializer.Deserialize<SettingsModel>(json, JsonOpts) ?? new SettingsModel();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Settings load failed, using defaults", ex);
                _current = new SettingsModel();
            }
            return Clone(_current);
        }
    }

    public void Save(SettingsModel model)
    {
        lock (_sync)
        {
            _current = Clone(model);
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
