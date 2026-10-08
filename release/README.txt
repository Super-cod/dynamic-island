Windows Dynamic Bar 1.0.0
========================

A Dynamic-Island-style overlay bar for Windows 10/11 (x64) that floats at the top
center of the screen and morphs to show live system information, then collapses back
to a compact pill. Built with C# / WPF on .NET 8. 100% self-contained - no .NET
runtime required on the target machine.

INSTALL
-------
Run WindowsDynamicBar-Setup.exe and follow the wizard.
- Per-user install, no administrator rights required (default: C:\Program Files\WindowsDynamicBar
  or the folder you choose; per-user default is fine).
- Optional tasks: desktop shortcut, start automatically with Windows.
- To uninstall: Settings > Apps > "Windows Dynamic Bar", or run Unins000.exe from
  the install folder. Your settings and logs are kept in %AppData%\WindowsDynamicBar;
  delete that folder for a full clean removal.

WHAT IT DOES
------------
- Media widget: album art, title/artist/album, progress bar, prev / play-pause / next
  transport controls (Windows System Media Transport Controls API).
- Volume widget: live volume level with slider (Core Audio API), mute state.
- Battery widget: charge level, charging state, power source, energy-saver flag.
- Network widget: connectivity state, SSID/connection name, IPv4 address, adapter kind.
- Bluetooth widget: paired device list with connect/disconnect events.
- Download widget: shows large temporary files appearing in your Downloads folder.
- Privacy widget: camera and microphone in-use indicators (global device watchers).
- Timer & stopwatch widget: countdowns with desktop toast when finished.
- Focus mode widget: quiet-hours toggle that suppresses the bar.
- System monitor widget: CPU, GPU, RAM with a live area chart (PDH counters).
- Notification widget: toast notification listener (requires permission; may be
  limited for non-packaged apps - see LIMITATIONS).
- Clipboard widget: shows clipboard activity (format detection only; off by default).
- USB widget: device connect/disconnect events.
- Screenshot widget: watches Pictures\Screenshots for new captures.
- Always-on-top, click-through outside the pill, per-monitor DPI aware, blur-behind
  acrylic effect, spring-physics animations at 60/120 fps.
- System tray icon with menu: toggle bar, settings, quit.
- First-run setup wizard (monitor choice, hotkeys, theme, features).
- Settings window: hotkeys, widget toggles, theme (dark/light/auto), opacity,
  animations, startup with Windows, reset to defaults.

HOTKEYS (defaults - configurable in Settings)
---------------------------------------------
Ctrl+Alt+B   Toggle bar visibility
Ctrl+Alt+M   Show media widget
Ctrl+Alt+T   Show timer widget

SETTINGS & LOGS
---------------
Settings:  %AppData%\WindowsDynamicBar\settings.json
Logs:      %AppData%\WindowsDynamicBar\logs\
The app is single-instance; launching again focuses the existing instance.

LIMITATIONS (honest notes)
--------------------------
- Brightness control uses WMI internal panel interfaces and works only on machines
  whose GPU/panel drivers expose them.
- The notification listener requires Windows notification access; unpackaged (.exe)
  apps may be denied by Windows. The app degrades gracefully when access is refused.
- Download detection is a heuristic based on large temporary files in the Downloads
  folder; it is not tied into browser download managers.
- Screenshots are detected by watching the Pictures\Screenshots folder.
- Clipboard monitoring detects format presence only (no content is read or stored);
  it is disabled by default for privacy.

RUNNING FROM SOURCE (rebuild)
-----------------------------
Requires: Windows 10 19041+ / Windows 11 x64, .NET 8 SDK, Visual Studio 2022 (optional).

  dotnet build WindowsDynamicBar.sln -c Debug -p:Platform=x64
  dotnet run --project src\WindowsDynamicBar -c Debug -p:Platform=x64

Release self-contained single-file publish:

  dotnet publish src\WindowsDynamicBar\WindowsDynamicBar.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Platform=x64

Installer (requires Inno Setup 6):

  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\WindowsDynamicBar.iss

Tests:

  dotnet build tests\WindowsDynamicBar.Tests -c Debug -p:Platform=x64
  tests\WindowsDynamicBar.Tests\bin\x64\Debug\net8.0-windows10.0.19041.0\WindowsDynamicBar.Tests.exe

ARTIFACTS
---------
release\WindowsDynamicBar.exe        - the app (self-contained, x64)
release\WindowsDynamicBar-Setup.exe  - installer
release\README.txt                   - this file
