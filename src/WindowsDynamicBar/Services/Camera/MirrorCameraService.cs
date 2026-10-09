using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace WindowsDynamicBar.Services.Camera;

/// <summary>Lifecycle state of the mirror camera. Never fabricates frames - "blocked"/"none"/"busy"
/// are surfaced to the UI so it can explain why there is no picture.</summary>
public enum MirrorCameraStatus { Off, Starting, Live, Blocked, NoDevice, Busy, Error }

/// <summary>
/// Owns the webcam capture for the mirror panel using <see cref="MediaCapture"/> +
/// <see cref="MediaFrameReader"/>. Frames are decoded to 8-bit BGRA and kept in a single latest
/// buffer; the UI is notified at most once until it acknowledges, so a slow UI simply drops frames
/// (bounded queue) instead of piling work up. Nothing is ever recorded or persisted here.
/// </summary>
public sealed class MirrorCameraService : IDisposable
{
    // HRESULTs that MediaCapture surfaces for common failure modes.
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int MF_E_DEVICE_IN_USE = unchecked((int)0xC00D3E82);
    private const int MF_E_VIDEO_RECORDING_DEVICE_IN_USE = unchecked((int)0xC00D3704);
    private const int ERROR_NOT_FOUND = unchecked((int)0x80070490);
    private const int ERROR_DEVICE_NOT_CONNECTED = unchecked((int)0x8007048F);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _op = new(1, 1);

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private byte[]? _frame;
    private int _width;
    private int _height;
    private int _notified;
    private bool _disposed;

    public MirrorCameraStatus Status { get; private set; } = MirrorCameraStatus.Off;

    /// <summary>Raised (possibly off the UI thread) whenever <see cref="Status"/> changes.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised at most once per un-acknowledged frame, off the UI thread.</summary>
    public event Action? FrameAvailable;

    public bool IsLive => Status == MirrorCameraStatus.Live;

    public int Width { get { lock (_gate) return _width; } }
    public int Height { get { lock (_gate) return _height; } }

    // ------------------------------------------------------------------ start / stop

