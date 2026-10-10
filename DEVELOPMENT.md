# Windows Dynamic Bar — Development Guide & House Rules

## House Rules

### 1. Performance & Memory
- **Bitmap Image Hygiene**:
  - Always decode images at their displayed size using `DecodePixelWidth`/`DecodePixelHeight`.
  - Always call `BitmapSource.Freeze()` immediately after decoding or loading.
  - Full-resolution artwork and images must be dropped when no longer active (e.g. clipboard history keeps full images only for the 3 newest images; media player keeps at most 3 historical track bitmaps).
- **GDI & Native Handle Discipline**:
  - Any native handle acquired via Win32 API (`HICON`, `HBITMAP`, `HDC`, process handle `IntPtr`) must be destroyed or closed immediately after use via `DestroyIcon()`, `DeleteObject()`, or `CloseHandle()`.
  - GDI and USER handle counts must remain strictly flat during all interactions and repeated loops.
- **Bounded Capacities**:
  - All caches and ring buffers must be explicitly bounded (e.g., `WindowSwitcherService` LRU icon cache capped at 64; `TelemetryHistory` capped at 240 samples; clipboard history bounded to `Limit` items + pinned).
- **No Polling**:
  - Use native Windows event mechanisms instead of polling:
    - Clipboard: `AddClipboardFormatListener` / `WM_CLIPBOARDUPDATE`.
    - Battery & Power: `RegisterPowerSettingNotification` / `WM_POWERBROADCAST`.
    - Displays: `WM_DISPLAYCHANGE` with a 300 ms debounce timer.
    - Explorer restart: `RegisterWindowMessage("TaskbarCreated")`.
- **UI Responsiveness & Dispatcher Discipline**:
  - Heavy I/O (disk scans, network queries, process enumeration, icon extraction) must occur on background threads via `Task.Run()`.
  - Pre-build all UI panels and user controls. Never construct UI hierarchy on `MouseEnter` or mouse motion.
  - Coalesce rapid UI events (volume scroll wheel, network stats, timer ticks) to at most once per frame.
  - Target: Collapsed idle CPU < 1.0%, hotkey-to-search visibility < 100 ms, zero dropped frames during morph animations.

### 2. Privacy & Security
- Never log, store, or transmit clipboard content.
- Respect password manager exclusion formats: `ExcludeClipboardContentFromMonitorProcessing` and `CanIncludeInClipboardHistory = 0`.
- Honor the user-editable process ignore list (`ClipboardIgnoredApps`).
- Persisted clipboard entries must be encrypted using Windows DPAPI (`DataProtectionScope.CurrentUser`).

---

## 10-Minute Soak Test Procedure

The soak test verifies long-running stability, flat memory, zero GDI leaks, and CPU efficiency.

### Test Protocol
1. **Initial Baseline**:
   - Launch `WindowsDynamicBar.exe`.
   - Allow 30 seconds to settle.
   - Record baseline: Working Set, Private Bytes, GC Heap / LOH, GDI Objects, USER Objects, Handles, Threads, Idle CPU %.
2. **Stress Loops (8 minutes)**:
   - Hover open and close: **200 cycles**.
   - Track / media metadata changes: **60 cycles**.
   - Detail panels opened and closed: **30 times each** (CPU, GPU, RAM, Net, Weather, Clock, Mirror, Toggles, Clipboard, Timer).
   - Camera mirror opened and closed: **20 cycles**.
   - Speed test: **3 runs**.
   - Search launcher opened, queried, and closed: **100 cycles**.
   - Toast notifications dispatched: **100 notifications**.
   - Clipboard updates copied: **200 copies** (varying texts, URLs, image snippets, files).
   - Island page flips: **100 page switches**.
3. **Idle Cooldown (2 minutes)**:
   - Allow the island to remain collapsed and completely idle for 2 minutes.
4. **Final Measurements**:
   - Record final: Working Set, Private Bytes, GC Heap / LOH, GDI Objects, USER Objects, Handles, Threads, Idle CPU %.
   - Verify all metrics remain flat and bounded.

### Automated Script Driving
In `DEBUG` builds, commands can be piped directly to the island without moving the mouse:
```powershell
# Drive commands via named pipe
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "WindowsDynamicBar_Signal", [System.IO.Pipes.PipeDirection]::Out)
$pipe.Connect(500)
$bytes = [System.Text.Encoding]::UTF8.GetBytes("cmd:hover")
$pipe.Write($bytes, 0, $bytes.Length)
$pipe.Close()
```

---

## Build & Release Verification

1. **Clean build**:
   ```powershell
   dotnet build -c Release
   ```
2. **Unit tests**:
   ```powershell
   dotnet run --project tests\WindowsDynamicBar.Tests\WindowsDynamicBar.Tests.csproj -c Release
   ```
3. **Release packaging**:
   ```powershell
   .\build-release.ps1
   ```
