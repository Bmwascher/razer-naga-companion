# Phase 2-A: Settings Window + Active Mouse DPI — Design

**Date:** 2026-06-20
**Status:** Approved (ready for implementation plan)
**Builds on:** v1 (battery tray). See `2026-06-19-naga-v2-pro-battery-tray-design.md`.

## 1. Goal

Add a polished **Settings window** to Razer Naga Companion and, through it, let the user
**read and set the mouse's active hardware DPI** — turning the app from a battery readout
into a small Synapse replacement, without adding weight or new dependencies.

## 2. Scope

**In scope (Phase 2-A):**
- A single Fluent **Settings window**, opened from the popup's (currently disabled) Settings
  button and from a new tray-menu item.
- **Low-battery threshold** setting (surfaced from existing `AppSettings`).
- **Run at startup** toggle (mirrors the tray menu; registry remains source of truth).
- **Active mouse DPI** read + set: single X=Y value, slider + number box, explicit Apply,
  persisted to the mouse's onboard memory, with read-back confirmation.
- Poll cadence (on-battery / charging) in a **collapsed "Advanced"** section.

**Deferred (designed for, not built):**
- DPI **stages** (multi-stage cycle list) — protocol layer kept extensible.
- **Poll-rate** get/set — same `0x04` command family, additive later.
- **Theme switcher** — cut for now (the popup uses fixed dark colors and the app forces
  Dark; a real Light/HighContrast option needs a full theme-brush migration — its own task).
- Multi-device / dock support (separate sub-projects C and B).

## 3. Success criteria

- Settings window opens from both the popup button and the tray menu; only one instance
  ever exists; closing it never quits the app.
- Opening the window shows the mouse's **current DPI** read live from hardware; "unknown"
  and disabled DPI controls when the mouse is absent/asleep.
- Applying a DPI value changes the mouse's sensitivity, is confirmed by read-back, and
  **persists across a reboot** (proves the onboard VARSTORE write).
- Threshold and run-at-startup edits persist (threshold → `settings.json`; startup → HKCU
  Run key) and the tray checkmark stays in sync.
- Battery polling continues uninterrupted while the window is open and DPI is applied; no
  HID handle errors; idle footprint unchanged (~0% CPU, ~23 MB private).
- All new protocol code covered by pure unit tests; existing 22 tests stay green.

## 4. User experience

**Entry points (one shared window):**
- Popup: enable the existing "Settings" `ui:Button` → raises `SettingsRequested`.
- Tray right-click menu: new "Settings" item (inserted between "Run at startup" and the
  existing separator) → raises `SettingsRequested`.
- `AppHost` reuses a single `SettingsWindow?`: if visible, `Activate()`; else create + show.

**Window layout** (a `ui:FluentWindow`, dark, solid backdrop to match the popup, a
`ScrollViewer` of `ui:CardControl` rows):
1. **Low-battery alert** — `ui:NumberBox` (1–100, step 5).
2. **Run at startup** — `ui:ToggleSwitch` (read fresh from `StartupRegistration.IsEnabled()` on open).
3. **Mouse DPI** — "Current: N DPI" label, a `Slider` (100–30000) bound two-way to a
   `ui:NumberBox` (same VM value), and an **Apply DPI** button.
4. **Advanced** (`ui:CardExpander`, collapsed) — on-battery and charging poll seconds (`ui:NumberBox`).

**Buttons:** **Apply DPI** (writes to the mouse immediately) and **Close**. App settings
(threshold, cadence) are saved when the window closes; run-at-startup writes through
immediately on toggle.

**DPI interaction rules:**
- DPI is read **on open** to seed the slider/number box. Never seed on a null/failed read
  (show "unknown" instead); a genuine 100 DPI is valid and displays normally.
- Apply writes **only on the explicit button** — *not* on slider drag (avoids spamming HID writes).
- After a successful write, do a **read-back** and compare: show "Applied (N DPI)" only if it
  matches; otherwise "Couldn't confirm — wiggle the mouse and retry."
