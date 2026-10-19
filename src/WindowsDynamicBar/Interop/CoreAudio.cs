using System.Runtime.InteropServices;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Interop;

/// <summary>
/// Core Audio (WASAPI) COM interop: event-driven master volume, mute, and default-device
/// change notifications. Interfaces below match the documented IMMDeviceEnumerator /
/// IMMDevice / IAudioEndpointVolume vtable order exactly.
/// </summary>
internal static class CoreAudio
{
    private const int DEVICE_RENDER = 0;   // eRender
    private const int ROLE_CONSOLE = 0;    // eConsole (system volume, hardware keys)
    private const int ROLE_MULTIMEDIA = 1; // eMultimedia
    private const int STGM_READ = 0;

    // MMDeviceEnumerator hands every caller the same COM object. A normal (cached) runtime wrapper
    // for it - from a [ComImport] coclass or Activator - would be reused when NAudio creates its
    // own typed wrapper, and NAudio's cast then fails (WasapiLoopbackCapture, mic toggle). So this
    // class takes a private, uncached wrapper.
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private const int CLSCTX_ALL = 0x17;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr ppv);

    internal static object CreateEnumerator()
    {
        Guid clsid = CLSID_MMDeviceEnumerator, iid = IID_IUnknown;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out IntPtr unk));
        try { return Marshal.GetUniqueObjectForIUnknown(unk); }
        finally { Marshal.Release(unk); }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        int RegisterEndpointNotificationCallback(IntPtr pClient);
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr actParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        int GetState(out int pdwState);
    }

    // Exact vtable order (IAudioEndpointVolume):
    //  1 RegisterControlChangeNotify      10 GetChannelVolumeLevel
    //  2 UnregisterControlChangeNotify    11 GetChannelVolumeLevelScalar
    //  3 GetChannelCount                  12 SetMute
    //  4 SetMasterVolumeLevel             13 GetMute
    //  5 SetMasterVolumeLevelScalar       14 GetVolumeStepInfo
    //  6 GetMasterVolumeLevel             15 VolumeStepUp
    //  7 GetMasterVolumeLevelScalar       16 VolumeStepDown
    //  8 SetChannelVolumeLevel            17 QueryHardwareSupport
    //  9 SetChannelVolumeLevelScalar      18 GetVolumeRange
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, IntPtr pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, IntPtr pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        int GetMasterVolumeLevelScalar(out float pfLevel);
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, IntPtr pguidEventContext);
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, IntPtr pguidEventContext);
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, IntPtr pguidEventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
        int VolumeStepUp(IntPtr pguidEventContext);
        int VolumeStepDown(IntPtr pguidEventContext);
        int QueryHardwareSupport(out uint pdwHardwareSupportMask);
        int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }

    // Exact vtable order (IMMNotificationClient):
    //  OnDeviceStateChanged, OnDeviceAdded, OnDeviceRemoved,
    //  OnDefaultDeviceChanged, OnPropertyValueChanged
    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key);
    }

    // IAudioEndpointVolumeCallback — single method OnNotify(PAUDIO_VOLUME_NOTIFICATION_DATA)
    [ComImport, Guid("657804FA-D6AD-4496-8A70-06914B95241A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr pNotify);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIO_VOLUME_NOTIFICATION_DATA
    {
        public Guid guidEventContext;
        public int bMuted;
        public float fMasterVolume;
        public int nChannels;
        // float afChannelVolumes[nChannels] follows
    }

    /// <summary>CCW-implementable callback: volume change events arrive on a COM thread.</summary>
    private sealed class VolumeChangeSink : IAudioEndpointVolumeCallback, IMMNotificationClient
    {
        private readonly Action<float, bool> _onVolume;
        private readonly Action? _onDefaultDevice;

        public VolumeChangeSink(Action<float, bool> onVolume, Action? onDefaultDevice)
        {
            _onVolume = onVolume;
            _onDefaultDevice = onDefaultDevice;
        }

        public int OnNotify(IntPtr pNotify)
        {
            try
            {
                if (pNotify == IntPtr.Zero) return 0;
                var d = Marshal.PtrToStructure<AUDIO_VOLUME_NOTIFICATION_DATA>(pNotify);
                _onVolume(Math.Clamp(d.fMasterVolume, 0f, 1f), d.bMuted != 0);
            }
            catch { /* never throw across COM */ }
            return 0;
        }

        public void OnDeviceStateChanged(string deviceId, int newState) { }
        public void OnDeviceAdded(string deviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDefaultDeviceChanged(int flow, int role, string defaultDeviceId)
        {
            if (flow == DEVICE_RENDER) _onDefaultDevice?.Invoke();
        }
        public void OnPropertyValueChanged(string deviceId, IntPtr key) { }
    }

    // ---- state ----
    private static readonly object _sync = new();
    private static IMMDeviceEnumerator? _enumerator;
    private static IAudioEndpointVolume? _endpoint;
    private static VolumeChangeSink? _sink;
    private static IntPtr _volumeCallbackPtr = IntPtr.Zero;
    private static IntPtr _notificationClientPtr = IntPtr.Zero;
    private static bool _isShutdown;

    public static bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                return !_isShutdown && _endpoint is not null;
            }
        }
    }

    public static bool TryInitialize(Action<float, bool> onVolumeChanged, Action? onDefaultDeviceChanged, out string? error)
    {
        error = null;
        lock (_sync)
        {
            _isShutdown = false;
            CleanupCoreAudioUnsafe();

            try
            {
                _enumerator = (IMMDeviceEnumerator)CreateEnumerator();
                // Never call back into the enumerator inside its own notification callback: re-attach
                // on the thread pool (same rule as the mic toggle's DefaultDeviceSink).
                _sink = new VolumeChangeSink(onVolumeChanged, () => Task.Run(() =>
                {
                    bool reattached = AttachDefaultEndpoint();
                    if (reattached)
                    {
                        onDefaultDeviceChanged?.Invoke();
                    }
                }));

                _notificationClientPtr = Marshal.GetComInterfaceForObject(_sink, typeof(IMMNotificationClient));
                int regHr = _enumerator.RegisterEndpointNotificationCallback(_notificationClientPtr);
                if (regHr != 0)
                {
                    error = $"RegisterEndpointNotificationCallback failed 0x{regHr:X8}";
                    CleanupCoreAudioUnsafe();
                    return false;
                }

                _volumeCallbackPtr = Marshal.GetComInterfaceForObject(_sink, typeof(IAudioEndpointVolumeCallback));

                return AttachDefaultEndpointUnsafe(out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                CleanupCoreAudioUnsafe();
                Log.Warning("Core Audio init failed", ("error", ex.Message));
                return false;
            }
        }
    }

    public static bool AttachDefaultEndpoint() => AttachDefaultEndpoint(out _);

    public static bool AttachDefaultEndpoint(out string? error)
    {
        lock (_sync)
        {
            return AttachDefaultEndpointUnsafe(out error);
        }
    }

    private static bool AttachDefaultEndpointUnsafe(out string? error)
    {
        error = null;
        if (_isShutdown || _enumerator is null) return false;

        if (_endpoint is not null)
        {
            if (_volumeCallbackPtr != IntPtr.Zero)
            {
                try { _endpoint.UnregisterControlChangeNotify(_volumeCallbackPtr); } catch { }
            }
            try { Marshal.ReleaseComObject(_endpoint); } catch { }
            _endpoint = null;
        }

        IMMDevice? device = null;
        try
        {
            int hr = _enumerator.GetDefaultAudioEndpoint(DEVICE_RENDER, ROLE_CONSOLE, out device);
            if (hr != 0 || device is null)
                hr = _enumerator.GetDefaultAudioEndpoint(DEVICE_RENDER, ROLE_MULTIMEDIA, out device);

            if (hr != 0 || device is null)
            {
                error = $"No default render device (0x{hr:X8})";
                return false;
            }

            Guid iid = typeof(IAudioEndpointVolume).GUID;
            hr = device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out object obj);
            if (hr != 0 || obj is null)
            {
                error = $"Activate failed 0x{hr:X8}";
                return false;
            }

            var endpoint = (IAudioEndpointVolume)obj;

            if (_volumeCallbackPtr != IntPtr.Zero)
            {
                hr = endpoint.RegisterControlChangeNotify(_volumeCallbackPtr);
                if (hr != 0)
                {
                    error = $"RegisterControlChangeNotify 0x{hr:X8}";
                    Log.Warning("Volume callback registration failed", ("hr", hr));
                    try { Marshal.ReleaseComObject(endpoint); } catch { }
                    return false;
                }
            }

            _endpoint = endpoint;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (device is not null)
            {
                try { Marshal.ReleaseComObject(device); } catch { }
            }
        }
    }

    public static (float Level, bool Muted)? GetCurrent()
    {
        lock (_sync)
        {
            if (_isShutdown || _endpoint is null) return null;
            try
            {
                _endpoint.GetMasterVolumeLevelScalar(out float level);
                _endpoint.GetMute(out bool muted);
                return (Math.Clamp(level, 0f, 1f), muted);
            }
            catch { return null; }
        }
    }

    // Proper IPropertyStore declaration (GetValue is slot 6 counting IUnknown's 3).
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1, wReserved2, wReserved3;
        public IntPtr p;
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pvar);

    private const ushort VT_LPWSTR = 31;

    /// <summary>Friendly name of the current default render device (e.g. "Speakers (Realtek)").</summary>
    public static string? GetDefaultDeviceName()
    {
        lock (_sync)
        {
            if (_isShutdown || _enumerator is null) return null;
            try
            {
                int hr = _enumerator.GetDefaultAudioEndpoint(DEVICE_RENDER, ROLE_CONSOLE, out var dev);
                if (hr != 0 || dev is null)
                    hr = _enumerator.GetDefaultAudioEndpoint(DEVICE_RENDER, ROLE_MULTIMEDIA, out dev);
                if (hr != 0 || dev is null) return null;
                try
                {
                    if (dev.OpenPropertyStore(STGM_READ, out IntPtr propsPtr) != 0 || propsPtr == IntPtr.Zero) return null;
                    var store = (IPropertyStore)Marshal.GetObjectForIUnknown(propsPtr);
                    Marshal.Release(propsPtr);
                    try
                    {
                        var key = new PROPERTYKEY
                        {
                            // PKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, pid 14
                            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                            pid = 14
                        };
                        if (store.GetValue(ref key, out var pv) != 0) return null;
                        try
                        {
                            if (pv.vt != VT_LPWSTR || pv.p == IntPtr.Zero) return null;
                            return Marshal.PtrToStringUni(pv.p);
                        }
                        finally { PropVariantClear(ref pv); }
                    }
                    finally { Marshal.ReleaseComObject(store); }
                }
                finally { Marshal.ReleaseComObject(dev); }
            }
            catch { return null; }
        }
    }

    // ---- controls ----

    public static void SetLevel(float level01)
    {
        lock (_sync)
        {
            if (_isShutdown || _endpoint is null) return;
            try { _endpoint.SetMasterVolumeLevelScalar(Math.Clamp(level01, 0f, 1f), IntPtr.Zero); }
            catch (Exception ex) { Log.Debug("SetLevel failed", ("error", ex.Message)); }
        }
    }

    public static void SetMute(bool mute)
    {
        lock (_sync)
        {
            if (_isShutdown || _endpoint is null) return;
            try { _endpoint.SetMute(mute, IntPtr.Zero); }
            catch (Exception ex) { Log.Debug("SetMute failed", ("error", ex.Message)); }
        }
    }

    public static void StepUp()
    {
        lock (_sync)
        {
            if (_isShutdown || _endpoint is null) return;
            try { _endpoint.VolumeStepUp(IntPtr.Zero); } catch { }
        }
    }

    public static void StepDown()
    {
        lock (_sync)
        {
            if (_isShutdown || _endpoint is null) return;
            try { _endpoint.VolumeStepDown(IntPtr.Zero); } catch { }
        }
    }

    public static void Shutdown()
    {
        lock (_sync)
        {
            _isShutdown = true;
            CleanupCoreAudioUnsafe();
        }
    }

    private static void CleanupCoreAudioUnsafe()
    {
        if (_endpoint is not null)
        {
            if (_volumeCallbackPtr != IntPtr.Zero)
            {
                try { _endpoint.UnregisterControlChangeNotify(_volumeCallbackPtr); } catch { }
            }
            try { Marshal.ReleaseComObject(_endpoint); } catch { }
            _endpoint = null;
        }

        if (_enumerator is not null)
        {
            if (_notificationClientPtr != IntPtr.Zero)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_notificationClientPtr); } catch { }
            }
            try { Marshal.ReleaseComObject(_enumerator); } catch { }
            _enumerator = null;
        }

        if (_volumeCallbackPtr != IntPtr.Zero)
        {
            try { Marshal.Release(_volumeCallbackPtr); } catch { }
            _volumeCallbackPtr = IntPtr.Zero;
        }

        if (_notificationClientPtr != IntPtr.Zero)
        {
            try { Marshal.Release(_notificationClientPtr); } catch { }
            _notificationClientPtr = IntPtr.Zero;
        }

        _sink = null;
    }
}

