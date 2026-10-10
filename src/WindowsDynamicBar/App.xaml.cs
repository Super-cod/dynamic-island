using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Services.Audio;
using WindowsDynamicBar.Services.Clipboard;
using WindowsDynamicBar.Services.Media;
using WindowsDynamicBar.Services.Notifications;
using WindowsDynamicBar.Services.Privacy;
using WindowsDynamicBar.Services.SystemInfo;
using WindowsDynamicBar.Services.SystemMonitor;
using WindowsDynamicBar.Services.Timers;
using WindowsDynamicBar.Services.Toggles;
using WindowsDynamicBar.Services.Updates;
using WindowsDynamicBar.UI;

namespace WindowsDynamicBar;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private bool _logReady;
    public const string MutexName = "Local\\WindowsDynamicBar_SingleInstance_v1";

    // Composition root
    public static SettingsService Settings { get; private set; } = null!;
    public static EventBus Bus { get; private set; } = null!;
    public static VolumeService Volume { get; private set; } = null!;
    public static BrightnessService Brightness { get; private set; } = null!;
    public static MediaService Media { get; private set; } = null!;
    public static BatteryService Battery { get; private set; } = null!;
    public static NetworkService Network { get; private set; } = null!;
    public static BluetoothService Bluetooth { get; private set; } = null!;
    public static PrivacyService Privacy { get; private set; } = null!;
    public static ClipboardService Clipboard { get; private set; } = null!;
    public static DownloadService Downloads { get; private set; } = null!;
    public static UsbService Usb { get; private set; } = null!;
    public static ScreenshotService Screenshots { get; private set; } = null!;
    public static TimerService Timer { get; private set; } = null!;
    public static StopwatchService Stopwatch { get; private set; } = null!;
    public static FocusService Focus { get; private set; } = null!;
    public static SystemMonitorService SysMon { get; private set; } = null!;
    public static SystemTelemetryService Telemetry { get; private set; } = null!;
    public static NotificationListenerService Notifications { get; private set; } = null!;
    public static QuickToggleService? Toggles { get; private set; }
    public static ClipboardHistoryService? ClipHistory { get; private set; }

    public static BarWindow? Bar { get; set; }
    public static SettingsWindow? SettingsWnd { get; set; }
    public static TrayController? Tray { get; private set; }

    /// <summary>Session-only: while true, low-priority events do not expand the bar.</summary>
    public static bool GamingMode { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: activate existing instead of spawning a second bar.
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        Settings = SettingsService.Instance;
        var cfg = Settings.Load();

        Log.Init((LogLevel)cfg.LogLevel);
        _logReady = true;
        Log.Info("Windows Dynamic Bar starting", ("version", InfoVersion), ("pid", Environment.ProcessId));

        var sw = System.Diagnostics.Stopwatch.StartNew();

        Bus = new EventBus(Dispatcher);

        // Services — each constructed and started defensively: a failure in one
        // subsystem must never prevent the bar from running.
        try { Volume = new VolumeService(Bus, Settings); } catch (Exception ex) { Log.Error("Volume ctor", ex); }
        try { Brightness = new BrightnessService(Bus, Settings); } catch (Exception ex) { Log.Error("Brightness ctor", ex); }
        try { Media = new MediaService(Bus); } catch (Exception ex) { Log.Error("Media ctor", ex); }
        try { Battery = new BatteryService(Bus, Settings); } catch (Exception ex) { Log.Error("Battery ctor", ex); }
        try { Network = new NetworkService(Bus, Settings); } catch (Exception ex) { Log.Error("Network ctor", ex); }
        try { Bluetooth = new BluetoothService(Bus, Settings); } catch (Exception ex) { Log.Error("Bluetooth ctor", ex); }
        try { Privacy = new PrivacyService(Bus, Settings); } catch (Exception ex) { Log.Error("Privacy ctor", ex); }
        try { Clipboard = new ClipboardService(Bus, Settings); } catch (Exception ex) { Log.Error("Clipboard ctor", ex); }
        try { Downloads = new DownloadService(Bus, Settings); } catch (Exception ex) { Log.Error("Downloads ctor", ex); }
        try { Usb = new UsbService(Bus, Settings); } catch (Exception ex) { Log.Error("Usb ctor", ex); }
        try { Screenshots = new ScreenshotService(Bus, Settings); } catch (Exception ex) { Log.Error("Screenshots ctor", ex); }
        try { Timer = new TimerService(Bus, Settings); } catch (Exception ex) { Log.Error("Timer ctor", ex); }
        try { Stopwatch = new StopwatchService(Bus); } catch (Exception ex) { Log.Error("Stopwatch ctor", ex); }
        try { Focus = new FocusService(Bus); } catch (Exception ex) { Log.Error("Focus ctor", ex); }
        try { SysMon = new SystemMonitorService(Settings); } catch (Exception ex) { Log.Error("SysMon ctor", ex); }
        try { Telemetry = new SystemTelemetryService(Settings); } catch (Exception ex) { Log.Error("Telemetry ctor", ex); }
        try { Notifications = new NotificationListenerService(Bus, Settings); } catch (Exception ex) { Log.Error("Notifications ctor", ex); }
        if (cfg.FeatureToggles)
        {
            try { Toggles = new QuickToggleService(Settings); } catch (Exception ex) { Log.Error("Toggles ctor", ex); }
        }
        try { ClipHistory = new ClipboardHistoryService(Settings, Dispatcher); } catch (Exception ex) { Log.Error("Clipboard history ctor", ex); }
        if (cfg.FeatureClipboardHistory) StartServiceSafely(() => ClipHistory?.Start(), "clipboard history");
        Settings.SettingsChanged += OnSettingsChangedForServices;

        // Bar window
        Bar = new BarWindow();
        if (cfg.BarEnabled) Bar.Show();

        Tray = new TrayController();

        // Deferred starts (each isolated)
        StartServiceSafely(() => Volume?.Start(), "volume");
        if (cfg.FeatureVolumeHud) StartServiceSafely(() => Brightness?.Start(), "brightness");
        if (cfg.FeatureMedia) StartServiceSafelyAsync(async () => await Media.StartAsync(), "media");
        StartServiceSafely(() => Battery?.Start(), "battery");
        StartServiceSafely(() => Network?.Start(), "network");
        StartServiceSafely(() => Bluetooth?.Start(), "bluetooth");
        StartServiceSafely(() => Privacy?.Start(), "privacy");
        StartServiceSafely(() => Screenshots?.Start(), "screenshots");
        StartServiceSafely(() => Downloads?.Start(), "downloads");
        if (cfg.FeatureNotifications) StartServiceSafelyAsync(async () => await Notifications.StartAsync(), "notifications");
        if (Toggles is not null) StartServiceSafelyAsync(() => Toggles.StartAsync(), "toggles");
        StartServiceSafely(() => Timer?.RestoreFromState(), "timer restore");

        if (cfg.FeatureSystemMonitor)
            StartServiceSafely(() => SysMon?.Activate(), "sysmon");

        // First-run experience
        if (!cfg.FirstRunCompleted) FirstRunWindow.ShowSingleton();

        // A damaged settings file was moved aside: say so once.
        if (Settings.LoadProblem is { } problem)
        {
            Bus.Raise(BarEvent.Create(BarEventKind.Toast, "Settings were reset", problem,
                priority: 95, displayDuration: TimeSpan.FromSeconds(10)));
            Settings.ClearLoadProblem();
        }

        sw.Stop();
        Log.Info("Startup complete", ("ms", sw.ElapsedMilliseconds));
    }

    /// <summary>Feature switches that start/stop whole services (an off feature loads nothing).</summary>
    private static void OnSettingsChangedForServices(SettingsModel m)
    {
        var clip = ClipHistory;
        if (clip is not null)
        {
            if (m.FeatureClipboardHistory && !clip.Enabled) clip.Start();
            else if (!m.FeatureClipboardHistory && clip.Enabled) clip.Stop();
            else if (clip.Enabled) clip.ApplySettings(m);
        }

        if (Media is not null)
        {
            if (m.FeatureMedia && !Media.IsRunning) StartServiceSafelyAsync(() => Media.StartAsync(), "media");
            else if (!m.FeatureMedia && Media.IsRunning) Media.Stop();
        }

        if (Notifications is not null)
        {
            if (m.FeatureNotifications && !Notifications.IsRunning) StartServiceSafelyAsync(() => Notifications.StartAsync(), "notifications");
            else if (!m.FeatureNotifications && Notifications.IsRunning) Notifications.Stop();
        }

        if (m.FeatureToggles && Toggles is null)
        {
            Toggles = new QuickToggleService(Settings);
            StartServiceSafelyAsync(() => Toggles.StartAsync(), "toggles");
            ServiceToggled?.Invoke();
        }
        else if (!m.FeatureToggles && Toggles is not null)
        {
            var t = Toggles;
            Toggles = null;
            ServiceToggled?.Invoke();
            t.Dispose();
        }
    }

    /// <summary>A service object was created or released (the bar re-hooks its events).</summary>
    public static event Action? ServiceToggled;

    /// <summary>"Check for updates" (About tab, tray). Returns a status line for the UI.</summary>
    public static Task<string> CheckForUpdatesAsync(bool interactive) => Updater.CheckAsync(Settings.Current, Bus, interactive);

    public const string InfoVersion = "1.1.0";
    public const string AppName = "Windows Dynamic Bar";

    private static void StartServiceSafely(Action action, string name)
    {
        try { action(); }
        catch (Exception ex) { Log.Error($"Service '{name}' failed to start", ex); }
    }

    private static void StartServiceSafelyAsync(Func<Task> action, string name)
    {
        Task.Run(async () =>
        {
            try { await action(); }
            catch (Exception ex) { Log.Error($"Service '{name}' failed to start", ex); }
        });
    }

    private void SignalExistingInstance()
    {
        try
        {
            // Best-effort: find and activate the running instance's window.
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".", "WindowsDynamicBar_Signal", System.IO.Pipes.PipeDirection.Out);
            pipe.Connect(300);
            var bytes = System.Text.Encoding.UTF8.GetBytes("activate");
            pipe.Write(bytes, 0, bytes.Length);
        }
        catch { /* old instance may not be listening; exiting is correct anyway */ }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true; // never let one bug kill the whole bar
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_logReady)
        {
            Log.Info("Shutting down");
            if (Settings is not null) Settings.SettingsChanged -= OnSettingsChangedForServices;
            try { Tray?.Dispose(); } catch { }
            try { Bar?.Dispose(); } catch { }
            foreach (IDisposable? svc in new IDisposable?[]
                     {
                         Media, Volume, Brightness, Battery, Network, Bluetooth, Privacy,
                         Clipboard, Downloads, Screenshots, Timer, Stopwatch, Focus, SysMon, Telemetry, Notifications,
                         Toggles, ClipHistory
                     })
            {
                try { svc?.Dispose(); } catch { }
            }
            Log.Shutdown();
        }
        // Only the owning thread may release the mutex; a second instance
        // (which signaled us or exited early) never took ownership.
        if (_ownsSingleInstanceMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        }
        try { _singleInstanceMutex?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
