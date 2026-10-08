using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Animation;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Interop;
using WindowsDynamicBar.Services.Media;
using WindowsDynamicBar.Services.Privacy;
using WindowsDynamicBar.Services.SystemInfo;
using WindowsDynamicBar.Services.SystemMonitor;
using WindowsDynamicBar.Services.Timers;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace WindowsDynamicBar.UI;

public partial class BarWindow : Window, IDisposable
{
    // ---- geometry (DIPs) ----
    private const double WinW = 520;
    private const double WinH = 250;
    private const double PillTop = 6;
    private const double MaxPillW = 440;
    private const double MaxPillH = 210;

    // ---- telemetry zone geometry (DIPs) ----
    private const double RailZoneW = 130;
    private const double DetailZoneW = 272;
    private const double ZonePanelH = 96;

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
    private bool _overPill;
    private bool _pendingCollapse;
    private bool _userDismissed;
    private DateTime _dismissedAt;
    private double _currentW;
    private double _currentH;
    private double _maxOffsetDip;
    private bool _dragMoved;
    private double _dragStartX, _dragStartOffset;
    private string _levelMode = "volume";
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

    // telemetry zone state
    private double _zoneW;
    private TelemetryZoneState _zoneState = TelemetryZoneState.Hidden;
    private TelemetryAction _pendingAction = TelemetryAction.None;
    private TelemetryTarget _pendingMetric = TelemetryTarget.Cpu;
    private DateTime _pendingSince;
    private TelemetrySnapshot? _telemetry;
    private readonly DispatcherTimer _zoneProbe = new() { Interval = TimeSpan.FromMilliseconds(80) };

    // ---- clock & hover-expand & stats panel ----
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _hoverExpanded;
    private string _statsPanelTarget = "";
    private bool _statsPanelOpen;
    private const double StatsPanelW = 220;
    private double _statsPanelW;

