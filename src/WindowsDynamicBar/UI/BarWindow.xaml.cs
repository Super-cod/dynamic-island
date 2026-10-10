using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Media;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Interop;
using WindowsDynamicBar.Services.Audio;
using WindowsDynamicBar.Services.Media;
using WindowsDynamicBar.Services.Privacy;
using WindowsDynamicBar.Services.SystemInfo;
using WindowsDynamicBar.Services.SystemMonitor;
using WindowsDynamicBar.Services.Timers;
using WindowsDynamicBar.Services.Weather;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using WindowsDynamicBar.Core.Search;
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
    private const double IslandExpandedW = 600;    // s2 (hover hub)
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

    private double DynamicDetailW => Math.Round(IslandExpandedW + 13 + DynamicDetailCardW);
    private double DynamicMirrorW => Math.Round(IslandExpandedW + 13 + DynamicMirrorCardW);

    private double DetailWidth() => _activeMetric == IslandMetric.Mirror ? DynamicMirrorW : DynamicDetailW;

    private void ApplyDynamicCardScale()
    {
        double scale = GetDynamicOpenScale();
        ScaleTransform? st = scale < 0.999 ? new ScaleTransform(scale, scale) : null;
        DetailPanelComponent.LayoutTransform = st;
        NetPanelComponent.LayoutTransform = st;
        WeatherPanelComponent.LayoutTransform = st;
        MirrorPanelComponent.LayoutTransform = st;
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
    private AppInfo? _ghostApp;
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

    // ---- clock ----
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public BarWindow()
    {
        InitializeComponent();

        Width = _winW;
        Height = WinH;

        _cfg = App.Settings.Load();
        _currentW = Math.Max(80, _cfg.BarWidth);
        _currentH = Math.Max(24, _cfg.BarHeight);
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

        IconRow.MetricHovered += OnIconMetricHovered;
        IconRow.MetricUnhovered += OnIconMetricUnhovered;
        IconRow.MetricClicked += OnIconMetricClicked;
        DetailPanelComponent.GpuCycleRequested += OnGpuCycleRequested;
        NetPanelComponent.TestToggleRequested += OnNetTestToggle;
        MirrorPanelComponent.CameraStateChanged += OnMirrorCameraStateChanged;
        _collapseDelay.Tick += OnCollapseDelayTick;

        ProgressTrack.SizeChanged += (_, _) => SyncFills();
        VolumeHudBar.Scrub += OnVolumeHudScrub;

        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        _idleCollapse.Tick += (_, _) =>
        {
            _idleCollapse.Stop();
            if (!_islandHovering && App.Bus?.Active is null && _expanded) Collapse();
        };

        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        PreviewKeyDown += OnBarPreviewKeyDown;
        _timelineTimer.Tick += (_, _) => OnTimelineTick();
        _timelineTimer.Start();

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

        SourceInitialized += (_, _) => OnSourceInitialized();
        Log.Debug("BarWindow constructed");
    }

    private static SpringAnimator CreateAnimator(SettingsModel cfg) => new(
        cfg.ReduceMotion ? 0.35 : Math.Max(0.2, cfg.AnimationSpeed))
    {
        Stiffness = Math.Max(40, cfg.SpringStiffness),
        Damping = Math.Max(4, cfg.SpringDamping),
    };

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
            if (App.Media is not null) App.Media.MediaChanged += OnMediaChanged;
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

        RegisterHotkeys();
        try { App.Clipboard?.Attach(_hwnd); } catch { }

        Reposition();
        _blurL = int.MinValue;
        RefreshBlur();
        _fullscreenTimer?.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateCollapsed();

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unsubscribe();
        try { _collapseDelay.Stop(); } catch { }
        try { _clockTimer.Stop(); } catch { }
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
        _anim.Dispose();
        try
        {
            for (int id = 1; id <= 4; id++) NativeMethods.UnregisterHotKey(_hwnd, id);
        }
        catch { }
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

            case NativeMethods.WM_HOTKEY:
                handled = true;
                int id = wParam.ToInt32();
                Dispatcher.BeginInvoke(() =>
                {
                    switch (id)
                    {
                        case 1: Toggle(); break;
                        case 2: try { _ = App.Media?.RefreshAsync(showEvent: true, force: true); } catch { } break;
                        case 3: ShowOrStartTimer(); break;
                        case 4: ToggleSearch(); break;
                    }
                });
                break;

            case NativeMethods.WM_CLIPBOARDUPDATE:
                try { App.Clipboard?.OnClipboardUpdate(); } catch { }
                break;

            case NativeMethods.WM_DEVICECHANGE:
                try { App.Usb?.OnDeviceChange(wParam.ToInt32(), lParam); } catch { }
                break;

            case NativeMethods.WM_DISPLAYCHANGE:
            case NativeMethods.WM_SETTINGCHANGE:
            case NativeMethods.WM_POWERBROADCAST:
                Dispatcher.BeginInvoke(Reposition);
                break;
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

            double scale = 1.0;
            try
            {
                if (NativeMethods.GetDpiForMonitor(mon.Handle, NativeMethods.MDT_EFFECTIVE_DPI,
                        out uint dpiX, out _) == 0)
                    scale = dpiX / 96.0;
            }
            catch { }

            int workW = mon.WorkArea.Right - mon.WorkArea.Left;
            int workH = mon.WorkArea.Bottom - mon.WorkArea.Top;
            _winW = workW / scale;
            _winH = workH / scale;
            _maxOffsetDip = Math.Max(0, (_winW - Math.Max(80, _cfg.BarWidth)) / 2 - 8);

            // Clamp a previously saved offset in case the target monitor shrank.
            _cfg.CustomOffsetX = Math.Clamp(offsetDip, -_maxOffsetDip, _maxOffsetDip);

            // One fixed full-work-area window; the island morphs inside it (no per-frame resize).
            Width = _winW;
            Height = _winH;
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
                mon.WorkArea.Left, mon.WorkArea.Top,
                workW, workH,
                NativeMethods.SWP_NOACTIVATE);

            ApplyDynamicCardScale();
            UpdatePillMargin();
            RefreshBlur();
        }
        catch (Exception ex) { Log.Debug("Reposition failed", ("error", ex.Message)); }
    }

    private double CurrentDpi() =>
        PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    // ---------------------------------------------------------------- blur / shape

    private void RefreshBlur()
    {
        if (!_blurOn || _hwnd == IntPtr.Zero) return;
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
        else if (_currentW <= IslandExpandedW)
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
            double hoverLeft = (_winW - IslandExpandedW) / 2 + offset;
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
                double progress = Math.Clamp((_currentW - IslandExpandedW) / Math.Max(1.0, targetW - IslandExpandedW), 0.0, 1.0);
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

        double w = Math.Max(80, _cfg.BarWidth);
        double h = Math.Max(24, _cfg.BarHeight);
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
        _islandState = state;
        _activeMetric = metric;
        _collapseDelay.Stop();

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
            return;
        }

        switch (state)
        {
            case IslandVisualState.Collapsed:
                _pinned = false;
                HideIslandContent();
                AnimateIsland(Math.Max(80, _cfg.BarWidth), Math.Max(24, _cfg.BarHeight));
                MorphDisc(DiscCollapsed);
                break;

            case IslandVisualState.Expanded:
                EnsureTelemetry();
                ShowIslandContent(withPanel: false);
                AnimateIsland(IslandExpandedW, MeasureIslandHeight());
                MorphDisc(DiscExpanded);
                break;

            case IslandVisualState.Detail:
                EnsureTelemetry();
                ShowIslandContent(withPanel: true);
                IconRow.SetActive(metric);
                UpdatePanelContent();
                AnimateIsland(DetailWidth(), MeasureIslandHeight());
                MorphDisc(DiscExpanded);
                break;

            case IslandVisualState.Search:
                _pinned = false;
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
                SearchHost.Opacity = 1;
                break;
        }
    }

    private void RenderTelemetry(TelemetryDetailPanel panel, IslandMetric metric)
    {
        var snap = _telemetry;
        var tel = App.Telemetry;
        if (snap is null || tel is null)
        {
            switch (metric)
            {
                case IslandMetric.Cpu: panel.ShowCpu(new CpuStats(0, "", 0, 0, 0, false), null, null); break;
                case IslandMetric.Gpu: panel.ShowGpu(new GpuTrack { Name = "" }, 0, 1, null); break;
                case IslandMetric.Ram: panel.ShowRam(new MemoryStats(0, 0, 0, false), 0, 0, 0, -1, -1, null); break;
            }
            return;
        }

        var gpus = tel.SnapshotGpuTracks();
        var procs = snap.TopProcesses;
        switch (metric)
        {
            case IslandMetric.Cpu:
                panel.ShowCpu(snap.Cpu, tel.CpuCoreUsage, tel.CpuHistory, procs);
                break;

            case IslandMetric.Gpu:
                if (gpus.Count == 0) { panel.ShowGpu(new GpuTrack { Name = "" }, 0, 1, null, procs); break; }
                int idx = Math.Clamp(_gpuIndex, 0, gpus.Count - 1);
                panel.ShowGpu(gpus[idx], idx, gpus.Count, tel.GetGpuHistory(idx), procs);
                break;

            case IslandMetric.Ram:
                panel.ShowRam(snap.Memory, tel.RamSwapUsedBytes, tel.RamSwapTotalBytes,
                    tel.RamCacheBytes, tel.DiskReadBytesPerSec, tel.DiskWriteBytesPerSec,
                    tel.RamHistory, procs);
                break;
        }
    }

    private void AnimateIsland(double w, double h)
    {
        double targetTop = _islandState == IslandVisualState.Search ? Math.Round(_winH * 0.22) : PillTop;
        double targetR = _islandState == IslandVisualState.Search ? 29 : (_islandState != IslandVisualState.Collapsed ? 36 : _cfg.CornerRadius);

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
        { DetailPanelComponent, NetPanelComponent, MirrorPanelComponent, WeatherPanelComponent };

    /// <summary>Shows the panel matching the active metric (telemetry card, net card, mirror card
    /// or weather card) and fades the others out. Only meaningful in the island's Detail state.</summary>
    private UIElement ActivePanel => _activeMetric switch
    {
        IslandMetric.Net => NetPanelComponent,
        IslandMetric.Mirror => MirrorPanelComponent,
        IslandMetric.Weather => WeatherPanelComponent,
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

    private void HideIslandContent()
    {
        SearchHost.Visibility = Visibility.Collapsed;
        SearchHost.Opacity = 0;
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
        CollapsedPanel.Visibility = Visibility.Visible;
        OrbHost.Visibility = Visibility.Visible;
        Fade(CollapsedPanel, 1, 160);
        Fade(OrbHost, 1, 160);
        UpdateIslandContent();
    }

    private string _currentTitle = "";

    private void UpdateIslandContent()
    {
        UpdateCollapsed();
        var m = _lastMedia;
        string title = m is not null && !string.IsNullOrWhiteSpace(m.Title) ? m.Title : "Dynamic";
        UpdateTitleScroll(title);

        HoverArtist.Text = m is not null && !string.IsNullOrWhiteSpace(m.Artist) ? m.Artist : (m is not null ? m.Album : "Windows Dynamic Bar");

        var sessions = App.Media?.GetSessions();
        string appName = !string.IsNullOrWhiteSpace(m?.SourceAppName) ? m.SourceAppName : "Music";
        SourceChipText.Text = sessions is not null && sessions.Count > 1 ? $"{appName} ▾" : appName;
        SourceChip.Cursor = sessions is not null && sessions.Count > 1 ? Cursors.Hand : Cursors.Arrow;

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

        AlbumRadialGlowStop.Color = Color.FromArgb(38, dominant.R, dominant.G, dominant.B);
        AlbumRadialGlow.Opacity = _islandState != IslandVisualState.Collapsed ? 1.0 : 0.0;
        PillShadow.Color = _islandState != IslandVisualState.Collapsed ? dominant : Colors.Black;

        ShowDisc(m);
        IconRow.ReduceMotion = _cfg.ReduceMotion;
        UpdateIconRowValues();
    }

    private void UpdateTitleScroll(string title)
    {
        if (_currentTitle == title && HoverTitle.Text == title) return;
        _currentTitle = title;
        HoverTitle.Text = title;

        HoverTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textW = HoverTitle.DesiredSize.Width;
        double boxW = TitleContainer.ActualWidth > 0 ? TitleContainer.ActualWidth : 242;

        if (textW <= boxW)
        {
            HoverTitleTransform.BeginAnimation(TranslateTransform.XProperty, null);
            HoverTitleTransform.X = 0;
            HoverTitle.TextTrimming = TextTrimming.None;
        }
        else
        {
            if (_cfg.ReduceMotion)
            {
                HoverTitleTransform.BeginAnimation(TranslateTransform.XProperty, null);
                HoverTitleTransform.X = 0;
                HoverTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            }
            else
            {
                HoverTitle.TextTrimming = TextTrimming.None;
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
        if (_islandState != IslandVisualState.Collapsed)
        {
            UpdateTimeline();
            UpdateLyricDisplay();
        }
        CheckWarningStrip();
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
        var snap = _telemetry;
        double ram = snap is not null && snap.Memory.Available ? snap.Memory.UsedPercent : -1;
        double cpu = snap is not null && snap.Cpu.Available ? snap.Cpu.Utilization : -1;

        float batteryPct = -1;
        bool isCharging = true;
        try
        {
            var pwr = System.Windows.Forms.SystemInformation.PowerStatus;
            batteryPct = pwr.BatteryLifePercent * 100f;
            isCharging = (pwr.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online);
        }
        catch { }

        bool shouldShow = _warningLogic.Update(ram, cpu, batteryPct, isCharging, out string kind);
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
                var topProc = GetTopCpuProcess();
                string procStr = topProc is not null ? $" · {topProc.Value.Item1} at {topProc.Value.Item2}%" : "";
                text = $"CPU at {Math.Round(cpu):F0}%{procStr}";
            }
            else if (kind == "Battery")
            {
                text = $"Battery low at {Math.Round(batteryPct):F0}% · Plug in soon";
            }
        }

        if (shouldShow != _warningStripActive || kind != _warningKind)
        {
            _warningStripActive = shouldShow;
            _warningKind = kind;
            WarningStripText.Text = text;

            if (shouldShow)
            {
                WarningStrip.Visibility = Visibility.Visible;
                Fade(WarningStrip, 1, 200);
            }
            else
            {
                Fade(WarningStrip, 0, 150, () => WarningStrip.Visibility = Visibility.Collapsed);
            }

            if (_islandState != IslandVisualState.Collapsed)
            {
                AnimateIsland(_currentW, MeasureIslandHeight());
            }
        }
    }

    private static (string, string)? GetTopMemoryProcess()
    {
        try
        {
            var p = Process.GetProcesses().OrderByDescending(x => x.WorkingSet64).FirstOrDefault();
            if (p is not null)
            {
                string size = FormatBytes(p.WorkingSet64);
                return (p.ProcessName + ".exe", size);
            }
        }
        catch { }
        return null;
    }

    private static (string, string)? GetTopCpuProcess()
    {
        try
        {
            var p = Process.GetProcesses().OrderByDescending(x => x.WorkingSet64).FirstOrDefault();
            if (p is not null)
                return (p.ProcessName + ".exe", "high");
        }
        catch { }
        return null;
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
    }

    private void OnWarningCloseClick(object sender, RoutedEventArgs e)
    {
        _warningLogic.Dismiss(_warningKind);

        _warningStripActive = false;
        Fade(WarningStrip, 0, 150, () => WarningStrip.Visibility = Visibility.Collapsed);
        if (_islandState != IslandVisualState.Collapsed)
        {
            AnimateIsland(_currentW, MeasureIslandHeight());
        }
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
    }

    private void UpdateWeatherIcon()
    {
        var snap = _weather?.Snapshot;
        if (snap is null || !snap.Available)
        {
            IconRow.UpdateWeather(-1, "\u2014", Color.FromRgb(0xE8, 0xB0, 0x4B));
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
        if (monitor is null) { IconRow.UpdateNet(-1, "\u2014"); return; }

        var test = monitor.TestState;
        bool running = test.Phase is NetTestPhase.Ping or NetTestPhase.Download or NetTestPhase.Upload;

        if (running)
            IconRow.UpdateNet(test.ProgressPercent, $"{Math.Round(test.ProgressPercent):F0}%");
        else if (test.DownMbps is double down)
            IconRow.UpdateNet(Math.Clamp(down / NetworkMonitor.GraphMaxMbps * 100, 0, 100), ShortRate(down));
        else
            IconRow.UpdateNet(-1, "\u2014");
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
        if (_netMonitor is null) return;
        if (_netMonitor.TestRunning) _netMonitor.CancelSpeedTest();
        else _netMonitor.StartSpeedTest();
    }

    private void EnsureWeather()
    {
        if (_weather is not null) return;
        _weather = new WeatherService();
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
            _islandState, _activeMetric, pointerInside: true, IslandMetric.None, collapseElapsed: false);
        SetIslandState(state, metric);
    }

    /// <summary>Mouse left the island region entirely (grace timer defers collapse).</summary>
    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
    {
        if (_islandState == IslandVisualState.Search) return;
        _islandHovering = false;
        if (_expanded) return;
        if (_pinned) return;   // clicked open: hover-leave must not collapse it
        _collapseDelay.Stop();
        _collapseDelay.Start();
    }

    private void OnCollapseDelayTick(object? sender, EventArgs e)
    {
        if (_islandState == IslandVisualState.Search) return;
        _collapseDelay.Stop();
        if (_expanded || _pinned) return;

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
            _islandState, _activeMetric, pointerInside: false, IslandMetric.None, collapseElapsed: true);
        SetIslandState(state, metric);
        try { if (App.Telemetry is { IsActive: true }) App.Telemetry.Deactivate(); } catch { }
    }

    private void OnIconMetricHovered(IslandMetric metric)
    {
        if (_islandState == IslandVisualState.Search) return;
        if (_expanded || _pinned) return;   // pinned panel ignores hover until clicked again
        _collapseDelay.Stop();
        SetIslandState(IslandVisualState.Detail, metric);
    }

    private void OnIconMetricUnhovered()
    {
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
            SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);
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
            // If the dynamic island is open (expanded music pill, detail telemetry panel, or hovered),
            // transient HUD events (volume, brightness) must NOT collapse or preempt the open island.
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
        UpdateCollapsed();
        if (_expanded) Collapse();
    }

    private void OnMediaChanged(MediaInfo info)
    {
        _lastMedia = info;
        if (_islandState != IslandVisualState.Collapsed)
        {
            Dispatcher.BeginInvoke(() => UpdateIslandContent());
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
        _lastTimer = info;
        RerenderCurrent(BarEventKind.Timer, () => BarEvent.Create(BarEventKind.Timer,
            info.Label, TimerService.Format(info.Remaining), value: info.Progress,
            valueText: TimerService.Format(info.Remaining), payload: info, priority: 75,
            displayDuration: TimeSpan.FromSeconds(4)));
    }

    private void OnStopwatchChanged(StopwatchInfo info)
    {
        _lastStopwatch = info;
        RerenderCurrent(BarEventKind.Stopwatch, () => BarEvent.Create(
            BarEventKind.Stopwatch, "Stopwatch", StopwatchService.FormatMs(info.Elapsed),
            valueText: StopwatchService.FormatMs(info.Elapsed), payload: info, priority: 60,
            displayDuration: TimeSpan.FromSeconds(3)));
    }

    private void OnFocusChanged(FocusInfo info)
    {
        _lastFocus = info;
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

            _anim.Dispose();
            _anim = CreateAnimator(m);

            if (!_expanded)
            {
                _anim.Animate("w", _currentW, m.BarWidth, ApplyW);
                _anim.Animate("h", _currentH, m.BarHeight, ApplyH);
            }

            RegisterHotkeys();
            Reposition(m.CustomOffsetX);
            UpdateShape();
            UpdateCollapsed();
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
    /// that owns its own pointer gesture.</summary>
    private static bool IsInteractiveSource(object? source)
    {
        var d = source as DependencyObject;
        while (d is not null and not Window)
        {
            if (d is Slider or ButtonBase or Thumb or TextBox or ScrollViewer) return true;
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
            SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);
            return;
        }
        // While the island is open the pill is an interactive surface, not the volume card toggle.
        if (_islandState != IslandVisualState.Collapsed) return;
        Toggle();
    }

    private void OnPillMouseWheel(object sender, MouseWheelEventArgs e)
    {
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
        if (_islandState == IslandVisualState.Search)
        {
            CloseSearch();
            return;
        }
        if (_expanded && !_dragMoved) UserDismiss();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_islandState == IslandVisualState.Search)
            {
                e.Handled = true;
                CloseSearch();
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

        if (App.Bus?.Active is BarEvent active)
        {
            _shown = active;
            RenderEvent(active);
            Expand();
            return;
        }

        // Quick panel priority: running timer → media (playing or paused) → stopwatch → timer → volume.
        if (_lastTimer is { State: TimerState.Running } tActive && tActive.Remaining > TimeSpan.Zero)
            _shown = BarEvent.Create(BarEventKind.Timer, tActive.Label, TimerService.Format(tActive.Remaining),
                value: tActive.Progress, valueText: TimerService.Format(tActive.Remaining), payload: tActive, priority: 60);
        else if (_lastMedia is { } m && !string.IsNullOrWhiteSpace(m.Title))
            _shown = BarEvent.Create(BarEventKind.Media, m.Title, m.Artist, payload: m, priority: 60);
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
            else if (!wantVisible && IsVisible) Hide();
        }
        catch { }
    }

    // ---------------------------------------------------------------- hotkeys

    private void RegisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        for (int id = 1; id <= 4; id++)
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, id); } catch { }
        }
        if (TryParseHotkey(_cfg.HotkeyToggle, out uint m1, out uint v1))
            NativeMethods.RegisterHotKey(_hwnd, 1, m1 | NativeMethods.MOD_NOREPEAT, v1);
        if (TryParseHotkey(_cfg.HotkeyMedia, out uint m2, out uint v2))
            NativeMethods.RegisterHotKey(_hwnd, 2, m2 | NativeMethods.MOD_NOREPEAT, v2);
        if (TryParseHotkey(_cfg.HotkeyTimer, out uint m3, out uint v3))
            NativeMethods.RegisterHotKey(_hwnd, 3, m3 | NativeMethods.MOD_NOREPEAT, v3);
        if (TryParseHotkey(_cfg.HotkeySearch, out uint m4, out uint v4))
        {
            bool ok = NativeMethods.RegisterHotKey(_hwnd, 4, m4 | NativeMethods.MOD_NOREPEAT, v4);
            if (!ok) Log.Debug("Search hotkey registration failed", ("hotkey", _cfg.HotkeySearch));
        }
    }

    /// <summary>"Ctrl+Alt+Space" → (modifier flags, virtual-key code).</summary>
    public static bool TryParseHotkey(string? text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= NativeMethods.MOD_CONTROL; break;
                case "alt": mods |= NativeMethods.MOD_ALT; break;
                case "shift": mods |= NativeMethods.MOD_SHIFT; break;
                case "win" or "windows": mods |= NativeMethods.MOD_WIN; break;
                default: return false;
            }
        }

        var key = parts[^1];
        if (key.Equals("Space", StringComparison.OrdinalIgnoreCase))
        {
            if (mods == 0) return false;
            vk = 0x20;
            return true;
        }
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) { vk = c; return true; }
            return false;
        }
        if (key.Length >= 2 && key.Length <= 3 &&
            (key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), out int fn) &&
            fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }
        return false;
    }

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

        // Accent wash behind the whole pill, brightness scaled by the level (mirrors the reference).
        byte a = (byte)Math.Round(0x61 * (muted ? 0.0 : 0.35 + level * 0.65));
        VolumeHudGlowStop.Color = Color.FromArgb(a, accent.R, accent.G, accent.B);
        VolumeHudGlow.Visibility = Visibility.Visible;
    }

    private void TriggerLevelNudge()
    {
        if (_cfg.ReduceMotion) return;
        double targetW = Math.Clamp(TargetWidth(_shown?.Kind ?? BarEventKind.Volume), 120, MaxPillW);
        // Elastic spring nudge: small impulse expands width momentarily (+160 velocity) and bounces back
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
        if (_actionSuccessTimer?.IsEnabled == true) return;
        var cfg = App.Settings?.Current ?? _cfg;
        string? value = null;

        if (_lastTimer is { State: TimerState.Running, Remaining: var rem } && rem > TimeSpan.Zero)
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
            CollapsedTitle.Text = "Dynamic";
            SetOrb(IcDot);
        }

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

    private void ShowDisc(MediaInfo? m)
    {
        if (m is null || string.IsNullOrWhiteSpace(m.Title))
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
            art = AppIconHelper.CreateGradientDisc(dom, 122);
        }
        OrbDisc.SetArtwork(art, m.IsPlaying);
        OrbDisc.Visibility = Visibility.Visible;

        if (m.SourceAppIconBytes is not null)
        {
            SourceBadgeIcon.Source = AppIconHelper.BytesToBitmap(m.SourceAppIconBytes);
            SourceBadge.Visibility = _islandState != IslandVisualState.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            SourceBadge.Visibility = Visibility.Collapsed;
        }
    }

    private BitmapSource? BytesToImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
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
        CollapsedClock.Text = now.ToString("HH:mm");
        CollapsedDate.Text = now.ToString("ddd d MMM");
    }

    // ---------------------------------------------------------------- search bar with tab commands

    public void ToggleSearch()
    {
        if (_islandState == IslandVisualState.Search)
            CloseSearch();
        else
            OpenSearch();
    }

    public void OpenSearch()
    {
        _prevForegroundHwnd = NativeMethods.GetForegroundWindow();
        EnsureOnCursorMonitor();
        SetIslandState(IslandVisualState.Search, IslandMetric.None, force: true);

        double targetW = Math.Min(660.0, _winW * 0.94);
        AnimateIsland(targetW, 58.0);

        SearchInput.Text = "";
        SearchGhostText.Inlines.Clear();
        _ghostApp = null;
        _ghostRemainder = "";
        _searchResults.Clear();
        SearchResultsList.Children.Clear();
        SearchResultsSection.Visibility = Visibility.Collapsed;
        SearchResultsScroll.Height = double.NaN;
        _selectedResultIndex = -1;
        _armedRiskyCommand = false;
        _armedCommandTitle = "";
        RemoveAppTag();

        AppDiscoveryService.Instance.RefreshInBackground();

        Activate();
        SearchInput.Focus();
    }

    public void CloseSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _searchResults.Clear();
        SearchResultsList.Children.Clear();
        SearchResultsSection.Visibility = Visibility.Collapsed;
        SearchResultsScroll.Height = double.NaN;
        SearchGhostText.Inlines.Clear();
        _ghostApp = null;
        _ghostRemainder = "";
        _selectedResultIndex = -1;
        _armedRiskyCommand = false;
        _armedCommandTitle = "";
        RemoveAppTag();

        SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);

        if (_prevForegroundHwnd != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(_prevForegroundHwnd);
            _prevForegroundHwnd = IntPtr.Zero;
        }
    }

    private void EnsureOnCursorMonitor()
    {
        NativeMethods.GetCursorPos(out var pt);
        var monitors = NativeMethods.GetMonitors();
        if (monitors.Count == 0) return;
        var mon = monitors.FirstOrDefault(m =>
            pt.X >= m.Bounds.Left && pt.X < m.Bounds.Right &&
            pt.Y >= m.Bounds.Top && pt.Y < m.Bounds.Bottom) ?? monitors[0];

        double scale = 1.0;
        try
        {
            if (NativeMethods.GetDpiForMonitor(mon.Handle, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
                scale = dpiX / 96.0;
        }
        catch { }

        int workW = mon.WorkArea.Right - mon.WorkArea.Left;
        int workH = mon.WorkArea.Bottom - mon.WorkArea.Top;
        _winW = workW / scale;
        _winH = workH / scale;

        Width = _winW;
        Height = _winH;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
            mon.WorkArea.Left, mon.WorkArea.Top,
            workW, workH,
            NativeMethods.SWP_NOACTIVATE);
    }

    private void ActivateAppTag(AppInfo app)
    {
        _activeTag = app;
        SearchAppTagName.Text = app.Name;
        if (app.Icon != null)
        {
            SearchAppTagIcon.Source = app.Icon;
            SearchAppTagIcon.Visibility = Visibility.Visible;
            SearchAppTagLetter.Visibility = Visibility.Collapsed;
        }
        else
        {
            SearchAppTagLetter.Text = app.Name.Length > 0 ? char.ToUpperInvariant(app.Name[0]).ToString() : "•";
            SearchAppTagLetter.Visibility = Visibility.Visible;
            SearchAppTagIcon.Visibility = Visibility.Collapsed;
        }
        SearchAppTag.Visibility = Visibility.Visible;

        SearchInput.Text = "";
        SearchPlaceholder.Text = GetPlaceholderForApp(app);
        SearchPlaceholder.Visibility = Visibility.Visible;
        SearchGhostText.Inlines.Clear();
        _ghostApp = null;
        _ghostRemainder = "";

        // Empty input with active tag displays only the search bar
        SearchResultsSection.Visibility = Visibility.Collapsed;
        SearchResultsList.Children.Clear();
        _searchResults.Clear();
        _selectedResultIndex = -1;
        AnimateIslandHeight(58.0);
    }

    private void RemoveAppTag()
    {
        _activeTag = null;
        SearchAppTag.Visibility = Visibility.Collapsed;
        SearchAppTagIcon.Source = null;
        SearchPlaceholder.Text = "Search apps, files, commands...";
    }

    private static string GetPlaceholderForApp(AppInfo app)
    {
        string name = app.Name.ToLowerInvariant();
        if (name.Contains("chrome") || name.Contains("edge") || name.Contains("firefox") || name.Contains("browser"))
            return "Search Google or enter URL...";
        if (name.Contains("terminal") || name.Contains("powershell") || name.Contains("cmd"))
            return "Run command or script...";
        if (name.Contains("code") || name.Contains("visual studio"))
            return "Open folder or file...";
        if (name.Contains("spotify") || name.Contains("music"))
            return "Search songs, albums or commands...";
        if (name.Contains("explorer") || name.Contains("files"))
            return "Open folder or path...";
        return "Type a command...";
    }

    private void OnSearchInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_islandState != IslandVisualState.Search) return;

        string text = SearchInput.Text;
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
        _armedRiskyCommand = false;
        _armedCommandTitle = "";

        if (string.IsNullOrWhiteSpace(text))
        {
            SearchGhostText.Inlines.Clear();
            _ghostApp = null;
            _ghostRemainder = "";
            _searchCts?.Cancel();
            _searchCts = null;
            _searchResults.Clear();
            _selectedResultIndex = -1;
            SearchResultsList.Children.Clear();
            SearchResultsSection.Visibility = Visibility.Collapsed;
            SearchResultsScroll.Height = double.NaN;
            AnimateIslandHeight(58.0);
            return;
        }

        UpdateSearchResults(text);
    }

    private void OnSearchInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_islandState != IslandVisualState.Search) return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseSearch();
            return;
        }

        if (e.Key == Key.Down)
        {
            e.Handled = true;
            _armedRiskyCommand = false;
            _armedCommandTitle = "";
            if (_searchResults.Count > 0)
            {
                _selectedResultIndex = (_selectedResultIndex + 1) % _searchResults.Count;
                UpdateRowSelections();
                ScrollSelectedIntoView();
            }
            return;
        }

        if (e.Key == Key.Up)
        {
            e.Handled = true;
            _armedRiskyCommand = false;
            _armedCommandTitle = "";
            if (_searchResults.Count > 0)
            {
                _selectedResultIndex = (_selectedResultIndex - 1 + _searchResults.Count) % _searchResults.Count;
                UpdateRowSelections();
                ScrollSelectedIntoView();
            }
            return;
        }

        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            if (_ghostApp != null)
            {
                ActivateAppTag(_ghostApp);
                return;
            }
            if (_selectedResultIndex >= 0 && _selectedResultIndex < _searchResults.Count)
            {
                var sel = _searchResults[_selectedResultIndex];
                if (sel.Category == SearchCategory.Apps && !string.IsNullOrEmpty(sel.AppId))
                {
                    var app = AppDiscoveryService.Instance.GetApps().FirstOrDefault(a => a.AppId.Equals(sel.AppId, StringComparison.OrdinalIgnoreCase));
                    if (app != null)
                    {
                        ActivateAppTag(app);
                        return;
                    }
                }
            }
            return;
        }

        if (e.Key == Key.Back && string.IsNullOrEmpty(SearchInput.Text) && _activeTag != null)
        {
            e.Handled = true;
            string restored = _activeTag.Name;
            RemoveAppTag();
            SearchInput.Text = restored;
            SearchInput.CaretIndex = restored.Length;
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_selectedResultIndex >= 0 && _selectedResultIndex < _searchResults.Count)
            {
                ExecuteResult(_searchResults[_selectedResultIndex]);
            }
            else if (_searchResults.Count > 0)
            {
                ExecuteResult(_searchResults[0]);
            }
            else if (!string.IsNullOrWhiteSpace(SearchInput.Text))
            {
                ExecuteWebSearch(SearchInput.Text.Trim());
            }
            return;
        }
    }

    private void UpdateSearchResults(string text)
    {
        string query = text.Trim();
        _searchResults.Clear();

        if (_activeTag != null)
        {
            SearchGhostText.Inlines.Clear();
            _ghostApp = null;
            _ghostRemainder = "";

            var scoped = AppCommandRegistry.GetScopedCommands(_activeTag, query);
            _searchResults.AddRange(scoped);
            _selectedResultIndex = _searchResults.Count > 0 ? 0 : -1;
            RenderResultRows();
            return;
        }

        // 1. Ghost completion & Apps
        var allApps = AppDiscoveryService.Instance.GetApps();
        AppInfo? topPrefixApp = null;
        var matchedApps = new List<(AppInfo App, int Score, List<int> MatchedIndices)>();

        foreach (var app in allApps)
        {
            var match = FuzzyMatcher.Match(app.Name, query, app.LaunchCount);
            if (!match.Matched)
            {
                foreach (var alias in app.Aliases)
                {
                    var aliasMatch = FuzzyMatcher.Match(alias, query, app.LaunchCount);
                    if (aliasMatch.Matched && aliasMatch.Score > match.Score)
                    {
                        match = aliasMatch;
                    }
                }
            }

            if (match.Matched)
            {
                matchedApps.Add((app, match.Score, match.MatchedIndices));
            }

            if (topPrefixApp == null && app.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                topPrefixApp = app;
            }
        }

        // Update Ghost text
        if (topPrefixApp != null && topPrefixApp.Name.Length > text.Length)
        {
            _ghostApp = topPrefixApp;
            _ghostRemainder = topPrefixApp.Name.Substring(text.Length);
            SearchGhostText.Inlines.Clear();
            SearchGhostText.Inlines.Add(new Run(text) { Foreground = Brushes.Transparent });
            SearchGhostText.Inlines.Add(new Run(_ghostRemainder) { Foreground = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)) });
        }
        else
        {
            SearchGhostText.Inlines.Clear();
            _ghostApp = null;
            _ghostRemainder = "";
        }

        // Priority 1: Calculator / Converter
        var (evalOk, evalRes, evalSub) = ExpressionEvaluator.TryEvaluate(query);
        if (evalOk)
        {
            string capturedRes = evalRes;
            _searchResults.Add(new SearchResult
            {
                Title = evalRes,
                Subtitle = evalSub,
                Category = SearchCategory.Calculator,
                FallbackLetter = "=",
                FallbackColor = Color.FromRgb(0x2F, 0xD2, 0xB0),
                KeyboardHint = "Enter copy",
                Action = () =>
                {
                    try
                    {
                        Clipboard.SetText(capturedRes);
                        ShowActionSuccess($"✓ Copied {capturedRes}");
                    }
                    catch { }
                }
            });
        }

        // Priority 2: Apps, up to 4
        matchedApps.Sort((a, b) => b.Score.CompareTo(a.Score));
        foreach (var (app, score, indices) in matchedApps.Take(4))
        {
            var capturedApp = app;
            _searchResults.Add(new SearchResult
            {
                Title = app.Name,
                Subtitle = app.ExePath,
                Category = SearchCategory.Apps,
                Icon = app.Icon,
                FallbackLetter = app.Name.Length > 0 ? char.ToUpperInvariant(app.Name[0]).ToString() : "A",
                KeyboardHint = "Tab commands \u00B7 Enter open",
                Score = score,
                MatchedIndices = indices,
                AppId = app.AppId,
                ExePath = app.ExePath,
                Action = () =>
                {
                    try
                    {
                        AppDiscoveryService.Instance.RecordLaunch(capturedApp.AppId);
                        Process.Start(new ProcessStartInfo(capturedApp.ExePath) { UseShellExecute = true });
                        ShowActionSuccess($"✓ Opened {capturedApp.Name}");
                    }
                    catch { }
                }
            });
        }

        // Priority 3: Commands, up to 3
        var sysCmds = GetSystemCommands(query);
        _searchResults.AddRange(sysCmds.Take(3));

        // Priority 5: Web Search fallback
        _searchResults.Add(new SearchResult
        {
            Title = $"Search Google for \"{query}\"",
            Subtitle = "Open in default browser",
            Category = SearchCategory.Web,
            FallbackLetter = "G",
            FallbackColor = Color.FromRgb(0x42, 0x85, 0xF4),
            KeyboardHint = "Enter",
            Action = () => ExecuteWebSearch(query)
        });

        _selectedResultIndex = _searchResults.Count > 0 ? 0 : -1;
        RenderResultRows();

        // Priority 4: Files from Windows Search index (up to 3, async with debounce)
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        _ = Task.Run(async () =>
        {
            var files = await WindowsSearchService.SearchFilesAsync(query, token);
            if (token.IsCancellationRequested || files.Count == 0) return;

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested || _islandState != IslandVisualState.Search) return;

                int insertIdx = _searchResults.FindIndex(r => r.Category == SearchCategory.Web);
                if (insertIdx < 0) insertIdx = _searchResults.Count;

                foreach (var f in files.Take(3))
                {
                    _searchResults.Insert(insertIdx++, f);
                }
                RenderResultRows();
            });
        });
    }

    private List<SearchResult> GetSystemCommands(string query)
    {
        var list = new List<(SearchResult Item, int Score)>();
        var candidates = new (string Title, string Subtitle, bool IsRisky, Action Action)[]
        {
            ("Lock PC", "Lock Windows workstation", false, () => { try { Process.Start(new ProcessStartInfo("rundll32.exe", "user32.dll,LockWorkStation") { UseShellExecute = true }); ShowActionSuccess("✓ Locked PC"); } catch { } }),
            ("Sleep", "Put computer to sleep", false, () => { try { Process.Start(new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0") { UseShellExecute = true }); ShowActionSuccess("✓ Sleep"); } catch { } }),
            ("Restart", "Restart computer", true, () => { try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = true, CreateNoWindow = true }); ShowActionSuccess("✓ Restarting..."); } catch { } }),
            ("Shut Down", "Turn off computer", true, () => { try { Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = true, CreateNoWindow = true }); ShowActionSuccess("✓ Shutting down..."); } catch { } }),
            ("Task Manager", "View running processes and performance", false, () => { try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); ShowActionSuccess("✓ Opened Task Manager"); } catch { } }),
            ("Windows Settings", "Open system settings", false, () => { try { Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true }); ShowActionSuccess("✓ Opened Settings"); } catch { } }),
            ("Terminal", "Open Windows Terminal", false, () => { try { Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true }); ShowActionSuccess("✓ Opened Terminal"); } catch { } }),
        };

        foreach (var (title, subtitle, isRisky, action) in candidates)
        {
            var match = FuzzyMatcher.Match(title, query);
            if (match.Matched)
            {
                list.Add((new SearchResult
                {
                    Title = title,
                    Subtitle = subtitle,
                    Category = SearchCategory.Commands,
                    FallbackLetter = title[0].ToString(),
                    FallbackColor = Color.FromRgb(0x8A, 0x5C, 0xF6),
                    IsRisky = isRisky,
                    KeyboardHint = isRisky ? "Enter again to confirm" : "Enter",
                    Score = match.Score,
                    MatchedIndices = match.MatchedIndices,
                    Action = action
                }, match.Score));
            }
        }

        list.Sort((a, b) => b.Score.CompareTo(a.Score));
        return list.Select(x => x.Item).ToList();
    }

    private void ExecuteWebSearch(string query)
    {
        try
        {
            string url = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            ShowActionSuccess("✓ Searched Google");
        }
        catch { }
        CloseSearch();
    }

    private void ExecuteResult(SearchResult result)
    {
        if (result.IsRisky)
        {
            if (!_armedRiskyCommand || _armedCommandTitle != result.Title)
            {
                _armedRiskyCommand = true;
                _armedCommandTitle = result.Title;
                UpdateRowSelections();
                return;
            }
        }

        _armedRiskyCommand = false;
        _armedCommandTitle = "";

        try
        {
            result.Action?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to execute search result", ("error", ex.Message));
        }

        CloseSearch();
    }

    private void RenderResultRows()
    {
        if (_searchResults.Count == 0)
        {
            SearchResultsSection.Visibility = Visibility.Collapsed;
            SearchResultsList.Children.Clear();
            SearchResultsScroll.Height = double.NaN;
            AnimateIslandHeight(58.0);
            return;
        }

        SearchResultsSection.Visibility = Visibility.Visible;
        SearchResultsList.Children.Clear();

        var groups = _searchResults.GroupBy(r => r.Category);
        int itemIndex = 0;
        foreach (var group in groups)
        {
            string headerTitle = group.Key switch
            {
                SearchCategory.Calculator => "CALCULATOR",
                SearchCategory.Apps => "APPLICATIONS",
                SearchCategory.Commands => "COMMANDS",
                SearchCategory.Files => "FILES",
                SearchCategory.Web => "WEB SEARCH",
                _ => group.Key.ToString().ToUpperInvariant()
            };

            var headerBorder = new Border
            {
                Height = 22,
                Margin = new Thickness(8, itemIndex == 0 ? 2 : 6, 8, 2)
            };
            var headerText = new TextBlock
            {
                Text = headerTitle,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("WdbTextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            headerBorder.Child = headerText;
            SearchResultsList.Children.Add(headerBorder);

            foreach (var item in group)
            {
                int currentIndex = itemIndex++;
                var rowBorder = CreateResultRowBorder(item, currentIndex);
                SearchResultsList.Children.Add(rowBorder);
            }
        }

        SearchResultsSection.UpdateLayout();
        double desiredH = SearchResultsList.ActualHeight;
        if (desiredH <= 0) desiredH = _searchResults.Count * 44 + 40;
        double scrollH = Math.Min(330, desiredH + 10);
        SearchResultsScroll.Height = scrollH;
        double targetIslandH = 58 + 1 + scrollH + 16;
        AnimateIslandHeight(targetIslandH);
    }

    private Border CreateResultRowBorder(SearchResult item, int index)
    {
        bool isSel = index == _selectedResultIndex;
        var rowBorder = new Border
        {
            Height = 44,
            CornerRadius = new CornerRadius(8),
            Background = isSel ? new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)) : Brushes.Transparent,
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(10, 0, 12, 0),
            Tag = index,
            Cursor = Cursors.Hand
        };

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 1. Icon (28px rounded)
        var iconBorder = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };

        if (item.Icon != null)
        {
            iconBorder.Child = new Image
            {
                Source = item.Icon,
                Width = 22,
                Height = 22,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            iconBorder.Background = new SolidColorBrush(item.FallbackColor ?? Color.FromRgb(70, 70, 80));
            iconBorder.Child = new TextBlock
            {
                Text = item.FallbackLetter ?? "•",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        // 2. Title & Subtitle
        var textStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center
        };

        var titleBlock = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        RenderHighlightedText(titleBlock, item.Title, item.MatchedIndices);
        textStack.Children.Add(titleBlock);

        var subtitleBlock = new TextBlock
        {
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Cascadia Code, monospace"),
            Foreground = (Brush)FindResource("WdbTextMutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = item.Subtitle
        };
        textStack.Children.Add(subtitleBlock);

        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        // 3. Keyboard Hint (Selected row only)
        var hintBlock = new TextBlock
        {
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Cascadia Code, monospace"),
            Foreground = (Brush)FindResource("WdbTextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Text = GetItemHint(item),
            Visibility = isSel ? Visibility.Visible : Visibility.Collapsed
        };
        Grid.SetColumn(hintBlock, 2);
        grid.Children.Add(hintBlock);

        rowBorder.Child = grid;

        rowBorder.MouseEnter += (s, e) =>
        {
            if (s is Border b && b.Tag is int idx)
            {
                _selectedResultIndex = idx;
                _armedRiskyCommand = false;
                _armedCommandTitle = "";
                UpdateRowSelections();
            }
        };

        rowBorder.MouseDown += (s, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;
                ExecuteResult(item);
            }
        };

        return rowBorder;
    }

    private string GetItemHint(SearchResult item)
    {
        if (item.IsRisky)
        {
            if (_armedRiskyCommand && _armedCommandTitle == item.Title)
                return "Enter again to confirm";
            return "Enter";
        }
        if (item.Category == SearchCategory.Apps)
            return "Tab commands \u00B7 Enter open";
        return item.KeyboardHint ?? "Enter";
    }

    private void RenderHighlightedText(TextBlock tb, string fullText, List<int>? matchedIndices)
    {
        tb.Inlines.Clear();
        if (matchedIndices == null || matchedIndices.Count == 0)
        {
            tb.Inlines.Add(new Run(fullText) { Foreground = (Brush)FindResource("WdbTextBrush") });
            return;
        }

        var matchSet = new HashSet<int>(matchedIndices);
        var accentBrush = (Brush)FindResource("WdbAccentBrush");
        var textBrush = (Brush)FindResource("WdbTextBrush");

        int i = 0;
        while (i < fullText.Length)
        {
            bool isMatch = matchSet.Contains(i);
            int start = i;
            while (i < fullText.Length && matchSet.Contains(i) == isMatch)
            {
                i++;
            }
            string part = fullText.Substring(start, i - start);
            if (isMatch)
            {
                tb.Inlines.Add(new Run(part)
                {
                    Foreground = accentBrush,
                    FontWeight = FontWeights.Bold
                });
            }
            else
            {
                tb.Inlines.Add(new Run(part)
                {
                    Foreground = textBrush
                });
            }
        }
    }

    private void UpdateRowSelections()
    {
        foreach (UIElement child in SearchResultsList.Children)
        {
            if (child is Border b && b.Tag is int idx)
            {
                bool isSel = idx == _selectedResultIndex;
                b.Background = isSel ? new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)) : Brushes.Transparent;
                if (b.Child is Grid grid && grid.Children.Count >= 3 && grid.Children[2] is TextBlock hintBlock)
                {
                    hintBlock.Visibility = isSel ? Visibility.Visible : Visibility.Collapsed;
                    if (isSel && idx >= 0 && idx < _searchResults.Count)
                    {
                        hintBlock.Text = GetItemHint(_searchResults[idx]);
                    }
                }
            }
        }
    }

    private void ScrollSelectedIntoView()
    {
        foreach (UIElement child in SearchResultsList.Children)
        {
            if (child is Border b && b.Tag is int idx && idx == _selectedResultIndex)
            {
                b.BringIntoView();
                break;
            }
        }
    }

    private void AnimateIslandHeight(double targetH)
    {
        if (_cfg.ReduceMotion)
        {
            ApplyH(targetH);
            return;
        }
        _anim.Animate("h", _currentH, targetH, ApplyH);
    }

    private void ShowActionSuccess(string message)
    {
        _actionSuccessTimer?.Stop();
        CollapsedTitle.Text = message;
        _actionSuccessTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _actionSuccessTimer.Tick -= OnActionSuccessTick;
        _actionSuccessTimer.Tick += OnActionSuccessTick;
        _actionSuccessTimer.Start();
    }

    private void OnActionSuccessTick(object? sender, EventArgs e)
    {
        _actionSuccessTimer?.Stop();
        UpdateCollapsed();
    }

}
