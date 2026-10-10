using WindowsDynamicBar.Core.Search;
using WindowsDynamicBar.Core.Media;
using System.IO;

namespace WindowsDynamicBar.Tests;

/// <summary>Search-bar relevance: ranking tiers, typo tolerance, junk filters and row composition.</summary>
internal static partial class Program
{
    private static void RunSearchRankingTests()
    {
        Run("Ranking: 'chr' -> Google Chrome first", TestRankChrome);
        Run("Ranking: 'vsc' -> Visual Studio Code (initials)", TestRankVsCodeInitials);
        Run("Ranking: 'code' -> Visual Studio Code (word prefix)", TestRankWordPrefix);
        Run("Ranking: 'set' -> Settings first", TestRankSettings);
        Run("Ranking: 'notpad' -> Notepad (one typo)", TestRankTypo);
        Run("Ranking: 'xqz' -> nothing but the web row", TestRankNoJunk);
        Run("Ranking: uninstallers / docs / helpers never appear", TestRankJunkHidden);
        Run("Ranking: duplicate shortcuts of one app collapse", TestRankDedupe);
        Run("Ranking: usage boosts reorder within a tier", TestRankUsageBoost);
        Run("Ranking: weak subsequences are dropped", TestRankWeakSubsequence);
        Run("Compose: max 6 rows, groups contiguous, top hit first", TestComposeRows);
        Run("Compose: weak matches cut below a strong top hit", TestComposeCutsNoise);
        Run("Compose: web row last and only as a fallback", TestComposeWebRow);
        Run("File filter: hidden app/dependency folders", TestFileFolderFilter);
        Run("Artwork filter: app logo (transparent corners) is not cover art", TestArtworkLogoFilter);
    }

