using System.Diagnostics;
using System.IO;
using System.Threading;
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
    private int _buildGen;
    private byte[]? _prevTrackArt;          // artwork of the track before the current one
    private string _prevTrackAlbum = "";
    private string _lastAlbum = "";
    private DateTime _artPendingUntil;      // new track's artwork not confirmed yet
    private static readonly TimeSpan ArtSettleWindow = TimeSpan.FromSeconds(6);
    private int _noMediaToken;
    private static readonly TimeSpan SessionGapGrace = TimeSpan.FromSeconds(2.5);
    private byte[]? _dominantFor;          // artwork the cached dominant colour was computed from
    private Color _dominantCached;
    private MediaInfo? _lastEmitted;
    private string? _userSelectedAppId;
    private bool _started;
    private bool _disposed;
    private long _startGeneration;
    private bool _managerSubscribed;
    private readonly object _lifecycleLock = new();
    private Task? _startTask;
    private CancellationTokenSource? _startCts;

    /// <summary>Test seam: override SMTC manager request to test lifecycle transitions deterministically.</summary>
    internal static Func<CancellationToken, Task<GlobalSystemMediaTransportControlsSessionManager?>>? ManagerRequesterForTesting { get; set; }

    /// <summary>Test seam: invoked right before acquiring the lifecycle lock after manager acquisition to simulate races.</summary>
    internal static Action? BeforeStartupLockAcquiredForTesting { get; set; }

    /// <summary>Test seam: invoked when manager event handlers are subscribed.</summary>
    internal static Action? OnManagerSubscribedForTesting { get; set; }

    /// <summary>Test seam: invoked when manager event handlers are unsubscribed.</summary>
    internal static Action? OnManagerUnsubscribedForTesting { get; set; }

    /// <summary>Test seam: override DataReader.LoadAsync to test pending read operations and deferred cleanup deterministically.</summary>
    internal static Func<DataReader, IRandomAccessStreamWithContentType, uint, Windows.Foundation.IAsyncOperation<uint>>? LoadOperationProviderForTesting { get; set; }

    internal void SetStartedForTesting(bool started)
    {
        lock (_lifecycleLock)
        {
            _started = started;
        }
    }

    internal bool IsDisposedForTesting
    {
        get
        {
            lock (_lifecycleLock) return _disposed;
        }
    }

    internal bool HasManagerForTesting
    {
        get
        {
            lock (_lifecycleLock) return _manager is not null;
        }
    }

    internal bool HasManagerSubscriptionsForTesting
    {
        get
        {
            lock (_lifecycleLock) return _managerSubscribed;
        }
    }

    public event Action<MediaInfo>? MediaChanged;

    public MediaInfo? Current
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _disposed ? null : _lastEmitted;
            }
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_lifecycleLock)
            {
                if (!_started || _disposed) return false;
            }
            if (_lastEmitted is { IsPlaying: true }) return true;
            try
            {
                var cur = _current;
                if (cur?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    return true;
                var sessions = GetSessions();
                foreach (var s in sessions)
                {
                    try
                    {
                        if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }
    }

    public string? CurrentAppId => _current?.SourceAppUserModelId;

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _started && !_disposed;
            }
        }
    }

    /// <summary>Music feature switched off: drop the session hooks and the poll.</summary>
    public void Stop()
    {
        CancellationTokenSource? ctsToCancel;
        GlobalSystemMediaTransportControlsSessionManager? mgr;
        lock (_lifecycleLock)
        {
            if (_disposed) return;

            // Invalidate in-flight startup generation so any returning StartCoreAsync aborts immediately
            _startGeneration++;

            ctsToCancel = _startCts;
            _startCts = null;
            _startTask = null;
            mgr = _manager;
            _manager = null;
            bool wasStarted = _started;
            _started = false;

            if (mgr is not null)
            {
                try
                {
                    mgr.SessionsChanged -= OnSessionsChanged;
                    mgr.CurrentSessionChanged -= OnCurrentSessionChanged;
                    _managerSubscribed = false;
                    OnManagerUnsubscribedForTesting?.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Debug("Error unhooking SMTC manager", ("error", ex.Message));
                }
            }

            HookSession(null, immediate: true, suppressEvent: false);
        }

        ctsToCancel?.Cancel();
        ctsToCancel?.Dispose();
    }

    public MediaService(EventBus bus) => _bus = bus;

    public Task StartAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _started) return Task.CompletedTask;
            if (_startTask is not null) return _startTask;

            long generation = ++_startGeneration;
            _startCts = new CancellationTokenSource();
            var task = StartCoreAsync(generation, _startCts);
            if (generation == _startGeneration && !task.IsCompleted)
            {
                _startTask = task;
            }
            return task;
        }
    }

    private async Task StartCoreAsync(long generation, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            GlobalSystemMediaTransportControlsSessionManager? manager = null;
            try
            {
                if (ManagerRequesterForTesting is not null)
                {
                    manager = await ManagerRequesterForTesting(token);
                }
                else
                {
                    manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning("SMTC unavailable", ("error", ex.Message));
                return;
            }

            BeforeStartupLockAcquiredForTesting?.Invoke();

            lock (_lifecycleLock)
            {
                if (_disposed || generation != _startGeneration || token.IsCancellationRequested)
                {
                    return;
                }

                if (manager is null)
                {
                    Log.Warning("SMTC unavailable (null manager)");
                    return;
                }

                _manager = manager;
                try
                {
                    _manager.SessionsChanged += OnSessionsChanged;
                    _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                    _managerSubscribed = true;
                    OnManagerSubscribedForTesting?.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Warning("Failed to subscribe to SMTC manager events", ("error", ex.Message));
                }

                HookSession(PickBestSession());
                _started = true;
                Log.Info("Media service started");
            }
        }
        finally
        {
            CancellationTokenSource? ctsToDispose = null;
            lock (_lifecycleLock)
            {
                if (generation == _startGeneration)
                {
                    _startTask = null;
                    ctsToDispose = _startCts;
                    _startCts = null;
                }
            }
            ctsToDispose?.Dispose();
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        lock (_lifecycleLock)
        {
            if (!_started || _disposed) return;
        }
        HookSession(PickBestSession());
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

        var playing = sessions.FirstOrDefault(s =>
        {
            try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        });
        if (playing is not null) return playing;

        try { return _manager.GetCurrentSession() ?? sessions.FirstOrDefault(); }
        catch { return sessions.FirstOrDefault(); }
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
        lock (_lifecycleLock)
        {
            if (!_started || _disposed) return;
        }
        HookSession(PickBestSession());
    }

    private void HookSession(GlobalSystemMediaTransportControlsSession? session, bool immediate = false, bool suppressEvent = false)
    {
        if (_current is not null)
        {
            try
            {
                _current.MediaPropertiesChanged -= OnMediaProperties;
                _current.PlaybackInfoChanged -= OnPlaybackInfo;
                _current.TimelinePropertiesChanged -= OnTimeline;
            }
            catch (Exception ex)
            {
                Log.Debug("Error unhooking session", ("error", ex.Message));
            }
        }
        _current = session;
        int token = Interlocked.Increment(ref _noMediaToken);   // a new session cancels a pending "no media"
        if (_current is null)
        {
            _timelineTimer?.Stop();
            if (suppressEvent) return;
            if (immediate || _lastEmitted is null || string.IsNullOrEmpty(_lastEmitted.AppId)) { ClearToNoMedia(); return; }
            // Chrome drops and recreates its media session between YouTube videos. Keep the current
            // track (and the previous-artwork memory) through that gap; only a lasting gap is "no media".
            MediaTrace("session gone, waiting");
            _ = Task.Delay(SessionGapGrace).ContinueWith(_ =>
            {
                if (token == Volatile.Read(ref _noMediaToken) && _current is null && !_disposed) ClearToNoMedia();
            }, TaskScheduler.Default);
            return;
        }
        _current.MediaPropertiesChanged += OnMediaProperties;
        _current.PlaybackInfoChanged += OnPlaybackInfo;
        _current.TimelinePropertiesChanged += OnTimeline;
        _ = RefreshAsync(showEvent: true);
        StartTimelineTimer();
    }

    private void ClearToNoMedia()
    {
        _lastKey = null;
        _lastArtwork = null;
        _lastArtworkKey = null;
        _artPendingUntil = DateTime.MinValue;
        _dominantFor = null;
        Emit(new MediaInfo("", "", "", "", false, TimeSpan.Zero, TimeSpan.Zero, null, 0));
    }

    private async void OnMediaProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e)
    {
        if (_disposed || !_started) return;
        await RefreshAsync(showEvent: true);
    }

    private async void OnPlaybackInfo(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
    {
        if (_disposed || !_started) return;
        await RefreshAsync(showEvent: false);
    }

    private async void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e)
    {
        if (_disposed || !_started) return;
        await RefreshAsync(showEvent: false);
    }

    private void StartTimelineTimer()
    {
        // Safety-net poll for apps that do not raise timeline/playback events (e.g. browsers).
        // The UI extrapolates the position itself, so only real changes are forwarded.
        if (_disposed) return;
        _timelineTimer ??= new DispatcherTimerRef();
        _timelineTimer.Start(TimeSpan.FromSeconds(1), async () =>
        {
            try
            {
                if (_disposed || !_started) return;
                if (_manager is not null)
                {
                    var best = PickBestSession();
                    if (best is not null && best.SourceAppUserModelId != _current?.SourceAppUserModelId)
                    {
                        try
                        {
                            if (best.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            {
                                HookSession(best);
                                return;
                            }
                        }
                        catch { }
                    }
                }
                if (_current is null || _disposed || !_started) return;
                var info = await BuildInfoAsync();
                if (info is null || _disposed || !IsMeaningfulChange(_lastEmitted, info)) return;
                Emit(info);
            }
            catch (Exception ex)
            {
                Log.Debug("Timeline poll error", ("error", ex.Message));
            }
        });
    }

    public async Task RefreshAsync(bool showEvent, bool force = false)
    {
        if (_disposed) return;
        try
        {
            var info = await BuildInfoAsync();
            if (info is null || _disposed) return;
            Emit(info);

            string key = $"{info.Title}|{info.Artist}|{info.AppId}";
            bool trackChanged = key != _lastKey;
            _lastKey = key;
            if (showEvent && (trackChanged || force) && info.IsPlaying && !_disposed)
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
        var session = _current;
        if (session is null) return null;
        // Events and the 1 s poll overlap; only the newest build may emit, so an older in-flight
        // read can never put the previous track's metadata or artwork back on screen.
        int gen = Interlocked.Increment(ref _buildGen);
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (gen != Volatile.Read(ref _buildGen) || !ReferenceEquals(session, _current)) return null;

            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var identity = AppIconHelper.ResolveAppIdentity(session.SourceAppUserModelId);

            byte[]? art = _lastArtwork;
            string artKey = $"{props.Title}|{props.Artist}|{session.SourceAppUserModelId}";
            bool keyChanged = artKey != _lastArtworkKey;
            if (keyChanged)
            {
                // Browsers (YouTube) publish the new title before the new thumbnail: for a few
                // seconds the previous track's artwork is treated as "not available yet".
                _prevTrackArt = _lastArtwork ?? _prevTrackArt;
                _prevTrackAlbum = _lastAlbum;
                _artPendingUntil = DateTime.UtcNow + ArtSettleWindow;
            }
            bool pending = DateTime.UtcNow < _artPendingUntil;
            if (keyChanged || art is null || pending)
            {
                // Thumbnail from the SAME properties snapshot as the title (no second, newer read).
                var fetched = await TryGetArtworkAsync(props);
                if (gen != Volatile.Read(ref _buildGen)) return null;
                if (fetched is not null && AppIconHelper.IsArtworkTinyOrAppIcon(fetched, identity.IconBytes, 64))
                {
                    // Do not show tiny artwork or app icon on disc
                    fetched = null;
                }

                bool sameAlbum = !string.IsNullOrEmpty(props.AlbumTitle) && props.AlbumTitle == _prevTrackAlbum;
                if (fetched is not null && pending && !sameAlbum && SameBytes(fetched, _prevTrackArt))
                    fetched = null;                               // still the old track's thumbnail
                else if (fetched is not null)
                    _artPendingUntil = DateTime.MinValue;         // new artwork confirmed
                if (fetched is not null && SameBytes(fetched, _lastArtwork))
                    fetched = _lastArtwork;                       // unchanged: keep the reference (no UI churn)

                if (fetched is not null || keyChanged)
                {
                    if (!keyChanged && !ReferenceEquals(fetched, _lastArtwork)) MediaTrace("artwork arrived", ("title", props.Title));
                    _lastArtwork = fetched;
                    _lastArtworkKey = artKey;
                }
                art = _lastArtwork;
            }
            _lastAlbum = props.AlbumTitle ?? "";
            MediaTrace(keyChanged ? "track changed" : "build", ("title", props.Title), ("playing", playing),
                ("art", art is null ? "none" : Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(art))[..8]),
                ("pending", pending));

            var duration = timeline.EndTime - timeline.StartTime;
            var position = timeline.Position - timeline.StartTime;
            double progress = duration.TotalSeconds > 0
                ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1) : 0;

            bool canSeek = playback?.Controls?.IsPlaybackPositionEnabled ?? true;

            Color dominant = identity.DominantColor;
            if (art is not null)
            {
                // Decode once per artwork, not on every poll.
                if (!ReferenceEquals(art, _dominantFor))
                {
                    var artBmp = AppIconHelper.BytesToBitmap(art);
                    _dominantCached = artBmp is not null ? AppIconHelper.ExtractDominantColor(artBmp) : identity.DominantColor;
                    _dominantFor = art;
                }
                dominant = _dominantCached;
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

    private void Emit(MediaInfo info)
    {
        _lastEmitted = info;
        if (_disposed) return;
        MediaChanged?.Invoke(info);
    }

    /// <summary>True when the new snapshot differs in anything the UI shows. Position is
    /// extrapolated by the UI, so it only counts when it jumps (seek) by more than 1.5 s.</summary>
    internal static bool IsMeaningfulChange(MediaInfo? prev, MediaInfo next)
    {
        if (prev is null) return true;
        if (prev.Title != next.Title || prev.Artist != next.Artist || prev.Album != next.Album ||
            prev.AppId != next.AppId || prev.IsPlaying != next.IsPlaying || prev.Duration != next.Duration ||
            prev.CanSeek != next.CanSeek || !ReferenceEquals(prev.ArtworkBytes, next.ArtworkBytes))
            return true;
        TimeSpan expected = prev.Position;
        if (prev.IsPlaying && prev.LastUpdatedTime is { } lu && next.LastUpdatedTime is { } nu)
            expected += nu - lu;
        return Math.Abs((next.Position - expected).TotalSeconds) > 1.5;
    }

    private static bool SameBytes(byte[]? a, byte[]? b) =>
        a is not null && b is not null && (ReferenceEquals(a, b) || a.AsSpan().SequenceEqual(b));

    [Conditional("DEBUG")]
    private static void MediaTrace(string evt, params (string Key, object? Value)[] props) =>
        Log.Info("[media-trace] " + evt, props);

    internal static readonly TimeSpan ArtworkTimeout = TimeSpan.FromSeconds(2.5);

    private static async Task<byte[]?> TryGetArtworkAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        return await TryGetArtworkAsync(props.Thumbnail, ArtworkTimeout);
    }

    /// <summary>
    /// Tracks actual WinRT <see cref="Windows.Foundation.IAsyncOperation{TResult}"/> lifecycle and completion,
    /// decoupling timeout and cancellation from resource disposal.
    /// In WinRT, calling <see cref="Windows.Foundation.IAsyncInfo.Cancel"/> is an advisory request;
    /// the underlying operation continues executing until its <see cref="Windows.Foundation.IAsyncOperation{TResult}.Completed"/>
    /// handler is invoked. Resources (such as streams and readers) must never be disposed while
    /// an operation is still in-flight.
    /// </summary>
    private sealed class WinRtAsyncTracker<TResult>
    {
        private const int StatePending = 0;
        private const int StatePrompt = 1;
        private const int StateTimedOut = 2;

        private int _state = StatePending;
        private readonly TaskCompletionSource<TResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TResult> Task => _tcs.Task;

        public WinRtAsyncTracker(
            Windows.Foundation.IAsyncOperation<TResult> op,
            Action<TResult>? onLateSuccess,
            Action? onTerminalCleanup)
        {
            try
            {
                op.Completed = (info, status) =>
                {
                    try
                    {
                        if (status == Windows.Foundation.AsyncStatus.Completed)
                        {
                            TResult? result = default;
                            try { result = info.GetResults(); }
                            catch (Exception ex)
                            {
                                if (Interlocked.CompareExchange(ref _state, StatePrompt, StatePending) == StatePending)
                                {
                                    _tcs.TrySetException(ex);
                                }
                                onTerminalCleanup?.Invoke();
                                return;
                            }

                            if (Interlocked.CompareExchange(ref _state, StatePrompt, StatePending) == StatePending)
                            {
                                _tcs.TrySetResult(result!);
                            }
                            else
                            {
                                // Operation finished after timeout expired.
                                // The caller has already returned; safely dispose/clean up late resources now that
                                // the native operation has actually completed.
                                if (result is not null && onLateSuccess is not null)
                                {
                                    try { onLateSuccess(result); } catch { }
                                }
                                onTerminalCleanup?.Invoke();
                            }
                        }
                        else if (status == Windows.Foundation.AsyncStatus.Canceled)
                        {
                            if (Interlocked.CompareExchange(ref _state, StatePrompt, StatePending) == StatePending)
                            {
                                _tcs.TrySetCanceled();
                            }
                            else
                            {
                                onTerminalCleanup?.Invoke();
                            }
                        }
                        else // AsyncStatus.Error
                        {
                            Exception? ex = null;
                            try { ex = info.ErrorCode; } catch { }
                            ex ??= new Exception("WinRT async operation failed");

                            if (Interlocked.CompareExchange(ref _state, StatePrompt, StatePending) == StatePending)
                            {
                                _tcs.TrySetException(ex);
                            }
                            else
                            {
                                onTerminalCleanup?.Invoke();
                            }
                        }
                    }
                    catch
                    {
                        onTerminalCleanup?.Invoke();
                    }
                };
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }

        public bool MarkTimedOut() => Interlocked.CompareExchange(ref _state, StateTimedOut, StatePending) == StatePending;
    }

    internal static async Task<byte[]?> TryGetArtworkAsync(IRandomAccessStreamReference? thumb, TimeSpan timeout)
    {
        if (thumb is null || timeout <= TimeSpan.Zero) return null;

        var sw = Stopwatch.StartNew();

        Windows.Foundation.IAsyncOperation<IRandomAccessStreamWithContentType> openOp;
        try
        {
            openOp = thumb.OpenReadAsync();
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to initiate thumbnail open", ("error", ex.Message));
            return null;
        }

        var openTracker = new WinRtAsyncTracker<IRandomAccessStreamWithContentType>(
            openOp,
            onLateSuccess: lateStream =>
            {
                try { lateStream.Dispose(); } catch { }
            },
            onTerminalCleanup: null);

        var completedOpen = await Task.WhenAny(openTracker.Task, Task.Delay(timeout));
        if (completedOpen != openTracker.Task)
        {
            if (openTracker.MarkTimedOut())
            {
                // OpenReadAsync remained pending beyond the deadline.
                // Request cancellation on the WinRT operation, but do NOT dispose any late stream
                // until openOp actually reaches a terminal state (handled in openTracker.onLateSuccess).
                try { openOp.Cancel(); } catch { }
                Log.Debug("Artwork open timed out");
                return null;
            }
        }

        IRandomAccessStreamWithContentType? stream;
        try
        {
            stream = await openTracker.Task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug("Artwork open failed", ("error", ex.Message));
            return null;
        }

        if (stream is null) return null;

        TimeSpan remaining = timeout - sw.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            try { stream.Dispose(); } catch { }
            return null;
        }

        DataReader? reader = null;
        try
        {
            reader = new DataReader(stream);
            Windows.Foundation.IAsyncOperation<uint> loadOp;
            if (LoadOperationProviderForTesting is not null)
            {
                loadOp = LoadOperationProviderForTesting(reader, stream, 16u * 1024 * 1024);
            }
            else
            {
                loadOp = reader.LoadAsync(16u * 1024 * 1024);
            }

            var loadTracker = new WinRtAsyncTracker<uint>(
                loadOp,
                onLateSuccess: null,
                onTerminalCleanup: () =>
                {
                    try { reader.Dispose(); } catch { }
                    try { stream.Dispose(); } catch { }
                });

            var completedLoad = await Task.WhenAny(loadTracker.Task, Task.Delay(remaining));
            if (completedLoad != loadTracker.Task)
            {
                if (loadTracker.MarkTimedOut())
                {
                    // LoadAsync remained pending beyond the deadline.
                    // Request cancellation on the WinRT operation, but DO NOT dispose reader or stream here.
                    // Disposal will occur deferred via loadTracker's onTerminalCleanup once the underlying
                    // WinRT operation actually reaches a terminal state.
                    // Note: If the underlying native driver hangs permanently and never signals completion,
                    // reader and stream intentionally remain alive to avoid use-after-free memory corruption
                    // crashes in the native COM apartment.
                    try { loadOp.Cancel(); } catch { }
                    Log.Debug("Artwork load timed out");
                    return null;
                }
            }

            uint len;
            try
            {
                len = await loadTracker.Task;
            }
            catch (OperationCanceledException)
            {
                reader.Dispose();
                stream.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                Log.Debug("Artwork load failed", ("error", ex.Message));
                reader.Dispose();
                stream.Dispose();
                return null;
            }

            if (len == 0)
            {
                reader.Dispose();
                stream.Dispose();
                return null;
            }

            var bytes = new byte[len];
            reader.ReadBytes(bytes);
            reader.Dispose();
            stream.Dispose();
            return bytes.Length > 0 ? bytes : null;
        }
        catch (Exception ex)
        {
            Log.Debug("Artwork load setup failed", ("error", ex.Message));
            reader?.Dispose();
            stream.Dispose();
            return null;
        }
    }

    // ---- transport controls ----

    public async Task PlayPauseAsync()
    {
        if (_disposed) return;
        try { if (_current is not null) await _current.TryTogglePlayPauseAsync(); }
        catch (Exception ex) { Log.Debug("PlayPause", ("error", ex.Message)); }
    }

    public async Task NextAsync()
    {
        if (_disposed) return;
        try { if (_current is not null) await _current.TrySkipNextAsync(); }
        catch (Exception ex) { Log.Debug("Next", ("error", ex.Message)); }
    }

    public async Task PreviousAsync()
    {
        if (_disposed) return;
        try { if (_current is not null) await _current.TrySkipPreviousAsync(); }
        catch (Exception ex) { Log.Debug("Prev", ("error", ex.Message)); }
    }

    public async Task SeekAsync(double fraction)
    {
        if (_disposed) return;
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
        if (_disposed) return;
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
        CancellationTokenSource? ctsToCancel;
        GlobalSystemMediaTransportControlsSessionManager? mgr;
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;
            _startGeneration++;
            ctsToCancel = _startCts;
            _startCts = null;
            _startTask = null;
            mgr = _manager;
            _manager = null;

            if (mgr is not null)
            {
                try
                {
                    mgr.SessionsChanged -= OnSessionsChanged;
                    mgr.CurrentSessionChanged -= OnCurrentSessionChanged;
                    _managerSubscribed = false;
                    OnManagerUnsubscribedForTesting?.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Debug("Error unhooking SMTC manager", ("error", ex.Message));
                }
            }

            HookSession(null, immediate: true, suppressEvent: true);
        }

        ctsToCancel?.Cancel();
        ctsToCancel?.Dispose();

        _timelineTimer?.Dispose();
        _timelineTimer = null;
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
                    if (_disposed || _callback is null) return;
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
