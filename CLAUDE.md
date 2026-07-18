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
  **active-slot get 0x05/0x84** (ds 0x06, slot number at reply arg[0]) hardware-verified 2026-07-18
  (`--probe-profile` sweep — not in any reference repo; probe-only for now, no production caller yet).
  `Hid/ButtonBinding.cs` holds the button model: `ButtonBinding` (+`ToWire`; Default throws — never
  written), `RawButtonAction`, `ProfileList`, and `NagaV2ProButtons` (grid ids `0x40..0x4b` physical
  order; `FactoryBindingForPosition` = the digits row `1..9 0 - =` — a **freshly created onboard slot
  is EMPTY**, so "Default" writes the baked-in factory action and new slots are seeded with it).
- `Hid/RazerDevice.cs` (implements `Hid/IRazerDevice.cs`) — zero-access `CreateFile` +
  `HidD_Set/GetFeature`; `ExchangeAsync` transport (SET→`SetReadDelayMs` wait→GET, busy-retry,
  close-on-failure). `EnsureConnectedAsync` picks the **live** collection (the one whose
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
- **Button remapping (Phase B, shipped 2026-07-11)** — bindings are written **once** into an
  **app-owned onboard profile slot** (created on first Apply via the first FREE slot number, seeded
  with the factory map, recorded as `OnboardSlot` in settings; **the user's existing slots 01/02 are
  never taken or written**). The firmware holds bindings through power-cycles — no re-apply, no
  sentinel poll. No command exists to set the active slot: the user selects it once with the mouse's
  bottom button (LED colour = slot: white/red/green/blue/cyan; the dashboard's Profile card names it).
  Write path is `AppHost.WriteBindingAsync` (per-chip instant apply): ensure slot → write → read-back
  verify → persist; "Default" writes the factory action (deterministic) and is always available on any
  chip (the repair path).
- `Settings/` — `AppSettings` + `ISettingsStore`/`JsonSettingsStore`. JSON at
  `%APPDATA%\NagaBatteryTray\settings.json` (Roaming — **not** the install dir under `%LOCALAPPDATA%`);
  holds cadences, low-battery threshold/notify, `SetReadDelayMs` (SET→GET wait, default 400), cached
  transaction id, the sparse `ButtonBindings` table (grid position → key/disabled), and the adopted
  `OnboardSlot`. Corrupt file → silently resets to defaults.
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
  theme keys, shows a "Profile N · colour" line once an onboard slot is adopted, and its second button
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
  plus a DPI card with app-side presets and a Profile card, and, as a right-docked overlay,
  `SettingsView` (theme picker, general toggles, battery polling, reset-all-buttons). `CalloutViewModel`
  is the per-button state machine (Idle → Capturing → Writing → Confirmed | Failed) that replaces
  `ButtonRowViewModel`'s staged-op model — every action writes instantly through `AppHost`'s
  `WriteBindingAsync`, no stage-then-commit step. `DashboardViewModel` is the window's VM (header/DPI/
  profile state plus the `Callouts` list); `ProfileLiveness` is a pure comparer — is the mouse currently
  ON the app's onboard slot? (a profile-0 **effective-action** read, hardware-verified in the Phase B
  spike, compared against the app slot's expected bytes) — driving the Profile card's live/not-live/
  unknown text. WPF gotcha (fixed a first-click crash): a compiled template's plain
  `<ScaleTransform/>` is shared + frozen across stamped elements — swap in a fresh per-element
  transform before animating (`MouseStageView.PressScale`).
- `Ui/Themes/` — `DesignSystem.xaml` (theme-independent status colors — `Status.Positive/Warning/
  Critical` — plus shared styles `CardBorder`/`ChipBorder`/`LabelText`/`NumeralText`/`BodyText`/
  `SubtleText`) and 5 preset theme dictionaries (Porcelain default, Razer, Ice, Ultraviolet, Ember),
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
  shipped 2026-07-11). Spike + Stage 2 both hardware-accepted same day; see
  `docs/superpowers/specs/2026-06-21-naga-button-remap-design.md`.
- [x] GUI redesign — themed dashboard (`Ui/Dashboard/` + `Ui/Themes/`, 5 presets) replaces the old
  Settings window: instant-apply button remap chips with undo, DPI presets, a Profile liveness card,
  and a tray battery-level ring (shipped 2026-07-11); see
  `docs/superpowers/specs/2026-07-11-naga-gui-redesign-design.md`.
- [x] Profile probing — read-only `--probe-profile` spike (hardware run 2026-07-18): **HIT — the
  active onboard slot IS readable**: class `0x05` get `0x84` (data_size `0x06`, zero args) returns
  the active slot number at report arg[0] (literal 1..5; verified bijective/stable/reproduced across
  slots 1–3 + revisit; integrity re-check byte-identical, input-feel clean). Found by the opt-in
  blind sweep — the sourced shortlist (razerqdhid `0x80`/`0x8a`/`0x88`) all missed. Get/set symmetry
  suggests `0x05/0x04` = SET-active-profile (**unprobed** — a write, out of the spike's scope; a
  future opt-in spike could remove the bottom-button UX cost). Follow-up ordered: Profile card
  direct read (event-driven only — the no-polling rule stands). Capture:
  `%APPDATA%\NagaBatteryTray\probe-profile-*.md`; see
  `docs/superpowers/specs/2026-07-18-naga-profile-probe-design.md` §10.
- [ ] DPI stages + polling rate — program the onboard 5-stage DPI table (+ stage up/down) and
  polling-rate get/set; both openrazer-validated commands, write-on-action only (ordered 2026-07-17).
  Includes the deferred **DPI card rework** (user, 2026-07-17): the app-side preset list likely
  becomes the onboard stage table; known gripes to fix then — a hovered preset row paints the
  themed button hover background and reads as a text-input box, the hover-revealed ✕ floats
  far right of the value, and (branch review, 2026-07-17) a failed DPI apply is silent — the old
  Settings window said "Couldn't confirm — wiggle the mouse and retry", the card needs a status
  surface for that again.
- [ ] Lighting (last) — thumb-grid / scroll-wheel zone effects + brightness, theme-sync candidate;
  openrazer class 0x03/0x0F matrix commands.

## Conventions
TDD, DRY, YAGNI, surgical changes, conventional-commit messages, frequent commits. Read the FULL
file before editing. WPF-UI gotcha: `NumberBox.Value` commits on LostFocus/Enter — bind it
`UpdateSourceTrigger=PropertyChanged` so a button Click reads the typed value, not the prior one.
Tests cover logic layers only — `RazerProtocol`, `BatteryMonitor`, `DashboardViewModel`,
`JsonSettingsStore`, `IconRenderer`, `StartupRegistration`, `ButtonBinding`/`NagaV2ProButtons`,
`KeyToHidUsage`, `CalloutViewModel`, `ThemeManager`, `ProfileLiveness`, `PopupViewModel` — via
`Fakes/FakeRazerDevice` (the
`IRazerDevice` seam); HID transport, WPF windows, and the tray are exercised by the installed build
and `--probe`/`--probe-dpi`/`--probe-buttons`, not unit tests. Tests reach `internal` members through
`InternalsVisibleTo.cs` — don't tighten visibility or drop that file.
