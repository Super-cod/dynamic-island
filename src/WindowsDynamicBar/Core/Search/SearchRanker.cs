using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowsDynamicBar.Core.Search;

/// <summary>How a name matched the typed text, best first.</summary>
public enum MatchTier
{
    None = 0,
    Subsequence = 1,
    Typo = 2,
    Substring = 3,
    Initials = 4,
    WordPrefix = 5,
    Prefix = 6,
    Exact = 7,
}

public readonly record struct NameMatch(MatchTier Tier, int Score, List<int>? Indices)
{
    public bool Matched => Tier != MatchTier.None;
    public static readonly NameMatch None = new(MatchTier.None, 0, null);
}

/// <summary>
/// Search-bar relevance. Pure functions (no UI, no I/O) so the ranking is unit tested.
/// Tier order: exact &gt; prefix &gt; word prefix ("code" -&gt; Visual Studio Code) &gt; initials
/// ("vsc") &gt; substring &gt; one-typo word ("notpad") &gt; strong in-order subsequence (3+ chars).
/// Within a tier, shorter names (higher coverage) and usage boosts decide.
/// </summary>
public static class SearchRanker
{
    public const int MaxRows = 6;

    // Category bias: apps win over files when the name scores are close.
    public const int AppBias = 30;
    public const int CommandBias = 15;
    public const int WindowBias = 0;
    public const int FileBias = -25;

    /// <summary>Rows scoring this far below the top hit are noise (a prefix hit vs a scattered subsequence).</summary>
    public const int RelativeCutoff = 350;

    private static int TierBase(MatchTier t) => t switch
    {
        MatchTier.Exact => 1000,
        MatchTier.Prefix => 900,
        MatchTier.WordPrefix => 800,
        MatchTier.Initials => 700,
        MatchTier.Substring => 600,
        MatchTier.Typo => 450,
        MatchTier.Subsequence => 250,
        _ => 0,
    };

