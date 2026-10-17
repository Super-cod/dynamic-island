using System.Windows;
using System.Windows.Media;
using WindowsDynamicBar.Services.SystemInfo;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Battery chip on the collapsed pill and the low-battery warning. Values come from BatteryService
/// via BatteryChanged events — no independent polling or duplicate PBT_POWERSETTINGCHANGE registrations.
/// Hidden entirely on PCs without a battery.
/// </summary>
public partial class BarWindow
{
    private static readonly Brush BattNormal = FrozenHud(Color.FromRgb(0xF2, 0xF2, 0xF2));
    private static readonly Brush BattOrange = FrozenHud(Color.FromRgb(0xF2, 0x9A, 0x4A));
    private static readonly Brush BattRed = FrozenHud(Color.FromRgb(0xE5, 0x48, 0x4D));

    private bool _hasBattery;
    private double _battPct = -1;
    private bool _battOnAc;
#if DEBUG
    private (double Pct, bool OnAc)? _battSim;   // debug command "battery-sim": simulated readings
#endif

    /// <summary>Initialize battery chip state from BatteryService.</summary>
    internal void InitializeBatteryState()
    {
        if (App.Battery?.Current is { } cur)
            ApplyBatteryInfo(cur);
        else
            UpdateBatteryChips();
    }

    private void OnBatteryChanged(BatteryInfo info)
    {
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(() => ApplyBatteryInfo(info)); } catch { }
            return;
        }
        ApplyBatteryInfo(info);
    }

    internal void ApplyBatteryInfo(BatteryInfo info)
    {
        if (_disposed) return;
#if DEBUG
        if (_battSim is { } sim)
        {
            _hasBattery = true;
            _battPct = sim.Pct;
            _battOnAc = sim.OnAc;
            UpdateBatteryChips();
            OnBuddyBatteryUpdated(_hasBattery, _battPct, _battOnAc);
            return;
        }
#endif
        _hasBattery = info.HasBattery;
        _battPct = info.HasBattery ? info.Percent : -1;
        _battOnAc = info.Charging;
        UpdateBatteryChips();
        OnBuddyBatteryUpdated(_hasBattery, _battPct, _battOnAc);
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
