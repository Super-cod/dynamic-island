using System.IO;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.Services.Privacy;

/// <summary>
/// Screenshot detection via the user's Pictures\Screenshots folder (Win+PrtScn default
/// location) using a FileSystemWatcher. LIMITATION: Snipping Tool "copy" mode and
/// third-party tools saving elsewhere are not detected.
/// </summary>
public sealed class ScreenshotService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private FileSystemWatcher? _watcher;

    public ScreenshotService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public void Start()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            if (!Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime };
            _watcher.Created += (s, e) =>
            {
                if (!_settings.Current.FeatureScreenshots) return;
                string ext = Path.GetExtension(e.Name ?? "").ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp")) return;
                _bus.Raise(BarEvent.Create(BarEventKind.Screenshot, "Screenshot saved", e.Name,
                    payload: e.FullPath, priority: 60, displayDuration: TimeSpan.FromSeconds(3)));
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Debug("Screenshot watcher unavailable", ("error", ex.Message)); }
    }

    public void Dispose() { try { _watcher?.Dispose(); } catch { } }
}
