using System.IO;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Services.Settings;

namespace WindowsDynamicBar.Services.Privacy;

/// <summary>USB attach/detach via WM_DEVICECHANGE (message-based, no polling).</summary>
public sealed class UsbService
{
    private readonly EventBus _bus;
    private readonly SettingsService _settings;

    public UsbService(EventBus bus, SettingsService settings)
    {
        _bus = bus;
        _settings = settings;
    }

    /// <summary>Called from WndProc on WM_DEVICECHANGE.</summary>
    public void OnDeviceChange(int wParam, IntPtr lParam)
    {
        if (!_settings.Current.FeatureUsb) return;
        bool arrival = wParam == Interop.NativeMethods.DBT_DEVICEARRIVAL;
        bool removal = wParam == Interop.NativeMethods.DBT_DEVICEREMOVECOMPLETE;
        if (!arrival && !removal) return;

        string name = "USB device";
        try
        {
            if (lParam != IntPtr.Zero)
            {
                int devType = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 4);
                if (devType == Interop.NativeMethods.DBT_DEVTYP_VOLUME)
                {
                    int mask = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 12);
                    for (int i = 0; i < 26; i++)
                    {
                        if ((mask & (1 << i)) == 0) continue;
                        char drive = (char)('A' + i);
                        string label = "";
                        try
                        {
                            var driveInfo = new DriveInfo($"{drive}");
                            if (driveInfo.IsReady && !string.IsNullOrEmpty(driveInfo.VolumeLabel))
                                label = " · " + driveInfo.VolumeLabel;
                        }
                        catch { }
                        name = $"{drive}: drive{label}";
                        break;
                    }
                }
            }
        }
        catch { /* keep generic name */ }

        _bus.Raise(BarEvent.Create(BarEventKind.Usb,
            arrival ? "USB connected" : "USB removed", name,
            priority: 50, displayDuration: TimeSpan.FromSeconds(3)));
    }
}
