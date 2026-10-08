# Windows Dynamic Bar

**A Dynamic-Island-style overlay for Windows 10 / 11.** A compact pill sits at the top of your screen and expands only when something matters — then gets out of your way.

![Version](https://img.shields.io/badge/version-1.1.0-blue) ![.NET](https://img.shields.io/badge/.NET-8.0-black) ![License](https://img.shields.io/badge/license-MIT-green)

## Features

- **Media player** — album art, progress scrubbing, prev/play/next (Windows Media Session)
- **Volume & brightness** — drag sliders in the bar, or use your keyboard; device name shown
- **Battery** — percentage, charging state, time remaining; alerts at ≤20 %
- **Network** — SSID, connect/disconnect alerts, **real Wi-Fi signal bars from RSSI** (`wlanapi.dll`); no signal data available → no bars shown (never faked)
- **Bluetooth** — device connect/disconnect alerts with device classification
- **System monitor** — CPU / GPU / RAM live meters; **shows "N/A" when a counter is unavailable** (never a fake 0 %)
- **Telemetry island** — hover the collapsed bar for a CPU / GPU / RAM rail; hover a circle to expand a live 30-second graph panel (the sampler only runs while hovered)
- **Spinning media disc** — album art plays as a rotating CD/vinyl disc in the collapsed pill (decelerates to a stop when paused, crossfades between tracks, static under *Reduce motion*)
- **Privacy indicators** — camera / microphone in use, with the app name when known
- **Timers, stopwatch, focus sessions** — with full transport controls in the bar
- **Downloads, USB, screenshots** — folder watchers raise quiet, dismissable alerts
- **Notifications** — reads Windows notifications when packaged (MSIX); gracefully off otherwise
- **Gaming mode** — tray toggle; only critical alerts (privacy, timers, USB, low battery) open the bar while you play

## Getting started

| Method | File |
|---|---|
| **Installer (recommended)** | `release\WindowsDynamicBar-Setup.exe` |
| **Portable** | `release\WindowsDynamicBar.exe` (single file, no install) |

Run once — a 3-step wizard lets you pick a theme and startup behavior.

### Hotkeys

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+Space` | Toggle the bar |
| `Ctrl+Alt+M` | Show media player |
| `Ctrl+Alt+T` | Start / show a 5-minute timer |

All hotkeys are rebindable in **Settings → General**.

## Themes

**Dark** (default), **Light**, and **Translucent** (extra glassy), plus 9 accent colors — Settings → Appearance. The bar honors *Reduce motion* and a per-monitor DPI-aware anchor with drag-to-reposition.

## Privacy

No telemetry, no accounts, no network calls of any kind. Clipboard monitoring is **off by default** and records only the format of a copy — contents are never read or stored. See [docs/PRIVACY.md](docs/PRIVACY.md).

## Building from source

```powershell
# build (0 warnings, 0 errors expected)
dotnet build WindowsDynamicBar.sln -c Debug -p:Platform=x64

# tests (must print passed: 26, failed: 0)
.\tests\WindowsDynamicBar.Tests\bin\x64\Debug\net8.0-windows10.0.19041.0\WindowsDynamicBar.Tests.exe

# release publish (self-contained single file)
dotnet publish src\WindowsDynamicBar\WindowsDynamicBar.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:Platform=x64

# installer (Inno Setup 6)
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\WindowsDynamicBar.iss
```

Requires: .NET SDK 8.0+, Windows 10 19041+ (x64). Full details in [docs/BUILD.md](docs/BUILD.md).

## Documentation

| Doc | Contents |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Event pipeline, services, rendering model |
| [docs/PRIVACY.md](docs/PRIVACY.md) | Exactly what is and is not collected |
| [docs/BUILD.md](docs/BUILD.md) | Build, test, publish, installer, CI |
| [docs/CHANGELOG.md](docs/CHANGELOG.md) | Release history |
| [docs/PROJECT-STATUS.md](docs/PROJECT-STATUS.md) | Engineering status and roadmap |

## Known limitations

- **GPU %** requires Windows performance counters; when unavailable the bar shows **N/A** (a machine without PDH GPU counters — e.g. some VMs — will not show GPU usage).
- **Wi-Fi bars** need `wlanapi.dll`; on Ethernet or when RSSI is unavailable the card shows connection status only.
- **Brightness** controls internal panels via WMI; external monitors without DDC/CI are skipped.
- **Notification listening** requires a packaged (MSIX/Store) install; the plain .exe cannot register for user notifications.
- **Media control** follows the Windows Media Session (works with Spotify, Edge, foobar2000, etc.); apps that expose no session won't appear.

## License

MIT — see [LICENSE](LICENSE).
