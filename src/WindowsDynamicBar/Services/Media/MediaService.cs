using System.Diagnostics;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Services.Media;

public sealed record MediaInfo(
    string Title,
    string Artist,
    string Album,
    string AppId,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    byte[]? ArtworkBytes,
    double Progress);

public sealed class MediaService : IDisposable
{
    private readonly EventBus _bus;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _current;
    private DispatcherTimerRef? _timelineTimer;
    private byte[]? _lastArtwork;
    private string? _lastKey;
    private bool _started;

    public event Action<MediaInfo>? MediaChanged;

    public MediaService(EventBus bus) => _bus = bus;

    public async Task StartAsync()
    {
        if (_started) return;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
            HookSession(_manager.GetCurrentSession());
            _started = true;
            Log.Info("Media service started");
        }
        catch (Exception ex)
        {
            Log.Warning("SMTC unavailable", ("error", ex.Message));
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        HookSession(_manager?.GetCurrentSession());
    }

    private void HookSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_current is not null)
        {
            _current.MediaPropertiesChanged -= OnMediaProperties;
            _current.PlaybackInfoChanged -= OnPlaybackInfo;
            _current.TimelinePropertiesChanged -= OnTimeline;
        }
        _current = session;
        if (_current is null)
        {
            _timelineTimer?.Stop();
            _lastKey = null;
            return;
        }
        _current.MediaPropertiesChanged += OnMediaProperties;
        _current.PlaybackInfoChanged += OnPlaybackInfo;
        _current.TimelinePropertiesChanged += OnTimeline;
        _ = RefreshAsync(showEvent: true);
        StartTimelineTimer();
    }

    private async void OnMediaProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e)
        => await RefreshAsync(showEvent: true);

    private async void OnPlaybackInfo(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
        => await RefreshAsync(showEvent: true);

    private async void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e)
        => await RefreshAsync(showEvent: false);

    private void StartTimelineTimer()
    {
        _timelineTimer ??= new DispatcherTimerRef();
        _timelineTimer.Start(TimeSpan.FromMilliseconds(1000), async () =>
        {
            if (_current is null) return;
            var info = await BuildInfoAsync();
            if (info is null) return;
            if (info.IsPlaying) MediaChanged?.Invoke(info);
        });
    }

    public async Task RefreshAsync(bool showEvent)
    {
        try
        {
            var info = await BuildInfoAsync();
            if (info is null) return;
            MediaChanged?.Invoke(info);

            string key = $"{info.Title}|{info.AppId}";
            if (showEvent && key != _lastKey)
            {
                _lastKey = key;
                _bus.Raise(BarEvent.Create(BarEventKind.Media, info.Title,
                    info.Artist, payload: info,
                    displayDuration: null, priority: 60));
            }
        }
        catch (Exception ex) { Log.Debug("Media refresh failed", ("error", ex.Message)); }
    }

    private async Task<MediaInfo?> BuildInfoAsync()
    {
        if (_current is null) return null;
        try
        {
            var props = await _current.TryGetMediaPropertiesAsync();
            var playback = _current.GetPlaybackInfo();
            var timeline = _current.GetTimelineProperties();
            bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            // Artwork: re-fetch only when title/app changes (cheap on repeat).
            byte[]? art = _lastArtwork;
            string artKey = $"{props.Title}|{_current.SourceAppUserModelId}";
            if (artKey != _lastArtworkKey)
            {
                art = await TryGetArtworkAsync();
                _lastArtwork = art;
                _lastArtworkKey = artKey;
            }

            var duration = timeline.EndTime - timeline.StartTime;
            var position = timeline.Position - timeline.StartTime;
            double progress = duration.TotalSeconds > 0
                ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1) : 0;

            return new MediaInfo(
                string.IsNullOrWhiteSpace(props.Title) ? "Unknown" : props.Title,
                props.Artist ?? "",
                props.AlbumTitle ?? "",
                _current.SourceAppUserModelId ?? "",
                playing,
                position, duration, art, progress);
        }
        catch (Exception ex)
        {
            Log.Debug("BuildInfo failed", ("error", ex.Message));
            return null;
        }
    }

    private string? _lastArtworkKey;

    private async Task<byte[]?> TryGetArtworkAsync()
    {
        try
        {
            var props = await _current!.TryGetMediaPropertiesAsync();
            var thumb = props.Thumbnail;
            if (thumb is null) return null;
            using var stream = await thumb.OpenReadAsync();
            using var reader = new DataReader(stream);
            uint len = await reader.LoadAsync(16u * 1024 * 1024);
            var bytes = new byte[len];
            reader.ReadBytes(bytes);
            return bytes.Length > 0 ? bytes : null;
        }
        catch { return null; }
    }

    // ---- transport controls ----

    public async Task PlayPauseAsync()
    {
        try { _current?.TryTogglePlayPauseAsync(); } catch (Exception ex) { Log.Debug("PlayPause", ("error", ex.Message)); }
        await Task.CompletedTask;
    }

    public async Task NextAsync()
    {
        try { _current?.TrySkipNextAsync(); } catch (Exception ex) { Log.Debug("Next", ("error", ex.Message)); }
        await Task.CompletedTask;
    }

    public async Task PreviousAsync()
    {
        try { _current?.TrySkipPreviousAsync(); } catch (Exception ex) { Log.Debug("Prev", ("error", ex.Message)); }
        await Task.CompletedTask;
    }

    public async Task SeekAsync(double fraction)
    {
        try
        {
            if (_current is null) return;
            var timeline = _current.GetTimelineProperties();
            var duration = timeline.EndTime - timeline.StartTime;
            if (duration <= TimeSpan.Zero) return;
            var target = timeline.StartTime + TimeSpan.FromSeconds(duration.TotalSeconds * Math.Clamp(fraction, 0, 1));
            await _current.TryChangePlaybackPositionAsync(target.Ticks);
        }
        catch (Exception ex) { Log.Debug("Seek", ("error", ex.Message)); }
    }

    public void Dispose()
    {
        _timelineTimer?.Dispose();
        if (_current is not null)
        {
            _current.MediaPropertiesChanged -= OnMediaProperties;
            _current.PlaybackInfoChanged -= OnPlaybackInfo;
            _current.TimelinePropertiesChanged -= OnTimeline;
        }
        if (_manager is not null) _manager.SessionsChanged -= OnSessionsChanged;
    }
}

/// <summary>Minimal async-safe dispatcher timer wrapper.</summary>
internal sealed class DispatcherTimerRef : IDisposable
{
    private System.Windows.Threading.DispatcherTimer? _timer;
    private Func<Task>? _callback;
    private bool _disposed;

    public void Start(TimeSpan interval, Func<Task> callback)
    {
        if (_disposed) return;
        _callback = callback;
        if (_timer is null)
        {
            _timer = new System.Windows.Threading.DispatcherTimer(interval,
                System.Windows.Threading.DispatcherPriority.Background,
                async (s, e) =>
                {
                    if (_callback is null) return;
                    try { await _callback(); } catch { }
                },
                System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher);
        }
        _timer.Interval = interval;
        _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_timer is not null) { _timer.Stop(); _timer = null; }
        _callback = null;
    }
}