internal interface ICoreAudioController
{
    bool IsAvailable { get; }
    bool TryInitialize(Action<float, bool> onVolumeChanged, Action onDefaultDeviceChanged, out string? error);
    (float Level, bool Muted)? GetCurrent();
    string? GetDefaultDeviceName();
    void SetLevel(float level01);
    void SetMute(bool mute);
    void StepUp();
    void StepDown();
    void Shutdown();
}

internal sealed class CoreAudioController : ICoreAudioController
{
    public static readonly CoreAudioController Instance = new();

    public bool IsAvailable => CoreAudio.IsAvailable;
    public bool TryInitialize(Action<float, bool> onVolumeChanged, Action onDefaultDeviceChanged, out string? error) =>
        CoreAudio.TryInitialize(onVolumeChanged, onDefaultDeviceChanged, out error);
    public (float Level, bool Muted)? GetCurrent() => CoreAudio.GetCurrent();
    public string? GetDefaultDeviceName() => CoreAudio.GetDefaultDeviceName();
    public void SetLevel(float level01) => CoreAudio.SetLevel(level01);
    public void SetMute(bool mute) => CoreAudio.SetMute(mute);
    public void StepUp() => CoreAudio.StepUp();
    public void StepDown() => CoreAudio.StepDown();
    public void Shutdown() => CoreAudio.Shutdown();
}
