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
    private readonly string? _customDirectory;
    private FileSystemWatcher? _watcher;
    private readonly Dictionary<string, WatchState> _active = new();
    private readonly object _sync = new();
    private System.Threading.Timer? _progressTimer;
    private bool _started;
    private bool _disposed;

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

    public bool IsStarted
    {
        get { lock (_sync) return _started; }
    }

    public bool IsDisposed
    {
        get { lock (_sync) return _disposed; }
    }

    public int ActiveCount
    {
        get { lock (_sync) return _active.Count; }
    }

    public DownloadService(EventBus bus, SettingsService settings)
        : this(bus, settings, customDirectory: null)
    {
    }

    internal DownloadService(EventBus bus, SettingsService settings, string? customDirectory)
    {
        _bus = bus;
        _settings = settings;
        _customDirectory = customDirectory;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _started) return;
            _started = true;
        }

        FileSystemWatcher? watcher = null;
        System.Threading.Timer? timer = null;
        try
        {
            string dir = _customDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dir))
            {
                lock (_sync) { _started = false; }
                return;
            }

            watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Created += OnCreated;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Deleted += OnDeleted;

            timer = new System.Threading.Timer(_ => TickProgress(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            lock (_sync)
            {
                if (_disposed)
                {
                    CleanupWatcher(watcher);
                    timer.Dispose();
                    return;
                }
                _watcher = watcher;
                _progressTimer = timer;
            }

            watcher.EnableRaisingEvents = true;
            Log.Info("Download watcher started", ("dir", dir));
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _started = false;
                _watcher = null;
                _progressTimer = null;
            }
            CleanupWatcher(watcher);
            try { timer?.Dispose(); } catch { }
            Log.Warning("Download watcher failed", ("error", ex.Message));
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => OnFileActivity(e.FullPath, isNew: true);
    private void OnChanged(object sender, FileSystemEventArgs e) => OnFileActivity(e.FullPath, isNew: false);
    private void OnRenamed(object sender, RenamedEventArgs e) => OnFileRenamed(e.OldFullPath, e.FullPath);
    private void OnDeleted(object sender, FileSystemEventArgs e) => OnFileDone(e.FullPath, completed: false);

    internal static bool IsTempDownload(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".crdownload" or ".part" or ".download" or ".opdownload" or ".partial";
    }

    internal void OnFileActivity(string path, bool isNew)
    {
        // Cheap checks first: Changed fires for every write while a download runs, and
        // Settings.Current is a deep clone.
        if (!IsTempDownload(path)) return;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return;
            ProcessFileActivityCore(path, fi.Name, fi.Length, isNew);
        }
        catch { }
    }

    internal void ProcessFileActivityCore(string path, string fileName, long fileLength, bool isNew)
    {
        bool tracked;
        lock (_sync)
        {
            if (_disposed) return;
            tracked = _active.ContainsKey(path);
        }
        if (!tracked && !_settings.Current.FeatureDownloads) return;

        BarEvent? eventToRaise = null;
        lock (_sync)
        {
            if (_disposed) return;
            if (!_active.TryGetValue(path, out var st))
            {
                st = new WatchState
                {
                    DisplayName = CleanName(fileName),
                    Started = DateTime.UtcNow,
                    LastTick = DateTime.UtcNow,
                };
                _active[path] = st;
                eventToRaise = BarEvent.Create(BarEventKind.Download, "Downloading…", st.DisplayName,
                    value: 0, valueText: "0%", payload: st.DisplayName, priority: 35,
                    displayDuration: TimeSpan.FromSeconds(6));
            }
            st.LastBytes = fileLength;
        }

        if (eventToRaise is not null)
        {
            _bus.Raise(eventToRaise);
        }
    }

    internal static string CleanName(string tempName)
    {
        foreach (string ext in new[] { ".crdownload", ".part", ".download", ".opdownload", ".partial" })
            if (tempName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return tempName[..^ext.Length];
        return tempName;
    }

    internal void TickProgress() => TickProgress(lengthProvider: null, nowUtc: null);

    internal void TickProgress(Func<string, long?>? lengthProvider, DateTime? nowUtc)
    {
        List<(string path, string name, double frac, double speed)> updates = new();
        lock (_sync)
        {
            if (_disposed) return;
            var now = nowUtc ?? DateTime.UtcNow;
            foreach (var (path, st) in _active.ToList())
            {
                try
                {
                    long? length;
                    if (lengthProvider is not null)
                    {
                        length = lengthProvider(path);
                    }
                    else
                    {
                        var fi = new FileInfo(path);
                        length = fi.Exists ? fi.Length : null;
                    }

                    if (length is not long curBytes)
                    {
                        _active.Remove(path);
                        continue;
                    }

                    long delta = curBytes - st.LastBytes;
                    double secs = (now - st.LastTick).TotalSeconds;
                    if (secs >= 0.9)
                    {
                        st.Speed = delta / secs;
                        st.LastBytes = curBytes;
                        st.LastTick = now;
                    }
                    // Heuristic total: never shrink below current; grow with observed rate for ETA.
                    st.TotalEstimate = Math.Max(st.TotalEstimate, curBytes + (long)(st.Speed * 8));
                    double frac = st.TotalEstimate > 0 ? Math.Min(0.999, curBytes / (double)st.TotalEstimate) : 0;
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
            lock (_sync)
            {
                if (_disposed) return;
            }

            DownloadProgress?.Invoke(name, frac, speed);
            string speedText = FormatSpeed(speed);
            _bus.Raise(BarEvent.Create(BarEventKind.Download, name,
                $"{Math.Round(frac * 100)}% · {speedText}", value: frac,
                valueText: $"{Math.Round(frac * 100)}%", priority: 35,
                displayDuration: TimeSpan.FromSeconds(2), isTick: true));
        }
    }

    internal void OnFileRenamed(string oldPath, string newPath)
    {
        if (!IsTempDownload(oldPath)) return;
        if (IsTempDownload(newPath))
        {
            // Still downloading under its real name: carry the progress state over.
            lock (_sync)
            {
                if (_disposed) return;
                if (!_active.Remove(oldPath, out var st)) return;
                st.DisplayName = CleanName(Path.GetFileName(newPath));
                _active[newPath] = st;
            }
            return;
        }

        bool shouldAnnounce = false;
        string name = Path.GetFileName(newPath);
        lock (_sync)
        {
            if (_disposed) return;
            _active.Remove(oldPath);
            shouldAnnounce = _settings.Current.FeatureDownloads;
        }
        if (shouldAnnounce) AnnounceComplete(name);
    }

    internal void OnFileDone(string path, bool completed, Func<string, bool>? fileExists = null)
    {
        string name;
        lock (_sync)
        {
            if (_disposed) return;
            if (!_active.Remove(path, out var st)) return;
            name = st.DisplayName;
        }
        // Some tools copy to the final name and then delete the temp file: that is a completion too.
        string final = Path.Combine(Path.GetDirectoryName(path) ?? "", name);
        bool exists = fileExists is not null ? fileExists(final) : File.Exists(final);
        if (completed || exists) AnnounceComplete(name);
    }

    internal void AnnounceComplete(string name)
    {
        lock (_sync)
        {
            if (_disposed) return;
        }
        if (!_settings.Current.FeatureDownloads) return;

        _bus.Raise(BarEvent.Create(BarEventKind.Toast, "Download complete", name,
            payload: name, priority: 65, displayDuration: TimeSpan.FromSeconds(4)));
    }

    internal static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "…";
        if (bytesPerSec > 1024 * 1024) return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
        if (bytesPerSec > 1024) return $"{bytesPerSec / 1024:F0} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }

    public void Dispose()
    {
        FileSystemWatcher? watcherToDispose;
        System.Threading.Timer? timerToDispose;

        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;

            watcherToDispose = _watcher;
            _watcher = null;

            timerToDispose = _progressTimer;
            _progressTimer = null;

            _active.Clear();
        }

        CleanupWatcher(watcherToDispose);
        try { timerToDispose?.Dispose(); } catch { }
    }

    private void CleanupWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null) return;
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Changed -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Deleted -= OnDeleted;
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug("Error disposing download watcher", ("error", ex.Message));
        }
    }
}
