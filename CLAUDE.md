# Razer Naga Companion — project guide

Featherweight Windows system-tray app — a minimal **Razer Synapse replacement** for the
**Razer Naga V2 Pro**. Shows battery % in the tray, reads/sets the mouse's active
hardware DPI, and remaps the 12-button thumb grid (onboard, key+modifiers or disable)
through a themed dashboard UI.
.NET 10 (`net10.0-windows10.0.19041.0`), C#, WPF + WinForms, WPF-UI 4.3.0
(Fluent), HidSharp, CommunityToolkit.WinUI.Notifications (toasts), xUnit.
Public repo: https://github.com/Bmwascher/razer-naga-companion

## Hard, GATING constraint — never regress
Stay **lightweight** (~0% idle CPU, ~23 MB private working set) AND introduce **zero mouse
input-latency / feel regression**. This is the entire point of the app; any lag or bloat
defeats it. How this is upheld (keep it this way):
- Talk to the mouse only via HID **feature reports** (USB control endpoint). Never claim the
  OS-owned input collection. Open zero-access + `FILE_SHARE_READ|WRITE` (passive client) so the
  interrupt IN endpoint carrying movement/clicks is undisturbed.
- Device I/O is on-demand or infrequent: battery poll cadence floor **15 s**; read/set DPI or
  buttons only on explicit user action — **never poll DPI or buttons** (button bindings live in an
  onboard slot the firmware holds itself, so no re-apply/verify path exists). No new background timers/threads (the lone *persistent*
  timer is the battery poll; the USB **device-change hook** — `DeviceChangeWatcher` → debounced refresh —
  is event-driven and idle-free: a one-shot `Task.Delay`, not a timer). The 15 s floor is a UI-input clamp (`DashboardViewModel.ApplyTo`, `Math.Max(15, …)`)
  — `BatteryMonitor.ScheduleNext` reads the cadence unclamped, so don't add a poll-cadence path that
  bypasses that clamp (a hand-edited `settings.json` already can).
- Blocking `HidD_*Feature` calls run **off the UI thread** (`Task.Run`); battery + DPI serialize
  through one shared lock (`BatteryMonitor._readLock`, a `SemaphoreSlim`). The **Dashboard** window
  releases on close (`StateChanged` unsubscribed + `_dashboard = null`) followed by a one-shot post-close
  trim (double GC — the window is finalizable, one GC only queues it — then a working-set trim, 2 s
  after Closed, skipped on reopen; without it WPF idles at ~85 MB after the first dashboard open) so idle
  returns to baseline; the **popup** is a cached
  singleton that only hides — don't make it release-on-close (re-creating it per tray click adds cost).
- Acceptance gates (not aspirations): footprint back to baseline + measured input latency
  unchanged before/during/after operations. Binds ALL phases, especially button remapping (B).
- Reference: `docs/superpowers/specs/2026-06-20-naga-settings-dpi-design.md` §3.1.