    private static byte[] Png(int w, int h, bool transparentCorners)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool corner = transparentCorners && (x < 8 || x >= w - 8) && (y < 8 || y >= h - 8);
                px[i] = 200; px[i + 1] = 60; px[i + 2] = 40; px[i + 3] = corner ? (byte)0 : (byte)255;
            }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static void TestArtworkLogoFilter()
    {
        Assert(AppIconHelper.IsArtworkTinyOrAppIcon(Png(256, 256, true), null), "square logo with transparent corners rejected");
        Assert(!AppIconHelper.IsArtworkTinyOrAppIcon(Png(300, 300, false), null), "opaque square album art kept (Spotify)");
        Assert(!AppIconHelper.IsArtworkTinyOrAppIcon(Png(320, 180, false), null), "opaque 16:9 video thumbnail kept (YouTube)");
        Assert(AppIconHelper.IsArtworkTinyOrAppIcon(Png(32, 32, false), null), "tiny image rejected");
    }

    /// <summary>A Start menu like a real one: apps plus the usual junk shortcuts.</summary>
    private static List<AppInfo> Catalogue()
    {
        AppInfo App(string name, string target, int launches = 0)
        {
            var a = new AppInfo { Name = name, AppId = name, ExePath = name + ".lnk", TargetPath = target, LaunchCount = launches };
            AppDiscoveryService.AssignKnownAliases(a);
            return a;
        }
        return new List<AppInfo>
        {
            App("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
            App("Chromium", @"C:\Users\u\AppData\Local\Chromium\Application\chrome.exe"),
            App("Chrome Remote Desktop", @"C:\Program Files\Google\Chrome Remote Desktop\remoting_start_host.exe"),
            App("Visual Studio Code", @"C:\Users\u\AppData\Local\Programs\Microsoft VS Code\Code.exe"),
            App("Visual Studio 2022", @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"),
            App("Settings", "ms-settings:"),
            App("Set Program Access and Computer Defaults", @"C:\Windows\System32\ComputerDefaults.exe"),
            App("Notepad", @"C:\Windows\System32\notepad.exe"),
            App("Notepad++", @"C:\Program Files\Notepad++\notepad++.exe"),
            App("Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe"),
            App("File Explorer", @"C:\Windows\explorer.exe"),
            App("Spotify", @"C:\Users\u\AppData\Roaming\Spotify\Spotify.exe"),
            App("Steam", @"C:\Program Files (x86)\Steam\steam.exe"),
            // junk that must never surface
            App("Uninstall Steam", @"C:\Program Files (x86)\Steam\uninstall.exe"),
            App("Notepad++ Uninstaller", @"C:\Program Files\Notepad++\uninstall.exe"),
            App("Steam Support", @"C:\Program Files (x86)\Steam\unins000.exe"),
            App("Firefox Readme", @"C:\Program Files\Mozilla Firefox\readme.txt"),
            App("Python 3.12 Documentation", @"C:\Python312\Doc\python312.chm"),
            App("Release Notes", @"C:\Program Files\Foo\notes.html"),
            App("Chrome Setup", @"C:\Users\u\Downloads\ChromeSetup.exe"),
            App("Spotify Crash Helper", @"C:\Users\u\AppData\Roaming\Spotify\SpotifyCrashHelper.exe"),
            App("Google Chrome (2)", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),   // duplicate shortcut
        };
    }

    private static List<string> TopNames(string query, int max = 6) =>
        SearchRanker.RankApps(Catalogue(), query, DateTime.UtcNow, max).Select(r => r.App.Name).ToList();

    private static void TestRankChrome()
    {
        var names = TopNames("chr");
        AssertEq("Google Chrome", names.FirstOrDefault(), "top hit for 'chr'");
        Assert(names.Contains("Chromium"), "Chromium still listed");
    }

    private static void TestRankVsCodeInitials()
    {
        AssertEq("Visual Studio Code", TopNames("vsc").FirstOrDefault(), "top hit for 'vsc'");
        // Initials without any alias help.
        var m = SearchRanker.MatchName("Visual Studio Code", "vsc");
        AssertEq(MatchTier.Initials, m.Tier, "tier for 'vsc'");
        AssertEq(3, m.Indices!.Count, "highlights the three initials");
    }

    private static void TestRankWordPrefix()
    {
        var m = SearchRanker.MatchName("Visual Studio Code", "code");
        AssertEq(MatchTier.WordPrefix, m.Tier, "'code' is a word prefix");
        AssertEq("Visual Studio Code", TopNames("code").FirstOrDefault(), "top hit for 'code'");
        AssertEq("Firefox", TopNames("ff").FirstOrDefault(), "'ff' -> Firefox");
    }

    private static void TestRankSettings()
    {
        AssertEq("Settings", TopNames("set").FirstOrDefault(), "top hit for 'set'");
    }

    private static void TestRankTypo()
    {
        var names = TopNames("notpad");
        AssertEq("Notepad", names.FirstOrDefault(), "top hit for 'notpad'");
        AssertEq(MatchTier.Typo, SearchRanker.MatchName("Notepad", "notpad").Tier, "typo tier");
        Assert(!SearchRanker.MatchName("Notepad", "nxtpxd").Matched, "two typos do not match");
        AssertEq("Steam", TopNames("stem").FirstOrDefault(), "'stem' -> Steam (one missing letter)");
    }

    private static void TestRankNoJunk()
    {
        Assert(TopNames("xqz").Count == 0, "no app matches 'xqz'");
        var web = new SearchResult { Title = "Search the web for \"xqz\"", Subtitle = "", Category = SearchCategory.Web };
        var rows = SearchRanker.Compose(new List<SearchResult>(), new List<SearchResult>(), web, "xqz");
        AssertEq(1, rows.Count, "only one row");
        AssertEq(SearchCategory.Web, rows[0].Category, "the row is the web row");
    }

    private static void TestRankJunkHidden()
    {
        foreach (var q in new[] { "un", "uninstall", "steam", "notepad", "readme", "doc", "release", "setup", "helper", "spotify", "chrome" })
        {
            foreach (var name in TopNames(q, 20))
            {
                string n = name.ToLowerInvariant();
                Assert(!n.Contains("uninstall") && !n.Contains("readme") && !n.Contains("documentation") &&
                       !n.Contains("release notes") && !n.Contains("setup") && !n.Contains("helper") && name != "Steam Support",
                    $"junk '{name}' surfaced for '{q}'");
            }
        }
        Assert(AppFilters.IsJunk("Uninstall Foo", null), "name: uninstall");
        Assert(AppFilters.IsJunk("Foo", @"C:\Foo\unins000.exe"), "target: unins000");
        Assert(AppFilters.IsJunk("Foo Manual", @"C:\Foo\manual.pdf"), "target: pdf");
        Assert(!AppFilters.IsJunk("HelpDesk Pro", @"C:\HelpDesk\hd.exe"), "whole words only");
    }

    private static void TestRankDedupe()
    {
        var names = TopNames("google chrome", 10);
        AssertEq(1, names.Count(n => n.StartsWith("Google Chrome")), "one row per target exe");
    }

    private static void TestRankUsageBoost()
    {
        var apps = Catalogue();
        apps.First(a => a.Name == "Chrome Remote Desktop").LaunchCount = 0;
        var chromium = apps.First(a => a.Name == "Chromium");
        chromium.LaunchCount = 40;
        chromium.LastUsedUtc = DateTime.UtcNow.AddHours(-1);
        var top = SearchRanker.RankApps(apps, "chr", DateTime.UtcNow).First().App.Name;
        AssertEq("Chromium", top, "a heavily used app wins inside the same tier");
        // Boost is capped: frequency never lifts a substring hit over an exact name.
        Assert(SearchRanker.UsageBoost(100000, DateTime.UtcNow, DateTime.UtcNow) <= 90, "boost capped");
        Assert(SearchRanker.UsageBoost(0, DateTime.UtcNow.AddDays(-60), DateTime.UtcNow) < 2, "old use decays");
    }

    private static void TestRankWeakSubsequence()
    {
        Assert(!SearchRanker.MatchName("Steam", "sm").Matched, "2-char subsequence not allowed");
        Assert(!SearchRanker.MatchName("Chrome Remote Desktop", "cme").Matched, "scattered letters dropped");
        Assert(SearchRanker.MatchName("Chrome Remote Desktop", "crd").Matched, "word starts allowed");
    }

    private static SearchResult Row(string title, SearchCategory c, int score) =>
        new() { Title = title, Subtitle = "", Category = c, Score = score };

    private static void TestComposeRows()
    {
        var scored = new List<SearchResult>
        {
            Row("file a", SearchCategory.Files, 950),
            Row("app a", SearchCategory.Apps, 990),
            Row("win a", SearchCategory.Windows, 900),
            Row("app b", SearchCategory.Apps, 880),
            Row("file b", SearchCategory.Files, 860),
            Row("cmd a", SearchCategory.Commands, 840),
            Row("win b", SearchCategory.Windows, 820),
            Row("app c", SearchCategory.Apps, 800),
        };
        var calc = Row("4", SearchCategory.Calculator, 0);
        var rows = SearchRanker.Compose(new[] { calc }, scored, null, "2+2");
        AssertEq(6, rows.Count, "max 6 rows");
        AssertEq("4", rows[0].Title, "calculator first");
        AssertEq("app a", rows[1].Title, "then the top hit");
        // groups stay contiguous: Apps, Files, Windows
        var cats = rows.Skip(1).Select(r => r.Category).ToList();
        for (int i = 2; i < cats.Count; i++)
            Assert(cats[i] == cats[i - 1] || !cats.Take(i - 1).Contains(cats[i]), "groups contiguous");
    }

    private static void TestComposeCutsNoise()
    {
        // "set": a prefix hit (Settings) must not be padded with scattered subsequence matches.
        var apps = Catalogue();
        apps.Add(new AppInfo { Name = "Blackmagic RAW Speed Test", AppId = "b", ExePath = "b.lnk", TargetPath = @"C:\b\speed.exe" });
        apps.Add(new AppInfo { Name = "Send to OneNote", AppId = "s", ExePath = "s.lnk", TargetPath = @"C:\o\onenote.exe" });
        Assert(!SearchRanker.MatchName("Blackmagic RAW Speed Test", "set").Matched, "scattered 'S..e..T' is not a match");
        var rows = SearchRanker.RankApps(apps, "set", DateTime.UtcNow)
            .Select(r => new SearchResult { Title = r.App.Name, Subtitle = "", Category = SearchCategory.Apps, Score = r.Score })
            .ToList();
        var shown = SearchRanker.Compose(Array.Empty<SearchResult>(), rows, null, "set").Select(r => r.Title).ToList();
        AssertEq("Settings", shown.FirstOrDefault(), "Settings first");
        Assert(!shown.Contains("Send to OneNote"), "weak subsequence cut below a prefix hit");
    }

    private static void TestComposeWebRow()
    {
        var web = Row("web", SearchCategory.Web, 0);
        var many = Enumerable.Range(0, 5).Select(i => Row("a" + i, SearchCategory.Apps, 900 - i)).ToList();
        Assert(!SearchRanker.Compose(Array.Empty<SearchResult>(), many, web, "spot").Contains(web), "no web row with many results");
        var few = many.Take(2).ToList();
        var rows = SearchRanker.Compose(Array.Empty<SearchResult>(), few, web, "spot");
        AssertEq(web, rows.Last(), "web row last when < 3 results");
        var q = SearchRanker.Compose(Array.Empty<SearchResult>(), many, web, "how to install python");
        AssertEq(web, q.Last(), "question gets the web row");
        AssertEq(6, q.Count, "still max 6 rows");
        Assert(SearchRanker.LooksLikeUrl("github.com"), "url");
        Assert(SearchRanker.LooksLikeUrl("https://example.org/x"), "url with scheme");
        Assert(!SearchRanker.LooksLikeUrl("notes.txt"), "file names are not urls");
        Assert(!SearchRanker.LooksLikeUrl("visual studio"), "words are not urls");
        Assert(SearchRanker.LooksLikeQuestion("what is wpf"), "question word");
        Assert(SearchRanker.LooksLikeQuestion("wpf clip?"), "question mark");
        Assert(!SearchRanker.LooksLikeQuestion("chrome"), "not a question");
    }

    private static void TestFileFolderFilter()
    {
        Assert(AppFilters.IsExcludedFilePath(@"C:\Users\u\AppData\Local\x\a.txt"), "AppData");
        Assert(AppFilters.IsExcludedFilePath(@"C:\Program Files\x\a.dll"), "Program Files");
        Assert(AppFilters.IsExcludedFilePath(@"D:\src\app\node_modules\left-pad\index.js"), "node_modules");
        Assert(AppFilters.IsExcludedFilePath(@"D:\src\app\.git\HEAD"), ".git");
        Assert(AppFilters.IsExcludedFilePath(@"C:\Users\u\proj\.venv\Lib\site-packages\tzdata\Christmas"), "venv");
        Assert(!AppFilters.IsExcludedFilePath(@"C:\Users\u\Documents\Chromium notes.docx"), "Documents allowed");
    }
}
