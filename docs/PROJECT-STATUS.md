# Windows Dynamic Bar — Project Status & Working Doc

> Location: `C:\Users\swaya\Desktop\TIMEPASS\dynamic island\dynamic-island`
> Last verified: build 0 errors / 0 warnings, tests **26/26 pass**, v1.1.0.
> Single git commit (`d021233 "yes"`). Not yet pushed to GitHub (auth pending).

---

## 1. What is happening (how the whole thing works)

### Big picture

```
┌─ SERVICES (17) ─────────────────────────┐
│ Media, Volume, Brightness, Battery,     │
│ Network, Bluetooth, Privacy, Clipboard, │
│ Downloads, Usb, Screenshots, Timer,     │
│ Stopwatch, Focus, SysMon, Notifications │
│ + SystemTelemetryService                │
│   each: native API → Bus.Raise() (or    │
│   TelemetryUpdated for the hover zone)  │
└──────────────────┬──────────────────────┘
                   ▼
            EventBus (Core/Events)
   • coalesces same-kind events
   • priority queue (higher = more urgent)
   • expiry timer 250ms drains queue
   • Idle event when queue empties
                   ▼
          BarWindow (UI/BarWindow.xaml.cs)
   • OnBusEvent → RenderEvent(evt)
   • Expand() → springs grow Pill to content
   • idle / Deactivated / fullscreen → Collapse()
   • hover-probe (80ms) drives the telemetry zone
                   ▼
      Pill (Border, max 440×210 DIP)
      One shared "card" UI, reconfigured per event kind
      Left of the pill: Telemetry Zone (rail + metric panel)
```

Everything is **event-driven**. Services never touch UI; they `Bus.Raise(BarEvent.Create(kind, ...))`.
`BarWindow` is the only consumer. This is the key seam: to add a widget you touch **2 files**
(a service emit + a `Render*` method), not the whole app. The telemetry zone is the one exception:
`SystemTelemetryService` raises `TelemetryUpdated` (sampler thread) and `BarWindow` marshals to the
UI thread, so the sampler can live at its own pace and shut down when the zone is hidden.

### Telemetry zone interaction
- Collapsed pill hovered + `FeatureSystemMonitor` on + no priority widget → **rail** (CPU/GPU/RAM
  circles) debounced 130 ms; hover a circle → **metric panel** with live sparkline (120 ms);
  leaving → metric closes to rail (200 ms) then zone hides (180 ms).
- Window grows LEFT while the pill keeps its screen position (right edge fixed); blur + hit-test
  regions follow. Sampler activates on zone open, deactivates on close.

### Composition root
`App.xaml.cs` builds every service in a `try/catch` (one broken subsystem must never kill the bar),
then `Bar.Show()`, then `Tray`. Single-instance via named mutex + pipe `WindowsDynamicBar_Signal`
(second launch pings first → toggles bar). Unhandled UI exceptions are logged and swallowed
(`DispatcherUnhandledException` → `e.Handled = true`).

### Window / hit-testing / visuals
- One invisible 520×250 DIP window, `Topmost`, `ShowActivated=False`, transparent, no taskbar.
- `Pill` Border centered (`Margin="40,6"`), max **440×210 DIP**, corner radius 26.
- **Click-through**: `WM_NCHITTEST` returns `HTTRANSPARENT` outside the pill rect — you can
  click apps under the window but not the pill itself.
- **Blur-behind**: `BlurHelper.UpdateRegion(left, top, w, h, radius, enabled)` — called every
  frame the pill size changes (DWM acrylic region follows the spring animation).