    /// <summary>Score <paramref name="name"/> against the typed text. Indices are into <paramref name="name"/>.</summary>
    public static NameMatch MatchName(string name, string query)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(query)) return NameMatch.None;
        string q = query.Trim().ToLowerInvariant();
        string t = name.ToLowerInvariant();
        if (q.Length == 0) return NameMatch.None;

        int coverage = 40 * Math.Min(q.Length, t.Length) / Math.Max(1, t.Length);

        if (t == q) return new(MatchTier.Exact, TierBase(MatchTier.Exact) + coverage, Range(0, q.Length));
        if (t.StartsWith(q, StringComparison.Ordinal))
            return new(MatchTier.Prefix, TierBase(MatchTier.Prefix) + coverage, Range(0, q.Length));

        var words = Words(name);

        // A word starts with the text ("code" -> "Visual Studio Code"); earlier words rank a bit higher.
        for (int w = 0; w < words.Count; w++)
        {
            var (start, len) = words[w];
            if (len >= q.Length && string.CompareOrdinal(t, start, q, 0, q.Length) == 0)
                return new(MatchTier.WordPrefix, TierBase(MatchTier.WordPrefix) + coverage - Math.Min(w, 5) * 4,
                    Range(start, q.Length));
        }

        // Initials: "vsc" -> Visual Studio Code (must use consecutive words from the first one).
        if (q.Length >= 2 && words.Count >= q.Length)
        {
            bool ok = true;
            for (int i = 0; i < q.Length && ok; i++) ok = t[words[i].Start] == q[i];
            if (ok)
                return new(MatchTier.Initials, TierBase(MatchTier.Initials) + coverage,
                    words.Take(q.Length).Select(x => x.Start).ToList());
        }

        // Substring inside a word (2+ chars, so a single letter cannot match everything).
        if (q.Length >= 2)
        {
            int idx = t.IndexOf(q, StringComparison.Ordinal);
            if (idx >= 0)
                return new(MatchTier.Substring, TierBase(MatchTier.Substring) + coverage - Math.Min(idx, 20),
                    Range(idx, q.Length));
        }

        // One typo in a word of 4+ letters ("notpad" -> Notepad, "chorme" -> Chrome).
        if (q.Length >= 4 && !q.Contains(' '))
        {
            foreach (var (start, len) in words)
            {
                if (len < 4 || t[start] != q[0]) continue;
                string word = t.Substring(start, len);
                bool hit = EditDistanceAtMostOne(q, word);
                // Typo while still typing the word: compare with the word's prefix of similar length.
                for (int pl = q.Length - 1; !hit && pl <= q.Length + 1; pl++)
                    if (pl >= 4 && pl < len) hit = EditDistanceAtMostOne(q, word[..pl]);
                if (hit)
                    return new(MatchTier.Typo, TierBase(MatchTier.Typo) + coverage, Range(start, len));
            }
        }

        // In-order subsequence: only 3+ chars and only strong ones (mostly word starts / runs).
        if (q.Length >= 3)
        {
            var sub = Subsequence(t, q, words);
            if (sub is { } s) return s;
        }

        return NameMatch.None;
    }

    /// <summary>Best of the name and its aliases (well-known short names). Alias hits carry no highlight indices;
    /// their coverage uses the alias length, so "chr" prefers Chrome (alias "chrome") over Chromium.</summary>
    public static NameMatch MatchBest(string name, IReadOnlyList<string>? aliases, string query)
    {
        var best = MatchName(name, query);
        if (aliases is null) return best;
        foreach (var alias in aliases)
        {
            var m = MatchName(alias, query);
            if (!m.Matched) continue;
            if (m.Score > best.Score) best = new NameMatch(m.Tier, m.Score, null);
        }
        return best;
    }

    /// <summary>Frequency + recency boost, capped so it reorders within a tier but rarely across tiers.</summary>
    public static int UsageBoost(int launchCount, DateTime? lastUsedUtc, DateTime nowUtc)
    {
        double freq = launchCount > 0 ? 14 * Math.Log2(1 + launchCount) : 0;
        double recent = 0;
        if (lastUsedUtc is { } lu)
        {
            double days = Math.Max(0, (nowUtc - lu).TotalDays);
            recent = 35 * Math.Exp(-days / 7.0);   // ~half after 5 days
        }
        return (int)Math.Min(90, freq + recent);
    }

    /// <summary>Rank apps for the typed text: filtered, deduped by target, best first.</summary>
    public static List<(AppInfo App, int Score, NameMatch Match)> RankApps(
        IReadOnlyList<AppInfo> apps, string query, DateTime nowUtc, int max = MaxRows)
    {
        var ranked = new List<(AppInfo, int, NameMatch)>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (AppFilters.IsJunk(app.Name, app.TargetPath)) continue;
            var m = MatchBest(app.Name, app.Aliases, query);
            if (!m.Matched) continue;
            int score = m.Score + UsageBoost(app.LaunchCount, app.LastUsedUtc, nowUtc) + AppBias;
            ranked.Add((app, score, m));
        }
        ranked.Sort((a, b) =>
        {
            int c = b.Item2.CompareTo(a.Item2);
            return c != 0 ? c : a.Item1.Name.Length.CompareTo(b.Item1.Name.Length);
        });

        var result = new List<(AppInfo, int, NameMatch)>(max);
        foreach (var r in ranked)
        {
            string key = string.IsNullOrEmpty(r.Item1.TargetPath) ? r.Item1.ExePath : r.Item1.TargetPath!;
            if (!seenTargets.Add(key)) continue;   // duplicate shortcut of the same app
            result.Add(r);
            if (result.Count == max) break;
        }
        return result;
    }

    public static bool LooksLikeUrl(string query)
    {
        string q = query.Trim();
        if (q.Length < 4 || q.Contains(' ')) return false;
        if (q.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            q.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            q.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return true;
        int dot = q.LastIndexOf('.');
        if (dot <= 0 || dot >= q.Length - 2) return false;
        string tld = q[(dot + 1)..].Split('/')[0];
        return tld.Length is >= 2 and <= 6 && tld.All(char.IsLetter) && !q.Contains('\\') && !FileExtensions.Contains(tld);
    }

    // "notes.txt" is a file name, not a link.
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "csv", "json", "xml", "log", "ini",
        "exe", "dll", "msi", "zip", "rar", "png", "jpg", "jpeg", "gif", "bmp", "svg", "mp3", "mp4", "mkv",
        "cs", "py", "js", "ts", "html", "htm", "css", "java", "cpp", "lnk", "bat", "ps1", "sln",
    };

    private static readonly string[] QuestionWords =
        { "what", "how", "why", "who", "when", "where", "which", "is", "are", "can", "does", "do", "should", "will" };

    public static bool LooksLikeQuestion(string query)
    {
        string q = query.Trim();
        if (q.EndsWith('?')) return true;
        int sp = q.IndexOf(' ');
        if (sp <= 0) return false;
        string first = q[..sp].ToLowerInvariant();
        return QuestionWords.Contains(first);
    }

    /// <summary>The web row is offered only as a fallback: few results, a question, or a URL.</summary>
    public static bool ShouldOfferWeb(int resultCount, string query) =>
        resultCount < 3 || LooksLikeQuestion(query) || LooksLikeUrl(query);

    /// <summary>
    /// Final row list: <paramref name="pinned"/> (calculator / timer) first, then the best of
    /// <paramref name="scored"/> (any category, by Score), grouped so each category stays
    /// contiguous in order of its best row; the web row last and only when <see cref="ShouldOfferWeb"/>.
    /// Rows far below the top hit (<see cref="RelativeCutoff"/>) are dropped, so a strong match is
    /// not padded with weak fuzzy noise. At most <see cref="MaxRows"/> rows.
    /// </summary>
    public static List<SearchResult> Compose(IReadOnlyList<SearchResult> pinned, IEnumerable<SearchResult> scored,
        SearchResult? web, string query)
    {
        var best = scored.OrderByDescending(r => r.Score).ToList();
        if (best.Count > 0)
        {
            int floor = best[0].Score - RelativeCutoff;
            best.RemoveAll(r => r.Score < floor);
        }
        int total = pinned.Count + best.Count;
        bool withWeb = web is not null && ShouldOfferWeb(total, query);
        int room = Math.Max(0, MaxRows - pinned.Count - (withWeb ? 1 : 0));
        var top = best.Take(room).ToList();

        // Keep groups contiguous (headers), ordered by each group's best row, so row 0 stays the top hit.
        var grouped = top.GroupBy(r => r.Category).SelectMany(g => g);

        var rows = new List<SearchResult>(MaxRows);
        rows.AddRange(pinned.Take(MaxRows));
        rows.AddRange(grouped);
        if (withWeb && rows.Count < MaxRows) rows.Add(web!);
        return rows;
    }

    // ------------------------------------------------------------------ helpers

    private static List<int> Range(int start, int count) => Enumerable.Range(start, count).ToList();

    /// <summary>Word spans: split on non-alphanumerics and lower-&gt;Upper / letter-&gt;digit changes.</summary>
    internal static List<(int Start, int Len)> Words(string s)
    {
        var words = new List<(int, int)>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && !char.IsLetterOrDigit(s[i])) i++;
            if (i >= s.Length) break;
            int start = i++;
            while (i < s.Length && char.IsLetterOrDigit(s[i]) &&
                   !(char.IsLower(s[i - 1]) && char.IsUpper(s[i])) &&
                   char.IsDigit(s[i]) == char.IsDigit(s[i - 1]))
                i++;
            words.Add((start, i - start));
        }
        return words;
    }

    private static NameMatch? Subsequence(string t, string q, List<(int Start, int Len)> words)
    {
        var starts = new HashSet<int>(words.Select(w => w.Start));
        var idx = new List<int>(q.Length);
        int ti = 0, points = 0, prev = -2;
        foreach (char c in q)
        {
            if (c == ' ') continue;
            // Prefer the next word start holding this char; fall back to the next occurrence.
            int found = -1;
            for (int j = ti; j < t.Length; j++)
                if (t[j] == c && (starts.Contains(j) || j == prev + 1)) { found = j; break; }
            if (found < 0) found = t.IndexOf(c, ti);
            if (found < 0) return null;
            points += starts.Contains(found) ? 10 : found == prev + 1 ? 6 : 1;
            idx.Add(found);
            prev = found;
            ti = found + 1;
        }
        if (idx.Count == 0 || !starts.Contains(idx[0])) return null;
        int maxPoints = 10 * idx.Count;
        if (points * 10 < maxPoints * 8) return null;   // weak / scattered: no random junk
        return new NameMatch(MatchTier.Subsequence, TierBase(MatchTier.Subsequence) + 150 * points / maxPoints, idx);
    }

    /// <summary>Levenshtein distance &lt;= 1 (one insert, delete or substitute).</summary>
    internal static bool EditDistanceAtMostOne(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length > b.Length) i++;
            else if (a.Length < b.Length) j++;
            else { i++; j++; }
        }
        edits += (a.Length - i) + (b.Length - j);
        return edits <= 1;
    }
}

