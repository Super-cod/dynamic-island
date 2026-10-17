using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Interop;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Battery chip on the collapsed pill and the low-battery warning. Values come from Windows power
/// notifications (RegisterPowerSettingNotification: battery percentage and AC/DC source, delivered
/// as WM_POWERBROADCAST) - no polling. Hidden entirely on PCs without a battery.
/// </summary>
public partial class BarWindow
{
    private static readonly Guid GuidBatteryPercentage = new("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");
    private static readonly Guid GuidAcDcPowerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    private const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;

    private static readonly Brush BattNormal = FrozenHud(Color.FromRgb(0xF2, 0xF2, 0xF2));
    private static readonly Brush BattOrange = FrozenHud(Color.FromRgb(0xF2, 0x9A, 0x4A));
    private static readonly Brush BattRed = FrozenHud(Color.FromRgb(0xE5, 0x48, 0x4D));

    private IntPtr _powerNotifyPercent;
    private IntPtr _powerNotifyAc;
    private bool _hasBattery;
    private double _battPct = -1;
    private bool _battOnAc;
#if DEBUG
    private (double Pct, bool OnAc)? _battSim;   // debug command "battery-sim": simulated readings
#endif

    /// <summary>After the window handle exists: read once, then subscribe to changes.</summary>
    private void StartBatteryNotifications()
    {
        ReadBattery();
        UpdateBatteryChips();
        if (!_hasBattery || _hwnd == IntPtr.Zero) return;
        Guid pct = GuidBatteryPercentage, ac = GuidAcDcPowerSource;
        _powerNotifyPercent = NativeMethods.RegisterPowerSettingNotification(_hwnd, ref pct, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        _powerNotifyAc = NativeMethods.RegisterPowerSettingNotification(_hwnd, ref ac, 0);
        if (_powerNotifyPercent == IntPtr.Zero || _powerNotifyAc == IntPtr.Zero)
            Log.Info("Battery change notifications unavailable; chip updates on power status events only");
    }

    private void StopBatteryNotifications()
    {
        if (_powerNotifyPercent != IntPtr.Zero) { NativeMethods.UnregisterPowerSettingNotification(_powerNotifyPercent); _powerNotifyPercent = IntPtr.Zero; }
        if (_powerNotifyAc != IntPtr.Zero) { NativeMethods.UnregisterPowerSettingNotification(_powerNotifyAc); _powerNotifyAc = IntPtr.Zero; }
    }

    /// <summary>WM_POWERBROADCAST: true when it was a battery/AC change we care about.</summary>
    private static bool IsBatteryBroadcast(IntPtr wParam) =>
        wParam.ToInt32() is PBT_POWERSETTINGCHANGE or PBT_APMPOWERSTATUSCHANGE;

    private void OnBatteryChanged()
    {
        ReadBattery();
        UpdateBatteryChips();
        OnBuddyBatteryUpdated(_hasBattery, _battPct, _battOnAc);
    }

    private void ReadBattery()
    {
#if DEBUG
        if (_battSim is { } sim) { _hasBattery = true; _battPct = sim.Pct; _battOnAc = sim.OnAc; return; }
#endif
        if (!NativeMethods.GetSystemPowerStatus(out var s)) { _hasBattery = false; return; }
        // BatteryFlag 128 = no system battery, 255 = unknown status.
        _hasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255 && s.BatteryLifePercent != 255;
        _battPct = _hasBattery ? s.BatteryLifePercent : -1;
        _battOnAc = s.ACLineStatus == 1;
    }

    private void UpdateBatteryChips()
    {
        bool show = _hasBattery && _battPct >= 0;
        BattChipIdle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BattChipMusic.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        int pct = (int)Math.Round(_battPct);
        string glyph = BatteryGlyph(pct, _battOnAc);
        Brush color = _battOnAc || pct > 30 ? BattNormal : pct > 15 ? BattOrange : BattRed;
        string tip = _battOnAc ? $"Battery {pct}% · plugged in" : $"Battery {pct}%";
        foreach (var (g, t, chip) in new[] { (BattChipIdleGlyph, BattChipIdleText, BattChipIdle), (BattChipMusicGlyph, BattChipMusicText, BattChipMusic) })
        {
            g.Text = glyph;
            g.Foreground = color;
            t.Text = $"{pct}%";
            t.Foreground = color;
            chip.ToolTip = tip;
        }
    }

    /// <summary>Segoe Fluent battery glyphs: level 0..10, charging variants include the bolt.</summary>
    internal static string BatteryGlyph(int percent, bool charging)
    {
        int level = Math.Clamp((int)Math.Round(percent / 10.0), 0, 10);
        if (charging)
            return level switch { 10 => "", 9 => "", _ => ((char)(0xE85A + level)).ToString() };
        return level == 10 ? "" : ((char)(0xE850 + level)).ToString();
    }
}
