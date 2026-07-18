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
byte-compare against the §4.2 inventory. Any difference is printed loudly and recorded. This is the
session's evidence that the nominally read-only run left every observable profile surface unchanged
(it cannot *prevent* a misbehaving command — that's the §4.4 opt-in — but it detects one).

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
`[88..89]` are excluded. A byte offset (or a contiguous multi-byte span) is a **hit** iff:

1. **Stable within state** — identical across all samples of the same state.
2. **Discriminating** — the state→value mapping is **bijective** over the visited slots. Any
   encoding is accepted (1-based, 0-based, enum, bitmask, multi-byte); literal equality to the slot
   number is not required.
3. **Reproduced** — the revisit state yields the starting state's value again.

Offsets that vary but fail any rule are reported as *noise* (still captured — they may be counters
or battery echoes worth knowing about).

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
  [y/N]" — during the densest burst (mid pass 2 when it runs, otherwise mid pass 1) and again
  after completion; both answers land in the capture, mirroring the Phase B spike's recorded check.
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
