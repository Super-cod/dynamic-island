# Privacy

Windows Dynamic Bar is a local-only desktop utility. **It makes no network requests of its own and sends no data anywhere.**

## What is stored locally

| Data | Where | Default | Purpose |
|---|---|---|---|
| Settings (hotkeys, theme, toggles, positions) | `%APPDATA%\WindowsDynamicBar\settings.json` | written on first run | your preferences |
| Logs (diagnostic text) | `%APPDATA%\WindowsDynamicBar\logs\` | on | troubleshooting |
| First-run completion flag | `settings.json` → `FirstRunCompleted` | on | don't show wizard again |

Nothing else is written to disk.

## What is read, in memory only

- **Audio / display**: current volume, mute state, brightness (via Core Audio and WMI) to render the sliders.
- **Battery**: percentage, charge state, remaining time (via `WM_POWERBROADCAST` and WMI).
- **Network**: SSID, connection state, and **Wi-Fi RSSI (signal strength)** via `wlanapi.dll` to draw signal bars. Interface name/ MAC address are **not** collected.
- **Bluetooth**: connect/disconnect events and device name (via WMI) for the connect alert.
- **System performance**: CPU/GPU/RAM counters (PDH/WMI) for the monitor meters.
- **Media session**: title/artist/album art URL/position from the Windows Media Session (the same API Spotify's media overlay uses).
- **Privacy indicators**: whether camera/microphone are open and which app holds them (WMI `Win32_PnPEntity` + session enumeration) so you can be alerted.
- **Clipboard**: *format only* (e.g. "Text", "File drop") when clipboard monitoring is **enabled**. Clipboard **contents are never read, logged, or stored**. Monitoring is **off by default**.
- **Notifications**: only if the app is packaged (MSIX/Store) and user notifications are allowed — used to show notification previews in the bar. A plain `.exe` install cannot register for notifications at all.

## What is never collected

- No telemetry, analytics, or crash reporting
- No account, license key phoning home, or ads
- No file contents (screenshots/download watchers observe *file names in watched folders only*, never file bytes)
- No keystroke logging — hotkeys are global hotkey registrations, not a keylogger
- No network connections initiated by the app (verify with any firewall — you'll see zero outbound rules)

## Uninstall

Run **WindowsDynamicBar → Uninstall** from Start Menu (installer), then delete `%APPDATA%\WindowsDynamicBar\` to remove settings and logs.
