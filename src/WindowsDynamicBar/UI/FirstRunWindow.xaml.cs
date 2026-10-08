using System.Windows;

namespace WindowsDynamicBar.UI;

public partial class FirstRunWindow : Window
{
    public FirstRunWindow() => InitializeComponent();

    private void OnGetStarted(object sender, RoutedEventArgs e)
    {
        SettingsWindow.ShowSingleton();
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try { App.Settings?.Update(s => s.FirstRunCompleted = true); } catch { }
    }
}