    public BarWindow()
    {
        InitializeComponent();

        Width = WinW;
        Height = WinH;

        _cfg = App.Settings.Load();
        _currentW = Math.Max(80, _cfg.BarWidth);
        _currentH = Math.Max(24, _cfg.BarHeight);
        Pill.Width = _currentW;
        Pill.Height = _currentH;
        UpdatePillMargin();

        _anim = CreateAnimator(_cfg);
        _blurOn = _cfg.BlurEnabled;

        // rounded clip for artwork orbs
        HeaderArt.Clip = new RectangleGeometry(new Rect(0, 0, 34, 34), 17, 17);
        OrbDisc.ReduceMotion = _cfg.ReduceMotion;

        _zoneProbe.Tick += OnZoneProbe;
        if (_cfg.FeatureSystemMonitor && App.Telemetry is not null) _zoneProbe.Start();

        ProgressTrack.SizeChanged += (_, _) => SyncFills();
        LevelRow.SizeChanged += (_, _) => SyncFills();

        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();
        BigDisc.ReduceMotion = _cfg.ReduceMotion;

        Pill.MouseEnter += OnPillEnter;
        Pill.MouseLeave += OnPillLeave;

        _idleCollapse.Tick += (_, _) =>
        {
            _idleCollapse.Stop();
            if (!_overPill && App.Bus?.Active is null && _expanded) Collapse();
        };

        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _fullscreenTimer.Tick += OnFullscreenTick;

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
            if (App.Settings is not null) App.Settings.SettingsChanged -= OnSettingsChanged;
        }
        catch { /* teardown is best-effort */ }
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
        try { _zoneProbe.Stop(); } catch { }
        try { _clockTimer.Stop(); } catch { }
        try { _fullscreenTimer?.Stop(); } catch { }
        try { _idleCollapse.Stop(); } catch { }
        CancelDelays();
        _fadeCollapsed.Stop(); _fadeExpanded.Stop();
        _anim.Dispose();
        try
        {
            for (int id = 1; id <= 3; id++) NativeMethods.UnregisterHotKey(_hwnd, id);
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
            double zoneLeft = Math.Max(40, (WinW - _currentW) / 2);
            double left = zoneLeft;
            double w = _zoneW > 0.5 ? _zoneW + _currentW : _currentW;
            double h = _zoneW > 0.5 && ZoneMetricOpen ? ZonePanelH : _currentH;
            double minDim = Math.Min(Math.Max(0, _currentW), Math.Max(0, _currentH));
            double r = Math.Max(0, Math.Min(Pill.CornerRadius.TopLeft, minDim / 2));
            if (InRoundedRect(p.X, p.Y, left, PillTop, w, h, r))
                return new IntPtr(NativeMethods.HTCLIENT);
            if (_statsPanelOpen && _statsPanelW > 0.5)
            {
                double spLeft = _zoneW + zoneLeft + _currentW;
                if (InRoundedRect(p.X, p.Y, spLeft, PillTop, _statsPanelW, _currentH, 22))
                    return new IntPtr(NativeMethods.HTCLIENT);
            }
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

            double totalWidthDip = WinW + _zoneW + (_statsPanelOpen && _statsPanelW > 0.5 ? _statsPanelW : 0);
            int wPx = (int)Math.Round(totalWidthDip * scale);
            int hPx = (int)Math.Round(WinH * scale);
            int boundsW = mon.Bounds.Right - mon.Bounds.Left;
            _maxOffsetDip = Math.Max(0, (boundsW - wPx) / 2.0 / scale - 8);

            // Clamp a previously saved offset in case the target monitor shrank.
            double clamped = Math.Clamp(offsetDip, -_maxOffsetDip, _maxOffsetDip);
            int centerX = mon.Bounds.Left + boundsW / 2;
            int x = centerX + (int)Math.Round(clamped * scale)
                - (int)Math.Round((WinW + 2 * _zoneW) * scale / 2.0);
            int y = mon.Bounds.Top;
            Width = totalWidthDip;
            Height = WinH;
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, wPx, hPx,
                NativeMethods.SWP_NOACTIVATE);
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
        double zoneLeft = Math.Max(40, (WinW - _currentW) / 2);
        double l = zoneLeft * scale;
        double t = PillTop * scale;
        double totalW = (_zoneW > 0.5 ? _zoneW + _currentW : _currentW) + (_statsPanelOpen && _statsPanelW > 0.5 ? _statsPanelW : 0);
        double w = totalW * scale;
        double h = (_zoneW > 0.5 && ZoneMetricOpen ? ZonePanelH : _currentH) * scale;
        int li = (int)Math.Round(l);
        int ti = (int)Math.Round(t);
        int wi = (int)Math.Round(w);
        int hi = (int)Math.Round(h);
        if (li == _blurL && ti == _blurT && wi == _blurW && hi == _blurH) return;
        _blurL = li; _blurT = ti; _blurW = wi; _blurH = hi;
        BlurHelper.UpdateRegion(li, ti, wi, hi, (int)Math.Round(_cfg.CornerRadius * scale), true);
    }

    private void UpdateShape()
    {
        double minDim = Math.Min(Math.Max(0, _currentW), Math.Max(0, _currentH));
        double r = Math.Max(0, Math.Min(_cfg.CornerRadius, minDim / 2));
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

    private bool ZoneMetricOpen => _zoneState is TelemetryZoneState.Cpu or TelemetryZoneState.Gpu or TelemetryZoneState.Ram;

    private void UpdatePillMargin()
    {
        double zoneLeft = Math.Max(40, (WinW - _currentW) / 2);
        double left = _zoneW + zoneLeft;
        Pill.Margin = new Thickness(left, PillTop, 40, 0);
        ZoneHost.Margin = new Thickness(zoneLeft, PillTop, 0, 0);
        double railTop = PillTop + Math.Max(0, (ZoneHost.Height - 41) / 2);
        if (RailRow.Margin.Top != railTop) RailRow.Margin = new Thickness(0, railTop, 0, 0);
    }

    // ---------------------------------------------------------------- expand / collapse

    private static double TargetWidth(BarEventKind kind) => kind switch
    {
        BarEventKind.Media => 420,
        BarEventKind.Volume or BarEventKind.Brightness => 300,
        BarEventKind.Download => 380,
        BarEventKind.SystemMonitor => 360,
        BarEventKind.Timer or BarEventKind.Stopwatch or BarEventKind.Focus => 330,
        _ => 340,
    };

    private void Expand()
    {
        if (_shown is null) return;
        HideZoneNow();
        double w = Math.Clamp(TargetWidth(_shown.Kind), 120, MaxPillW);

        ExpandedHost.Visibility = Visibility.Visible;
        ExpandedHost.Measure(new Size(w, 10000));
        double h = Math.Clamp(
            Math.Max(_cfg.BarHeight, ExpandedHost.DesiredSize.Height), 40, MaxPillH);

        CancelDelays();
        _expanded = true;
        _pendingCollapse = false;
        _idleCollapse.Stop();

        Fade(CollapsedPanel, 0, 90);
        _delays.Add(RunAfter(70, () =>
            Fade(ExpandedHost, 1, 160)));

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
        if (_hoverExpanded)
        {
            _hoverExpanded = false;
            Fade(ExpandedHoverHost, 0, 110, () =>
            {
                if (!_expanded) ExpandedHoverHost.Visibility = Visibility.Collapsed;
            });
        }
        if (_statsPanelOpen) CloseStatsPanel();
        CollapsedPanel.Visibility = Visibility.Visible;
        _delays.Add(RunAfter(60, () => Fade(CollapsedPanel, 1, 140)));

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

    private bool ZoneGrabHover() =>
        _cfg.FeatureSystemMonitor && App.Telemetry is not null && !_expanded &&
        App.Bus?.Active is null && !App.GamingMode && !AnyPriorityWidget();

    private bool AnyPriorityWidget() =>
        _lastTimer is { State: TimerState.Running } ||
        _lastFocus is { Running: true } ||
        _lastStopwatch is { Running: true } ||
        _lastMedia is { IsPlaying: true };

    private void OnTelemetryUpdated(TelemetrySnapshot snap)
    {
        try { Dispatcher.BeginInvoke(() => ApplyTelemetry(snap)); } catch { }
    }

    private void ApplyTelemetry(TelemetrySnapshot snap)
    {
        _telemetry = snap;
        if (_zoneState != TelemetryZoneState.Hidden) RenderZoneValues();
        UpdateGauges();
    }

    private void OnZoneProbe(object? sender, EventArgs e)
    {
        try
        {
            if (_cfg.FeatureSystemMonitor is false || _expanded || App.Telemetry is null || App.GamingMode)
            {
                _pendingAction = TelemetryAction.None;
                return;
            }
            if (AnyPriorityWidget())
            {
                if (_zoneState != TelemetryZoneState.Hidden) HideZoneNow();
                return;
            }
            if (App.Bus?.Active is not null)
            {
                if (_zoneState != TelemetryZoneState.Hidden) HideZoneNow();
                return;
            }
            var target = ZoneTargetAtCursor();
            var action = TelemetryPolicy.Plan(_zoneState, target);
            if (action == TelemetryAction.None) { _pendingAction = TelemetryAction.None; return; }
            if (action == TelemetryAction.OpenMetric) _pendingMetric = target;
            if (action != _pendingAction) { _pendingAction = action; _pendingSince = DateTime.UtcNow; }
            int delay = action switch
            {
                TelemetryAction.OpenRail => TelemetryPolicy.OpenRailMs,
                TelemetryAction.OpenMetric => TelemetryPolicy.OpenMetricMs,
                TelemetryAction.CloseMetricToRail => TelemetryPolicy.CloseMetricMs,
                TelemetryAction.HideAll => ZoneMetricOpen
                    ? TelemetryPolicy.CloseMetricMs + TelemetryPolicy.CloseRailMs
                    : TelemetryPolicy.CloseRailMs,
                _ => 0,
            };
            if ((DateTime.UtcNow - _pendingSince).TotalMilliseconds >= delay)
                ExecuteZoneAction(action);
        }
        catch (Exception ex) { Log.Debug("Zone probe failed", ("error", ex.Message)); }
    }

    private void ExecuteZoneAction(TelemetryAction action)
    {
        _pendingAction = TelemetryAction.None;
        switch (action)
        {
            case TelemetryAction.OpenRail: OpenZoneRail(); break;
            case TelemetryAction.OpenMetric: OpenZoneMetric(_pendingMetric); break;
            case TelemetryAction.CloseMetricToRail: CloseZoneMetric(); break;
            case TelemetryAction.HideAll: HideZone(); break;
        }
    }

    private static TelemetryZoneState StateOf(TelemetryTarget t) => t switch
    {
        TelemetryTarget.Cpu => TelemetryZoneState.Cpu,
        TelemetryTarget.Gpu => TelemetryZoneState.Gpu,
        TelemetryTarget.Ram => TelemetryZoneState.Ram,
        _ => TelemetryZoneState.Rail,
    };

    private static TelemetryTarget TargetOf(TelemetryZoneState s) => s switch
    {
        TelemetryZoneState.Cpu => TelemetryTarget.Cpu,
        TelemetryZoneState.Gpu => TelemetryTarget.Gpu,
        TelemetryZoneState.Ram => TelemetryTarget.Ram,
        _ => TelemetryTarget.Rail,
    };

    private TelemetryTarget ZoneTargetAtCursor()
    {
        if (_hwnd == IntPtr.Zero || _cfg.FeatureSystemMonitor is false) return TelemetryTarget.None;
        NativeMethods.GetCursorPos(out var pt);
        var wpt = PointFromScreen(new Point(pt.X, pt.Y));
        double zoneLeft = Math.Max(40, (WinW - _currentW) / 2);
        double zoneRight = zoneLeft + _zoneW;
        double pillLeft = zoneRight;
        double pillRight = pillLeft + _currentW;
        double regionBottom = PillTop + (_zoneW > 0.5 && ZoneMetricOpen ? ZonePanelH : _currentH);

        double regionLeft = _zoneW > 0.5 ? zoneLeft : pillLeft;
        if (wpt.X < regionLeft || wpt.X > pillRight || wpt.Y < PillTop || wpt.Y > regionBottom)
            return TelemetryTarget.None;

        if (_zoneW > 0.5)
        {
            double railLeft = zoneRight - RailZoneW;
            double top = PillTop + Math.Max(0, (ZoneHost.Height - 41) / 2);
            if (wpt.X >= railLeft && wpt.X <= zoneRight)
            {
                if (wpt.Y >= top && wpt.Y <= top + 34)
                {
                    TelemetryTarget[] seq = { TelemetryTarget.Cpu, TelemetryTarget.Gpu, TelemetryTarget.Ram };
                    for (int i = 0; i < 3; i++)
                    {
                        double x = railLeft + 7 + 40 * i;
                        if (wpt.X >= x && wpt.X <= x + 34) return seq[i];
                    }
                }
                return TelemetryTarget.Rail;
            }
            if (ZoneMetricOpen && wpt.X >= zoneLeft && wpt.X < railLeft)
                return TargetOf(_zoneState);
        }

        if (wpt.X >= pillLeft && wpt.X <= pillRight) return TelemetryTarget.Pill;
        return TelemetryTarget.None;
    }

    private void OpenZoneRail()
    {
        if (_zoneState != TelemetryZoneState.Hidden) return;
        _zoneState = TelemetryZoneState.Rail;
        ZoneHost.Visibility = Visibility.Visible;
        EnsureTelemetry();
        _anim.Animate("zx", _zoneW, RailZoneW, ApplyZoneW);
        _anim.Animate("zh", ZoneHost.Height, _currentH, ApplyZoneH);
    }

    private void OpenZoneMetric(TelemetryTarget target)
    {
        var kind = StateOf(target);
        bool fromHidden = _zoneState == TelemetryZoneState.Hidden;
        _zoneState = kind;
        if (fromHidden) ZoneHost.Visibility = Visibility.Visible;
        EnsureTelemetry();
        if (DetailPanelComponent.Visibility != Visibility.Visible)
        {
            DetailPanelComponent.Visibility = Visibility.Visible;
            DetailPanelComponent.Opacity = 0;
        }
        RenderZoneValues();
        _anim.Animate("zx", _zoneW, RailZoneW + DetailZoneW, ApplyZoneW);
        _anim.Animate("zh", ZoneHost.Height, ZonePanelH, ApplyZoneH);
        if (DetailPanelComponent.Opacity < 0.05)
        {
            if (_cfg.ReduceMotion) DetailPanelComponent.Opacity = 1;
            else
            {
                var tw = new TweenAnimation();
                _activeFades.Add(tw);
                tw.Run(0, 1, TimeSpan.FromMilliseconds(180), v => DetailPanelComponent.Opacity = v, Easing.CubicOut);
                tw.Completed += () => { DetailPanelComponent.Opacity = 1; _activeFades.Remove(tw); };
            }
        }
    }

    private void CloseZoneMetric()
    {
        if (!ZoneMetricOpen) return;
        _zoneState = TelemetryZoneState.Rail;
        if (_cfg.ReduceMotion)
        {
            DetailPanelComponent.Opacity = 0;
            DetailPanelComponent.Visibility = Visibility.Collapsed;
        }
        else
        {
            var tw = new TweenAnimation();
            _activeFades.Add(tw);
            tw.Run(DetailPanelComponent.Opacity, 0, TimeSpan.FromMilliseconds(150), v => DetailPanelComponent.Opacity = v, Easing.CubicOut);
            tw.Completed += () =>
            {
                DetailPanelComponent.Visibility = Visibility.Collapsed;
                _activeFades.Remove(tw);
            };
        }
        _anim.Animate("zx", _zoneW, RailZoneW, ApplyZoneW);
        _anim.Animate("zh", ZoneHost.Height, _currentH, ApplyZoneH);
    }

    private void HideZone()
    {
        if (_zoneState == TelemetryZoneState.Hidden) return;
        _zoneState = TelemetryZoneState.Hidden;
        _pendingAction = TelemetryAction.None;
        DetailPanelComponent.Opacity = 0;
        DetailPanelComponent.Visibility = Visibility.Collapsed;
        _anim.Animate("zx", _zoneW, 0, ApplyZoneW);
        _anim.Animate("zh", ZoneHost.Height, _currentH, ApplyZoneH);
        RunAfter(240, () =>
        {
            if (_zoneState != TelemetryZoneState.Hidden) return;
            ZoneHost.Visibility = Visibility.Collapsed;
            try { if (App.Telemetry is { IsActive: true }) App.Telemetry.Deactivate(); } catch { }
            _telemetry = null;
        });
    }

    private void HideZoneNow()
    {
        if (_zoneState == TelemetryZoneState.Hidden) return;
        _zoneState = TelemetryZoneState.Hidden;
        _pendingAction = TelemetryAction.None;
        DetailPanelComponent.Opacity = 0;
        DetailPanelComponent.Visibility = Visibility.Collapsed;
        _anim.Jump("zx", 0, ApplyZoneW);
        _anim.Jump("zh", _currentH, ApplyZoneH);
        ZoneHost.Visibility = Visibility.Collapsed;
        try { if (App.Telemetry is { IsActive: true }) App.Telemetry.Deactivate(); } catch { }
        _telemetry = null;
    }

    private void EnsureTelemetry()
    {
        try { if (App.Telemetry is { IsActive: false }) App.Telemetry.Activate(); } catch { }
    }

    private void ApplyZoneW(double v)
    {
        v = Math.Max(0, v);
        _zoneW = v;
        bool active = v > 0.5;
        if (active) ZoneHost.Visibility = Visibility.Visible;
        else if (_zoneState == TelemetryZoneState.Hidden) ZoneHost.Visibility = Visibility.Collapsed;
        ZoneHost.Width = v;
        DetailCol.Width = new GridLength(Math.Max(0, v - RailZoneW));
        UpdatePillMargin();
        Reposition();
        RefreshBlur();
    }

    private void ApplyZoneH(double v)
    {
        v = Math.Max(0, v);
        ZoneHost.Height = v;
        UpdatePillMargin();
        RefreshBlur();
    }

    private void RenderZoneValues()
    {
        var s = _telemetry;
        if (s is null) return;
        SetTextFast(RailCpuText, FmtPct(s.Cpu.Available ? s.Cpu.Utilization : null));
        var gpus = App.Telemetry?.SnapshotGpuTracks();
        SetTextFast(RailGpuText, FmtPct(gpus is { Count: > 0 } ? gpus[0].Utilization : null));
        SetTextFast(RailRamText, FmtPct(s.Memory.Available ? s.Memory.UsedPercent : null));
        if (ZoneMetricOpen) RenderZoneDetail(s);
    }

    private static string FmtPct(double? v) =>
        v is null || double.IsNaN(v.Value) ? "n/a" : $"{Math.Round(v.Value)}%";

    private static void SetTextFast(TextBlock t, string v)
    {
        if (t.Text != v) t.Text = v;
    }

    private void RenderZoneDetail(TelemetrySnapshot s)
    {
        switch (_zoneState)
        {
            case TelemetryZoneState.Cpu:
                DetailPanelComponent.ShowCpu(s.Cpu, App.Telemetry?.CpuHistory);
                break;

            case TelemetryZoneState.Gpu:
                var gpus = App.Telemetry?.SnapshotGpuTracks() ?? new List<GpuTrack>();
                if (gpus.Count > 0)
                {
                    var g = gpus[0];
                    var gpuStats = new GpuAdapterStats(g.Luid, g.Name, g.Utilization, g.VramUsed, g.VramTotal, g.VramAvailable);
                    DetailPanelComponent.ShowGpu(gpuStats, g.History);
                }
                else
                {
                    DetailPanelComponent.ShowGpu(new GpuAdapterStats(0, "No GPU", 0, 0, 0, false), null);
                }
                break;

            case TelemetryZoneState.Ram:
                DetailPanelComponent.ShowRam(s.Memory, App.Telemetry?.RamHistory);
                break;
        }
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
            HideZoneNow();

            // Refresh in place when the same widget is already on screen.
            if (_shown?.Kind == evt.Kind && _expanded)
            {
                _shown = evt;
                RenderEvent(evt);
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
        if (_overPill) _pendingCollapse = true;
        else if (_expanded) Collapse();
    }

    private void OnMediaChanged(MediaInfo info)
    {
        _lastMedia = info;
        if (!_expanded)
        {
            UpdateCollapsed();
            return;
        }
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
            _cfg.CollapseDelayMs = m.CollapseDelayMs;
            _cfg.HoverPreview = m.HoverPreview;
            _cfg.SystemMonitorInBar = m.SystemMonitorInBar;
            _cfg.CollapsedMediaArt = m.CollapsedMediaArt;
            _cfg.FeatureSystemMonitor = m.FeatureSystemMonitor;
            if (_cfg.FeatureSystemMonitor && App.Telemetry is not null && !_zoneProbe.IsEnabled)
                _zoneProbe.Start();
            else if (!_cfg.FeatureSystemMonitor && _zoneProbe.IsEnabled)
            {
                _zoneProbe.Stop();
                HideZoneNow();
            }
            _cfg.AutoHideFullscreen = m.AutoHideFullscreen;
            _cfg.BarEnabled = m.BarEnabled;
            _cfg.MonitorIndex = m.MonitorIndex;
            _cfg.CustomOffsetX = m.CustomOffsetX;
            _cfg.HotkeyToggle = m.HotkeyToggle;
            _cfg.HotkeyMedia = m.HotkeyMedia;
            _cfg.HotkeyTimer = m.HotkeyTimer;

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

    private void OnPillEnter(object sender, MouseEventArgs e)
    {
        _overPill = true;
        _pendingCollapse = false;
        _idleCollapse.Stop();
        if (ZoneGrabHover()) return;
        if (_expanded || !_cfg.HoverPreview) return;
        if (App.Bus?.Active is BarEvent active)
        {
            _shown = active;
            _userDismissed = false;
            RenderEvent(active);
            Expand();
            return;
        }
        // Hover-expand: show big disc + music info + 2x3 gauge grid
        if (!_expanded && !ZoneGrabHover())
        {
            _hoverExpanded = true;
            var m = _lastMedia;
            bool playing = m?.IsPlaying ?? false;
            string title = m is not null && !string.IsNullOrWhiteSpace(m.Title) ? m.Title : "Dynamic";
            string artist = m is not null && !string.IsNullOrWhiteSpace(m.Artist) ? m.Artist : "Windows Dynamic Bar";
            var art = m is not null ? BytesToImage(m.ArtworkBytes) : null;
            BigDisc.SetArtwork(art, playing);
            BigDisc.ReduceMotion = _cfg.ReduceMotion;
            HoverTitle.Text = title;
            HoverArtist.Text = artist;
            double prog = m?.Progress ?? 0;
            HoverProgressFill.Width = 0;
            if (HoverProgressTrack.ActualWidth > 0)
                HoverProgressFill.Width = Math.Clamp(prog, 0, 1) * HoverProgressTrack.ActualWidth;
            GaugeGrid.Visibility = Visibility.Visible;
            EnsureTelemetry();
            UpdateGauges();
            ExpandedHoverHost.Visibility = Visibility.Visible;
            HideZoneNow();
            double w = Math.Clamp(440, 120, MaxPillW);
            ExpandedHoverHost.Measure(new Size(w, 10000));
            double h = Math.Clamp(
                Math.Max(_cfg.BarHeight, ExpandedHoverHost.DesiredSize.Height + 20), 40, MaxPillH);
            CancelDelays();
            _expanded = true;
            _pendingCollapse = false;
            _idleCollapse.Stop();
            Fade(CollapsedPanel, 0, 90);
            _delays.Add(RunAfter(70, () => Fade(ExpandedHoverHost, 1, 160)));
            _anim.Animate("w", _currentW, w, ApplyW);
            _anim.Animate("h", _currentH, h, ApplyH);
        }
    }

    private void OnPillLeave(object sender, MouseEventArgs e)
    {
        _overPill = false;
        if (_pendingCollapse && _expanded)
        {
            _pendingCollapse = false;
            if (App.Bus?.Active is null) UserDismiss(clearBus: false);
            else Collapse();
            return;
        }
        if (_expanded && App.Bus?.Active is null)
        {
            _idleCollapse.Interval = TimeSpan.FromMilliseconds(Math.Max(600, _cfg.CollapseDelayMs));
            _idleCollapse.Start();
        }
    }

    private void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        NativeMethods.GetCursorPos(out var pt);
        _dragStartX = pt.X;
        _dragStartOffset = _cfg.CustomOffsetX;
        _dragMoved = false;
        Pill.CaptureMouse();
    }

    private void OnPillMouseMove(object sender, MouseEventArgs e)
    {
        if (!Pill.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        NativeMethods.GetCursorPos(out var pt);
        if (!_dragMoved)
        {
            if (Math.Abs(pt.X - _dragStartX) < 5) return;
            _dragMoved = true;
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
        if (e.ChangedButton != MouseButton.Left) return;
        if (_dragMoved)
        {
            _dragMoved = false;
            double saved = _cfg.CustomOffsetX;
            try { App.Settings?.Update(s => s.CustomOffsetX = saved); } catch { }
            return;
        }
        Toggle();
    }

    private void OnPillMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (App.Volume is null) return;
        float delta = e.Delta > 0 ? 0.02f : -0.02f;
        float cur = App.Volume.Level;
        float next = Math.Clamp(cur + delta, 0f, 1f);
        App.Volume.SetLevel(next);
        e.Handled = true;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_expanded && !_dragMoved) UserDismiss();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _expanded)
        {
            e.Handled = true;
            UserDismiss();
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
        for (int id = 1; id <= 3; id++)
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, id); } catch { }
        }
        if (TryParseHotkey(_cfg.HotkeyToggle, out uint m1, out uint v1))
            NativeMethods.RegisterHotKey(_hwnd, 1, m1 | NativeMethods.MOD_NOREPEAT, v1);
        if (TryParseHotkey(_cfg.HotkeyMedia, out uint m2, out uint v2))
            NativeMethods.RegisterHotKey(_hwnd, 2, m2 | NativeMethods.MOD_NOREPEAT, v2);
        if (TryParseHotkey(_cfg.HotkeyTimer, out uint m3, out uint v3))
            NativeMethods.RegisterHotKey(_hwnd, 3, m3 | NativeMethods.MOD_NOREPEAT, v3);
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
        if (key.Equals("Space", StringComparison.OrdinalIgnoreCase)) { vk = 0x20; return true; }
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
        LevelRow.Visibility = Visibility.Collapsed;
        LevelCaption.Visibility = Visibility.Collapsed;
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

        HeaderGlyph.Text = isVolume ? (muted ? IcMute : IcVolume) : IcBrightness;
        HeaderText.Text = isVolume ? "Volume" : "Brightness";
        HeaderSub.Text = dev ?? (isVolume ? "Default output" : "Internal display");
        HeaderValue.Text = evt.ValueText ?? $"{Math.Round(Math.Clamp(level, 0, 1) * 100)}%";
        HeaderValue.Visibility = Visibility.Visible;

        LevelRow.Visibility = Visibility.Visible;
        LevelGlyph.Text = isVolume ? (muted ? IcMute : IcVolume) : IcBrightness;
        _syncing = true;
        try { LevelSlider.Value = Math.Clamp(level, 0, 1); }
        finally { _syncing = false; }

        LevelCaption.Visibility = Visibility.Visible;
        LevelCaption.Text = isVolume
            ? (muted ? "Muted" : (dev is null ? "Default output" : dev))
            : "Internal panel (WMI)";
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
        else if (_lastMedia is { } m && cfg.CollapsedMediaArt)
        {
            CollapsedTitle.Text = string.IsNullOrWhiteSpace(m.Title) ? "Playing" : m.Title;
            ShowDisc(m);
            var bigArt = BytesToImage(m.ArtworkBytes);
            BigDisc.SetArtwork(bigArt, m.IsPlaying);
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

    private void ShowDisc(MediaInfo m)
    {
        OrbArt.Visibility = Visibility.Collapsed;
        OrbGlyph.Visibility = Visibility.Collapsed;
        var art = BytesToImage(m.ArtworkBytes);
        OrbDisc.SetArtwork(art, m.IsPlaying);
        OrbDisc.Visibility = Visibility.Visible;
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

    private static string FormatTime(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ---------------------------------------------------------------- slider wiring

    private void SyncFills()
    {
        if (ProgressSlider.ActualWidth > 0)
            ProgressFill.Width = Math.Clamp(ProgressSlider.Value, 0, 1) * ProgressSlider.ActualWidth;
        if (LevelSlider.ActualWidth > 0)
            LevelFill.Width = Math.Clamp(LevelSlider.Value, 0, 1) * LevelSlider.ActualWidth;
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

    private void OnLevelSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LevelSlider.ActualWidth > 0)
            LevelFill.Width = Math.Clamp(e.NewValue, 0, 1) * LevelSlider.ActualWidth;
        if (_syncing) return;
        double v = Math.Clamp(e.NewValue, 0, 1);
        try
        {
            if (_levelMode == "volume") App.Volume?.SetLevel((float)v);
            else App.Brightness?.SetLevel((float)v);
        }
        catch (Exception ex) { Log.Debug("Set level failed", ("error", ex.Message)); }
    }

    // ---------------------------------------------------------------- buttons

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        try { _ = App.Media?.PreviousAsync(); } catch { }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        try { _ = App.Media?.PlayPauseAsync(); } catch { }
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

    private void UpdateGauges()
    {
        var snap = _telemetry;
        if (snap is null) return;
        var gpus = App.Telemetry?.SnapshotGpuTracks();
        SetTextFast(GaugeCpuPct, FmtPct(snap.Cpu.Available ? snap.Cpu.Utilization : null));
        SetTextFast(GaugeGpuPct, FmtPct(gpus is { Count: > 0 } ? gpus[0].Utilization : null));
        SetTextFast(GaugeRamPct, FmtPct(snap.Memory.Available ? snap.Memory.UsedPercent : null));
        if (_statsPanelOpen) RenderStatsPanelContent(_statsPanelTarget);
    }

    private void OnGaugeCpuClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OnGaugeClick("CPU");
    }

    private void OnGaugeGpuClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OnGaugeClick("GPU");
    }

    private void OnGaugeRamClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OnGaugeClick("RAM");
    }

    private void OnGaugeClick(string target)
    {
        if (_statsPanelOpen && _statsPanelTarget == target)
        {
            CloseStatsPanel();
            return;
        }
        _statsPanelTarget = target;
        OpenStatsPanel(target);
    }

    private void OpenStatsPanel(string target)
    {
        _statsPanelOpen = true;
        StatsPanelTitle.Text = $"{target} STATS";
        RenderStatsPanelContent(target);
        StatsPanel.Visibility = Visibility.Visible;
        UpdateStatsPanelMargin();
        _anim.Animate("sp", _statsPanelW, StatsPanelW, ApplyStatsPanelW);
    }

    private void CloseStatsPanel()
    {
        _statsPanelOpen = false;
        _anim.Animate("sp", _statsPanelW, 0, ApplyStatsPanelW);
        RunAfter(250, () =>
        {
            if (!_statsPanelOpen) StatsPanel.Visibility = Visibility.Collapsed;
        });
    }

    private void ApplyStatsPanelW(double v)
    {
        v = Math.Max(0, v);
        _statsPanelW = v;
        StatsPanel.Width = v;
        if (v > 0.5) StatsPanel.Visibility = Visibility.Visible;
        else if (!_statsPanelOpen) StatsPanel.Visibility = Visibility.Collapsed;
        UpdateStatsPanelMargin();
        Reposition();
        RefreshBlur();
    }

    private void UpdateStatsPanelMargin()
    {
        double zoneLeft = Math.Max(40, (WinW - _currentW) / 2);
        double pillRight = _zoneW + zoneLeft + _currentW;
        StatsPanel.Margin = new Thickness(pillRight, PillTop, 0, 0);
        StatsPanel.Height = _currentH;
    }

    private void RenderStatsPanelContent(string target)
    {
        var snap = _telemetry;
        if (snap is null) return;
        switch (target)
        {
            case "CPU":
                StatsPanelTitle.Text = "CPU STATS";
                StatsPanelPercent.Text = FmtPct(snap.Cpu.Available ? snap.Cpu.Utilization : null);
                StatsPanelInfo.Text = snap.Cpu.Available
                    ? $"{snap.Cpu.Name}\n{TelemetryFormat.Frequency(snap.Cpu.FrequencyMhz)} · {snap.Cpu.PhysicalCores}c/{snap.Cpu.LogicalThreads}t"
                    : "Data unavailable";
                RedrawStatsPanelGraph(App.Telemetry?.CpuHistory);
                break;
            case "GPU":
                StatsPanelTitle.Text = "GPU STATS";
                var gpus = App.Telemetry?.SnapshotGpuTracks();
                if (gpus is { Count: > 0 })
                {
                    var g = gpus[0];
                    StatsPanelPercent.Text = FmtPct(g.Utilization);
                    StatsPanelInfo.Text = g.VramAvailable
                        ? $"{g.Name}\nVRAM: {TelemetryFormat.Bytes(g.VramUsed)} / {TelemetryFormat.Bytes(g.VramTotal)}"
                        : g.Name;
                    RedrawStatsPanelGraph(g.History);
                }
                else
                {
                    StatsPanelPercent.Text = "n/a";
                    StatsPanelInfo.Text = "No GPU data available";
                    RedrawStatsPanelGraph(null);
                }
                break;
            case "RAM":
                StatsPanelTitle.Text = "RAM STATS";
                StatsPanelPercent.Text = FmtPct(snap.Memory.Available ? snap.Memory.UsedPercent : null);
                StatsPanelInfo.Text = snap.Memory.Available
                    ? $"Used: {TelemetryFormat.Bytes(snap.Memory.UsedBytes)} / {TelemetryFormat.Bytes(snap.Memory.TotalBytes)}\nTotal physical memory"
                    : "Data unavailable";
                RedrawStatsPanelGraph(App.Telemetry?.RamHistory);
                break;
        }
    }

    private void RedrawStatsPanelGraph(TelemetryHistory? h)
    {
        double gw = Math.Max(40, StatsPanelW - 28);
        double gh = 60;
        RedrawGraphTo(h, gw, gh, StatsPanelGraphLine, StatsPanelGraphFill);
    }

    private void RedrawGraphTo(TelemetryHistory? h, double w, double hh,
        System.Windows.Shapes.Path linePath, System.Windows.Shapes.Path fillPath)
    {
        if (h is null || h.IsEmpty || w < 8 || hh < 4)
        {
            linePath.Data = null;
            fillPath.Data = null;
            return;
        }
        double[] vals = h.Snapshot();
        int n = vals.Length;
        double xStep = w / Math.Max(1, n - 1);
        double usable = Math.Max(4, hh - 4);
        var points = new Point[n];
        for (int i = 0; i < n; i++)
        {
            double v = Math.Clamp(vals[i], 0, 100);
            points[i] = new Point(i * xStep, hh - 2 - (v / 100.0) * usable);
        }
        var lineFig = new PathFigure { StartPoint = points[0], IsFilled = false };
        for (int i = 1; i < n; i++) lineFig.Segments.Add(new LineSegment(points[i], true));
        var line = new PathGeometry();
        line.Figures.Add(lineFig);
        linePath.Data = line;

        var fillPts = new List<Point>(points);
        fillPts.Add(new Point(points[n - 1].X, hh));
        fillPts.Add(new Point(points[0].X, hh));
        var fillFig = new PathFigure { StartPoint = points[0], IsClosed = true };
        fillFig.Segments.Add(new PolyLineSegment(fillPts, true));
        var fillGeo = new PathGeometry();
        fillGeo.Figures.Add(fillFig);
        fillPath.Data = fillGeo;
    }
}
