using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.UI;

/// <summary>
/// First-run 4-step wizard:
/// 1. Welcome
/// 2. Features selection
/// 3. Permissions (only for selected features: notifications, camera, startup)
/// 4. Shortcuts (search, clipboard with "Try it")
/// </summary>
public partial class FirstRunWindow : Window
{
    private static FirstRunWindow? _open;

    /// <summary>Open the guide (or bring the open one forward).</summary>
    public static void ShowSingleton()
    {
        if (_open is null)
        {
            _open = new FirstRunWindow();
            _open.Closed += (_, _) =>
            {
                _open = null;
                App.Settings?.Update(s => s.FirstRunCompleted = true);
            };
            _open.Show();
        }
        _open.Activate();
    }

    private int _step = 1;
    private bool _startupEnabled;

    public FirstRunWindow()
    {
        InitializeComponent();
        LoadCurrentSettings();
        UpdateStep();
    }

    private void LoadCurrentSettings()
    {
        var s = App.Settings?.Current ?? new SettingsModel();
        FeatMedia.IsChecked = s.FeatureMedia;
        FeatIdle.IsChecked = s.FeatureIdleDashboard;
        FeatNotifications.IsChecked = s.FeatureNotifications;
        FeatVolumeHud.IsChecked = s.FeatureVolumeHud;
        FeatSearch.IsChecked = s.FeatureSearch;
        FeatMirror.IsChecked = s.FeatureMirror;
        FeatSpeedTest.IsChecked = s.FeatureSpeedTest;
        FeatClipboard.IsChecked = s.FeatureClipboardHistory;
        FeatToggles.IsChecked = s.FeatureToggles;
        FeatTimers.IsChecked = s.FeatureTimers;

        ShortcutSearchBox.Text = string.IsNullOrWhiteSpace(s.HotkeySearch) ? "Ctrl+Space" : s.HotkeySearch;
        ShortcutClipBox.Text = string.IsNullOrWhiteSpace(s.HotkeyClipboard) ? "Ctrl+Alt+V" : s.HotkeyClipboard;

        _startupEnabled = StartupRegistration.IsEnabled();
        UpdateStartupStatusText();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == 2)
        {
            ApplyFeatureChoices();
        }
        else if (_step == 4)
        {
            Finish();
            return;
        }

        _step++;
        UpdateStep();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step <= 1) return;
        _step--;
        UpdateStep();
    }

    private void UpdateStep()
    {
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        Dot1.Fill = DotBrush(_step >= 1);
        Dot2.Fill = DotBrush(_step >= 2);
        Dot3.Fill = DotBrush(_step >= 3);
        Dot4.Fill = DotBrush(_step >= 4);

        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step == 4 ? "Finish" : "Next";

        StepCaption.Text = _step switch
        {
            1 => "Dynamic-Island-style interactive overlay for your desktop",
            2 => "Select features (all on by default)",
            3 => "System permissions & startup",
            4 => "Keyboard shortcuts & launch",
            _ => "",
        };

        FooterHint.Text = $"Step {_step} of 4";

        if (_step == 3)
        {
            // Only show permission cards for features selected in Step 2
            PermNotifCard.Visibility = FeatNotifications.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PermCameraCard.Visibility = FeatMirror.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            // Check real status of permissions now (never requested before user reaches this step)
            RefreshPermissionsStatus();
        }
    }

    private static Brush DotBrush(bool on) =>
        on ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))
           : new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

    private void ApplyFeatureChoices()
    {
        try
        {
            App.Settings?.Update(s =>
            {
                s.FeatureMedia = FeatMedia.IsChecked == true;
                s.FeatureIdleDashboard = FeatIdle.IsChecked == true;
                s.FeatureNotifications = FeatNotifications.IsChecked == true;
                s.FeatureVolumeHud = FeatVolumeHud.IsChecked == true;
                s.FeatureSearch = FeatSearch.IsChecked == true;
                s.FeatureMirror = FeatMirror.IsChecked == true;
                s.FeatureSpeedTest = FeatSpeedTest.IsChecked == true;
                s.FeatureClipboardHistory = FeatClipboard.IsChecked == true;
                s.FeatureToggles = FeatToggles.IsChecked == true;
                s.FeatureTimers = FeatTimers.IsChecked == true;
            });
        }
        catch { }
    }

    private void RefreshPermissionsStatus()
    {
        if (FeatNotifications.IsChecked == true)
        {
            try
            {
                var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
                var status = listener.GetAccessStatus();
                if (status == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed)
                {
                    PermNotifStatus.Text = "Status: Granted";
                    PermNotifStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
                    PermNotifButton.Content = "Granted";
                    PermNotifButton.IsEnabled = false;
                }
                else
                {
                    PermNotifStatus.Text = $"Status: {status} (click to request access)";
                    PermNotifStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
                    PermNotifButton.Content = "Enable";
                    PermNotifButton.IsEnabled = true;
                }
            }
            catch (Exception)
            {
                PermNotifStatus.Text = "Status: Setup via Windows Settings";
                PermNotifStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
            }
        }

        _startupEnabled = StartupRegistration.IsEnabled();
        UpdateStartupStatusText();
    }

    private async void OnRequestNotifPermission(object sender, RoutedEventArgs e)
    {
        try
        {
            var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
            var status = await listener.RequestAccessAsync();
            if (status == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed)
            {
                PermNotifStatus.Text = "Status: Granted";
                PermNotifStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
                PermNotifButton.Content = "Granted";
                PermNotifButton.IsEnabled = false;
                App.Settings?.Update(s => s.NotificationAccess = true);
            }
            else
            {
                Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
            }
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true }); } catch { }
        }
    }

    private void OnOpenCameraSettings(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true }); } catch { }
    }

    private void OnToggleStartup(object sender, RoutedEventArgs e)
    {
        _startupEnabled = !_startupEnabled;
        StartupRegistration.Apply(_startupEnabled);
        App.Settings?.Update(s => s.EnableAtStartup = _startupEnabled);
        UpdateStartupStatusText();
    }

    private void UpdateStartupStatusText()
    {
        PermStartupStatus.Text = _startupEnabled ? "Status: Enabled" : "Status: Disabled";
        PermStartupStatus.Foreground = _startupEnabled
            ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
        PermStartupToggle.Content = _startupEnabled ? "Disable" : "Enable";
    }

    private void OnTrySearch(object sender, RoutedEventArgs e)
    {
        try { App.Bar?.OpenSearch(); } catch { }
    }

    private void OnTryClipboard(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.Bar is { } bar)
            {
                // Trigger clipboard panel
                var method = bar.GetType().GetMethod("OpenClipboardPanel",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                method?.Invoke(bar, new object[] { true });
            }
        }
        catch { }
    }

    private void Finish()
    {
        try
        {
            string searchKey = ShortcutSearchBox.Text.Trim();
            string clipKey = ShortcutClipBox.Text.Trim();
            App.Settings?.Update(s =>
            {
                s.FirstRunCompleted = true;
                if (!string.IsNullOrEmpty(searchKey)) s.HotkeySearch = searchKey;
                if (!string.IsNullOrEmpty(clipKey)) s.HotkeyClipboard = clipKey;
            });
        }
        catch { }
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _open = null;
    }
}
