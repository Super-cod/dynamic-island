using Microsoft.Win32;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Core.Settings;

/// <summary>Windows "run at logon" integration via HKCU\...\Run (applied, not just stored).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowsDynamicBar";

    public static void Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enable)
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) { Log.Warning("Startup registration failed", ("error", ex.Message)); }
    }
}
