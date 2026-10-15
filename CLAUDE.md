# Razer Naga Companion — project guide

Featherweight Windows system-tray app — a minimal **Razer Synapse replacement** for the
**Razer Naga V2 Pro and Naga V3 Pro**: tray battery %, hardware DPI read/set, 12-button
thumb-grid remapping (onboard), onboard profile switching, themed dashboard.
.NET 10 (`net10.0-windows10.0.19041.0`), C#, WPF + WinForms, WPF-UI 4.3.0 (Fluent), HidSharp,
CommunityToolkit.WinUI.Notifications, xUnit. Public repo:
https://github.com/Bmwascher/razer-naga-companion

This file is the entry point: hard rules, commands, directory, roadmap. The deep subsystem
reference is `docs/architecture.md`; per-feature design history is `docs/superpowers/specs/`.

## Hard rules (never regress)

1. **GATING: stay lightweight (~0% idle CPU, ~23 MB private WS) with ZERO mouse input-latency /
   feel regression.** Feature reports only — never claim the OS-owned input collection. No
   polling of DPI or buttons, ever; device I/O is on-demand or the battery poll (15 s floor — a
   UI clamp in `DashboardViewModel.ApplyTo`; don't add a cadence path that bypasses it). No new
   background timers/threads. HID calls off the UI thread, serialized on the one
   `BatteryMonitor._readLock`. Dashboard releases on close (+ post-close GC/trim); popup is a
   cached singleton — never make it release-on-close. Footprint-back-to-baseline and
   unchanged-input-feel are acceptance gates. Mechanics: `docs/architecture.md`.
2. **Never remove the button snapshot + raw-undo path** (`AppHost.RestoreRawAsync` and the sweep
   snapshot feeding it). It is what makes v2.3's edit-any-slot-in-place safe — it restores even
   Synapse actions the app can't model. User-confirmed safety model.
3. **Theming: no `DropShadowEffect`/`BitmapEffect` anywhere** (software-rendered; glows are
   gradient brushes) and **no hardcoded colors in themed XAML** — always `DynamicResource App.*`.
4. **README.md + `docs/images/` screenshots ride every branch**: any user-visible change updates
   them in the SAME branch before merge (screenshots regenerate via the UI probe — see
   Diagnostics). No bulk catch-ups from commit archaeology.
5. **Run-at-login stays a delayed scheduled task** (`StartupRegistration.cs`) — never the HKCU
   `Run` key (SAC boot-time race, see `docs/architecture.md`).
6. **TDD**; tests cover logic layers through the `IRazerDevice` seam (`Fakes/FakeRazerDevice`);
   WPF windows/tray/HID transport are exercised by the installed build and the probes — the two
   deliberate exceptions are `DpiPillInteractionTests` (always-on real-window regression) and the
   gated screenshot probe. Since 2026-08-19 the transport exemption covers **only the two Win32
   wrappers** (`IControlCollection`/`IControlCollectionSource`) and the WPF windows — connection
   selection above that seam is ordinary tested code (`docs/architecture.md`). Tests reach
   `internal` via `InternalsVisibleTo.cs` — don't tighten visibility or drop that file.

## Commands (user-local .NET SDK — not on PATH)

- Build: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
- Test: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test` — single:
  `--filter "FullyQualifiedName~<Class>"` (or `Name=<method>`)
- Install/update: `.\scripts\install.ps1` (publish → `%LOCALAPPDATA%\Programs\NagaBatteryTray` →
  run-at-login → launch); `.\scripts\uninstall.ps1` reverses (keeps settings.json)
- Solution: `NagaBatteryTray.slnx` (XML format). `global.json` pins SDK 10.0.301
  (`rollForward: latestFeature`). CI: `.github/workflows/test.yml` runs `dotnet test` on a
  windows runner per push/PR — local `dotnet test` remains the primary verification. No
  `.editorconfig`.
- Release publish is self-contained single-file; the 5 `*_cor3.dll` WPF natives MUST ship beside
  the exe (copying the exe alone → `DllNotFoundException`).
- **SAC (this machine)** may veto launching fresh unsigned builds by hash (`0x800711C7`). Dev
  runs: `& $dotnet "…\bin\Debug\…\NagaBatteryTray.dll"` `-WindowStyle Hidden`; if vetoed, rebuild
  with `-p:Deterministic=false` for a fresh hash. Details + the run-at-login story:
  `docs/architecture.md`.

### Diagnostics

- HID probes (installed exe): `--probe` battery · `--probe-dpi` · `--probe-buttons
  [--reset|--slot-test]` · `--probe-profile` (capture → `%APPDATA%\NagaBatteryTray\`) ·
  `--probe-dock` · `--probe-latency` (read-only reply timing; the evidence for tuning
  `RazerDevice.FastReadRungs` — needs the mouse awake)
- Perf benches (gated `NAGA_PERF=1`, filter `PerfBench`): `PerfBenchIo` times the device pipeline
  against a latency-modelling emulator and reports round-trip counts beside wall clock;
  `PerfBenchUi` times window open + settings scrolling; `PerfBenchHost` times the schtasks query.
  Vars: `NAGA_PERF_OUT` (report path), `NAGA_PERF_LABEL` — a before/after pair is a diff of two runs.
- UI screenshot probe (layout iteration + README screenshots, no install needed): set
  `NAGA_UI_PROBE=1`, run test filter `DashboardScreenshotProbe`; vars: `NAGA_UI_PROBE_OUT`,
  `NAGA_UI_PROBE_THEME`, `NAGA_UI_PROBE_STATE` (steady|renaming|named|typing|switching|offline),
  `NAGA_UI_PROBE_TARGET` (dashboard|popup|settings — the settings drawer on its own, where
  state `preference` seeds a preferred mouse other than the connected one)

## Directory

| Path | What lives there |
| --- | --- |
| `src/NagaBatteryTray/Hid/` | Wire protocol (`RazerProtocol`), button model (`ButtonBinding`), transport (`RazerDevice` behind `IRazerDevice`), connection selection (`MouseConnection` + `ConnectionPolicy` over the `IControlCollection` Win32 seam) |
| `src/NagaBatteryTray/Monitoring/` | `BatteryMonitor` — the one poll timer + device-call serialization; `DeviceScopedState` — the per-mouse cache |
| `src/NagaBatteryTray/Settings/` | `AppSettings` + JSON store (`%APPDATA%\NagaBatteryTray\settings.json`) |
| `src/NagaBatteryTray/Ui/` | Tray icon renderer/host, popup, device-change watcher, toasts, `AppHost` lifecycle |
| `src/NagaBatteryTray/Ui/Dashboard/` | The dashboard: mouse stage + remap chips, DPI card, Profile card, settings overlay |
| `src/NagaBatteryTray/Ui/Themes/` | `DesignSystem.xaml` + 5 theme dictionaries + `ThemeManager` |
| `src/NagaBatteryTray/Startup/`, `Diagnostics/` | Run-at-login task registration; `--probe*` implementations |
| `tests/NagaBatteryTray.Tests/` | xUnit suite incl. `FakeRazerDevice`, the screenshot probe, `DpiPillInteractionTests` |
| `scripts/` | `install.ps1` / `uninstall.ps1` |
| `docs/architecture.md` | **The deep reference** — subsystem how-and-why, gotchas, protocol tables, prior art |
| `docs/superpowers/specs/` | Per-feature design specs (dated; each roadmap item links its spec) |
| `docs/images/` | README screenshots — probe-rendered, regenerate on UI change (rule 4) |
| `docs/icon/` | App-icon SVG sources + `build-ico.py`, which rasterises them and packs `src/NagaBatteryTray/app.ico`. Edit the SVGs and rerun it; never hand-edit the `.ico` |
| `.superpowers/sdd/progress.md` | Gitignored session ledger — append per milestone |

## Roadmap

Shipped (spec links = the full story):

- v1 battery tray → Settings + active DPI (2026-06-20, `specs/2026-06-20-naga-settings-dpi-design.md`)
- Dock relay — **closed, non-viable** on this firmware; `--probe-dock` is the re-test tool
  (`specs/2026-06-20-naga-dock-pro-design.md` §6)
- Reliability pass: wired/USB-C reads, instant charge status, GUID tray icon (2026-06-21)
- B — button remapping, onboard (2026-07-11; v2.3 edit-any-slot 2026-07-19,
  `specs/2026-06-21-naga-button-remap-design.md` §13.2)
- GUI redesign — themed dashboard, 5 presets (2026-07-11, `specs/2026-07-11-naga-gui-redesign-design.md`)
- Profile probing → active-slot get/set `0x05/0x84`/`0x05/0x04` — **our own protocol discovery**
  (2026-07-18, `specs/2026-07-18-naga-profile-probe-design.md` §10)
- Dashboard polish: CardTitle role, segmented DPI presets + type-in readout + failure line,
  slot rename/LED dots/adaptive caption, rail datum, preset-✕ crash fix (2026-07-19/20,
  `specs/2026-07-19-naga-dashboard-polish-design.md`)
- Naga V3 Pro dual support — probed the V3 Pro on real hardware, confirmed battery / DPI /
  button-remap / profile protocol parity with the `--probe*` suite, device layer now
  auto-detects either model (2026-08-19,
  `specs/2026-08-19-naga-v3pro-dual-support-design.md`)
- Mouse selector + per-device state isolation — closes the three V3 acceptance follow-ups, which
  investigation showed were **one root cause**: state cached for one mouse outliving the mouse
  underneath it. Ships a dashboard header mouse selector (defaults to the V3, choice persisted),
  preference-ordered scanning with reselect on the battery path only, a `MouseConnection` +
  `ConnectionPolicy` state machine over a new `IControlCollection` Win32 seam, connection identity
  (id within an operation, model across time), an atomic button write, a per-mouse state cache, and
  per-mouse profile names (2026-08-19, `specs/2026-08-19-naga-mouse-selector-design.md`)
- Perf pass — the one real gap was the transport waiting on a fixed schedule unrelated to how
  fast the mouse answered. Every idempotent read (battery, charging, DPI, button, profile) now
  polls early on a measured rung schedule — `--probe-latency`, added by this pass, found every
  command class ready in 21-24 ms — under a two-part patience cap (`ReadPatience`) so no read is
  less patient, or slower to give up, than the flat wait it replaced; `ExchangeAsync` keeps the
  writes and the transaction-id probe. The run-at-login `schtasks` query came off the tray and
  dashboard-open paths behind a generation token. Dashboard content 3301→638 ms, startup battery
  read 1214→93 ms, grid sweep 1149→420 ms, dead-receiver discovery 1212→500 ms, round-trip
  counts identical to base in all ten scenarios — so this removed waiting, not work. No spec:
  bounded work, reviewed after the fact (Fable diff gate, PASS at round 2, degraded for want of a
  second vendor). The story is `docs/architecture.md` § `RazerDevice.cs`; the benches are
  `PerfBenchIo`/`PerfBenchUi`/`PerfBenchHost`, gated `NAGA_PERF=1` (2026-10-15)
- ReadyToRun — **closed, not adopted.** Measured on a branch straight after the perf pass:
  precompiling IL at publish reached the tray icon 25.5 ms sooner and the first battery
  percentage 49.0 ms sooner (40 interleaved A/B pairs, paired medians), but raised idle private
  working set 24.03→26.98 MB against hard rule 1's ~23 MB gate, added 59.5 MiB of binary
  (+31.4%) and cost +10.8 ms before `Main` from mapping the larger image. Branch deleted, nothing
  shipped; the numbers, the method and the one reusable lesson (measure startup out of a publish
  folder, never through `install.ps1`) are
  `specs/2026-10-15-ready-to-run-evaluation.md` (2026-10-15)

Next:

- [ ] **DPI stages + polling rate** — onboard 5-stage table (+ stage up/down), polling-rate
  get/set (openrazer-validated, write-on-action only). Carries two deferred riders: the preset
  row likely becomes the onboard stage table (user, 2026-07-17), and the right-rail relayout
  happens when the polling-rate box joins it (user, 2026-07-20).
- [ ] **Lighting** (last) — thumb-grid / scroll-wheel zone effects + brightness, theme-sync
  candidate; openrazer class 0x03/0x0F.
- [ ] **Create-profile UI** — unscheduled, no owner date. The V3 Pro ships with a single onboard
  slot created, so the Profile card can only ever offer that one; the create/delete commands
  (class `0x05` `0x02`/`0x03`) are already in `RazerProtocol` but nothing calls them. Logged out of
  the mouse-selector pass as an explicit non-goal (spec §3).

## Workflow & conventions

- DRY, YAGNI, surgical changes; conventional-commit messages, frequent commits. Read the FULL
  file before editing.
- Feature work runs on a branch per pass; the user's visual acceptance of the **installed build**
  gates the merge (merge → delete branch → ledger entry). **Push to origin only when the user
  says so.**
- Hardware-facing changes get probe/spike verification before UI work builds on them (see the
  spec pattern in `docs/superpowers/specs/`).
- Gotchas index (details in `docs/architecture.md`): WPF layout-clip on bound Height (rail),
  frozen shared template transforms, WPF-UI `NumberBox` LostFocus commit, nested-button Click
  bubbling (`e.Handled`), `ThemeManager.Apply` entry-assembly pack URIs in test hosts.
