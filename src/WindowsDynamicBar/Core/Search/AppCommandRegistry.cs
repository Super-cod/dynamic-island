using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Core.Search;

public static class AppCommandRegistry
{
    private static readonly string TermHistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WindowsDynamicBar", "terminal_history.json");

    private static readonly HashSet<string> RiskyCommandVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "del", "erase", "rmdir", "rd", "format", "shutdown", "diskpart"
    };

    public static List<SearchResult> GetScopedCommands(AppInfo app, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<SearchResult>();

        string appKey = app.AppId.ToLowerInvariant();
        if (appKey == "chrome" || app.Name.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
            return GetChromeCommands(app, query);

        if (appKey == "terminal" || app.Name.Contains("Terminal", StringComparison.OrdinalIgnoreCase) ||
            app.Name.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) || app.Name.Contains("Command Prompt", StringComparison.OrdinalIgnoreCase))
            return GetTerminalCommands(app, query);

        if (appKey == "vscode" || app.Name.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase) || app.Name.Contains("Code", StringComparison.OrdinalIgnoreCase))
            return GetVsCodeCommands(app, query);

        if (appKey == "spotify" || app.Name.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
            return GetSpotifyCommands(app, query);

        if (appKey == "explorer" || app.Name.Contains("Explorer", StringComparison.OrdinalIgnoreCase) || app.Name.Contains("Files", StringComparison.OrdinalIgnoreCase))
            return GetExplorerCommands(app, query);

        return GetGenericAppCommands(app, query);
    }

    // ------------------- Chrome -------------------
    private static List<SearchResult> GetChromeCommands(AppInfo app, string query)
    {
        var list = new List<SearchResult>();
        string q = query.Trim();

        // 1. Direct URL check
        if (Regex.IsMatch(q, @"^(?:https?:\/\/)?(?:[a-zA-Z0-9-]+\.)+[a-zA-Z]{2,}(?:\/.*)?$", RegexOptions.IgnoreCase))
        {
            string url = q.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || q.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? q : "https://" + q;
            list.Add(new SearchResult
            {
                Title = $"Open {q}",
                Subtitle = url,
                Category = SearchCategory.Commands,
                Icon = app.Icon,
                KeyboardHint = "Enter",
                Action = () => LaunchUrl(url)
            });
        }

        // 2. Known site prefixes
        var siteMap = new (string[] Prefixes, string SiteName, string BaseUrl, string SearchUrl)[]
        {
            (new[] { "yt", "youtube" }, "YouTube", "https://www.youtube.com", "https://www.youtube.com/results?search_query={0}"),
            (new[] { "gh", "github" }, "GitHub", "https://github.com", "https://github.com/search?q={0}"),
            (new[] { "gmail" }, "Gmail", "https://mail.google.com", "https://mail.google.com/mail/u/0/#search/{0}"),
            (new[] { "maps" }, "Google Maps", "https://maps.google.com", "https://www.google.com/maps/search/{0}"),
            (new[] { "reddit" }, "Reddit", "https://www.reddit.com", "https://www.reddit.com/search/?q={0}"),
            (new[] { "wiki", "wikipedia" }, "Wikipedia", "https://en.wikipedia.org", "https://en.wikipedia.org/wiki/Special:Search?search={0}"),
        };

        foreach (var (prefixes, siteName, baseUrl, searchUrl) in siteMap)
        {
            foreach (var p in prefixes)
            {
                if (q.Equals(p, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new SearchResult
                    {
                        Title = $"Open {siteName}",
                        Subtitle = baseUrl,
                        Category = SearchCategory.Commands,
                        Icon = app.Icon,
                        KeyboardHint = "Enter",
                        Action = () => LaunchUrl(baseUrl)
                    });
                    break;
                }
                else if (q.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                {
                    string sub = q[(p.Length + 1)..].Trim();
                    string target = string.Format(searchUrl, Uri.EscapeDataString(sub));
                    list.Add(new SearchResult
                    {
                        Title = $"Search {siteName} for '{sub}'",
                        Subtitle = target,
                        Category = SearchCategory.Commands,
                        Icon = app.Icon,
                        KeyboardHint = "Enter",
                        Action = () => LaunchUrl(target)
                    });
                    break;
                }
            }
        }

        // 3. Fallback: Search Google
        string googleSearch = "https://www.google.com/search?q=" + Uri.EscapeDataString(q);
        list.Add(new SearchResult
        {
            Title = $"Search Google for '{q}'",
            Subtitle = googleSearch,
            Category = SearchCategory.Commands,
            Icon = app.Icon,
            KeyboardHint = "Enter",
            Action = () => LaunchUrl(googleSearch)
        });

        return list;
    }

    private static void LaunchUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ------------------- Terminal -------------------
    private static List<SearchResult> GetTerminalCommands(AppInfo app, string query)
    {
        var list = new List<SearchResult>();
        string q = query.Trim();

        bool isRisky = CheckIfRisky(q);

        list.Add(new SearchResult
        {
            Title = $"Run '{q}'",
            Subtitle = "PowerShell in user directory",
            Category = SearchCategory.Commands,
            Icon = app.Icon,
            IsRisky = isRisky,
            KeyboardHint = isRisky ? "Enter again to confirm" : "Enter",
            Action = () => RunTerminalCommand(q)
        });

        // Search Terminal History
        var history = LoadTerminalHistory();
        foreach (var cmd in history.Where(c => c.Contains(q, StringComparison.OrdinalIgnoreCase) && !c.Equals(q, StringComparison.OrdinalIgnoreCase)).Take(3))
        {
            bool hRisky = CheckIfRisky(cmd);
            list.Add(new SearchResult
            {
                Title = cmd,
                Subtitle = "History",
                Category = SearchCategory.Commands,
                Icon = app.Icon,
                IsRisky = hRisky,
                KeyboardHint = hRisky ? "Enter again to confirm" : "Enter",
                Action = () => RunTerminalCommand(cmd)
            });
        }

        return list;
    }

    private static bool CheckIfRisky(string cmd)
    {
        string[] parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && RiskyCommandVerbs.Contains(parts[0])) return true;
        if (cmd.Contains("sudo", StringComparison.OrdinalIgnoreCase) || cmd.Contains("-Force", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void RunTerminalCommand(string cmd)
    {
        try
        {
            SaveTerminalCommand(cmd);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -Command \"{cmd}\"",
                WorkingDirectory = home,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Debug("Terminal run failed", ("error", ex.Message));
        }
    }

    private static List<string> LoadTerminalHistory()
    {
        var result = new List<string>();
        try
        {
            // 1. Bar's saved commands
            if (File.Exists(TermHistoryPath))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(TermHistoryPath));
                if (list is not null) result.AddRange(list);
            }

            // 2. PowerShell PSReadLine history
            string psHistory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt");
            if (File.Exists(psHistory))
            {
                var lines = File.ReadLines(psHistory).Reverse().Take(50);
                foreach (var l in lines)
                {
                    string trimmed = l.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !result.Contains(trimmed))
                        result.Add(trimmed);
                }
            }
        }
        catch { }
        return result;
    }

    private static void SaveTerminalCommand(string cmd)
    {
        try
        {
            var history = new List<string>();
            if (File.Exists(TermHistoryPath))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(TermHistoryPath));
                if (list is not null) history = list;
            }
            history.RemoveAll(c => c.Equals(cmd, StringComparison.OrdinalIgnoreCase));
            history.Insert(0, cmd);
            while (history.Count > 4) history.RemoveAt(history.Count - 1);

            string dir = Path.GetDirectoryName(TermHistoryPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(TermHistoryPath, JsonSerializer.Serialize(history));
        }
        catch { }
    }

    // ------------------- VS Code -------------------
    private static List<SearchResult> GetVsCodeCommands(AppInfo app, string query)
    {
        var list = new List<SearchResult>();
        string q = query.Trim();

        if ("new window".StartsWith(q, StringComparison.OrdinalIgnoreCase) || q.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new SearchResult
            {
                Title = "New Window",
                Subtitle = "code --new-window",
                Category = SearchCategory.Commands,
                Icon = app.Icon,
                KeyboardHint = "Enter",
                Action = () => LaunchVsCode("-n")
            });
        }

        // Search recent workspaces / projects folders
        var dirs = GetRecentWorkspacesAndFolders();
        foreach (var d in dirs)
        {
            string dirName = Path.GetFileName(d);
            if (dirName.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                string targetDir = d;
                list.Add(new SearchResult
                {
                    Title = dirName,
                    Subtitle = d,
                    Category = SearchCategory.Commands,
                    Icon = app.Icon,
                    KeyboardHint = "Enter",
                    Action = () => LaunchVsCode($"\"{targetDir}\"")
                });
            }
        }

        if (list.Count == 0)
        {
            list.Add(new SearchResult
            {
                Title = $"Open project '{q}'",
                Subtitle = "Open in VS Code",
                Category = SearchCategory.Commands,
                Icon = app.Icon,
                KeyboardHint = "Enter",
                Action = () => LaunchVsCode($"\"{q}\"")
            });
        }

        return list;
    }

    private static void LaunchVsCode(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c code {args}",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }

    private static List<string> GetRecentWorkspacesAndFolders()
    {
        var list = new List<string>();
        try
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (Directory.Exists(desktop)) list.AddRange(Directory.GetDirectories(desktop).Take(15));
            if (Directory.Exists(docs)) list.AddRange(Directory.GetDirectories(docs).Take(15));
        }
        catch { }
        return list;
    }

    // ------------------- Spotify -------------------
    private static List<SearchResult> GetSpotifyCommands(AppInfo app, string query)
    {
        var list = new List<SearchResult>();
        string q = query.Trim();

        list.Add(new SearchResult
        {
            Title = $"Search Spotify for '{q}'",
            Subtitle = $"spotify:search:{q}",
            Category = SearchCategory.Commands,
            Icon = app.Icon,
            KeyboardHint = "Enter",
            Action = () =>
            {
                try { Process.Start(new ProcessStartInfo($"spotify:search:{Uri.EscapeDataString(q)}") { UseShellExecute = true }); } catch { }
            }
        });

        if ("play".StartsWith(q, StringComparison.OrdinalIgnoreCase) || "pause".StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new SearchResult
            {
                Title = "Play / Pause",
                Subtitle = "Media Control",
                Category = SearchCategory.Commands,
                Icon = app.Icon,
                KeyboardHint = "Enter",
                Action = () => { _ = App.Media?.PlayPauseAsync(); }
            });
        }
        else if ("next".StartsWith(q, StringComparison.OrdinalIgnoreCase) || "skip".StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new SearchResult
            {
                Title = "Next Track",
                Subtitle = "Media Control",
                Category = SearchCategory.Commands,
                Icon = app.Icon,
                KeyboardHint = "Enter",
                Action = () => { _ = App.Media?.NextAsync(); }
            });
        }

        return list;
    }

    // ------------------- Explorer -------------------
    private static List<SearchResult> GetExplorerCommands(AppInfo app, string query)
    {
        var list = new List<SearchResult>();
        string q = query.Trim();

        // 1. Direct path check
        if (Path.IsPathRooted(q) && (Directory.Exists(q) || File.Exists(q)))
        {
            list.Add(new SearchResult
            {
                Title = $"Open {q}",
                Subtitle = q,
                Category = SearchCategory.Commands,
                Icon = app.Icon,
                KeyboardHint = "Enter",
                Action = () => { try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{q}\"") { UseShellExecute = true }); } catch { } }
            });
            return list;
        }

        // 2. Known folders matching query
        var known = new (string Name, string Path)[]
        {
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            ("Downloads", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        };

        foreach (var (name, path) in known)
        {
            if (name.Contains(q, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
            {
                string target = path;
                list.Add(new SearchResult
                {
                    Title = name,
                    Subtitle = path,
                    Category = SearchCategory.Commands,
                    Icon = app.Icon,
                    KeyboardHint = "Enter",
                    Action = () => { try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true }); } catch { } }
                });
            }
        }

        return list;
    }

    // ------------------- Generic Apps -------------------
    private static List<SearchResult> GetGenericAppCommands(AppInfo app, string query)
    {
        var list = new List<SearchResult>();
        string q = query.Trim();

        list.Add(new SearchResult
        {
            Title = $"Open {app.Name} with '{q}'",
            Subtitle = app.ExePath,
            Category = SearchCategory.Commands,
            Icon = app.Icon,
            KeyboardHint = "Enter",
            Action = () =>
            {
                try
                {
                    AppDiscoveryService.Instance.RecordLaunch(app.AppId);
                    Process.Start(new ProcessStartInfo(app.ExePath, $"\"{q}\"") { UseShellExecute = true });
                }
                catch { }
            }
        });

        return list;
    }
}
