using System;
using System.Windows;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.UI;

public partial class FirstRunWindow : Window
{
    private int _step = 1;

    public FirstRunWindow()
    {
        InitializeComponent();
        UpdateStep();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == 2) ApplyChoices();
        if (_step >= 3) { Finish(); return; }
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

        Dot1.Fill = DotBrush(_step >= 1);
        Dot2.Fill = DotBrush(_step >= 2);
        Dot3.Fill = DotBrush(_step >= 3);

        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step == 3 ? "Get started" : "Next";
        StepCaption.Text = _step switch
        {
            2 => "Choose your look",
            3 => "Quick start",
            _ => "A Dynamic-Island-style overlay for your desktop",
        };
        FooterHint.Text = _step == 3 ? "Takes about 15 seconds" : $"Step {_step} of 3";
    }

    private static System.Windows.Media.Brush DotBrush(bool on) =>
        on ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF))
           : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

    private void ApplyChoices()
    {
        try
        {
            var theme = ThemeLight.IsChecked == true ? AppTheme.Light
                : ThemeTranslucent.IsChecked == true ? AppTheme.Translucent
                : AppTheme.Dark;

            bool startup = StartWithWindows.IsChecked == true;
            App.Settings?.Update(s =>
            {
                s.Theme = theme;
                s.EnableAtStartup = startup;
            });
            StartupRegistration.Apply(startup);
        }
        catch { /* settings are best-effort at first run */ }
    }

    private void Finish()
    {
        try { App.Settings?.Update(s => s.FirstRunCompleted = true); } catch { }
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try { App.Settings?.Update(s => s.FirstRunCompleted = true); } catch { }
    }
}
