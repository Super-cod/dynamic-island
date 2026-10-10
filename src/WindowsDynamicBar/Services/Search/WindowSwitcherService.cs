using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Search;

namespace WindowsDynamicBar.Services.Search;

/// <summary>One switchable top-level window, in Z-order (most recently used first).</summary>
public sealed record WindowEntry(IntPtr Handle, string Title, string ProcessName, string? ExePath, int Order);

/// <summary>
/// Window list for the search bar ("Windows" group, "w " prefix). Enumerated on a background thread
/// when the bar opens and cached; keystrokes only rank the cached list. Rules (like Alt+Tab):
/// visible, has a title, no owner (unless WS_EX_APPWINDOW), not a tool window, not cloaked
/// (DWMWA_CLOAKED: hidden UWP / other-desktop windows), not this app.
/// App icons come from the process exe, are converted once at the size they are shown, frozen,
/// and kept in a 64-entry LRU cache (the HICON is destroyed right after conversion).
/// </summary>
public sealed class WindowSwitcherService
{
    public const int IconSize = 22;   // the search row shows icons at 22 DIP

    private readonly object _sync = new();
    private readonly LruCache<string, ImageSource> _icons = new(64, StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<WindowEntry> _windows = Array.Empty<WindowEntry>();
    private CancellationTokenSource? _cts;

    public static WindowSwitcherService Instance { get; } = new();

    public IReadOnlyList<WindowEntry> Windows { get { lock (_sync) return _windows; } }

    /// <summary>Raised (worker thread) when a fresh list is ready.</summary>
    public event Action? Refreshed;

    /// <summary>Re-enumerate in the background (previous refresh is cancelled).</summary>
    public void RefreshInBackground(IntPtr ownWindow)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _cts, cts)?.Cancel();
        var token = cts.Token;
        uint ownPid = (uint)Environment.ProcessId;
        _ = Task.Run(() =>
        {
            var list = Enumerate(ownPid, token);
            if (token.IsCancellationRequested) return;
            lock (_sync) _windows = list;
            // Icons load here (disk + shell), never on the UI thread; rows read the cache only.
            foreach (var path in list.Select(w => w.ExePath).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (token.IsCancellationRequested) return;
                if (!_icons.TryGet(path!, out _) && LoadIcon(path!) is { } icon) _icons.Set(path!, icon);
            }
            Refreshed?.Invoke();
        }, token);
    }

    /// <summary>Fuzzy rank on title and process name; empty query = Z-order (newest first).</summary>
    public IReadOnlyList<(WindowEntry Window, int Score, List<int>? Matched)> Match(string query, int max)
    {
        var all = Windows;
        if (string.IsNullOrWhiteSpace(query))
            return all.Take(max).Select(w => (w, 0, (List<int>?)null)).ToList();

        var ranked = new List<(WindowEntry, int, List<int>?)>();
        foreach (var w in all)
        {
            var t = FuzzyMatcher.Match(w.Title, query);
            var p = FuzzyMatcher.Match(w.ProcessName, query);
            if (!t.Matched && !p.Matched) continue;
            // Title matches win ties; recently used windows rank slightly higher.
            int score = Math.Max(t.Matched ? t.Score : 0, p.Matched ? p.Score - 5 : 0) - Math.Min(w.Order, 20);
            ranked.Add((w, score, t.Matched ? t.MatchedIndices : null));
        }
        return ranked.OrderByDescending(r => r.Item2).Take(max).ToList();
    }

    /// <summary>The cached app icon for a window (null until the background refresh loaded it).</summary>
    public ImageSource? CachedIcon(WindowEntry w) =>
        !string.IsNullOrEmpty(w.ExePath) && _icons.TryGet(w.ExePath, out var icon) ? icon : null;

    public int CachedIconCount => _icons.Count;

    // ------------------------------------------------------------------ enumeration

    private List<WindowEntry> Enumerate(uint ownPid, CancellationToken token)
    {
        var result = new List<WindowEntry>();
        var names = new Dictionary<uint, (string Name, string? Path)>();
        var title = new StringBuilder(512);
        int order = 0;
        EnumWindows((hwnd, _) =>
        {
            if (token.IsCancellationRequested) return false;
            if (!IsWindowVisible(hwnd)) return true;
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownPid) return true;

            title.Clear();
            title.EnsureCapacity(len + 1);
            GetWindowText(hwnd, title, title.Capacity);
            string text = title.ToString();
            if (text.Length == 0 || text is "Program Manager") return true;

            if (!names.TryGetValue(pid, out var proc))
            {
                proc = ProcessInfo(pid);
                names[pid] = proc;
            }
            result.Add(new WindowEntry(hwnd, text, proc.Name, proc.Path, order++));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static (string Name, string? Path) ProcessInfo(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return ("", null);
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref size)) return ("", null);
            string path = sb.ToString();
            return (Path.GetFileNameWithoutExtension(path), path);
        }
        finally { CloseHandle(h); }
    }

    private static ImageSource? LoadIcon(string exePath)
    {
        var info = new SHFILEINFO();
        IntPtr ok = SHGetFileInfo(exePath, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(IconSize, IconSize));
            src.Freeze();
            return src;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or ExternalException)
        {
            Log.Debug("Window icon conversion failed", ("error", ex.GetType().Name));
            return null;
        }
        finally { DestroyIcon(info.hIcon); }   // the managed copy is independent of the HICON
    }

    // ------------------------------------------------------------------ Win32

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
}
