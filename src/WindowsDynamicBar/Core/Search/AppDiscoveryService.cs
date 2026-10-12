using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Media;

namespace WindowsDynamicBar.Core.Search;

/// <summary>
/// Start-menu app index for the search bar. Discovery (directory walk, shortcut resolution, icon
/// extraction) only ever runs on a background thread; <see cref="GetApps"/> returns the in-memory
/// snapshot and never blocks. The bar pre-loads it at startup, so keystrokes only rank memory.
/// </summary>
public sealed class AppDiscoveryService
{
    private static readonly Lazy<AppDiscoveryService> Lazy = new(() => new AppDiscoveryService());
    public static AppDiscoveryService Instance => Lazy.Value;

    private readonly object _sync = new();
    private List<AppInfo> _apps = new();
    private Dictionary<string, int> _launchCounts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateTime> _lastUsed = new(StringComparer.OrdinalIgnoreCase);
    private int _refreshing;   // 1 while a background discovery runs

    private static readonly string StatsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsDynamicBar");
    private readonly string _statsPath = Path.Combine(StatsDir, "search_stats.json");
    private readonly string _recentPath = Path.Combine(StatsDir, "search_recent.json");

    /// <summary>True once the first background discovery has finished.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Raised (worker thread) after the first discovery finishes.</summary>
    public event Action? Loaded;

    private AppDiscoveryService()
    {
        LoadStats();
    }

    /// <summary>Snapshot of the index (empty until the first background load completes).</summary>
    public IReadOnlyList<AppInfo> GetApps()
    {
        lock (_sync) return _apps;
    }

