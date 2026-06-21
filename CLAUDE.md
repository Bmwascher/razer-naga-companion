# Razer Naga Companion — project guide

Featherweight Windows system-tray app — a minimal **Razer Synapse replacement** for the
**Razer Naga V2 Pro**. Shows battery % in the tray and reads/sets the mouse's active
hardware DPI. .NET 10 (`net10.0-windows10.0.19041.0`), C#, WPF + WinForms, WPF-UI 4.3.0
(Fluent), HidSharp, xUnit. Public repo: https://github.com/Bmwascher/razer-naga-companion

## Hard, GATING constraint — never regress
Stay **lightweight** (~0% idle CPU, ~23 MB private working set) AND introduce **zero mouse
input-latency / feel regression**. This is the entire point of the app; any lag or bloat
defeats it. How this is upheld (keep it this way):
- Talk to the mouse only via HID **feature reports** (USB control endpoint). Never claim the
  OS-owned input collection. Open zero-access + `FILE_SHARE_READ|WRITE` (passive client) so the
  interrupt IN endpoint carrying movement/clicks is undisturbed.
- Device I/O is on-demand or infrequent: battery poll cadence floor **15 s**; read/set DPI only
  on explicit user action — **never poll DPI**. No new background timers/threads.
- Blocking `HidD_*Feature` calls run **off the UI thread** (`Task.Run`); battery + DPI are
  serialized through one read lock. On-demand windows release on close so idle returns to baseline.
- Acceptance gates (not aspirations): footprint back to baseline + measured input latency
  unchanged before/during/after operations. Binds ALL phases, especially button remapping (B).
- Reference: `docs/superpowers/specs/2026-06-20-naga-settings-dpi-design.md` §3.1.

## Build / test / install (user-local .NET SDK)
The SDK is at `%LOCALAPPDATA%\Microsoft\dotnet` (not on PATH; `DOTNET_ROOT` set at User scope).
- Build:   `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
- Test:    `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
- Install: `.\scripts\install.ps1` — publishes Release (self-contained single-file), installs to
  `%LOCALAPPDATA%\Programs\NagaBatteryTray`, registers run-at-login, launches. Re-run to update.
- HID diagnostics: `NagaBatteryTray.exe --probe` (battery), `--probe-dpi` (raw DPI reply offsets).

Release is self-contained single-file; the 5 `*_cor3.dll` WPF native libraries MUST ship beside
the exe (single-file leaves them out of the bundle; copying the exe alone → DllNotFoundException).

## Architecture
- `Hid/RazerProtocol.cs` — pure 90-byte report build/parse + XOR CRC. Battery (class 0x07) and
  DPI (class 0x04, big-endian X/Y at reply `[10..13]`, VARSTORE persist) via shared
  `BuildReport`/`ValidateReply`.
- `Hid/RazerDevice.cs` — zero-access `CreateFile` + `HidD_Set/GetFeature`; `ExchangeAsync`
  transport (SET→wait→GET, busy-retry, close-on-failure); battery + Get/SetDpiAsync.
- `Monitoring/BatteryMonitor.cs` — poll timer + state machine; DPI pass-throughs (blocking lock).
- `Ui/` — `IconRenderer`, `TrayIconController`, `PopupWindow`, `SettingsWindow`/`SettingsViewModel`,
  `DoubleIntConverter`. `AppHost.cs`/`Program.cs` — single-instance, lifecycle, run-at-login.
- Design specs + implementation plans live in `docs/superpowers/`.

## Roadmap
- [x] v1 — battery tray
- [x] Phase 2-A — Settings window + active DPI (shipped 2026-06-20)
- [ ] C — Mouse Dock Pro charger support
- [ ] B — Button remapping (feasibility spike first; the V2 Pro remap protocol isn't documented)

## Conventions
TDD, DRY, YAGNI, surgical changes, conventional-commit messages, frequent commits. Read the FULL
file before editing. WPF-UI gotcha: `NumberBox.Value` commits on LostFocus/Enter — bind it
`UpdateSourceTrigger=PropertyChanged` so a button Click reads the typed value, not the prior one.
