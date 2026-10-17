using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Clipboard;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Interop;

namespace WindowsDynamicBar.Services.Clipboard;

/// <summary>
/// Clipboard history. Capture is event-driven (AddClipboardFormatListener / WM_CLIPBOARDUPDATE,
/// no polling); the clipboard is read on the UI thread only long enough to copy the raw data,
/// then hashing, thumbnails and PNG encoding run on the thread pool.
/// Skipped (never stored): content marked ExcludeClipboardContentFromMonitorProcessing,
/// CanIncludeInClipboardHistory = 0 or "Clipboard Viewer Ignore" (password managers set these),
/// copies made by apps on the ignore list, text over 100 KB, and anything while paused.
/// Memory only unless "Remember history" is on (then DPAPI CurrentUser-encrypted on disk).
/// Content is never logged and never leaves the PC.
/// </summary>
public sealed class ClipboardHistoryService : IDisposable
{
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint CF_DIB = 8;
    private const uint CF_DIBV5 = 17;
    private const int ThumbSize = 96;

    private static readonly uint FmtExclude = NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint FmtCanInclude = NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint FmtViewerIgnore = NativeMethods.RegisterClipboardFormat("Clipboard Viewer Ignore");
    private static readonly uint FmtPng = NativeMethods.RegisterClipboardFormat("PNG");

    private readonly SettingsService _settings;
    private readonly ClipboardHistory _history = new();
    private readonly Dispatcher _dispatcher;
    private readonly string _storePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsDynamicBar", "clipboard.dat");
    private readonly object _timerSync = new();
    private Timer? _saveTimer;
    private Timer? _autoClearTimer;
    private CancellationTokenSource _cts = new();
    private IntPtr _hwnd;
    private int _retries;
    private bool _enabled;

    public ClipboardHistoryService(SettingsService settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
    }

    /// <summary>Raised (any thread) after the history changed.</summary>
    public event Action? Changed;

    public bool Paused { get; private set; }
    public bool Enabled => _enabled;
    public int Count => _history.Count;
    public int Limit => _history.Limit;

    public IReadOnlyList<ClipItem> Items => _history.Snapshot();

    /// <summary>Feature on: load persisted history (if enabled) and accept captures.</summary>
    public void Start()
    {
        if (_enabled) return;
        _enabled = true;
        _cts = new CancellationTokenSource();
        ApplySettings(_settings.Current);
        if (_settings.Current.ClipboardPersist) _ = Task.Run(LoadPersisted, _cts.Token);
    }

    /// <summary>Feature off: stop capturing, drop the in-memory history and timers.</summary>
    public void Stop()
    {
        if (!_enabled) return;
        _enabled = false;
        _cts.Cancel();
        lock (_timerSync)
        {
            _saveTimer?.Dispose(); _saveTimer = null;
            _autoClearTimer?.Dispose(); _autoClearTimer = null;
        }
        _history.ClearEverything();
        Changed?.Invoke();
    }