/// <summary>Start-menu and file-result quality filters.</summary>
public static class AppFilters
{
    private static readonly string[] JunkNameParts =
    {
        "uninstall", "uninstaller", "readme", "read me", "documentation", "release notes", "release note",
        "changelog", "change log", "what's new", "license", "licence", "help", "manual", "user guide",
        "website", "web site", "home page", "homepage", "installer", "setup", "repair",
        "crash report", "helper", "updater", "check for updates",
    };

    private static readonly string[] JunkTargetNames =
    {
        "unins", "uninst", "setup", "install", "helper", "crashpad", "crashreport", "updater", "update.exe",
    };

    private static readonly string[] DocExtensions =
        { ".txt", ".pdf", ".htm", ".html", ".chm", ".url", ".rtf", ".md", ".doc", ".docx", ".xps", ".hlp" };

    /// <summary>True for uninstallers, docs, installers and helper executables.</summary>
    public static bool IsJunk(string name, string? targetPath)
    {
        string n = name.ToLowerInvariant();
        foreach (var part in JunkNameParts)
            if (ContainsWord(n, part)) return true;

        if (!string.IsNullOrEmpty(targetPath))
        {
            string ext = Path.GetExtension(targetPath).ToLowerInvariant();
            if (DocExtensions.Contains(ext)) return true;
            string file = Path.GetFileName(targetPath).ToLowerInvariant();
            foreach (var part in JunkTargetNames)
                if (file.Contains(part, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static readonly string[] ExcludedFolders =
    {
        @"\appdata\", @"\program files\", @"\program files (x86)\", @"\programdata\", @"\node_modules\",
        @"\.git\", @"\$recycle.bin\", @"\windows\winsxs\", @"\.venv\", @"\venv\", @"\site-packages\",
        @"\__pycache__\", @"\.vs\",
    };

    /// <summary>Files inside app/system/dependency folders never show as results.</summary>
    public static bool IsExcludedFilePath(string path)
    {
        string p = path.Replace('/', '\\').ToLowerInvariant();
        if (!p.EndsWith('\\')) p += "\\";
        foreach (var f in ExcludedFolders)
            if (p.Contains(f, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Whole-word (or whole-phrase) containment so "help" does not hide "HelpDesk Pro".</summary>
    private static bool ContainsWord(string haystack, string word)
    {
        int idx = 0;
        while ((idx = haystack.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = idx == 0 || !char.IsLetterOrDigit(haystack[idx - 1]);
            int end = idx + word.Length;
            bool endOk = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);
            if (startOk && endOk) return true;
            idx = end;
        }
        return false;
    }
}