    public async Task<bool> StartAsync()
    {
        if (_disposed) return false;
        if (IsLive) return true;

        await _op.WaitAsync();
        try
        {
            if (_disposed) return false;
            if (IsLive) return true;

            SetStatus(MirrorCameraStatus.Starting);

            var device = await FindPreferredCameraAsync();
            if (device is null)
            {
                SetStatus(MirrorCameraStatus.NoDevice);
                return false;
            }

            var capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                VideoDeviceId = device.Id,
            };

            try
            {
                await capture.InitializeAsync(settings);
            }
            catch (Exception ex)
            {
                capture.Dispose();
                SetStatus(MapFailure(ex));
                return false;
            }

            var source = capture.FrameSources.Values
                .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (source is null)
            {
                capture.Dispose();
                SetStatus(MirrorCameraStatus.Error);
                return false;
            }

            // Prefer a small format (<= 640x480): plenty for the panel and keeps per-frame copies tiny.
            var preferred = source.SupportedFormats
                .Where(f => f.VideoFormat.Width <= 640 && f.VideoFormat.Height <= 480)
                .OrderByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
                .FirstOrDefault();
            if (preferred is not null)
            {
                try { await source.SetFormatAsync(preferred); }
                catch { /* keep the current format if the driver rejects the change */ }
            }

            MediaFrameReader reader;
            try
            {
                reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            }
            catch (Exception ex)
            {
                capture.Dispose();
                SetStatus(MapFailure(ex));
                return false;
            }

            reader.FrameArrived += OnFrameArrived;

            var startStatus = await reader.StartAsync();
            if (startStatus != MediaFrameReaderStartStatus.Success)
            {
                reader.FrameArrived -= OnFrameArrived;
                reader.Dispose();
                capture.Dispose();
                SetStatus(MapFailure(null));
                return false;
            }

            _capture = capture;
            _reader = reader;
            SetStatus(MirrorCameraStatus.Live);
            return true;
        }
        finally
        {
            _op.Release();
        }
    }

    public async Task StopAsync()
    {
        await _op.WaitAsync();
        try
        {
            var reader = _reader;
            _reader = null;
            if (reader is not null)
            {
                reader.FrameArrived -= OnFrameArrived;
                try { await reader.StopAsync(); } catch { /* stopping is best-effort */ }
                reader.Dispose();
            }

            var capture = _capture;
            _capture = null;
            capture?.Dispose();

            lock (_gate)
            {
                _frame = null;
                _width = 0;
                _height = 0;
            }
            Interlocked.Exchange(ref _notified, 0);

            if (Status != MirrorCameraStatus.Off)
                SetStatus(MirrorCameraStatus.Off);
        }
        finally
        {
            _op.Release();
        }
    }

    // ------------------------------------------------------------------ frame hand-off

    /// <summary>Copies the latest decoded BGRA frame into <paramref name="destination"/> (which must
    /// be at least <see cref="Width"/> * <see cref="Height"/> * 4 bytes). Returns false when no frame
    /// is available yet. Call <see cref="AcknowledgeFrame"/> once the frame has been consumed.</summary>
    public bool TryCopyLatest(byte[] destination, out int width, out int height)
    {
        lock (_gate)
        {
            width = _width;
            height = _height;
            if (_frame is null || width <= 0 || height <= 0) return false;
            int len = Math.Min(_frame.Length, destination.Length);
            if (len <= 0) return false;
            Buffer.BlockCopy(_frame, 0, destination, 0, len);
            return true;
        }
    }

    public void AcknowledgeFrame() => Interlocked.Exchange(ref _notified, 0);

    private unsafe void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        if (frame is null) return;

        var bitmap = frame.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null) return;

        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;
        if (w <= 0 || h <= 0) return;

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        // The CsWinRT projection cannot be cast straight to a raw COM interface, so unwrap it to its
        // ABI pointer, QueryInterface for IMemoryBufferByteAccess, then call GetBuffer through the
        // vtable (slot 3) without materialising a runtime-callable wrapper per frame.
        IntPtr inspectable = MarshalInspectable<object>.FromManaged(reference);
        Guid iid = typeof(IMemoryBufferByteAccess).GUID;
        int hr = Marshal.QueryInterface(inspectable, ref iid, out IntPtr access);
        Marshal.Release(inspectable);
        if (hr != 0) return;

        IntPtr vtable = Marshal.ReadIntPtr(access);
        IntPtr method = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        var getBuffer = (GetBufferDelegate)Marshal.GetDelegateForFunctionPointer(method, typeof(GetBufferDelegate));
        hr = getBuffer(access, out byte* data, out uint capacity);
        Marshal.Release(access);
        if (hr != 0 || data == null) return;

        var plane = buffer.GetPlaneDescription(0);
        int rowBytes = w * 4;

        lock (_gate)
        {
            int len = w * h * 4;
            if ((uint)len > capacity) return;
            if (_frame is null || _frame.Length != len) _frame = new byte[len];
            _width = w;
            _height = h;

            byte* src = data + plane.StartIndex;
            for (int y = 0; y < h; y++)
                Marshal.Copy((IntPtr)(src + y * plane.Stride), _frame, y * rowBytes, rowBytes);
        }

        if (Interlocked.CompareExchange(ref _notified, 1, 0) == 0)
            FrameAvailable?.Invoke();
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<DeviceInformation?> FindPreferredCameraAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        if (devices.Count == 0) return null;

        var front = devices.FirstOrDefault(d => d.EnclosureLocation?.Panel == Panel.Front);
        return front ?? devices[0];
    }

    private static MirrorCameraStatus MapFailure(Exception? ex)
    {
        if (ex is UnauthorizedAccessException) return MirrorCameraStatus.Blocked;
        int hr = ex?.HResult ?? 0;
        return hr switch
        {
            E_ACCESSDENIED => MirrorCameraStatus.Blocked,
            MF_E_DEVICE_IN_USE => MirrorCameraStatus.Busy,
            MF_E_VIDEO_RECORDING_DEVICE_IN_USE => MirrorCameraStatus.Busy,
            ERROR_NOT_FOUND => MirrorCameraStatus.NoDevice,
            ERROR_DEVICE_NOT_CONNECTED => MirrorCameraStatus.NoDevice,
            _ => MirrorCameraStatus.Error,
        };
    }

    private void SetStatus(MirrorCameraStatus status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Non-blocking teardown: Dispose() is called from the UI thread, so we must not block on an
        // async StopAsync (which would deadlock resuming on the captured UI context). Stopping the
        // reader and disposing the capture is enough to release the device.
        var reader = _reader;
        _reader = null;
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            try { reader.Dispose(); } catch { /* teardown is best-effort */ }
        }

        var capture = _capture;
        _capture = null;
        try { capture?.Dispose(); } catch { /* teardown is best-effort */ }

        lock (_gate)
        {
            _frame = null;
            _width = 0;
            _height = 0;
        }
        Interlocked.Exchange(ref _notified, 0);
        Status = MirrorCameraStatus.Off;
        _op.Dispose();
    }
}

[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal unsafe delegate int GetBufferDelegate(IntPtr pThis, out byte* buffer, out uint capacity);
