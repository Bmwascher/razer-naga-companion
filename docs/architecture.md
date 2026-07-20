# Razer Naga Companion — architecture reference

The deep subsystem reference. `CLAUDE.md` at the repo root is the entry point (rules, commands,
directory, roadmap); this file holds the how-and-why detail it points at. Per-feature design
history lives in `docs/superpowers/specs/`.

## The gating constraint, mechanically

The app must stay lightweight (~0% idle CPU, ~23 MB private working set) with **zero mouse
input-latency / feel regression**. How that is upheld — keep it this way:

- Talk to the mouse only via HID **feature reports** (USB control endpoint). Never claim the
  OS-owned input collection. Open zero-access + `FILE_SHARE_READ|WRITE` (passive client) so the
  interrupt IN endpoint carrying movement/clicks is undisturbed.
- Device I/O is on-demand or infrequent: battery poll cadence floor **15 s**; read/set DPI or
  buttons only on explicit user action — **never poll DPI or buttons** (button bindings live in an
  onboard slot the firmware holds itself, so no re-apply/verify path exists). No new background
  timers/threads: the lone *persistent* timer is the battery poll; the USB **device-change hook**
  (`DeviceChangeWatcher` → debounced refresh) is event-driven and idle-free — a one-shot
  `Task.Delay`, not a timer. The 15 s floor is a UI-input clamp (`DashboardViewModel.ApplyTo`,
  `Math.Max(15, …)`) — `BatteryMonitor.ScheduleNext` reads the cadence unclamped, so don't add a
  poll-cadence path that bypasses that clamp (a hand-edited `settings.json` already can).
- Blocking `HidD_*Feature` calls run **off the UI thread** (`Task.Run`); battery + DPI serialize
  through one shared lock (`BatteryMonitor._readLock`, a `SemaphoreSlim`). The **Dashboard** window
  releases on close (`StateChanged` unsubscribed + `_dashboard = null`) followed by a one-shot
  post-close trim (double GC — the window is finalizable, one GC only queues it — then a
  working-set trim, 2 s after Closed, skipped on reopen; without it WPF idles at ~85 MB after the
  first dashboard open) so idle returns to baseline. The **popup** is a cached singleton that only
  hides — don't make it release-on-close (re-creating it per tray click adds cost).
- Acceptance gates (not aspirations): footprint back to baseline + measured input latency
  unchanged before/during/after operations. Binds ALL phases, especially button remapping.
- Origin: `docs/superpowers/specs/2026-06-20-naga-settings-dpi-design.md` §3.1.

## Smart App Control on the dev machine

SAC enforcement can block *loading* a freshly-built unsigned binary by hash (`0x800711C7`) —
building still works (the signed SDK compiles), but launching a fresh Release exe (or loading a
fresh Debug DLL) may be vetoed until the hash earns cloud reputation. For dev runs, launch the
Debug build via the signed host (`& $dotnet "…\bin\Debug\…\NagaBatteryTray.dll"`); .NET builds are
deterministic (same source → same hash → same verdict), so add `-p:Deterministic=false` to mint a
new hash and retry until one clears. The installed Release exe never shows a console; `dotnet …dll`
does, so launch it `-WindowStyle Hidden`.

**Run-at-login was a SAC casualty (fixed 2026-06-25):** the old HKCU `Run` key fired ~52 s into
boot, before the network was up, so SAC's ISG cloud-reputation lookup failed *closed* and vetoed
the load (CodeIntegrity events 3033/3077) — even though the *same* hash launches fine once online.
It is NOT a permanent veto of a reputable hash, just a boot-time race. Fix: run-at-login is a logon
scheduled task with a `PT1M` delay (see `StartupRegistration.cs`), so it launches after the machine
is online. Keep this — don't revert to the Run key. A rebuilt exe still gets a new (unproven) hash,
so prefer keeping the known-good installed exe over republishing just to re-register startup.

