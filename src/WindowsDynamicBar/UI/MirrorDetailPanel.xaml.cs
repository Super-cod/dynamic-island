using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsDynamicBar.Services.Camera;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Privacy-first mirror panel: just the camera frame. Opening the panel never starts the camera;
/// clicking the Mirror icon toggles it on and off. Nothing is recorded.
/// </summary>
public partial class MirrorDetailPanel : UserControl
{
    private static readonly Brush NameBrush = Frozen(Color.FromRgb(0xE6, 0xE0, 0xA8));
    private static readonly Brush MetricBrush = Frozen(Color.FromRgb(0x8A, 0xB4, 0x78));

    private readonly MirrorCameraService _cam = new();

    private WriteableBitmap? _bitmap;
    private byte[]? _pixels;
    private bool _starting;

    public MirrorDetailPanel()
    {
        InitializeComponent();
        TitleText.Foreground = NameBrush;

        _cam.StatusChanged += OnCameraStatusChanged;
        _cam.FrameAvailable += OnFrameAvailable;

        UpdateVisuals();
    }

    /// <summary>Honours the global Reduce Motion preference (frames land without animation).</summary>
    public bool ReduceMotion { get; set; }

    public bool IsCameraLive => _cam.IsLive;

    /// <summary>Raised on the UI thread when the camera goes live/off, so the host can update the
    /// Mirror icon ring + "LIVE"/"off" label.</summary>
    public event Action<bool>? CameraStateChanged;

    // ------------------------------------------------------------------ public control

    public void StartCamera() => _ = StartCameraAsync();

    public void StopCamera() => _ = StopCameraAsync();

    /// <summary>Stops the camera and releases the device. Called when the host window closes.</summary>
    public void Shutdown()
    {
        _cam.FrameAvailable -= OnFrameAvailable;
        _cam.StatusChanged -= OnCameraStatusChanged;
        _cam.Dispose();
    }

    private async Task StartCameraAsync()
    {
        if (_starting || _cam.IsLive) return;
        _starting = true;
        try
        {
            await _cam.StartAsync();
        }
        finally
        {
            _starting = false;
            UpdateVisuals();
        }
    }

    private async Task StopCameraAsync()
    {
        if (_cam.Status == MirrorCameraStatus.Off) return;
        await _cam.StopAsync();
        Preview.Source = null;
        _bitmap = null;
        _pixels = null;
        UpdateVisuals();
    }

    // ------------------------------------------------------------------ camera callbacks

    private void OnCameraStatusChanged()
    {
        // Fires off the UI thread while stopping/starting; marshal for safety.
        if (Dispatcher.CheckAccess()) UpdateVisuals();
        else Dispatcher.BeginInvoke(new Action(UpdateVisuals));
    }

    private void OnFrameAvailable()
    {
        // Background thread: at most one pending update (bounded queue in the service).
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderFrame));
    }

    private void RenderFrame()
    {
        int w = _cam.Width, h = _cam.Height;
        if (w <= 0 || h <= 0)
        {
            _cam.AcknowledgeFrame();
            return;
        }

        int len = w * h * 4;
        if (_pixels is null || _pixels.Length != len) _pixels = new byte[len];

        if (!_cam.TryCopyLatest(_pixels, out int fw, out int fh) || fw <= 0 || fh <= 0)
        {
            _cam.AcknowledgeFrame();
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != fw || _bitmap.PixelHeight != fh)
        {
            _bitmap = new WriteableBitmap(fw, fh, 96, 96, PixelFormats.Bgr32, null);
            Preview.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, fw, fh), _pixels, fw * 4, 0);
        _cam.AcknowledgeFrame();
    }

    // ------------------------------------------------------------------ visuals

    private void UpdateVisuals()
    {
        bool live = _cam.IsLive;
        string status = StatusText();

        string tag = live ? "REC off" : "private";
        TitleText.Inlines.Clear();
        TitleText.Inlines.Add(Run("\u2524", NameBrush));
        TitleText.Inlines.Add(Run("mirror", MetricBrush));
        TitleText.Inlines.Add(Run($"\u251C\u2524{status}\u251C\u2524{tag}\u251C", NameBrush));

        Placeholder.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        Placeholder.Text = _cam.Status switch
        {
            MirrorCameraStatus.Blocked => "Camera access is blocked.",
            MirrorCameraStatus.NoDevice => "No camera found.",
            MirrorCameraStatus.Busy => "Camera is in use by another app.",
            _ => "Click the Mirror icon",
        };

        CameraStateChanged?.Invoke(live);
    }

    private string StatusText() => _cam.Status switch
    {
        MirrorCameraStatus.Live => "live",
        MirrorCameraStatus.Blocked => "blocked",
        MirrorCameraStatus.NoDevice => "none",
        MirrorCameraStatus.Busy => "busy",
        _ => "off",
    };

    // ------------------------------------------------------------------ helpers

    private static Run Run(string text, Brush brush) => new(text) { Foreground = brush };

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}