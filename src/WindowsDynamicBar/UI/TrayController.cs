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
        // Rebuilt on every settings change: Clear() alone leaves the old items undisposed.
        var old = new ToolStripItem[_menu.Items.Count];
        _menu.Items.CopyTo(old, 0);
        _menu.Items.Clear();
        foreach (var item in old) item.Dispose();

        var open = new ToolStripMenuItem("Open island");
        open.Click += (_, _) => App.Bar?.Toggle();
        _menu.Items.Add(open);

        var search = new ToolStripMenuItem("Search");
        search.Click += (_, _) => App.Bar?.OpenSearch();
        _menu.Items.Add(search);

        var settings = new ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => SettingsWindow.ShowSingleton();
        _menu.Items.Add(settings);

        _menu.Items.Add(new ToolStripSeparator());

        bool paused = !(App.Settings?.Current.BarEnabled ?? true);
        var pauseIsland = new ToolStripMenuItem("Pause island")
        {
            CheckOnClick = true,
            Checked = paused,
            ToolTipText = "Hides the island while keeping the tray icon running",
        };
        pauseIsland.CheckedChanged += (_, _) =>
        {
            bool isPaused = pauseIsland.Checked;
            App.Settings?.Update(s => s.BarEnabled = !isPaused);
            try { if (isPaused) App.Bar?.Hide(); else App.Bar?.Show(); } catch { }
        };
        _menu.Items.Add(pauseIsland);

        var dnd = new ToolStripMenuItem("Do Not Disturb")
        {
            CheckOnClick = true,
            Checked = App.GamingMode,
            ToolTipText = "Only critical alerts (battery, privacy) expand the island",
        };
        dnd.CheckedChanged += (_, _) => App.GamingMode = dnd.Checked;
        _menu.Items.Add(dnd);

        var updates = new ToolStripMenuItem("Check for updates…");
        updates.Click += async (_, _) =>
        {
            try
            {
                string res = await App.CheckForUpdatesAsync(interactive: true);
                if (!string.IsNullOrEmpty(res))
                {
                    _icon.ShowBalloonTip(3000, App.AppName, res, ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Tray update check", ex);
            }
        };
        _menu.Items.Add(updates);

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

        _menu.Items.Add(new ToolStripSeparator());

        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        _menu.Items.Add(quit);
    }

    /// <summary>Re-creates the tray icon after explorer.exe restarts (TaskbarCreated).</summary>
    public void RecreateIcon()
    {
        try
        {
            _icon.Visible = false;
            _icon.Icon = LoadAppIcon();
            _icon.Visible = true;
        }
        catch { }
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
                    var buf = new byte[512];
                    int n = await server.ReadAsync(buf, 0, buf.Length);
                    string msg = Encoding.UTF8.GetString(buf, 0, Math.Max(0, n));
                    if (System.Windows.Application.Current?.Dispatcher is { } dsp)
                    {
#if DEBUG
                        // Debug builds only: scripted UI tests drive the island without moving the mouse.
                        if (msg.StartsWith("cmd:", StringComparison.Ordinal))
                        {
                            string cmd = msg[4..].Trim();
                            _ = dsp.BeginInvoke(() => App.Bar?.RunDebugCommand(cmd));
                            continue;
                        }
#endif
                        _ = dsp.BeginInvoke(() => App.Bar?.Toggle());
                    }
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
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            var streamInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamInfo?.Stream is { } stream) return new Icon(stream);
        }
        catch { }
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(path)) return new Icon(path);
        }
        catch { }
        try
        {
            if (Environment.ProcessPath is { } exePath && System.IO.File.Exists(exePath))
            {
                var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon != null) return icon;
            }
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
