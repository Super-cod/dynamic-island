using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace WindowsDynamicBar.Core.Logging;

public enum LogLevel { Debug = 0, Info = 1, Warning = 2, Error = 3 }

/// <summary>
/// Lightweight structured logger. Writes to %AppData%\WindowsDynamicBar\logs.
/// Never logs secrets: callers must not pass API keys or clipboard content.
/// Thread-safe, rotation by size, levels filterable.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static string _logFile = "";
    private static LogLevel _minLevel = LogLevel.Info;
    private static long _maxBytes = 5 * 1024 * 1024;
    private static bool _initialized;

    public static string LogDirectory
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WindowsDynamicBar", "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void Init(LogLevel minLevel = LogLevel.Debug)
    {
        lock (Sync)
        {
            if (_initialized) return;
            _minLevel = minLevel;
            OpenWriter_NoLock();
            _initialized = true;
            Info("Log initialized", ("level", minLevel.ToString()));
        }
    }

    private static void OpenWriter_NoLock()
    {
        _writer?.Dispose();
        _logFile = Path.Combine(LogDirectory, $"wdb-{DateTime.Now:yyyyMMdd}.log");
        var fs = new FileStream(_logFile, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
    }

    public static void Debug(string message, params (string Key, object? Value)[] props) => Write(LogLevel.Debug, message, null, props);
    public static void Info(string message, params (string Key, object? Value)[] props) => Write(LogLevel.Info, message, null, props);
    public static void Warning(string message, params (string Key, object? Value)[] props) => Write(LogLevel.Warning, message, null, props);
    public static void Error(string message, Exception? ex = null, params (string Key, object? Value)[] props) => Write(LogLevel.Error, message, ex, props);

    private static void Write(LogLevel level, string message, Exception? ex, (string Key, object? Value)[] props)
    {
        if (!_initialized) Init();
        if (level < _minLevel) return;

        var sb = new StringBuilder(160);
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        sb.Append(" [").Append(level.ToString().ToUpperInvariant().PadRight(7)).Append("] ");
        sb.Append(message);
        foreach (var (key, value) in props)
            sb.Append(' ').Append(key).Append('=').Append(value ?? "null");
        if (ex != null)
            sb.Append(" exception=").Append(ex.GetType().Name).Append(": ").Append(ex.Message)
              .Append(" | ").Append(ex.StackTrace?.Replace('\n', ' '));

        lock (Sync)
        {
            try
            {
                if (_writer == null) OpenWriter_NoLock();
                _writer!.WriteLine(sb.ToString());
                if (_writer.BaseStream.Length > _maxBytes)
                {
                    _writer.Dispose();
                    _writer = null;
                    var old = _logFile;
                    var rotated = Path.Combine(LogDirectory, $"wdb-{DateTime.Now:yyyyMMdd-HHmmss}.old.log");
                    File.Move(old, rotated, overwrite: true);
                    OpenWriter_NoLock();
                }
            }
            catch { /* logging must never crash the app */ }
        }

        if (Debugger.IsAttached && level >= LogLevel.Warning)
            System.Diagnostics.Debug.WriteLine(sb.ToString());
    }

    public static string ReadRecentLogs(int maxLines = 400)
    {
        try
        {
            if (!File.Exists(_logFile)) return "(no log file)";
            var lines = File.ReadAllLines(_logFile);
            var take = Math.Min(lines.Length, maxLines);
            return string.Join(Environment.NewLine, lines.Skip(lines.Length - take));
        }
        catch (Exception ex) { return "Failed to read logs: " + ex.Message; }
    }

    public static void ClearAll()
    {
        lock (Sync)
        {
            try
            {
                _writer?.Dispose();
                _writer = null;
                foreach (var f in Directory.GetFiles(LogDirectory, "wdb-*.log"))
                    try { File.Delete(f); } catch { }
                OpenWriter_NoLock();
            }
            catch { }
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
            _initialized = false;
        }
    }
}
