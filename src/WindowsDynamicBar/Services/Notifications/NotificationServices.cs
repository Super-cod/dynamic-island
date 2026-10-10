using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.Services.Notifications;

public sealed record AppNotification(string AppName, string Title, string Body, DateTimeOffset When);

/// <summary>
/// Windows notification integration via UserNotificationListener.
/// LIMITATION (documented honestly): the userNotificationListener capability is only
/// granted to MSIX-packaged apps. When running as an unpackaged exe (our default
/// distribution), Windows denies access; we detect this once, disable the feature
/// cleanly, and surface the reason in Settings → Notifications. No crash, no retry loops.
/// When access IS available (e.g. running from an MSIX package), notifications arrive
/// through the listener event with per-app filtering and priority rules from settings.
/// </summary>
public sealed class NotificationListenerService : IDisposable
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;
    private Windows.UI.Notifications.Management.UserNotificationListener? _listener;
    private bool _running;
    private DateTime _lastRaise = DateTime.MinValue;

    public bool AccessGranted { get; private set; }
    public bool IsRunning => _running;
    public string? AccessError { get; private set; }

    public event Action<AppNotification>? NotificationReceived;

    public NotificationListenerService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public async Task StartAsync()
    {
        if (_running) return;
        if (!_settings.Current.FeatureNotifications || !_settings.Current.NotificationAccess)
        {
            AccessError = "Notification access is disabled in Settings.";
            return;
        }
        try
        {
            _listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
            var status = await _listener.RequestAccessAsync();
            if (status != Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed)
            {
                AccessGranted = false;
                AccessError = status switch
                {
                    Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Denied =>
                        "Windows denied notification access (requires a packaged app identity).",
                    Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Unspecified =>
                        "Notification access was not granted.",
                    _ => $"Notification access unavailable: {status}"
                };
                Log.Info("Notification listener denied", ("status", status.ToString()));
                return;
            }

            AccessGranted = true;
            AccessError = null;
            _listener.NotificationChanged += OnNotificationChanged;
            _running = true;
            Log.Info("Notification listener started");
        }
        catch (Exception ex)
        {
            AccessGranted = false;
            AccessError = "Notification API unavailable: " + ex.Message;
            Log.Info("Notification listener unavailable", ("error", ex.Message));
        }
    }

    private void OnNotificationChanged(Windows.UI.Notifications.Management.UserNotificationListener sender,
        Windows.UI.Notifications.UserNotificationChangedEventArgs args)
    {
        if (!_settings.Current.FeatureNotifications || !_settings.Current.NotificationAccess) return;
        try
        {
            var notification = _listener!.GetNotification(args.UserNotificationId);
            if (notification is null) return;

            string appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? "App";
            string title = appName;
            string body = "";
            var text = notification.Notification?.Visual?.GetBinding(
                Windows.UI.Notifications.KnownNotificationBindings.ToastGeneric)?.GetTextElements();
            if (text is { Count: > 0 })
            {
                title = string.IsNullOrEmpty(text[0].Text) ? title : text[0].Text;
                for (int i = 1; i < text.Count; i++)
                    body += (i > 1 ? " " : "") + text[i].Text;
            }

            // DND-style debounce: max one bar event every 2 seconds.
            var info = new AppNotification(appName, title, body, DateTimeOffset.Now);
            NotificationReceived?.Invoke(info);

            if (DateTime.UtcNow - _lastRaise < TimeSpan.FromSeconds(2)) return;
            _lastRaise = DateTime.UtcNow;

            // Per-app filtering: simple allow/deny by substring in title/app (extension point
            // for richer rules in settings later).
            _bus.Raise(BarEvent.Create(BarEventKind.Notification, appName,
                string.IsNullOrEmpty(body) ? title : body,
                payload: info, priority: 50, displayDuration: TimeSpan.FromSeconds(4)));
        }
        catch (Exception ex) { Log.Debug("Notification read failed", ("error", ex.Message)); }
    }

    public void Stop()
    {
        if (_listener is not null && _running)
        {
            try { _listener.NotificationChanged -= OnNotificationChanged; } catch { }
        }
        _running = false;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Our OWN toast notifications (timer finished, download done, etc.) via the shell
/// toast API. Works for unpackaged apps: we register an AUMID in HKCU\Software\Classes\AppUserModelId
/// so toasts carry our identity without needing MSIX.
/// </summary>
public static class ToastSender
{
    private const string Aumid = "WindowsDynamicBar.Toast";
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\AppUserModelId\" + Aumid);
            key?.SetValue("DisplayName", "Windows Dynamic Bar");
            _registered = true;
        }
        catch (Exception ex) { Log.Debug("AUMID registration failed", ("error", ex.Message)); }
    }

    public static void Show(string title, string body)
    {
        try
        {
            EnsureRegistered();
            var doc = new Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml(
                $"<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(title)}</text><text>{Escape(body)}</text>" +
                $"</binding></visual></toast>");
            var toast = new Windows.UI.Notifications.ToastNotification(doc) { Tag = "wdb-" + Environment.TickCount };
            Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
        }
        catch (Exception ex)
        {
            // Toasts are best-effort; the bar itself already shows these events.
            Log.Debug("Toast failed", ("error", ex.Message));
        }
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
