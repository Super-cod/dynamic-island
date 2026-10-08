using System;
using System.Drawing;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.UI;

/// <summary>
/// System tray icon (WinForms NotifyIcon hosted on the WPF UI thread), tray menu,
/// and the single-instance activation pipe (second launches toggle the bar).
/// </summary>
public sealed class TrayController : IDisposable
{
    public const string PipeName = "WindowsDynamicBar_Signal";

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private bool _disposed;

    public TrayController()
    {
        _menu = new ContextMenuStrip();
        RebuildMenu();

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = App.AppName,
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => App.Bar?.Toggle();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) App.Bar?.Toggle();
        };

        SettingsServiceBridge.Subscribe(RebuildMenu);
        StartSignalPipe();
        Log.Debug("Tray icon ready");
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();

        var toggle = new ToolStripMenuItem("Toggle bar\t(Ctrl+Alt+Space)");
        toggle.Click += (_, _) => App.Bar?.Toggle();
        _menu.Items.Add(toggle);

        var sysmon = new ToolStripMenuItem("System monitor")
        {
            CheckOnClick = true,
            Checked = App.Settings?.Current.FeatureSystemMonitor ?? false,
        };
        sysmon.CheckedChanged += (_, _) =>
        {
            bool on = sysmon.Checked;
            App.Settings?.Update(s => s.FeatureSystemMonitor = on);
            try { if (on) App.SysMon?.Activate(); else App.SysMon?.Deactivate(); } catch { }
        };
        _menu.Items.Add(sysmon);

        var barVisible = new ToolStripMenuItem("Show bar")
        {
            CheckOnClick = true,
            Checked = App.Settings?.Current.BarEnabled ?? true,
        };
        barVisible.CheckedChanged += (_, _) =>
        {
            bool on = barVisible.Checked;
            App.Settings?.Update(s => s.BarEnabled = on);
            try { if (on) App.Bar?.Show(); else App.Bar?.Hide(); } catch { }
        };
        _menu.Items.Add(barVisible);

        _menu.Items.Add(new ToolStripSeparator());

        var settings = new ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => SettingsWindow.ShowSingleton();
        _menu.Items.Add(settings);

        var logs = new ToolStripMenuItem("Open logs folder");
        logs.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = Core.Logging.Log.LogDirectory,
                UseShellExecute = true,
            }); } catch (Exception ex) { Log.Error("Open logs failed", ex); }
        };
        _menu.Items.Add(logs);

        var about = new ToolStripMenuItem($"About {App.AppName} {App.InfoVersion}");
        about.Click += (_, _) =>
        {
            MessageBox.Show(
                $"{App.AppName} {App.InfoVersion}\n\n" +
                "A Dynamic-Island-style overlay for Windows 10/11.\n" +
                "Local-only: no telemetry, no data leaves this PC.",
                "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        _menu.Items.Add(about);

        _menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        _menu.Items.Add(exit);
    }

    /// <summary>Second instance connects here and asks the first one to toggle.</summary>
    private void StartSignalPipe()
    {
        Task.Run(async () =>
        {
            while (!_disposed)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    var buf = new byte[64];
                    _ = await server.ReadAsync(buf, 0, buf.Length);
                    if (System.Windows.Application.Current?.Dispatcher is { } dsp)
                        _ = dsp.BeginInvoke(() => App.Bar?.Toggle());
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex)
                {
                    if (_disposed) return;
                    Log.Debug("Signal pipe error", ("error", ex.Message));
                    await Task.Delay(500);
                }
            }
        });
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(path)) return new Icon(path);
        }
        catch { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _icon.Visible = false; _icon.Dispose(); } catch { }
        try { _menu.Dispose(); } catch { }
    }
}

/// <summary>Bridges SettingsService.SettingsChanged to the tray menu rebuild (UI thread).</summary>
internal static class SettingsServiceBridge
{
    private static Action? _handler;

    public static void Subscribe(Action handler)
    {
        _handler = handler;
        if (App.Settings is not null)
            App.Settings.SettingsChanged += _ => Rebuild();
    }

    private static void Rebuild()
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dsp)
            dsp.BeginInvoke(() => _handler?.Invoke());
        else _handler?.Invoke();
    }
}
