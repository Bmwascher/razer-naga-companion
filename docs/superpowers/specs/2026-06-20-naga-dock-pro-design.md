# Phase C: Mouse Dock Pro Charger Support — Design

**Date:** 2026-06-20
**Status:** Approved (ready for implementation plan)
**Builds on:** Phase 2-A (Settings + active DPI). See `2026-06-20-naga-settings-dpi-design.md`
(its §3.1 non-functional invariants bind this phase unchanged).

## 1. Goal

Make the tray app's battery/charging readout **correct and fresh while the mouse sits on the
Razer Mouse Dock Pro**, by talking to the dock (USB PID `0x00A4`) as a second passive HID
endpoint. Two end goals:

1. **Charging-on-dock** — the tray/popup reflects when the mouse is charging on the dock (and,
   importantly, does **not** claim "charging" merely because the dock is connected).
2. **Battery via dock when the mouse is asleep** — when the mouse's own wireless endpoint
   (`0x00A8`) stops answering, fall back to reading battery/charging *through the dock* so the
   percentage stays live instead of going "unknown."

This is a **research-gated** phase: the relay protocol is community-reverse-engineered and
unverified, so a hardware **feasibility spike comes first** and gates everything downstream.

## 2. Scope

**In scope (Phase C):**
- **Stage 1 — feasibility spike:** a `--probe-dock` diagnostic that queries the dock (`0x00A4`)
  for battery (`0x07/0x80`) and charging (`0x07/0x84`) and dumps raw replies, run across a state
  matrix. Diagnostic CLI path only; no resident-app behavior change.
- **Stage 2 — minimal dock-aware fallback** (built only on what the spike confirms): read the
  mouse first; fall back to the dock relay **only** when the mouse read is `Absent`. Surface
  through the **existing** battery/charging UI — no new battery/charging widgets.
- **Optional:** a single popup line (`Dock: connected` / `charging`) shown only when the dock is
  present. Built last, kept or dropped based on the spike; never blocks goals 1–2.

**Deferred / out of scope:**
- Dock **RGB / charge-indicator LED** control (openrazer accessory `0x03/0x10`) — lighting, not
  charging state.
- Dock **pairing / nearby-mice discovery** (openrazer PR #2817 tx `0x3F` ids `0x46/0x41/0x42/0xb9`).
- Modeling the dock as a fully distinct device with its own settings/icon (beyond the optional line).
- Phase B (button remapping).

## 3. Success criteria

- `--probe-dock` enumerates the dock (`0x00A4`), finds its 91-byte feature collection, and prints
  the raw battery and charging replies (and decoded values) at each transaction id tried.
- We obtain a recorded **state-matrix table** (see §6) answering: does the dock relay battery and
  charging, at which tx id, and in which states does it answer when `0x00A8` is silent.
- **If the spike confirms a relay:** with the mouse asleep on the dock, the tray battery % and
  charging state stay live via the dock instead of decaying to "unknown."
- **In all cases:** when the dock is present but the mouse is **not** charging, the UI shows
  not-charging (never infers "charging" from dock presence).
- When the dock is **absent**, behavior is byte-for-byte identical to today (no fallback path taken).
- Idle footprint returns to baseline (~0% CPU, ~23 MB private working set); mouse input latency
  unchanged. No new background timers/threads. All existing tests stay green; new fallback logic
  is unit-tested via the `IRazerDevice` seam.

## 3.1 Non-functional invariants (HARD, GATING requirement)

Phase 2-A §3.1 applies in full and is **re-affirmed**; this phase adds a second device but must
not relax any of it. Specifics for the dock:

**Lightweight:**
- **No new timer or thread.** The dock read **piggybacks the existing battery poll** (cadence and
  mechanism unchanged; floor 15 s). It runs **at most once per poll, and only when the mouse read
  already returned `Absent`** — so a present, awake mouse adds zero dock traffic.
- The dock HID handle is opened **on demand** and released when no longer needed (mouse reachable
  again / dispose), mirroring the on-demand-window philosophy, so idle steady-state returns to baseline.
- **No new dependencies.**

**Zero mouse-input-latency regression:**
- The dock is opened with **zero desired access** + `FILE_SHARE_READ|WRITE` and exchanged via
  `HidD_Set/GetFeature` (USB **control endpoint**) — exactly the passive client model used for the
  mouse. We never claim the dock's (or mouse's) input collection.
- Dock and mouse feature transfers are **serialized through the single existing `_readLock`** —
  never concurrent feature transfers across the two handles.
- Dock I/O is **on-demand within a poll**, never its own periodic traffic. DPI is untouched and
  still never polled.

