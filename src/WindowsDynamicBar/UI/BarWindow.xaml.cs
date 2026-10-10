using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Animation;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Idle;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Media;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Core.Timers;
using WindowsDynamicBar.Interop;
using WindowsDynamicBar.Services.Audio;
using WindowsDynamicBar.Services.Calendar;
using WindowsDynamicBar.Services.Media;
using WindowsDynamicBar.Services.Privacy;
using WindowsDynamicBar.Services.Search;
using WindowsDynamicBar.Services.SystemInfo;
using WindowsDynamicBar.Services.SystemMonitor;
using WindowsDynamicBar.Services.Timers;
using WindowsDynamicBar.Services.Toggles;
using WindowsDynamicBar.Services.Weather;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using WindowsDynamicBar.Core.Search;
using System.Runtime.InteropServices;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace WindowsDynamicBar.UI;

public partial class BarWindow : Window, IDisposable
{
    // ---- geometry (DIPs) ----
    private const double WinH = 300;
    private const double PillTop = 6;
    private const double MaxPillW = 1200;
    private const double MaxPillH = 280;

    // ---- island geometry (DIPs) : dynamic based on screen size ----
    private const double IslandExpandedW = 600;    // s2 (hover hub, music layout)
    private const double IdleIslandW = 640;        // s2 in the idle layout (no media session)
    private const double IdleCollapsedW = 420;     // collapsed idle pill
    private const double IdleCollapsedH = 44;
    private const double IdleCollapsedR = 22;
    private const double MusicLeftW = 400;         // 158 disc gutter + 242 media stack
    private const double IdleLeftW = 438;          // 24 pad + 190 clock + 20 gap + 204 greeting column
    private const int LayoutFadeMs = 280;          // music <-> idle crossfade
    private const double BaseIslandH = 152;        // target height in music mode
    private const double BaseDetailW = 1082;       // s3 baseline on >= 1650px desktop displays
    private const double BaseMirrorW = 843;        // s3 mirror baseline (600 + 13 + 230)
    private const double BaseDetailCardW = 494;    // native card width
    private const double BaseMirrorCardW = 230;    // native mirror card width
    private const double DiscCollapsed = 27;
    private const double DiscExpanded = 122;
    private const double IslandPadV = 13;          // symmetric top/bottom padding inside the island
    private const double MinIslandH = 63;          // smallest island height (empty/placeholder content)

    /// <summary>
    /// Dynamic scale factor for the opened detail island and card based on screen width.
    /// On large desktop monitors (>= 1650 DIPs) it returns 1.0 (original full size).
    /// On laptop screens (1200 - 1650 DIPs) it scales gracefully between 0.78 and 1.0,
    /// ensuring the opened island never touches the right screen edge or pushes the island off-balance.
    /// </summary>
    private double GetDynamicOpenScale()
    {
        if (_winW >= 1650) return 1.0;
        double t = Math.Clamp((_winW - 1200.0) / (1650.0 - 1200.0), 0.0, 1.0);
        return 0.78 + 0.22 * t;
    }

    private double DynamicDetailCardW => Math.Round(BaseDetailCardW * GetDynamicOpenScale());
    private double DynamicMirrorCardW => Math.Round(BaseMirrorCardW * GetDynamicOpenScale());

    private double DynamicDetailW => Math.Round(IslandOpenW + 13 + DynamicDetailCardW);
    private double DynamicMirrorW => Math.Round(IslandOpenW + 13 + DynamicMirrorCardW);

    // Layout-dependent geometry: the idle layout (no media session) has its own open/collapsed sizes.
    private double IslandOpenW => _idleMode ? IdleIslandW : IslandExpandedW;
    private double CollapsedW => _idleMode ? IdleCollapsedW : Math.Max(80, _cfg.BarWidth);
    // The warning strip (40 px) adds to the collapsed pill too, so a low-battery warning is seen.
    private double CollapsedH => (_idleMode ? IdleCollapsedH : Math.Max(24, _cfg.BarHeight)) + (_warningStripActive ? 40 : 0);
    private double CollapsedRadius => _idleMode ? IdleCollapsedR : _cfg.CornerRadius;

    private double DetailWidth() => _activeMetric == IslandMetric.Mirror ? DynamicMirrorW : DynamicDetailW;

    private void ApplyDynamicCardScale()
    {
        double scale = GetDynamicOpenScale();
        ScaleTransform? st = scale < 0.999 ? new ScaleTransform(scale, scale) : null;
        DetailPanelComponent.LayoutTransform = st;
        NetPanelComponent.LayoutTransform = st;
        WeatherPanelComponent.LayoutTransform = st;
        MirrorPanelComponent.LayoutTransform = st;
        TogglesPanelComponent.LayoutTransform = st;
        ClipboardPanelComponent.LayoutTransform = st;
        TimerPanelComponent.LayoutTransform = st;
    }

    // ---- icon glyphs (Segoe Fluent Icons; verified against font render) ----
    private const string IcDot = "\u25CF";
    private const string IcMusic = "\uEC4F";
    private const string IcPlay = "\uE768";
    private const string IcPause = "\uE769";
    private const string IcPrev = "\uE892";
    private const string IcNext = "\uE893";
    private const string IcVolume = "\uE767";
    private const string IcMute = "\uE74F";
    private const string IcBrightness = "\uE706";
    private const string IcBattery = "\uE850";
    private const string IcBatteryCharging = "\uE85A";
    private const string IcNetwork = "\uE968";
    private const string IcBluetooth = "\uE702";
    private const string IcCamera = "\uE722";
    private const string IcMic = "\uE720";
    private const string IcClipboard = "\uE77F";
    private const string IcDownload = "\uE896";
    private const string IcTimer = "\uE917";
    private const string IcStopwatch = "\uE916";
    private const string IcFocus = "\uEA3A";
    private const string IcBell = "\uEA8F";
    private const string IcUsb = "\uE88E";
    private const string IcRecord = "\uE7C8";
    private const string IcGauge = "\uE9D2";

    // ---- state ----
    private readonly SettingsModel _cfg;
    private SpringAnimator _anim;
    private readonly TweenAnimation _fadeCollapsed = new();
    private readonly TweenAnimation _fadeExpanded = new();
    private readonly DispatcherTimer _idleCollapse = new();
    private readonly DispatcherTimer? _fullscreenTimer;
    private readonly System.Collections.Generic.List<DispatcherTimer> _delays = new();

    private HwndSource? _src;
    private IntPtr _hwnd;
    private bool _expanded;
    private bool _userDismissed;
    private DateTime _dismissedAt;
    private double _currentW;
    private double _currentH;
    private double _currentTop = PillTop;
    private double _currentCornerRadius = 26;
    private double _winH = 800;
    private double _maxOffsetDip;
    private bool _dragMoved;
    private bool _dragPressing;
    private double _dragStartX, _dragStartOffset;
    private string _levelMode = "volume";

    // ---- Island Search Bar state ----
    private IntPtr _prevForegroundHwnd;
    private AppInfo? _activeTag;
    private string _ghostRemainder = "";
    private readonly List<SearchResult> _searchResults = new();
    private int _selectedResultIndex = -1;
    private CancellationTokenSource? _searchCts;
    private DispatcherTimer? _actionSuccessTimer;
    private bool _armedRiskyCommand;
    private string _armedCommandTitle = "";