- When the device is Unknown/absent, the DPI slider/number/Apply are **disabled** and the
  label reads "Current: unknown."
- The VM clamps DPI to 100–30000 (controls' Min/Max are not sufficient — binding can push
  transient values).

## 5. Architecture & components

Layering is unchanged; each layer gains a thin, DRY addition:

```
RazerProtocol (pure)  →  RazerDevice (HID)  →  BatteryMonitor (serialize)  →  AppHost  →  SettingsWindow/VM
   + DPI build/parse       + Get/SetDpiAsync     + Get/SetDpi pass-throughs    + wiring     + UI
```

### 5.1 Protocol — `Hid/RazerProtocol.cs` (pure, unit-tested)

New constants: `CommandClassDpi = 0x04`, `CommandIdGetDpi = 0x85`, `CommandIdSetDpi = 0x05`,
`DataSizeDpi = 0x07`, `DpiMin = 100`, `DpiMax = 30000`.

Two DRY extractions (existing public signatures preserved — they're called by tests,
`RazerDevice`, and `ProbeCommand`):
- `private static byte[] BuildReport(byte transactionId, byte dataSize, byte commandClass, byte commandId, ReadOnlySpan<byte> payload)` — assembles the 90-byte report (`[1]=tid, [5]=dataSize, [6]=class, [7]=id`, payload at `[8]+`, CRC at `[88]`) into the 91-byte buffer; `report[0]` (status), `report[89]` (reserved), and every byte not explicitly set stay `0x00`. `BuildFeatureBuffer` delegates to it passing `DataSize` + `CommandClassPower` + an empty payload → **byte-identical** battery buffer (pinned by the existing regression tests, which must pass before and after the refactor).
- `private static ReplyResult ValidateReply(byte[] buffer91)` — status byte + XOR CRC over `[3..88]` vs `[89]`. `ParseReply` is refactored to call it, then read `buffer[10]` (behavior unchanged).

New public methods:
- `byte[] BuildGetDpiBuffer(byte transactionId)` — args all zero (`arg[0]=0x00` NOSTORE).
- `byte[] BuildSetDpiBuffer(byte transactionId, int dpiX, int dpiY)` — clamps to 100–30000;
  `arg[0]=0x01` (VARSTORE = persist), `arg[1..2]=X` big-endian, `arg[3..4]=Y` big-endian, rest 0.
- `ReplyResult ParseDpiReply(byte[] buffer91, out int dpiX, out int dpiY)` — `ValidateReply`,
  then `X=(buffer[10]<<8)|buffer[11]`, `Y=(buffer[12]<<8)|buffer[13]`. **Range guard:** if a
  decoded value is outside 100–30000, return `Failed` (defends against a wrong-layout firmware
  reply that still has a valid CRC).

### 5.2 Device — `Hid/IRazerDevice.cs`, `Hid/RazerDevice.cs`, `Hid/DpiSetting.cs`

