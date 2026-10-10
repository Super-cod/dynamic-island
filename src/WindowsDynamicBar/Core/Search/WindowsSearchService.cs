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

                string sql = $"SELECT TOP 4 System.ItemName, System.ItemPathDisplay FROM SystemIndex WHERE Scope='file:' AND (System.ItemName LIKE '{cleanQ}%' OR CONTAINS(System.ItemName, '\"{cleanQ}*\"'))";
                using var cmd = new OleDbCommand(sql, conn);
                using var reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    if (ct.IsCancellationRequested) break;
                    string name = reader["System.ItemName"]?.ToString() ?? "";
                    string path = reader["System.ItemPathDisplay"]?.ToString() ?? "";

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;

                    BitmapSource? icon = AppIconHelper.ExtractIconFromFile(path);

                    string targetPath = path;
                    results.Add(new SearchResult
                    {
                        Title = name,
                        Subtitle = path,
                        Category = SearchCategory.Files,
                        Icon = icon,
                        FallbackLetter = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "F",
                        KeyboardHint = "Enter",
                        Action = () =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
                            }
                            catch { }
                        }
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
