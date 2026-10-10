using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Media;

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
    double Progress,
    string SourceAppName = "",
    byte[]? SourceAppIconBytes = null,
    bool CanSeek = true,
    DateTimeOffset? LastUpdatedTime = null,
    Color? DominantColor = null);

public sealed class MediaService : IDisposable
{
    private readonly EventBus _bus;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _current;
    private DispatcherTimerRef? _timelineTimer;
    private byte[]? _lastArtwork;
    private string? _lastKey;
    private string? _lastArtworkKey;
    private string? _userSelectedAppId;
    private bool _started;

    public event Action<MediaInfo>? MediaChanged;

    public string? CurrentAppId => _current?.SourceAppUserModelId;

    public MediaService(EventBus bus) => _bus = bus;

    public async Task StartAsync()
    {
        if (_started) return;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
            HookSession(PickBestSession());
            _started = true;
            Log.Info("Media service started");
        }
        catch (Exception ex)
        {
            Log.Warning("SMTC unavailable", ("error", ex.Message));
        }
    }

    public IReadOnlyList<GlobalSystemMediaTransportControlsSession> GetSessions()
    {
        if (_manager is null) return Array.Empty<GlobalSystemMediaTransportControlsSession>();
        try { return _manager.GetSessions(); }
        catch { return Array.Empty<GlobalSystemMediaTransportControlsSession>(); }
    }

    private GlobalSystemMediaTransportControlsSession? PickBestSession()
    {
        if (_manager is null) return null;
        var sessions = GetSessions();
        if (sessions.Count == 0) return null;

        if (!string.IsNullOrEmpty(_userSelectedAppId))
        {
            var matched = sessions.FirstOrDefault(s => s.SourceAppUserModelId == _userSelectedAppId);
            if (matched is not null) return matched;
        }

        var playing = sessions.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        if (playing is not null) return playing;

        return _manager.GetCurrentSession() ?? sessions.FirstOrDefault();
    }

    public void CycleSession()
    {
        if (_manager is null) return;
        var sessions = GetSessions();
        if (sessions.Count <= 1) return;

        int idx = -1;
        if (_current is not null)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].SourceAppUserModelId == _current.SourceAppUserModelId)
                {
                    idx = i;
                    break;
                }
            }
        }

        int nextIdx = (idx + 1) % sessions.Count;
        var nextSession = sessions[nextIdx];
        _userSelectedAppId = nextSession.SourceAppUserModelId;
        HookSession(nextSession);
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        HookSession(PickBestSession());
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
            _lastArtwork = null;
            _lastArtworkKey = null;
            MediaChanged?.Invoke(new MediaInfo("", "", "", "", false, TimeSpan.Zero, TimeSpan.Zero, null, 0));
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
        => await RefreshAsync(showEvent: false);

    private async void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e)
        => await RefreshAsync(showEvent: false);

    private void StartTimelineTimer()
    {
        _timelineTimer ??= new DispatcherTimerRef();
        _timelineTimer.Start(TimeSpan.FromMilliseconds(250), async () =>
        {
            if (_current is null) return;
            var info = await BuildInfoAsync();
            if (info is null) return;
            MediaChanged?.Invoke(info);  // Fire on ALL state changes (playing and paused)
        });
    }

    public async Task RefreshAsync(bool showEvent, bool force = false)
    {
        try
        {
            var info = await BuildInfoAsync();
            if (info is null) return;
            MediaChanged?.Invoke(info);

            string key = $"{info.Title}|{info.Artist}|{info.AppId}";
            bool trackChanged = key != _lastKey;
            _lastKey = key;
            if (showEvent && (trackChanged || force) && info.IsPlaying)
            {
                _bus.Raise(BarEvent.Create(BarEventKind.Media, info.Title,
                    info.Artist, payload: info,
                    displayDuration: TimeSpan.FromSeconds(4.5), priority: 60));
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

            var identity = AppIconHelper.ResolveAppIdentity(_current.SourceAppUserModelId);

            byte[]? art = _lastArtwork;
            string artKey = $"{props.Title}|{props.Artist}|{_current.SourceAppUserModelId}";
            bool keyChanged = artKey != _lastArtworkKey;
            if (keyChanged || art is null)
            {
                var fetched = await TryGetArtworkAsync();
                if (fetched is not null && AppIconHelper.IsArtworkTinyOrAppIcon(fetched, identity.IconBytes, 64))
                {
                    // Do not show tiny artwork or app icon on disc
                    fetched = null;
                }

                if (fetched is not null || keyChanged)
                {
                    _lastArtwork = fetched;
                    _lastArtworkKey = artKey;
                }
                art = _lastArtwork;
            }

            var duration = timeline.EndTime - timeline.StartTime;
            var position = timeline.Position - timeline.StartTime;
            double progress = duration.TotalSeconds > 0
                ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1) : 0;

            bool canSeek = playback.Controls?.IsPlaybackPositionEnabled ?? true;

            Color dominant = identity.DominantColor;
            if (art is not null)
            {
                var artBmp = AppIconHelper.BytesToBitmap(art);
                if (artBmp is not null)
                    dominant = AppIconHelper.ExtractDominantColor(artBmp);
            }

            return new MediaInfo(
                string.IsNullOrWhiteSpace(props.Title) ? "Unknown" : props.Title,
                props.Artist ?? "",
                props.AlbumTitle ?? "",
                _current.SourceAppUserModelId ?? "",
                playing,
                position, duration, art, progress,
                identity.DisplayName,
                identity.IconBytes,
                canSeek,
                timeline.LastUpdatedTime,
                dominant);
        }
        catch (Exception ex)
        {
            Log.Debug("BuildInfo failed", ("error", ex.Message));
            return null;
        }
    }

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
        try { if (_current is not null) await _current.TryTogglePlayPauseAsync(); }
        catch (Exception ex) { Log.Debug("PlayPause", ("error", ex.Message)); }
    }

    public async Task NextAsync()
    {
        try { if (_current is not null) await _current.TrySkipNextAsync(); }
        catch (Exception ex) { Log.Debug("Next", ("error", ex.Message)); }
    }

    public async Task PreviousAsync()
    {
        try { if (_current is not null) await _current.TrySkipPreviousAsync(); }
        catch (Exception ex) { Log.Debug("Prev", ("error", ex.Message)); }
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

    public async Task SeekToTimeAsync(TimeSpan position)
    {
        try
        {
            if (_current is null) return;
            var timeline = _current.GetTimelineProperties();
            var target = timeline.StartTime + position;
            await _current.TryChangePlaybackPositionAsync(target.Ticks);
        }
        catch (Exception ex) { Log.Debug("SeekToTime", ("error", ex.Message)); }
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
