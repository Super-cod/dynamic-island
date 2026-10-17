using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Windows.Devices.Radios;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Interop;

namespace WindowsDynamicBar.Services.Toggles;

public enum ToggleId { WiFi, Bluetooth, MicMute, DarkMode, DoNotDisturb }

/// <summary>Snapshot of one toggle for the UI. <see cref="On"/> is the real, last-read state.</summary>
public sealed record ToggleState(ToggleId Id, string Label, string GlyphOn, string GlyphOff, bool On);

/// <summary>
/// Quick toggles that can really be read AND changed without admin rights or undocumented hacks:
///  - Wi-Fi / Bluetooth: Windows.Devices.Radios (only when RadioAccessStatus is Allowed),
///  - microphone mute: Core Audio default capture endpoint,
///  - dark mode: HKCU Personalize AppsUseLightTheme + SystemUsesLightTheme, then WM_SETTINGCHANGE,
///  - Do Not Disturb: the island's own notification DND (setting).
/// Not offered (no reliable public API): airplane mode (needs admin / RadioManagement internals),
/// night light (undocumented CloudStore blob), Windows Focus Assist state (undocumented WNF).
/// All OS calls run off the UI thread; <see cref="Changed"/> is raised from worker threads.
/// </summary>
public sealed class QuickToggleService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly SettingsService _settings;
    private readonly object _sync = new();
    private readonly Dictionary<ToggleId, ToggleState> _states = new();
    private readonly List<Radio> _radios = new();
    private MMDeviceEnumerator? _audio;
    private MMDevice? _mic;
    private AudioEndpointVolume? _micVolume;
    private DefaultDeviceSink? _deviceSink;
    private CancellationTokenSource? _cts;
    private bool _started;

    public event Action? Changed;

    public QuickToggleService(SettingsService settings) => _settings = settings;

    /// <summary>Toggles that are currently supported, in display order.</summary>
    public IReadOnlyList<ToggleState> States
    {
        get
        {
            lock (_sync)
                return Enum.GetValues<ToggleId>().Where(_states.ContainsKey).Select(id => _states[id]).ToList();
        }
    }

    public int OnCount
    {
        get { lock (_sync) return _states.Values.Count(s => s.On); }
    }

    /// <summary>Discover what is supported and hook change events (idempotent, background).</summary>
    public Task StartAsync()
    {
        if (_started) return Task.CompletedTask;
        _started = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        return Task.Run(async () =>
        {
            await InitRadiosAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            InitMic();
            ReadDarkMode();
            ReadDnd();
            Changed?.Invoke();
        }, token);
    }

    // ------------------------------------------------------------------ radios (Wi-Fi, Bluetooth)

    private async Task InitRadiosAsync(CancellationToken token)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                Log.Info("Radio control not allowed; Wi-Fi/Bluetooth toggles hidden", ("status", access));
                return;
            }
            var radios = await Radio.GetRadiosAsync();
            if (token.IsCancellationRequested) return;
            foreach (var r in radios)
            {
                ToggleId? id = r.Kind switch
                {
                    RadioKind.WiFi => ToggleId.WiFi,
                    RadioKind.Bluetooth => ToggleId.Bluetooth,
                    _ => null,
                };
                if (id is null) continue;
                lock (_sync)
                {
                    if (_states.ContainsKey(id.Value)) continue;   // first radio of a kind wins
                    _radios.Add(r);
                    _states[id.Value] = MakeRadioState(id.Value, r.State == RadioState.On);
                }
                r.StateChanged += OnRadioStateChanged;
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Info("Radios unavailable; Wi-Fi/Bluetooth toggles hidden", ("error", ex.Message));
        }
    }

    private static ToggleState MakeRadioState(ToggleId id, bool on) => id == ToggleId.WiFi
        ? new ToggleState(id, "Wi-Fi", "\uE701", "\uEB5E", on)
        : new ToggleState(id, "Bluetooth", "\uE702", "\uE702", on);

    private void OnRadioStateChanged(Radio sender, object args)
    {
        ToggleId id = sender.Kind == RadioKind.WiFi ? ToggleId.WiFi : ToggleId.Bluetooth;
        lock (_sync)
        {
            if (!_radios.Contains(sender)) return;
            _states[id] = MakeRadioState(id, sender.State == RadioState.On);
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ microphone mute

    private void InitMic()
    {
        try
        {
            _audio ??= new MMDeviceEnumerator();
            if (_deviceSink is null)
            {
                _deviceSink = new DefaultDeviceSink(this);
                _audio.RegisterEndpointNotificationCallback(_deviceSink);
            }
            AttachMic();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            // Each source is independent: a broken audio stack hides only the mic toggle.
            Log.Info("Capture endpoint unavailable; mic toggle hidden", ("error", ex.Message));
        }
    }

    private void AttachMic()
    {
        lock (_sync)
        {
            if (_micVolume is not null)
            {
                _micVolume.OnVolumeNotification -= OnMicNotification;
                _micVolume.Dispose();
                _micVolume = null;
            }
            _mic?.Dispose();
            _mic = null;
            _states.Remove(ToggleId.MicMute);
            if (_audio is null) return;
            try
            {
                if (!_audio.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)) return;
                _mic = _audio.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                _micVolume = _mic.AudioEndpointVolume;
                _micVolume.OnVolumeNotification += OnMicNotification;
                _states[ToggleId.MicMute] = MicState(_micVolume.Mute);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                Log.Info("Mic endpoint attach failed", ("error", ex.Message));
            }
        }
    }

    private static ToggleState MicState(bool muted) => new(ToggleId.MicMute, "Mic mute", "\uF781", "\uE720", muted);

    private void OnMicNotification(AudioVolumeNotificationData data)
    {
        lock (_sync) _states[ToggleId.MicMute] = MicState(data.Muted);
        Changed?.Invoke();
    }

    private sealed class DefaultDeviceSink : IMMNotificationClient
    {
        private readonly QuickToggleService _owner;
        public DefaultDeviceSink(QuickToggleService owner) => _owner = owner;
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow != DataFlow.Capture || role != Role.Console) return;
            // COM callback thread: re-attach on the thread pool, never inside the callback.
            Task.Run(() => { _owner.AttachMic(); _owner.Changed?.Invoke(); });
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    // ------------------------------------------------------------------ dark mode

    /// <summary>Called on WM_SETTINGCHANGE ("ImmersiveColorSet") so changes made elsewhere show up.</summary>
    public void RefreshDarkMode()
    {
        Task.Run(() => { ReadDarkMode(); Changed?.Invoke(); });
    }

    private void ReadDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue("AppsUseLightTheme") is not int apps) return;   // no value = cannot read: hidden
            bool dark = apps == 0;
            lock (_sync) _states[ToggleId.DarkMode] = new ToggleState(ToggleId.DarkMode, "Dark", "\uE708", "\uE708", dark);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Info("Theme registry unreadable; dark-mode toggle hidden", ("error", ex.Message));
        }
    }

    private static void WriteDarkMode(bool dark)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: true)
                        ?? throw new InvalidOperationException("Personalize key missing");
        int light = dark ? 0 : 1;
        key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
        // Tell running apps and the shell (taskbar/Start) to re-read the colour set.
        NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet",
            NativeMethods.SMTO_ABORTIFHUNG, 1000, out _);
    }

    // ------------------------------------------------------------------ do not disturb (island)

    private void ReadDnd()
    {
        bool on = _settings.Current.DoNotDisturb;
        lock (_sync) _states[ToggleId.DoNotDisturb] = new ToggleState(ToggleId.DoNotDisturb, "DND", "\uE7ED", "\uEA8F", on);
    }

    /// <summary>DND changed elsewhere (tray / settings).</summary>
    public void RefreshDnd()
    {
        ReadDnd();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ set

    /// <summary>Set a toggle. Returns null on success or the real error text; on failure the
    /// previous state stays (it is re-read, never assumed).</summary>
    public async Task<string?> SetAsync(ToggleId id, bool on)
    {
        try
        {
            switch (id)
            {
                case ToggleId.WiFi:
                case ToggleId.Bluetooth:
                {
                    Radio? radio;
                    lock (_sync) radio = _radios.FirstOrDefault(r => (r.Kind == RadioKind.WiFi) == (id == ToggleId.WiFi));
                    if (radio is null) return "Radio not found";
                    var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
                    if (result != RadioAccessStatus.Allowed) return $"Windows refused: {result}";
                    lock (_sync) _states[id] = MakeRadioState(id, radio.State == RadioState.On);
                    break;
                }
                case ToggleId.MicMute:
                    await Task.Run(() =>
                    {
                        lock (_sync)
                        {
                            if (_micVolume is null) throw new InvalidOperationException("No microphone");
                            _micVolume.Mute = on;
                            _states[id] = MicState(_micVolume.Mute);
                        }
                    });
                    break;
                case ToggleId.DarkMode:
                    await Task.Run(() => { WriteDarkMode(on); ReadDarkMode(); });
                    break;
                case ToggleId.DoNotDisturb:
                    _settings.Update(s => s.DoNotDisturb = on);
                    ReadDnd();
                    break;
            }
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Security.SecurityException or IOException)
        {
            Log.Warning("Toggle failed", ("toggle", id), ("error", ex.Message));
            return ex.Message;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        lock (_sync)
        {
            foreach (var r in _radios) r.StateChanged -= OnRadioStateChanged;
            _radios.Clear();
            if (_micVolume is not null)
            {
                _micVolume.OnVolumeNotification -= OnMicNotification;
                _micVolume.Dispose();
                _micVolume = null;
            }
            _mic?.Dispose();
            _mic = null;
            if (_audio is not null && _deviceSink is not null)
            {
                try { _audio.UnregisterEndpointNotificationCallback(_deviceSink); }
                catch (COMException) { /* endpoint enumerator already gone */ }
            }
            _deviceSink = null;
            _audio?.Dispose();
            _audio = null;
        }
        _cts?.Dispose();
    }
}
