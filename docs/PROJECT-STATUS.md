# Windows Dynamic Bar — Project Status & Working Doc

> Location: `C:\Users\swaya\Desktop\TIMEPASS\dynamic island\dynamic-island`
> Last verified: build 0 errors / 0 warnings, tests **14/14 pass**, smoke-tested running.
> Single git commit (`d021233 "yes"`). Not yet pushed to GitHub (auth pending).

---

## 1. What is happening (how the whole thing works)

### Big picture

```
┌─ SERVICES (16) ─────────────────────────┐
│ Media, Volume, Brightness, Battery,     │
│ Network, Bluetooth, Privacy, Clipboard, │
│ Downloads, Usb, Screenshots, Timer,     │
│ Stopwatch, Focus, SysMon, Notifications │
│   each: native API → BarEvent.Raise()   │
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
                   ▼
      Pill (Border, max 440×210 DIP)
      One shared "card" UI, reconfigured per event kind
```

Everything is **event-driven**. Services never touch UI; they `Bus.Raise(BarEvent.Create(kind, ...))`.
`BarWindow` is the only consumer. This is the key seam: to add a widget you touch **2 files**
(a service emit + a `Render*` method), not the whole app.

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
| Tests | ✅ 14/14 | EventBus, springs, hotkeys, settings, formats, tween |
| Launch | ✅ smoke-tested | startup **969 ms**, logs at `%AppData%\WindowsDynamicBar\logs\` |
| Idle perf | ✅ **1.48%** CPU of one core | springs/tweens stop when settled |
| Memory | ⚠️ 257 MB WS | normal for self-contained .NET 8 WPF (runtime baseline) |
| Media widget | ✅ | SMTC session, art via `DataReader`, prev/play/next, seek slider |
| Volume | ✅ | Core Audio events, 60 ms coalesce, slider sets volume |
| Brightness | ✅/⚠️ | WMI internal panels — works only where drivers expose it |
| Battery | ✅ | PowerModeChanged + low-freq poll; charging glyph |
| Network | ✅ | event + poll; SSID/IPv4/adapter; 4-bar signal UI |
| Bluetooth | ✅ | DeviceWatcher; connect/disconnect events; name classification |
| Privacy (cam/mic) | ✅ | device in-use watchers |
| Downloads | ⚠️ heuristic | 1 s poll of Downloads folder temp files |
| USB | ✅ | DeviceWatcher add/remove |
| Screenshots | ✅ | watcher on `Pictures\Screenshots` |
| Timer/Stopwatch | ✅ | 250 ms/100 ms ticks; toast on finish; priority 99 |
| Focus | ✅ | quiets the bar; toggle from card |
| SysMon | ✅ | PDH CPU/GPU + GlobalMemoryStatusEx RAM; perf-mode-scaled interval |
| Notifications | ⚠️ see bugs | listener needs OS permission; likely denied for plain .exe |
| Clipboard | ⚠️ | format-presence only, **off by default** |
| Tray | ✅ | toggle / sysmon / show / settings / logs / about / exit |
| Hotkeys | ✅ | parser + RegisterHotkey; Ctrl+Alt+B/M/T defaults |
| Settings window | ✅ | programmatic rows; debounced auto-save; reset defaults |
| First-run wizard | ✅ | shown once, sets `FirstRunCompleted` |
| Installer | ✅ | Inno Setup 6, per-user, no admin |
| Glyphs | ✅ audited | all 25 code points verified vs Microsoft's official table |
| Single-file publish | ✅ fixed | **requires** `-p:IncludeNativeLibrariesForSelfExtract=true` |

---

## 3. What is wrong (known bugs / deviations)

1. **Hotkey text mismatch** — `FirstRunWindow.xaml` + `TrayController` say *"Ctrl+Alt+Space toggle"*,
   but the actual default in settings is **Ctrl+Alt+B**. One of them must change (cheap fix:
   make defaults Ctrl+Alt+Space everywhere, or edit the two strings).
2. **Notifications from a plain .exe are unreliable** — `UserNotificationListener` wants
   package identity (MSIX) or explicit user grant. Code degrades gracefully (logs + disables),
   but expect "notification API unavailable" until the app is MSIX-packaged or the user grants
   access. Same for our own toasts (`ToastSender` AUMID registration helps but is not guaranteed).
3. **Downloads heuristic misses browser-internal downloads** (Chrome/Firefox keep data inside
   their own profiles until done; we only see temp files in `Downloads`). Edge cases: files
   smaller than threshold, other folders.
4. **Brightness** only works on machines whose panels expose WMI internal interfaces
   (`WmiMonitorBrightness`); external monitors will show nothing useful.
5. **Network signal bars are decorative** — no real RSSI is read; bars reflect link state, not dBm.
6. **Two copies of the project exist** — `C:\Users\swaya\WindowsDynamicBar` (old) and this one.
   They are byte-identical right now, but will diverge. **Delete or archive the old one.**
7. **Release artifacts are not in git** (163 MB exe > GitHub's 100 MB limit; 48 MB setup also
   excluded on purpose). They must be rebuilt: publish command + ISCC command are in
   `release/README.txt`.
8. **Drag persists X only** — vertical position is always top (`Margin 40,6`). Fine for an
   island, but there is no "monitor choice" UI for multi-monitor users.
9. **Settings window is functional but plain** — no icons, no live preview, no per-widget
   ordering; built programmatically in `SettingsWindow.xaml.cs` (`Populate()`).
10. **GPU counter** `GPU Engine(*)\Utilization Percentage` can fail on some drivers → GPU row
    stays 0% (logged "GPU counters unavailable"; CPU/RAM still work).
11. **`release/README.txt` hotkey line** may also drift from actual defaults — treat
    `SettingsModel.cs` as the single source of truth.

---

## 4. What is pending

- [ ] **Push to GitHub** — repo committed locally; blocked on `gh auth login` / PAT.
- [ ] Write this doc into the repo's `docs/` ✅ (this file) and a top-level `README.md` for GitHub.
- [ ] Fix hotkey label mismatch (#1 above).
- [ ] Decide notification strategy: MSIX package vs AUMID-only (affects installer story).
- [ ] Delete/archived old copy at `C:\Users\swaya\WindowsDynamicBar` (#6).
- [ ] Light-theme polish (dark works; light exists in settings but visual audit not done).
- [ ] End-to-end manual test pass on a second machine / clean VM (installer + wizard + hotkeys).
- [ ] Per-monitor support (bar currently centers on primary work area).

---

## 5. What can be improved (prioritized)

**P0 – correctness**
- Fix hotkey strings; add a tiny "hotkeys" section screenshot to README.
- MSIX packaging branch (unlocks reliable notifications + toasts).
- CI: GitHub Actions windows-latest → `dotnet build` + run tests (5 lines of YAML).

**P1 – UX quality**
- Collapsed orb: show live mini-info (♪ spinning art when media plays, battery %, timer countdown).
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
| Ctrl+Alt+B | Toggle bar |
| Ctrl+Alt+M | Media widget |
| Ctrl+Alt+T | Timer widget |

*(Tray + FirstRun wizard currently display Ctrl+Alt+Space for toggle — bug #1.)*

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
│   ├── Settings/SettingsModel.cs     ← ALL DEFAULTS / FEATURE FLAGS
│   └── Settings/StartupRegistration.cs
├── Interop/
│   ├── BlurHelper.cs                 DWM blur region per frame
│   ├── CoreAudio.cs                  volume COM interop
│   └── NativeMethods.cs              user32/dwm/winmm P/Invokes, WM_NCHITTEST
├── Services/                        ← one file per subsystem; emit BarEvents
│   ├── Audio/VolumeBrightnessServices.cs
│   ├── Media/MediaService.cs
│   ├── Notifications/NotificationServices.cs  (+ ToastSender)
│   ├── Privacy/PrivacyDownloadServices.cs     (privacy, downloads, usb, screenshots)
│   ├── SystemInfo/SystemServices.cs           (battery, network, bluetooth)
│   ├── SystemMonitor/SystemMonitorService.cs  (PDH counters)
│   └── Timers/TimerServices.cs                (timer, stopwatch, focus)
└── UI/
    ├── BarWindow.xaml                card layout + styles (glyphs are verified codes!)
    ├── BarWindow.xaml.cs             expand/collapse, RenderEvent + 13 Render* methods
    ├── FirstRunWindow.xaml(.cs)      welcome wizard
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
