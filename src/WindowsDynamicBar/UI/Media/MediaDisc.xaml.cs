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
    private bool _frontShown;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastTickMs;
    private readonly DispatcherTimer _spin = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(33)
    };
    private readonly System.Collections.Generic.List<TweenAnimation> _tweens = new();

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
        IsVisibleChanged += (_, _) => SyncSpin();
        OnDiameterChanged(this, new DependencyPropertyChangedEventArgs(DiameterProperty, 36.0, Diameter));
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

        Image fadeIn = _frontShown ? ArtBack : ArtFront;
        Image fadeOut = _frontShown ? ArtFront : ArtBack;
        fadeIn.Source = art;
        fadeIn.Opacity = 0;
        fadeIn.Visibility = Visibility.Visible;
        fadeOut.Visibility = Visibility.Visible;

        if (ReduceMotion)
        {
            fadeIn.Opacity = 1;
            fadeOut.Opacity = 0;
            fadeOut.Source = null;
            fadeOut.Visibility = Visibility.Collapsed;
            _frontShown = !_frontShown;
            return;
        }

        var tIn = new TweenAnimation();
        var tOut = new TweenAnimation();
        _tweens.Add(tIn);
        _tweens.Add(tOut);
        tIn.Run(0, 1, TimeSpan.FromMilliseconds(240), v => fadeIn.Opacity = v, Easing.CubicOut);
        tOut.Run(1, 0, TimeSpan.FromMilliseconds(240), v => fadeOut.Opacity = v, Easing.CubicOut);
        tOut.Completed += () =>
        {
            fadeOut.Source = null;
            fadeOut.Visibility = Visibility.Collapsed;
            _frontShown = !_frontShown;
            _tweens.Remove(tIn);
            _tweens.Remove(tOut);
        };
    }

    private void SyncSpin()
    {
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