# Phase B: Button Remapping (Naga V2 Pro thumb grid) — Design

**Date:** 2026-06-21
**Status:** **DRAFT — spike-gated.** Stage 1 (`--probe-buttons` diagnostic) is built and run on hardware
first; Stage 2 (the resident remap feature) is built **only** on what the spike confirms. A spike FAIL is
a documented close-out (Phase C precedent), never a constraint-violating workaround.
**Builds on:** Phase 2-A (Settings + active DPI). Its §3.1 non-functional invariants
(`2026-06-20-naga-settings-dpi-design.md`) bind this phase **unchanged**.

## 1. Goal

Let a user reassign the **12 thumb-grid side buttons** of the Razer Naga V2 Pro — the headline feature
of the mouse — without Razer Synapse, the same way we already read/set DPI: a **passive, on-demand HID
feature-report write** that the mouse's **own firmware** applies. The remap is therefore *onboard*: after
the write, the firmware emits the bound usage itself and **our app is never in the input loop**.

MVP target: bind any thumb button to a **single keyboard key, optionally with modifiers held**
(`Ctrl`/`Shift`/`Alt`/`Win` + key — e.g. `Ctrl+Shift+M`), or **disable** it. That is one onboard
keyboard action per button; chord-style combos are in, recorded *sequences*/macros are not (they are
software-resident — see §2).

This is a **research-gated** phase. The remap command is reverse-engineered from the **sibling Basilisk
V3**, and the one piece that exists in **no published source** — the V2 Pro's 12 thumb-grid **button-ID
table** — plus the proof that V2 Pro firmware **accepts and persists** a directly-written onboard map,
must come from a hardware **feasibility spike** that gates everything downstream.

## 2. Scope

**In scope (Phase B):**
- **Stage 1 — feasibility spike:** a `--probe-buttons` diagnostic that (a) proves the V2 Pro accepts our
  remap write using a **known** shared Basilisk button ID, (b) **discovers the 12 thumb-grid button IDs**,
  and (c) tests **which write modes persist** (volatile "direct" profile vs. onboard slot). The high-churn
  discovery loop uses the **volatile** profile → **no flash wear from the scan**; only the persistence test
  makes one deliberate onboard-slot write. CLI path only; no resident-app behavior change.
- **Stage 2 — minimal remap feature** (built only on a spike PASS): bind each of the 12 grid buttons to a
  **keyboard key (+modifiers)** or **disabled**, persisted in `settings.json`, applied through the
  existing device/monitor layer, configured from a new **Settings-window "Buttons" section**.

**Deferred / out of scope (this phase):**
- **Macros / recorded keystroke sequences** — the firmware stores **one action per button**, not a
  script; authoring/playback is Synapse-resident. Out by design.
- **HyperShift / held-button secondary layer**, **per-application auto-switching**, **Type-Text /
  Unicode**, **Launch-Program**, **inter-device** — all require a live software arbiter that observes/
  rewrites input. **Categorically disqualified** by the gating constraint (§4.x), not merely deferred.
- **Mouse-button / DPI-stage / media-key** targets — onboard-capable and constraint-compatible, but kept
  out of the MVP to minimise the spike's verification surface. Additive later (§11).
- **Main L/R/M/wheel/DPI-clutch** remapping — only the **thumb grid** is in scope first (its IDs are the
  unknown; the main buttons already have a Basilisk table). Additive later.
- Per-button **RGB/lighting** (separate command class `0x03`; not stored onboard on this device).

**Decided by the spike (not guessed here):**
- The concrete **12-entry thumb-grid button-ID table** (`NagaV2ProButtons`).
- The **persistence model** Stage 2 ships: *volatile direct + re-apply on connect* vs. *onboard slot,
  written once*. The spike tests both; §6 records the result and §5.3/§5.4 branch on it.

## 3. Success criteria

**Stage 1 (the gate):**
- `--probe-buttons` builds and sends `RazerProtocol.BuildSetButtonBuffer(...)`; binding a **known**
  Basilisk ID (e.g. wheel-click) to a marker key visibly takes effect → **V2 Pro accepts the write**.