    /// <summary>Re-scan on a worker thread. A scan already in flight is not duplicated.</summary>
    public void RefreshInBackground()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                var discovered = DiscoverAppsInternal();
                bool first;
                lock (_sync)
                {
                    _apps = discovered;
                    first = !IsLoaded;
                    IsLoaded = true;
                }
                if (first) Loaded?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Warning("App discovery failed", ("error", ex.Message));
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        });
    }

    public void RecordLaunch(string appIdOrName)
    {
        if (string.IsNullOrWhiteSpace(appIdOrName)) return;
        lock (_sync)
        {
            _launchCounts[appIdOrName] = _launchCounts.GetValueOrDefault(appIdOrName, 0) + 1;
            _lastUsed[appIdOrName] = DateTime.UtcNow;
            var app = _apps.FirstOrDefault(a => a.AppId.Equals(appIdOrName, StringComparison.OrdinalIgnoreCase) ||
                                                a.Name.Equals(appIdOrName, StringComparison.OrdinalIgnoreCase));
            if (app is not null)
            {
                app.LaunchCount = _launchCounts[appIdOrName];
                app.LastUsedUtc = _lastUsed[appIdOrName];
            }
            SaveStats();
        }
    }

    /// <summary>"Clear all data": forget launch counts and recency (memory and disk).</summary>
    public void ClearUsage()
    {
        lock (_sync)
        {
            _launchCounts.Clear();
            _lastUsed.Clear();
            foreach (var app in _apps) { app.LaunchCount = 0; app.LastUsedUtc = null; }
            foreach (var path in new[] { _statsPath, _recentPath })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning("Search history not deleted", ("error", ex.Message));
                }
            }
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
        _launchCounts = ReadJson<int>(_statsPath);
        _lastUsed = ReadJson<DateTime>(_recentPath);
    }

    private static Dictionary<string, T> ReadJson<T>(string path)
    {
        var empty = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return empty;
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(path));
            return dict is null ? empty : new Dictionary<string, T>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Debug("Search stats unreadable; starting fresh", ("path", path), ("error", ex.Message));
            return empty;
        }
    }

    private void SaveStats()
    {
        try
        {
            Directory.CreateDirectory(StatsDir);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_statsPath, JsonSerializer.Serialize(_launchCounts, opts));
            File.WriteAllText(_recentPath, JsonSerializer.Serialize(_lastUsed, opts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Search stats not saved", ("error", ex.Message));
        }
    }

    private List<AppInfo> DiscoverAppsInternal()
    {
        var result = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);

        // 1. Add well-known core applications first
        AddKnownCoreApps(result);

        // 2. Scan Start Menu programs
        ScanStartMenuDirectory(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), result);
        ScanStartMenuDirectory(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), result);

        // Apply launch counts, last-used times & aliases
        foreach (var app in result.Values)
        {
            if (_launchCounts.TryGetValue(app.AppId, out int cnt) || _launchCounts.TryGetValue(app.Name, out cnt))
                app.LaunchCount = cnt;
            if (_lastUsed.TryGetValue(app.AppId, out var lu) || _lastUsed.TryGetValue(app.Name, out lu))
                app.LastUsedUtc = lu;
            AssignKnownAliases(app);
        }

        // Duplicate shortcuts of one app (same target exe): keep the most used, then the shortest name.
        return result.Values
            .GroupBy(a => string.IsNullOrEmpty(a.TargetPath) ? a.ExePath : a.TargetPath!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(a => a.LaunchCount).ThenBy(a => a.Name.Length).First())
            .OrderByDescending(a => a.LaunchCount).ThenBy(a => a.Name)
            .ToList();
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
                TargetPath = exe,
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

        // Windows Terminal when installed (wt.exe app-execution alias), else Windows PowerShell.
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe");
        if (File.Exists(wt))
            Add("Terminal", wt, "terminal", new[] { "term", "windows terminal", "wt" });
        else
            Add("Windows PowerShell", pwsh, "terminal", new[] { "term", "powershell", "terminal" });

        // Settings is a packaged app (no Start-menu .lnk); its URI is the launch target.
        Add("Settings", "ms-settings:", "ms-settings", new[] { "windows settings" });

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
        string progDir = Path.Combine(basePath, "Programs");
        if (!Directory.Exists(progDir)) progDir = basePath;
        if (!Directory.Exists(progDir)) return;

        string[] lnkFiles;
        try { lnkFiles = Directory.GetFiles(progDir, "*.lnk", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Start menu scan failed", ("dir", progDir), ("error", ex.Message));
            return;
        }

        foreach (var lnk in lnkFiles)
        {
            string name = Path.GetFileNameWithoutExtension(lnk);
            if (result.ContainsKey(name)) continue;
            string? target = ShellLink.ResolveTarget(lnk);
            if (AppFilters.IsJunk(name, target)) continue;

            var info = new AppInfo
            {
                Name = name,
                ExePath = lnk,
                TargetPath = target,
                AppId = lnk,
                Icon = AppIconHelper.ExtractIconFromFile(lnk),
            };
            AssignKnownAliases(info);
            result[name] = info;
        }
    }

    /// <summary>Common short names launchers accept ("ff" for Firefox, "code" for VS Code).</summary>
    public static void AssignKnownAliases(AppInfo app)
    {
        void Alias(params string[] names)
        {
            foreach (var n in names)
                if (!app.Aliases.Contains(n)) app.Aliases.Add(n);
        }

        string n = app.Name.ToLowerInvariant();
        if (n.Contains("chrome")) Alias("chrome", "google chrome");
        else if (n.Contains("firefox")) Alias("ff", "firefox");
        else if (n.Contains("microsoft edge")) Alias("edge");
        else if (n.Contains("command prompt")) Alias("cmd");
        else if (n.Contains("terminal") || n.Contains("powershell")) Alias("term");
        else if (n.Contains("visual studio code") || n.Contains("vscode") || n.Contains("vs code")) Alias("vscode", "vs code", "code");
        else if (n.Contains("spotify")) Alias("spotify");
        else if (n.Contains("explorer")) Alias("files", "file explorer");
    }
}

/// <summary>Resolves a .lnk to its target path via the shell's IShellLink (worker thread).</summary>
internal static class ShellLink
{
    public static string? ResolveTarget(string lnkPath)
    {
        object? obj = null;
        try
        {
            obj = new CShellLink();
            ((IPersistFile)obj).Load(lnkPath, 0 /* STGM_READ */);
            var sb = new StringBuilder(520);
            ((IShellLinkW)obj).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            string target = sb.ToString();
            return target.Length > 0 ? target : null;   // MSI-advertised shortcuts have no plain path
        }
        catch (COMException ex)
        {
            Log.Debug("Shortcut target unresolved", ("lnk", lnkPath), ("hr", ex.HResult));
            return null;
        }
        finally
        {
            if (obj is not null) Marshal.ReleaseComObject(obj);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    }
}