    public void ApplySettings(SettingsModel s)
    {
        _history.Limit = s.ClipboardLimit;
        if (!s.ClipboardPersist) DeletePersisted();
        else ScheduleSave();
        ScheduleAutoClear();
        Changed?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        Paused = paused;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ capture

    /// <summary>WM_CLIPBOARDUPDATE (UI thread). Copies raw data out and returns quickly.</summary>
    public void OnClipboardUpdate(IntPtr hwnd)
    {
        if (!_enabled) return;
        _hwnd = hwnd;
        if (Paused) return;   // our own copy-backs are captured too and just move the item to the top
        _retries = 0;
        Capture();
    }

    private void Capture()
    {
        if (!NativeMethods.OpenClipboard(_hwnd))
        {
            // Another app still holds the clipboard: try again once the dispatcher is idle (no sleeps).
            if (++_retries <= 3) _dispatcher.BeginInvoke(DispatcherPriority.Background, Capture);
            return;
        }

        RawClip? raw;
        try
        {
            if (IsSensitive()) return;
            string? owner = OwnerProcessName();
            if (owner is not null && IsIgnoredApp(owner)) return;
            raw = ReadRaw();
        }
        finally { NativeMethods.CloseClipboard(); }

        if (raw is null) return;
        var token = _cts.Token;
        _ = Task.Run(() => ProcessRaw(raw), token);
    }

    private static bool IsSensitive()
    {
        if (FmtExclude != 0 && NativeMethods.IsClipboardFormatAvailable(FmtExclude)) return true;
        if (FmtViewerIgnore != 0 && NativeMethods.IsClipboardFormatAvailable(FmtViewerIgnore)) return true;
        if (FmtCanInclude != 0 && NativeMethods.IsClipboardFormatAvailable(FmtCanInclude))
        {
            IntPtr h = NativeMethods.GetClipboardData(FmtCanInclude);
            if (h != IntPtr.Zero)
            {
                IntPtr p = NativeMethods.GlobalLock(h);
                try { if (p != IntPtr.Zero && (long)NativeMethods.GlobalSize(h) >= 4 && Marshal.ReadInt32(p) == 0) return true; }
                finally { if (p != IntPtr.Zero) NativeMethods.GlobalUnlock(h); }
            }
        }
        return false;
    }

    private static string? OwnerProcessName()
    {
        IntPtr owner = NativeMethods.GetClipboardOwner();
        if (owner == IntPtr.Zero) return null;
        NativeMethods.GetWindowThreadProcessId(owner, out uint pid);
        if (pid == 0) return null;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (ArgumentException) { return null; }          // process already gone
        catch (InvalidOperationException) { return null; }
    }

    private bool IsIgnoredApp(string processName)
    {
        foreach (var name in _settings.Current.ClipboardIgnoredApps.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string n = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            if (string.Equals(n, processName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private sealed record RawClip(ClipKind Kind, string? Text, string[]? Files, byte[]? Png, byte[]? Dib);

    /// <summary>Files first, then text, then image (Office text copies also carry pictures).</summary>
    private static RawClip? ReadRaw()
    {
        if (NativeMethods.IsClipboardFormatAvailable(CF_HDROP))
        {
            IntPtr h = NativeMethods.GetClipboardData(CF_HDROP);
            if (h != IntPtr.Zero)
            {
                uint n = NativeMethods.DragQueryFile(h, 0xFFFFFFFF, null, 0);
                var files = new List<string>();
                for (uint i = 0; i < n && i < 500; i++)
                {
                    uint len = NativeMethods.DragQueryFile(h, i, null, 0);
                    var sb = new StringBuilder((int)len + 1);
                    NativeMethods.DragQueryFile(h, i, sb, (uint)sb.Capacity);
                    files.Add(sb.ToString());
                }
                if (files.Count > 0) return new RawClip(ClipKind.Files, null, files.ToArray(), null, null);
            }
        }
        if (NativeMethods.IsClipboardFormatAvailable(CF_UNICODETEXT))
        {
            string? text = ReadGlobalString(NativeMethods.GetClipboardData(CF_UNICODETEXT));
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (Encoding.UTF8.GetByteCount(text) > ClipboardHistory.MaxTextBytes) return null;   // too large: not kept
                return new RawClip(ClipboardHistory.IsLink(text) ? ClipKind.Link : ClipKind.Text, text, null, null, null);
            }
        }
        if (FmtPng != 0 && NativeMethods.IsClipboardFormatAvailable(FmtPng))
        {
            byte[]? png = ReadGlobalBytes(NativeMethods.GetClipboardData(FmtPng));
            if (png is { Length: > 0 }) return new RawClip(ClipKind.Image, null, null, png, null);
        }
        uint dibFmt = NativeMethods.IsClipboardFormatAvailable(CF_DIBV5) ? CF_DIBV5 : NativeMethods.IsClipboardFormatAvailable(CF_DIB) ? CF_DIB : 0;
        if (dibFmt != 0)
        {
            byte[]? dib = ReadGlobalBytes(NativeMethods.GetClipboardData(dibFmt));
            if (dib is { Length: > 40 }) return new RawClip(ClipKind.Image, null, null, null, dib);
        }
        return null;
    }

    private void ProcessRaw(RawClip raw)
    {
        try
        {
            ClipItem item;
            var now = DateTime.UtcNow;
            switch (raw.Kind)
            {
                case ClipKind.Files:
                    item = new ClipItem
                    {
                        Id = _history.NewId(), Kind = ClipKind.Files, CreatedUtc = now, Files = raw.Files,
                        Hash = ClipboardHistory.HashText("f", string.Join("\n", raw.Files!)),
                    };
                    break;
                case ClipKind.Image:
                {
                    BitmapSource? full = raw.Png is not null ? DecodePng(raw.Png) : DecodeDib(raw.Dib!);
                    if (full is null) return;
                    byte[] png = raw.Png ?? EncodePng(full);
                    item = new ClipItem
                    {
                        Id = _history.NewId(), Kind = ClipKind.Image, CreatedUtc = now,
                        Hash = ClipboardHistory.Hash("i", png), FullPng = png,
                        ThumbPng = Thumbnail(png), ImageWidth = full.PixelWidth, ImageHeight = full.PixelHeight,
                    };
                    break;
                }
                default:
                    item = new ClipItem
                    {
                        Id = _history.NewId(), Kind = raw.Kind, CreatedUtc = now, Text = raw.Text,
                        Hash = ClipboardHistory.HashText("t", raw.Text!),
                    };
                    break;
            }
            if (_history.Add(item)) AfterChange();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or InvalidOperationException
                                       or ArgumentException or COMException)
        {
            Log.Debug("Clipboard item could not be processed", ("kind", raw.Kind), ("error", ex.GetType().Name));
        }
    }

    private void AfterChange()
    {
        ScheduleSave();
        ScheduleAutoClear();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ images

    private static BitmapSource? DecodePng(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var f = dec.Frames[0];
        f.Freeze();
        return f;
    }

    /// <summary>DIB (BITMAPINFOHEADER + pixels) -> BMP file in memory -> decoder.</summary>
    private static BitmapSource? DecodeDib(byte[] dib)
    {
        int headerSize = BitConverter.ToInt32(dib, 0);
        short bitCount = BitConverter.ToInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int clrUsed = BitConverter.ToInt32(dib, 32);
        int masks = compression == 3 && headerSize == 40 ? 12 : 0;   // BI_BITFIELDS colour masks after a v1 header
        int palette = bitCount <= 8 ? (clrUsed != 0 ? clrUsed : 1 << bitCount) * 4 : clrUsed * 4;
        int offset = 14 + headerSize + masks + palette;

        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(offset).CopyTo(bmp, 10);
        Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);
        using var ms = new MemoryStream(bmp);
        var dec = new BmpBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var f = dec.Frames[0];
        f.Freeze();
        return f;
    }

    private static byte[] EncodePng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>96 px wide thumbnail (decoded at that size, frozen) re-encoded as a small PNG.</summary>
    private static byte[] Thumbnail(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = ThumbSize;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return EncodePng(bi);
    }

    // ------------------------------------------------------------------ actions

    public void Remove(long id) { if (_history.Remove(id)) AfterChange(); }

    public void SetPinned(long id, bool pinned) { _history.SetPinned(id, pinned); AfterChange(); }

    public void ClearAll() { _history.ClearUnpinned(); AfterChange(); }

    /// <summary>Put an item back on the clipboard (UI thread). Optionally paste it into
    /// <paramref name="pasteInto"/> (focus restored first, then Ctrl+V).</summary>
    public string? Restore(ClipItem item, IntPtr pasteInto)
    {
        if (!item.CanRestore) return "The full image is no longer kept";
        try
        {
            var data = new System.Windows.DataObject();
            switch (item.Kind)
            {
                case ClipKind.Files:
                    var list = new System.Collections.Specialized.StringCollection();
                    list.AddRange(item.Files!.ToArray());
                    data.SetFileDropList(list);
                    break;
                case ClipKind.Image:
                    var img = DecodePng(item.FullPng!);
                    if (img is null) return "Image could not be decoded";
                    data.SetImage(img);
                    data.SetData("PNG", new MemoryStream(item.FullPng!));
                    break;
                default:
                    data.SetText(item.Text ?? "", System.Windows.TextDataFormat.UnicodeText);
                    break;
            }
            System.Windows.Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            return "Clipboard is busy: " + ex.Message;
        }

        if (pasteInto != IntPtr.Zero && WindowActivation.BringToFront(pasteInto))
            WindowActivation.SendPaste();
        return null;
    }

    // ------------------------------------------------------------------ auto-clear

    private void ScheduleAutoClear()
    {
        int hours = _settings.Current.ClipboardAutoClearHours;
        lock (_timerSync)
        {
            _autoClearTimer?.Dispose();
            _autoClearTimer = null;
            if (!_enabled || hours <= 0) return;
            var maxAge = TimeSpan.FromHours(hours);
            if (_history.AutoClear(maxAge, DateTime.UtcNow) > 0) { ScheduleSave(); Changed?.Invoke(); }
            if (_history.OldestUnpinnedUtc() is not DateTime oldest) return;   // nothing to expire: no timer
            var due = oldest + maxAge - DateTime.UtcNow;
            if (due < TimeSpan.Zero) due = TimeSpan.Zero;
            _autoClearTimer = new Timer(_ => ScheduleAutoClear(), null, due + TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    // ------------------------------------------------------------------ persistence (DPAPI)

    private sealed record StoredItem(long Id, ClipKind Kind, string Hash, DateTime CreatedUtc, bool Pinned,
        string? Text, string[]? Files, byte[]? ThumbPng, byte[]? FullPng, int W, int H);

    /// <summary>Coalesce saves: write 2 s after the last change (one timer, only while needed).</summary>
    private void ScheduleSave()
    {
        if (!_settings.Current.ClipboardPersist || !_enabled) return;
        lock (_timerSync)
        {
            _saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
    }

    private void SaveNow()
    {
        if (!_settings.Current.ClipboardPersist || !_enabled) return;
        try
        {
            var items = _history.Snapshot().Select(i => new StoredItem(i.Id, i.Kind, i.Hash, i.CreatedUtc, i.Pinned,
                i.Text, i.Files?.ToArray(), i.ThumbPng, i.FullPng, i.ImageWidth, i.ImageHeight)).ToList();
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(items);
            byte[] cipher = Dpapi.Protect(plain);
            Array.Clear(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            string tmp = _storePath + ".tmp";
            File.WriteAllBytes(tmp, cipher);
            File.Move(tmp, _storePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicFailure)
        {
            Log.Warning("Clipboard history save failed", ("error", ex.GetType().Name));
        }
    }

    private void LoadPersisted()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            byte[] plain = Dpapi.Unprotect(File.ReadAllBytes(_storePath));
            var items = JsonSerializer.Deserialize<List<StoredItem>>(plain) ?? new();
            Array.Clear(plain);
            _history.Load(items.Select(s => new ClipItem
            {
                Id = s.Id, Kind = s.Kind, Hash = s.Hash, CreatedUtc = s.CreatedUtc, Pinned = s.Pinned,
                Text = s.Text, Files = s.Files, ThumbPng = s.ThumbPng, FullPng = s.FullPng, ImageWidth = s.W, ImageHeight = s.H,
            }));
            ScheduleAutoClear();
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicFailure or JsonException)
        {
            Log.Warning("Clipboard history could not be restored; starting empty", ("error", ex.GetType().Name));
        }
    }

    private void DeletePersisted()
    {
        try { if (File.Exists(_storePath)) File.Delete(_storePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Could not delete stored clipboard history", ("error", ex.GetType().Name));
        }
    }

    /// <summary>"Clear all data": memory and disk.</summary>
    public void ForgetEverything()
    {
        _history.ClearEverything();
        DeletePersisted();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_timerSync)
        {
            if (_saveTimer is not null && _settings.Current.ClipboardPersist && _enabled) SaveNow();
            _saveTimer?.Dispose(); _saveTimer = null;
            _autoClearTimer?.Dispose(); _autoClearTimer = null;
        }
        _cts.Dispose();
    }

    // ------------------------------------------------------------------ Win32 helpers

    private static string? ReadGlobalString(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        IntPtr p = NativeMethods.GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            int maxChars = (int)Math.Min(int.MaxValue / 2, (long)NativeMethods.GlobalSize(h) / 2);
            string s = Marshal.PtrToStringUni(p, Math.Max(0, maxChars));
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s[..nul] : s;
        }
        finally { NativeMethods.GlobalUnlock(h); }
    }

    private static byte[]? ReadGlobalBytes(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        long size = (long)NativeMethods.GlobalSize(h);
        if (size <= 0 || size > 256L * 1024 * 1024) return null;
        IntPtr p = NativeMethods.GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(p, bytes, 0, (int)size);
            return bytes;
        }
        finally { NativeMethods.GlobalUnlock(h); }
    }
}

/// <summary>Raised when DPAPI cannot protect/unprotect (e.g. a file from another user).</summary>
public sealed class CryptographicFailure : Exception
{
    public CryptographicFailure(string message) : base(message) { }
}

/// <summary>DPAPI (CurrentUser scope) via crypt32 - no extra package.</summary>
internal static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsDynamicBar.ClipboardHistory.v1");

    public static byte[] Protect(byte[] plain) => Transform(plain, protect: true);
    public static byte[] Unprotect(byte[] cipher) => Transform(cipher, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = new NativeMethods.DATA_BLOB();
        var entropy = new NativeMethods.DATA_BLOB();
        var outBlob = new NativeMethods.DATA_BLOB();
        var hIn = GCHandle.Alloc(input, GCHandleType.Pinned);
        var hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = input.Length; inBlob.pbData = hIn.AddrOfPinnedObject();
            entropy.cbData = Entropy.Length; entropy.pbData = hEnt.AddrOfPinnedObject();
            const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
            bool ok = protect
                ? NativeMethods.CryptProtectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : NativeMethods.CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok) throw new CryptographicFailure($"DPAPI error {Marshal.GetLastWin32Error()}");
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            hIn.Free();
            hEnt.Free();
            if (outBlob.pbData != IntPtr.Zero) NativeMethods.LocalFree(outBlob.pbData);
        }
    }
}