- The diagnostic captures a recorded **physical-position → button-ID table** for all 12 grid buttons
  (§6), and records **which write modes persist** (volatile applies instantly; does slot 1 survive an
  unplug/replug with **all Razer software absent**?).
- `--probe-buttons --reset` restores the grid to factory keys; discovery touches **only** the volatile
  profile, so a replug also fully restores factory behaviour at any time.

**Stage 2 (only if the gate passes):**
- From the Settings "Buttons" section, a user binds a thumb button to a key (+modifiers) or disables it;
  pressing that physical button then emits the bound usage — **with Synapse not running**.
- A bound key, read back via the device (`0x028c`), matches what was written (write-then-verify).
- With **no remaps configured**, behaviour is byte-for-byte identical to today.
- Idle footprint returns to baseline (~0% CPU, ~23 MB private working set); **mouse input latency
  unchanged** before/during/after a remap. No new background timer/thread. All existing tests stay
  green; new logic is unit-tested via the `IRazerDevice` seam.

## 3.1 Non-functional invariants (HARD, GATING requirement)

Phase 2-A §3.1 applies in full and is **re-affirmed**. A remap is a **persistent device-side change**,
so it is handled with *more* care than DPI, never less:

**Lightweight / zero input-latency:**
- A remap is **one `HidD_SetFeature`** on the USB **control endpoint** — the *identical transport and report
  envelope* as the shipping DPI VARSTORE set (same 91-byte buffer, same CRC range, same passive
  control-endpoint path), differing only in command class/id/data_size/args. It **never** claims the mouse's
  (or keyboard's) input collection, never
  touches the interrupt-IN endpoint carrying movement/clicks, and adds **no resident input-observing
  process**. After the write the firmware emits the bound usage; **we are not in the input path at all.**
- Writes are **off the UI thread** (`Task.Run`) and **serialized on the existing `BatteryMonitor._readLock`**
  (the same lock battery+DPI already share). Never a concurrent feature transfer.
- Writes are **user-action-triggered only** (like `SetDpi`) — **no poll, no new timer/thread.** If the
  spike selects the *re-apply* persistence model, re-application **piggybacks the existing startup +
  `DeviceChangeWatcher` debounced-refresh** path — still event-driven, no new persistent timer.
- **Flash-wear discipline:** onboard flash has finite write-endurance. Writes are **deliberate**
  (explicit user Apply), **read-back-verified**, and **coalesced** (write only buttons that changed). The
  spike uses the **volatile** profile for the high-churn discovery loop.
- **No new dependencies.**

**Verification (gating acceptance — see §9):** footprint back to baseline after a remap, and mouse input
latency unchanged before vs. during/after writes. A build failing either is not shippable. Binds this phase.

## 4. User experience

- **Stage 1:** developer/CLI only (`--probe-buttons`, `--probe-buttons --reset`). No resident change.
- **Stage 2 default:** unchanged until the user opens Settings. The tray/popup are untouched by this phase.
- **Settings → "Buttons":** a representation of the 12-button thumb grid; each button shows its current
  binding and offers a **key capture/picker** (press-to-capture or dropdown) with **modifier toggles**
  (Ctrl/Shift/Alt/Win), plus a **"Disabled"** choice and a **"Default"** (restore factory key) choice.
  **Apply** writes the changed buttons through the monitor and read-back-verifies; **Close** without
  Apply changes nothing (consistent with the DPI panel).
- **Coexistence note (surfaced in UI/README):** remaps assume **Synapse is not running**; if Synapse is
  installed and active it may override the onboard binding at runtime (documented V2 Pro behaviour). We
  are a Synapse *replacement*, so this is acceptable but stated.

## 5. Architecture & components

Spike-gated, additive, and aligned with the existing layering. **One** new pure-protocol builder
(`BuildSetButtonBuffer`) is shared by both stages; Stage 2 adds a read-back parser, a small action model,
a thin device/monitor pass-through, settings, and one Settings-window section.

```
Stage 1:  RazerProtocol.BuildSetButtonBuffer (+ test vectors)
          → ProbeCommand.--probe-buttons (acceptance-probe → grid-discovery → persistence-test → restore)
          → Program.cs dispatch                                            [one-shot, passive, CLI only]

Stage 2:  RazerProtocol (BuildGetButtonBuffer/ParseButtonReply, ButtonBinding model, NagaV2ProButtons)
          → RazerDevice.SetButtonAsync/GetButtonAsync (existing ExchangeAsync)
          → BatteryMonitor.SetButtonAsync (+ ApplyRemaps on connect, if re-apply model) [shared _readLock]
          → AppSettings (RemapTable) + AppHost wiring
          → SettingsWindow/SettingsViewModel "Buttons" section
```

### 5.1 Protocol — `Hid/RazerProtocol.cs` (shared; testable today)

Add, mirroring `BuildSetDpiBuffer` exactly:

```csharp
public const byte CommandClassButton = 0x02;
public const byte CommandIdSetButton = 0x0c;   // write
public const byte CommandIdGetButton = 0x8c;   // read-back
public const byte DataSizeButton    = 0x0a;    // 10 arg bytes

// MVP function categories only (YAGNI — deferred mouse/DPI/media categories live in §6 prose,
// added when those targets land):
public const byte FnDisabled = 0x00, FnKeyboard = 0x02;

/// args = [profile, buttonId, hypershift, category, dataLen, data0..data4].
/// hypershift is a fixed wire-format byte (always 0x00 this phase); the HyperShift secondary-layer
/// FEATURE stays permanently out (§10) — the parameter only satisfies the Basilisk report layout.
public static byte[] BuildSetButtonBuffer(byte tid, byte profile, byte buttonId, byte hypershift,
                                          byte category, ReadOnlySpan<byte> data)
{
    Span<byte> args = stackalloc byte[10];
    args[0] = profile; args[1] = buttonId; args[2] = hypershift;
    args[3] = category; args[4] = (byte)Math.Min(data.Length, 5);
    for (int i = 0; i < data.Length && i < 5; i++) args[5 + i] = data[i];
    return BuildReport(tid, DataSizeButton, CommandClassButton, CommandIdSetButton, args);
}
```

- **Keyboard payload** (`category 0x02`): `data = [modifierBitmask, hidUsage]`. Modifier bits:
  `0x01 LCtrl, 0x02 LShift, 0x04 LAlt, 0x08 LGUI, 0x10 RCtrl, 0x20 RShift, 0x40 RAlt, 0x80 RGUI`;
  `hidUsage` per USB HUT 1.5 (e.g. `A`=0x04). **Disabled** (`category 0x00`): zero-length data.
- **TDD anchor (works before any hardware):** assert `BuildSetButtonBuffer` reproduces the documented
  Basilisk vectors byte-for-byte — tilt-wheel-left (scroll L, `0x34`)→Ctrl+C must yield args `01 34 00 02 02 01 06 00 00 00`
  (profile 1, button 0x34, no hypershift, keyboard, len 2, LCtrl, `C`), with the XOR CRC at `report[88]`.
  This locks the layout independently of the spike.
- **Stage 2 read-back:** `BuildGetButtonBuffer(tid, profile, buttonId, hypershift)` reuses `DataSizeButton`
  (`0x0a`) and a **10-byte zero-padded** arg span with only `[0]=profile, [1]=buttonId, [2]=hypershift` set
  — mirroring `BuildGetDpiBuffer` (id `0x8c`). `ParseButtonReply(buffer91, out category, out data)` reuses
  the existing `ValidateReply` (status + CRC over `buffer[3..88]`). Guards a wrong-layout reply like `ParseDpiReply`.

### 5.2 Stage 1 — `Diagnostics/ProbeCommand.cs` (`--probe-buttons`)

A transient one-shot CLI like `--probe`/`--probe-dpi`, **not** unit-tested, **not** in the resident
runtime. New `Program.cs` dispatch: `--probe-buttons` → `RunButtons()`, `--probe-buttons --reset` →
`RunButtonsReset()`. It opens the **mouse** control path exactly as the existing probes do (zero-access,
`GetMaxFeatureReportLength()==91`, auto-probed tx id). Steps, all on **volatile profile 0** unless noted:

1. **Acceptance + volatility probe.** Bind a **known** Basilisk ID (e.g. wheel-click `0x03`) → marker key
   (`F13`) on **volatile profile 0**. Prompt the user to press it; capture the emitted key via
   `Console.ReadKey`. *(Reading the console's own keystrokes through the normal OS input stack is orthogonal
   to the gating constraint, which forbids claiming the **mouse's** HID input collection — not ordinary
   keyboard input.)* `F13` ⇒ **firmware accepts our write** — disambiguating "rejected" from "wrong ID"
   before any grid guess. Then **confirm profile 0 is genuinely volatile/no-flash** (the bind clears on
   replug; a read-back shows the factory action) **before** the bulk scan. **If the write is rejected, or
   profile 0 is not volatile, abort** — do not run step 2.
2. **Grid discovery (bounded).** Across a **hard-bounded** candidate-ID scan range (a small documented
   window around the expected grid IDs; the plan pins the exact range and a max-writes ceiling), for each
   candidate ID: **first read its current factory action via `GetButton` and record it**, then bind it to a
   **distinct** marker key on **volatile profile 0**. Walk the user through pressing each of the **12 grid
   buttons in labeled order**; `Console.ReadKey` per press decodes **physical-position → button-ID**. Output
   is the §6 table — position → ID **and each button's captured factory action** (so restore needs no
   assumption about the default layout). Every write is volatile; the loop's write count is bounded by the range.
