# Windows Dynamic Bar

**A Dynamic-Island-style overlay for Windows 10 / 11.** A compact pill sits at the top of your screen and expands only when something matters — then gets out of your way.

![Version](https://img.shields.io/badge/version-1.1.0-blue) ![.NET](https://img.shields.io/badge/.NET-8.0-black) ![License](https://img.shields.io/badge/license-MIT-green)

## Features

- **Media player** — album art, progress scrubbing, prev/play/next (Windows Media Session) and synchronized lyrics
- **Volume & brightness HUD** — live on-screen display when keys or sliders change
- **Battery** — percentage chip on collapsed pill with charging bolt; warning strip at ≤15% when on battery
- **Network** — SSID, connect/disconnect alerts, real Wi-Fi signal bars from RSSI (`wlanapi.dll`), network speed test
- **Bluetooth** — device connect/disconnect alerts with device classification
- **System monitor** — CPU / GPU / RAM live meters and telemetry detail charts
- **Clipboard history** — WM_CLIPBOARDUPDATE capture (no polling), limit 10–50 items, pinned items, DPAPI encryption, password manager exclusions
- **Countdown timer & stopwatch** — presets, custom durations, dot-matrix countdown, search command integration
- **Search & window switcher** — application launcher, file search, math evaluation, and "w " window switcher
- **Quick toggles** — Wi-Fi, Bluetooth, microphone mute, dark mode, do not disturb
- **First-run guide** — 4-step onboarding wizard for features, permissions, and shortcuts
- **Multi-monitor & gaming mode** — Per-monitor DPI V2 aware, autohide in fullscreen games/videos

## Getting Started

### Hotkeys

| Hotkey | Action |
|---|---|
| `Ctrl+Space` | Open Search launcher & Window Switcher |
| `Ctrl+Alt+V` | Open Clipboard History |
| `Ctrl+Alt+Space` | Toggle the island |
| `Ctrl+Alt+M` | Open Media Player |
| `Ctrl+Alt+T` | Start or show 5-minute timer |

All hotkeys can be recorded and customized in **Settings → General** with real-time conflict detection.

### Installation & Packaging

- **Installer (Inno Setup)**: `release\WindowsDynamicBar-Setup.exe` (per-user, no admin required, Start menu shortcut, clean uninstaller)
- **Portable**: `release\WindowsDynamicBar.exe` / `release\WindowsDynamicBar-Portable-win-x64.zip`

Run the release script to build, test, publish, and package:
```powershell
.\build-release.ps1
```

#### Code Signing
To sign the resulting binary with your Authenticode certificate:
```powershell
.\build-release.ps1 -CertPath "C:\path\to\certificate.pfx" -CertPassword "YourPassword"
```
Or set `$env:CODESIGN_CERT` and `$env:CODESIGN_PASSWORD`. If omitted, the build completes unsigned cleanly without errors.

## Building from Source

```powershell
# Restore & build
dotnet build -c Release

# Run test suite
dotnet run --project tests\WindowsDynamicBar.Tests\WindowsDynamicBar.Tests.csproj -c Release

# Publish self-contained ReadyToRun x64
dotnet publish src\WindowsDynamicBar\WindowsDynamicBar.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishTrimmed=false
```

Requires: .NET SDK 8.0+, Windows 10 19041+ (x64). Full developer guide in [DEVELOPMENT.md](DEVELOPMENT.md).

## Documentation

| Document | Contents |
|---|---|
| [DEVELOPMENT.md](DEVELOPMENT.md) | House rules, performance targets, and 10-minute soak test protocol |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | System architecture, event pipeline, rendering model |
| [docs/PRIVACY.md](docs/PRIVACY.md) | Local-only privacy policy, encryption, and data protection |
| [docs/BUILD.md](docs/BUILD.md) | Build instructions and packaging details |
| [docs/CHANGELOG.md](docs/CHANGELOG.md) | Release history |

## License

MIT — see [LICENSE](LICENSE).
