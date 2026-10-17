using System.IO;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Services.Settings;

namespace WindowsDynamicBar.Services.Privacy;

/// <summary>
/// Download progress by watching the user's Downloads folder for browser temp files
/// (.crdownload, .part, .download, .opdownload). LIMITATION (documented): Windows does
/// not expose a universal download API; only file-based downloads landing in Downloads
/// are tracked. In-app/async downloads are invisible to this model.
/// </summary>
public sealed class DownloadService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private FileSystemWatcher? _watcher;
    private readonly Dictionary<string, WatchState> _active = new();
    private readonly object _sync = new();
    private System.Threading.Timer? _progressTimer;

    private sealed class WatchState
    {
        public string DisplayName = "";
        public long LastBytes;
        public long TotalEstimate;
        public double Speed;
        public DateTime LastTick;
        public DateTime Started;
    }

    public event Action<string, double, double>? DownloadProgress; // name, fraction, speed

    public DownloadService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public void Start()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dir)) return;

            _watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher.Created += (s, e) => OnFileActivity(e.FullPath, isNew: true);
            _watcher.Changed += (s, e) => OnFileActivity(e.FullPath, isNew: false);
            // Browsers finish by RENAMING the temp file (X.crdownload -> X); Chrome also renames
            // "Unconfirmed 123.crdownload" -> "X.crdownload" first. A deleted temp file was cancelled.
            _watcher.Renamed += (s, e) => OnFileRenamed(e.OldFullPath, e.FullPath);
            _watcher.Deleted += (s, e) => OnFileDone(e.FullPath, completed: false);
            _watcher.EnableRaisingEvents = true;

            _progressTimer = new System.Threading.Timer(_ => TickProgress(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            Log.Info("Download watcher started", ("dir", dir));
        }
        catch (Exception ex) { Log.Warning("Download watcher failed", ("error", ex.Message)); }
    }

    private static bool IsTempDownload(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".crdownload" or ".part" or ".download" or ".opdownload" or ".partial";
    }

    private void OnFileActivity(string path, bool isNew)
    {
        // Cheap checks first: Changed fires for every write while a download runs, and
        // Settings.Current is a deep clone.
        if (!IsTempDownload(path)) return;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return;
            bool tracked;
            lock (_sync) tracked = _active.ContainsKey(path);
            if (!tracked && !_settings.Current.FeatureDownloads) return;
            lock (_sync)
            {
                if (!_active.TryGetValue(path, out var st))
                {
                    st = new WatchState
                    {
                        DisplayName = CleanName(fi.Name),
                        Started = DateTime.UtcNow,
                        LastTick = DateTime.UtcNow,
                    };
                    _active[path] = st;
                    _bus.Raise(BarEvent.Create(BarEventKind.Download, "Downloading…", st.DisplayName,
                        value: 0, valueText: "0%", payload: st.DisplayName, priority: 35,
                        displayDuration: TimeSpan.FromSeconds(6)));
                }
                st.LastBytes = fi.Length;
            }
        }
        catch { }
    }

    private static string CleanName(string tempName)
    {
        foreach (string ext in new[] { ".crdownload", ".part", ".download", ".opdownload", ".partial" })
            if (tempName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return tempName[..^ext.Length];
        return tempName;
    }

    private void TickProgress()
    {
        List<(string path, string name, double frac, double speed)> updates = new();
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            foreach (var (path, st) in _active.ToList())
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists) { _active.Remove(path); continue; }
                    long delta = fi.Length - st.LastBytes;
                    double secs = (now - st.LastTick).TotalSeconds;
                    if (secs >= 0.9)
                    {
                        st.Speed = delta / secs;
                        st.LastBytes = fi.Length;
                        st.LastTick = now;
                    }
                    // Heuristic total: never shrink below current; grow with observed rate for ETA.
                    st.TotalEstimate = Math.Max(st.TotalEstimate, fi.Length + (long)(st.Speed * 8));
                    double frac = st.TotalEstimate > 0 ? Math.Min(0.999, fi.Length / (double)st.TotalEstimate) : 0;
                    // Only surface files that are actually moving — a stalled temp file
                    // must not keep popping the bar open once its card has collapsed.
                    if (delta > 0)
                        updates.Add((path, st.DisplayName, frac, st.Speed));
                }
                catch { _active.Remove(path); }
            }
        }

        foreach (var (path, name, frac, speed) in updates)
        {
            DownloadProgress?.Invoke(name, frac, speed);
            string speedText = FormatSpeed(speed);
            _bus.Raise(BarEvent.Create(BarEventKind.Download, name,
                $"{Math.Round(frac * 100)}% · {speedText}", value: frac,
                valueText: $"{Math.Round(frac * 100)}%", priority: 35,
                displayDuration: TimeSpan.FromSeconds(2), isTick: true));
        }
    }

    private void OnFileRenamed(string oldPath, string newPath)
    {
        if (!IsTempDownload(oldPath)) return;
        if (IsTempDownload(newPath))
        {
            // Still downloading under its real name: carry the progress state over.
            lock (_sync)
            {
                if (!_active.Remove(oldPath, out var st)) return;
                st.DisplayName = CleanName(Path.GetFileName(newPath));
                _active[newPath] = st;
            }
            return;
        }
        lock (_sync) _active.Remove(oldPath);
        if (_settings.Current.FeatureDownloads) AnnounceComplete(Path.GetFileName(newPath));
    }

    private void OnFileDone(string path, bool completed)
    {
        string name;
        lock (_sync)
        {
            if (!_active.Remove(path, out var st)) return;
            name = st.DisplayName;
        }
        // Some tools copy to the final name and then delete the temp file: that is a completion too.
        string final = Path.Combine(Path.GetDirectoryName(path) ?? "", name);
        if (completed || File.Exists(final)) AnnounceComplete(name);
    }

    private void AnnounceComplete(string name) =>
        _bus.Raise(BarEvent.Create(BarEventKind.Toast, "Download complete", name,
            payload: name, priority: 65, displayDuration: TimeSpan.FromSeconds(4)));

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "…";
        if (bytesPerSec > 1024 * 1024) return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
        if (bytesPerSec > 1024) return $"{bytesPerSec / 1024:F0} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { }
        _progressTimer?.Dispose();
    }
}