3. **Persistence test.** Write **one** discovered binding to the **volatile** profile (applies instantly?)
   **and** to **onboard slot 1** (does it survive **unplug/replug with all Razer software closed**?). Record
   both — this selects Stage 2's persistence model. This is the spike's **one** deliberate onboard-slot write.
4. **Restore.** Because discovery wrote **only** the volatile profile, an **unplug/replug fully restores**
   factory behaviour at any time. `--probe-buttons --reset` is a convenience that rewrites each button's
   **captured factory action** (from step 2) without a replug, and restores the single slot-1 test button —
   so the mouse is never left stranded.

### 5.3 Stage 2 — device & monitor (`Hid/IRazerDevice.cs`, `Hid/RazerDevice.cs`, `Monitoring/BatteryMonitor.cs`)

- `IRazerDevice` gains `Task<bool> SetButtonAsync(ButtonBinding b, CancellationToken ct)` and (for verify)
  `Task<ButtonBinding?> GetButtonAsync(byte buttonId, CancellationToken ct)`. `FakeRazerDevice` implements
  both with assertion fields (last write, call counts) — the unit seam, mirroring the DPI fakes.
- `RazerDevice` implements them over the **existing `ExchangeAsync`** (SET→`SetReadDelayMs` wait→GET,
  busy-retry, close-on-failure) — no new transport. `tid != 0` gating as battery/DPI already do.