- **Springs**: `Core/Animation/SpringAnimator.cs` — semi-implicit Euler, 60 Hz
  `DispatcherPriority.Render` timer, **stops itself when settled** (that's why idle CPU ≈ 1.5%).
  `TweenAnimation` (120 Hz) for fades.
- Rounded-rect hit region and blur region are recomputed in `UpdateShape()`.

### Widget card anatomy (`BarWindow.xaml`)
One shared layout; each `Render*` method shows/hides named rows:

| Row | Used by |
|---|---|
| `CollapsedPanel` (orb + title + value) | idle pill |
| `HeaderRow` (icon/art + title + sub + big value) | all widgets |
| `ProgressRow` (track + fill + seek slider + times) | media, download, timer |
| `TransportRow` (⏮ ⏸ ⏭) | media |
| `LevelRow` (glyph + slider) | volume, brightness |
| `BarsRow` (4 signal bars) | network |
| `SysMonRow` (CPU/GPU/RAM bars) | system monitor |
| `ActionsRow` (primary/secondary buttons) | timer, stopwatch, focus |
| `FooterText` | Bluetooth/USB device names, file names |

`ResetRows()` collapses everything; `RenderEvent` re-shows what it needs. This keeps XAML small.

### Interaction model
- Event arrives → `Expand()` → springs morph pill to `Max(needed, …)` size → card fades in.
- Auto-collapse: idle timer (`_idleCollapse`, per-event `displayDuration`), window
  `Deactivated`, fullscreen detected → collapse + suppress.
- Click pill → expand/pin; click outside → `UserDismiss` (clears bus + collapses).
- Drag pill horizontally → persists **X offset only** (`CustomOffsetX`), springs it back on release.
- Keyboard: Esc collapses (`OnPreviewKeyDown`); global hotkeys registered per settings.

---

## 2. What is done and verified working

| Area | Status | Notes |
|---|---|---|
| Build | ✅ 0 err / 0 warn | `dotnet build ... -c Debug -p:Platform=x64` |
| Tests | ✅ 26/26 | EventBus, springs, hotkeys, settings, formats, tween, RSSI, availability flags, diagnostics, gaming threshold, telemetry policy/history/parser/disc |
| Launch | ✅ smoke-tested | startup **969 ms** (54:50 instance) / 1744 ms (debug), logs at `%AppData%\WindowsDynamicBar\logs\` |
| Idle perf | ✅ **1.48%** CPU of one core | springs/tweens stop when settled |
| Memory | ⚠️ 257 MB WS | normal for self-contained .NET 8 WPF (runtime baseline) |
| Media widget | ✅ | SMTC session, art via `DataReader`, prev/play/next, seek slider |
| Volume | ✅ | Core Audio events, 60 ms coalesce, slider sets volume |
| Brightness | ✅/⚠️ | WMI internal panels — works only where drivers expose it |
| Battery | ✅ | PowerModeChanged + low-freq poll; charging glyph |
| Network | ✅ | event + poll; SSID/IPv4/adapter; **real RSSI bars** (wlanapi.dll), hidden when unknown |
| Bluetooth | ✅ | DeviceWatcher; connect/disconnect events; name classification |
| Privacy (cam/mic) | ✅ | device in-use watchers |
| Downloads | ⚠️ heuristic | 1 s poll of Downloads folder temp files |
| USB | ✅ | DeviceWatcher add/remove |
| Screenshots | ✅ | watcher on `Pictures\Screenshots` |
| Timer/Stopwatch | ✅ | 250 ms/100 ms ticks; toast on finish; priority 99 |
| Focus | ✅ | quiets the bar; toggle from card |
| SysMon | ✅ | PDH CPU/GPU + GlobalMemoryStatusEx RAM; perf-mode-scaled interval |
| Telemetry island | ✅ | hover rail + metric graph panel; sampler on-demand; DXGI GPU names/VRAM; Policy-driven debounce |
| Media disc | ✅ | spinning CD/vinyl art in collapsed pill; decel/crossfade; Reduce-motion aware |
| Notifications | ⚠️ see bugs | listener needs OS permission; likely denied for plain .exe |
| Clipboard | ⚠️ | format-presence only, **off by default** |
| Tray | ✅ | toggle / sysmon / show / settings / logs / about / exit |
| Hotkeys | ✅ | parser + RegisterHotkey; Ctrl+Alt+Space/M/T defaults; tray label reads live setting |
| Settings window | ✅ | sidebar pages (General/Appearance/Widgets/Privacy/Diagnostics); debounced auto-save; reset defaults |
| First-run wizard | ✅ | 3-step wizard (theme + startup + hotkeys); sets `FirstRunCompleted` |
| Themes | ✅ | Dark / Light / Translucent + 9 accents via ThemeService |
| Gaming mode | ✅ | tray toggle; suppresses events below Privacy priority (60) |
| Installer | ✅ | Inno Setup 6, per-user, no admin |
| Glyphs | ✅ audited | all 25 code points verified vs Microsoft's official table |
| Single-file publish | ✅ fixed | **requires** `-p:IncludeNativeLibrariesForSelfExtract=true` |

---

## 3. What is wrong (known bugs / deviations)

1. ~~**Hotkey text mismatch**~~ — **RESOLVED (1.1.0)**: default is `Ctrl+Alt+Space` in
   `SettingsModel.cs`; tray label reads the live setting; FirstRun wizard matches.
2. **Notifications from a plain .exe are unreliable** — `UserNotificationListener` wants
   package identity (MSIX) or explicit user grant. Code degrades gracefully (logs + disables),
   but expect "notification API unavailable" until the app is MSIX-packaged or the user grants
   access. Same for our own toasts (`ToastSender` AUMID registration helps but is not guaranteed).
3. **Downloads heuristic misses browser-internal downloads** (Chrome/Firefox keep data inside
   their own profiles until done; we only see temp files in `Downloads`). Edge cases: files
   smaller than threshold, other folders.
4. **Brightness** only works on machines whose panels expose WMI internal interfaces
   (`WmiMonitorBrightness`); external monitors will show nothing useful.
5. ~~**Network signal bars are decorative**~~ — **RESOLVED (1.1.0)**: real RSSI via
   `wlanapi.dll`; bars hidden (-1) when no signal data is available.
6. **Two copies of the project exist** — `C:\Users\swaya\WindowsDynamicBar` (old) and this one.
   They are byte-identical right now, but will diverge. **Delete or archive the old one.**
7. **Release artifacts are not in git** (163 MB exe > GitHub's 100 MB limit; 48 MB setup also
   excluded on purpose). They must be rebuilt: publish command + ISCC command are in
   `release/README.txt`.
8. **Drag persists X only** — vertical position is always top (`Margin 40,6`). Fine for an
   island, but there is no "monitor choice" UI for multi-monitor users.
   **Update (1.1.0):** `MonitorIndex` setting + display-change re-anchoring added; offset
   clamped on monitor change. A drag-Y setting is still open.
9. ~~**Settings window is functional but plain**~~ — **RESOLVED (1.1.0)**: sidebar with
   page navigation, header/subtitle, Diagnostics page with live service status.
10. **GPU counter** `GPU Engine(*)\Utilization Percentage` can fail on some drivers → GPU row
    stays **N/A** (1.1.0: honest availability flag instead of fake 0%; CPU/RAM still work). The new
    telemetry zone shows "Data unavailable" for missing GPU counters and reads adapter names from DXGI.
11. ~~**`release/README.txt` hotkey line**~~ — **RESOLVED (1.1.0)**: regenerated with
    Ctrl+Alt+Space; `SettingsModel.cs` remains source of truth.
12. **Telemetry zone GPU** currently shows the first available adapter only (its utilization + VRAM).
    A future wired-up "which GPU" selector is planned; multi-adapter sums are validated in tests but
    not yet surfaced in the panel.

---

## 4. What is pending

- [ ] **Push to GitHub** — repo committed locally; blocked on `gh auth login` / PAT.
- [x] Write `docs/` + top-level `README.md` (1.1.0: README, LICENSE, CHANGELOG, PRIVACY, BUILD, ARCHITECTURE).
- [x] Fix hotkey label mismatch (1.1.0).
- [ ] Decide notification strategy: MSIX package vs AUMID-only (affects installer story).
- [ ] Delete/archive old copy at `C:\Users\swaya\WindowsDynamicBar` (#6).
- [x] Light-theme polish (1.1.0: ThemeService with Dark/Light/Translucent).
- [ ] End-to-end manual test pass on a second machine / clean VM (installer + wizard + hotkeys).
- [x] Per-monitor support (1.1.0: `MonitorIndex` + display-change re-anchor + offset clamp).

---

## 5. What can be improved (prioritized)

**P0 – correctness**
- Fix hotkey strings; add a tiny "hotkeys" section screenshot to README.
- MSIX packaging branch (unlocks reliable notifications + toasts).
- CI: GitHub Actions windows-latest → `dotnet build` + run tests (5 lines of YAML).

**P1 – UX quality**
- Collapsed orb mini-info: spinning media art is done (MediaDisc); battery % / timer countdown still open.
- Light theme pass + system-theme follow (`RequestedTheme` sync with Windows).
- Settings: icons per section, widget enable previews, "test a widget" button.
- Seek slider: live time tooltip on hover; scrubbing feedback.
- Volume/brightness widget: remember last-opened type; wheel-over-pill adjusts.

**P2 – features**
- Multiple monitors: choose target monitor + per-monitor DPI offsets.
- Widget pinning/ordering + "favorites" (persisted in settings).
- Focus modes (work/game/sleep) with schedules; DND integration.
- Now-playing art blur background inside expanded card.
- Keyboard navigation of the card (arrow keys between transport buttons).
- ARM64 build + publish profile.

**P3 – polish**
- Localization (strings resx; bar currently hard-coded English).
- Onboarding tooltips on first events ("this is your media widget…").
- Crash reporter: dump to `%AppData%` on unhandled, offer to open issues page.

---

## 6. What happens at what (timers, priorities, hotkeys)

### Polling / timers (services)

| What | Interval | Where | Notes |
|---|---|---|---|
| EventBus expiry drain | 250 ms | `EventBus.cs` | stops when queue empty |
| Spring animation | 60 Hz | `SpringAnimator` | **stops when settled** |
| Tween/fade | 120 Hz | `TweenAnimation` | stops on completion |
| Fullscreen check | 1500 ms | `BarWindow` | hides bar over fullscreen apps |
| Timer service tick | 250 ms | `TimerServices` | countdown |
| Stopwatch tick | 100 ms | `TimerServices` | |
| Focus break reminder tick | 500 ms | `TimerServices` | |
| SysMon sample | 200 ms + scaled | `SystemMonitorService` | scaled by `PerformanceMode` |
| Telemetry sampler | 500/800/1000 ms | `SystemTelemetryService` | perf-mode scaled; runs only while the zone is hovered |
| Zone hover probe | 80 ms | `BarWindow` | stops when zone feature is off / expanded |
| Media timeline | 1000 ms | `MediaService` | only with active session |
| Battery poll | low-freq | `SystemServices` | + `PowerModeChanged` events |
| Downloads scan | 1000 ms | `PrivacyDownloadServices` | heuristic |
| Download progress | 1000 ms | `PrivacyDownloadServices` | while file grows |
| Volume change coalesce | 60 ms | `VolumeBrightnessServices` | |
| Brightness coalesce | 300 ms | `VolumeBrightnessServices` | skip <0.5% deltas |
| Notification debounce | 2000 ms | `NotificationServices` | max 1 event / 2 s |
| Settings save debounce | `DispatcherTimerSink` | `SettingsWindow` | |

### Event priority (higher wins, used by EventBus queue)

| Priority | Kind |
|---|---|
| 99 | Timer finished / focus-break toast |
| 70 | Volume |
| 65 | Screenshot |
| 60 | Download complete |
| 58 | Timer running updates |
| 50 | Notification, clipboard-ish generic |
| 45 | Bluetooth connect/disconnect |
| 40 | USB |
| 15 | Download progress ticks |
| 5 | SysMon samples |

### Global hotkeys (defaults from `SettingsModel.cs` — source of truth)

| Combo | Action |
|---|---|
| Ctrl+Alt+Space | Toggle bar |
| Ctrl+Alt+M | Media widget |
| Ctrl+Alt+T | Timer widget |

*(All rebindable in Settings → General; tray label reads the live value.)*

### Config & data locations

| Thing | Path |
|---|---|
| Settings | `%AppData%\WindowsDynamicBar\settings.json` |
| Logs | `%AppData%\WindowsDynamicBar\logs\wdb-YYYYMMDD.log` |
| Startup entry | `HKCU\...\Run\WindowsDynamicBar` |
| Toast AUMID | `HKCU\Software\Classes\AppUserModelId\WindowsDynamicBar.Toast` |

---

## 7. File map (where to work on what)

```
src/WindowsDynamicBar/
├── App.xaml.cs                  composition root, service wiring, single-instance
├── Core/
│   ├── Animation/SpringAnimator.cs   springs + tween (add easing here)
│   ├── Events/EventBus.cs            queue, coalesce, priority, Idle
│   ├── Logging/Log.cs                file logger
│   ├── Media/DiscRotation.cs         pure disc spin physics (tested)
│   ├── Settings/SettingsModel.cs     ← ALL DEFAULTS / FEATURE FLAGS
│   ├── Settings/StartupRegistration.cs
│   └── Telemetry/                    pure cores (tested)
│       ├── TelemetryHistory.cs       fixed circular buffer
│       ├── TelemetryFormat.cs        Bytes/Frequency/Percent
│       └── TelemetryPolicy.cs        zone state machine + debounce timings
├── Interop/
│   ├── BlurHelper.cs                 DWM blur region per frame
│   ├── CoreAudio.cs                  volume COM interop
│   ├── Dxgi.cs                       DXGI factory/adapter enum (names, LUID, VRAM)
│   └── NativeMethods.cs              user32/dwm/winmm P/Invokes, WM_NCHITTEST
├── Services/                        ← one file per subsystem; emit BarEvents
│   ├── Audio/VolumeBrightnessServices.cs
│   ├── Media/MediaService.cs
│   ├── Notifications/NotificationServices.cs  (+ ToastSender)
│   ├── Privacy/PrivacyDownloadServices.cs     (privacy, downloads, usb, screenshots)
│   ├── SystemInfo/SystemServices.cs           (battery, network, bluetooth)
│   ├── SystemMonitor/SystemMonitorService.cs  (PDH counters - event card)
│   ├── SystemMonitor/SystemTelemetryService.cs (sampler for the hover zone)
│   ├── SystemMonitor/GpuEngineParser.cs        (per-LUID PDH GPU engine grouping)
│   ├── SystemMonitor/TelemetryTypes.cs         (snapshot records)
│   └── Timers/TimerServices.cs                (timer, stopwatch, focus)
└── UI/
    ├── BarWindow.xaml                card layout + styles (glyphs are verified codes!)
    ├── BarWindow.xaml.cs             expand/collapse, RenderEvent + 13 Render* methods,
    │                                 telemetry-zone probe/state machine + graph drawing
    ├── FirstRunWindow.xaml(.cs)      welcome wizard
    ├── Media/MediaDisc.xaml(.cs)     rotating CD/vinyl artwork control
    ├── SettingsWindow.xaml(.cs)      programmatic settings builder (Populate)
    └── TrayController.cs           tray menu + pipe server (toggle)
```

### How to add a new widget (recipe)
1. Add `BarEventKind.MyThing` (Core/Events).
2. In your service: detect condition → `Bus.Raise(BarEvent.Create(kind, title, valueText, priority, displayDuration))`.
3. In `BarWindow.xaml.cs`: `case BarEventKind.MyThing: RenderMyThing(evt);` and write `RenderMyThing`
   (call `ResetRows()` pattern, show needed rows, set texts).
4. Add feature flag in `SettingsModel` + toggle row in `SettingsWindow.Populate()`.
5. Add glyph constant to `Ic*` block (verify code point against Microsoft's Fluent icon table!).

---

## 8. Commands

```powershell
# build
dotnet build WindowsDynamicBar.sln -c Debug -p:Platform=x64

# test (run exe directly — dotnet test looks in wrong RID folder)
tests\WindowsDynamicBar.Tests\bin\x64\Debug\net8.0-windows10.0.19041.0\WindowsDynamicBar.Tests.exe

# run
dotnet run --project src\WindowsDynamicBar -c Debug -p:Platform=x64

# publish (self-contained single-file; the flag is REQUIRED)
dotnet publish src\WindowsDynamicBar\WindowsDynamicBar.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:Platform=x64

# installer (Inno Setup 6; per-user install at %LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe)
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\WindowsDynamicBar.iss
```
