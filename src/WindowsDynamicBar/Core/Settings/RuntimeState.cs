using System.IO;
using System.Text.Json;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Core.Settings;

/// <summary>Values the app records while running (not user choices). Kept out of settings.json so
/// that writing them never raises SettingsChanged and never re-applies the whole UI.</summary>
public sealed class RuntimeStateModel
{
    public string NetDailyDate { get; set; } = "";
    public long NetDailyRxBytes { get; set; }
    public long NetDailyTxBytes { get; set; }

    /// <summary>UTC end time of the running countdown timer (survives an app restart); null = none.</summary>
    public DateTime? TimerEndUtc { get; set; }
    public double TimerTotalSeconds { get; set; }
    public string TimerLabel { get; set; } = "";
    /// <summary>Seconds left on a paused timer (no end time while paused); 0 = not paused.</summary>
    public double TimerPausedSeconds { get; set; }
}

/// <summary>%AppData%\WindowsDynamicBar\state.json, written atomically (temp file then replace).</summary>
public sealed class RuntimeState
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsDynamicBar", "state.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly Lazy<RuntimeState> Lazy = new(() => new RuntimeState(DefaultPath));
    private static RuntimeState? _override;
    public static RuntimeState Instance => _override ?? Lazy.Value;

    /// <summary>Tests: point the store at a scratch file so the user's state.json is never touched.</summary>
    internal static void UseForTests(string path) => _override = new RuntimeState(path);

    private readonly object _sync = new();
    private readonly string _path;
    private RuntimeStateModel _current;

    public RuntimeState(string path)
    {
        _path = path;
        _current = Read(path);
    }

    /// <summary>Returns a copy; mutate it through <see cref="Update"/>.</summary>
    public RuntimeStateModel Current
    {
        get { lock (_sync) return Copy(_current); }
    }

    public void Update(Action<RuntimeStateModel> mutate)
    {
        lock (_sync)
        {
            var next = Copy(_current);
            mutate(next);
            _current = next;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_current, JsonOpts));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (IOException ex) { Log.Warning("State save failed", ("error", ex.Message)); }
            catch (UnauthorizedAccessException ex) { Log.Warning("State save failed", ("error", ex.Message)); }
        }
    }

    private static RuntimeStateModel Read(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<RuntimeStateModel>(File.ReadAllText(path), JsonOpts) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning("State file unreadable, starting fresh", ("error", ex.Message));
        }
        return new RuntimeStateModel();
    }

    private static RuntimeStateModel Copy(RuntimeStateModel m) => new()
    {
        NetDailyDate = m.NetDailyDate,
        NetDailyRxBytes = m.NetDailyRxBytes,
        NetDailyTxBytes = m.NetDailyTxBytes,
        TimerEndUtc = m.TimerEndUtc,
        TimerTotalSeconds = m.TimerTotalSeconds,
        TimerLabel = m.TimerLabel,
        TimerPausedSeconds = m.TimerPausedSeconds,
    };
}