- `BatteryMonitor` gains `SetButtonAsync(...)` / `GetButtonAsync(...)` pass-throughs that acquire the
  **existing `_readLock`** (DPI blocks, poll skips-if-busy — unchanged). No cadence/timer change.
- **If the spike picks the re-apply model:** `BatteryMonitor.ApplyRemapsAsync(table)` writes only the
  bindings that **differ from factory default** (changed-only / idempotent) to the **volatile** profile;
  `AppHost` calls it on **startup** and from the **existing `DeviceChangeWatcher` debounced refresh** (mouse
  reconnect) — **no new persistent timer/thread**, and bounded by that **same existing debounce** so a
  flapping link cannot produce a write storm.
  **If the spike picks the onboard-slot model:** bindings are written **once on user Apply** to a slot; no
  per-connect re-write.

### 5.4 Stage 2 — settings, model & wiring (`Settings/AppSettings.cs`, `AppHost.cs`)

- `ButtonBinding` (a small value type: `buttonId`, `ActionKind { Default, Disabled, Key }`, `modifiers`,
  `hidUsage`) and a 12-entry `RemapTable` keyed by grid position. Persisted in `settings.json` (Roaming),
  corrupt/missing → defaults, exactly as today. The discovered `NagaV2ProButtons` ID table is a baked-in
  constant (filled from §6), so the stored table is position-indexed and firmware-id-stable.
