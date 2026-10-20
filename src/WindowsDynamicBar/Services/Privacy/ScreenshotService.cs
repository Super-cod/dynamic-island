using System.IO;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Services.Settings;

namespace WindowsDynamicBar.Services.Privacy;

internal interface IScreenshotWatcher : IDisposable
{
    event Action<string, string>? Created;
    void Start();
    void Stop();
}

internal sealed class FileSystemScreenshotWatcher : IScreenshotWatcher
{
    private readonly FileSystemWatcher _watcher;
    public event Action<string, string>? Created;

    public FileSystemScreenshotWatcher(string directoryPath)
    {
        _watcher = new FileSystemWatcher(directoryPath)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime
        };
        _watcher.Created += OnCreated;
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        Created?.Invoke(e.Name ?? Path.GetFileName(e.FullPath), e.FullPath);
    }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        try
        {
            _watcher.EnableRaisingEvents = false;
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        try
        {
            _watcher.Created -= OnCreated;
            _watcher.Dispose();
        }
        catch { }
    }
}

/// <summary>
/// Screenshot detection via the user's Pictures\Screenshots folder (Win+PrtScn default
/// location) using a FileSystemWatcher. LIMITATION: Snipping Tool "copy" mode and
/// third-party tools saving elsewhere are not detected.
/// </summary>
public sealed class ScreenshotService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private readonly Func<string, IScreenshotWatcher> _watcherFactory;
    private readonly Func<string?> _directoryResolver;
    private readonly Func<string, string, BarEvent> _eventFactory;
    private readonly object _sync = new();

    private IScreenshotWatcher? _watcher;
    private bool _started;
    private bool _disposed;

    public bool IsStarted
    {
        get { lock (_sync) return _started; }
    }

    public bool IsDisposed
    {
        get { lock (_sync) return _disposed; }
    }

    internal bool HasWatcher
    {
        get { lock (_sync) return _watcher is not null; }
    }

    public ScreenshotService(EventBus bus, SettingsService settings)
        : this(bus, settings, watcherFactory: null, directoryResolver: null, eventFactory: null)
    {
    }

    internal ScreenshotService(
        EventBus bus,
        SettingsService settings,
        Func<string, IScreenshotWatcher>? watcherFactory = null,
        Func<string?>? directoryResolver = null,
        Func<string, string, BarEvent>? eventFactory = null)
    {
        _bus = bus;
        _settings = settings;
        _watcherFactory = watcherFactory ?? (dir => new FileSystemScreenshotWatcher(dir));
        _directoryResolver = directoryResolver ?? ResolveDefaultDirectory;
        _eventFactory = eventFactory ?? CreateBarEvent;
    }

    internal static string? ResolveDefaultDirectory()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            return Directory.Exists(dir) ? dir : null;
        }
        catch (Exception ex)
        {
            Log.Debug("Failed resolving screenshot directory", ("error", ex.Message));
            return null;
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _started) return;
            _started = true;
        }

        IScreenshotWatcher? watcher = null;
        try
        {
            string? dir = _directoryResolver();
            if (string.IsNullOrWhiteSpace(dir))
            {
                lock (_sync)
                {
                    _started = false;
                }
                return;
            }

            lock (_sync)
            {
                if (_disposed) return;
            }

            watcher = _watcherFactory(dir);
            watcher.Created += OnScreenshotCreated;

            lock (_sync)
            {
                if (_disposed)
                {
                    CleanupWatcher(watcher);
                    return;
                }
            }

            watcher.Start();

            // Deferred publication pattern: assign _watcher only after watcher.Start()
            // succeeds and verify that disposal did not race with startup.
            lock (_sync)
            {
                if (_disposed)
                {
                    CleanupWatcher(watcher);
                    return;
                }
                _watcher = watcher;
            }

            Log.Info("Screenshot service started", ("dir", dir));
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _started = false;
                _watcher = null;
            }
            CleanupWatcher(watcher);
            Log.Debug("Screenshot watcher unavailable", ("error", ex.Message));
        }
    }

    private void OnScreenshotCreated(string name, string fullPath) => OnScreenshotCreatedCore(name, fullPath);

    internal void OnScreenshotCreatedCore(string name, string fullPath)
    {
        try
        {
            lock (_sync)
            {
                if (_disposed) return;
            }

            if (!_settings.Current.FeatureScreenshots) return;
            if (!IsScreenshotFile(name)) return;

            BarEvent barEvent = _eventFactory(name, fullPath);

            // Gated dispatch outside locks.
            // Publication guarantee: A check immediately before an external invocation
            // does not eliminate the race between that check and the invocation (if Dispose()
            // is called concurrently on another thread after the check passes). However,
            // this check guarantees that any callback reaching this point after Dispose() has
            // acquired the lock and set _disposed = true is suppressed from notifying or raising events.
            lock (_sync)
            {
                if (_disposed) return;
            }

            _bus.Raise(barEvent);
        }
        catch (Exception ex)
        {
            Log.Debug("Screenshot event error", ("error", ex.Message));
        }
    }

    internal static bool IsScreenshotFile(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return false;
        string ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp";
    }

    internal static BarEvent CreateBarEvent(string name, string fullPath)
    {
        return BarEvent.Create(BarEventKind.Screenshot, "Screenshot saved", name,
            payload: fullPath, priority: 60, displayDuration: TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        IScreenshotWatcher? watcherToDispose;

        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;

            watcherToDispose = _watcher;
            _watcher = null;
        }

        CleanupWatcher(watcherToDispose);
    }

    private void CleanupWatcher(IScreenshotWatcher? watcher)
    {
        if (watcher is null) return;
        try
        {
            watcher.Created -= OnScreenshotCreated;
            watcher.Stop();
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug("Error disposing screenshot watcher", ("error", ex.Message));
        }
    }
}
