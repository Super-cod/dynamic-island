using System.Linq;
using System.Windows;
using System.Windows.Media;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Core.Telemetry;

namespace WindowsDynamicBar.UI;

/// <summary>Live application of the Features tab: a switched-off feature loses its ring, button,
/// hotkey and timers (services are stopped/released in App.OnSettingsChangedForServices).</summary>
public partial class BarWindow
{
    private string _appliedLayout = "";

    private void ApplyFeatureSettings(SettingsModel m, bool first)
    {
        bool mirrorWasOn = _cfg.FeatureMirror;
        _cfg.FeatureMedia = m.FeatureMedia;
        _cfg.FeatureIdleDashboard = m.FeatureIdleDashboard;
        _cfg.FeatureNotifications = m.FeatureNotifications;
        _cfg.FeatureVolumeHud = m.FeatureVolumeHud;
        _cfg.FeatureSearch = m.FeatureSearch;
        _cfg.FeatureMirror = m.FeatureMirror;
        _cfg.FeatureSpeedTest = m.FeatureSpeedTest;
        _cfg.FeatureToggles = m.FeatureToggles;
        _cfg.FeatureTimers = m.FeatureTimers;
        _cfg.ClockFormat = m.ClockFormat;
        _cfg.AccentMode = m.AccentMode;
        _cfg.ReduceMotionMode = m.ReduceMotionMode;
        _twelveHour = ResolveTwelveHour();

        if (!m.FeatureMirror && (mirrorWasOn || first)) MirrorPanelComponent.StopCamera();
        IdleSearchButton.Visibility = m.FeatureSearch ? Visibility.Visible : Visibility.Collapsed;
        IdleFocusButton.Visibility = m.FeatureTimers ? Visibility.Visible : Visibility.Collapsed;
        IdleOpenPanel.Opacity = m.FeatureIdleDashboard ? 1 : 0;
        IdleDotClock.Visibility = m.FeatureIdleDashboard ? Visibility.Visible : Visibility.Hidden;
        IdleGreeting.Visibility = m.FeatureIdleDashboard ? Visibility.Visible : Visibility.Hidden;
        if (!m.FeatureIdleDashboard) IdleEventLine.Visibility = Visibility.Collapsed;
        NetPanelComponent.SpeedTestEnabled = m.FeatureSpeedTest;
        if (!first) ApplyLayoutMode();   // music switched off -> idle layout
    }

    /// <summary>Rings from settings minus the ones whose feature is off.</summary>
    private void ApplyRingLayout(bool first)
    {
        var (p1, p2) = IslandLayout.FromSettings(_cfg.IslandPage1, _cfg.IslandPage2);
        p1 = p1.Where(RingEnabled).ToList();
        p2 = p2.Where(RingEnabled).ToList();
        string key = IslandLayout.Format(p1) + "|" + IslandLayout.Format(p2);
        if (!first && key == _appliedLayout) return;
        _appliedLayout = key;
        IconRow.SetLayout(p1, p2);
        if (_islandState == IslandVisualState.Detail && !IconRow.IsOnVisiblePage(_activeMetric))
            SetIslandState(IslandVisualState.Expanded, IslandMetric.None, force: true);
        UpdateIconRowValues();
    }

    private bool RingEnabled(IslandMetric m) => m switch
    {
        IslandMetric.Mirror => _cfg.FeatureMirror,
        IslandMetric.Clipboard => _cfg.FeatureClipboardHistory,
        IslandMetric.Toggles => _cfg.FeatureToggles,
        IslandMetric.Timer => _cfg.FeatureTimers,
        _ => true,
    };

    private static double HueOf(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d < 1e-6) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 + 360) % 360;
    }
}