- `AppHost` loads the table and (re-apply model) applies it on startup/device-change; passes the monitor
  to the Settings window so Apply routes writes through `_readLock`.

### 5.5 Stage 2 — UI (`Ui/SettingsWindow.xaml(.cs)`, `Ui/SettingsViewModel.cs`)

- A new "Buttons" section in the existing Settings window: 12 rows/cells (one per grid button) bound to the
  `RemapTable`; each offers key-capture + modifier toggles, **Disabled**, and **Default**. Apply writes the
  **changed** buttons via the monitor and read-back-verifies; a failed verify surfaces inline (no silent
  success). Follows the existing WPF-UI conventions, incl. the `NumberBox`/`UpdateSourceTrigger` gotcha for
  any numeric input. (A photo-accurate grid layout is a nicety; an MVP list of 12 labeled rows is acceptable
  and is what the plan will build first.)

## 6. Button-remap HID protocol (from prior art — to be hardware-verified in Stage 1)

The V2 Pro is expected to speak our existing 90-byte feature-report protocol (tx `0x1f`, CRC XOR
`[2..87]`). The remap command (reverse-engineered from the **Basilisk V3**, PID `0x0099`, tx `0x1f`):

| Op | class | id | data_size | args (`report[8..17]`) |
| --- | --- | --- | --- | --- |
| Set button | `0x02` | `0x0c` | `0x0a` | `[profile, buttonId, hypershift, category, dataLen, d0..d4]` |
| Get button (verify) | `0x02` | `0x8c` | `0x0a` | `[profile, buttonId, hypershift, 0,0,0,0,0,0,0]` → reply carries `[category,len,data…]` |

- **profile:** `0x00` = "direct"/volatile (applies instantly, no flash); `0x01..0x05` = onboard slots.
- **category (MVP):** `0x00` disabled (no data); `0x02` keyboard (`data=[modifierBitmask, hidUsage]`).
- **Worked Basilisk vectors (the TDD oracle):** tilt-wheel-left (scroll L, `0x34`)→Ctrl+C
  `args = 01 34 00 02 02 01 06 00 00 00`; tilt-wheel-right (scroll R, `0x35`)→Ctrl+V
  `01 35 00 02 02 01 19 00 00 00`. Basilisk button IDs (main/wheel only): `0x01` L, `0x02` R, `0x03` M,
  `0x04` rear-side, `0x05` front-side, `0x09` wheel-up, `0x0a` wheel-down, `0x34/0x35` scroll L/R (tilt-wheel).
  **The 12 Naga thumb-grid IDs are in no published table — Stage 1 captures them.**

**Source & confidence.** The command family is documented for the Basilisk V3 by `geezmolycos/razerqdhid`
(GPL — **reference only**; borrow bytes, never its WebHID/libusb transport) and cross-checked against the
keyboard-remap class `0x02` in openrazer #2031. **openrazer implements no mouse remapping for any device**,
so unlike battery/DPI there is **no authoritative reference to validate against** — confidence is **high**
that the command fits our envelope and that the in-scope categories are onboard, but **medium** that V2 Pro
firmware accepts and *persists* a directly-written map (the precise things the spike gates). Independent
proof the model needs no input interception: `razerqdhid` performs these onboard writes from a sandboxed
browser via WebHID `send/receiveFeatureReport` only — an environment that structurally cannot subscribe to
input. **Do not** assume the keyboard `0x0d` non-analog id applies to the mouse (mouse uses `0x0c`), and
**do not** mistake the LED custom-frame command (`0x03/0x0b`) for remapping.

**Gating spike — Stage 1 results (must be recorded before Stage 2 is trusted):**

