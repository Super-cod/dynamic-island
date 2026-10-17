using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Animation;
using WindowsDynamicBar.Core.Media;

namespace WindowsDynamicBar.UI.Media;

public partial class MediaDisc : UserControl
{
    private double _angle;
    private double _omega;
    private BitmapSource? _currentArt;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastTickMs;
    private readonly DispatcherTimer _spin = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(33)
    };
    private readonly System.Collections.Generic.List<TweenAnimation> _tweens = new();
    private bool _isControlLoaded;

    internal bool IsSpinTimerRunningForTesting => _spin.IsEnabled;
    internal int ActiveTweenCountForTesting => _tweens.Count;
    internal void RaiseLoadedForTesting() => OnLoaded(this, new RoutedEventArgs(LoadedEvent));
    internal void RaiseUnloadedForTesting() => OnUnloaded(this, new RoutedEventArgs(UnloadedEvent));

    public static readonly DependencyProperty DiameterProperty =
        DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(MediaDisc),
            new PropertyMetadata(36.0, OnDiameterChanged));

    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    public static readonly DependencyProperty ReduceMotionProperty =
        DependencyProperty.Register(nameof(ReduceMotion), typeof(bool), typeof(MediaDisc),
            new PropertyMetadata(false, OnReduceMotionChanged));

    public bool ReduceMotion
    {
        get => (bool)GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    public MediaDisc()
    {
        InitializeComponent();
        _spin.Tick += OnSpinTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => SyncSpin();
        OnDiameterChanged(this, new DependencyPropertyChangedEventArgs(DiameterProperty, 36.0, Diameter));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isControlLoaded = true;
        SyncSpin();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isControlLoaded = false;
        _spin.Stop();
        foreach (var t in _tweens) t.Dispose();
        _tweens.Clear();
        ArtFront.Opacity = 1;
    }

    public void SetArtwork(BitmapSource? art, bool playing)
    {
        _playing = playing;
        if (ReferenceEquals(_currentArt, art))
        {
            SyncSpin();
            return;
        }
        _currentArt = art;
        if (art is null)
        {
            foreach (var t in _tweens) t.Dispose();
            _tweens.Clear();
            ArtFront.Source = null;
            ArtFront.Visibility = Visibility.Collapsed;
            ArtBack.Source = null;
            ArtBack.Visibility = Visibility.Collapsed;
            ArtMask.Visibility = Visibility.Collapsed;
            Fallback.Visibility = Visibility.Visible;
        }
        else
        {
            ArtMask.Visibility = Visibility.Visible;
            Fallback.Visibility = Visibility.Collapsed;
            Crossfade(art);
        }
        SyncSpin();
    }

    public void SetPlaying(bool playing)
    {
        if (_playing == playing) return;
        _playing = playing;
        SyncSpin();
    }

    private bool _playing;

    private void Crossfade(BitmapSource art)
    {
        foreach (var t in _tweens) t.Dispose();
        _tweens.Clear();

        // If no artwork was showing previously, or motion is reduced, display immediately on ArtFront
        if (ArtFront.Source is null || ReduceMotion)
        {
            ArtBack.Source = null;
            ArtBack.Visibility = Visibility.Collapsed;
            ArtFront.Source = art;
            ArtFront.Opacity = 1;
            ArtFront.Visibility = Visibility.Visible;
            return;
        }

        // Put the previous artwork on ArtBack underneath so it remains visible during transition
        ArtBack.Source = ArtFront.Source;
        ArtBack.Opacity = 1;
        ArtBack.Visibility = Visibility.Visible;

        // Place incoming artwork on ArtFront (on top) and fade it in
        ArtFront.Source = art;
        ArtFront.Opacity = 0;
        ArtFront.Visibility = Visibility.Visible;

        var tIn = new TweenAnimation();
        _tweens.Add(tIn);
        tIn.Run(0, 1, TimeSpan.FromMilliseconds(240), v => ArtFront.Opacity = v, Easing.CubicOut);
        tIn.Completed += () =>
        {
            ArtBack.Source = null;
            ArtBack.Visibility = Visibility.Collapsed;
            ArtFront.Opacity = 1;
            _tweens.Remove(tIn);
        };
    }

    private void SyncSpin()
    {
        if (!_isControlLoaded)
        {
            _spin.Stop();
            return;
        }
        if (ReduceMotion || !IsVisible)
        {
            _spin.Stop();
            _angle = 0;
            _omega = 0;
            Spin.Angle = 0;
            return;
        }
        // Always keep timer running if omega > 0.5, regardless of playing state
        // This allows the disc to decelerate smoothly when paused
        if (_omega >= 0.5)
        {
            if (!_spin.IsEnabled)
            {
                _lastTickMs = _clock.ElapsedMilliseconds;
                _spin.Start();
            }
        }
        else if (_playing)
        {
            // Start spinning when playing and omega is below threshold
            if (!_spin.IsEnabled)
            {
                _lastTickMs = _clock.ElapsedMilliseconds;
                _spin.Start();
            }
        }
        else
        {
            // Stopped and omega is low enough
            _spin.Stop();
            _omega = 0;
        }
    }

    private void OnSpinTick(object? sender, EventArgs e)
    {
        long now = _clock.ElapsedMilliseconds;
        double dt = Math.Min(0.1, (now - _lastTickMs) / 1000.0);
        _lastTickMs = now;
        (_angle, _omega) = DiscRotation.Step(_angle, _omega, _playing, dt);
        Spin.Angle = _angle;
        if (DiscRotation.ShouldTrim(_playing, _omega))
        {
            _omega = 0;
            _spin.Stop();
        }
    }

    private static void OnDiameterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (MediaDisc)d;
        double dia = Math.Max(20, (double)e.NewValue);
        c.Width = dia;
        c.Height = dia;
        double s = dia / 36.0;
        c.DiscScale.ScaleX = s;
        c.DiscScale.ScaleY = s;
    }

    private static void OnReduceMotionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MediaDisc)d).SyncSpin();
    }
}