using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WindowsDynamicBar.Core.Clipboard;

public enum ClipKind { Text, Link, Image, Files }

/// <summary>One clipboard history entry. Images keep a small PNG thumbnail always and the full
/// PNG only while they are among the newest <see cref="ClipboardHistory.FullImageCount"/> items.</summary>
public sealed class ClipItem
{
    public required long Id { get; init; }
    public required ClipKind Kind { get; init; }
    public required string Hash { get; init; }
    public DateTime CreatedUtc { get; set; }
    public bool Pinned { get; set; }
    public string? Text { get; init; }
    public IReadOnlyList<string>? Files { get; init; }
    public byte[]? ThumbPng { get; set; }
    public byte[]? FullPng { get; set; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }

    /// <summary>One-line preview (max 60 chars, whitespace collapsed).</summary>
    public string Preview => Kind switch
    {
        ClipKind.Image => $"Image {ImageWidth}×{ImageHeight}",
        ClipKind.Files when Files is { Count: 1 } f => Path.GetFileName(f[0]) is { Length: > 0 } n ? n : f[0],
        ClipKind.Files when Files is { Count: > 1 } f => $"{f.Count} files: {string.Join(", ", f.Select(Path.GetFileName))}",
        _ => ClipboardHistory.OneLine(Text ?? "", 60),
    };

    /// <summary>False for an image whose full-size copy was dropped (only the thumbnail is left).</summary>
    public bool CanRestore => Kind != ClipKind.Image || FullPng is not null;
}

/// <summary>
/// Clipboard history rules, independent of the OS clipboard (unit-testable):
/// newest first; identical to the newest = skipped; identical to an older item = moved to the top;
/// at most <see cref="Limit"/> unpinned items (pinned items stay and do not count); the full image
/// is kept only for the newest 3 images; optional auto-clear by age. Thread-safe.
/// </summary>
public sealed class ClipboardHistory
{
    public const int FullImageCount = 3;
    public const int MaxLimit = 50;
    public const int MaxTextBytes = 100 * 1024;

    private readonly object _sync = new();
    private readonly List<ClipItem> _items = new();
    private long _nextId = 1;
    private int _limit = 10;

    public int Limit
    {
        get { lock (_sync) return _limit; }
        set { lock (_sync) { _limit = Math.Clamp(value, 1, MaxLimit); Trim(); } }
    }

    public int Count { get { lock (_sync) return _items.Count; } }
    public int UnpinnedCount { get { lock (_sync) return _items.Count(i => !i.Pinned); } }

    public IReadOnlyList<ClipItem> Snapshot() { lock (_sync) return _items.ToList(); }

    public long NewId() { lock (_sync) return _nextId++; }

    /// <summary>Add a captured item. Returns false when it was a duplicate of the newest item.</summary>
    public bool Add(ClipItem item)
    {
        lock (_sync)
        {
            if (_items.Count > 0 && _items[0].Hash == item.Hash && _items[0].Kind == item.Kind) return false;
            int existing = _items.FindIndex(i => i.Hash == item.Hash && i.Kind == item.Kind);
            if (existing >= 0)
            {
                // Copied again (e.g. restored from history): move to the top, keep its pin.
                var old = _items[existing];
                _items.RemoveAt(existing);
                old.CreatedUtc = item.CreatedUtc;
                if (old.Kind == ClipKind.Image && old.FullPng is null) old.FullPng = item.FullPng;
                _items.Insert(0, old);
            }
            else _items.Insert(0, item);
            Trim();
            return true;
        }
    }

    public bool Remove(long id) { lock (_sync) return _items.RemoveAll(i => i.Id == id) > 0; }

    public void SetPinned(long id, bool pinned)
    {
        lock (_sync)
        {
            var it = _items.FirstOrDefault(i => i.Id == id);
            if (it is null) return;
            it.Pinned = pinned;
            Trim();
        }
    }

    /// <summary>"Clear all": removes every unpinned item (pinned items are kept, like Windows).</summary>
    public void ClearUnpinned() { lock (_sync) _items.RemoveAll(i => !i.Pinned); }

    public void ClearEverything() { lock (_sync) _items.Clear(); }

    /// <summary>Remove unpinned items older than <paramref name="maxAge"/>; returns how many.</summary>
    public int AutoClear(TimeSpan maxAge, DateTime nowUtc)
    {
        lock (_sync) return _items.RemoveAll(i => !i.Pinned && nowUtc - i.CreatedUtc > maxAge);
    }

    /// <summary>Oldest unpinned creation time (for scheduling the next auto-clear), or null.</summary>
    public DateTime? OldestUnpinnedUtc()
    {
        lock (_sync)
        {
            var times = _items.Where(i => !i.Pinned).Select(i => i.CreatedUtc).ToList();
            return times.Count == 0 ? null : times.Min();
        }
    }

    /// <summary>Restore items (persisted history); the same rules apply.</summary>
    public void Load(IEnumerable<ClipItem> items)
    {
        lock (_sync)
        {
            _items.Clear();
            _items.AddRange(items.OrderByDescending(i => i.CreatedUtc));
            _nextId = _items.Count == 0 ? 1 : _items.Max(i => i.Id) + 1;
            Trim();
        }
    }

    private void Trim()
    {
        // Unpinned items beyond the limit are dropped, oldest first.
        int unpinned = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Pinned) continue;
            if (++unpinned > _limit) { _items.RemoveAt(i); i--; }
        }
        // Full images only for the newest FullImageCount images.
        int images = 0;
        foreach (var it in _items)
        {
            if (it.Kind != ClipKind.Image) continue;
            if (++images > FullImageCount) it.FullPng = null;
        }
    }

    /// <summary>Case-insensitive filter on the preview, text and file paths; empty = everything.</summary>
    public static IReadOnlyList<ClipItem> Filter(IReadOnlyList<ClipItem> items, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return items;
        string q = query.Trim();
        return items.Where(i =>
            i.Preview.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (i.Text?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (i.Files?.Any(f => f.Contains(q, StringComparison.OrdinalIgnoreCase)) ?? false)).ToList();
    }

    public static string Hash(string kind, ReadOnlySpan<byte> data)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(data, h);
        return kind + ":" + Convert.ToHexString(h[..12]);
    }

    public static string HashText(string kind, string text) => Hash(kind, Encoding.UTF8.GetBytes(text));

    /// <summary>A single http(s) URL with no spaces counts as a link.</summary>
    public static bool IsLink(string text)
    {
        string t = text.Trim();
        return t.Length < 2048 && !t.Contains(' ') && !t.Contains('\n')
               && Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
    }

    public static string OneLine(string text, int max)
    {
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length > max) break;
        }
        return sb.Length > max ? sb.ToString(0, max - 1) + "…" : sb.ToString();
    }

    /// <summary>"now", "3 min", "2 h", "4 d".</summary>
    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h";
        return $"{(int)age.TotalDays} d";
    }
}