| Question | Result (to fill from hardware) |
| --- | --- |
| Does the V2 Pro accept `0x020c` (known-ID acceptance probe)? | _TBD_ |
| The 12 thumb-grid button IDs (physical position → id) | _TBD_ |
| Does a **volatile** (profile 0) write apply instantly? | _TBD_ |
| Does an **onboard slot** (profile 1) write **persist** across unplug/replug with **no Razer software**? | _TBD_ |
| Required preamble/handshake before a write is accepted? **and does it alter normal input** (see gate) | _TBD_ |

**Gate:** PASS = command accepted **and** the 12 IDs captured **and** at least the volatile write honored →
Stage 2 proceeds (persistence model chosen from rows 3–4). FAIL = the firmware rejects the write, or no
write mode persists without a Synapse-class arbiter → **Phase B is closed out as non-viable on this
firmware** (documented like the Phase C dock relay), since the only durable alternative — a resident input
interceptor — violates §3.1. `--probe-buttons` is kept as the re-test tool.

**Input-feel acceptance (a §3.1 gate, not mere overhead):** if a write-preamble/handshake is required,
verify the mouse keeps emitting normal movement/clicks with **no input-feel change** throughout the
handshake→write→handshake window. A handshake that suspends or alters normal input is a **§3.1 FAIL /
close-out**, not "a few extra reports."

## 7. Error handling & edge cases

- **Write rejected / busy:** existing `ExchangeAsync` busy-retry + status validation; surfaced to the user
  on Apply (no false "saved").
- **Read-back mismatch:** Apply reports the button as not-applied rather than claiming success.
- **Mouse absent / unplugged mid-write:** `EnsureConnectedAsync` fails gracefully; handle dropped; (re-apply
  model) the binding re-asserts on the next device-change/connect.
- **Firmware doesn't persist (slot model):** caught by the spike → either ship the **re-apply** model or, if
  nothing persists, close the phase. Not papered over.
- **Unknown/again-default button:** "Default" rewrites the factory key; a never-touched button is never
  written (flash-wear discipline).
- **Synapse running concurrently:** may override at runtime; documented (§4). We do not fight it.
- **Wireless vs wired link:** writes target whichever interface is **live** (the existing wired-first,
  verify-it-answers selection); the spike checks both links, since an onboard write while wired could
  target a different store than the wireless link reads.
- **Smart App Control:** may veto a freshly-built unsigned binary by hash (`0x800711C7`); the documented
  dev-host / `-p:Deterministic=false` workaround applies to running the spike binary, not the protocol.

## 8. Threading & concurrency

All remap HID I/O is **off the UI thread**, inside `BatteryMonitor` under the **single shared `_readLock`**
already used by battery+DPI — never a second concurrent feature transfer. UI updates marshal back via the
existing `Dispatch`. (Re-apply model) re-application runs inside the existing debounced device-change
handler / startup, off-UI, under the same lock. **No new synchronization primitives, timers, or threads.**

## 9. Testing strategy

**Stage 1 (manual, hardware-in-the-loop — this *is* the gate):** run `--probe-buttons` to fill §6; confirm
acceptance, capture the 12 IDs, record which write modes persist; `--probe-buttons --reset` restores.

**Stage 2 (unit, via `IRazerDevice` + `FakeRazerDevice`):**
- `BuildSetButtonBuffer` reproduces the Basilisk vectors byte-for-byte; CRC over `[2..87]`; `DataSizeButton`
  and class/id correct. (Runs **now**, pre-spike.)
- `BuildGetButtonBuffer`/`ParseButtonReply` round-trip; a wrong-layout reply → `Failed`.
- `SetButtonAsync` routes the binding to the device and returns its result; `GetButtonAsync` decodes it.
- (Re-apply model) `ApplyRemapsAsync` writes every configured binding on connect; an empty table writes
  nothing (assert zero device calls — protects the no-extra-I/O invariant).
- A button left "Default" is never written on Apply (coalesced-write / flash-wear assertion).
- `RemapTable` round-trips through `JsonSettingsStore`; corrupt JSON → defaults; `SettingsViewModel`
  edits map to the right `ButtonBinding`s.
- All existing tests stay green. HID transport, WPF window, and tray remain manual via the installed build
  + `--probe-buttons` (same boundary as today).

