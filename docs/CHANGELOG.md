# Changelog

## Telemetry + media island (workspace extension, rides on 1.1.0)

### Added
- **`SystemTelemetryService`** — PDH CPU / per-engine GPU (grouped per adapter LUID) / RAM sampler
  with a dedicated DXGI adapter enumeration (`IDXGIFactory1`/`IDXGIAdapter1`) for GPU names, LUIDs,
  and dedicated VRAM (paired with `GPU Adapter Memory` counters by adapter index). Rolling 60-sample
  buffers; intervals scaled by `PerformanceMode` (500/800/1000 ms); first tick after 200 ms.
- **Telemetry zone UI** — collapsed pill → hover **rail** (compact CPU / GPU / RAM circles) →
  left-expanding **metric panel** (big %, sub-info, live 30-second sparkline). Window grows to the
  LEFT (pill stays anchored to its screen position; monitor-bounds clamping preserved). Debounced
  open/close per `TelemetryPolicy` (130/150/200 ms); sampler activates only while hovered and shuts
  down when the zone hides.
- **`MediaDisc` control** — album art rendered as a rotating CD/vinyl disc in the collapsed pill,
  decelerating to a stop on pause, crossfading between tracks, honoring *Reduce motion*.
- **GPU honesty in the zone** — adapter names come from DXGI (never hard-coded); "Data unavailable"
  shown when a counter is missing.

### Changed
- `BarWindow` geometry is now zone-aware (`Reposition`, `HitTest`, `RefreshBlur`), window right edge
  stays fixed while the zone grows left; pill stays browser-anchored.
- Hovering the pill while `FeatureSystemMonitor` is on opens the telemetry zone instead of the
  quick-panel (unless a timer/focus/stopwatch is running or media is playing).

### Fixed
- Corner radius could momentarily go negative during spring settle ("not a valid value for property
  'CornerRadius'" log spam) — now clamped at 0 in `UpdateShape()`.

### Tests
- Suite expanded 20 → 26: `TelemetryHistory` overflow/order, `TelemetryFormat`, `GpuEngineParser`
  (parse + per-LUID grouping + clamp), `DiscRotation` (converge / decelerate / no-jump),
  `TelemetryPolicy` transitions.

## 1.1.0 (2026-10-09)

### Added
- **Theme system** — Dark, Light, and Translucent bar themes plus 9 accent colors (Settings → Appearance).
- **Settings sidebar** — redesigned Settings with page navigation: General, Appearance, Widgets, Privacy, Diagnostics.
- **Diagnostics page** — version/OS/.NET/arch info, live service status, open/clear logs, copy-diagnostics-to-clipboard.
- **First-run wizard** — 3-step onboarding (welcome → theme & startup → quick-start hotkeys).
- **Gaming mode** — tray toggle; only critical alerts (privacy, timers, stopwatch, focus, USB, toasts) open the bar.
- **Wi-Fi signal bars** — real RSSI readout via `wlanapi.dll`; bars hidden when signal data is unavailable (never fabricated).
- **Tray hotkey label** — tray menu reads the live hotkey from settings instead of a hard-coded string.
- **Reduce motion** honored for all bar animations (was already implemented; now surfaced and tested).

### Changed
- **System monitor honesty** — CPU/GPU counters carry availability flags; bar shows **N/A** (not a fake 0 %) when a counter is unavailable.
- **Collapsed-state priority** — running timer → focus session → stopwatch → playing media → system stats → idle.
- **Window anchoring** — `WM_DISPLAYCHANGE` re-anchors the pill and clamps the saved drag offset to the new monitor bounds; invalid monitor index falls back to primary.
- Version bumped 1.0.0 → 1.1.0 across csproj and `App.InfoVersion`.

### Fixed
- **Theme application** — compiled App.xaml brushes are frozen, so mutating `.Color` threw "read-only state" and themes silently never applied. ThemeService and the accent-color setter now *replace* the resource instead of mutating.
- **Second-instance exit crash** — a second launch called `Mutex.ReleaseMutex()` without owning it → unhandled `ApplicationException` during shutdown. Exit now tracks ownership and skips release/logging for instances that never started.
- Settings window resize is no longer restricted by stale content bounds.
- Diagnostics report now includes a labeled `Services:` section.

### Tests
- Suite expanded 14 → 20: RSSI mapping, counter-availability flags, settings round-trip (monitor index / reduce-motion / theme), theme enum serialization, diagnostics report content, gaming-mode priority threshold.

## 1.0.0 (2026-09)

Initial release: media, volume, brightness, battery, network, Bluetooth, system monitor, privacy indicators, timers/stopwatch/focus, downloads, USB, screenshots, notifications, tray, hotkeys, single-instance pipe, spring animation.
