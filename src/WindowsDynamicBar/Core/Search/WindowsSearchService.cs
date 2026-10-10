using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Media;

namespace WindowsDynamicBar.Core.Search;

public static class WindowsSearchService
{
    private const int MaxFiles = 6;

    private static bool IsHiddenOrSystem(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return (attr & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;   // gone or unreadable: not a useful result
        }
    }

    private const string ConnString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    public static async Task<List<SearchResult>> SearchFilesAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return new List<SearchResult>();

        // 80ms debounce
        await Task.Delay(80, ct);
        if (ct.IsCancellationRequested) return new List<SearchResult>();

        return await Task.Run(() =>
        {
            var results = new List<SearchResult>();
            try
            {
                string cleanQ = query.Replace("'", "''").Trim();
                using var conn = new OleDbConnection(ConnString);
                conn.Open();

                string sql = $"SELECT TOP 25 System.ItemName, System.ItemPathDisplay FROM SystemIndex WHERE Scope='file:' AND (System.ItemName LIKE '{cleanQ}%' OR CONTAINS(System.ItemName, '\"{cleanQ}*\"'))";
                using var cmd = new OleDbCommand(sql, conn);
                using var reader = cmd.ExecuteReader();

                while (reader.Read() && results.Count < MaxFiles)
                {
                    if (ct.IsCancellationRequested) break;
                    string name = reader["System.ItemName"]?.ToString() ?? "";
                    string path = reader["System.ItemPathDisplay"]?.ToString() ?? "";

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                    if (AppFilters.IsExcludedFilePath(path) || IsHiddenOrSystem(path)) continue;
                    var match = SearchRanker.MatchName(name, query);
                    if (!match.Matched) continue;

                    BitmapSource? icon = AppIconHelper.ExtractIconFromFile(path);

                    string targetPath = path;
                    results.Add(new SearchResult
                    {
                        Title = name,
                        Subtitle = Path.GetDirectoryName(path) ?? path,
                        Category = SearchCategory.Files,
                        Icon = icon,
                        FallbackLetter = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "F",
                        KeyboardHint = "Enter open",
                        Score = match.Score + SearchRanker.FileBias,
                        MatchedIndices = match.Indices,
                        ExePath = targetPath,
                        Action = () => Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true }),
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Windows Search index query error", ("error", ex.Message));
            }
            return results;
        }, ct);
    }
}