- New value type: `public readonly record struct DpiSetting(int X, int Y);` (mirrors `BatteryReading`).
- Interface gains: `Task<DpiSetting?> GetDpiAsync(CancellationToken ct)` (null = couldn't read)
  and `Task<bool> SetDpiAsync(int dpiX, int dpiY, CancellationToken ct)`.
- Extract the transport currently inlined in `QueryAsync` into
  `private async Task<byte[]?> ExchangeAsync(byte[] request, CancellationToken ct)`
  (SetFeature → delay → GetFeature → one busy retry, where "busy" is detected from the raw
  reply status `reply[1] == 0x01` so battery and DPI keep identical retry behavior).
  `QueryAsync` (battery) and both DPI methods call it. **Fix:** on `HidD_SetFeature`/`HidD_GetFeature` returning false,
  `CloseHandle()` before returning null so the next `EnsureOpen` re-acquires (today a false
  return leaks a stale handle).
- `GetDpiAsync` / `SetDpiAsync`: `EnsureOpen` → `ResolveTransactionIdAsync` (reuses cached
  `0x1f`) → build → `ExchangeAsync` → parse, wrapped in the same
  `catch (Exception ex) when (ex is not OperationCanceledException) { CloseHandle(); LogOnce(ex); }`
  pattern as `ReadAsync`. `SetDpiAsync` returns true only if the reply parses `Success`.

### 5.3 Monitor — `Monitoring/BatteryMonitor.cs`

The device holds **one** HID handle; DPI ops must not race the battery poll. Add thin
pass-throughs that share the existing `SemaphoreSlim _readLock`:
- `Task<DpiSetting?> GetDpiAsync()` and `Task<bool> SetDpiAsync(int x, int y)`.
- **Critical:** these take the lock with a **blocking** `await _readLock.WaitAsync(ct)` —
  *not* `PollAsync`'s skip-if-busy `WaitAsync(0)` — so an Apply is never silently dropped
  while a poll is in flight. They honor the monitor's CT and use the same try/finally shape.
- Expose a **device-present** signal (`DevicePresent = State.Status == Online`) so the VM can
  bind the DPI controls' enabled-state. `DeviceStatus` is only `{Unknown, Online}`, and a
  recently-Online but *asleep* mouse can still read `Online` until staleness flips it — so a
  **null `GetDpiAsync` on open also forces** "Current: unknown" + disabled controls,
  regardless of `Status`.

### 5.4 UI — `Ui/SettingsWindow.xaml(.cs)`, `Ui/SettingsViewModel.cs`

- `SettingsWindow` is a `ui:FluentWindow` (`WindowBackdropType=None` → solid themed dark to
  match the popup; `WindowStartupLocation=CenterScreen`; resizable with a `ScrollViewer` so
  content scrolls rather than clips). No new dependencies — WPF-UI 4.3.0 is already
  referenced and its `ThemesDictionary{Dark}`+`ControlsDictionary` are merged globally in
  `AppHost`, so `ui:` controls style correctly with no per-window setup.
- `SettingsViewModel : INotifyPropertyChanged` (same `Set<T>`/`OnChanged` pattern as
  `PopupViewModel`): holds editable copies of the app settings + DPI state (`int Dpi`,
  `string CurrentDpiText`, `string DpiStatus`, `bool DevicePresent`). `SetCurrentDpi(DpiSetting?)`
  updates the label and seeds `Dpi`; the `Dpi` setter clamps. `ToSettings()` writes back.
- Code-behind stays thin: exposes events the `AppHost` wires (`SaveRequested`,
  `StartupToggled(bool)`, `ApplyDpiRequested(int)`) and methods to push results
  (`SetCurrentDpi`, `SetDpiStatus`, `SetDevicePresent`). Three independent actions: the
  startup toggle and Apply DPI take effect immediately; threshold/cadence persist on Close.

### 5.5 Wiring — `AppHost.cs`, `Ui/PopupWindow.xaml(.cs)`, `Ui/TrayIconController.cs`

- Popup: remove `IsEnabled="False"`/"Coming soon" from the Settings button, add
  `Click="OnSettings"` → `SettingsRequested` (mirrors `OnRefresh`/`RefreshRequested`).
- Tray: add a "Settings" menu item raising `SettingsRequested`.
- `AppHost`: hold `private SettingsWindow? _settingsWindow`. `OpenSettings()` reuses the
  single window (Activate if visible; else construct with current settings + fresh
  `_startup.IsEnabled()`, subscribe events, `Closed += () => _settingsWindow = null`, show,
  then fire-and-forget an initial `_monitor.GetDpiAsync()` and marshal the result to the
  window via the existing `Dispatch`). `SaveRequested` (on Close) copies threshold/cadence
  into `_settings.Settings` + `Save()`. `StartupToggled(bool)` immediately calls
  `_startup.Enable()/Disable()` and `_tray.SetStartupChecked(...)` to keep the tray item in
  sync — note the existing tray `SetStartup` handler only enables/disables, so the window
  path adds the tray-check sync. `ApplyDpiRequested` runs `_monitor.SetDpiAsync` off-thread, then a
  read-back, then `Dispatch` the confirmation. `Quit()` also closes `_settingsWindow`.

### 5.6 Settings persistence — `Settings/AppSettings.cs`, `JsonSettingsStore`

- Persisted in `settings.json` exactly as today (System.Text.Json). Threshold + poll fields
  already exist and are simply surfaced. **No new fields are required** (theme is cut;
  run-at-startup lives in the registry, not JSON; DPI lives on the mouse).
- **DPI is never written to `settings.json`** — it is device state in the mouse's onboard
  memory (the SET uses VARSTORE), so the window always reads the live value on open. Caching
  it would risk showing stale data if DPI is changed by another tool.
- `JsonSettingsStore` must tolerate older `settings.json` files (missing fields → defaults).

## 6. DPI HID protocol (verified) + gating hardware check

Verified against OpenRazer `master` (the Naga V2 Pro, PIDs `0x00A7`/`0x00A8`, is explicitly
supported with `get_dpi_xy`/`set_dpi_xy`, `DPI_MAX = 30000`). Reuses our existing 90-byte
report, CRC (XOR `[2..87]`), and **transaction id `0x1f`**:

| Op | class | id | data_size | request args | reply |
| --- | --- | --- | --- | --- | --- |
| GET DPI | `0x04` | `0x85` | `0x07` | `arg[0]=0x00` (NOSTORE) | X=`buffer[10..11]` BE, Y=`buffer[12..13]` BE (`arg[0]`=varstore echo) |
| SET DPI | `0x04` | `0x05` | `0x07` | `arg[0]=0x01` (VARSTORE), `arg[1..2]=X` BE, `arg[3..4]=Y` BE | echoes args |

**Gating hardware verification (must happen before trusting `ParseDpiReply`):** OpenRazer has
a second, byte-style DPI getter for some mice; the V2 Pro's reply layout is inferred by
analogy. Before relying on the parse offsets, set DPI to a known round value (e.g. 1600 =
`0x0640`) and dump the full 91-byte GET reply hex (extend `--probe` or a one-shot call);
confirm `buffer[10..11] = 0x06,0x40`. This is a **gating task** in the plan, not a footnote.

## 7. Error handling & edge cases

- **Mouse asleep/offline on open:** `GetDpiAsync` → null → "Current: unknown", DPI controls
  disabled; never seed slider to 0/Min.
- **Apply while asleep:** HID write may "succeed" but be ignored; the **read-back compare**
  surfaces "couldn't confirm" instead of a false success.
- **Out-of-range decode:** `ParseDpiReply` returns `Failed` → treated as a failed read.
- **Rapid Apply clicks:** serialized by the blocking `_readLock`; disable the button while
  in flight.
- **Device unplugged between open and Apply:** Apply re-`EnsureOpen`s and fails gracefully.
- **Quit with a DPI op mid-flight:** the op honors the monitor CT; `Dispose`'s
  `_readLock.Wait(1000)` timeout still applies — no deadlock.
- **Older `settings.json`:** deserializes with defaults for any absent field.

## 8. Threading & concurrency

All HID I/O stays off the UI thread. DPI calls route through `BatteryMonitor` and take the
shared `_readLock` with a **blocking** `WaitAsync(ct)` (battery polls keep their skip-if-busy
`WaitAsync(0)`). Results marshal back to the window via `AppHost.Dispatch`
(`Dispatcher.Invoke`). A DPI round-trip can take ~0.4–1.0 s, so it is never run on the
Dispatcher.

## 9. Testing strategy

**Pure unit tests** (xUnit, `RazerProtocolTests` style):
- Refactor regression: existing battery/charging buffer + CRC tests stay green; add a test
  asserting `BuildReport(empty)` == the old hardcoded battery buffer byte-for-byte.
- `BuildGetDpiBuffer(0x1f)` layout + CRC.
- `BuildSetDpiBuffer(0x1f, 1600, 1600)`: `0x05` id, `0x01` varstore, `0x0640` X/Y big-endian, CRC.
- `BuildSetDpiBuffer` clamps: 50 → 100 (`0x0064`), 99999 → 30000 (`0x7530`).
- `ParseDpiReply`: success decode; status `0x01` → Busy; bad CRC → Failed; out-of-range → Failed.
- `ValidateReply` extraction: battery `ParseReply` results unchanged.
- `SettingsViewModel`: ctor copies settings; `ToSettings()` round-trips; `SetCurrentDpi`
  seeds/clamps; `SetCurrentDpi(null)` → "unknown".

**Behavior tests (fake device):** extend `FakeRazerDevice` with `Dpi`/`SetDpiResult`/
`LastSetX`/`LastSetY`. Assert `BatteryMonitor.SetDpiAsync` routes clamped X=Y to the device,
that it **blocks** for the lock (not skip), and that a concurrent poll skips.

**Manual hardware (gating + acceptance):**
1. **(Gating)** Dump GET-DPI reply hex; confirm offsets (§6).
2. Apply 1600 via the window → read-back confirms → reboot → DPI persists.
3. Apply while the mouse is asleep → graceful "couldn't confirm."
4. Open Settings during active polling, Apply repeatedly → no missed clicks, battery keeps
   updating, no HID errors.
5. Toggle run-at-startup → tray checkmark + HKCU key both update.
6. Unplug mouse → DPI controls disabled, "unknown"; replug → enabled with current DPI.

## 10. Out of scope / future

DPI stages, poll-rate get/set, theme switcher (needs full theme-brush migration), and
multi-device/dock support are deferred. The protocol/device extractions
(`BuildReport`/`ValidateReply`/`ExchangeAsync`) make stages and poll-rate thin additions
later (new constants + one build/parse pair + a UI section), with no rework of the active-DPI
path.

## 11. Global constraints (carried over from v1)

- **No admin/UAC.** Per-user only. Single-instance mutex unchanged.
- **Target framework** `net10.0-windows10.0.19041.0`; WPF + WinForms; WPF-UI **4.3.0**
  (confirmed current, .NET 10-ready) — no new dependencies.
- **HID:** VID `0x1532`, mouse PID `0x00A8` (wireless) / `0x00A7` (wired); 90-byte report,
  91-byte feature buffer; CRC XOR `[2..87]`; transaction id `0x1f`.
- **DRY, YAGNI, TDD, frequent commits.** Conventional-commit messages. Surgical changes that
  preserve existing style.

## File structure

```
Create:
  src/NagaBatteryTray/Hid/DpiSetting.cs            DpiSetting record struct
  src/NagaBatteryTray/Ui/SettingsWindow.xaml(.cs)  FluentWindow + CardControl rows
  src/NagaBatteryTray/Ui/SettingsViewModel.cs      INotifyPropertyChanged VM
Modify:
  src/NagaBatteryTray/Hid/RazerProtocol.cs         DPI constants, BuildReport/ValidateReply, Build/Parse DPI
  src/NagaBatteryTray/Hid/IRazerDevice.cs          Get/SetDpiAsync
  src/NagaBatteryTray/Hid/RazerDevice.cs           ExchangeAsync extraction, Get/SetDpiAsync, CloseHandle-on-false
  src/NagaBatteryTray/Monitoring/BatteryMonitor.cs Get/SetDpi pass-throughs (blocking lock), device-present
  src/NagaBatteryTray/Settings/AppSettings.cs      (no new fields required)
  src/NagaBatteryTray/Ui/PopupWindow.xaml(.cs)     enable Settings button, SettingsRequested
  src/NagaBatteryTray/Ui/TrayIconController.cs      Settings menu item, SettingsRequested
  src/NagaBatteryTray/AppHost.cs                    single-window wiring, open/save/apply-DPI, lifecycle
  src/NagaBatteryTray/Diagnostics/ProbeCommand.cs  (optional) GET-DPI hex dump for the gating check
  tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs  implement new interface members + assertion fields
  tests/NagaBatteryTray.Tests/RazerProtocolTests.cs     DPI build/parse/clamp tests
```