**Verification (gating acceptance — see §9):** footprint back to baseline after dock activity, and
mouse input latency unchanged before vs. during/after dock reads. A build failing either is not shippable.

## 4. User experience

- **Default:** no new UI. Battery % and the existing charging indicator simply stay correct while
  the mouse charges/sleeps on the dock. Goal 1 is largely realized by the *existing* charging read
  (`0x84`); the fallback keeps it (and the %) fresh when `0x00A8` goes silent (goal 2).
- **"Dock present, not charging":** shows not-charging — presence alone never implies charging.
- **Optional popup line** (build last; user opted in as nice-to-have): when the dock is enumerated,
  the popup shows one extra line — `Dock: connected`, upgrading to `Dock: charging` when the mouse
  is charging on it. Hidden entirely when no dock is present. No settings toggle in v1 of this phase.

## 5. Architecture & components

Spike-gated. Stage 1 is self-contained and ships nothing into the resident app. Stage 2 reuses the
existing layering; **no new protocol code** (the dock speaks our exact 90-byte report).

```
Stage 1:  ProbeCommand.--probe-dock  ── one-shot, passive, CLI only
Stage 2:  RazerProtocol (unchanged) → RazerDevice (PID-parameterized) → BatteryMonitor (mouse-first,
          dock-fallback, shared lock) → AppHost → existing tray/popup  (+ optional Dock line)
```

### 5.1 Stage 1 — `Diagnostics/ProbeCommand.cs` (`--probe-dock`)

- New `Program.cs` dispatch: `--probe-dock` → `ProbeCommand.RunDock()` (sibling of `Run`/`RunDpi`).
- Reuses `RazerProtocol.BuildFeatureBuffer(tid, commandId)` to build the **battery** (`0x80`) and
  **charging** (`0x84`) requests — already parameterized by command id, so no protocol additions.
- Dock control-path lookup: enumerate `DeviceList` for VID `0x1532`, PID `0x00A4`, select the
  collection with `GetMaxFeatureReportLength() == 91` (same heuristic as the mouse), open
  zero-access, `ExchangeAsync`-style SET→wait→GET.
