using Microsoft.Win32;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Core.Settings;

/// <summary>Windows "run at logon" integration via HKCU\...\Run (applied, not just stored).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowsDynamicBar";

    /// <summary>True when the Run entry exists and points at this executable.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string v && Environment.ProcessPath is { } exe
                   && v.Trim('"').Equals(exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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