    // ---- volume / brightness HUD accents ----
    private static readonly Color VolAccent = Color.FromRgb(0x2F, 0xD2, 0xB0);
    private static readonly Color BrightAccent = Color.FromRgb(0xFF, 0xC2, 0x4D);
    private static readonly Brush HudMutedBrush = FrozenHud(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush HudValueBrush = FrozenHud(Color.FromRgb(0xE8, 0xE8, 0xDC));
    private bool _timerPaused, _stopwatchPaused, _focusPaused;
    private bool _disposed;

    // blur region cache (px)
    private bool _blurOn;
    private int _blurL = int.MinValue, _blurT, _blurW, _blurH;

    // latest states
    private MediaInfo? _lastMedia;
    private BitmapSource? _lastArt;
    private byte[]? _lastArtBytes;
    private TimerInfo? _lastTimer;
    private StopwatchInfo? _lastStopwatch;
    private FocusInfo? _lastFocus;
    private SystemStats? _lastStats;

    // ---- Island state machine (Steps 2-8) ----
    private IslandVisualState _islandState = IslandVisualState.Collapsed;
    private IslandMetric _activeMetric = IslandMetric.None;
    private readonly DispatcherTimer _collapseDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _hoverCheckTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _islandHovering;
    private bool _pinned;   // detail panel open: stays open regardless of hover until toggled off
    private double _winW = 520;
    private int _gpuIndex;
    private TelemetrySnapshot? _telemetry;
    private NetworkMonitor? _netMonitor;
    private WeatherService? _weather;

    // ---- timeline interpolation timer (250ms) ----
    private readonly DispatcherTimer _timelineTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _isScrubbing;
    private TimeSpan _scrubTime;

    // ---- LRCLIB lyrics & caching ----
    private readonly LrcLibClient _lrcClient = new();
    private CancellationTokenSource? _lrcCts;
    private List<LrcLine>? _currentLrcLines;
    private string? _currentLyricLine;
    private string? _lastLyricTrackKey;

    // ---- Equalizer & Spectrum capture ----
    private readonly WasapiSpectrumService _spectrum = new();

    // ---- Volume hover & toast ----
    private bool _syncingHoverVol;
    private DispatcherTimer? _volumeToastTimer;
    private bool _volumeToastActive;

    // ---- Warning strip ----
    private readonly WarningStripLogic _warningLogic = new();
    private bool _warningStripActive;
    private string _warningKind = "";

    // ---- clock: the ONE 1 s tick (collapsed clocks, idle ring/clock, calendar, focus button) ----
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // ---- idle layout (no media session) ----
    private bool _idleMode = true;                 // no media session -> idle layout
    private bool _layoutApplied;
    private double _layoutP = 1;                   // 0 = music layout, 1 = idle layout
    private readonly TweenAnimation _layoutTween = new();
    private readonly TweenAnimation _hueTween = new();
    private readonly TweenAnimation _pulseTween = new();
    private readonly CalendarService _calendar = new();
    private DayPeriod _todPeriod;
    private double _todHue;
    private bool _todInit;
    private bool _twelveHour;
    private string _eventLineKey = "";
    private bool _focusWasRunning;
    private TimeSpan _focusLastRemaining;
    private uint _msgTaskbarCreated;
    private DispatcherTimer? _displayChangeDebounceTimer;

    public BarWindow()
    {
        InitializeComponent();

        Width = _winW;
        Height = WinH;

        _cfg = App.Settings.Load();
        _twelveHour = ResolveTwelveHour();
        _currentW = CollapsedW;
        _currentH = CollapsedH;
        _currentCornerRadius = CollapsedRadius;
        Pill.Width = _currentW;
        Pill.Height = _currentH;
        ApplyDynamicCardScale();
        UpdatePillMargin();

        _anim = CreateAnimator(_cfg);
        _blurOn = _cfg.BlurEnabled;

        // rounded clip for artwork orbs
        HeaderArt.Clip = new RectangleGeometry(new Rect(0, 0, 34, 34), 17, 17);
        OrbDisc.ReduceMotion = _cfg.ReduceMotion;
        DetailPanelComponent.ReduceMotion = _cfg.ReduceMotion;
        NetPanelComponent.ReduceMotion = _cfg.ReduceMotion;
        MirrorPanelComponent.ReduceMotion = _cfg.ReduceMotion;

        var (page1, page2) = IslandLayout.FromSettings(_cfg.IslandPage1, _cfg.IslandPage2);
        IconRow.SetLayout(page1, page2);
        IconRow.MetricHovered += OnIconMetricHovered;
        IconRow.MetricUnhovered += OnIconMetricUnhovered;
        IconRow.MetricClicked += OnIconMetricClicked;
        IconRow.PageChanged += OnIconPageChanged;
        DetailPanelComponent.GpuCycleRequested += OnGpuCycleRequested;
        TogglesPanelComponent.ToggleRequested += OnToggleRequested;
        if (App.Toggles is not null) App.Toggles.Changed += OnTogglesChanged;
        _hookedToggles = App.Toggles;
        App.ServiceToggled += OnServiceToggled;
        HookClipboardPanel(true);
        WindowSwitcherService.Instance.Refreshed += OnWindowListRefreshed;
        TimerPanelComponent.StartRequested += OnTimerStartRequested;
        TimerPanelComponent.StopRequested += OnTimerStopRequested;
        TimerPanelComponent.PauseRequested += OnTimerPauseRequested;
        TimerPanelComponent.ResumeRequested += OnTimerResumeRequested;
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler((s, _) =>
            {
                if (s is TextBox tb && Window.GetWindow(tb) is BarWindow win)
                {
                    win.Activate();
                    tb.Focus();
                }
            }));
        NetPanelComponent.TestToggleRequested += OnNetTestToggle;
        MirrorPanelComponent.CameraStateChanged += OnMirrorCameraStateChanged;
        _collapseDelay.Tick += OnCollapseDelayTick;
        _hoverCheckTimer.Tick += OnHoverCheckTick;

        ProgressTrack.SizeChanged += (_, _) => SyncFills();
        VolumeHudBar.Scrub += OnVolumeHudScrub;

        _layoutTween.Completed += FinishLayoutFade;
        _pulseTween.Completed += () => { PillPulse.ScaleX = 1; PillPulse.ScaleY = 1; };
        _calendar.Changed += () =>
        {
            try { Dispatcher.BeginInvoke(() => RefreshIdle(DateTime.Now)); } catch { }
        };
        IdleDotClock.ReduceMotion = _cfg.ReduceMotion;
        ApplyLayoutMode(instant: true);

        // One 1 s timer; it only runs while the bar is visible (stopped when hidden for fullscreen).
        _clockTimer.Tick += (_, _) => OnSecondTick();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _clockTimer.Start(); OnSecondTick(); }
            else _clockTimer.Stop();
            SyncTimelineTimer();
        };
        OnSecondTick();

        _idleCollapse.Tick += (_, _) =>
        {
            _idleCollapse.Stop();
            if (!_islandHovering && App.Bus?.Active is null && _expanded) Collapse();
        };

        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        PreviewKeyDown += OnBarPreviewKeyDown;
        // Progress/lyrics interpolation: runs only while the island is open (see SyncTimelineTimer).
        _timelineTimer.Tick += (_, _) => OnTimelineTick();

        _spectrum.SpectrumChanged += bands =>
        {
            try { Dispatcher.InvokeAsync(() => HoverEq.SetSpectrum(bands)); } catch { }
        };

        if (App.Volume is not null)
        {
            App.Volume.VolumeChanged += (_, _) =>
            {
                try { Dispatcher.InvokeAsync(UpdateVolumeControls); } catch { }
            };
        }

        ApplySettingsUi(_cfg);
        Subscribe();
        InitSearch();

        SourceInitialized += (_, _) => OnSourceInitialized();
        Log.Debug("BarWindow constructed");
    }

    private SpringAnimator CreateAnimator(SettingsModel cfg)
    {
        var a = new SpringAnimator(cfg.ReduceMotion ? 0.35 : Math.Max(0.2, cfg.AnimationSpeed))
        {
            Stiffness = Math.Max(40, cfg.SpringStiffness),
            Damping = Math.Max(4, cfg.SpringDamping),
        };
        a.AnimationCompleted += OnAnimationsSettled;
        a.AnimationCompleted += () => { SearchTrace("animation completed"); _traceLastFrameMs = -1; };
        return a;
    }

    // ---------------------------------------------------------------- lifecycle

    private void Subscribe()
    {
        try
        {
            if (App.Bus is not null)
            {
                App.Bus.EventRaised += OnBusEvent;
                App.Bus.Idle += OnBusIdle;
            }
            if (App.Media is not null)
            {
                App.Media.MediaChanged += OnMediaChanged;
                _lastMedia = App.Media.Current;
            }
            if (App.Timer is not null) App.Timer.TimerChanged += OnTimerChanged;
            if (App.Stopwatch is not null) App.Stopwatch.StopwatchChanged += OnStopwatchChanged;
            if (App.Focus is not null) App.Focus.FocusChanged += OnFocusChanged;
            if (App.SysMon is not null) App.SysMon.StatsChanged += OnStatsChanged;
            if (App.Telemetry is not null) App.Telemetry.TelemetryUpdated += OnTelemetryUpdated;
            if (App.Volume is not null) App.Volume.VolumeChanged += OnExternalVolumeChanged;
            if (App.Settings is not null) App.Settings.SettingsChanged += OnSettingsChanged;
        }
        catch (Exception ex) { Log.Error("Bar subscribe failed", ex); }
    }

    private void Unsubscribe()
    {
        try
        {
            if (App.Bus is not null)
            {
                App.Bus.EventRaised -= OnBusEvent;
                App.Bus.Idle -= OnBusIdle;
            }
            if (App.Media is not null) App.Media.MediaChanged -= OnMediaChanged;
            if (App.Timer is not null) App.Timer.TimerChanged -= OnTimerChanged;
            if (App.Stopwatch is not null) App.Stopwatch.StopwatchChanged -= OnStopwatchChanged;
            if (App.Focus is not null) App.Focus.FocusChanged -= OnFocusChanged;
            if (App.SysMon is not null) App.SysMon.StatsChanged -= OnStatsChanged;
            if (App.Telemetry is not null) App.Telemetry.TelemetryUpdated -= OnTelemetryUpdated;
            if (App.Volume is not null) App.Volume.VolumeChanged -= OnExternalVolumeChanged;
            if (App.Settings is not null) App.Settings.SettingsChanged -= OnSettingsChanged;
        }
        catch { /* teardown is best-effort */ }
    }

    private void OnExternalVolumeChanged(float level, bool muted)
    {
        try { Dispatcher.BeginInvoke(() => UpdateVolumeControls()); } catch { }
    }

    private void OnSourceInitialized()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _src = HwndSource.FromHwnd(_hwnd);
        _src?.AddHook(WndProc);

        // Apply WS_EX_NOACTIVATE so the pill does not steal focus when it appears
        IntPtr exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle.ToInt64() | NativeMethods.WS_EX_NOACTIVATE));

        _msgTaskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        RegisterHotkeys();
        try { App.Clipboard?.Attach(_hwnd); } catch { }
        StartBatteryNotifications();

        Reposition();
        _blurL = int.MinValue;
        RefreshBlur();
        _fullscreenTimer?.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureWeather();
        UpdateCollapsed();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, PrewarmSearch);
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unsubscribe();
        IconRow.MetricHovered -= OnIconMetricHovered;
        IconRow.MetricUnhovered -= OnIconMetricUnhovered;
        IconRow.MetricClicked -= OnIconMetricClicked;
        IconRow.PageChanged -= OnIconPageChanged;
        TogglesPanelComponent.ToggleRequested -= OnToggleRequested;
        if (_hookedToggles is not null) _hookedToggles.Changed -= OnTogglesChanged;
        App.ServiceToggled -= OnServiceToggled;
        HookClipboardPanel(false);
        WindowSwitcherService.Instance.Refreshed -= OnWindowListRefreshed;
        TimerPanelComponent.StartRequested -= OnTimerStartRequested;
        TimerPanelComponent.StopRequested -= OnTimerStopRequested;
        TimerPanelComponent.PauseRequested -= OnTimerPauseRequested;
        TimerPanelComponent.ResumeRequested -= OnTimerResumeRequested;
        try { _collapseDelay.Stop(); } catch { }
        try { _hoverCheckTimer.Stop(); } catch { }
        try { _clockTimer.Stop(); } catch { }
        try { _layoutTween.Dispose(); _hueTween.Dispose(); _pulseTween.Dispose(); } catch { }
        try { _timelineTimer.Stop(); } catch { }
        try { _volumeToastTimer?.Stop(); } catch { }
        try { _lrcCts?.Cancel(); _lrcClient.Dispose(); } catch { }
        try { _spectrum.Dispose(); } catch { }
        try { _fullscreenTimer?.Stop(); } catch { }
        try { _idleCollapse.Stop(); } catch { }
        CancelDelays();
        _fadeCollapsed.Stop(); _fadeExpanded.Stop();
        try { if (_netMonitor is not null) { _netMonitor.Updated -= OnNetUpdated; _netMonitor.Dispose(); _netMonitor = null; } } catch { }
        try { if (_weather is not null) { _weather.Updated -= OnWeatherUpdated; _weather.Dispose(); _weather = null; } } catch { }
        try { _searchCts?.Cancel(); } catch { }
        try { _actionSuccessTimer?.Stop(); } catch { }
        try { MirrorPanelComponent.Shutdown(); } catch { }
        _anim.AnimationCompleted -= OnAnimationsSettled;
        _anim.Dispose();
        try
        {
            for (int id = 1; id <= 5; id++) NativeMethods.UnregisterHotKey(_hwnd, id);
        }
        catch { }
        StopBatteryNotifications();
        try { _src?.RemoveHook(WndProc); } catch { }
        try { BlurHelper.Detach(); } catch { }
    }

    // ---------------------------------------------------------------- window proc

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_NCHITTEST:
                handled = true;
                return HitTest(lParam);

            case NativeMethods.WM_MOUSEACTIVATE:
                if (_islandState is IslandVisualState.Detail or IslandVisualState.Search)
                {
                    handled = true;
                    return new IntPtr(NativeMethods.MA_ACTIVATE);
                }
                break;

            case NativeMethods.WM_HOTKEY:
                handled = true;
                int id = wParam.ToInt32();
                if (id == 4) SearchTrace("hotkey received");
                Dispatcher.BeginInvoke(() =>
                {
                    if (id == 4) SearchTrace("hotkey dispatched");
                    switch (id)
                    {
                        case 1: Toggle(); break;
                        case 2: try { _ = App.Media?.RefreshAsync(showEvent: true, force: true); } catch { } break;
                        case 3: ShowOrStartTimer(); break;
                        case 4: ToggleSearch(); break;
                        case 5: OpenClipboardPanel(byHotkey: true); break;
                    }
                });
                break;

            case NativeMethods.WM_CLIPBOARDUPDATE:
                App.ClipHistory?.OnClipboardUpdate(hwnd);
                try { App.Clipboard?.OnClipboardUpdate(); } catch { }
                break;

            case NativeMethods.WM_DEVICECHANGE:
                try { App.Usb?.OnDeviceChange(wParam.ToInt32(), lParam); } catch { }
                break;

            case NativeMethods.WM_SETTINGCHANGE:
                // Light/dark switched (here or in Settings): the dark-mode toggle re-reads the registry.
                if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                    App.Toggles?.RefreshDarkMode();
                // Regional settings may have switched the clock between 12 and 24 h.
                Dispatcher.BeginInvoke(() =>
                {
                    _twelveHour = ResolveTwelveHour();
                    // Windows "Animation effects" changed: re-derive Reduce Motion when following it.
                    if (_cfg.ReduceMotionMode == ReduceMotionMode.FollowSystem && App.Settings is { } st) ApplySettingsUi(st.Current);
                    OnSecondTick();
                    Reposition();
                });
                break;

            case NativeMethods.WM_POWERBROADCAST when IsBatteryBroadcast(wParam):
                Dispatcher.BeginInvoke(OnBatteryChanged);
                break;

            case NativeMethods.WM_DISPLAYCHANGE:
                if (_displayChangeDebounceTimer is null)
                {
                    _displayChangeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _displayChangeDebounceTimer.Tick += (_, _) =>
                    {
                        _displayChangeDebounceTimer.Stop();
                        Reposition();
                    };
                }
                _displayChangeDebounceTimer.Stop();
                _displayChangeDebounceTimer.Start();
                break;

            case NativeMethods.WM_POWERBROADCAST:
                Dispatcher.BeginInvoke(Reposition);
                break;
        }

        if (_msgTaskbarCreated != 0 && (uint)msg == _msgTaskbarCreated)
        {
            Dispatcher.BeginInvoke(() =>
            {
                App.Tray?.RecreateIcon();
                Reposition();
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            });
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private IntPtr HitTest(IntPtr lParam)
    {
        try
        {
            long lp = lParam.ToInt64();
            var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            var p = PointFromScreen(screen);
            double left = Pill.Margin.Left;
            double minDim = Math.Min(Math.Max(0, _currentW), Math.Max(0, _currentH));
            double r = Math.Max(0, Math.Min(_currentCornerRadius, minDim / 2));
            if (InRoundedRect(p.X, p.Y, left, _currentTop, _currentW, _currentH, r))
                return new IntPtr(NativeMethods.HTCLIENT);
            return new IntPtr(NativeMethods.HTTRANSPARENT);
        }
        catch { return new IntPtr(NativeMethods.HTCLIENT); }
    }

    private static bool InRoundedRect(double x, double y, double l, double t, double w, double h, double r)
    {
        if (x < l || y < t || x > l + w || y > t + h) return false;
        double ix0 = l + r, iy0 = t + r, ix1 = l + w - r, iy1 = t + h - r;
        if (x >= ix0 && x <= ix1) return true;
        if (y >= iy0 && y <= iy1) return true;
        double cx = x < ix0 ? ix0 : ix1;
        double cy = y < iy0 ? iy0 : iy1;
        double dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    // ---------------------------------------------------------------- positioning

    private void Reposition() => Reposition(_cfg.CustomOffsetX);

    private void Reposition(double offsetDip)
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            var monitors = NativeMethods.GetMonitors();
            if (monitors.Count == 0) return;

            var cfg = _cfg;
            NativeMethods.MonitorInfoEx mon =
                cfg.MonitorIndex >= 0 && cfg.MonitorIndex < monitors.Count
                    ? monitors[cfg.MonitorIndex]
                    : monitors.FirstOrDefault(m => m.Bounds.Left == 0 && m.Bounds.Top == 0)
                      ?? monitors[0];

            PlaceWindow(mon);
            _maxOffsetDip = Math.Max(0, (_winW - CollapsedW) / 2 - 8);

            // Clamp a previously saved offset in case the target monitor shrank.
            _cfg.CustomOffsetX = Math.Clamp(offsetDip, -_maxOffsetDip, _maxOffsetDip);

            ApplyDynamicCardScale();
            UpdatePillMargin();
            RefreshBlur();
        }
        catch (Exception ex) { Log.Debug("Reposition failed", ("error", ex.Message)); }
    }

    // The window is layered (per-pixel alpha): every changed frame pushes the whole surface, so it
    // is kept only as tall as the island can get. Search (which sits lower on screen) gets the full
    // work area while it is open and until its closing animation has settled.
    private const double CompactWinH = 340;   // 6 top + 280 max card + shadow
    private NativeMethods.MonitorInfoEx? _placedOn;
    private bool _tallWindow;

    /// <summary>Size the one overlay window to the work area of <paramref name="mon"/> (full width;
    /// compact height unless the search bar needs the full height).</summary>
    private void PlaceWindow(NativeMethods.MonitorInfoEx mon)
    {
        double scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(mon.Handle, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
            scale = dpiX / 96.0;

        int workW = mon.WorkArea.Right - mon.WorkArea.Left;
        int workH = mon.WorkArea.Bottom - mon.WorkArea.Top;
        _placedOn = mon;
        _winW = workW / scale;
        _winH = workH / scale;   // full work-area height (search position is relative to it)

        double hDip = _tallWindow ? _winH : Math.Min(_winH, CompactWinH);
        int hPx = (int)Math.Ceiling(hDip * scale);
        Width = _winW;
        Height = hDip;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
            mon.WorkArea.Left, mon.WorkArea.Top, workW, hPx, NativeMethods.SWP_NOACTIVATE);
    }

    private void SetTallWindow(bool tall)
    {
        if (tall == _tallWindow || _hwnd == IntPtr.Zero) return;
        _tallWindow = tall;
        if (_placedOn is { } mon) PlaceWindow(mon);
    }

    /// <summary>Animations settled: once search is fully closed, give back the tall window.</summary>
    private void OnAnimationsSettled()
    {
        if (_islandState != IslandVisualState.Search && _searchPhase == SearchPhase.Closed && _tallWindow) SetTallWindow(false);
    }

    private double CurrentDpi() =>
        PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    // ---------------------------------------------------------------- blur / shape

    private void RefreshBlur()
    {
        if (!_blurOn || _blurSuspended || _hwnd == IntPtr.Zero) return;
        double scale = CurrentDpi();
        double l = Pill.Margin.Left * scale;
        double t = _currentTop * scale;
        double w = _currentW * scale;
        double h = _currentH * scale;
        int li = (int)Math.Round(l);
        int ti = (int)Math.Round(t);
        int wi = (int)Math.Round(w);
        int hi = (int)Math.Round(h);
        if (li == _blurL && ti == _blurT && wi == _blurW && hi == _blurH) return;
        _blurL = li; _blurT = ti; _blurW = wi; _blurH = hi;
        double targetR = _currentCornerRadius;
        BlurHelper.UpdateRegion(li, ti, wi, hi, (int)Math.Round(targetR * scale), true);
    }

    private void UpdateShape()
    {
        double minDim = Math.Min(Math.Max(0, _currentW), Math.Max(0, _currentH));
        double targetR = _currentCornerRadius;
        double r = Math.Max(0, Math.Min(targetR, minDim / 2));
        Pill.CornerRadius = new CornerRadius(r);
        // Content clip = the inner rounded shape (inside the 1 px border).
        double inner = Math.Max(0, r - 1);
        _pillClip.Rect = new Rect(0, 0, Math.Max(0, _currentW - 2), Math.Max(0, _currentH - 2));
        _pillClip.RadiusX = inner;
        _pillClip.RadiusY = inner;
        RefreshBlur();
    }

    private void ApplyW(double v)
    {
        v = Math.Max(20, v);
        _currentW = v;
        Pill.Width = v;
        UpdatePillMargin();
        UpdateShape();
        SyncFills();
    }

    private void ApplyH(double v)
    {
        SearchTraceFrame();
        v = Math.Max(10, v);
        _currentH = v;
        Pill.Height = v;
        UpdatePillMargin();
        UpdateShape();
    }

    private void ApplyTop(double v)
    {
        _currentTop = v;
        UpdatePillMargin();
        RefreshBlur();
    }

    private void ApplyCornerRadius(double r)
    {
        _currentCornerRadius = r;
        UpdateShape();
    }

    /// <summary>Position the pill. Up to the normal expanded width it stays centred on screen and
    /// grows symmetrically about the centre (the small pill expands evenly on both sides on hover).
    /// Wider than that - i.e. the detail panel - it extends to the right dynamically sized to the screen.
    /// On large screens it keeps its left edge fixed. On smaller laptop screens it smoothly adjusts
    /// so it never touches the right screen edge or pushes the island abruptly.</summary>
    private void UpdatePillMargin()
    {
        double offset = _islandState == IslandVisualState.Search ? 0 : _cfg.CustomOffsetX;
        double left;
        if (_islandState == IslandVisualState.Search)
        {
            left = (_winW - _currentW) / 2;
        }
        else if (_currentW <= IslandOpenW)
        {
            // Collapsed and hover: stay centred so the pill grows evenly on both sides.
            left = (_winW - _currentW) / 2 + offset;
        }
        else
        {
            // Detail panel is open or opening.
            // On desktop: keep left edge fixed at the hover center so disc/icons don't shift.
            // On laptop: if extending to targetDetailW would exceed screen bounds (safe right margin 24 DIPs),
            // smoothly and proportionally adjust left so it never touches the right edge or jerks.
            double hoverLeft = (_winW - IslandOpenW) / 2 + offset;
            double targetW = DetailWidth();
            const double safeMargin = 24.0;
            double maxAllowedLeft = _winW - targetW - safeMargin;

            if (hoverLeft <= maxAllowedLeft)
            {
                // Plenty of room on the right (desktop monitor) - left edge stays completely stationary!
                left = hoverLeft;
            }
            else
            {
                // Smaller screen (laptop) - interpolate left smoothly as _currentW expands
                // so the movement is gentle, fluid and proportional throughout the spring animation.
                double progress = Math.Clamp((_currentW - IslandOpenW) / Math.Max(1.0, targetW - IslandOpenW), 0.0, 1.0);
                left = hoverLeft + (maxAllowedLeft - hoverLeft) * progress;
            }
        }
        double maxLeft = Math.Max(8, _winW - _currentW - 8);
        left = Math.Max(8, Math.Min(left, maxLeft));
        Pill.Margin = new Thickness(left, _currentTop, 0, 0);
    }

    // ---------------------------------------------------------------- expand / collapse

    private static double TargetWidth(BarEventKind kind) => kind switch
    {
        BarEventKind.Media => 420,
        BarEventKind.Volume or BarEventKind.Brightness => 380,
        BarEventKind.Download => 380,
        BarEventKind.SystemMonitor => 360,
        BarEventKind.Timer or BarEventKind.Stopwatch or BarEventKind.Focus => 330,
        _ => 340,
    };

    private void Expand()
    {
        if (_shown is null) return;
        if (_islandState != IslandVisualState.Collapsed) SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);

        // Guarantee mutual exclusion: legacy ExpandedHost and modern IslandExpandedHost must never overlap
        IslandExpandedHost.Visibility = Visibility.Collapsed;
        IslandExpandedHost.Opacity = 0;
        foreach (UIElement panel in AllPanels)
        {
            panel.Visibility = Visibility.Collapsed;
            panel.Opacity = 0;
            panel.IsHitTestVisible = false;
        }

        double w = Math.Clamp(TargetWidth(_shown.Kind), 120, MaxPillW);

        ExpandedHost.Visibility = Visibility.Visible;
        ExpandedHost.Measure(new Size(w, 10000));
        double h = Math.Clamp(
            Math.Max(_cfg.BarHeight, ExpandedHost.DesiredSize.Height), 40, MaxPillH);

        CancelDelays();
        _expanded = true;
        _idleCollapse.Stop();

        Fade(CollapsedPanel, 0, 50);
        Fade(OrbHost, 0, 50, () =>
        {
            if (_expanded) OrbHost.Visibility = Visibility.Collapsed;
        });
        _delays.Add(RunAfter(40, () =>
            Fade(ExpandedHost, 1, 120)));

        ClaimGeometryForSpring(reveal: false);
        _anim.Animate("w", _currentW, w, ApplyW);
        _anim.Animate("h", _currentH, h, ApplyH);
        SyncFills();
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _idleCollapse.Stop();
        CancelDelays();
        VolumeHudGlow.Visibility = Visibility.Collapsed;
        VolumeHudGlowStop.Color = Colors.Transparent;

        Fade(ExpandedHost, 0, 110, () =>
        {
            if (!_expanded) ExpandedHost.Visibility = Visibility.Collapsed;
        });
        CollapsedPanel.Visibility = Visibility.Visible;
        OrbHost.Visibility = Visibility.Visible;
        _delays.Add(RunAfter(60, () =>
        {
            Fade(CollapsedPanel, 1, 140);
            Fade(OrbHost, 1, 140);
        }));

        double w = CollapsedW;
        double h = CollapsedH;
        ClaimGeometryForSpring(reveal: false);
        _anim.Animate("w", _currentW, w, ApplyW);
        _anim.Animate("h", _currentH, h, ApplyH);
        UpdateCollapsed();
    }

    private void Fade(UIElement el, double to, int ms, Action? done = null)
    {
        if (_cfg.ReduceMotion || ms <= 0)
        {
            el.Opacity = to;
            if (to > 0) el.Visibility = Visibility.Visible;
            done?.Invoke();
            return;
        }
        var tw = new TweenAnimation();
        tw.Completed += () => done?.Invoke();
        tw.Run(el.Opacity, to, TimeSpan.FromMilliseconds(ms), v => el.Opacity = v, Easing.CubicOut);
        // keep ref for cancellation: store on element tag? simple: track in list via closure container
        _activeFades.Add(tw);
        tw.Completed += () => _activeFades.Remove(tw);
    }

    private readonly System.Collections.Generic.List<TweenAnimation> _activeFades = new();

    private void CancelDelays()
    {
        foreach (var t in _delays) t.Stop();
        _delays.Clear();
        foreach (var f in _activeFades) f.Stop();
        _activeFades.Clear();
    }

    private DispatcherTimer RunAfter(int ms, Action action)
    {
        var t = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); _delays.Remove(t); action(); };
        t.Start();
        _delays.Add(t);
        return t;
    }

    // ---------------------------------------------------------------- telemetry zone

    private void OnTelemetryUpdated(TelemetrySnapshot snap)
    {
        try { Dispatcher.BeginInvoke(() => ApplyTelemetry(snap)); } catch { }
    }

    private void ApplyTelemetry(TelemetrySnapshot snap)
    {
        _telemetry = snap;
        if (_islandState == IslandVisualState.Detail)
        {
            UpdatePanelContent();
            FitIslandHeight();
        }
        else if (_islandState == IslandVisualState.Expanded) UpdateIslandContent();
    }


    /// <summary>Set the Island state: Collapsed, Expanded (hover), or Detail (a single metric card
    /// open). One method drives size, disc, content fades, panels and icon highlight (R4/R9).</summary>
    private void SetIslandState(IslandVisualState state, IslandMetric metric, bool force = false)
    {
        if (!force && state == _islandState && metric == _activeMetric) return;

        bool sameState = state == _islandState;
        var prevState = _islandState;
        _islandState = state;
        _activeMetric = metric;
        if (prevState == IslandVisualState.Search && state != IslandVisualState.Search) LeaveSearchMode(state);
        _collapseDelay.Stop();
        VolumeHudGlow.Visibility = Visibility.Collapsed;
        VolumeHudGlowStop.Color = Colors.Transparent;

        // Privacy: the mirror camera is released the moment it is no longer the active metric
        // (hovering another icon, collapsing, switching panels). Starting it only ever happens
        // from an explicit Mirror icon click or the panel's CAMERA ON button.
        if (metric != IslandMetric.Mirror) MirrorPanelComponent.StopCamera();

        // Switching metric while a panel is already open: refresh content (height never changes
        // once enlarged - the panel rides inside the expanded island and only the width extends).
        if (sameState && state == IslandVisualState.Detail)
        {
            IconRow.SetActive(metric);
            SwitchPanel();
            UpdatePanelContent();
            AnimateIsland(DetailWidth(), MeasureIslandHeight());
            if (metric == IslandMetric.Timer)
            {
                Activate();
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => TimerPanelComponent.FocusCustom());
            }
            return;
        }

        switch (state)
        {
            case IslandVisualState.Collapsed:
                _pinned = false;
                _hoverCheckTimer.Stop();
                IconRow.SetPage(0, instant: true);
                IconRow.SuspendPulse();
                // Closing the search bar: the pill's own content returns only after the morph lands.
                HideIslandContent(revealCollapsed: _searchPhase != SearchPhase.Closing);
                AnimateIsland(CollapsedW, CollapsedH);
                MorphDisc(DiscCollapsed);
                break;

            case IslandVisualState.Expanded:
                EnsureTelemetry();
                _hoverCheckTimer.Start();
                ShowIslandContent(withPanel: false);
                AnimateIsland(IslandOpenW, MeasureIslandHeight());
                MorphDisc(DiscExpanded);
                break;

            case IslandVisualState.Detail:
                EnsureTelemetry();
                if (!_pinned) _hoverCheckTimer.Start(); else _hoverCheckTimer.Stop();
                ShowIslandContent(withPanel: true);
                IconRow.SetActive(metric);
                SwitchPanel();   // entering Detail from Expanded must show the metric's panel too
                UpdatePanelContent();
                AnimateIsland(DetailWidth(), MeasureIslandHeight());
                MorphDisc(DiscExpanded);
                if (metric == IslandMetric.Timer)
                {
                    Activate();
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, () => TimerPanelComponent.FocusCustom());
                }
                break;

            case IslandVisualState.Search:
                // Only one view is visible: everything else is Collapsed (cannot show through or take space).
                _pinned = false;
                _hoverCheckTimer.Stop();
                _expanded = false;
                _idleCollapse.Stop();
                CancelDelays();   // no pending fade may bring another view back while searching
                ExpandedHost.Visibility = Visibility.Collapsed;
                ExpandedHost.Opacity = 0;
                WarningStrip.Visibility = Visibility.Collapsed;
                CollapsedPanel.Visibility = Visibility.Collapsed;
                CollapsedPanel.Opacity = 0;
                OrbHost.Visibility = Visibility.Collapsed;
                OrbHost.Opacity = 0;
                IslandExpandedHost.Visibility = Visibility.Collapsed;
                IslandExpandedHost.Opacity = 0;
                foreach (UIElement panel in AllPanels)
                {
                    panel.Visibility = Visibility.Collapsed;
                    panel.Opacity = 0;
                    panel.IsHitTestVisible = false;
                }
                SearchHost.Visibility = Visibility.Visible;
                break;
        }
        SyncTimelineTimer();
        // Reduce Motion applies geometry instantly, so no "settled" event will follow.
        if (state != IslandVisualState.Search && _searchPhase == SearchPhase.Closed && (_cfg.ReduceMotion || !_anim.IsAnimating))
            SetTallWindow(false);
    }

    private void RenderTelemetry(TelemetryDetailPanel panel, IslandMetric metric)
    {
        var snap = _telemetry;
        var tel = App.Telemetry;
        if (snap is null || tel is null)
        {
            // No sample yet: show an empty canvas (rows appear once real values arrive).
            switch (metric)
            {
                case IslandMetric.Cpu: panel.ShowCpu(new CpuStats(0, "", 0, 0, 0, false), null, null); break;
                case IslandMetric.Gpu: panel.ShowGpu(new GpuTrack(), 0, Array.Empty<string>(), null); break;
                case IslandMetric.Ram: panel.ShowRam(new MemoryStats(0, 0, 0, false), 0, 0, 0, -1, -1, null); break;
            }
            return;
        }

        var gpus = tel.SnapshotGpuTracks();
        var procs = snap.TopProcesses;
        bool fahrenheit = _cfg.TemperatureFahrenheit;
        switch (metric)
        {
            case IslandMetric.Cpu:
                tel.SensorLuid = 0;
                panel.ShowCpu(snap.Cpu, tel.CpuCoreUsage, tel.CpuHistory, procs,
                    tel.CpuTemperatureC, tel.CpuLoad, fahrenheit);
                break;

            case IslandMetric.Gpu:
                if (gpus.Count == 0) { tel.SensorLuid = 0; panel.ShowGpu(new GpuTrack(), 0, Array.Empty<string>(), null, procs); break; }
                int idx = Math.Clamp(_gpuIndex, 0, gpus.Count - 1);
                tel.SensorLuid = gpus[idx].Luid;   // NVML is read only for the GPU on screen
                panel.ShowGpu(gpus[idx], idx, gpus.Select(g => g.Name).ToList(), tel.GetGpuHistory(idx), procs, fahrenheit);
                break;

            case IslandMetric.Ram:
                tel.SensorLuid = 0;
                panel.ShowRam(snap.Memory, tel.RamSwapUsedBytes, tel.RamSwapTotalBytes,
                    tel.RamCacheBytes, tel.DiskReadBytesPerSec, tel.DiskWriteBytesPerSec,
                    tel.RamHistory, procs);
                break;
        }
    }

    private void AnimateIsland(double w, double h)
    {
        if (_searchPhase == SearchPhase.Closing && _islandState == IslandVisualState.Collapsed)
        {
            // The search bar is morphing back to the pill: (re)target that one morph.
            MorphSearch(w, h, PillTop, CollapsedRadius, ScaledMs(SearchCloseMs), OnSearchClosed);
            return;
        }
        ClaimGeometryForSpring(reveal: false);
        if (_islandState == IslandVisualState.Search) return;   // the search morph owns the geometry

        double targetTop = _islandState == IslandVisualState.Search ? Math.Round(_winH * 0.22) : PillTop;
        double targetR = _islandState == IslandVisualState.Search ? 29 : (_islandState != IslandVisualState.Collapsed ? 36 : CollapsedRadius);

        if (_cfg.ReduceMotion)
        {
            ApplyW(w);
            ApplyH(h);
            ApplyTop(targetTop);
            ApplyCornerRadius(targetR);
            return;
        }
        _anim.Animate("w", _currentW, w, ApplyW);
        _anim.Animate("h", _currentH, h, ApplyH);
        _anim.Animate("top", _currentTop, targetTop, ApplyTop);
        _anim.Animate("radius", _currentCornerRadius, targetR, ApplyCornerRadius);
    }

    /// <summary>Height of the island derived from its hover content: the tallest of the media
    /// cluster or the disc, plus symmetric vertical padding, clamped to the allowed range. The
    /// detail panel never grows the island - it rides inside this height so opening a metric only
    /// extends the island's right border.</summary>
    private double MeasureIslandHeight()
    {
        double h = BaseIslandH;
        if (_warningStripActive) h += 40;
        return Math.Clamp(h, MinIslandH, MaxPillH);
    }

    /// <summary>Re-fit the island height after content changes, animating only when the
    /// content-driven target actually changed (avoids per-tick churn).</summary>
    private void FitIslandHeight()
    {
        double target = MeasureIslandHeight();
        if (Math.Abs(target - _currentH) > 0.5)
            AnimateIsland(DetailWidth(), target);
    }

    private void MorphDisc(double size)
    {
        OrbContainer.Margin = new Thickness(_islandState == IslandVisualState.Collapsed ? 10.5 : 16, 0, 0, 0);
        if (_cfg.ReduceMotion)
        {
            OrbHost.Width = size;
            OrbHost.Height = size;
            OrbHost.CornerRadius = new CornerRadius(size / 2);
            SourceBadge.Visibility = (_islandState != IslandVisualState.Collapsed && _lastMedia is not null && _lastMedia.SourceAppIconBytes is not null) ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        double from = OrbHost.Width > 0 ? OrbHost.Width : DiscCollapsed;
        _anim.Animate("disc", from, size, v =>
        {
            OrbHost.Width = v;
            OrbHost.Height = v;
            OrbHost.CornerRadius = new CornerRadius(v / 2);
        });
        SourceBadge.Visibility = (_islandState != IslandVisualState.Collapsed && _lastMedia is not null && _lastMedia.SourceAppIconBytes is not null) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowIslandContent(bool withPanel)
    {
        // Mutual exclusion: dismiss and hide legacy ExpandedHost when modern dynamic island is shown
        if (_expanded)
        {
            _expanded = false;
            _idleCollapse.Stop();
        }
        ExpandedHost.Visibility = Visibility.Collapsed;
        ExpandedHost.Opacity = 0;

        ApplyDynamicCardScale();
        CollapsedPanel.Visibility = Visibility.Visible;
        OrbHost.Visibility = Visibility.Visible;
        Fade(OrbHost, 1, 160);
        IslandExpandedHost.Visibility = Visibility.Visible;
        Fade(CollapsedPanel, 0, 120);
        Fade(IslandExpandedHost, 1, 160);
        EnsureWeather();
        _weather?.Start();
        if (withPanel)
        {
            EnsureNetMonitor();
            _netMonitor?.Start();
        }
        else
        {
            foreach (UIElement panel in AllPanels)
            {
                panel.IsHitTestVisible = false;
                Fade(panel, 0, 120, () =>
                {
                    if (_islandState != IslandVisualState.Detail) panel.Visibility = Visibility.Collapsed;
                });
            }
        }
        UpdateIslandContent();
    }

    private UIElement[] AllPanels => new UIElement[]
        { DetailPanelComponent, NetPanelComponent, MirrorPanelComponent, WeatherPanelComponent, TogglesPanelComponent,
          ClipboardPanelComponent, TimerPanelComponent };

    /// <summary>Shows the panel matching the active metric (telemetry card, net card, mirror card
    /// or weather card) and fades the others out. Only meaningful in the island's Detail state.</summary>
    private UIElement ActivePanel => _activeMetric switch
    {
        IslandMetric.Net => NetPanelComponent,
        IslandMetric.Mirror => MirrorPanelComponent,
        IslandMetric.Weather => WeatherPanelComponent,
        IslandMetric.Toggles => TogglesPanelComponent,
        IslandMetric.Clipboard => ClipboardPanelComponent,
        IslandMetric.Timer => TimerPanelComponent,
        _ => DetailPanelComponent,
    };

    private void SwitchPanel()
    {
        ApplyDynamicCardScale();
        var show = ActivePanel;
        foreach (UIElement panel in AllPanels)
        {
            if (ReferenceEquals(panel, show)) continue;
            panel.IsHitTestVisible = false;
            Fade(panel, 0, 120, () =>
            {
                if (!ReferenceEquals(ActivePanel, panel)) panel.Visibility = Visibility.Collapsed;
            });
        }

        show.Visibility = Visibility.Visible;
        show.IsHitTestVisible = true;
        Fade(show, 1, 160);
    }

    private void HideIslandContent(bool revealCollapsed = true)
    {
        ExpandedHost.Visibility = Visibility.Collapsed;
        ExpandedHost.Opacity = 0;
        if (_searchPhase != SearchPhase.Closing)   // a closing search bar fades itself out
        {
            SearchHost.Visibility = Visibility.Collapsed;
            SearchHost.Opacity = 0;
        }
        IconRow.SetActive(IslandMetric.None);
        foreach (UIElement panel in AllPanels)
        {
            panel.IsHitTestVisible = false;
            Fade(panel, 0, 100, () =>
            {
                if (_islandState == IslandVisualState.Collapsed) panel.Visibility = Visibility.Collapsed;
            });
        }
        Fade(IslandExpandedHost, 0, 120,
            () => { if (_islandState == IslandVisualState.Collapsed) IslandExpandedHost.Visibility = Visibility.Collapsed; });
        _netMonitor?.Stop();
        _weather?.Stop();
        MirrorPanelComponent.StopCamera();
        if (revealCollapsed)
        {
            CollapsedPanel.Visibility = Visibility.Visible;
            OrbHost.Visibility = Visibility.Visible;
            Fade(CollapsedPanel, 1, 160);
            Fade(OrbHost, 1, 160);
        }
        UpdateIslandContent();
    }

    private string _currentTitle = "";
    private bool _settingsApplied;
    private bool _titleMarqueeOpen;

    private void UpdateIslandContent()
    {
        UpdateCollapsed();
        var m = _lastMedia;
        // No placeholder text: without a media session the idle layout is shown instead.
        string title = m is not null && !string.IsNullOrWhiteSpace(m.Title) ? m.Title : (m?.SourceAppName ?? "");
        UpdateTitleScroll(title);

        HoverArtist.Text = m is not null && !string.IsNullOrWhiteSpace(m.Artist) ? m.Artist : (m?.Album ?? "");

        var sessions = App.Media?.GetSessions();
        string appName = m?.SourceAppName ?? "";
        SourceChipText.Text = sessions is not null && sessions.Count > 1 ? $"{appName} ▾" : appName;
        SourceChip.Cursor = sessions is not null && sessions.Count > 1 ? Cursors.Hand : Cursors.Arrow;
        SourceChip.Visibility = string.IsNullOrWhiteSpace(appName) ? Visibility.Collapsed : Visibility.Visible;

        bool hasMedia = m is not null && !string.IsNullOrWhiteSpace(m.Title);
        bool playing = m?.IsPlaying == true;
        HoverPlayGlyph.Text = playing ? IcPause : IcPlay;

        UpdateTimeline();
        UpdateLyricsOnMediaChanged(m);
        UpdateVolumeControls();

        Color dominant = m?.DominantColor ?? Color.FromRgb(0x0A, 0x84, 0xFF);
        HoverEq.ReduceMotion = _cfg.ReduceMotion;
        HoverEq.AccentColor = dominant;
        HoverEq.Running = playing;
        HoverEq.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;

        if (playing && _islandState != IslandVisualState.Collapsed)
            _spectrum.Start();
        else
            _spectrum.Stop();

        LyricText.Foreground = new SolidColorBrush(Color.FromArgb(255,
            (byte)(dominant.R * 0.45 + 255 * 0.55),
            (byte)(dominant.G * 0.45 + 255 * 0.55),
            (byte)(dominant.B * 0.45 + 255 * 0.55)));

        // Glow / radial tint: Auto = album colour with music, time-of-day accent when idle;
        // Time of day = always the time-of-day colour; Fixed = the chosen colour.
        Color glow = _cfg.AccentMode switch
        {
            AccentMode.Fixed => FixedAccent(),
            AccentMode.TimeOfDay => IdleClock.Accent(_todHue),
            _ => _idleMode ? IdleClock.Accent(_todHue) : dominant,
        };
        AlbumRadialGlowStop.Color = Color.FromArgb(38, glow.R, glow.G, glow.B);
        AlbumRadialGlow.Opacity = _islandState is IslandVisualState.Expanded or IslandVisualState.Detail ? 1.0 : 0.0;
        PillShadow.Color = _islandState != IslandVisualState.Collapsed ? glow : Colors.Black;

        ShowDisc(m);
        IconRow.ReduceMotion = _cfg.ReduceMotion;
        UpdateIconRowValues();
        if (_idleMode && _islandState != IslandVisualState.Collapsed) UpdateIdleOpen(DateTime.Now);
    }

    private void UpdateTitleScroll(string title)
    {
        bool open = _islandState is IslandVisualState.Expanded or IslandVisualState.Detail;
        if (_currentTitle == title && HoverTitle.Text == title && open == _titleMarqueeOpen) return;
        _currentTitle = title;
        _titleMarqueeOpen = open;
        HoverTitle.Text = title;
        // Measure after the layout pass: when the island is just opening the box width is stale.
        if (!_titleMarqueeQueued)
        {
            _titleMarqueeQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ApplyTitleMarquee);
        }
    }

    private bool _titleMarqueeQueued;

    /// <summary>Static title when it fits; otherwise a slow back-and-forth marquee while the island
    /// is open, or an ellipsis (collapsed / Reduce Motion).</summary>
    private void ApplyTitleMarquee()
    {
        _titleMarqueeQueued = false;
        bool open = _titleMarqueeOpen;
        HoverTitle.MaxWidth = double.PositiveInfinity;
        HoverTitle.TextTrimming = TextTrimming.None;
        HoverTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textW = HoverTitle.DesiredSize.Width;
        double boxW = TitleContainer.ActualWidth > 0 ? TitleContainer.ActualWidth : 242;

        if (textW <= boxW)
        {
            HoverTitleTransform.BeginAnimation(TranslateTransform.XProperty, null);
            HoverTitleTransform.X = 0;
        }
        else
        {
            if (_cfg.ReduceMotion || !open)
            {
                HoverTitleTransform.BeginAnimation(TranslateTransform.XProperty, null);
                HoverTitleTransform.X = 0;
                HoverTitle.MaxWidth = boxW;   // inside a Canvas the trimming needs an explicit width
                HoverTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            }
            else
            {
                double delta = textW - boxW + 8;
                var anim = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromSeconds(10),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.14)));
                anim.KeyFrames.Add(new SplineDoubleKeyFrame(-delta, KeyTime.FromPercent(0.46), new KeySpline(0.4, 0, 0.2, 1)));
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(-delta, KeyTime.FromPercent(0.64)));
                anim.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromPercent(0.96), new KeySpline(0.4, 0, 0.2, 1)));
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1.0)));

                HoverTitleTransform.BeginAnimation(TranslateTransform.XProperty, anim);
            }
        }
    }

    private void UpdateTimeline()
    {
        if (_isScrubbing) return;
        var m = _lastMedia;
        if (m is null || m.Duration <= TimeSpan.Zero)
        {
            HoverProgressContainer.Visibility = Visibility.Collapsed;
            return;
        }

        HoverProgressContainer.Visibility = Visibility.Visible;

        TimeSpan pos = m.Position;
        if (m.IsPlaying && m.LastUpdatedTime.HasValue)
        {
            var el = DateTimeOffset.UtcNow - m.LastUpdatedTime.Value;
            if (el > TimeSpan.Zero) pos += el;
        }
        pos = TimeSpan.FromTicks(Math.Clamp(pos.Ticks, 0, m.Duration.Ticks));

        HoverTimeElapsed.Text = FormatTime(pos);
        TimeSpan rem = m.Duration - pos;
        HoverTimeRemaining.Text = $"-{FormatTime(rem)}";

        double trackW = HoverProgressBarTrack.ActualWidth;
        if (trackW > 0)
        {
            double frac = pos.TotalSeconds / m.Duration.TotalSeconds;
            HoverProgressFill.Width = Math.Clamp(frac * trackW, 0, trackW);
            if (HoverProgressKnob.Visibility == Visibility.Visible)
            {
                HoverProgressKnob.Margin = new Thickness(Math.Clamp(frac * trackW - 5, -5, trackW - 5), 0, 0, 0);
            }
        }
    }

    private void UpdateLyricsOnMediaChanged(MediaInfo? m)
    {
        string trackKey = $"{m?.Artist}|{m?.Title}|{(int)(m?.Duration.TotalSeconds ?? 0)}";
        if (trackKey != _lastLyricTrackKey)
        {
            _lastLyricTrackKey = trackKey;
            _currentLrcLines = null;
            _currentLyricLine = null;
            LyricText.Text = "";
            LyricText.Opacity = 0;

            if (!string.IsNullOrWhiteSpace(m?.Artist) && !string.IsNullOrWhiteSpace(m?.Title) && m.Duration > TimeSpan.Zero)
            {
                var cts = new CancellationTokenSource();
                _lrcCts?.Cancel();
                _lrcCts = cts;
                _ = Task.Run(async () =>
                {
                    var lines = await _lrcClient.FetchLyricsAsync(m.Artist, m.Title, m.Duration, cts.Token);
                    if (!cts.IsCancellationRequested && lines is not null)
                    {
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            _currentLrcLines = lines;
                            UpdateLyricDisplay();
                        });
                    }
                });
            }
        }
        else
        {
            UpdateLyricDisplay();
        }
    }

    private void UpdateLyricDisplay()
    {
        if (_volumeToastActive) return;

        TimeSpan pos = _lastMedia?.Position ?? TimeSpan.Zero;
        if (_lastMedia?.IsPlaying == true && _lastMedia.LastUpdatedTime.HasValue)
        {
            var el = DateTimeOffset.UtcNow - _lastMedia.LastUpdatedTime.Value;
            if (el > TimeSpan.Zero) pos += el;
        }

        string? line = LrcLibClient.GetCurrentLineText(_currentLrcLines, pos);
        if (line != _currentLyricLine)
        {
            _currentLyricLine = line;
            if (string.IsNullOrWhiteSpace(line))
            {
                LyricText.Text = "";
                LyricText.Opacity = 0;
            }
            else
            {
                LyricText.Text = line;
                if (_cfg.ReduceMotion)
                {
                    LyricText.Opacity = 1;
                    LyricTransform.Y = 0;
                }
                else
                {
                    LyricTransform.Y = 4;
                    LyricText.Opacity = 0;
                    _anim.Animate("lyricSlide", 4, 0, v => LyricTransform.Y = v);
                    _anim.Animate("lyricFade", 0, 1, v => LyricText.Opacity = v);
                }
            }
        }
    }

    private void OnTimelineTick()
    {
        UpdateTimeline();
        UpdateLyricDisplay();
    }

    /// <summary>The 250 ms progress/lyrics timer is only useful while the open island shows a
    /// media session; it never runs collapsed or idle.</summary>
    private void SyncTimelineTimer()
    {
        bool want = IsVisible && !_idleMode && _islandState is IslandVisualState.Expanded or IslandVisualState.Detail;
        if (want == _timelineTimer.IsEnabled) return;
        if (want) _timelineTimer.Start(); else _timelineTimer.Stop();
    }

    private void UpdateVolumeControls()
    {
        if (App.Volume is null) return;
        _syncingHoverVol = true;
        try
        {
            if (!HoverVolumeSlider.IsMouseCaptureWithin)
                HoverVolumeSlider.Value = App.Volume.Level;
            bool muted = App.Volume.Muted || App.Volume.Level <= 0.001f;
            HoverSpeakerGlyph.Text = muted ? "\uE74F" : "\uE767";
        }
        finally { _syncingHoverVol = false; }
    }

    private void OnVolumeGroupMouseEnter(object sender, MouseEventArgs e)
    {
        if (_cfg.ReduceMotion)
        {
            HoverVolumeSliderHost.Width = 74;
            HoverVolumeSliderHost.Opacity = 1;
            return;
        }
        _anim.Animate("volSliderW", HoverVolumeSliderHost.ActualWidth, 74, v => HoverVolumeSliderHost.Width = v);
        _anim.Animate("volSliderOp", HoverVolumeSliderHost.Opacity, 1, v => HoverVolumeSliderHost.Opacity = v);
    }

    private void OnVolumeGroupMouseLeave(object sender, MouseEventArgs e)
    {
        if (HoverVolumeSlider.IsFocused || HoverVolumeSlider.IsKeyboardFocusWithin || HoverVolumeSlider.IsMouseCaptureWithin) return;
        if (_cfg.ReduceMotion)
        {
            HoverVolumeSliderHost.Width = 0;
            HoverVolumeSliderHost.Opacity = 0;
            return;
        }
        _anim.Animate("volSliderW", HoverVolumeSliderHost.ActualWidth, 0, v => HoverVolumeSliderHost.Width = v);
        _anim.Animate("volSliderOp", HoverVolumeSliderHost.Opacity, 0, v => HoverVolumeSliderHost.Opacity = v);
    }

    private void OnHoverVolumeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingHoverVol) return;
        if (App.Volume is not null)
        {
            float lvl = (float)e.NewValue;
            if (lvl > 0 && App.Volume.Muted) App.Volume.ToggleMute();
            App.Volume.SetLevel(lvl);
        }
    }

    private void OnHoverSpeakerClick(object sender, RoutedEventArgs e)
    {
        App.Volume?.ToggleMute();
        UpdateVolumeControls();
    }

    private void OnDiscMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (App.Volume is null) return;
        float delta = e.Delta > 0 ? 0.03f : -0.03f;
        float next = Math.Clamp(App.Volume.Level + delta, 0f, 1f);
        App.Volume.SetLevel(next);
        ShowVolumeToast($"Volume {Math.Round(next * 100)}%");
        e.Handled = true;
    }

    private void ShowVolumeToast(string message)
    {
        _volumeToastActive = true;
        LyricText.Text = message;
        LyricText.Opacity = 1;
        LyricTransform.Y = 0;

        _volumeToastTimer ??= new DispatcherTimer();
        _volumeToastTimer.Interval = TimeSpan.FromSeconds(2.6);
        _volumeToastTimer.Tick -= OnVolumeToastTick;
        _volumeToastTimer.Tick += OnVolumeToastTick;
        _volumeToastTimer.Stop();
        _volumeToastTimer.Start();
    }

    private void OnVolumeToastTick(object? sender, EventArgs e)
    {
        _volumeToastTimer?.Stop();
        _volumeToastActive = false;
        UpdateLyricDisplay();
    }

    private void OnSourceChipClick(object sender, RoutedEventArgs e)
    {
        var sessions = App.Media?.GetSessions();
        if (sessions is not null && sessions.Count > 1)
        {
            App.Media?.CycleSession();
        }
    }

    private void OnDiscClicked(object sender, MouseButtonEventArgs e)
    {
        string? appId = App.Media?.CurrentAppId;
        if (string.IsNullOrWhiteSpace(appId)) return;

        Task.Run(() =>
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(appId);
                if (name.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) name = "Spotify";
                else if (name.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) name = "chrome";
                else if (name.Contains("Edge", StringComparison.OrdinalIgnoreCase)) name = "msedge";

                var procs = Process.GetProcessesByName(name);
                if (procs.Length == 0)
                {
                    procs = Process.GetProcesses().Where(p => p.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                }

                foreach (var p in procs)
                {
                    IntPtr hwnd = p.MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                    {
                        NativeMethods.ShowWindow(hwnd, 9); // SW_RESTORE
                        NativeMethods.SetForegroundWindow(hwnd);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Bring source app forward failed", ("error", ex.Message));
            }
        });
    }

    private void OnHoverProgressMouseEnter(object sender, MouseEventArgs e)
    {
        if (_lastMedia?.CanSeek == true)
            HoverProgressKnob.Visibility = Visibility.Visible;
    }

    private void OnHoverProgressMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isScrubbing)
        {
            HoverProgressKnob.Visibility = Visibility.Collapsed;
            HoverProgressTooltip.Visibility = Visibility.Collapsed;
        }
    }

    private void OnHoverProgressMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_lastMedia is null || !_lastMedia.CanSeek || _lastMedia.Duration <= TimeSpan.Zero) return;
        _isScrubbing = true;
        HoverProgressBarTrack.CaptureMouse();
        UpdateScrubFromPoint(e.GetPosition(HoverProgressBarTrack).X);
    }

    private void OnHoverProgressMouseMove(object sender, MouseEventArgs e)
    {
        if (_lastMedia is null || _lastMedia.Duration <= TimeSpan.Zero) return;
        double x = e.GetPosition(HoverProgressBarTrack).X;
        double trackW = HoverProgressBarTrack.ActualWidth;
        if (trackW <= 0) return;

        double frac = Math.Clamp(x / trackW, 0.0, 1.0);
        var hoverTime = TimeSpan.FromSeconds(_lastMedia.Duration.TotalSeconds * frac);

        if (_lastMedia.CanSeek)
        {
            HoverProgressTooltipText.Text = FormatTime(hoverTime);
            HoverProgressTooltip.Margin = new Thickness(Math.Clamp(x - 16, 0, Math.Max(0, trackW - 32)), -24, 0, 0);
            HoverProgressTooltip.Visibility = Visibility.Visible;
        }

        if (_isScrubbing)
        {
            UpdateScrubFromPoint(x);
        }
    }

    private void OnHoverProgressMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isScrubbing) return;
        _isScrubbing = false;
        HoverProgressBarTrack.ReleaseMouseCapture();
        HoverProgressTooltip.Visibility = Visibility.Collapsed;
        if (!HoverProgressContainer.IsMouseOver)
            HoverProgressKnob.Visibility = Visibility.Collapsed;

        if (_lastMedia is not null && _lastMedia.CanSeek)
        {
            _ = App.Media?.SeekToTimeAsync(_scrubTime);
        }
    }

    private void UpdateScrubFromPoint(double x)
    {
        if (_lastMedia is null || _lastMedia.Duration <= TimeSpan.Zero) return;
        double trackW = HoverProgressBarTrack.ActualWidth;
        if (trackW <= 0) return;

        double frac = Math.Clamp(x / trackW, 0.0, 1.0);
        _scrubTime = TimeSpan.FromSeconds(_lastMedia.Duration.TotalSeconds * frac);
        HoverProgressFill.Width = frac * trackW;
        HoverProgressKnob.Margin = new Thickness(Math.Clamp(frac * trackW - 5, -5, trackW - 5), 0, 0, 0);
        HoverProgressKnob.Visibility = Visibility.Visible;
        HoverTimeElapsed.Text = FormatTime(_scrubTime);
        HoverTimeRemaining.Text = $"-{FormatTime(_lastMedia.Duration - _scrubTime)}";
    }

    private void OnBarPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!Pill.IsMouseOver && !IsKeyboardFocusWithin) return;
        if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is Slider) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key is Key.Left or Key.Right
            && _islandState is IslandVisualState.Expanded or IslandVisualState.Detail)
        {
            if (e.Key == Key.Right) IconRow.SetPage(IconRow.Page + 1); else IconRow.SetPage(IconRow.Page - 1);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                _ = App.Media?.PlayPauseAsync();
                e.Handled = true;
                break;
            case Key.Left:
                SeekRelative(-5);
                e.Handled = true;
                break;
            case Key.Right:
                SeekRelative(+5);
                e.Handled = true;
                break;
            case Key.Up:
                AdjustVolume(0.05f);
                e.Handled = true;
                break;
            case Key.Down:
                AdjustVolume(-0.05f);
                e.Handled = true;
                break;
            case Key.N:
                _ = App.Media?.NextAsync();
                e.Handled = true;
                break;
            case Key.P:
                _ = App.Media?.PreviousAsync();
                e.Handled = true;
                break;
        }
    }

    private void SeekRelative(double seconds)
    {
        if (_lastMedia is null || !_lastMedia.CanSeek || _lastMedia.Duration <= TimeSpan.Zero) return;
        TimeSpan target = _lastMedia.Position + TimeSpan.FromSeconds(seconds);
        target = TimeSpan.FromTicks(Math.Clamp(target.Ticks, 0, _lastMedia.Duration.Ticks));
        _ = App.Media?.SeekToTimeAsync(target);
    }

    private void AdjustVolume(float delta)
    {
        if (App.Volume is null) return;
        float next = Math.Clamp(App.Volume.Level + delta, 0f, 1f);
        App.Volume.SetLevel(next);
        ShowVolumeToast($"Volume {Math.Round(next * 100)}%");
    }

    private void CheckWarningStrip()
    {
        // Telemetry only samples while the island is open; a stale snapshot must not keep
        // counting towards a RAM/CPU warning.
        var snap = _telemetry is { } t && DateTime.UtcNow - t.Timestamp < TimeSpan.FromSeconds(3) ? t : null;
        double ram = snap is not null && snap.Memory.Available ? snap.Memory.UsedPercent : -1;
        double cpu = snap is not null && snap.Cpu.Available ? snap.Cpu.Utilization : -1;

        // Battery values are cached from power notifications (no polling here).
        float batteryPct = _hasBattery ? (float)_battPct : -1;
        bool isCharging = _battOnAc;

        bool shouldShow = _warningLogic.Update(ram, cpu, batteryPct, isCharging, out string kind);
        if (shouldShow == _warningStripActive && kind == _warningKind)
        {
            // No state change: only the battery percentage text can move while the strip is up.
            if (shouldShow && kind == "Battery")
            {
                string live = $"Battery at {Math.Round(batteryPct):F0}%";
                if (WarningStripText.Text != live) WarningStripText.Text = live;
            }
            return;
        }
        string text = "";

        if (shouldShow)
        {
            if (kind == "RAM")
            {
                var topProc = GetTopMemoryProcess();
                string procStr = topProc is not null ? $" · {topProc.Value.Item1} is using {topProc.Value.Item2}" : "";
                text = $"RAM at {Math.Round(ram):F0}%{procStr}";
            }
            else if (kind == "CPU")
            {
                var top = snap?.TopProcesses is { Count: > 0 } tp ? tp[0] : null;
                string procStr = top is not null ? $" · {top.Name} at {top.CpuPercent:F0}%" : "";
                text = $"CPU at {Math.Round(cpu):F0}%{procStr}";
            }
            else if (kind == "Battery")
            {
                text = $"Battery at {Math.Round(batteryPct):F0}%";
            }
        }

        if (shouldShow != _warningStripActive || kind != _warningKind)
        {
            _warningStripActive = shouldShow;
            _warningKind = kind;
            WarningStripText.Text = text;

            if (shouldShow)
            {
                // While the search bar is up the strip waits; FinishSearchClose brings it back.
                if (_islandState != IslandVisualState.Search && _searchPhase == SearchPhase.Closed)
                {
                    WarningStrip.Visibility = Visibility.Visible;
                    Fade(WarningStrip, 1, 200);
                }
            }
            else
            {
                Fade(WarningStrip, 0, 150, () => WarningStrip.Visibility = Visibility.Collapsed);
            }

            // The island grows by the strip's 40 px (collapsed pill included) and shrinks back.
            CollapsedPanel.Margin = new Thickness(0, 0, 0, shouldShow ? 40 : 0);
            if (_islandState != IslandVisualState.Collapsed) AnimateIsland(_currentW, MeasureIslandHeight());
            else if (!_expanded) AnimateIsland(CollapsedW, CollapsedH);
        }
    }

    private static (string, string)? GetTopMemoryProcess()
    {
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (InvalidOperationException) { return null; }
        try
        {
            Process? top = null;
            foreach (var p in all)
                if (top is null || p.WorkingSet64 > top.WorkingSet64) top = p;
            return top is null ? null : (top.ProcessName + ".exe", FormatBytes(top.WorkingSet64));
        }
        finally { foreach (var p in all) p.Dispose(); }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{(double)bytes / (1024L * 1024 * 1024):F1} GB";
        if (bytes >= 1024L * 1024)
            return $"{(double)bytes / (1024L * 1024):F0} MB";
        return $"{bytes / 1024} KB";
    }

    private void OnWarningReviewClick(object sender, RoutedEventArgs e)
    {
        if (_warningKind == "RAM") SetIslandState(IslandVisualState.Detail, IslandMetric.Ram, force: true);
        else if (_warningKind == "CPU") SetIslandState(IslandVisualState.Detail, IslandMetric.Cpu, force: true);
        else if (_warningKind == "Battery")
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:batterysaver") { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception ex) { Log.Warning("Battery settings could not open", ("error", ex.Message)); }
        }
    }

    private void OnWarningCloseClick(object sender, RoutedEventArgs e)
    {
        _warningLogic.Dismiss(_warningKind);

        _warningStripActive = false;
        Fade(WarningStrip, 0, 150, () => WarningStrip.Visibility = Visibility.Collapsed);
        CollapsedPanel.Margin = new Thickness(0);
        if (_islandState != IslandVisualState.Collapsed) AnimateIsland(_currentW, MeasureIslandHeight());
        else if (!_expanded) AnimateIsland(CollapsedW, CollapsedH);
    }

    private void UpdateIconRowValues()
    {
        var snap = _telemetry;
        double cpu = snap is not null && snap.Cpu.Available ? snap.Cpu.Utilization : -1;
        var gpus = App.Telemetry?.SnapshotGpuTracks();
        double gpu = gpus is { Count: > 0 } ? gpus[0].Utilization : -1;
        double ram = snap is not null && snap.Memory.Available ? snap.Memory.UsedPercent : -1;
        IconRow.UpdateValues(cpu, gpu, ram);
        IconRow.SetAvailability(cpu >= 0, gpu >= 0, ram >= 0);
        UpdateNetIcon();
        UpdateWeatherIcon();
        UpdatePage2Rings();
    }

    /// <summary>Clip: fill = history size / limit, label = count. Toggles: no arc, label = number
    /// on. Timer: arc = progress, label = time left or "off". Values come only from real state.</summary>
    private void UpdatePage2Rings()
    {
        var t = _lastTimer;
        if (t is { State: TimerState.Running or TimerState.Paused } && t.Remaining > TimeSpan.Zero)
            IconRow.SetRing(IslandMetric.Timer, t.Progress * 100, IdleClock.FormatCountdown(t.Remaining));
        else
            IconRow.SetRing(IslandMetric.Timer, -1, "off");
        UpdateClipRing();
        UpdateTogglesRing();
    }

    // ---------------------------------------------------------------- clipboard history

    private IntPtr _clipPasteTarget;
    private bool _clipByHotkey;

    private void HookClipboardPanel(bool on)
    {
        var p = ClipboardPanelComponent;
        if (on)
        {
            p.ItemChosen += OnClipItemChosen;
            p.PinRequested += OnClipPin;
            p.DeleteRequested += OnClipDelete;
            p.ClearRequested += OnClipClear;
            p.PauseRequested += OnClipPause;
            p.CloseRequested += CloseClipboardPanel;
            if (App.ClipHistory is not null) App.ClipHistory.Changed += OnClipHistoryChanged;
        }
        else
        {
            p.ItemChosen -= OnClipItemChosen;
            p.PinRequested -= OnClipPin;
            p.DeleteRequested -= OnClipDelete;
            p.ClearRequested -= OnClipClear;
            p.PauseRequested -= OnClipPause;
            p.CloseRequested -= CloseClipboardPanel;
            if (App.ClipHistory is not null) App.ClipHistory.Changed -= OnClipHistoryChanged;
        }
    }

    private void UpdateClipRing()
    {
        var c = App.ClipHistory;
        if (c is null || !c.Enabled) { IconRow.SetRing(IslandMetric.Clipboard, -1, "off"); return; }
        int n = c.Items.Count(i => !i.Pinned);
        IconRow.SetRing(IslandMetric.Clipboard, n * 100.0 / Math.Max(1, c.Limit), c.Paused ? "pause" : n.ToString(CultureInfo.InvariantCulture));
    }

    private void RenderClipboard()
    {
        var c = App.ClipHistory;
        ClipboardPanelComponent.Render(c?.Items ?? Array.Empty<WindowsDynamicBar.Core.Clipboard.ClipItem>(),
            c?.Limit ?? _cfg.ClipboardLimit, c?.Paused ?? false, c?.Enabled ?? false);
    }

    private void OnClipHistoryChanged()
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (IslandExpandedHost.Visibility != Visibility.Visible) return;
                UpdateClipRing();
                if (_islandState == IslandVisualState.Detail && _activeMetric == IslandMetric.Clipboard) RenderClipboard();
            });
        }
        catch (TaskCanceledException) { /* dispatcher shutting down */ }
    }

    /// <summary>Hotkey (or debug/test): open the island directly on the clipboard card. Only the
    /// hotkey takes keyboard focus (the search box); hover/click never steals focus.</summary>
    private void OpenClipboardPanel(bool byHotkey)
    {
        if (App.ClipHistory is not { Enabled: true }) return;
        if (_islandState == IslandVisualState.Search) CloseSearch();
        if (!IsVisible && _cfg.BarEnabled) Show();
        _clipByHotkey = byHotkey;
        _clipPasteTarget = byHotkey ? NativeMethods.GetForegroundWindow() : IntPtr.Zero;
        if (_clipPasteTarget == _hwnd) _clipPasteTarget = IntPtr.Zero;
        ClipboardPanelComponent.ResetQuery();
        int page = IconRow.PageOf(IslandMetric.Clipboard);
        if (page >= 0) IconRow.SetPage(page, instant: true);
        _pinned = true;
        SetIslandState(IslandVisualState.Detail, IslandMetric.Clipboard, force: true);
        if (byHotkey)
        {
            Activate();
            // The card was just made visible: focus its search box after this layout pass.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (_activeMetric == IslandMetric.Clipboard) ClipboardPanelComponent.FocusSearch();
            });
        }
    }

    private void CloseClipboardPanel()
    {
        bool byHotkey = _clipByHotkey;
        IntPtr back = _clipPasteTarget;
        _clipByHotkey = false;
        _clipPasteTarget = IntPtr.Zero;
        _pinned = false;
        SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);
        if (byHotkey && back != IntPtr.Zero) WindowActivation.BringToFront(back);
    }

    private void OnClipItemChosen(WindowsDynamicBar.Core.Clipboard.ClipItem item)
    {
        var c = App.ClipHistory;
        if (c is null) return;
        // The window that was in front when the hotkey opened the card.
        IntPtr origin = _clipPasteTarget;
        bool byHotkey = _clipByHotkey;
        _clipPasteTarget = IntPtr.Zero;
        _clipByHotkey = false;
        _pinned = false;
        SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);

        bool paste = _cfg.ClipboardPasteOnSelect && origin != IntPtr.Zero;
        string? error = c.Restore(item, paste ? origin : IntPtr.Zero);   // paste: focus restored, then Ctrl+V
        if (error is not null) { ShowActionSuccess(error); return; }
        if (!paste)
        {
            ShowActionSuccess("Copied");
            if (byHotkey && origin != IntPtr.Zero) WindowActivation.BringToFront(origin);
        }
    }

    private void OnClipPin(long id, bool pinned) => App.ClipHistory?.SetPinned(id, pinned);
    private void OnClipDelete(long id) => App.ClipHistory?.Remove(id);
    private void OnClipClear() => App.ClipHistory?.ClearAll();
    private void OnClipPause(bool paused) => App.ClipHistory?.SetPaused(paused);

    private void UpdateTogglesRing()
    {
        // No arc for toggles: the label is the number of toggles that are on.
        var t = App.Toggles;
        IconRow.SetRing(IslandMetric.Toggles, -1, t is null ? "" : $"{t.OnCount} on");
    }

    // ---------------------------------------------------------------- quick toggles

    private string _toggleStatus = "";
    private QuickToggleService? _hookedToggles;

    private void OnServiceToggled()
    {
        if (_hookedToggles is not null) _hookedToggles.Changed -= OnTogglesChanged;
        _hookedToggles = App.Toggles;
        if (_hookedToggles is not null) _hookedToggles.Changed += OnTogglesChanged;
    }

    private void RenderToggles()
    {
        var t = App.Toggles;
        TogglesPanelComponent.ReduceMotion = _cfg.ReduceMotion;
        TogglesPanelComponent.Render(t?.States ?? Array.Empty<ToggleState>(), _toggleStatus);
    }

    /// <summary>A toggle changed (here or elsewhere, from OS events): refresh ring + panel.</summary>
    private void OnTogglesChanged()
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (IslandExpandedHost.Visibility != Visibility.Visible) return;
                UpdateTogglesRing();
                if (_islandState == IslandVisualState.Detail && _activeMetric == IslandMetric.Toggles) RenderToggles();
            });
        }
        catch (TaskCanceledException) { /* dispatcher shutting down */ }
    }

    private async void OnToggleRequested(ToggleId id, bool on)
    {
        var svc = App.Toggles;
        if (svc is null) return;
        string? error = await svc.SetAsync(id, on);
        // On failure the old (re-read) state stays and the real error is shown once.
        string label = svc.States.FirstOrDefault(s => s.Id == id)?.Label ?? id.ToString();
        _toggleStatus = error is null ? $"{label} {(on ? "on" : "off")}" : $"{label}: {error}";
        if (_islandState == IslandVisualState.Detail && _activeMetric == IslandMetric.Toggles) RenderToggles();
        UpdateTogglesRing();
    }

    private void UpdateWeatherIcon()
    {
        var snap = _weather?.Snapshot;
        if (snap is null || !snap.Available)
        {
            IconRow.UpdateWeather(-1, "", Color.FromRgb(0xE8, 0xB0, 0x4B));
            return;
        }

        // Ring shows the temperature mapped over a -10..40 degC window; colour follows the temp.
        double frac = Math.Clamp((snap.TempC + 10) / 50.0, 0, 1);
        IconRow.UpdateWeather(frac * 100, $"{Math.Round(snap.TempC):F0}\u00b0", WeatherAccent(snap.TempC));
    }

    private static Color WeatherAccent(double tempC)
    {
        double n = Math.Clamp((tempC + 10) / 50.0, 0, 1);
        var cold = Color.FromRgb(0x6F, 0xA8, 0xDC);
        var warm = Color.FromRgb(0xE8, 0xB0, 0x4B);
        return n < 0.5
            ? Lerp(cold, warm, n * 2)
            : Lerp(warm, Color.FromRgb(0xC9, 0x60, 0x4A), (n - 0.5) * 2);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private void UpdateNetIcon()
    {
        var monitor = _netMonitor;
        if (monitor is null) { IconRow.UpdateNet(-1, ""); return; }

        var test = monitor.TestState;
        bool running = test.Phase is NetTestPhase.Ping or NetTestPhase.Download or NetTestPhase.Upload;

        if (running)
            IconRow.UpdateNet(test.ProgressPercent, $"{Math.Round(test.ProgressPercent):F0}%");
        else if (test.DownMbps is double down)
            IconRow.UpdateNet(Math.Clamp(down / NetworkMonitor.GraphMaxMbps * 100, 0, 100), ShortRate(down));
        else
            IconRow.UpdateNet(-1, "");
    }

    private static string ShortRate(double mbps)
        => mbps >= 1000 ? $"{mbps / 1000.0:F1}G" : $"{Math.Round(mbps):F0}M";

    private void EnsureNetMonitor()
    {
        if (_netMonitor is not null) return;
        _netMonitor = new NetworkMonitor(App.Settings);
        _netMonitor.Updated += OnNetUpdated;
        NetPanelComponent.ReduceMotion = _cfg.ReduceMotion;
        _netMonitor.Start();
    }

    private void OnNetUpdated()
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (IslandExpandedHost.Visibility != Visibility.Visible) return;
                UpdateNetIcon();
                if (_islandState == IslandVisualState.Detail && _activeMetric == IslandMetric.Net && _netMonitor is not null)
                    NetPanelComponent.Render(_netMonitor);
            });
        }
        catch { }
    }

    private void OnNetTestToggle()
    {
        if (_netMonitor is null || !_cfg.FeatureSpeedTest) return;
        if (_netMonitor.TestRunning) _netMonitor.CancelSpeedTest();
        else _netMonitor.StartSpeedTest();
    }

    private void EnsureWeather()
    {
        if (_weather is not null) return;
        _weather = new WeatherService(App.Settings);
        _weather.Updated += OnWeatherUpdated;
        WeatherPanelComponent.ReduceMotion = _cfg.ReduceMotion;
        _weather.Start();
    }

    private void OnWeatherUpdated()
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (IslandExpandedHost.Visibility == Visibility.Visible) UpdateWeatherIcon();
                if (_islandState == IslandVisualState.Detail && _activeMetric == IslandMetric.Weather && _weather is not null)
                    WeatherPanelComponent.Render(_weather.Snapshot);
            });
        }
        catch { }
    }

    private void UpdatePanelContent()
    {
        if (_activeMetric == IslandMetric.Mirror)
        {
            // The mirror panel owns its own rendering (camera frames); nothing to push here.
            UpdateIconRowValues();
            return;
        }

        if (_activeMetric == IslandMetric.Net)
        {
            EnsureNetMonitor();
            if (_netMonitor is not null) NetPanelComponent.Render(_netMonitor);
            UpdateIconRowValues();
            return;
        }

        if (_activeMetric == IslandMetric.Toggles)
        {
            RenderToggles();
            UpdateIconRowValues();
            return;
        }

        if (_activeMetric == IslandMetric.Clipboard)
        {
            RenderClipboard();
            UpdateIconRowValues();
            return;
        }

        if (_activeMetric == IslandMetric.Timer)
        {
            TimerPanelComponent.Render(_lastTimer ?? App.Timer?.Current);
            UpdateIconRowValues();
            return;
        }

        if (_activeMetric == IslandMetric.Weather)
        {
            EnsureWeather();
            if (_weather is not null) WeatherPanelComponent.Render(_weather.Snapshot);
            UpdateIconRowValues();
            return;
        }

        RenderTelemetry(DetailPanelComponent, _activeMetric);
        UpdateIconRowValues();
    }

    private bool IsPointerOverIsland()
    {
        try
        {
            if (!IsVisible || Pill == null || !Pill.IsVisible) return false;
            if (Pill.IsMouseOver) return true;

            if (NativeMethods.GetCursorPos(out var pt))
            {
                var screenPoint = Pill.PointToScreen(new Point(0, 0));
                double dpi = CurrentDpi();
                double left = screenPoint.X;
                double top = screenPoint.Y;
                double right = left + Pill.ActualWidth * dpi;
                double bottom = top + Pill.ActualHeight * dpi;

                return pt.X >= (left - 4) && pt.X <= (right + 4) &&
                       pt.Y >= (top - 4) && pt.Y <= (bottom + 4);
            }
        }
        catch { }
        return false;
    }

    private void OnHoverCheckTick(object? sender, EventArgs e)
    {
        if (_islandState == IslandVisualState.Search || _islandState == IslandVisualState.Collapsed)
        {
            _hoverCheckTimer.Stop();
            return;
        }
        if (_expanded) return;
        if (_pinned && _islandState == IslandVisualState.Detail) return;

        bool pointerOver = IsPointerOverIsland();
        if (!pointerOver)
        {
            _islandHovering = false;
            if (!_collapseDelay.IsEnabled)
            {
                _collapseDelay.Stop();
                _collapseDelay.Start();
            }
        }
        else
        {
            _islandHovering = true;
            if (_collapseDelay.IsEnabled)
            {
                _collapseDelay.Stop();
            }
        }
    }

    /// <summary>Mouse entered the island region (pill + icons + panel).</summary>
    private void OnIslandMouseEnter(object sender, MouseEventArgs e)
    {
        if (_islandState == IslandVisualState.Search) return;
        _islandHovering = true;
        _collapseDelay.Stop();
        if (_expanded)
        {
            if (_shown?.Kind is BarEventKind.Volume or BarEventKind.Brightness)
            {
                UserDismiss(clearBus: true);
            }
            else
            {
                return; // interactive event-bus card owns the pill
            }
        }

        var (state, metric) = IslandStateMachine.Resolve(
            _islandState, _activeMetric, pointerInside: true, IslandMetric.None, collapseElapsed: false, _cfg.HoverPreview);
        SetIslandState(state, metric);
        if (_islandState == IslandVisualState.Expanded) _hoverCheckTimer.Start();
    }

    /// <summary>Mouse left the island region entirely (grace timer defers collapse).</summary>
    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
    {
        if (_islandState == IslandVisualState.Search) return;
        _islandHovering = false;
        if (_expanded) return;
        if (_pinned && _islandState == IslandVisualState.Detail) return;
        _collapseDelay.Stop();
        _collapseDelay.Start();
    }

    private void OnCollapseDelayTick(object? sender, EventArgs e)
    {
        if (_islandState == IslandVisualState.Search) return;
        _collapseDelay.Stop();
        if (_expanded || (_pinned && _islandState == IslandVisualState.Detail)) return;

        if (!IsPointerOverIsland())
        {
            _islandHovering = false;
        }

        // Still inside the island but no icon is hovered: close the extended panel but keep the
        // island in its hover (Expanded) form, since the pointer is still over the island.
        if (_islandHovering)
        {
            if (_islandState == IslandVisualState.Detail)
                SetIslandState(IslandVisualState.Expanded, IslandMetric.None);
            return;
        }

        if (App.Bus?.Active is not null) return;
        var (state, metric) = IslandStateMachine.Resolve(
            _islandState, _activeMetric, pointerInside: false, IslandMetric.None, collapseElapsed: true, _cfg.HoverPreview);
        SetIslandState(state, metric);
        try { if (App.Telemetry is { IsActive: true }) App.Telemetry.Deactivate(); } catch { }
    }

    /// <summary>The ring's panel switched off the visible page: close it, keep the island open.</summary>
    private void OnIconPageChanged(int page)
    {
        _collapseDelay.Stop();
        if (_islandState == IslandVisualState.Detail && !IconRow.IsOnVisiblePage(_activeMetric))
        {
            _pinned = false;
            SetIslandState(IslandVisualState.Expanded, IslandMetric.None, force: true);
            if (!IsPointerOverIsland())
            {
                _islandHovering = false;
                _collapseDelay.Stop();
                _collapseDelay.Start();
            }
            else
            {
                _hoverCheckTimer.Start();
            }
        }
        UpdateIconRowValues();
    }

    /// <summary>Metrics whose ring opens a detail panel (page-2 panels register here as they land).</summary>
    private static bool HasPanel(IslandMetric m) => m is IslandMetric.Cpu or IslandMetric.Gpu or IslandMetric.Ram
        or IslandMetric.Mirror or IslandMetric.Net or IslandMetric.Weather or IslandMetric.Toggles
        or IslandMetric.Clipboard or IslandMetric.Timer;

    private void OnIconMetricHovered(IslandMetric metric)
    {
        if (!_cfg.HoverPreview) return;
        if (_islandState == IslandVisualState.Search) return;
        if (!HasPanel(metric)) return;
        if (_expanded || _pinned) return;   // pinned panel ignores hover until clicked again
        _collapseDelay.Stop();
        SetIslandState(IslandVisualState.Detail, metric);
    }

    private void OnIconMetricUnhovered()
    {
        if (!_cfg.HoverPreview) return;
        if (_islandState == IslandVisualState.Search) return;
        if (_pinned) return;   // pinned panel ignores hover until clicked again
        // Leaving an icon closes the extended panel after a short grace (so moving between icons
        // still just switches metric). Hover only previews; use click to keep it open.
        _collapseDelay.Stop();
        _collapseDelay.Start();
    }

    private void OnIconMetricClicked(IslandMetric metric)
    {
        if (_islandState == IslandVisualState.Search) return;
        if (!HasPanel(metric)) return;
        // Mirror: a click toggles the camera (per privacy design). Click once to start and pin the
        // panel; click again to stop the camera and shrink back to the collapsed island.
        if (metric == IslandMetric.Mirror)
        {
            if (MirrorPanelComponent.IsCameraLive)
            {
                MirrorPanelComponent.StopCamera();
                _pinned = false;
                SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);
                return;
            }
            _pinned = true;
            SetIslandState(IslandVisualState.Detail, metric, force: true);
            MirrorPanelComponent.StartCamera();
            return;
        }

        // Click the active (pinned) icon again -> close the extended canvas.
        if (_pinned && _activeMetric == metric)
        {
            _pinned = false;
            SetIslandState(IslandVisualState.Expanded, IslandMetric.None, force: true);
            if (!IsPointerOverIsland())
            {
                _islandHovering = false;
                _collapseDelay.Stop();
                _collapseDelay.Start();
            }
            else
            {
                _hoverCheckTimer.Start();
            }
            return;
        }
        // Otherwise click opens the metric's canvas and pins it open.
        _pinned = true;
        SetIslandState(IslandVisualState.Detail, metric, force: true);
    }

    private void OnMirrorCameraStateChanged(bool live)
        => IconRow.UpdateMirror(live, live ? "LIVE" : "off");

    private void OnGpuCycleRequested()
    {
        var gpus = App.Telemetry?.SnapshotGpuTracks();
        int count = gpus?.Count ?? 0;
        if (count <= 1) return;
        _gpuIndex = (_gpuIndex + 1) % count;
        if (_islandState == IslandVisualState.Detail && _activeMetric == IslandMetric.Gpu)
        {
            UpdatePanelContent();
            FitIslandHeight();
        }
    }

    private void EnsureTelemetry()
    {
        try { if (App.Telemetry is { IsActive: false }) App.Telemetry.Activate(); } catch { }
    }

    // ---------------------------------------------------------------- events in

    private static bool IsTicking(BarEventKind kind) =>
        kind is BarEventKind.Media or BarEventKind.Timer or BarEventKind.Stopwatch
            or BarEventKind.Focus or BarEventKind.Download or BarEventKind.SystemMonitor;

    private BarEvent? _shown;

    private void OnBusEvent(BarEvent evt)
    {
        try
        {
            // Volume/brightness HUD switched off: those events never open the island.
            if (!_cfg.FeatureVolumeHud && evt.Kind is BarEventKind.Volume or BarEventKind.Brightness) return;

            // If the dynamic island is open (expanded music pill, detail telemetry panel, or hovered),
            // transient HUD events (volume, brightness) or media must NOT collapse or preempt the open island.
            if (_islandState != IslandVisualState.Collapsed || _islandHovering)
            {
                if (evt.Kind is BarEventKind.Volume or BarEventKind.Brightness)
                {
                    if (evt.Kind == BarEventKind.Volume)
                    {
                        UpdateVolumeControls();
                    }
                    return;
                }

                if (evt.Kind == BarEventKind.Media)
                {
                    UpdateIslandContent();
                    return;
                }

                if (evt.Kind is BarEventKind.Focus or BarEventKind.Timer)
                {
                    _shown = evt;   // the open island (timer card, ring, Focus button) shows the countdown
                    return;
                }

                // The search bar is never replaced by an incoming card.
                if (_islandState == IslandVisualState.Search)
                {
                    _shown = evt;
                    return;
                }
            }

            // If music is playing, starting or running a timer must not expand over or interfere with the music disc
            if ((evt.Kind is BarEventKind.Timer or BarEventKind.Focus) && IsMusicPlaying())
            {
                _shown = evt;
                UpdateCollapsed();
                return;
            }

            // Refresh in place when the same widget is already on screen.
            if (_shown?.Kind == evt.Kind && _expanded)
            {
                _shown = evt;
                RenderEvent(evt);
                if (evt.Kind is BarEventKind.Volume or BarEventKind.Brightness)
                    TriggerLevelNudge();
                return;
            }

            // Respect a recent manual dismissal of this same kind (no nagging).
            if (_userDismissed && _shown?.Kind == evt.Kind &&
                DateTime.UtcNow - _dismissedAt < TimeSpan.FromSeconds(3))
            {
                _shown = evt;
                return;
            }

            // Do Not Disturb (island): notification cards never open the island.
            if (_cfg.DoNotDisturb && evt.Kind == BarEventKind.Notification)
            {
                _shown = evt;
                return;
            }

            // Gaming mode: only events at or above Privacy priority interrupt.
            if (App.GamingMode && evt.Priority < (int)BarEventKind.Privacy)
            {
                _shown = evt;
                UpdateCollapsed();
                return;
            }

            // Ticking kinds (progress refreshes) never force a collapsed bar open.
            if (IsTicking(evt.Kind) && _shown?.Kind == evt.Kind)
            {
                _shown = evt;
                if (_expanded) RenderEvent(evt);
                return;
            }

            _shown = evt;
            _userDismissed = false;
            RenderEvent(evt);
            Expand();
        }
        catch (Exception ex) { Log.Error("Bus event render failed", ex); }
    }

    private void OnBusIdle()
    {
        _shown = null;
        VolumeHudGlow.Visibility = Visibility.Collapsed;
        VolumeHudGlowStop.Color = Colors.Transparent;
        UpdateCollapsed();
        if (_expanded) Collapse();
    }

    private void OnMediaChanged(MediaInfo info)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnMediaChanged(info));
            return;
        }

        _lastMedia = info;
        ApplyLayoutMode();
        if (_islandState != IslandVisualState.Collapsed)
        {
            UpdateIslandContent();
        }
        else
        {
            UpdateCollapsed();
        }

        if (!_expanded) return;
        if (_shown?.Kind == BarEventKind.Media)
        {
            _shown = BarEvent.Create(BarEventKind.Media, info.Title, info.Artist,
                payload: info, priority: 60);
            RenderEvent(_shown);
        }
    }

    private void OnTimerChanged(TimerInfo info)
    {
        var prev = _lastTimer;
        _lastTimer = info;
        long second = (long)Math.Ceiling(info.Remaining.TotalSeconds);
        bool finishedNow = info.State == TimerState.Finished && prev?.State == TimerState.Running;
        if (second != _timerUiSecond || info.State != prev?.State)
        {
            _timerUiSecond = info.State == TimerState.Running ? second : -1;
            try { Dispatcher.BeginInvoke(() => OnTimerUi(info, finishedNow)); }
            catch (TaskCanceledException) { /* dispatcher shutting down */ }
        }
        RerenderCurrent(BarEventKind.Timer, () => BarEvent.Create(BarEventKind.Timer,
            info.Label, TimerService.Format(info.Remaining), value: info.Progress,
            valueText: TimerService.Format(info.Remaining), payload: info, priority: 75,
            displayDuration: TimeSpan.FromSeconds(4)));
    }

    private void OnStopwatchChanged(StopwatchInfo info)
    {
        _lastStopwatch = info;
        try { Dispatcher.BeginInvoke(() => { if (_islandState == IslandVisualState.Collapsed || !_expanded) UpdateCollapsed(); }); } catch { }
        RerenderCurrent(BarEventKind.Stopwatch, () => BarEvent.Create(
            BarEventKind.Stopwatch, "Stopwatch", StopwatchService.FormatMs(info.Elapsed),
            valueText: StopwatchService.FormatMs(info.Elapsed), payload: info, priority: 60,
            displayDuration: TimeSpan.FromSeconds(3)));
    }

    private void OnFocusChanged(FocusInfo info)
    {
        _lastFocus = info;
        try { Dispatcher.BeginInvoke(() => OnFocusUi(info)); } catch { }
        RerenderCurrent(BarEventKind.Focus, () => BarEvent.Create(BarEventKind.Focus,
            "Focus", info.Phase, valueText: TimerService.Format(info.Remaining),
            payload: info, priority: 70, displayDuration: TimeSpan.FromSeconds(4)));
    }

    /// <summary>Re-render the visible widget from a fresh local state (never expands).</summary>
    private void RerenderCurrent(BarEventKind kind, Func<BarEvent> make)
    {
        if (!_expanded || _shown?.Kind != kind) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_expanded || _shown?.Kind != kind) return;
            _shown = make();
            RenderEvent(_shown);
        });
    }

    private void OnStatsChanged(SystemStats s)
    {
        _lastStats = s;
        if (_expanded && _shown?.Kind == BarEventKind.SystemMonitor)
        {
            _shown = BarEvent.Create(BarEventKind.SystemMonitor, "System", payload: s, priority: 50);
            RenderEvent(_shown);
        }
        else if (!_expanded && _cfg.SystemMonitorInBar) UpdateCollapsed();
    }

    private void OnSettingsChanged(SettingsModel m)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSettingsChanged(m));
            return;
        }
        ApplySettingsUi(m);
    }

    /// <summary>Apply a fresh settings snapshot to the live window.</summary>
    private void ApplySettingsUi(SettingsModel m)
    {
        try
        {
            // Effective Reduce Motion: Always / Never / follow Windows "Animation effects".
            m.ReduceMotion = m.ReduceMotionMode switch
            {
                ReduceMotionMode.Always => true,
                ReduceMotionMode.Never => false,
                _ => !SystemParameters.ClientAreaAnimation,
            };
            bool first = !_settingsApplied;
            _settingsApplied = true;
            bool animChanged = first || _cfg.ReduceMotion != m.ReduceMotion || _cfg.AnimationSpeed != m.AnimationSpeed
                               || _cfg.SpringStiffness != m.SpringStiffness || _cfg.SpringDamping != m.SpringDamping;
            bool hotkeysChanged = first || _cfg.HotkeyToggle != m.HotkeyToggle || _cfg.HotkeyMedia != m.HotkeyMedia
                                  || _cfg.HotkeyTimer != m.HotkeyTimer || _cfg.HotkeySearch != m.HotkeySearch
                                  || _cfg.HotkeyClipboard != m.HotkeyClipboard
                                  || _cfg.FeatureClipboardHistory != m.FeatureClipboardHistory
                                  || _cfg.FeatureSearch != m.FeatureSearch || _cfg.FeatureTimers != m.FeatureTimers;
            _cfg.HotkeyClipboard = m.HotkeyClipboard;
            _cfg.FeatureClipboardHistory = m.FeatureClipboardHistory;
            _cfg.ClipboardPasteOnSelect = m.ClipboardPasteOnSelect;
            bool placeChanged = first || _cfg.MonitorIndex != m.MonitorIndex
                                || Math.Abs(_cfg.CustomOffsetX - m.CustomOffsetX) > 0.5;
            bool sizeChanged = first || _cfg.BarWidth != m.BarWidth || _cfg.BarHeight != m.BarHeight
                               || _cfg.CornerRadius != m.CornerRadius;
            _cfg.BarWidth = m.BarWidth;
            _cfg.BarHeight = m.BarHeight;
            _cfg.CornerRadius = m.CornerRadius;
            _cfg.AccentColor = m.AccentColor;
            _cfg.Opacity = m.Opacity;
            _cfg.Theme = m.Theme;
            _cfg.BlurEnabled = m.BlurEnabled;
            _cfg.ShadowEnabled = m.ShadowEnabled;
            _cfg.AnimationSpeed = m.AnimationSpeed;
            _cfg.SpringStiffness = m.SpringStiffness;
            _cfg.SpringDamping = m.SpringDamping;
            _cfg.ReduceMotion = m.ReduceMotion;
            OrbDisc.ReduceMotion = m.ReduceMotion;
            DetailPanelComponent.ReduceMotion = m.ReduceMotion;
            NetPanelComponent.ReduceMotion = m.ReduceMotion;
            MirrorPanelComponent.ReduceMotion = m.ReduceMotion;
            IdleDotClock.ReduceMotion = m.ReduceMotion;
            _cfg.CollapseDelayMs = m.CollapseDelayMs;
            _cfg.HoverPreview = m.HoverPreview;
            _cfg.SystemMonitorInBar = m.SystemMonitorInBar;
            _cfg.CollapsedMediaArt = m.CollapsedMediaArt;
            _cfg.FeatureSystemMonitor = m.FeatureSystemMonitor;
            _cfg.AutoHideFullscreen = m.AutoHideFullscreen;
            _cfg.BarEnabled = m.BarEnabled;
            _cfg.MonitorIndex = m.MonitorIndex;
            _cfg.CustomOffsetX = m.CustomOffsetX;
            _cfg.HotkeyToggle = m.HotkeyToggle;
            _cfg.HotkeyMedia = m.HotkeyMedia;
            _cfg.HotkeyTimer = m.HotkeyTimer;
            _cfg.HotkeySearch = m.HotkeySearch;
            _cfg.TemperatureFahrenheit = m.TemperatureFahrenheit;
            ApplyFeatureSettings(m, first);
            _warningLogic.RamPercent = Math.Clamp(m.WarnRamPercent, 50, 100);
            _warningLogic.CpuPercent = Math.Clamp(m.WarnCpuPercent, 50, 100);
            _warningLogic.BatteryPercent = Math.Clamp(m.WarnBatteryPercent, 5, 100);
            if (_cfg.DoNotDisturb != m.DoNotDisturb)
            {
                _cfg.DoNotDisturb = m.DoNotDisturb;
                App.Toggles?.RefreshDnd();
            }
            _cfg.IslandPage1 = m.IslandPage1;
            _cfg.IslandPage2 = m.IslandPage2;
            ApplyRingLayout(first);

            // accent (shared resource → DynamicResource consumers update live;
            // compiled brushes are frozen, so replace instead of mutating)
            try
            {
                if (ColorConverter.ConvertFromString(m.AccentColor) is Color c)
                    Application.Current.Resources["WdbAccentBrush"] = new SolidColorBrush(c);
            }
            catch { }

            // theme + background translucency (shared brush instances → live repaint)
            Core.Theme.ThemeService.Apply(m.Theme, m.Opacity);

            if (Pill.Effect is DropShadowEffect sh)
                sh.Opacity = m.ShadowEnabled ? 0.55 : 0;

            bool blurWasOn = _blurOn;
            _blurOn = m.BlurEnabled;
            if (!_blurOn) BlurHelper.Disable();
            else if (!blurWasOn || _blurL == int.MinValue)
            {
                BlurHelper.Attach(_hwnd);
                _blurL = int.MinValue;
                RefreshBlur();
            }

            if (animChanged)
            {
                _anim.AnimationCompleted -= OnAnimationsSettled;
                _anim.Dispose();
                _anim = CreateAnimator(m);
            }

            // Only re-size a collapsed pill; an open island keeps its geometry.
            if (sizeChanged && !_expanded && _islandState == IslandVisualState.Collapsed)
            {
                ClaimGeometryForSpring(reveal: true);
                _anim.Animate("w", _currentW, CollapsedW, ApplyW);
                _anim.Animate("h", _currentH, CollapsedH, ApplyH);
            }

            if (hotkeysChanged) RegisterHotkeys();
            if (placeChanged) Reposition(m.CustomOffsetX);
            UpdateShape();
            UpdateCollapsed();
            IdleSearchText.Text = string.IsNullOrWhiteSpace(m.HotkeySearch)
                ? "Search" : $"Search · {m.HotkeySearch.Replace("+", " ")}";
            ApplyTodColors();
            UpdateIslandContent();
            if (_expanded && _shown is { } s) { RenderEvent(s); }
        }
        catch (Exception ex) { Log.Error("Apply settings failed", ex); }
    }

    // ---------------------------------------------------------------- hover / input

    private void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_islandState == IslandVisualState.Search) return;
        // Pressing a control (slider, button) is that control's gesture - never start a pill drag,
        // otherwise dragging the mirror zoom/light sliders would slide the whole island sideways.
        if (IsInteractiveSource(e.OriginalSource)) { _dragPressing = false; return; }
        NativeMethods.GetCursorPos(out var pt);
        _dragStartX = pt.X;
        _dragStartOffset = _cfg.CustomOffsetX;
        _dragMoved = false;
        _dragPressing = true;
        // Do NOT capture yet: capturing here routes the following MouseLeftButtonUp to the
        // Pill, so clicks on child controls (metric icons, canvas) would never reach them.
        // Capture only once a real drag begins.
    }

    /// <summary>True when the event originated inside an interactive control (slider/button/thumb)
    /// or inside the active detail panel, owning its own pointer gesture.</summary>
    private bool IsInteractiveSource(object? source)
    {
        var d = source as DependencyObject;
        while (d is not null and not Window)
        {
            if (d is Slider or ButtonBase or Thumb or TextBox or ScrollViewer) return true;
            if (_islandState == IslandVisualState.Detail && ActivePanel is { } ap && ReferenceEquals(d, ap)) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OnPillMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragPressing || e.LeftButton != MouseButtonState.Pressed) return;
        NativeMethods.GetCursorPos(out var pt);
        if (!_dragMoved)
        {
            if (Math.Abs(pt.X - _dragStartX) < 5) return;
            _dragMoved = true;
            Pill.CaptureMouse();
        }
        double scale = CurrentDpi();
        double offset = Math.Clamp(_dragStartOffset + (pt.X - _dragStartX) / scale,
            -_maxOffsetDip, _maxOffsetDip);
        _cfg.CustomOffsetX = offset;
        Reposition(offset);
    }

    private void OnPillMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Pill.IsMouseCaptured) Pill.ReleaseMouseCapture();
        _dragPressing = false;
        if (e.ChangedButton != MouseButton.Left) return;
        if (_dragMoved)
        {
            _dragMoved = false;
            double saved = _cfg.CustomOffsetX;
            try { App.Settings?.Update(s => s.CustomOffsetX = saved); } catch { }
            return;
        }
        // A press that started on a control (slider/button) belongs to it, not the pill.
        if (IsInteractiveSource(e.OriginalSource)) return;
        // Clicking the open (pinned) canvas closes it.
        if (_pinned)
        {
            _pinned = false;
            SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);
            return;
        }
        if (_islandState == IslandVisualState.Collapsed)
        {
            Toggle();
            return;
        }
        if (_islandState == IslandVisualState.Expanded)
        {
            _pinned = false;
            SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);
            return;
        }
    }

    private void OnPillMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_islandState is IslandVisualState.Expanded or IslandVisualState.Detail && IconRow.IsMouseOver
            && IconRow.PageCount > 1)
        {
            if (e.Delta < 0) IconRow.SetPage(IconRow.Page + 1); else IconRow.SetPage(IconRow.Page - 1);
            e.Handled = true;
            return;
        }
        if (App.Volume is null) return;
        float delta = e.Delta > 0 ? 0.03f : -0.03f;
        float cur = App.Volume.Level;
        float next = Math.Clamp(cur + delta, 0f, 1f);
        App.Volume.SetLevel(next);
        if (_islandState != IslandVisualState.Collapsed)
        {
            ShowVolumeToast($"Volume {Math.Round(next * 100)}%");
        }
        e.Handled = true;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        SearchTrace("deactivated", ("phase", _searchPhase), ("hadFocus", _searchHadFocus));
        if (_islandState == IslandVisualState.Search)
        {
            // Click-outside closes only a fully open bar that has had focus; OnSearchOpened
            // re-checks once Opening finishes.
            if (_searchPhase == SearchPhase.Open && _searchHadFocus) CloseSearch();
            return;
        }
        // The clipboard card opened by its hotkey closes when the user clicks elsewhere.
        if (_clipByHotkey && _activeMetric == IslandMetric.Clipboard)
        {
            _clipByHotkey = false;
            _clipPasteTarget = IntPtr.Zero;
            _pinned = false;
            SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);
            return;
        }
        if (!_cfg.HoverPreview && _islandState is IslandVisualState.Expanded or IslandVisualState.Detail)
        {
            _pinned = false;
            SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);
            return;
        }
        if (_expanded && !_dragMoved) UserDismiss();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _islandState == IslandVisualState.Detail && _activeMetric == IslandMetric.Clipboard)
        {
            e.Handled = true;
            CloseClipboardPanel();
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_islandState == IslandVisualState.Search)
            {
                e.Handled = true;
                CloseSearch();
                return;
            }
            if (_islandState is IslandVisualState.Detail or IslandVisualState.Expanded)
            {
                e.Handled = true;
                _pinned = false;
                SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);
                return;
            }
            if (_expanded)
            {
                e.Handled = true;
                UserDismiss();
            }
        }
    }

    /// <summary>Toggle the bar open/closed; when idle shows a quick panel.</summary>
    public void Toggle()
    {
        if (_expanded) { UserDismiss(); return; }

        if (!IsVisible && _cfg.BarEnabled) Show();
        _userDismissed = false;

        if (App.Bus?.Active is BarEvent active && !((active.Kind is BarEventKind.Timer or BarEventKind.Focus) && IsMusicPlaying()))
        {
            _shown = active;
            RenderEvent(active);
            Expand();
            return;
        }

        // Quick panel priority: running timer (if music not playing) → media (playing or paused) → stopwatch → timer → volume.
        if (!IsMusicPlaying() && _lastTimer is { State: TimerState.Running } tActive && tActive.Remaining > TimeSpan.Zero)
            _shown = BarEvent.Create(BarEventKind.Timer, tActive.Label, TimerService.Format(tActive.Remaining),
                value: tActive.Progress, valueText: TimerService.Format(tActive.Remaining), payload: tActive, priority: 60);
        else if (_lastMedia is { } m && !string.IsNullOrWhiteSpace(m.Title))
            _shown = BarEvent.Create(BarEventKind.Media, m.Title, m.Artist, payload: m, priority: 60);
        else if (_lastTimer is { State: TimerState.Running } tRunning && tRunning.Remaining > TimeSpan.Zero)
            _shown = BarEvent.Create(BarEventKind.Timer, tRunning.Label, TimerService.Format(tRunning.Remaining),
                value: tRunning.Progress, valueText: TimerService.Format(tRunning.Remaining), payload: tRunning, priority: 60);
        else if (_lastStopwatch is { } sw && sw.Elapsed > TimeSpan.Zero)
            _shown = BarEvent.Create(BarEventKind.Stopwatch, "Stopwatch",
                StopwatchService.FormatMs(sw.Elapsed), valueText: StopwatchService.FormatMs(sw.Elapsed),
                payload: sw, priority: 60);
        else if (_lastTimer is { } t && t.Remaining > TimeSpan.Zero)
            _shown = BarEvent.Create(BarEventKind.Timer, t.Label, TimerService.Format(t.Remaining),
                value: t.Progress, valueText: TimerService.Format(t.Remaining), payload: t, priority: 60);
        else
        {
            var cur = CoreAudio.GetCurrent();
            float lvl = cur?.Level ?? 0f;
            bool muted = cur?.Muted ?? false;
            string? dev = CoreAudio.GetDefaultDeviceName();
            string text = muted ? "Muted" : $"{Math.Round(lvl * 100)}%";
            _shown = BarEvent.Create(BarEventKind.Volume, muted ? "Muted" : "Volume", text,
                value: muted ? 0 : lvl, valueText: text, payload: (lvl, muted, dev), priority: 60);
        }

        RenderEvent(_shown!);
        Expand();
    }

    private void UserDismiss(bool clearBus = true)
    {
        _userDismissed = true;
        _dismissedAt = DateTime.UtcNow;
        if (clearBus) { try { App.Bus?.Dismiss(); } catch { } }
        Collapse();
    }

    private void ShowOrStartTimer()
    {
        if (_lastTimer is { } t && t.Remaining > TimeSpan.Zero)
        {
            App.Bus?.Raise(BarEvent.Create(BarEventKind.Timer, t.Label,
                TimerService.Format(t.Remaining), value: t.Progress,
                valueText: TimerService.Format(t.Remaining), payload: t, priority: 90,
                displayDuration: TimeSpan.FromSeconds(6)));
        }
        else
        {
            try { App.Timer?.Start(TimeSpan.FromMinutes(5)); } catch (Exception ex) { Log.Error("Timer start", ex); }
        }
    }

    // ---------------------------------------------------------------- fullscreen autohide

    private void OnFullscreenTick(object? sender, EventArgs e)
    {
        try
        {
            var cfg = App.Settings?.Current ?? _cfg;
            bool fs = cfg.AutoHideFullscreen &&
                      NativeMethods.IsWindowFullscreen(NativeMethods.GetForegroundWindow());
            bool wantVisible = cfg.BarEnabled && !fs;
            if (wantVisible && !IsVisible) Show();
            else if (!wantVisible && IsVisible && _searchPhase == SearchPhase.Closed) Hide();   // never under an open search bar
        }
        catch { }
    }

    // ---------------------------------------------------------------- hotkeys

    private void RegisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        for (int id = 1; id <= 5; id++)
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, id); } catch { }
        }
        if (_hotkeysSuspended) return;
        if (TryParseHotkey(_cfg.HotkeyToggle, out uint m1, out uint v1))
            NativeMethods.RegisterHotKey(_hwnd, 1, m1 | NativeMethods.MOD_NOREPEAT, v1);
        if (TryParseHotkey(_cfg.HotkeyMedia, out uint m2, out uint v2))
            NativeMethods.RegisterHotKey(_hwnd, 2, m2 | NativeMethods.MOD_NOREPEAT, v2);
        if (_cfg.FeatureTimers && TryParseHotkey(_cfg.HotkeyTimer, out uint m3, out uint v3))
            NativeMethods.RegisterHotKey(_hwnd, 3, m3 | NativeMethods.MOD_NOREPEAT, v3);
        if (_cfg.FeatureSearch && TryParseHotkey(_cfg.HotkeySearch, out uint m4, out uint v4))
        {
            bool ok = NativeMethods.RegisterHotKey(_hwnd, 4, m4 | NativeMethods.MOD_NOREPEAT, v4);
            if (!ok) Log.Debug("Search hotkey registration failed", ("hotkey", _cfg.HotkeySearch));
        }
        if (_cfg.FeatureClipboardHistory && TryParseHotkey(_cfg.HotkeyClipboard, out uint m5, out uint v5))
        {
            bool ok = NativeMethods.RegisterHotKey(_hwnd, 5, m5 | NativeMethods.MOD_NOREPEAT, v5);
            if (!ok) Log.Info("Clipboard hotkey is taken by another app", ("hotkey", _cfg.HotkeyClipboard));
        }
    }

    private bool _hotkeysSuspended;

    /// <summary>While the settings window records a shortcut, our own global hotkeys must not
    /// swallow the keys being pressed.</summary>
    public void SetHotkeysSuspended(bool suspended)
    {
        if (_hotkeysSuspended == suspended) return;
        _hotkeysSuspended = suspended;
        if (suspended)
        {
            for (int id = 1; id <= 5; id++) NativeMethods.UnregisterHotKey(_hwnd, id);
        }
        else RegisterHotkeys();
    }

    /// <summary>"Ctrl+Alt+Space" → (modifier flags, virtual-key code).</summary>
    public static bool TryParseHotkey(string? text, out uint mods, out uint vk) => Hotkeys.TryParse(text, out mods, out vk);

    // ---------------------------------------------------------------- rendering

    private void ResetRows()
    {
        HeaderGlyph.Visibility = Visibility.Visible;
        HeaderGlyph.Text = IcDot;
        HeaderArt.Visibility = Visibility.Collapsed;
        HeaderArt.Source = null;
        HeaderText.Text = "";
        HeaderSub.Text = "";
        HeaderSub.Visibility = Visibility.Visible;
        HeaderValue.Visibility = Visibility.Collapsed;
        HeaderValue.Text = "";
        ProgressRow.Visibility = Visibility.Collapsed;
        ProgressSlider.IsEnabled = true;
        TransportRow.Visibility = Visibility.Collapsed;
        HeaderRow.Visibility = Visibility.Visible;
        VolumeHudRow.Visibility = Visibility.Collapsed;
        VolumeHudGlow.Visibility = Visibility.Collapsed;
        VolumeHudGlowStop.Color = Colors.Transparent;
        BarsRow.Visibility = Visibility.Collapsed;
        SetBars(0);
        SysMonRow.Visibility = Visibility.Collapsed;
        ActionsRow.Visibility = Visibility.Collapsed;
        FooterText.Visibility = Visibility.Collapsed;
        FooterText.Text = "";
        _syncing = true;
        try { ProgressSlider.Value = 0; } finally { _syncing = false; }
        ProgressFill.Width = 0;
    }

    private bool _syncing;

    private void RenderEvent(BarEvent evt)
    {
        ResetRows();
        switch (evt.Kind)
        {
            case BarEventKind.Media:
                RenderMedia(evt);
                break;
            case BarEventKind.Volume:
                _levelMode = "volume";
                RenderLevel(evt);
                break;
            case BarEventKind.Brightness:
                _levelMode = "brightness";
                RenderLevel(evt);
                break;
            case BarEventKind.Battery:
                RenderBattery(evt);
                break;
            case BarEventKind.Network:
                RenderNetwork(evt);
                break;
            case BarEventKind.Bluetooth:
                RenderBluetooth(evt);
                break;
            case BarEventKind.Privacy:
                RenderPrivacy(evt);
                break;
            case BarEventKind.Download:
                RenderDownload(evt);
                break;
            case BarEventKind.Timer:
                RenderTimer(evt);
                break;
            case BarEventKind.Stopwatch:
                RenderStopwatch(evt);
                break;
            case BarEventKind.Focus:
                RenderFocus(evt);
                break;
            case BarEventKind.SystemMonitor:
                RenderSysMon(evt);
                break;
            default:
                RenderGeneric(evt);
                break;
        }
        SyncFills();
    }

    private void RenderMedia(BarEvent evt)
    {
        HeaderGlyph.Text = IcMusic;
        if (evt.Payload is MediaInfo mi)
        {
            var art = BytesToImage(mi.ArtworkBytes);
            if (art is not null)
            {
                HeaderArt.Source = art;
                HeaderArt.Visibility = Visibility.Visible;
                HeaderGlyph.Visibility = Visibility.Collapsed;
            }
            HeaderText.Text = string.IsNullOrWhiteSpace(mi.Title) ? evt.Title : mi.Title;
            HeaderSub.Text = !string.IsNullOrWhiteSpace(mi.Artist) ? mi.Artist : mi.Album;

            ProgressRow.Visibility = Visibility.Visible;
            _syncing = true;
            try { ProgressSlider.Value = Math.Clamp(mi.Progress, 0, 1); }
            finally { _syncing = false; }
            PositionText.Text = FormatTime(mi.Position);
            DurationText.Text = FormatTime(mi.Duration);

            TransportRow.Visibility = Visibility.Visible;
            PlayGlyph.Text = mi.IsPlaying ? IcPause : IcPlay;
        }
        else
        {
            HeaderText.Text = evt.Title;
            HeaderSub.Text = evt.Subtitle ?? "";
            if (evt.Value is double p)
            {
                ProgressRow.Visibility = Visibility.Visible;
                _syncing = true;
                try { ProgressSlider.Value = Math.Clamp(p, 0, 1); }
                finally { _syncing = false; }
                PositionText.Text = evt.ValueText ?? "";
            }
        }
    }

    private void RenderLevel(BarEvent evt)
    {
        bool isVolume = _levelMode == "volume";
        double level = evt.Value ?? 0;
        bool muted = false;
        string? dev = null;
        if (evt.Payload is (float lv, bool mu, string dn)) { level = lv; muted = mu; dev = dn; }
        else if (evt.Payload is float fl) level = fl;
        else if (evt.Payload is double dl) level = dl;
        if (isVolume && dev is null) dev = App.Volume?.DeviceName;
        level = Math.Clamp(level, 0, 1);
        Color accent = isVolume ? VolAccent : BrightAccent;

        HeaderRow.Visibility = Visibility.Collapsed;
        VolumeHudRow.Visibility = Visibility.Visible;

        VolumeHudIcon.Text = isVolume ? (muted ? IcMute : IcVolume) : IcBrightness;
        VolumeHudIcon.Foreground = HudValueBrush;
        VolumeHudLabel.Text = muted ? "MUTED" : isVolume ? "SPEAKERS" : "DISPLAY";

        VolumeHudBar.Bright = !isVolume;
        VolumeHudBar.Muted = isVolume && muted;
        VolumeHudBar.ReduceMotion = _cfg.ReduceMotion;
        VolumeHudBar.Level = muted ? 0 : level;

        if (muted)
        {
            VolumeHudValue.Text = "Muted";
            VolumeHudValue.FontSize = 13;
            VolumeHudValue.Foreground = HudMutedBrush;
        }
        else
        {
            VolumeHudValue.Text = Math.Round(level * 100).ToString();
            VolumeHudValue.FontSize = 24;
            VolumeHudValue.Foreground = HudValueBrush;
        }

        // Keep the island background clean: do not leak accent wash onto the island pill.
        VolumeHudGlow.Visibility = Visibility.Collapsed;
        VolumeHudGlowStop.Color = Colors.Transparent;
    }

    private void TriggerLevelNudge()
    {
        if (_cfg.ReduceMotion) return;
        double targetW = Math.Clamp(TargetWidth(_shown?.Kind ?? BarEventKind.Volume), 120, MaxPillW);
        // Elastic spring nudge: small impulse expands width momentarily (+160 velocity) and bounces back
        ClaimGeometryForSpring(reveal: false);
        _anim.Animate("w", _currentW, targetW, ApplyW, velocity: 160);
        AnimateIconPop(VolumeHudIconScale);
    }

    private void AnimateIconPop(ScaleTransform? st)
    {
        if (st is null || _cfg.ReduceMotion) return;
        st.ScaleX = 1.15;
        st.ScaleY = 1.15;
        var tw = new TweenAnimation();
        _activeFades.Add(tw);
        tw.Completed += () => _activeFades.Remove(tw);
        tw.Run(1.15, 1.0, TimeSpan.FromMilliseconds(200), v =>
        {
            st.ScaleX = v;
            st.ScaleY = v;
        }, Easing.CubicOut);
    }

    private void RenderBattery(BarEvent evt)
    {
        bool charging = false;
        string? remaining = null;
        if (evt.Payload is BatteryInfo b) { charging = b.Charging; remaining = b.TimeRemaining; }
        HeaderGlyph.Text = charging ? IcBatteryCharging : IcBattery;
        HeaderText.Text = string.IsNullOrEmpty(evt.Title) ? "Battery" : evt.Title;
        HeaderSub.Text = evt.Subtitle ?? (charging ? "Charging" : "On battery");
        string pct = evt.ValueText ?? (evt.Value is double v ? $"{Math.Round(v * 100)}%" : null) ?? "";
        HeaderValue.Text = pct;
        HeaderValue.Visibility = string.IsNullOrEmpty(pct) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrEmpty(remaining))
        {
            FooterText.Text = remaining;
            FooterText.Visibility = Visibility.Visible;
        }
    }

    private void RenderNetwork(BarEvent evt)
    {
        HeaderGlyph.Text = IcNetwork;
        HeaderText.Text = string.IsNullOrEmpty(evt.Title) ? "Network" : evt.Title;
        int bars = -1;
        string? name = null;
        string? status = null;
        if (evt.Payload is NetworkInfo ni)
        {
            bars = ni.SignalBars;
            name = ni.Name ?? ni.Kind;
            status = ni.Connected ? (ni.Internet ? "Internet connected" : "Connected") : "Disconnected";
        }
        HeaderSub.Text = name ?? evt.Subtitle ?? "";
        if (bars >= 0)
        {
            BarsRow.Visibility = Visibility.Visible;
            SetBars(bars);
        }
        // bars < 0 → signal unknown: no bar meter, status text only (honest display).
        if (!string.IsNullOrEmpty(status) || !string.IsNullOrEmpty(evt.ValueText))
        {
            FooterText.Text = status ?? evt.ValueText ?? "";
            FooterText.Visibility = Visibility.Visible;
        }
    }

    private void RenderBluetooth(BarEvent evt)
    {
        HeaderGlyph.Text = IcBluetooth;
        HeaderText.Text = string.IsNullOrEmpty(evt.Title) ? "Bluetooth" : evt.Title;
        string kind = "";
        bool connected = true;
        if (evt.Payload is BluetoothDeviceInfo bd) { kind = bd.Kind; connected = bd.Connected; }
        HeaderSub.Text = !string.IsNullOrEmpty(kind) ? kind : evt.Subtitle ?? "";
        string status = connected ? (evt.Subtitle ?? "Connected") : "Disconnected";
        FooterText.Text = status;
        FooterText.Visibility = Visibility.Visible;
    }

    private void RenderPrivacy(BarEvent evt)
    {
        if (evt.Payload is PrivacyState p)
        {
            HeaderGlyph.Text = p.CameraInUse ? IcCamera : IcMic;
            HeaderText.Text = p.CameraInUse && p.MicInUse ? "Camera & microphone in use"
                : p.CameraInUse ? "Camera in use" : "Microphone in use";
            var parts = new System.Collections.Generic.List<string>();
            if (p.CameraInUse) parts.Add(p.CameraApp is { Length: > 0 } c ? $"Camera: {c}" : "Camera active");
            if (p.MicInUse) parts.Add(p.MicApp is { Length: > 0 } m ? $"Mic: {m}" : "Mic active");
            HeaderSub.Text = "";
            FooterText.Text = string.Join("  ·  ", parts);
            FooterText.Visibility = Visibility.Visible;
        }
        else RenderGeneric(evt);
    }

    private void RenderDownload(BarEvent evt)
    {
        HeaderGlyph.Text = IcDownload;
        HeaderText.Text = string.IsNullOrEmpty(evt.Title) ? "Downloading" : evt.Title;
        HeaderSub.Text = evt.Subtitle ?? "";
        double frac = Math.Clamp(evt.Value ?? 0, 0, 1);
        ProgressRow.Visibility = Visibility.Visible;
        ProgressSlider.IsEnabled = false;
        _syncing = true;
        try { ProgressSlider.Value = frac; }
        finally { _syncing = false; }
        PositionText.Text = $"{Math.Round(frac * 100)}%";
        DurationText.Text = evt.ValueText ?? "";
    }

    private void RenderTimer(BarEvent evt)
    {
        HeaderGlyph.Text = IcTimer;
        TimerInfo? ti = evt.Payload as TimerInfo;
        HeaderText.Text = string.IsNullOrEmpty(evt.Title) ? "Timer" : evt.Title;
        string stateText = ti is null ? "" : ti.State.ToString();
        _timerPaused = stateText.Contains("Paus", StringComparison.OrdinalIgnoreCase);
        HeaderSub.Text = _timerPaused ? "Paused" : stateText;
        string remaining = evt.ValueText ?? (ti is not null ? TimerService.Format(ti.Remaining) : "");
        HeaderValue.Text = remaining;
        HeaderValue.Visibility = string.IsNullOrEmpty(remaining) ? Visibility.Collapsed : Visibility.Visible;

        if (ti is not null)
        {
            ProgressRow.Visibility = Visibility.Visible;
            ProgressSlider.IsEnabled = false;
            _syncing = true;
            try { ProgressSlider.Value = Math.Clamp(ti.Progress, 0, 1); }
            finally { _syncing = false; }
            PositionText.Text = remaining;
            DurationText.Text = $"{Math.Round(ti.Progress * 100)}%";
        }

        ActionsRow.Visibility = Visibility.Visible;
        ActionPrimary.Content = _timerPaused ? "Resume" : "Pause";
        ActionSecondary.Content = "Cancel";
    }

    private void RenderStopwatch(BarEvent evt)
    {
        HeaderGlyph.Text = IcStopwatch;
        StopwatchInfo? si = evt.Payload as StopwatchInfo ?? _lastStopwatch;
        HeaderText.Text = "Stopwatch";
        _stopwatchPaused = si is not null && !si.Running;
        HeaderSub.Text = si is null ? "" : si.Running ? "Running" : "Paused";
        string elapsed = evt.ValueText ?? (si is not null ? StopwatchService.FormatMs(si.Elapsed) : "0:00");
        HeaderValue.Text = elapsed;
        HeaderValue.Visibility = Visibility.Visible;

        if (si is { Laps.Count: > 0 })
        {
            FooterText.Text = $"Last lap  {StopwatchService.FormatMs(si.Laps[^1])}";
            FooterText.Visibility = Visibility.Visible;
        }

        ActionsRow.Visibility = Visibility.Visible;
        ActionPrimary.Content = si is null || si.Running ? "Pause" : "Resume";
        ActionSecondary.Content = "Lap";
    }

    private void RenderFocus(BarEvent evt)
    {
        HeaderGlyph.Text = IcFocus;
        FocusInfo? fi = evt.Payload as FocusInfo ?? _lastFocus;
        HeaderText.Text = "Focus";
        _focusPaused = fi is not null && !fi.Running;
        HeaderSub.Text = fi?.Phase ?? evt.Subtitle ?? "";
        string remaining = evt.ValueText ?? (fi is not null ? TimerService.Format(fi.Remaining) : "");
        HeaderValue.Text = remaining;
        HeaderValue.Visibility = string.IsNullOrEmpty(remaining) ? Visibility.Collapsed : Visibility.Visible;

        ActionsRow.Visibility = Visibility.Visible;
        ActionPrimary.Content = fi is null || fi.Running ? "Pause" : "Resume";
        ActionSecondary.Content = "Stop";
    }

    private void RenderSysMon(BarEvent evt)
    {
        HeaderGlyph.Text = IcGauge;
        HeaderText.Text = "System";
        HeaderSub.Text = "Live performance";
        SysMonRow.Visibility = Visibility.Visible;
        UpdateStats(evt.Payload as SystemStats ?? _lastStats);
    }

    private void UpdateStats(SystemStats? s)
    {
        if (s is null) return;
        double barArea = Math.Max(60, _currentW - 32 - 40 - 42 - 10);
        CpuFill.Width = s.CpuAvailable ? Math.Clamp(s.Cpu, 0, 100) / 100 * barArea : 0;
        GpuFill.Width = s.GpuAvailable ? Math.Clamp(s.Gpu, 0, 100) / 100 * barArea : 0;
        RamFill.Width = Math.Clamp(s.Ram, 0, 100) / 100 * barArea;
        CpuValue.Text = s.CpuAvailable ? $"{Math.Round(s.Cpu)}%" : "N/A";
        GpuValue.Text = s.GpuAvailable ? $"{Math.Round(s.Gpu)}%" : "N/A";
        RamValue.Text = $"{Math.Round(s.Ram)}%";
    }

    private void RenderGeneric(BarEvent evt)
    {
        HeaderGlyph.Text = KindGlyph(evt.Kind);
        HeaderText.Text = evt.Title;
        HeaderSub.Text = evt.Subtitle ?? "";
        HeaderSub.Visibility = string.IsNullOrEmpty(evt.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        if (evt.Value is double frac && evt.ValueText is not null)
        {
            ProgressRow.Visibility = Visibility.Visible;
            ProgressSlider.IsEnabled = false;
            _syncing = true;
            try { ProgressSlider.Value = Math.Clamp(frac, 0, 1); }
            finally { _syncing = false; }
            PositionText.Text = $"{Math.Round(Math.Clamp(frac, 0, 1) * 100)}%";
            DurationText.Text = evt.ValueText;
        }
        else if (evt.ValueText is not null)
        {
            HeaderValue.Text = evt.ValueText;
            HeaderValue.Visibility = Visibility.Visible;
        }
    }

    private string KindGlyph(BarEventKind kind) => kind switch
    {
        BarEventKind.Media => IcMusic,
        BarEventKind.Volume => IcVolume,
        BarEventKind.Brightness => IcBrightness,
        BarEventKind.Battery => IcBattery,
        BarEventKind.Network => IcNetwork,
        BarEventKind.Bluetooth => IcBluetooth,
        BarEventKind.Download => IcDownload,
        BarEventKind.Screenshot or BarEventKind.Recording => IcCamera,
        BarEventKind.Privacy => IcCamera,
        BarEventKind.Clipboard => IcClipboard,
        BarEventKind.Notification or BarEventKind.Toast => IcBell,
        BarEventKind.Timer or BarEventKind.Stopwatch => IcTimer,
        BarEventKind.Focus => IcFocus,
        BarEventKind.SystemMonitor => IcGauge,
        BarEventKind.Usb => IcUsb,
        _ => IcDot,
    };

    private void SetBars(int count)
    {
        var accent = Application.Current.Resources["WdbAccentBrush"] as SolidColorBrush;
        var dim = Application.Current.Resources["WdbDimBrush"] as SolidColorBrush
                  ?? new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        Bar1.Background = count >= 1 ? accent! : dim;
        Bar2.Background = count >= 2 ? accent! : dim;
        Bar3.Background = count >= 3 ? accent! : dim;
        Bar4.Background = count >= 4 ? accent! : dim;
    }

    /// <summary>
    /// Collapsed pill priority: active timer > focus > stopwatch > playing media >
    /// sysmon stats > idle. Shows the most useful live state at a glance.
    /// </summary>
    private void UpdateCollapsed()
    {
        if (_actionSuccessTimer?.IsEnabled == true && _lastTimer?.State != TimerState.Running) return;
        var cfg = _cfg;   // kept in sync by ApplySettingsUi; Settings.Current deep-clones
        string? value = null;

        bool musicNow = IsMusicPlaying();
        if (musicNow != _traceMusicBranch)
        {
            _traceMusicBranch = musicNow;
            SearchTrace("collapsed shows", ("music", musicNow), ("timer", _lastTimer?.State), ("title", _lastMedia?.Title));
        }
        if (musicNow)
        {
            var m = _lastMedia ?? App.Media?.Current;
            if (m is not null)
            {
                CollapsedTitle.Text = string.IsNullOrWhiteSpace(m.Title) ? m.SourceAppName : m.Title;
                if (cfg.CollapsedMediaArt)
                    ShowDisc(m);
                else
                    SetOrb(IcDot);
            }

            if (_lastTimer is { State: TimerState.Running or TimerState.Paused, Remaining: var rem } && rem > TimeSpan.Zero)
            {
                value = TimerService.Format(rem);
            }
            else if (_lastFocus is { Running: true, Remaining: var frem } && frem > TimeSpan.Zero)
            {
                value = TimerService.Format(frem);
            }
            else if (_lastStopwatch is { Running: true, Elapsed: var sel })
            {
                value = StopwatchService.FormatMs(sel);
            }
        }
        else
        {
            if (_lastTimer is { State: TimerState.Running or TimerState.Paused, Remaining: var rem } && rem > TimeSpan.Zero)
            {
                CollapsedTitle.Text = string.IsNullOrWhiteSpace(_lastTimer.Label) ? "Timer" : _lastTimer.Label;
                SetOrb(IcTimer);
                value = TimerService.Format(rem);
            }
            else if (_lastFocus is { Running: true, Remaining: var frem } && frem > TimeSpan.Zero)
            {
                CollapsedTitle.Text = "Focus";
                SetOrb(IcFocus);
                value = TimerService.Format(frem);
            }
            else if (_lastStopwatch is { Running: true, Elapsed: var sel })
            {
                CollapsedTitle.Text = "Stopwatch";
                SetOrb(IcStopwatch);
                value = StopwatchService.FormatMs(sel);
            }
            else if (_lastMedia is { } m && !string.IsNullOrWhiteSpace(m.Title) && cfg.CollapsedMediaArt)
            {
                CollapsedTitle.Text = m.Title;
                ShowDisc(m);
            }
            else
            {
                // No placeholder: the music row is only shown while a media session exists.
                CollapsedTitle.Text = HasMediaSession(_lastMedia)
                    ? (string.IsNullOrWhiteSpace(_lastMedia!.Title) ? _lastMedia.SourceAppName : _lastMedia.Title)
                    : "";
                SetOrb(IcDot);
            }
        }
        if (_idleMode) UpdateIdleCollapsed(DateTime.Now);

        if (cfg.SystemMonitorInBar && _lastStats is { } s)
        {
            string cpu = s.CpuAvailable ? $"{Math.Round(s.Cpu)}%" : "n/a";
            value = value is null ? $"{cpu} · {Math.Round(s.Ram)}%" : $"{value}  ·  {cpu}·{Math.Round(s.Ram)}%";
        }

        if (value is null) CollapsedValue.Visibility = Visibility.Collapsed;
        else
        {
            CollapsedValue.Text = value;
            CollapsedValue.Visibility = Visibility.Visible;
        }
    }

    private void SetOrb(string glyph)
    {
        OrbDisc.Visibility = Visibility.Collapsed;
        OrbArt.Visibility = Visibility.Collapsed;
        OrbGlyph.Text = glyph;
        OrbGlyph.Visibility = Visibility.Visible;
    }

    private BitmapSource? _lastGradientDisc;
    private Color? _lastGradientColor;

    private void ShowDisc(MediaInfo? m)
    {
        if (m is null || (string.IsNullOrWhiteSpace(m.Title) && string.IsNullOrWhiteSpace(m.SourceAppName)))
        {
            SetOrb(IcDot);
            SourceBadge.Visibility = Visibility.Collapsed;
            return;
        }
        OrbArt.Visibility = Visibility.Collapsed;
        OrbGlyph.Visibility = Visibility.Collapsed;
        var art = BytesToImage(m.ArtworkBytes);
        if (art is null)
        {
            Color dom = m.DominantColor ?? Color.FromRgb(0x0A, 0x84, 0xFF);
            if (_lastGradientDisc is not null && _lastGradientColor == dom)
            {
                art = _lastGradientDisc;
            }
            else
            {
                art = AppIconHelper.CreateGradientDisc(dom, 122);
                _lastGradientDisc = art;
                _lastGradientColor = dom;
            }
        }
        else
        {
            _lastGradientDisc = null;
            _lastGradientColor = null;
        }
        OrbDisc.SetArtwork(art, m.IsPlaying);
        OrbDisc.Visibility = Visibility.Visible;

        if (m.SourceAppIconBytes is not null)
        {
            if (!ReferenceEquals(m.SourceAppIconBytes, _badgeIconBytes))
            {
                _badgeIconBytes = m.SourceAppIconBytes;
                SourceBadgeIcon.Source = AppIconHelper.BytesToBitmap(m.SourceAppIconBytes);
            }
            SourceBadge.Visibility = _islandState != IslandVisualState.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            SourceBadge.Visibility = Visibility.Collapsed;
        }
    }

    private byte[]? _badgeIconBytes;

    private BitmapSource? BytesToImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            _lastArtBytes = null;
            _lastArt = null;
            return null;
        }
        if (ReferenceEquals(bytes, _lastArtBytes) && _lastArt is not null) return _lastArt;
        try
        {
            using var ms = new MemoryStream(bytes);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            _lastArt = img;
            _lastArtBytes = bytes;
            return img;
        }
        catch { return null; }
    }

    internal static string FormatTime(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ---------------------------------------------------------------- slider wiring

    private void SyncFills()
    {
        if (ProgressSlider.ActualWidth > 0)
            ProgressFill.Width = Math.Clamp(ProgressSlider.Value, 0, 1) * ProgressSlider.ActualWidth;
    }

    private void OnProgressSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ProgressSlider.ActualWidth > 0)
            ProgressFill.Width = Math.Clamp(e.NewValue, 0, 1) * ProgressSlider.ActualWidth;
    }

    private void OnProgressSliderReleased(object sender, MouseButtonEventArgs e)
    {
        if (_syncing || !ProgressSlider.IsEnabled) return;
        if (_shown?.Kind == BarEventKind.Media)
            try { _ = App.Media?.SeekAsync(Math.Clamp(ProgressSlider.Value, 0, 1)); }
            catch { }
    }

    private void OnVolumeHudScrub(double v)
    {
        v = Math.Clamp(v, 0, 1);
        try
        {
            if (_levelMode == "volume")
            {
                if (v > 0 && App.Volume?.Muted == true) App.Volume.ToggleMute();
                App.Volume?.SetLevel((float)v);
            }
            else App.Brightness?.SetLevel((float)v);
        }
        catch (Exception ex) { Log.Debug("Set level failed", ("error", ex.Message)); }
    }

    private static SolidColorBrush FrozenHud(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ---------------------------------------------------------------- buttons

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        try { _ = App.Media?.PreviousAsync(); } catch { }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lastMedia is not null && !string.IsNullOrWhiteSpace(_lastMedia.Title))
            {
                bool nextPlaying = !_lastMedia.IsPlaying;
                _lastMedia = _lastMedia with { IsPlaying = nextPlaying };
                HoverPlayGlyph.Text = nextPlaying ? IcPause : IcPlay;
                HoverEq.Running = nextPlaying;
                HoverEq.Visibility = Visibility.Visible;
                OrbDisc.SetPlaying(nextPlaying);
                if (nextPlaying && _islandState != IslandVisualState.Collapsed)
                    _spectrum.Start();
                else
                    _spectrum.Stop();
            }
            _ = App.Media?.PlayPauseAsync();
        }
        catch { }
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        try { _ = App.Media?.NextAsync(); } catch { }
    }

    private void OnActionPrimary(object sender, RoutedEventArgs e)
    {
        try
        {
            switch (_shown?.Kind)
            {
                case BarEventKind.Timer:
                    if (_timerPaused) App.Timer?.Resume();
                    else App.Timer?.Pause();
                    break;
                case BarEventKind.Stopwatch:
                    if (_stopwatchPaused) App.Stopwatch?.Start();
                    else App.Stopwatch?.Pause();
                    break;
                case BarEventKind.Focus:
                    if (_focusPaused) App.Focus?.Resume();
                    else App.Focus?.Pause();
                    break;
            }
        }
        catch (Exception ex) { Log.Error("Action primary failed", ex); }
    }

    private void OnActionSecondary(object sender, RoutedEventArgs e)
    {
        try
        {
            switch (_shown?.Kind)
            {
                case BarEventKind.Timer:
                    App.Timer?.Cancel();
                    UserDismiss();
                    break;
                case BarEventKind.Stopwatch:
                    App.Stopwatch?.Lap();
                    break;
                case BarEventKind.Focus:
                    App.Focus?.Stop();
                    UserDismiss();
                    break;
            }
        }
        catch (Exception ex) { Log.Error("Action secondary failed", ex); }
    }

    // ---------------------------------------------------------------- clock & hover stats
    private void UpdateClock()
    {
        var now = DateTime.Now;
        CollapsedClock.Text = IdleClock.FormatTime(now, _twelveHour);
        CollapsedDate.Text = now.ToString("ddd d MMM");
    }

    // ---------------------------------------------------------------- idle layout (no media session)

    private bool HasMediaSession(MediaInfo? m) => _cfg.FeatureMedia &&
        m is not null && (!string.IsNullOrWhiteSpace(m.Title) || !string.IsNullOrWhiteSpace(m.AppId));

    private bool IsMusicPlaying()
    {
        if (!_cfg.FeatureMedia) return false;
        if (_lastMedia is { IsPlaying: true }) return true;
        if (App.Media is not null && App.Media.IsPlaying)
        {
            if (_lastMedia is null || !_lastMedia.IsPlaying)
            {
                var cur = App.Media.Current;
                if (cur is not null) _lastMedia = cur;
                else _ = App.Media.RefreshAsync(showEvent: false);
            }
            return true;
        }
        return false;
    }

    /// <summary>12-hour clock? From the setting, or the Windows region format when it says System.</summary>
    private bool ResolveTwelveHour() => _cfg.ClockFormat switch
    {
        ClockFormat.Hours12 => true,
        ClockFormat.Hours24 => false,
        _ => ReadTwelveHour(),
    };

    private static bool ReadTwelveHour()
    {
        try
        {
            CultureInfo.CurrentCulture.ClearCachedData();
            return IdleClock.Uses12Hour(new CultureInfo(CultureInfo.CurrentCulture.Name, useUserOverride: true));
        }
        catch { return IdleClock.Uses12Hour(CultureInfo.CurrentCulture); }
    }

    /// <summary>Pick the music or idle layout from the media session and crossfade (280 ms) when it
    /// changes. The ring grid on the right is shared and never touched.</summary>
    private void ApplyLayoutMode(bool instant = false)
    {
        bool idle = !HasMediaSession(_lastMedia);
        if (_layoutApplied && idle == _idleMode) return;
        _idleMode = idle;
        _layoutApplied = true;

        MusicCollapsedRow.Visibility = Visibility.Visible;
        IdleCollapsedRow.Visibility = Visibility.Visible;
        MediaInfoStack.Visibility = Visibility.Visible;
        IdleOpenPanel.Visibility = Visibility.Visible;
        OrbContainer.Visibility = Visibility.Visible;
        OrbContainer.IsHitTestVisible = !idle;

        double to = idle ? 1 : 0;
        if (instant || _cfg.ReduceMotion)
        {
            _layoutTween.Stop();
            SetLayoutProgress(to);
            FinishLayoutFade();
        }
        else _layoutTween.Run(_layoutP, to, TimeSpan.FromMilliseconds(LayoutFadeMs), SetLayoutProgress, Easing.CubicOut);

        if (!instant)
        {
            switch (_islandState)
            {
                case IslandVisualState.Collapsed when !_expanded: AnimateIsland(CollapsedW, CollapsedH); break;
                case IslandVisualState.Expanded: AnimateIsland(IslandOpenW, MeasureIslandHeight()); break;
                case IslandVisualState.Detail: AnimateIsland(DetailWidth(), MeasureIslandHeight()); break;
            }
        }
        RefreshIdle(DateTime.Now);
        SyncTimelineTimer();
    }

    private void SetLayoutProgress(double p)
    {
        _layoutP = p;
        MusicCollapsedRow.Opacity = 1 - p;
        IdleCollapsedRow.Opacity = p;
        MediaInfoStack.Opacity = 1 - p;
        IdleOpenPanel.Opacity = p;
        OrbContainer.Opacity = 1 - p;
        IslandLeftHost.Width = MusicLeftW + (IdleLeftW - MusicLeftW) * p;
    }

    private void FinishLayoutFade()
    {
        MusicCollapsedRow.Visibility = _idleMode ? Visibility.Collapsed : Visibility.Visible;
        MediaInfoStack.Visibility = _idleMode ? Visibility.Collapsed : Visibility.Visible;
        OrbContainer.Visibility = _idleMode ? Visibility.Collapsed : Visibility.Visible;
        IdleCollapsedRow.Visibility = _idleMode ? Visibility.Visible : Visibility.Collapsed;
        IdleOpenPanel.Visibility = _idleMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The single 1 s tick. Re-aligns itself to the wall-clock second so the ring and the
    /// colon blink stay in step. Only does work for what is on screen.</summary>
    private void OnSecondTick()
    {
        var now = DateTime.Now;
        UpdateClock();
        UpdateTimeOfDay(now);
        CheckWarningStrip();
        if (_idleMode)
        {
            if (_cfg.FeatureIdleDashboard) _calendar.RefreshIfStale(TimeSpan.FromMinutes(5));
            RefreshIdle(now);
        }
        _clockTimer.Interval = TimeSpan.FromMilliseconds(1000 - now.Millisecond + 10);
    }

    private void RefreshIdle(DateTime now)
    {
        if (!_idleMode) return;
        if (_islandState == IslandVisualState.Collapsed || _expanded) UpdateIdleCollapsed(now);
        else if (_islandState is IslandVisualState.Expanded or IslandVisualState.Detail) UpdateIdleOpen(now);
    }

    private void UpdateIdleCollapsed(DateTime now)
    {
        IdleRing.Fraction = now.Second / 60.0;
        IdleTime.Text = IdleClock.FormatTime(now, _twelveHour);
        IdleDate.Text = now.ToString("ddd, MMM d", CultureInfo.CurrentCulture);
        IdleDayBar.Fraction = IdleClock.DayFraction(now);
        if (_actionSuccessTimer?.IsEnabled == true && _lastTimer?.State != TimerState.Running) return;   // chip is showing a confirmation

        string? chip = null;
        if (_lastTimer is { State: TimerState.Running or TimerState.Paused, Remaining: var trem } && trem > TimeSpan.Zero)
            chip = $"{(string.IsNullOrWhiteSpace(_lastTimer.Label) ? "Timer" : _lastTimer.Label)} {IdleClock.FormatCountdown(trem)}";
        else if (App.Focus?.Current is { Running: true } f)
            chip = $"Focus {IdleClock.FormatCountdown(f.Remaining)}";
        else if (_lastStopwatch is { Running: true, Elapsed: var sel })
            chip = $"Stopwatch {StopwatchService.FormatMs(sel)}";
        else if (_cfg.FeatureIdleDashboard && _calendar.NextAfter(now) is { } ev)
            chip = $"{ev.Title} · {IdleClock.FormatUntil(ev.Start - now)}";

        IdleChip.Visibility = chip is null ? Visibility.Collapsed : Visibility.Visible;
        if (chip is not null) IdleChipText.Text = chip;
    }

    private void UpdateIdleOpen(DateTime now)
    {
        if (!_cfg.FeatureIdleDashboard) return;
        IdleDotClock.SetTime(IdleClock.FormatTime(now, _twelveHour), now.Second % 2 == 0);
        IdleFullDate.Text = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
        double frac = IdleClock.DayFraction(now);
        IdleDayBarLarge.Fraction = frac;
        IdleDayLabel.Text = $"day {(int)Math.Floor(frac * 100)}% · {_todPeriod.Name}";
        IdleGreeting.Text = IdleClock.Greeting(now.Hour);
        UpdateEventLine(now);
        UpdateFocusButton();
    }

    /// <summary>Next real calendar event (bold) + "in 25 min". Hidden when no calendar is connected.</summary>
    private void UpdateEventLine(DateTime now)
    {
        if (!_calendar.HasCalendar)
        {
            IdleEventLine.Visibility = Visibility.Collapsed;
            _eventLineKey = "";
            return;
        }
        var ev = _calendar.NextAfter(now);
        string when = ev is null ? "" : IdleClock.FormatIn(ev.Start - now);
        string key = ev is null ? "none" : $"{ev.Title}|{when}";
        IdleEventLine.Visibility = Visibility.Visible;
        if (key == _eventLineKey) return;
        _eventLineKey = key;
        IdleEventLine.Inlines.Clear();
        if (ev is null)
        {
            IdleEventLine.Inlines.Add(new Run("Nothing else today"));
            return;
        }
        IdleEventLine.Inlines.Add(new Run(ev.Title) { FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        IdleEventLine.Inlines.Add(new Run(" " + when));
    }

    /// <summary>The Focus button runs the one timer service ("Focus", 25 min); while any timer runs
    /// it shows that countdown and a click stops it.</summary>
    private void UpdateFocusButton()
    {
        var t = _lastTimer ?? App.Timer?.Current;
        bool running = t is { State: TimerState.Running } && t.Remaining > TimeSpan.Zero;
        IdleFocusText.Text = running ? $"{t!.Label} {IdleClock.FormatCountdown(t.Remaining)}" : "Focus 25:00";
        // The pill style does not pass Foreground down, so the text block is coloured directly.
        if (running)
        {
            IdleFocusButton.Background = new SolidColorBrush(_cfg.AccentMode == AccentMode.Fixed ? FixedAccent() : IdleClock.Accent(_todHue));
            IdleFocusText.Foreground = Brushes.Black;
        }
        else
        {
            IdleFocusButton.ClearValue(BackgroundProperty);
            IdleFocusText.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private void OnIdleFocusClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.Timer is null) return;
            if (App.Timer.Current.State == TimerState.Running) App.Timer.Cancel();
            else App.Timer.Start(TimeSpan.FromMinutes(25), "Focus");
        }
        catch (Exception ex) { Log.Error("Idle focus toggle failed", ex); }
        UpdateFocusButton();
    }

    private void OnIdleSearchClick(object sender, RoutedEventArgs e) => OpenSearch();

    private long _timerUiSecond = -1;

    /// <summary>Timer state on the UI thread (at most once per displayed second).</summary>
    private void OnTimerUi(TimerInfo info, bool finishedNow)
    {
        if (finishedNow) PulseIsland();   // once, scale 1 -> 1.03 -> 1 (skipped with Reduce Motion)
        UpdateCollapsed();
        if (IslandExpandedHost.Visibility == Visibility.Visible)
        {
            UpdatePage2Rings();
            if (_islandState == IslandVisualState.Detail && _activeMetric == IslandMetric.Timer)
                TimerPanelComponent.Render(info);
            if (_idleMode) UpdateFocusButton();
        }
    }

    private void OnTimerStartRequested(TimeSpan duration)
    {
        try { App.Timer?.Start(duration, "Timer"); }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private void OnTimerStopRequested() => App.Timer?.Cancel();

    private void OnTimerPauseRequested()
    {
        try { App.Timer?.Pause(); }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private void OnTimerResumeRequested()
    {
        try { App.Timer?.Resume(); }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    /// <summary>Focus updates arrive on a worker thread (2 Hz). Detect a natural finish (countdown
    /// reached zero rather than the user stopping it) and pulse the island once.</summary>
    private void OnFocusUi(FocusInfo info)
    {
        bool finished = _focusWasRunning && !info.Running && info.Remaining <= TimeSpan.Zero
                        && _focusLastRemaining <= TimeSpan.FromSeconds(1.5);
        _focusWasRunning = info.Running;
        _focusLastRemaining = info.Remaining;
        if (finished) PulseIsland();
        UpdateCollapsed();
        if (_idleMode) RefreshIdle(DateTime.Now);
    }

    /// <summary>One gentle pulse: scale 1 -> 1.03 -> 1.</summary>
    private void PulseIsland()
    {
        if (_cfg.ReduceMotion) return;
        _pulseTween.Run(0, 1, TimeSpan.FromMilliseconds(420), t =>
        {
            double s = 1 + 0.03 * Math.Sin(Math.PI * t);
            PillPulse.ScaleX = s;
            PillPulse.ScaleY = s;
        }, t => t);
    }

    // ---------------------------------------------------------------- time-of-day colour

    /// <summary>Tracks the day period; when it changes the hue animates (shortest way round the
    /// colour wheel) over 800 ms instead of jumping.</summary>
    private void UpdateTimeOfDay(DateTime now)
    {
        var period = IdleClock.PeriodFor(now.Hour);
        if (!_todInit)
        {
            _todInit = true;
            _todPeriod = period;
            _todHue = period.Hue;
            ApplyTodColors();
            return;
        }
        if (period == _todPeriod) return;
        _todPeriod = period;

        double from = _todHue, delta = IdleClock.HueDelta(from, period.Hue);
        if (_cfg.ReduceMotion)
        {
            _todHue = period.Hue;
            ApplyTodColors();
            return;
        }
        _hueTween.Run(0, 1, TimeSpan.FromMilliseconds(800), t =>
        {
            _todHue = IdleClock.NormalizeHue(from + delta * t);
            ApplyTodColors();
        }, Easing.CubicOut);
    }

    private Color FixedAccent()
    {
        try { if (ColorConverter.ConvertFromString(_cfg.AccentColor) is Color c) return c; }
        catch (FormatException) { /* validated on load; fall back below */ }
        return Color.FromRgb(0x0A, 0x84, 0xFF);
    }

    /// <summary>Push the idle accent (time of day, or the fixed colour) to the ring, dots, day bars, glow and tint.</summary>
    private void ApplyTodColors()
    {
        Color accent = _cfg.AccentMode == AccentMode.Fixed ? FixedAccent() : IdleClock.Accent(_todHue);
        IdleDotClock.Hue = _cfg.AccentMode == AccentMode.Fixed ? HueOf(accent) : _todHue;
        IdleRing.Color = accent;
        IdleDayBar.Color = accent;
        IdleDayBarLarge.Color = accent;
        if (App.Timer?.Current is { State: TimerState.Running })
            IdleFocusButton.Background = new SolidColorBrush(accent);
        if (_idleMode && _islandState != IslandVisualState.Collapsed)
        {
            AlbumRadialGlowStop.Color = Color.FromArgb(38, accent.R, accent.G, accent.B);
            PillShadow.Color = accent;
        }
    }

}
