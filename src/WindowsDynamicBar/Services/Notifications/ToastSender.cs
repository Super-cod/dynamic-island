using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Services.Notifications;

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