## Build / test / install (user-local .NET SDK)
The SDK is at `%LOCALAPPDATA%\Microsoft\dotnet` (not on PATH; `DOTNET_ROOT` set at User scope).
- Build:   `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
- Test:    `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test` — single test: append
  `--filter "FullyQualifiedName~RazerProtocolTests"` (or `Name=<method>`).
- Install: `.\scripts\install.ps1` — publishes Release (self-contained single-file), installs to
  `%LOCALAPPDATA%\Programs\NagaBatteryTray`, registers run-at-login, launches. Re-run to update.
  `.\scripts\uninstall.ps1` reverses it (leaves `%APPDATA%\NagaBatteryTray\settings.json`).
- HID diagnostics: `NagaBatteryTray.exe --probe` (battery), `--probe-dpi` (raw DPI reply offsets),
  `--probe-buttons` (remap spike: acceptance/grid-discovery/persistence; `--reset` restores recorded
  actions, `--slot-test` re-runs the scratch-slot persistence test), `--probe-profile` (read-only
  profile inventory + active-slot read hunt; capture to `%APPDATA%\NagaBatteryTray\probe-profile-*.md`).
- Solution is `NagaBatteryTray.slnx` (XML format, not `.sln`). No `global.json` (SDK unpinned),
  no CI, no `.editorconfig` — verification is local `dotnet test` only.

Release is self-contained single-file; the 5 `*_cor3.dll` WPF native libraries MUST ship beside
the exe (single-file leaves them out of the bundle; copying the exe alone → DllNotFoundException).

**Smart App Control (this machine):** SAC enforcement can block *loading* a freshly-built unsigned binary
by hash (`0x800711C7`) — building still works (the signed SDK compiles), but launching a fresh Release exe
(or loading a fresh Debug DLL) may be vetoed until the hash earns cloud reputation. For dev runs, launch the
Debug build via the signed host (`& $dotnet "…\bin\Debug\…\NagaBatteryTray.dll"`);
.NET builds are deterministic (same source → same hash → same verdict), so add `-p:Deterministic=false` to
mint a new hash and retry until one clears. The installed Release exe never shows a console; `dotnet …dll`
does, so launch it `-WindowStyle Hidden`.
- **Run-at-login was a SAC casualty (fixed 2026-06-25):** the old HKCU `Run` key fired ~52 s into boot, before
  the network was up, so SAC's ISG cloud-reputation lookup failed *closed* and vetoed the load (CodeIntegrity
  events 3033/3077) — even though the *same* hash launches fine once online. It is NOT a permanent veto of a
  reputable hash, just a boot-time race. Fix: run-at-login is now a logon scheduled task with a `PT1M` delay
  (see `StartupRegistration.cs`), so it launches after the machine is online. Keep this — don't revert to the
  Run key. A rebuilt exe still gets a new (unproven) hash, so prefer keeping the known-good installed exe over
  republishing just to re-register startup.

## Architecture
- `Hid/RazerProtocol.cs` — pure 90-byte report build/parse + XOR CRC (over bytes `[2..87]`).
  Battery (class 0x07) and DPI (class 0x04, big-endian X/Y at reply `[10..13]`, VARSTORE persist)
  via private `BuildReport`/`ValidateReply`; public API is
  `BuildFeatureBuffer`/`BuildGetDpiBuffer`/`BuildSetDpiBuffer` + `ParseReply`/`ParseDpiReply`.
  `ParseDpiReply` treats DPI outside `100..30000` as **Failed** (guards wrong-layout firmware replies).
  Buttons (class 0x02, set 0x0c / get 0x8c, args `[profile,buttonId,hypershift,category,len,d0..d4]`;
  `ParseButtonReply` guards via the **echo check** — the reply must echo the request's
  profile/buttonId/hypershift), profile lifecycle (class 0x05: list 0x81 / create 0x02 / delete 0x03),
  device mode (0x00/0x84 get, 0x00/0x04 set) — all hardware-verified 2026-07-11 (spike, spec §6);
  **active-slot get 0x05/0x84** (ds 0x06, slot number at reply arg[0]; echo-checked parse) and
  **set 0x05/0x04** (ds 0x01, arg[0]=slot; **persists across power-cycle** — full bottom-button
  parity) both hardware-verified 2026-07-18 (`--probe-profile` sweep + `--set-test`; in no
  reference repo — our own discovery). Consumed by the Profile card (`AppHost.RefreshProfileAsync`
  read on open/refresh, `SwitchProfileAsync` write-on-action via the card's slot dropdown) and the
  `--probe-profile` tools.
  `Hid/ButtonBinding.cs` holds the button model: `ButtonBinding` (+`ToWire`; the Default marker
  itself has no wire form — callers map it to the factory binding first), `RawButtonAction`,
  `ProfileList`, and `NagaV2ProButtons` (grid ids `0x40..0x4b` physical order;
  `FactoryBindingForPosition` = the digits row `1..9 0 - =` — a **freshly created onboard slot is
  EMPTY** (hardware-observed), so "Default" writes the baked-in factory action).
- `Hid/RazerDevice.cs` (implements `Hid/IRazerDevice.cs`) — zero-access `CreateFile` +
  `HidD_Set/GetFeature`; `ExchangeAsync` transport (SET→`SetReadDelayMs` wait→GET, busy-retry,
  close-on-failure) for battery/DPI/writes + the tid probe, and `FastReadAsync` for idempotent
  onboard-memory READS (button/profile queries): early-poll doubling ladder 50→400 ms, ready =
  completed status + class/cmd echo — the grid sweep's 12 reads land in ~a second instead of ~5 s. `EnsureConnectedAsync` picks the **live** collection (the one whose
  `GetMaxFeatureReportLength()==91`, **not** usage page 0xFF00 — none exposed) by trying candidates
  **wired `0x00A7` first, then wireless `0x00A8`**, and **verifying each actually answers a battery
  query** before committing: the dock's wireless receiver stays enumerated when the mouse switches to
  wired, so first-enumerated alone would lock onto a dead collection (this was the wired/USB-C "no
  response" bug, `932398e`). A read returning null drops the handle so the next poll re-selects
  (self-heals on plug/unplug). Explicit refreshes (`RefreshNowAsync`) additionally call **`Reset()`** so
  they re-select *immediately* — the null-drop self-heal is too slow on a wired plug, where the stale
  wireless handle keeps answering *not-charging* (`c6c4b9a`). The active link is surfaced
  (`BatteryReading.IsWired` → `DeviceState.Wired` → popup top-right "Wired"/"Wireless"/"On battery").
  VID `0x1532`. The Razer **transaction id is auto-probed** (`ResolveTransactionIdAsync` over
  `TransactionIdProbeSet`) and cached; every battery/DPI call gates on `tid != 0`, returning Absent/null
  silently until it resolves.
- `Monitoring/BatteryMonitor.cs` — poll timer + arming state machine; takes `IRazerDevice`; battery
  poll + DPI/button/profile pass-throughs serialize on one `_readLock` (poll skips if busy, the
  pass-throughs block). The poll itself does battery I/O only. `RefreshNowAsync`
  (manual button, wake, device-change) calls `_device.Reset()` first to re-select the active interface;
  the frequent background poll reuses the handle for efficiency.
- **Button remapping (Phase B 2026-07-11, reworked v2.3 2026-07-19)** — bindings live in the
  mouse's **onboard slots**; the firmware holds them through power-cycles — no re-apply, no
  sentinel poll. Since v2.3 **every slot is editable in place** (the user reversed the Phase B
  never-write-user-slots rule once byte-for-byte reads shipped — see the spec §13.2): writes
  target the **ACTIVE (displayed) slot**, and safety is **snapshot + raw undo** — before any
  overwrite the chip snapshots the button's on-mouse raw action (from the grid sweep), and ↶
  restores it verbatim via `AppHost.RestoreRawAsync`, which round-trips even Synapse
  macros/mouse functions the app can't model. Never remove that snapshot/undo path. The active
  slot is readable AND settable (`0x05/0x84`/`0x05/0x04` — the Profile card's ↻/dropdown); the
  mouse's bottom button (LED colour = slot: white/red/green/blue/cyan) remains the hardware
  fallback. Write path is `AppHost.WriteBindingAsync` → `WriteVerifiedAsync` (per-chip instant
  apply): resolve active slot → write → read-back verify; "Default" writes the factory action
  (deterministic, the repair path). Nothing is persisted app-side; the old adopt/seed machinery
  and the settings `ButtonBindings` table are retired (properties kept for JSON back-compat).
- `Settings/` — `AppSettings` + `ISettingsStore`/`JsonSettingsStore`. JSON at
  `%APPDATA%\NagaBatteryTray\settings.json` (Roaming — **not** the install dir under `%LOCALAPPDATA%`);
  holds cadences, low-battery threshold/notify, `SetReadDelayMs` (SET→GET wait, default 400), the
  cached transaction id, app-side DPI presets, and app-side profile-slot names (`ProfileNames`,
  keyed by slot number — the firmware stores no names, so a rename is a dashboard label only).
  `ButtonBindings`/`OnboardSlot` survive as properties for JSON back-compat
  but are no longer read or written (v2.3 — the firmware holds bindings, the grid reads hardware).
  Corrupt file → silently resets to defaults.
- `Ui/` — `IconRenderer` (two switchable styles, `TrayIconStyle` setting, switchable live from the
  settings overlay: the default **coin gauge** — a filled dark disc (`Color.FromArgb(205, 16,
  18, 22)`) covers the canvas edge-to-edge, its **rim is the battery-level ring** (hairline 5.5% of
  render, floored at 1 final px so AA can't dash it; track a faint full circle, arc colored by level —
  green/amber/red, green while charging), and the digits sit **inside** the coin, always **white**,
  laid out from their **ink bounds via a `GraphicsPath`** at 57% of render height (46% for 3-digit
  "100") with the width cap **derived from the ring's inner radius**, so the ink box geometrically
  never touches the ring — composition adopted from HoroTW/RazerBatteryTray (its own icons measure 41%
  digits / 6% ring) after the earlier ring-behind-full-height-digits design read as stray pixels at
  real tray sizes; or **"Text"** — the classic look, no coin/ring, digits fill the whole icon height
  and are colored by battery level instead of white. Both styles render 4x supersampled and are
  **downscaled in-app** (HighQualityBicubic) to the exact `SM_CXSMICON` size — handing the shell an
  oversized icon lets its low-quality resampler mush the digits at 16 px); `TrayIcon` (raw
  **Shell_NotifyIcon** keyed by a **stable GUID derived from the exe path** so
  Windows persists the taskbar position across restarts/sleep — fixes the position-reset bug; the
  path-derived GUID also differs between the installed exe and a dev-host run, dodging the shell's
  one-GUID-per-executable conflict; uses `NOTIFYICON_VERSION_4`, so handle **only** `NIN_SELECT`/
  `WM_CONTEXTMENU`, **not** the duplicate raw `WM_LBUTTONUP`/`WM_RBUTTONUP` or every click double-fires),
  wrapped by `TrayIconController` (tray menu item is **"Dashboard"**, renamed from "Settings"); `PopupWindow`
  (+`PopupViewModel`; cached singleton that **re-parks off-screen before every show** and positions in
  **physical px** to dodge mixed-DPI bugs and a reposition flash; restyled to the `App.*` DynamicResource
  theme keys — the old app-profile line is gone since v2.3 — and its second button
  opens the dashboard); `DeviceChangeWatcher` (hidden top-level window; `WM_DEVICECHANGE`/
  `DBT_DEVNODES_CHANGED` → debounced refresh); `Notifications` (low-battery toast), `DoubleIntConverter`.
  `AppHost.cs` lifecycle (owns the monitor, tray, and device-change hook) / `Program.cs` single-instance
  (named Mutex); run-at-login is `Startup/StartupRegistration.cs` (a **delayed-logon scheduled task**
  named `NagaBatteryTray`, registered via `schtasks /XML`; the exe self-registers through the
  `--enable-startup` switch that `install.ps1` calls); `Diagnostics/ProbeCommand.cs` backs
  `--probe`/`--probe-dpi`/`--probe-dock`/`--probe-buttons` (`[--reset|--slot-test]`); `KeyToHidUsage`
  (WPF Key ↔ HID usage map + modifier bits, used by the dashboard's key capture).
- `Ui/Dashboard/` (replaces the deleted `SettingsWindow`/`SettingsViewModel`/`ButtonRowViewModel`) —
  `DashboardWindow` (a WPF-UI `FluentWindow` shell; releases on close — the monitor's `StateChanged`
  subscription is removed and the field nulled, same release-on-close discipline the old Settings
  window had) hosts `MouseStageView`: the stage is the user's **product render** of the thumb panel
  (`Assets/naga-thumb.png`, a csproj `<Resource>`; grayscale blank-key AI edit of the real photo,
  decoded at `DecodePixelWidth=560` ≈ 2 MB only while the dashboard is open) clipped to
  `ShellOutline` — a silhouette **traced from the render's own pixels** (adaptive-threshold edge
  scan; the shadowed lower-left tail is a synthesized closure to the measured tip). The SAME
  geometry is stroked in four widening `App.AccentSoft` bands as a **rim glow behind the image**
  (strokes only, per the no-effects rule) so the glow hugs the visible cutout edge exactly — rest
  0.5 opacity, animating to 1.0 while any key captures (`DashboardViewModel.AnyCapturing`). The 12
  grid keys are hit targets over the render's blank keycaps with **app-drawn theme-colored
  numerals** (rows 1 2 3 / 4 5 6 / 7 8 9 / 10 11 12, face-on = hardware order), flanked by the 12
  instant-apply binding callout chips — click to capture a key, Disable, or Default, each with a
  5 s one-shot undo, actions revealed on hover/focus (reserved-height, so columns never shift);
  hovering a chip or its grid key highlights both (two-way hover-pairing) —
  plus a DPI card (log-scale slider; app-side presets as a **segmented control** — one ChipFill
  track, bare touching segments, only the active one chromed, in-segment hover-revealed ✕ with
  reserved width whose Click MUST stay `e.Handled` — un-handled it bubbles into the segment's
  apply Click against a disconnected container (was an app-killing crash; regression:
  `DpiPillInteractionTests`); a **click-to-type readout** — the big numerals swap for a
  digits-only box, Enter applies + saves as preset, click-away applies, Esc cancels, and
  deleting a preset never changes the live DPI; a `Status.Warning` "Couldn't confirm — wiggle
  the mouse and retry" line surfaces a failed apply via `DashboardViewModel.DpiStatus`) and a Profile card (slot
  dropdown rows carry the slot's **LED colour dot** + app-side name; ✎ swaps the box for a
  rename TextBox — Enter commits / Esc cancels / 24-char cap, stored in settings
  `ProfileNames`; a text-only LED caption row under the box spells the colour out — plus the
  slot number once a rename hides it — and yields to transient notes), and, as a right-docked overlay,
  `SettingsView` (theme picker, general toggles, battery polling, reset-all-buttons). `CalloutViewModel`
  is the per-button state machine (Idle → Capturing → Writing → Confirmed | Failed) that replaces
  `ButtonRowViewModel`'s staged-op model — every action writes instantly through `AppHost`'s
  `WriteBindingAsync`, no stage-then-commit step. **The grid shows hardware truth for the ACTIVE
  slot (v2.2)**: every inventory read that yields an active slot kicks `AppHost.ReadGridAsync` — a
  generation-guarded sequential sweep of the 12 buttons (`0x02/0x8c`) that fills chips
  progressively (`SetPending`/`SetFromDevice`; keyboard/disabled decode, other categories show
  "Synapse action (0xNN)"). Since v2.3 the sweep read doubles as the **undo snapshot source**
  (`_currentRaw` → `_prevRaw` on write; `SetPending` clears it so a stale slot's raw can't become
  another slot's undo target), every displayed slot is editable, and undo is offered only when a
  snapshot exists. `DashboardViewModel` is the window's VM (header/DPI/
  profile state plus the `Callouts` list); the Profile card is a slot **dropdown**
  (`SelectedProfileSlot` mirrors the mouse's actual active slot from `0x05/0x84`; sync is guarded
  so only a USER pick raises `SwitchRequested` → `0x05/0x04`, and a failed switch snaps the
  selection back) — `ProfileLiveness`'s effective-action inference is deleted. WPF gotcha
  (fixed a first-click crash): a compiled template's plain
  `<ScaleTransform/>` is shared + frozen across stamped elements — swap in a fresh per-element
  transform before animating (`MouseStageView.PressScale`).
- `Ui/Themes/` — `DesignSystem.xaml` (theme-independent colors — `Status.Positive/Warning/Critical`
  plus the slot-LED dot brushes `Slot.White/Red/Green/Blue/Cyan` — and shared styles `CardBorder`/
  `ChipBorder`/`LabelText`/`CardTitle`/`NumeralText`/`BodyText`/`SubtleText`; `CardTitle` (12px
  SemiBold SmallCaps) is the card/section **header** role — `LabelText`'s 10px all-small-caps
  renders ~7px glyphs and is for chip-number tags, never headers) and 5 preset theme dictionaries
  (Porcelain default, Razer, Ice, Ultraviolet, Ember),
  each defining the same 12 semantic `App.*` brush keys (canvas, card fill/stroke, chip fill/stroke,
  accent/accent-soft, text primary/secondary, numeral, glow, plus the `App.ThemeName` marker);
  `ThemeManager.Apply` swaps the marked dictionary in `Application.Resources.MergedDictionaries` at
  runtime, then pushes the theme accent into WPF-UI (`ApplicationAccentColorManager.Apply` followed by
  re-`ApplicationThemeManager.Apply` with `updateAccent: false` — the re-apply makes already-
  instantiated Fluent chrome re-bake the pushed accent; keep `updateAccent: false` or the OS accent
  stomps it). Two hard rules: **no `DropShadowEffect`/`BitmapEffect` anywhere** (stays software-rendered —
  glows are gradient brushes like `App.GlowSoft`, not effects), and **no hardcoded colors in themed
  XAML** — always a `DynamicResource App.*` key, never a literal color, so a theme swap actually
  repaints everything.
- Design specs + implementation plans live in `docs/superpowers/`.

## References / prior art (Razer HID protocol)
Reverse-engineering cross-checks for the wire protocol. **Reference only — don't copy code (all GPL),
and don't copy their transport:** the two Windows apps below claim the USB interface via libusb, which
our gating constraint forbids — borrow the protocol bytes, not the I/O path.
- [openrazer/openrazer](https://github.com/openrazer/openrazer) — the authoritative open-source Razer
  protocol (Linux C driver). Validates ours **byte-for-byte**: 90-byte `razer_report`, XOR CRC over
  `[2..87]`, battery `0x07/0x80`, charging `0x07/0x84`, DPI get `0x04/0x85` / set `0x04/0x05` VARSTORE.
  Naga V2 Pro supported (PID `0x00A7`/`0x00A8`) with a **hard-coded transaction id `0x1f`** (our probe
  set leads with `0x1f`, so it converges there). Has **no button remapping** for any mouse — Phase B's
  protocol came from the Basilisk V3 command family (geezmolycos/razerqdhid) and was hardware-verified
  by our own spike (spec §6); don't mistake the LED `0x03/0x0B` "custom frame" command for remapping.
- [Tekk-Know/RazerBatteryTaskbar](https://github.com/Tekk-Know/RazerBatteryTaskbar) — Electron tray
  battery app, same niche. Confirms battery `0x07/0x80` + tx id `0x1f`. **Phase C lead:** supports the
  **Razer Mouse Dock Pro (PID `0x00A4`, tx `0x1f`)**. Caveat: archived (2024) and lists V2 Pro PIDs as
  `0x008F`/`0x0090` (vs our/openrazer's `0x00A7`/`0x00A8`) — verify PIDs against the connected device.
- [hsutungyu/razer-mouse-battery-windows](https://github.com/hsutungyu/razer-mouse-battery-windows) —
  small Python battery script (Mamba Wireless, tx `0x3f`). Confirms the report envelope + battery
  offset/scaling; note its CRC covers only the header (fine for the zero-payload battery query, wrong
  for SET DPI — ours XORs the full `[2..87]`). Stale (2022); doesn't cover the V2 Pro.

## Roadmap
- [x] v1 — battery tray
- [x] Phase 2-A — Settings window + active DPI (shipped 2026-06-20)
- [x] C — Mouse Dock Pro charger support — **CLOSED 2026-06-21 (not built): dock relay non-viable** on
  this firmware (`0x00A4` never answers a battery/charging query — confirmed across 4 states incl.
  actively charging). Goal 1 (charging-on-dock) already works via the mouse's own read; goal 2 (relay)
  dropped. `--probe-dock` kept as the re-test tool. See `docs/superpowers/specs/2026-06-20-naga-dock-pro-design.md` §6.
- [x] Reliability + UI polish (2026-06-21): wired/USB-C battery read, instant charge-status on USB
  plug/unplug (device-change hook), GUID tray icon (stable taskbar position), larger tray digits,
  widened popup + themed header/charging-pill, no popup reposition flash.
- [x] B — Button remapping (MVP: key+modifiers/disable per grid button, onboard app-owned slot —
  shipped 2026-07-11; the app-owned-slot model was superseded 2026-07-19 by v2.3 in-place editing
  of ANY slot, spec §13.2). Spike + Stage 2 both hardware-accepted same day; see
  `docs/superpowers/specs/2026-06-21-naga-button-remap-design.md`.
- [x] GUI redesign — themed dashboard (`Ui/Dashboard/` + `Ui/Themes/`, 5 presets) replaces the old
  Settings window: instant-apply button remap chips with undo, DPI presets, a Profile card (a
  liveness card then; a slot dropdown since v2.2-2.3), and a tray battery-level ring (shipped
  2026-07-11); see `docs/superpowers/specs/2026-07-11-naga-gui-redesign-design.md`.
- [x] Profile probing — read-only `--probe-profile` spike (hardware run 2026-07-18): **HIT — the
  active onboard slot IS readable**: class `0x05` get `0x84` (data_size `0x06`, zero args) returns
  the active slot number at report arg[0] (literal 1..5; verified bijective/stable/reproduced across
  slots 1–3 + revisit; integrity re-check byte-identical, input-feel clean). Found by the opt-in
  blind sweep — the sourced shortlist (razerqdhid `0x80`/`0x8a`/`0x88`) all missed. The symmetric
  **set `0x05/0x04` was then verified same-day** by the opt-in `--set-test` spike (ds `0x01`,
  LED-confirmed, persists across power-cycle, integrity byte-identical). Follow-up ordered:
  Profile card direct read + Activate (event-driven / write-on-action only — no polling). Capture:
  `%APPDATA%\NagaBatteryTray\probe-profile-*.md`; see
  `docs/superpowers/specs/2026-07-18-naga-profile-probe-design.md` §10.
- [x] Dashboard polish (2026-07-19, accepted 2026-07-20) — post-v2.3 screenshot review pass:
  `CardTitle` header role (cards/sections stop using 10px small-caps titles), DPI preset row +
  couldn't-confirm status line (closes all three recorded 2026-07-17 DPI-card gripes), profile
  slot **rename** (app-side `ProfileNames`) + LED colour dots + caption, rail top aligned to the
  chip columns. Round-3 user iteration (2026-07-20): text-only adaptive LED caption, presets
  became a **segmented control**, preset-✕ crash fixed (bubbled Click → `DpiPillInteractionTests`
  regression + the gated `NAGA_UI_PROBE=1` screenshot probe), click-to-type DPI readout.
  See `docs/superpowers/specs/2026-07-19-naga-dashboard-polish-design.md`.
- [ ] DPI stages + polling rate — program the onboard 5-stage DPI table (+ stage up/down) and
  polling-rate get/set; both openrazer-validated commands, write-on-action only (ordered 2026-07-17).
  Includes the deferred **DPI card rework part 2** (user, 2026-07-17): the app-side preset list
  likely becomes the onboard stage table (the hover/✕/silent-failure gripes recorded then were
  already closed by the 2026-07-19 dashboard-polish pass). Also carries the deferred **right-rail
  relayout** (user, 2026-07-20): the side panel's card arrangement gets rethought when the
  polling-rate box joins it.
- [ ] Lighting (last) — thumb-grid / scroll-wheel zone effects + brightness, theme-sync candidate;
  openrazer class 0x03/0x0F matrix commands.

## Conventions
TDD, DRY, YAGNI, surgical changes, conventional-commit messages, frequent commits. Read the FULL
file before editing.
**README.md rides every branch** (rule added 2026-07-20 after a 132-commit catch-up produced from
commit archaeology): any user-visible change — features, UI, install/uninstall, diagnostics,
roadmap — updates README.md in the SAME branch before it merges. The README's screenshots live in
`docs/images/` and are rendered by the gated UI probe (`NAGA_UI_PROBE=1` +
`--filter DashboardScreenshotProbe`; `NAGA_UI_PROBE_TARGET=popup` for the popup) — regenerate them
in the same pass whenever the dashboard or popup visibly changes; stale screenshots are worse than
stale prose. WPF-UI gotcha: `NumberBox.Value` commits on LostFocus/Enter — bind it
`UpdateSourceTrigger=PropertyChanged` so a button Click reads the typed value, not the prior one.
Tests cover logic layers only — `RazerProtocol`, `BatteryMonitor`, `DashboardViewModel`,
`JsonSettingsStore`, `IconRenderer`, `StartupRegistration`, `ButtonBinding`/`NagaV2ProButtons`,
`KeyToHidUsage`, `CalloutViewModel`, `ThemeManager`, `PopupViewModel` — via
`Fakes/FakeRazerDevice` (the
`IRazerDevice` seam); HID transport, WPF windows, and the tray are exercised by the installed build
and `--probe`/`--probe-dpi`/`--probe-buttons`, not unit tests. Tests reach `internal` members through
`InternalsVisibleTo.cs` — don't tighten visibility or drop that file.
