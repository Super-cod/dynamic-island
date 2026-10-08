# Architecture

## Overview

WPF (.NET 8, `net8.0-windows10.0.19041.0`, x64) single-window overlay. No MVVM framework — plain code-behind with a clear service layer.

```
App (composition root)
 ├─ 17 services ── raise events ──▶ EventBus (priority queue)
 ├─ BarWindow  ◀── Drain() ─────── EventRenderMap ──▶ Render* methods
 │   └─ Telemetry zone: SystemTelemetryService.TelemetryUpdated ─▶ UI thread (Dispatcher.BeginInvoke)
 ├─ TrayController (WinForms NotifyIcon)
 └─ SettingsWindow / FirstRunWindow (owned windows)
```

## Event pipeline

1. **Producers** — services (`SystemMonitorService`, `NetworkService`, `VolumeService`, `TimerService`, …) call `EventBus.Publish(evt)`.
2. **EventBus** (`Core/Events/EventBus.cs`) — priority queue with **coalescing** (same `BarEventKind` replaces), **expiry** (stale events dropped), and an **Idle** event when the queue drains.
3. **Priority** — `int`, same scale as `BarEventKind`: routine values 10–55, critical ≥ 60 (`Privacy` = 60, `Timer` = 75, `Usb` = 85, `Toast` = 90).
4. **Consumer** — `BarWindow.OnBusEvent` runs on the dispatcher. When **gaming mode** is on, events with `Priority < 60` are silently dropped (critical alerts still open the bar).
5. **RenderEvent** maps kind → `RenderXxx(evt)` which mutates named parts in `BarWindow.xaml` and runs spring animations.

## Rendering model

- **Pill** — one borderless `Window` (`BarWindow`), max 440×210 DIP, top-center anchored.
- **Click-through** — `WM_NCHITTEST` returns `HTTRANSPARENT` except over interactive children.
- **Collapsed priority** (what fills the pill when idle): running timer → focus → stopwatch → playing media → system stats → "Dynamic" idle glyph.
- **Animation** — `SpringAnimator` (damped spring, retargetable) + `TweenAnimation` (eased linear). Both disabled/simplified when `ReduceMotion` is set.
- **Theming** — shared brushes in `App.xaml`; `ThemeService.Apply(theme, opacity)` mutates them at runtime. All consumers use `DynamicResource`.

## Services (17)

| Group | Services |
|---|---|
| System | `SystemMonitorService` (CPU/GPU/RAM + availability flags), **`SystemTelemetryService`** (high-frequency sampler for the hover zone: PDH CPU + per-engine GPU grouped by LUID, DXGI adapter names/VRAM, `GlobalMemoryStatusEx` RAM, 60-sample rolling history, perf-mode intervals), `SystemServices` (battery, network, RSSI via `wlanapi.dll`, Bluetooth) |
| Audio/Display | `VolumeBrightnessServices` (Core Audio + WMI) |
| Media | `MediaService` (Windows Media Session) |
| Productivity | `TimerServices` (timer/stopwatch/focus) |
| Privacy | `PrivacyDownloadServices` (camera/mic indicators, clipboard **format only**, download folder watchers) |
| USB/Screen | USB attach/detach, screenshot watcher (in `SystemServices` / `PrivacyDownloadServices`) |
| Notifications | `NotificationServices` (WinRT — **only functional when packaged/MSIX**) |
| Infra | `EventBus`, `SettingsService` (atomic save/load, debounced `DispatcherTimerSink`), `Log`, `StartupRegistration` (Run key / startup task) |

## Telemetry zone (`BarWindow` + `SystemTelemetryService`)

- **State machine** is pure (`Core/Telemetry/TelemetryPolicy.cs`: targets/states/actions, debounce
  130/150/200 ms) and unit-tested; `BarWindow` drives it with an 80 ms cursor probe.
- States: `Hidden → Rail → {Cpu, Gpu, Ram}`. Hover rail circles open the metric panel; leaving the
  region closes it. The window grows LEFT (right edge fixed, pill stays screen-anchored);
  `Reposition`/`HitTest`/`RefreshBlur` are zone-aware.
- Sampler `Activate()`d on zone open, `Deactivate()`d on close — zero idle cost.
- GPU honesty: adapter utilization comes from per-instance `GPU Engine(*)\Utilization Percentage`
  (grouped by adapter LUID, `GpuEngineParser`); names + dedicated VRAM from DXGI (`Interop/Dxgi.cs`),
  paired with `GPU Adapter Memory` counters by adapter index. Shows "Data unavailable", never fakes.

## Media disc (`UI/Media/MediaDisc`)

- Collapsed pill shows album art as a rotating CD/vinyl disc. Spin physics is pure and tested
  (`Core/Media/DiscRotation.cs`): converges to ~45°/s while playing, `exp(-3t)` decay on pause,
  trims when < 0.5°/s. Two `Image` layers crossfade per track; static under *Reduce motion*.

## Multi-monitor

- `MonitorIndex` in settings (-1 = primary) picks the anchor monitor.
- `WM_DISPLAYCHANGE` → `Reposition()` re-anchors; saved drag `offsetDip` is clamped to the new work-area bounds; invalid index → primary.
- Per-monitor DPI handled by WPF with `GetDpiForMonitor` clamping in the drag logic.

## Single instance

Named mutex + named pipe `WindowsDynamicBar_Signal` — a second launch signals the first to toggle the bar, then exits.

## Interop

`Interop/NativeMethods.cs` (user32/shell32/`wlanapi`), `Interop/CoreAudio.cs` (COM endpoint devices), `Interop/Dxgi.cs` (DXGI factory/adapter enumeration — GPU names, LUIDs, dedicated VRAM), `Interop/BlurHelper.cs` (DWM blur). `WinForms` + `System.Drawing` global usings are **disabled** in the csproj — fully qualify `System.Windows.HorizontalAlignment` etc. to avoid CS0104.

## Tests

`tests/WindowsDynamicBar.Tests` — dependency-free harness (`Run`/`Assert`), references the app project, `InternalsVisibleTo` grants access to internals. 26 tests: EventBus semantics, springs/tweens, hotkey parser, settings round-trips, RSSI mapping, counter-availability flags, diagnostics report, gaming-mode threshold, `TelemetryHistory` overflow/order, `TelemetryFormat`, `GpuEngineParser` (parse + per-LUID grouping + clamp), `DiscRotation` (converge/decelerate/no-jump), `TelemetryPolicy` (state transitions + debounce timings).