**Gating acceptance (manual):** after a batch of remap writes, idle CPU returns to ~0% and private working
set to ~23 MB; mouse input latency unchanged at idle, during Apply, and during a connect-time re-apply.

## 10. Out of scope / future

Mouse-button / DPI-stage / media targets, main-button remapping, a photo-accurate grid UI, and per-button
lighting are deferred but **additive** on this design (same command, more categories/IDs). Macros,
HyperShift, per-app profiles, Type-Text, and Launch-Program remain **permanently out** under §3.1.

## 11. Global constraints (carried over)

- **No admin/UAC; per-user only; single-instance mutex unchanged.**
- **Target** `net10.0-windows10.0.19041.0`; WPF + WinForms; WPF-UI 4.3.0 — **no new dependencies.**
- **HID:** VID `0x1532`; mouse PID `0x00A8`/`0x00A7`; 90-byte report, 91-byte feature buffer; CRC XOR
  `[2..87]`; tx `0x1f`. Remap: class `0x02`, set `0x0c` / get `0x8c`, `data_size 0x0a`,
  args `[profile,buttonId,hypershift,category,len,d0..d4]`.
- **Lightweight + zero mouse-input-latency are hard, gating requirements** (§3.1): one passive
  control-endpoint feature write per changed button, off-UI, serialized on the one `_readLock`,
  user-action-triggered (or re-applied via the existing event-driven device-change/startup path — no new
  timer/thread); deliberate, read-back-verified, coalesced writes; footprint + latency verified as
  acceptance gates.
- **Reference-only prior art (all GPL):** re-derive protocol bytes; never copy code, never adopt the
  interface-claiming transport. See CLAUDE.md "References / prior art."
- **DRY, YAGNI, TDD, frequent commits, conventional-commit messages, surgical changes.**

## File structure

```
Stage 1 (spike):
Modify:
  src/NagaBatteryTray/Hid/RazerProtocol.cs            BuildSetButtonBuffer (+ button class/id/data-size consts)
  src/NagaBatteryTray/Diagnostics/ProbeCommand.cs     RunButtons()/RunButtonsReset(): acceptance, discovery, persistence, restore
  src/NagaBatteryTray/Program.cs                      dispatch --probe-buttons [--reset]
  tests/NagaBatteryTray.Tests/RazerProtocolTests.cs   BuildSetButtonBuffer vs Basilisk vectors + CRC (pre-spike)

Stage 2 (resident feature, gated on Stage 1):
Modify:
  src/NagaBatteryTray/Hid/RazerProtocol.cs            BuildGetButtonBuffer/ParseButtonReply, ButtonBinding model, NagaV2ProButtons table
  src/NagaBatteryTray/Hid/IRazerDevice.cs             SetButtonAsync/GetButtonAsync
  src/NagaBatteryTray/Hid/RazerDevice.cs              implement over existing ExchangeAsync
  src/NagaBatteryTray/Monitoring/BatteryMonitor.cs    SetButtonAsync/GetButtonAsync pass-throughs (+ ApplyRemapsAsync, if re-apply model) under _readLock
  src/NagaBatteryTray/Settings/AppSettings.cs         RemapTable persistence
  src/NagaBatteryTray/AppHost.cs                      load table; (re-apply) apply on startup + device-change; pass monitor to Settings
  src/NagaBatteryTray/Ui/SettingsViewModel.cs         "Buttons" section: per-button binding edits
  src/NagaBatteryTray/Ui/SettingsWindow.xaml(.cs)     12-button grid/list with key capture + modifiers + Disabled/Default + Apply
  tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs   SetButtonAsync/GetButtonAsync + assertion fields
  tests/NagaBatteryTray.Tests/RazerProtocolTests.cs      get/parse round-trip
  tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs     routing + (re-apply) apply-on-connect + no-extra-write
  tests/NagaBatteryTray.Tests/SettingsViewModelTests.cs  binding edits → ButtonBinding
  tests/NagaBatteryTray.Tests/JsonSettingsStoreTests.cs  RemapTable persistence round-trip + corrupt-JSON → defaults
```
