# Naga V2 Pro — read-only profile probe (`--probe-profile`) design

Status: **spec approved-pending-user-review** · 2026-07-18
Reviewed by Codex (second-opinion pass, 2026-07-18): verdict *proceed with changes* — all 11
findings folded in below (request-shape tuples §5.3, fingerprint oracle §4.3, repeatability rules
§5.2, opt-in sweep §4.4 + integrity re-check §4.5, ops hygiene §4.1/§9, analyzer unit tests §8).

## 1. Goal

Answer one question with hardware evidence: **does any read-only command report which onboard
profile slot is active?** The Phase B spike (2026-06-21 design, §5.2 step 4) established that no
such command is *documented* — the V2 Pro switches slots only via its bottom hardware button, LED
colour = slot (1–5 = white/red/green/blue/cyan) — and the dashboard's Profile card therefore infers
liveness by comparing one button's effective action (`ProfileLiveness`). A direct read would upgrade
that card from inference to fact.

**Success criterion:** a `{class, id, data_size, args}` request plus a reply byte offset (or
multi-byte/bitmask encoding) whose value maps **bijectively** to the active slot, stable within a
state, reproduced on a revisit (§5.2). **A miss is scoped:** "no hit in this enumerated corpus"
(§5.3) — never "no active-slot command exists."

**Secondary goal (inventory):** capacity + existing slots via the verified list command, each
existing slot's 12 grid-button bindings (raw category/data always; decoded only where the category
is recognized), the profile-0 read-through view, and device mode — all captured to a durable
markdown record.

## 2. Non-goals

- **Zero writes of any kind.** Stricter than the buttons spike: no create/delete/set, ever. Only
  get-half command ids (`>= 0x80`) are reachable by construction (§7).
