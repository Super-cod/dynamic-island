using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Services.Media;

public sealed record LrcLine(TimeSpan Timestamp, string Text);

public sealed class LrcLibClient : IDisposable
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    static LrcLibClient()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "WindowsDynamicBar/1.1 (https://github.com/dynamic-bar)");
    }

    private readonly ConcurrentDictionary<string, List<LrcLine>> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cacheFilePath;
    private readonly object _diskLock = new();
    private bool _cacheLoaded;

    public LrcLibClient()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string folder = Path.Combine(appData, "WindowsDynamicBar");
        try { Directory.CreateDirectory(folder); } catch { }
        _cacheFilePath = Path.Combine(folder, "lyrics_cache.json");
        LoadDiskCache();
    }

    public async Task<List<LrcLine>?> FetchLyricsAsync(string artist, string title, TimeSpan duration, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;

        string key = BuildCacheKey(artist, title, duration);
        if (_memoryCache.TryGetValue(key, out var cached))
            return cached;

        try
        {
            int durSec = (int)Math.Round(duration.TotalSeconds);
            string url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}&duration={durSec}";

            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("syncedLyrics", out var syncedElem)) return null;

            string? synced = syncedElem.GetString();
            if (string.IsNullOrWhiteSpace(synced)) return null;

            var lines = ParseLrc(synced);
            if (lines.Count > 0)
            {
                _memoryCache[key] = lines;
                SaveToDiskCache(key, synced);
                return lines;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug("LRCLIB request failed", ("error", ex.Message));
        }

        return null;
    }

    public static List<LrcLine> ParseLrc(string lrc)
    {
        var result = new List<LrcLine>();
        var regex = new Regex(@"\[(\d+):(\d+(?:\.\d+)?)\](.*)", RegexOptions.Compiled);

        using var reader = new StringReader(lrc);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var match = regex.Match(line);
            if (!match.Success) continue;

            if (int.TryParse(match.Groups[1].Value, out int minutes) &&
                double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            {
                var ts = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
                string text = match.Groups[3].Value.Trim();
                result.Add(new LrcLine(ts, text));
            }
        }

        result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return result;
    }

    public static string? GetCurrentLineText(IReadOnlyList<LrcLine>? lines, TimeSpan position)
    {
        if (lines is null || lines.Count == 0) return null;

        string? current = null;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Timestamp <= position)
                current = lines[i].Text;
            else
                break;
        }

        return string.IsNullOrWhiteSpace(current) ? null : current;
    }

    private static string BuildCacheKey(string artist, string title, TimeSpan duration)
        => $"{artist.Trim().ToLowerInvariant()}|{title.Trim().ToLowerInvariant()}|{(int)Math.Round(duration.TotalSeconds)}";

    private void LoadDiskCache()
    {
        if (_cacheLoaded) return;
        _cacheLoaded = true;
        try
        {
            lock (_diskLock)
            {
                if (!File.Exists(_cacheFilePath)) return;
                string text = File.ReadAllText(_cacheFilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
                if (dict is null) return;

                foreach (var (k, v) in dict)
                {
                    var lines = ParseLrc(v);
                    if (lines.Count > 0)
                        _memoryCache[k] = lines;
                }
            }
        }
        catch { }
    }

    private void SaveToDiskCache(string key, string rawLrc)
    {
        try
        {
            lock (_diskLock)
            {
                Dictionary<string, string> dict = new();
                if (File.Exists(_cacheFilePath))
                {
                    try
                    {
                        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_cacheFilePath));
                        if (loaded is not null) dict = loaded;
                    }
                    catch { }
                }

                dict[key] = rawLrc;

                // Limit disk cache to 150 tracks
                if (dict.Count > 150)
                {
                    var toRemove = new List<string>();
                    int excess = dict.Count - 150;
                    foreach (var k in dict.Keys)
                    {
                        toRemove.Add(k);
                        if (toRemove.Count >= excess) break;
                    }
                    foreach (var k in toRemove) dict.Remove(k);
                }

                File.WriteAllText(_cacheFilePath, JsonSerializer.Serialize(dict));
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _memoryCache.Clear();
    }
}
