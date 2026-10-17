using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.Services.Settings;

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
    private bool _hasLoaded;

    public event Action<SettingsModel>? SettingsChanged;

    /// <summary>Set when settings.json could not be read or was moved aside; shown once, then cleared.</summary>
    public string? LoadProblem { get; private set; }

    public void ClearLoadProblem() => LoadProblem = null;

    public string SettingsPath { get; internal set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WindowsDynamicBar", "settings.json");

    public SettingsModel Current { get { lock (_sync) return Clone(_current); } }

    /// <summary>Test seam: override file stream creation to test transient errors deterministically.</summary>
    internal static Func<string, Stream>? StreamOpenerForTesting { get; set; }

    private SettingsService() { }

    public SettingsModel Load() => Load(SettingsPath);

    /// <summary>Read, migrate and validate. A file that cannot be parsed is renamed to
    /// settings.bad-&lt;date&gt;.json and defaults are used (LoadProblem explains it once).
    /// Transient read errors are retried; if still unreadable, the file is preserved on disk.</summary>
    internal SettingsModel Load(string path)
    {
        lock (_sync)
        {
            if (!File.Exists(path))
            {
                if (!_hasLoaded)
                {
                    _current = new SettingsModel();
                    _hasLoaded = true;
                }
                return Clone(_current);
            }

            string? json = null;
            Exception? readEx = null;

            // Bounded retry policy for transient file locks or sharing violations
            const int maxReadAttempts = 3;
            for (int attempt = 0; attempt < maxReadAttempts; attempt++)
            {
                try
                {
                    if (StreamOpenerForTesting is not null)
                    {
                        using var customStream = StreamOpenerForTesting(path);
                        using var reader = new StreamReader(customStream);
                        json = reader.ReadToEnd();
                    }
                    else
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        json = reader.ReadToEnd();
                    }
                    readEx = null;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    readEx = ex;
                    if (attempt < maxReadAttempts - 1)
                    {
                        Thread.Sleep(25 * (attempt + 1));
                    }
                }
            }

            if (readEx is not null)
            {
                // File exists but could not be read due to persistent lock or permission issue.
                // Do NOT move to .bad; preserve on disk and report problem.
                if (!_hasLoaded)
                {
                    _current = new SettingsModel();
                    _hasLoaded = true;
                    LoadProblem = "Your settings file could not be read; defaults are in use.";
                }
                else
                {
                    LoadProblem = "Your settings file could not be read; current settings are in use.";
                }
                Log.Warning("Settings file unreadable; preserving file and memory state", ("error", readEx.GetType().Name), ("path", path));
                return Clone(_current);
            }

            try
            {
                var loaded = JsonSerializer.Deserialize<SettingsModel>(json!, JsonOpts)
                             ?? throw new JsonException("settings.json is empty");
                Migrate(loaded);
                Validate(loaded);
                _current = loaded;
                _hasLoaded = true;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                _current = new SettingsModel();
                _hasLoaded = true;
                string dir = Path.GetDirectoryName(path) ?? ".";
                string bad = Path.Combine(dir, $"settings.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                try
                {
                    File.Move(path, bad, overwrite: true);
                    LoadProblem = $"Your settings file was damaged and has been reset. The old file was kept as {Path.GetFileName(bad)}.";
                }
                catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
                {
                    LoadProblem = "Your settings file could not be read; defaults are in use.";
                }
                Log.Warning("Settings file damaged; defaults loaded", ("error", ex.GetType().Name), ("backup", bad));
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
        bool saved = false;
        lock (_sync)
        {
            var candidate = Clone(model);
            candidate.SettingsVersion = CurrentVersion;
            Validate(candidate);

            string targetPath = SettingsPath;
            string? dir = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            Directory.CreateDirectory(dir);

            string tmp = Path.Combine(dir, $"settings.{Guid.NewGuid():N}.tmp");
            try
            {
                var json = JsonSerializer.Serialize(candidate, JsonOpts);
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                const int maxReplaceAttempts = 3;
                for (int attempt = 0; attempt < maxReplaceAttempts; attempt++)
                {
                    try
                    {
                        if (File.Exists(targetPath))
                        {
                            File.Replace(tmp, targetPath, null, ignoreMetadataErrors: true);
                        }
                        else
                        {
                            File.Move(tmp, targetPath);
                        }
                        saved = true;
                        break;
                    }
                    catch (Exception ex) when (attempt < maxReplaceAttempts - 1 && (ex is IOException or UnauthorizedAccessException))
                    {
                        Thread.Sleep(25 * (attempt + 1));
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Move(tmp, targetPath, overwrite: true);
                        saved = true;
                        break;
                    }
                }

                if (saved)
                {
                    _current = candidate;
                    _hasLoaded = true;
                }
                else
                {
                    Log.Error("Settings save failed after retries: unable to replace settings file");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Settings save failed", ex);
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
        }

        if (saved)
        {
            SettingsChanged?.Invoke(Current);
        }
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