- For each of tx `0x1f` (PR #2817 relay id) then `0xff` (master accessory default): print the full
  91-byte reply hex, the status byte, and the decoded battery (`buffer[10]`, `raw*100/255`) and
  charging (`buffer[10]`, `0/1`) values. Uses the longer wait already in `SetReadDelayMs` (400 ms ≫
  the ~31 ms relay window), so no timing change needed.
- Pure diagnostic: a `Thread.Sleep`-based one-shot like the existing probes; **not** unit-tested,
  **not** part of the resident runtime.

### 5.2 Stage 2 — device layer (`Hid/RazerDevice.cs`, `Hid/IRazerDevice.cs`)

- **Parameterize the PID set.** `FindControlPath` currently hard-iterates the mouse PIDs
  `{0x00A8, 0x00A7}`. Generalize it to take the target PID list so the same class can target the
  dock `{0x00A4}` with the identical passive transport. A small device "profile"
  (`{ pids, transactionIds, label }`) keeps mouse vs. dock differences in data, not duplicated code.
- **Transaction id.** The mouse auto-probes and caches `CachedTransactionId`. The dock must **not**
  clobber that cache. The dock profile uses a fixed attempt order `[0x1f, 0xff]` (resolved/locked by
  the spike) and caches separately (new optional settings field, e.g. `CachedDockTransactionId`) or
  not at all. Decision locked by Stage 1's result.
- The dock instance exposes the existing `ReadAsync` (battery + charging) — DPI is irrelevant to the
  dock and is not called on it.

### 5.3 Stage 2 — orchestration (`Monitoring/BatteryMonitor.cs`)

- `BatteryMonitor` gains an **optional** dock device: `IRazerDevice? dock` (null when no dock support
  / none present). The mouse device is unchanged and primary.
- In `PollAsync` (already holding `_readLock`): `reading = mouse.ReadAsync()`; **if `reading` is
  `Absent` and a dock is enumerated (PID `0x00A4`)**, `reading = dock.ReadAsync()` and tag its
  source. The dock read runs **inside the same lock acquisition** — no extra lock, no concurrency.
- `ProcessReading`/state machine unchanged except for carrying the source/`IsFromDock` flag for the
  optional UI. "Dock present but not charging" naturally yields `IsCharging == false`.
- No change to cadence, `ScheduleNext`, or the skip-if-busy poll guard.

### 5.4 Stage 2 — wiring & optional UI (`AppHost.cs`, `Ui/PopupWindow`, `Ui/PopupViewModel`)

- `AppHost` constructs the dock `RazerDevice` (dock profile) and passes it to `BatteryMonitor`.
- **Dock presence** is determined by **passive USB enumeration** of PID `0x00A4` (no feature reports,
  no device open — the free signal), checked at poll time. The dock **HID handle** is opened
  on-demand only when a fallback **relay read** is actually performed (mouse `Absent`), preserving the
  zero-extra-I/O invariant: a reachable mouse triggers no dock feature traffic.
- **Optional line:** `DeviceState` carries `DockStatus { None, Connected, Charging }`, derived with
  **no extra dock I/O** — `None`/`Connected` come from enumeration; `Charging` comes from the
  *existing* charging read (mouse `0x84` when reachable, or the relay's charging byte when we already
  fell back). The dock is never queried *solely* to drive this line. `PopupViewModel` exposes a bound
  string + visibility; `PopupWindow.xaml` adds one `TextBlock` shown only when `DockStatus != None`.
  Built last.

### 5.5 Settings — `Settings/AppSettings.cs`

- At most one optional field (`CachedDockTransactionId`) if Stage 1 shows caching is worthwhile;
  otherwise no settings change. Older `settings.json` tolerated as today (missing → default).

## 6. Dock HID protocol (from prior art — to be hardware-verified in Stage 1)

The dock is a separate USB composite device, PID `0x00A4`, with a mouse-like HID topology (verified
by live enumeration: MI_00 mouse / MI_01 COL01–08 / MI_02). It is expected to speak our existing
90-byte feature-report protocol (CRC XOR `[2..87]`, reply value at `buffer[10]`):

| Op | class | id | data_size | tx (try order) | reply |
| --- | --- | --- | --- | --- | --- |
| Battery (relay) | `0x07` | `0x80` | `0x02` | `0x1f`, then `0xff` | `buffer[10]` = 0..255 → `raw*100/255` |
| Charging (relay) | `0x07` | `0x84` | `0x02` | `0x1f`, then `0xff` | `buffer[10]` = `0`/`1` |

**Source & confidence:** the relay (send the standard battery/charging report to the dock endpoint
with tx `0x1f`; dock relays over RF ~31 ms; value in `arguments[1]` ≡ our `buffer[10]`) comes from
openrazer **open, unmerged** PR #2817, reverse-engineered from Synapse 4 captures. Upstream master's
accessory driver only does dock *lighting* (and uses tx `0xff` for dock-addressed reports), and **no
shipped tool reads battery through `0x00A4`** — most read the mouse PID directly because the mouse is
usually reachable while docked (confirmed here: `0x00A8` and `0x00A4` enumerate simultaneously).
Presence/"docked" has **no dedicated command**; it is inferred from whether the relayed read succeeds.

**Gating spike — Stage 1 state matrix (must be recorded before Stage 2 is trusted):**

| State | Expectation to confirm |
| --- | --- |
| Mouse active, **off** dock | Dock relay: answers? value vs. mouse's own read? |
| Mouse docked, **charging** | Charging byte `1`; battery tracks mouse |
| Mouse docked, **asleep / radio off** | **Key case:** does the dock relay answer when `0x00A8` is silent? |
| Dock present, **mouse not charging** (current state) | Charging byte `0`; no false "charging" |

If the dock does **not** relay battery in the asleep state, **goal 2 is dropped** and we keep only
what works (e.g. dock-presence as a charging hint); this is reported plainly, not papered over.

**Spike results (2026-06-20, hardware in the loop) — GATE: relay NOT confirmed.** The dock is
addressable (`0x00A4`, `mi_00`, `GetMaxFeatureReportLength()==91`), opens zero-access, and **accepts**
the battery (`0x07/0x80`) and charging (`0x07/0x84`) reports — it echoes the request and returns a
real status byte. But across runs (off-dock, docked, docked+busy-retry) at both tx `0x1f` and `0xff`
it returned only `0x04` timeout / `0x01` busy / `0x03` failure — **never `0x02` success with a real
value.** This correlates with the dock not currently charging/hosting the mouse: the mouse is linked
as `0x00A8` (HyperSpeed), almost certainly via a **separate dongle**, so the dock has no RF/charging
path to relay through. **Stage 2 is on hold** pending a working dock↔mouse link (connect the mouse
*through the dock* as its receiver and confirm it charges), then re-run `--probe-dock` expecting
`0x02` + a sane `%`. The `--probe-dock` diagnostic (with busy-retry) is committed and is the re-test tool.

## 7. Error handling & edge cases

- **No dock present:** fallback path never taken; identical to today.
- **Dock present, relay unsupported/fails:** treated as a failed read → existing `Absent` → Unknown
  after N misses. No regression; goal 2 simply yields nothing.
- **Mouse reachable directly while docked:** mouse read succeeds → dock is never queried (no extra I/O).
- **Dock unplugged mid-session:** next fallback `EnsureOpen` fails gracefully; handle released.
- **Wrong/garbage relay reply:** the existing CRC/status validation in `ParseReply` rejects it;
  battery range stays sane (0..255 → 0..100%).
- **Quit with a dock read mid-flight:** honors the monitor CT; `_readLock.Wait(1000)` on dispose still applies.

## 8. Threading & concurrency

All dock HID I/O is off the UI thread, inside `BatteryMonitor.PollAsync`, under the single shared
`_readLock` already held for the poll. There is never a second concurrent feature transfer. The
optional UI update marshals back via the existing `Dispatch`. No new synchronization primitives.

## 9. Testing strategy

**Stage 1 (manual, hardware-in-the-loop, gating):** run `--probe-dock` across the §6 state matrix;
record the reply hex, the working tx id, and which states answer. This *is* the gate for Stage 2.

**Stage 2 (unit, via `IRazerDevice` + `FakeRazerDevice`):**
- Mouse returns `Absent`, dock present and returns a value → `BatteryMonitor` reads from the dock and
  tags source = dock.
- Mouse returns a value → dock is **never** queried (assert no dock call — protects the zero-extra-I/O invariant).
- Dock null/absent → behavior identical to today.
- "Dock present, not charging" → `IsCharging == false`, `DockStatus == Connected` (not `Charging`).
- All existing tests stay green; protocol bytes already covered by `RazerProtocolTests`.

**Gating acceptance (manual):** after dock fallback activity, idle CPU returns to ~0% and private
working set to ~23 MB; mouse input latency unchanged at idle, during a normal poll, and during a
dock-fallback poll (before vs. after).

## 10. Out of scope / future

Dock RGB/charge-LED, pairing/nearby-mice, dock-as-distinct-device, and Phase B remain deferred. The
PID-parameterized device profile makes any later dock feature (or a third Razer device) an additive
change, not a rework.

## 11. Global constraints (carried over)

- **No admin/UAC; per-user only; single-instance mutex unchanged.**
- **Target** `net10.0-windows10.0.19041.0`; WPF + WinForms; WPF-UI 4.3.0 — **no new dependencies.**
- **HID:** VID `0x1532`; mouse PID `0x00A8`/`0x00A7`, **dock PID `0x00A4`**; 90-byte report, 91-byte
  feature buffer; CRC XOR `[2..87]`; reply value at `buffer[10]`; battery `0x07/0x80`, charging
  `0x07/0x84`.
- **Lightweight + zero mouse-input-latency are hard, gating requirements** (§3.1): no new background
  work, on-demand passive dock I/O serialized through the one read lock, piggybacked on the existing
  poll, footprint + latency verified as acceptance gates.
- **Reference-only prior art (all GPL):** re-derive protocol bytes; never copy code, never adopt the
  interface-claiming libusb transport. See CLAUDE.md "References / prior art."
- **DRY, YAGNI, TDD, frequent commits, conventional-commit messages, surgical changes.**

## File structure

```
Stage 1 (spike):
Modify:
  src/NagaBatteryTray/Diagnostics/ProbeCommand.cs   RunDock(): query 0x00A4 battery+charging, dump hex
  src/NagaBatteryTray/Program.cs                    dispatch --probe-dock

Stage 2 (fallback, gated on Stage 1):
Modify:
  src/NagaBatteryTray/Hid/RazerDevice.cs            PID-parameterized profile (mouse vs dock), dock tx strategy
  src/NagaBatteryTray/Hid/IRazerDevice.cs           (no change expected; dock reuses ReadAsync)
  src/NagaBatteryTray/Monitoring/BatteryMonitor.cs  optional dock device, mouse-first/dock-fallback under _readLock
  src/NagaBatteryTray/AppHost.cs                    construct dock device, pass to monitor
  src/NagaBatteryTray/Settings/AppSettings.cs       (optional) CachedDockTransactionId
  src/NagaBatteryTray/Ui/PopupViewModel.cs          (optional) DockStatus → bound string + visibility
  src/NagaBatteryTray/Ui/PopupWindow.xaml(.cs)      (optional) one Dock status line
  tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs   support a second (dock) fake + assertion fields
  tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs     mouse-first/dock-fallback + no-extra-call tests
```
