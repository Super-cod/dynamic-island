Windows Dynamic Bar 1.1.0
==========================

A Dynamic-Island-style overlay for Windows 10 / 11. A compact pill at the top of
your screen expands only when something matters - media, volume, brightness,
battery, network, Bluetooth, system stats, privacy indicators, timers - then
gets out of your way.

FILES
-----
- WindowsDynamicBar.exe      Portable build (single file, self-contained).
                               Run it anywhere - no installation required.
- WindowsDynamicBar-Setup.exe  Per-user installer (Start Menu + uninstaller).
- checksums.txt              SHA-256 hashes of the files above.

QUICK START
-----------
Run either file. The first time, a 3-step wizard asks for your theme and
whether to start with Windows. The bar appears at the top-center of your screen
and collapses into a small pill. Hover it or trigger a hotkey to expand.

HOTKEYS (defaults - rebind in Settings -> General)
---------
Ctrl+Alt+Space   Toggle the bar
Ctrl+Alt+M       Show media player
Ctrl+Alt+T       Start / show a 5-minute timer

KEY FEATURES
------------
- Media player with album art, seek, prev/play/next (Windows Media Session)
- Spinning media disc - album art as a rotating CD/vinyl disc in the collapsed
  pill (decelerates when paused, crossfades per track, static under Reduce motion)
- Telemetry island - hover the collapsed bar for a CPU / GPU / RAM rail; hover a
  circle to expand a live 30-second graph panel (sampler runs while hovered only)
- Volume and brightness sliders; device name shown
- Battery percentage, charging state, low-battery alert
- Wi-Fi SSID + REAL signal bars from RSSI (no bars when signal is unavailable)
- Bluetooth connect/disconnect alerts
- CPU / GPU / RAM live meters (GPU shows "N/A" when its counter is unavailable)
- Camera / microphone privacy indicators with the responsible app
- Timer, stopwatch, and focus sessions with in-bar controls
- Download, USB, and screenshot alerts
- Gaming mode (tray) - only critical alerts pop while you play
- Dark / Light / Translucent themes, 9 accent colors, per-monitor anchoring
- "Reduce motion" option

CONTROLS
--------
- Drag the pill sideways to move it; position persists.
- Right-click the tray icon for the full menu (Toggle, System monitor,
  Show bar, Gaming mode, Settings, Open logs, About, Exit).
- In the bar: hover any music/level meters to get sliders; use Esc to collapse.

PRIVACY
-------
No telemetry, no accounts, no network calls. Clipboard monitoring is off by
default and records only the FORMAT of a copy, never contents. Settings and
short diagnostic logs are stored locally under %APPDATA%\WindowsDynamicBar.

KNOWN LIMITATIONS
-----------------
- GPU % needs Windows performance counters; otherwise shows N/A.
- Wi-Fi bars require wlanapi.dll (Wi-Fi only; Ethernet shows status without bars).
- Brightness works on panels exposing WMI; external monitors may be skipped.
- Notification previews need an MSIX (Store) install; the plain .exe cannot
  register for user notifications.
- Media control follows the Windows Media Session (Spotify, Edge, foobar2000, ...).
- The Settings window is dark-only; the bar itself follows your chosen theme.
  (Documented limitation.)

SYSTEM REQUIREMENTS
-------------------
- Windows 10 (19041+) or Windows 11, x64
- ~270 MB working set (self-contained .NET 8 WPF baseline)

UNINSTALL
---------
Installer version: Start Menu -> Windows Dynamic Bar -> Uninstall.
Portable version: delete the .exe; remove %APPDATA%\WindowsDynamicBar to delete settings/logs.

Build & verify: dotnet build->0 warnings/0 errors; test suite 26/26 passing.