- No UI changes — consuming a discovered read is follow-up work.
- No hunt for a *set*-active command (that's a write).
- No sweep of classes other than `0x05` (candidates from other classes enter only via the sourced
  shortlist, §5.3).

## 3. Hardware baseline (verified facts this spike builds on)

From the Phase B spike (hardware-verified 2026-07-11): 90-byte report — `[0]` status, `[1]`
transaction id, `[5]` data_size, `[6]` class, `[7]` command id, args `[8..87]`, XOR CRC over
`[2..87]` at `[88]`; 91-byte HID buffer prepends report id `0x00`. Profile list = `0x05/0x81`
(data_size `0x06`: 1 capacity byte + up to 5 slot numbers). Button get = `0x02/0x8c` (args
`[profile, buttonId, hypershift]` in a 10-byte zero-padded frame; reply echoes those three — the
echo check). Profile `0x00` is the volatile direct profile; **its reads pass through to the active
onboard slot** (the read-through oracle). Device mode get = `0x00/0x84`. Grid ids `0x40..0x4b`.

## 4. Probe flow

### 4.1 Preflight

- Print the required hardware conditions and wait for confirmation: **tray app closed** (the CLI's
  zero-access handle is shared-access; a concurrent battery poll would interleave exchanges) and
  **all Razer software absent** — same conditions as the Phase B spike.
- Session bootstrap like the other probes (91-byte feature collection), but **resolve the
  transaction id via `TransactionIdProbeSet`** (battery query per candidate id until one answers)
  instead of hardcoding `0x1f`, and **load `SetReadDelayMs` from settings** for SET→GET pacing.
  Both land in the capture header.

### 4.2 Inventory (verified commands only)

1. Profile list → capacity + existing slots.
2. Device mode (`0x00/0x84`).
3. Per existing slot × 12 grid ids: `GetButton` — record raw `(category, dataLen, d0..d4)` always;
   decode to human-readable (`Ctrl+C`, `Disabled`, factory digit) **only** for recognized categories
   (`0x00` disabled, `0x02` keyboard); anything else prints as `unknown category 0xNN, raw …` (the
   Phase B run also observed mouse category `0x01`).
4. Profile-0 read-through × 12 grid ids (the effective actions — identifies the *currently* active
   slot's content without naming the slot).

### 4.3 Slot fingerprint selection

From the inventory, compute the **smallest set of grid buttons whose complete raw
`(category, data)` tuples uniquely distinguish every existing slot** (pure logic, unit-tested —
§8). At every state stop (§5.1) the probe reads that set via profile 0 and matches it against the
inventory — an oracle for the active slot that doesn't trust the typed LED colour. If no unique
fingerprint exists (two slots bound identically across all 12 buttons), states are recorded as
**LED-identified, not independently verified**, and the capture says so.

### 4.4 Pass 1 — sourced shortlist · Pass 2 — opt-in blind sweep

Both passes run the same diff-across-states protocol (§5) over their candidate corpus (§5.3).
Pass 2 runs **only** if pass 1 finds no hit, and **only after its own explicit `[y/N]` prompt**
(default N) that states the residual risk: reads are proven side-effect-free only for verified ids;
an undocumented id is *probably* inert but unproven on this firmware.

### 4.5 Post-run integrity re-check

After the last pass: re-read the profile list, device mode, and **all** per-slot bindings, and
byte-compare against the §4.2 inventory. Any difference is printed loudly and recorded. Results are
classified as **proven changes** (both readings succeeded and differ) versus **inconclusive**
observations (a surface that was readable before and is not after — missing evidence, not proof of
change). This is the session's evidence that the nominally read-only run left every observable
profile surface unchanged (it cannot *prevent* a misbehaving command — that's the §4.4 opt-in — but
it detects one).

### 4.6 Report

Paste-ready markdown capture (§9), checkpointed to disk **after every completed state** (an abort
preserves partial evidence), final verdict last.

## 5. Discovery protocol — diff-across-states

### 5.1 States

A **state** = one position of the bottom-button cycle. The user cycles to each available slot,
types the LED colour at each stop; the probe then reads the fingerprint set (§4.3) and sends every
candidate, recording full replies. **Tour shape:** every available slot once, then **return to the
starting slot** (the revisit state), ≥ **2 samples per candidate per state**. With N existing
slots that is N+1 states. Needs ≥ 2 existing slots to discriminate at all (with fewer, the probe
says so and records inventory only).

### 5.2 Hit rules (pure logic, unit-tested)

Diff only the args region `[8..87]` of each reply — status/tid/envelope `[0..7]` and CRC/reserved
`[88..89]` are excluded. Analysis is **per-byte**: a byte offset is a **hit** iff:

1. **Stable within state** — identical across all samples of the same state.
2. **Discriminating** — the state→value mapping is **bijective** over the visited slots. Any
   encoding is accepted (1-based, 0-based, enum, bitmask, multi-byte); literal equality to the slot
   number is not required.
3. **Reproduced** — the revisit state yields the starting state's value again.

Offsets that vary but fail any rule are reported as *noise* (still captured — they may be counters
or battery echoes worth knowing about). Analysis runs in two passes: per-byte first, then an
adjacent-pair pass over report offsets whose two constituent bytes neither one alone is a hit —
evaluating the pair as a big-endian tuple under the same three rules. So a 2-byte encoding is
detected automatically whether it happens to be bijective byte-by-byte or only as a tuple; only an
exotic encoding spanning more than 2 bytes would fall back to manual inspection of the recorded
per-byte noise values in the capture.

### 5.3 Candidate corpus

Every candidate is a full tuple `{class, id, data_size, args, source, status}` where status ∈
{documented, hardware-verified, speculative}. The shortlist is finalized at implementation time
from the reference repos; expected shape:

| class | id | shape | source | status | role |
|---|---|---|---|---|---|
| 0x05 | 0x81 | ds 0x06, zero args | Phase B spike | hardware-verified | **control** (must answer, must NOT track slot) |
| 0x05 | 0x80 | documented layout | razerqdhid cmd_profile | documented | count/info control |
| 0x05 | 0x82..0x8f picks | per-source shape | razerqdhid / openrazer-adjacent | documented | shortlist |

No numeric id enters the shortlist without a readable source. **Pass 2 sweep:** class `0x05`, ids
`0x80..0x9f` minus already-tried, one declared shape — data_size `0x06`, six zero arg bytes
(mirroring the one verified class-0x05 get). A pass-2 miss therefore claims only: *no hit for the
zero-argument ds-0x06 form of class-0x05 ids 0x80..0x9f on this firmware.*

## 6. Safety envelope

- **Read-only by construction**: the single new builder takes a command id but the probe only ever
  passes ids `>= 0x80`; no code path composes a set-half id.
- **Liveness sentinel**: a battery query between candidates; no answer after busy-retries → abort
  the pass, keep the capture (already checkpointed), run the §4.5 integrity re-check if the device
  returns, and report partial.
- **Pass 2 is opt-in** with the residual-risk statement (§4.4); declining still yields the full
  inventory + pass-1 evidence.
- **Input-feel check (hard gate)**: a recorded prompt — "move the mouse now; any stutter/lag?
  [y/N]" — fired mid-tour in **every pass that runs** (pass 1 can't know whether pass 2 will be
  opted into, so each tour carries its own check; pass 2's sweep is the densest burst when it
  happens) and again after completion; all answers land in the capture, mirroring the Phase B
  spike's recorded check.
- Standard pacing: busy-retry, configured SET→GET delay, one exchange at a time.

## 7. Code changes

- `RazerProtocol`: `BuildProfileGetProbeBuffer(byte tid, byte commandId, byte dataSize, ReadOnlySpan<byte> args)`
  — class `0x05` get through the existing `BuildReport`/CRC path; throws if `commandId < 0x80`
  (read-only by construction, §6).
- `Diagnostics/ProbeCommand.RunProfileProbe()` + `--probe-profile` switch in `Program.cs`; console
  interaction mirrors `--probe-buttons`.
- `Diagnostics/ProfileProbeAnalysis` (new, **pure**): fingerprint-set selection (§4.3) and hit
  detection (§5.2) as static functions over recorded data — no I/O, so an analyzer bug never forces
  recollecting hardware evidence.
- Capture writer: timestamped `probe-profile-YYYYMMDD-HHmmss.md` in `%APPDATA%\NagaBatteryTray\`
  plus a `probe-profile-latest.md` copy (never silently overwrite the only hardware record);
  rewritten after every completed state (§4.6).

## 8. Testing

Per repo conventions (logic layers only): `RazerProtocol` builder bytes (class/id/data_size
placement, CRC, the `< 0x80` throw) and `ProfileProbeAnalysis` (fingerprint minimality + no-unique-
fingerprint fallback; hit rules incl. stability/bijectivity/revisit failures, multi-byte and
bitmask encodings, noise classification) — synthetic recorded-data fixtures. The interactive flow
and transport are hardware-exercised via the installed probe, like every other probe.

## 9. Capture format

Header: app version + commit, PID + wired/wireless link, resolved transaction id, `SetReadDelayMs`,
device mode, date. Body: profile inventory (§4.2 tables), fingerprint set (or "none — LED-only"),
state order incl. revisit, per state: typed LED colour, fingerprint match result, per candidate:
raw request + full reply hex, status byte, CRC validity, sample number. Footer: integrity re-check
result, input-feel answers, noise offsets, verdict. **Indexing convention stated in the file:**
all offsets are 90-byte *report* offsets (the 91-byte HID buffer prepends the report id).

## 10. Outcomes

- **Hit** → follow-up (separate change): teach `RazerDevice`/`BatteryMonitor` the read and switch
  the Profile card to it (event-driven reads only — the no-polling rule stands).
- **Miss** → the Profile card keeps the effective-action inference; the capture documents the
  enumerated corpus so the question isn't reopened casually.
- Either way the inventory + integrity evidence stands alone as documentation of the user's
  Synapse-era slots.

## 11. Result — hardware run 2026-07-18 (capture `probe-profile-20260718-104514.md`)

**HIT: `0x05/0x84`, data_size `0x06`, zero args → active slot number at report arg[0]** (literal
1..5). Evidence: status `0x02` + CRC-valid in every state; values 0x01/0x02/0x03 tracking slots
1/2/3 bijectively, stable across both samples per state, reproduced on the revisit. Found by the
**opt-in pass-2 sweep** — the sourced shortlist all missed (`0x80` available-count constant 0x03,
`0x8a` total-count constant 0x05, `0x88` ds45 rejected status `0x03`; the `0x81` control answered
everywhere and tracked nothing, as required). Conditions: wireless (PID `0x00A8`), tid `0x1f`,
3 existing slots [01 02 03], **no fingerprint available** (all three slots hold identical factory
bindings — states were LED-identified, consistent with the read-through view matching throughout).
Integrity re-check: **UNCHANGED** — every observable profile surface byte-identical. Input-feel:
clean at all three recorded checks (hard gate upheld).

Corollary: `0x84 = 0x80 | 0x04` — by the protocol's get/set symmetry (DPI `0x85`/`0x05`, device
mode `0x84`/`0x04`), `0x05/0x04` is the likely SET-active-profile. **Unprobed** (a write — outside
this spike's zero-writes scope); a future opt-in spike could test it and retire the bottom-button
step entirely.

Follow-up ordered: the §10 Hit path — Profile card direct read, event-driven only.

## 12. Set-active spike (`--probe-profile --set-test`, ordered 2026-07-18)

Tests the §11 corollary: is `0x05/0x04` SET-active-profile? **This sub-mode writes** — one
undocumented command, opt-in via its own consent prompt; `Run()` (the plain probe) remains
zero-writes. Envelope: targets only slots from the live inventory (builder-enforced 1..5, plus an
inventory range guard against wrong-layout lists); flow = read active A (`0x05/0x84`,
**echo-checked** so a SET's own reflected reply can't pose as the read-back) → consent → set to the
next existing slot B (ds `0x01`, one ds `0x06` fallback if rejected) → read-back + **user LED
confirmation as physical ground truth** → optional power-cycle persistence check → restore to A
(same shape; any unconfirmed restore prints the bottom-button recovery immediately and taints the
verdict) → §4.5 integrity re-check → input-feel. Verdict grammar: VERIFIED (read-back = B AND LED
confirmed, persistence clause appended) / NOT ACCEPTED (genuine non-success statuses on both
shapes, scoped to "this run") / INDETERMINATE (any transport null — never misrecorded as
rejection) / DECLINED. A pass finalizes the Profile card design: direct active-slot read + an
"Activate" write-on-action button, retiring the bottom-button step.

**Result — hardware run 2026-07-18 (capture `probe-profile-20260718-220936.md`):
SET-ACTIVE VERIFIED.** `0x05/0x04` ds `0x01` arg[0]=slot accepted first try (status `0x02`);
echo-checked read-back slot 2; LED confirmed (white→red); **persisted across a power-cycle** —
full bottom-button parity; restore to slot 1 accepted + LED-confirmed; integrity re-check
UNCHANGED (byte-identical); input-feel clean at baseline/post-set/final. The ds `0x06` fallback
shape was never needed. Both directions of the active-slot protocol are now hardware-verified:
get `0x05/0x84`, set `0x05/0x04`.

## 13. Profile card v2 — the consumer (approved 2026-07-18; **v2.1 selector, user 2026-07-19**)

> **v2.1 revision (user acceptance feedback):** the single app-slot "Activate" button is replaced
> by a **slot selector**: the card lists every existing onboard slot (profile list `0x05/0x81`,
> read on the same open/↻ triggers), highlights the active one (`0x05/0x84`), and any other slot
> is click-to-switch (`0x05/0x04`, verified for arbitrary existing slots by the §12 spike). The
> app's adopted slot carries a marker (it holds the remaps). All other rules below stand
> (event-driven reads, write-on-click only, visible failure, no polling).

`IRazerDevice`/`RazerDevice` gain `GetActiveProfileAsync` (echo-checked `0x84` read → `byte?`) and
`SetActiveProfileAsync(slot)` (`0x04` ds `0x01`), tid-gated like every command; `BatteryMonitor`
exposes both as `_readLock`-serialized pass-throughs. The card reads the active slot on the same
event-driven triggers as today (dashboard open, its ↻ button, device-change path if wired later —
**no polling, no new timers**) and renders fact: **Live** when active == adopted `OnboardSlot`,
otherwise **NotLive** with "On Slot M · colour" plus an **Activate** button that writes set-active
on click, re-reads to confirm, and surfaces failure visibly ("Couldn't switch — wiggle the mouse
and retry"). Safe to offer freely: the write persists across power-cycles (§12) — no re-apply
machinery. `ProfileLiveness` (the effective-action comparer) retires, superseded by the direct
read; in v2.1 the state enum is gone too — pill flags (`IsActive`/`IsApp`) plus a checked/unchecked
bool carry the states. The popup's profile line stays
settings-based (no I/O on that path). Tests: VM state mapping + monitor pass-throughs via
`FakeRazerDevice` (extended with active-slot fields).

### 13.1 v2.2 — dropdown selector + the grid reads the displayed profile (user, 2026-07-19)

> **User direction:** "The profile selector should be a dropdown and then each profile should
> accurately read the configured 12 buttons."

Two changes, superseding v2.1's pill row (all v2.1 protocol rules stand — same commands, same
event-driven triggers, no polling, visible failure):

**Dropdown selector.** The pill `ItemsControl` becomes a themed `ComboBox` over the same
`ProfileSlots` items (label "Slot N · colour", " · app" suffix on the adopted slot), two-way bound
to a new `SelectedProfileSlot`. The selected item ALWAYS mirrors the mouse's actual active slot —
programmatic sync (inventory reads, failed-switch resync) is guarded so only a USER pick raises the
VM's `SwitchRequested`, which drives the existing `0x05/0x04` switch + confirm re-read. A failed
switch resyncs the selection back to the last-known active slot (the dropdown never lies about
where the mouse is). `ProfileTitle` is dropped — the dropdown carries the identity; the detail
line remains the status surface ("● app profile active" / "○ remaps live on Slot N · colour" /
"Switching…" / failure / "state unknown — mouse unreachable").

**The grid shows hardware truth for the ACTIVE profile.** After every inventory read that yields
an active slot (dashboard open, ↻, post-switch confirm, post-adopt refresh — the existing triggers,
nothing new), AppHost sweeps the 12 grid buttons of that slot via the existing verified
`GetButtonAsync(slot, buttonId)` (`0x02/0x8c`, hypershift 0), sequentially in position order,
updating each callout chip as its read lands (chips show "…" while pending, so the fill is visibly
progressive, top-down). Onboard-memory reads use `RazerDevice.FastReadAsync` — an early-poll
doubling ladder (50→100→200→400 ms) gated on completed-status + class/cmd echo, added after the
flat per-read `SetReadDelayMs` wait made the sweep ~5 s (user acceptance find, 2026-07-19); a
healthy read lands in ~50-100 ms, so the full sweep is around a second. A generation counter
discards results from a superseded sweep (new switch/refresh mid-sweep) and the sweep stops when
the dashboard has closed; a chip that is busy or capturing is skipped, never clobbered. Decode for
display: `FnKeyboard` → the existing `KeyToHidUsage.Describe`; `FnDisabled`/empty → "Disabled";
any other category → "Synapse action (0xNN)" (raw round-trips, never rewritten); failed read →
"—" with an explanatory tooltip. This surfaces bindings the app never wrote (Synapse-configured
user slots) — the whole point of the revision.

**Edit rule — you edit the profile you're on, and only the app slot is editable.**
- Active == app slot → full editing (capture/Disable/Default/undo), unchanged pipeline.
- Active is a user slot and the app slot exists on the mouse → **view mode**: hover-pairing and
  tooltips stay live, action buttons and capture are disabled, and a hint replaces the offline
  text slot: "viewing Slot M — switch to Slot N · colour to edit". The user's slots' CONTENTS are
  never written (standing constraint); viewing them is read-only by construction.
- No app slot on the mouse (fresh install, or the recorded slot was lost to a factory reset) →
  editable: the first write creates + factory-seeds the slot as today AND THEN **switches the
  mouse to it** (`0x05/0x04`, best-effort) — without the switch the just-written binding would be
  invisible under the new read-what's-active grid. This is the one new write in v2.2 and it is a
  slot SWITCH (bottom-button parity), never a content write to a user slot.

Perf gate: the sweep is 12 feature-report reads serialized on the existing `_readLock`, fired only
by the same explicit user actions that already trigger profile reads — no new timers, no polling,
battery poll unaffected (it skips while the lock is busy). Tests: callout decode mapping
(`SetFromDevice`), selector sync guard (programmatic set must not raise `SwitchRequested`, user
pick must, failed-switch resync), editability mapping (app-active / foreign-active / no-app /
lost-slot), all via the existing fakes.

### 13.2 v2.3 — every profile editable in place (user, 2026-07-19)

> **User direction:** "What's wrong with being able to map each of the 3 profiles?" — confirmed
> explicitly: all onboard slots become editable, reversing the Phase B no-write rule (recorded in
> memory; the reversal is safe because v2.2 gave the app byte-for-byte reads of every slot).

**Model: you edit the profile you're on — and you can be on any of them.** View mode, the
app-owned-slot concept, and the "○ remaps live on Slot N" line all disappear. Writes target the
**ACTIVE (displayed) slot** (AppHost resolves it from the last active-slot read, or reads it
on demand if unknown; unknown-and-unreadable → the write fails visibly as today). The adoption
machinery (`EnsureOnboardSlotAsync`, factory seeding, the recorded `OnboardSlot`, the settings
`ButtonBindings` table, the popup's app-profile line) is retired — the firmware holds every
binding, the grid reads hardware truth, and no slot is special. The `OnboardSlot`/`ButtonBindings`
settings PROPERTIES survive for JSON back-compat but are no longer read or written.

**Safety = snapshot + raw undo (replaces no-write).** `CalloutViewModel` tracks the button's
best-known on-mouse raw action (`_currentRaw`): set by every sweep read and updated to the wire
form of every verified write. Before any overwrite the current raw is snapshotted; undo restores
it through a new raw write path (`AppHost.RestoreRawAsync` → `SetButtonAsync(activeSlot, id,
raw.Category, raw.Data)` + read-back verify) — which restores **Synapse macros and mouse functions
byte-for-byte**, actions the app can't even model. Undo is offered only when a snapshot exists
(a write landing before the first sweep read of that chip has no known prior — no undo window,
honest as ever). The kind-tuple undo path (`_prev`) is deleted; one undo mechanism remains.
Display after an unmodeled restore is the "Synapse action (0xNN)" label, same as a sweep read.
No-op suppression still requires a clean display and never fires under an override.

**Consequences swept up:** `IsGridEditable`/`GridHint`/capture-revocation and all view-mode
guards are deleted (every state is editable; offline still disables the grid); `ProfileSlotItem`
loses `IsApp` and the "· app" label suffix; the card detail line reduces to transient status
("Switching…", failure text, "state unknown — mouse unreachable", else empty); reset-all resets
the ACTIVE slot (wording already says "to factory"; each chip's individual raw undo still opens).
Tests move accordingly: raw-snapshot undo suite (restore byte-for-byte incl. unmodeled raw,
no-undo-without-snapshot, busy-guard preserved), editability suite deleted, selection suite
unchanged.