## HID layer (`src/NagaBatteryTray/Hid/`)

### `RazerProtocol.cs`

Pure 90-byte report build/parse + XOR CRC (over bytes `[2..87]`).

- **Battery** (class 0x07) and **DPI** (class 0x04, big-endian X/Y at reply `[10..13]`, VARSTORE
  persist) via private `BuildReport`/`ValidateReply`; public API is
  `BuildFeatureBuffer`/`BuildGetDpiBuffer`/`BuildSetDpiBuffer` + `ParseReply`/`ParseDpiReply`.
  `ParseDpiReply` treats DPI outside `100..30000` as **Failed** (guards wrong-layout firmware
  replies).
- **Buttons** (class 0x02, set 0x0c / get 0x8c, args
  `[profile,buttonId,hypershift,category,len,d0..d4]`); `ParseButtonReply` guards via the **echo
  check** — the reply must echo the request's profile/buttonId/hypershift.
- **Profile lifecycle** (class 0x05: list 0x81 / create 0x02 / delete 0x03), **device mode**
  (0x00/0x84 get, 0x00/0x04 set) — all hardware-verified 2026-07-11 (spike, button-remap spec §6).
- **Active-slot get 0x05/0x84** (ds 0x06, slot number at reply arg[0]; echo-checked parse) and
  **set 0x05/0x04** (ds 0x01, arg[0]=slot; **persists across power-cycle** — full bottom-button
  parity), both hardware-verified 2026-07-18 (`--probe-profile` sweep + `--set-test`; in no
  reference repo — our own discovery). Consumed by the Profile card
  (`AppHost.RefreshProfileAsync` read on open/refresh, `SwitchProfileAsync` write-on-action via
  the card's slot dropdown) and the `--probe-profile` tools.

### `ButtonBinding.cs`

The button model: `ButtonBinding` (+`ToWire`; the Default marker itself has no wire form — callers
map it to the factory binding first), `RawButtonAction`, `ProfileList`, and `NagaV2ProButtons`
(grid ids `0x40..0x4b` physical order; `FactoryBindingForPosition` = the digits row `1..9 0 - =` —
a **freshly created onboard slot is EMPTY** (hardware-observed), so "Default" writes the baked-in
factory action).

### `RazerDevice.cs` (implements `IRazerDevice.cs`)

Zero-access `CreateFile` + `HidD_Set/GetFeature`.

- `ExchangeAsync` transport (SET→`SetReadDelayMs` wait→GET, busy-retry, close-on-failure) for
  battery/DPI/writes + the tid probe, and `FastReadAsync` for idempotent onboard-memory READS
  (button/profile queries): early-poll doubling ladder 50→400 ms, ready = completed status +
  class/cmd echo — the grid sweep's 12 reads land in ~a second instead of ~5 s.
- `EnsureConnectedAsync` picks the **live** collection (the one whose
  `GetMaxFeatureReportLength()==91`, **not** usage page 0xFF00 — none exposed) by trying candidates
  **wired `0x00A7` first, then wireless `0x00A8`**, and **verifying each actually answers a battery
  query** before committing: the dock's wireless receiver stays enumerated when the mouse switches
  to wired, so first-enumerated alone would lock onto a dead collection (this was the wired/USB-C
  "no response" bug, `932398e`).
- A read returning null drops the handle so the next poll re-selects (self-heals on plug/unplug).
  Explicit refreshes (`RefreshNowAsync`) additionally call **`Reset()`** so they re-select
  *immediately* — the null-drop self-heal is too slow on a wired plug, where the stale wireless
  handle keeps answering *not-charging* (`c6c4b9a`). The active link is surfaced
  (`BatteryReading.IsWired` → `DeviceState.Wired` → popup top-right "Wired"/"Wireless"/"On
  battery").
- VID `0x1532`. The Razer **transaction id is auto-probed** (`ResolveTransactionIdAsync` over
  `TransactionIdProbeSet`) and cached; every battery/DPI call gates on `tid != 0`, returning
  Absent/null silently until it resolves.

## Monitoring (`Monitoring/BatteryMonitor.cs`)

Poll timer + arming state machine; takes `IRazerDevice`; battery poll + DPI/button/profile
pass-throughs serialize on one `_readLock` (poll skips if busy, the pass-throughs block). The poll
itself does battery I/O only. `RefreshNowAsync` (manual button, wake, device-change) calls
`_device.Reset()` first to re-select the active interface; the frequent background poll reuses the
handle for efficiency.

## Button remapping model (Phase B 2026-07-11, reworked v2.3 2026-07-19)

Bindings live in the mouse's **onboard slots**; the firmware holds them through power-cycles — no
re-apply, no sentinel poll. Since v2.3 **every slot is editable in place** (the user reversed the
Phase B never-write-user-slots rule once byte-for-byte reads shipped — button-remap spec §13.2):

- Writes target the **ACTIVE (displayed) slot**, and safety is **snapshot + raw undo** — before
  any overwrite the chip snapshots the button's on-mouse raw action (from the grid sweep), and ↶
  restores it verbatim via `AppHost.RestoreRawAsync`, which round-trips even Synapse macros/mouse
  functions the app can't model. **Never remove that snapshot/undo path.**
- The active slot is readable AND settable (`0x05/0x84`/`0x05/0x04` — the Profile card's
  ↻/dropdown); the mouse's bottom button (LED colour = slot: white/red/green/blue/cyan) remains
  the hardware fallback.
- Write path is `AppHost.WriteBindingAsync` → `WriteVerifiedAsync` (per-chip instant apply):
  resolve active slot → write → read-back verify; "Default" writes the factory action
  (deterministic, the repair path).
- Nothing is persisted app-side; the old adopt/seed machinery and the settings `ButtonBindings`
  table are retired (properties kept for JSON back-compat).

## Settings (`Settings/`)

`AppSettings` + `ISettingsStore`/`JsonSettingsStore`. JSON at `%APPDATA%\NagaBatteryTray\settings.json`
(Roaming — **not** the install dir under `%LOCALAPPDATA%`); holds cadences, low-battery
threshold/notify, `SetReadDelayMs` (SET→GET wait, default 400), the cached transaction id,
app-side DPI presets, and app-side profile-slot names (`ProfileNames`, keyed by slot number — the
firmware stores no names, so a rename is a dashboard label only). `ButtonBindings`/`OnboardSlot`
survive as properties for JSON back-compat but are no longer read or written (v2.3 — the firmware
holds bindings, the grid reads hardware). Corrupt file → silently resets to defaults.

## UI shell (`Ui/`)

### `IconRenderer`

Two switchable styles (`TrayIconStyle` setting, switchable live from the settings overlay):

- Default **coin gauge** — a filled dark disc (`Color.FromArgb(205, 16, 18, 22)`) covers the
  canvas edge-to-edge, its **rim is the battery-level ring** (hairline 5.5% of render, floored at
  1 final px so AA can't dash it; track a faint full circle, arc colored by level —
  green/amber/red, green while charging), and the digits sit **inside** the coin, always
  **white**, laid out from their **ink bounds via a `GraphicsPath`** at 57% of render height (46%
  for 3-digit "100") with the width cap **derived from the ring's inner radius**, so the ink box
  geometrically never touches the ring. Composition adopted from HoroTW/RazerBatteryTray (its own
  icons measure 41% digits / 6% ring) after the earlier ring-behind-full-height-digits design read
  as stray pixels at real tray sizes.
- **"Text"** — the classic look, no coin/ring, digits fill the whole icon height and are colored
  by battery level instead of white.

Both styles render 4x supersampled and are **downscaled in-app** (HighQualityBicubic) to the exact
`SM_CXSMICON` size — handing the shell an oversized icon lets its low-quality resampler mush the
digits at 16 px.

### `TrayIcon` / `TrayIconController`

Raw **Shell_NotifyIcon** keyed by a **stable GUID derived from the exe path** so Windows persists
the taskbar position across restarts/sleep (fixes the position-reset bug); the path-derived GUID
also differs between the installed exe and a dev-host run, dodging the shell's
one-GUID-per-executable conflict. Uses `NOTIFYICON_VERSION_4`, so handle **only**
`NIN_SELECT`/`WM_CONTEXTMENU`, **not** the duplicate raw `WM_LBUTTONUP`/`WM_RBUTTONUP` — or every
click double-fires. `TrayIconController` wraps it (tray menu item is **"Dashboard"**, renamed from
"Settings").

### `PopupWindow` (+`PopupViewModel`)

Cached singleton that **re-parks off-screen before every show** and positions in **physical px**
to dodge mixed-DPI bugs and a reposition flash; styled via the `App.*` DynamicResource theme keys
(the old app-profile line is gone since v2.3); its second button opens the dashboard.

### Support pieces

`DeviceChangeWatcher` (hidden top-level window; `WM_DEVICECHANGE`/`DBT_DEVNODES_CHANGED` →
debounced refresh); `Notifications` (low-battery toast); `DoubleIntConverter`. `AppHost.cs` owns
lifecycle (monitor, tray, device-change hook); `Program.cs` is single-instance (named Mutex).
Run-at-login is `Startup/StartupRegistration.cs` (a **delayed-logon scheduled task** named
`NagaBatteryTray`, registered via `schtasks /XML`; the exe self-registers through the
`--enable-startup` switch that `install.ps1` calls). `Diagnostics/ProbeCommand.cs` backs
`--probe`/`--probe-dpi`/`--probe-dock`/`--probe-buttons` (`[--reset|--slot-test]`)/`--probe-profile`.
`KeyToHidUsage` maps WPF Key ↔ HID usage + modifier bits (used by the dashboard's key capture).

## Dashboard (`Ui/Dashboard/`)

Replaces the deleted `SettingsWindow`/`SettingsViewModel`/`ButtonRowViewModel`.

- `DashboardWindow` — a WPF-UI `FluentWindow` shell; releases on close (the monitor's
  `StateChanged` subscription is removed and the field nulled, same release-on-close discipline
  the old Settings window had).
- `MouseStageView` — the stage is the user's **product render** of the thumb panel
  (`Assets/naga-thumb.png`, a csproj `<Resource>`; grayscale blank-key AI edit of the real photo,
  decoded at `DecodePixelWidth=560` ≈ 2 MB only while the dashboard is open) clipped to
  `ShellOutline` — a silhouette **traced from the render's own pixels** (adaptive-threshold edge
  scan; the shadowed lower-left tail is a synthesized closure to the measured tip). The SAME
  geometry is stroked in four widening `App.AccentSoft` bands as a **rim glow behind the image**
  (strokes only, per the no-effects rule) so the glow hugs the visible cutout edge exactly — rest
  0.5 opacity, animating to 1.0 while any key captures (`DashboardViewModel.AnyCapturing`).
- The 12 grid keys are hit targets over the render's blank keycaps with **app-drawn theme-colored
  numerals** (rows 1 2 3 / 4 5 6 / 7 8 9 / 10 11 12, face-on = hardware order), flanked by the 12
  instant-apply binding callout chips — click to capture a key, Disable, or Default, each with a
  5 s one-shot undo, actions revealed on hover/focus (reserved-height, so columns never shift);
  hovering a chip or its grid key highlights both (two-way hover-pairing).
- **DPI card** — click-to-type readout (the big numerals swap for a digits-only box: Enter applies
  + saves as preset, click-away applies, Esc cancels; typed values snap to the slider's 50-DPI
  granularity); log-scale slider (apply on `PreviewMouseUp`, not `DragCompleted` — a track click
  never raises the latter); presets as a **segmented control** — one ChipFill track (radius 8, 2px
  inset), bare touching segments (radius 6), only the active segment chromed (AccentSoft fill +
  accent ring), in-segment hover-revealed ✕ with reserved width. **The ✕'s Click MUST stay
  `e.Handled`** — un-handled it bubbles into the segment's own apply Click against a disconnected
  container (`{DisconnectedItem}` hard-cast = the 2026-07-20 app-killing crash; regression:
  `DpiPillInteractionTests`). Deleting a preset never changes the live DPI (user decision). A
  `Status.Warning` "Couldn't confirm — wiggle the mouse and retry" line surfaces a failed apply
  via `DashboardViewModel.DpiStatus`.
- **Profile card** — slot dropdown rows carry the slot's **LED colour dot** + app-side name; ✎
  swaps the box for a rename TextBox (Enter commits / Esc cancels / 24-char cap, stored in
  settings `ProfileNames`); a text-only LED caption row under the box spells the colour out — plus
  the slot number once a rename hides it — and yields to transient notes. `SelectedProfileSlot`
  mirrors the mouse's actual active slot from `0x05/0x84`; sync is guarded so only a USER pick
  raises `SwitchRequested` → `0x05/0x04`, and a failed switch snaps the selection back.
- **Grid = hardware truth for the ACTIVE slot (v2.2)**: every inventory read that yields an active
  slot kicks `AppHost.ReadGridAsync` — a generation-guarded sequential sweep of the 12 buttons
  (`0x02/0x8c`) that fills chips progressively (`SetPending`/`SetFromDevice`; keyboard/disabled
  decode, other categories show "Synapse action (0xNN)"). Since v2.3 the sweep read doubles as the
  **undo snapshot source** (`_currentRaw` → `_prevRaw` on write; `SetPending` clears it so a stale
  slot's raw can't become another slot's undo target); undo is offered only when a snapshot exists.
- `CalloutViewModel` is the per-button state machine (Idle → Capturing → Writing → Confirmed |
  Failed); every action writes instantly through `AppHost.WriteBindingAsync`, no stage-then-commit.
  `DashboardViewModel` is the window's VM (header/DPI/profile state plus the `Callouts` list).
  `SettingsView` is the right-docked overlay (theme picker, general toggles, battery polling,
  reset-all-buttons). `RailTopMarginConverter` pins the right rail's top edge to the chip columns
  (computed top margin — **never** a bound Height on the rail wrapper: WPF layout-clips a child
  arranged smaller than its desired size, which silently amputated the Profile card's body).
- WPF gotchas: a compiled template's plain `<ScaleTransform/>` is shared + frozen across stamped
  elements — swap in a fresh per-element transform before animating (`MouseStageView.PressScale`).
  WPF-UI `NumberBox.Value` commits on LostFocus/Enter — bind `UpdateSourceTrigger=PropertyChanged`
  so a button Click reads the typed value, not the prior one.

## Themes (`Ui/Themes/`)

- `DesignSystem.xaml` — theme-independent colors (`Status.Positive/Warning/Critical` plus the
  slot-LED dot brushes `Slot.White/Red/Green/Blue/Cyan`) and shared styles
  `CardBorder`/`ChipBorder`/`LabelText`/`CardTitle`/`NumeralText`/`BodyText`/`SubtleText`.
  `CardTitle` (12px SemiBold SmallCaps) is the card/section **header** role — `LabelText`'s 10px
  all-small-caps renders ~7px glyphs and is for chip-number tags, never headers.
- 5 preset theme dictionaries (Porcelain default, Razer, Ice, Ultraviolet, Ember), each defining
  the same 12 semantic `App.*` brush keys (canvas, card fill/stroke, chip fill/stroke,
  accent/accent-soft, text primary/secondary, numeral, glow, plus the `App.ThemeName` marker).
- `ThemeManager.Apply` swaps the marked dictionary in `Application.Resources.MergedDictionaries`
  at runtime, then pushes the theme accent into WPF-UI (`ApplicationAccentColorManager.Apply`
  followed by re-`ApplicationThemeManager.Apply` with `updateAccent: false` — the re-apply makes
  already-instantiated Fluent chrome re-bake the pushed accent; keep `updateAccent: false` or the
  OS accent stomps it).
- Two hard rules (also in CLAUDE.md): **no `DropShadowEffect`/`BitmapEffect` anywhere** (stays
  software-rendered — glows are gradient brushes like `App.GlowSoft`, not effects), and **no
  hardcoded colors in themed XAML** — always a `DynamicResource App.*` key, never a literal color,
  so a theme swap actually repaints everything.

## Test-side UI tooling (`tests/`)

- `DashboardScreenshotProbe` — gated visual diagnostic (`NAGA_UI_PROBE=1`): renders the real
  `DashboardWindow` (or the popup via `NAGA_UI_PROBE_TARGET=popup`) off-screen with seeded state
  (`NAGA_UI_PROBE_STATE`: steady | renaming | named | typing | switching | offline;
  `NAGA_UI_PROBE_THEME`) and writes a PNG. Also the README screenshot source (`docs/images/`).
  Note for test hosts: don't use `ThemeManager.Apply` there — its relative pack URIs resolve
  against the ENTRY assembly; merge theme dictionaries with assembly-qualified URIs instead.
- `DpiPillInteractionTests` — always-on regression that drives a real bubbled `Click` through a
  real `DashboardWindow` (the preset-✕ crash). Shares the `wpf-ui` xunit collection with the
  probe so the two never own a WPF `Application` concurrently.

## References / prior art (Razer HID protocol)

Reverse-engineering cross-checks for the wire protocol. **Reference only — don't copy code (all
GPL), and don't copy their transport:** the two Windows apps below claim the USB interface via
libusb, which the gating constraint forbids — borrow the protocol bytes, not the I/O path.

- [openrazer/openrazer](https://github.com/openrazer/openrazer) — the authoritative open-source
  Razer protocol (Linux C driver). Validates ours **byte-for-byte**: 90-byte `razer_report`, XOR
  CRC over `[2..87]`, battery `0x07/0x80`, charging `0x07/0x84`, DPI get `0x04/0x85` / set
  `0x04/0x05` VARSTORE. Naga V2 Pro supported (PID `0x00A7`/`0x00A8`) with a **hard-coded
  transaction id `0x1f`** (our probe set leads with `0x1f`, so it converges there). Has **no
  button remapping** for any mouse — Phase B's protocol came from the Basilisk V3 command family
  (geezmolycos/razerqdhid) and was hardware-verified by our own spike (button-remap spec §6);
  don't mistake the LED `0x03/0x0B` "custom frame" command for remapping.
- [Tekk-Know/RazerBatteryTaskbar](https://github.com/Tekk-Know/RazerBatteryTaskbar) — Electron
  tray battery app, same niche. Confirms battery `0x07/0x80` + tx id `0x1f`. Supports the **Razer
  Mouse Dock Pro (PID `0x00A4`, tx `0x1f`)**. Caveat: archived (2024) and lists V2 Pro PIDs as
  `0x008F`/`0x0090` (vs our/openrazer's `0x00A7`/`0x00A8`) — verify PIDs against the connected
  device.
- [hsutungyu/razer-mouse-battery-windows](https://github.com/hsutungyu/razer-mouse-battery-windows)
  — small Python battery script (Mamba Wireless, tx `0x3f`). Confirms the report envelope +
  battery offset/scaling; note its CRC covers only the header (fine for the zero-payload battery
  query, wrong for SET DPI — ours XORs the full `[2..87]`). Stale (2022); doesn't cover the V2
  Pro.
