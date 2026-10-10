using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Media;

namespace WindowsDynamicBar.Core.Search;

public sealed class AppDiscoveryService
{
    private static readonly Lazy<AppDiscoveryService> Lazy = new(() => new AppDiscoveryService());
    public static AppDiscoveryService Instance => Lazy.Value;

    private readonly object _sync = new();
    private List<AppInfo> _apps = new();
    private Dictionary<string, int> _launchCounts = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    private readonly string _statsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WindowsDynamicBar", "search_stats.json");

    private AppDiscoveryService()
    {
        LoadStats();
    }

    public IReadOnlyList<AppInfo> GetApps()
    {
        lock (_sync)
        {
            if (!_initialized)
            {
                _apps = DiscoverAppsInternal();
                _initialized = true;
            }
            return _apps.ToList();
        }
    }

    public void RefreshInBackground()
    {
        Task.Run(() =>
        {
            try
            {
                var discovered = DiscoverAppsInternal();
                lock (_sync)
                {
                    _apps = discovered;
                    _initialized = true;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("App discovery background refresh failed", ("error", ex.Message));
            }
        });
    }

    public void RecordLaunch(string appIdOrName)
    {
        if (string.IsNullOrWhiteSpace(appIdOrName)) return;
        lock (_sync)
        {
            _launchCounts[appIdOrName] = _launchCounts.GetValueOrDefault(appIdOrName, 0) + 1;
            var app = _apps.FirstOrDefault(a => a.AppId.Equals(appIdOrName, StringComparison.OrdinalIgnoreCase) ||
                                                a.Name.Equals(appIdOrName, StringComparison.OrdinalIgnoreCase));
            if (app is not null)
            {
                app.LaunchCount = _launchCounts[appIdOrName];
            }
            SaveStats();
        }
    }

    public int GetLaunchCount(string appIdOrName)
    {
        lock (_sync)
        {
            return _launchCounts.GetValueOrDefault(appIdOrName, 0);
        }
    }

    private void LoadStats()
    {
        try
        {
            if (File.Exists(_statsPath))
            {
                string json = File.ReadAllText(_statsPath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                if (dict is not null)
                {
                    _launchCounts = new Dictionary<string, int>(dict, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch { }
    }

    private void SaveStats()
    {
        try
        {
            string dir = Path.GetDirectoryName(_statsPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(_launchCounts, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_statsPath, json);
        }
        catch { }
    }

    private List<AppInfo> DiscoverAppsInternal()
    {
        var result = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);

        // 1. Add well-known core applications first
        AddKnownCoreApps(result);

        // 2. Scan Start Menu programs
        ScanStartMenuDirectory(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), result);
        ScanStartMenuDirectory(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), result);

        // Apply launch counts & aliases
        foreach (var app in result.Values)
        {
            if (_launchCounts.TryGetValue(app.AppId, out int cnt) || _launchCounts.TryGetValue(app.Name, out cnt))
            {
                app.LaunchCount = cnt;
            }
            AssignKnownAliases(app);
        }

        return result.Values.OrderByDescending(a => a.LaunchCount).ThenBy(a => a.Name).ToList();
    }

    private void AddKnownCoreApps(Dictionary<string, AppInfo> result)
    {
        void Add(string name, string exe, string appId, string[] aliases)
        {
            BitmapSource? icon = AppIconHelper.ExtractIconFromFile(exe);

            var info = new AppInfo
            {
                Name = name,
                ExePath = exe,
                AppId = appId,
                Icon = icon,
                Aliases = aliases.ToList()
            };
            result[name] = info;
        }

        // Chrome
        string chromePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe");
        if (!File.Exists(chromePath))
            chromePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe");
        if (File.Exists(chromePath))
            Add("Google Chrome", chromePath, "chrome", new[] { "chrome", "google chrome", "browser" });

        // Terminal / PowerShell
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe");
        Add("Terminal", pwsh, "terminal", new[] { "term", "cmd", "powershell", "windows terminal" });

        // VS Code
        string codePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe");
        if (!File.Exists(codePath))
            codePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft VS Code\Code.exe");
        if (File.Exists(codePath))
            Add("Visual Studio Code", codePath, "vscode", new[] { "vscode", "vs code", "code" });

        // Spotify
        string spotifyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Spotify\Spotify.exe");
        if (File.Exists(spotifyPath))
            Add("Spotify", spotifyPath, "spotify", new[] { "spotify", "music" });

        // Explorer
        string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Add("File Explorer", explorerPath, "explorer", new[] { "files", "file explorer", "explorer" });
    }

    private void ScanStartMenuDirectory(string basePath, Dictionary<string, AppInfo> result)
    {
        try
        {
            string progDir = Path.Combine(basePath, "Programs");
            if (!Directory.Exists(progDir)) progDir = basePath;
            if (!Directory.Exists(progDir)) return;

            var lnkFiles = Directory.GetFiles(progDir, "*.lnk", SearchOption.AllDirectories);
            foreach (var lnk in lnkFiles)
            {
                string name = Path.GetFileNameWithoutExtension(lnk);
                if (IsIgnoredShortcut(name)) continue;
                if (result.ContainsKey(name)) continue;

                BitmapSource? icon = AppIconHelper.ExtractIconFromFile(lnk);

                var info = new AppInfo
                {
                    Name = name,
                    ExePath = lnk,
                    AppId = lnk,
                    Icon = icon,
                };
                AssignKnownAliases(info);
                result[name] = info;
            }
        }
        catch { }
    }

    private static bool IsIgnoredShortcut(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower.Contains("uninstall") || lower.Contains("read me") || lower.Contains("help") || lower.Contains("website");
    }

    private static void AssignKnownAliases(AppInfo app)
    {
        string n = app.Name.ToLowerInvariant();
        if (n.Contains("chrome") && !app.Aliases.Contains("chrome"))
        {
            app.Aliases.Add("chrome");
            app.Aliases.Add("google chrome");
        }
        else if ((n.Contains("terminal") || n.Contains("powershell") || n.Contains("cmd")) && !app.Aliases.Contains("term"))
        {
            app.Aliases.Add("term");
            app.Aliases.Add("cmd");
            app.Aliases.Add("powershell");
        }
        else if (n.Contains("visual studio code") || n.Contains("vscode") || n.Contains("vs code"))
        {
            if (!app.Aliases.Contains("vscode")) app.Aliases.Add("vscode");
            if (!app.Aliases.Contains("vs code")) app.Aliases.Add("vs code");
            if (!app.Aliases.Contains("code")) app.Aliases.Add("code");
        }
        else if (n.Contains("spotify") && !app.Aliases.Contains("spotify"))
        {
            app.Aliases.Add("spotify");
        }
        else if (n.Contains("explorer") && !app.Aliases.Contains("files"))
        {
            app.Aliases.Add("files");
            app.Aliases.Add("file explorer");
        }
    }
}
