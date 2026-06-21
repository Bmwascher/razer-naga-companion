# Razer Naga Companion — project guide

Featherweight Windows system-tray app — a minimal **Razer Synapse replacement** for the
**Razer Naga V2 Pro**. Shows battery % in the tray and reads/sets the mouse's active
hardware DPI. .NET 10 (`net10.0-windows10.0.19041.0`), C#, WPF + WinForms, WPF-UI 4.3.0
(Fluent), HidSharp, CommunityToolkit.WinUI.Notifications (toasts), xUnit.
Public repo: https://github.com/Bmwascher/razer-naga-companion

## Hard, GATING constraint — never regress
Stay **lightweight** (~0% idle CPU, ~23 MB private working set) AND introduce **zero mouse
input-latency / feel regression**. This is the entire point of the app; any lag or bloat
defeats it. How this is upheld (keep it this way):
- Talk to the mouse only via HID **feature reports** (USB control endpoint). Never claim the
  OS-owned input collection. Open zero-access + `FILE_SHARE_READ|WRITE` (passive client) so the
  interrupt IN endpoint carrying movement/clicks is undisturbed.
- Device I/O is on-demand or infrequent: battery poll cadence floor **15 s**; read/set DPI only
  on explicit user action — **never poll DPI**. No new background timers/threads (the lone timer is
  the battery poll). The 15 s floor is a UI-input clamp (`SettingsViewModel.ApplyTo`, `Math.Max(15, …)`)
  — `BatteryMonitor.ScheduleNext` reads the cadence unclamped, so don't add a poll-cadence path that
  bypasses that clamp (a hand-edited `settings.json` already can).
- Blocking `HidD_*Feature` calls run **off the UI thread** (`Task.Run`); battery + DPI serialize
  through one shared lock (`BatteryMonitor._readLock`, a `SemaphoreSlim`). The **Settings** window
  releases on close (`_settingsWindow = null`) so idle returns to baseline; the **popup** is a cached
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
- HID diagnostics: `NagaBatteryTray.exe --probe` (battery), `--probe-dpi` (raw DPI reply offsets).
- Solution is `NagaBatteryTray.slnx` (XML format, not `.sln`). No `global.json` (SDK unpinned),
  no CI, no `.editorconfig` — verification is local `dotnet test` only.

Release is self-contained single-file; the 5 `*_cor3.dll` WPF native libraries MUST ship beside
the exe (single-file leaves them out of the bundle; copying the exe alone → DllNotFoundException).

## Architecture
- `Hid/RazerProtocol.cs` — pure 90-byte report build/parse + XOR CRC (over bytes `[2..87]`).
  Battery (class 0x07) and DPI (class 0x04, big-endian X/Y at reply `[10..13]`, VARSTORE persist)
  via private `BuildReport`/`ValidateReply`; public API is
  `BuildFeatureBuffer`/`BuildGetDpiBuffer`/`BuildSetDpiBuffer` + `ParseReply`/`ParseDpiReply`.
  `ParseDpiReply` treats DPI outside `100..30000` as **Failed** (guards wrong-layout firmware replies).
- `Hid/RazerDevice.cs` (implements `Hid/IRazerDevice.cs`) — zero-access `CreateFile` +
  `HidD_Set/GetFeature`; `ExchangeAsync` transport (SET→`SetReadDelayMs` wait→GET, busy-retry,
  close-on-failure). Device is found by the HID collection whose `GetMaxFeatureReportLength()==91`,
  **not** by usage page 0xFF00 (none exposed — verified empirically); VID `0x1532`, PID `0x00A8`
  wireless / `0x00A7` wired. The Razer **transaction id is auto-probed** (`ResolveTransactionIdAsync`
  over `TransactionIdProbeSet`) and cached; every battery/DPI call gates on `tid != 0`, returning
  Absent/null silently until it resolves.
- `Monitoring/BatteryMonitor.cs` — poll timer + arming state machine; takes `IRazerDevice`; battery
  poll + DPI pass-throughs serialize on one `_readLock` (poll skips if busy, DPI blocks).
- `Settings/` — `AppSettings` + `ISettingsStore`/`JsonSettingsStore`. JSON at
  `%APPDATA%\NagaBatteryTray\settings.json` (Roaming — **not** the install dir under `%LOCALAPPDATA%`);
  holds cadences, low-battery threshold/notify, `SetReadDelayMs` (SET→GET wait, default 400), cached
  transaction id. Corrupt file → silently resets to defaults.
- `Ui/` — `IconRenderer`, `TrayIconController`, `PopupWindow` (+`PopupViewModel`; parks off-screen and
  positions in **physical px** to dodge mixed-DPI bugs), `SettingsWindow`/`SettingsViewModel`,
  `Notifications` (low-battery toast), `DoubleIntConverter`. `AppHost.cs` lifecycle / `Program.cs`
  single-instance (named Mutex); run-at-login is `Startup/StartupRegistration.cs`;
  `Diagnostics/ProbeCommand.cs` backs `--probe`/`--probe-dpi`.
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
  remap protocol is undocumented even here and still needs a Synapse USB-capture spike (don't mistake
  the LED `0x03/0x0B` "custom frame" command for remapping).
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
- [ ] C — Mouse Dock Pro charger support
- [ ] B — Button remapping (feasibility spike first; the V2 Pro remap protocol isn't documented)

## Conventions
TDD, DRY, YAGNI, surgical changes, conventional-commit messages, frequent commits. Read the FULL
file before editing. WPF-UI gotcha: `NumberBox.Value` commits on LostFocus/Enter — bind it
`UpdateSourceTrigger=PropertyChanged` so a button Click reads the typed value, not the prior one.
Tests cover logic layers only — `RazerProtocol`, `BatteryMonitor`, `SettingsViewModel`,
`JsonSettingsStore`, `IconRenderer`, `StartupRegistration` — via `Fakes/FakeRazerDevice` (the
`IRazerDevice` seam); HID transport, WPF windows, and the tray are exercised by the installed build
and `--probe`/`--probe-dpi`, not unit tests. Tests reach `internal` members through
`InternalsVisibleTo.cs` — don't tighten visibility or drop that file.
