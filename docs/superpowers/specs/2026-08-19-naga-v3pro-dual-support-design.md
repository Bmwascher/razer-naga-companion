# Naga V3 Pro — dual-support device layer design

Status: **spec approved-pending-user-review** · 2026-08-19
Probe spike ran this session against real hardware (throwaway HidSharp tools, outside the repo);
all findings in §3 are hardware-verified. User decisions recorded in §2.

## 1. Goal

The app drives **both** the Razer Naga V2 Pro and the Razer Naga V3 Pro through one device
layer. It finds whichever mouse answers, reports which model it found, and every existing
feature (battery, DPI, button remap, profiles, dashboard, probes) works unchanged on both.

**Success criteria:**

- Installed build on the V3 Pro: tray battery %, dashboard, DPI get/set, remap chips, profile
  card all work over the 2.4 GHz dongle AND over the cable, and the header flips
  Wireless/Wired on a cable plug/unplug without restart.
- Same build still passes the full test suite through `FakeRazerDevice` (V2 behavior is
  regression-locked by the existing suite).
- Dashboard, popup, tray tooltip, and toasts show the detected model name, never a hardcoded
  one.
- `--probe`, `--probe-dpi`, `--probe-buttons`, `--probe-profile` find and exercise whichever
  supported mouse is connected.

## 2. User decisions (2026-08-19, settled — do not re-open)

| Decision | Choice |
| --- | --- |
| Dual support vs clean swap | **Dual support** — scan both PID sets, drive whichever answers |
| Naming | App stays **"Razer Naga Companion"**; UI shows the **detected** model name; offline fallback "Razer Naga" |
| Stage art | **Keep the V2 render** (`naga-thumb.png`) for both models for now; a V3 render is a follow-up when the user supplies one |

## 3. Hardware baseline (probe spike, 2026-08-19, real V3 Pro)

- Identity: product string "Razer Naga V3 Pro", VID `0x1532`, **wired PID `0x00E7`**,
  **wireless (HyperSpeed dongle) PID `0x00E8`** — the same wired-is-one-below-wireless pattern
  as the V2 Pro (`0x00A7`/`0x00A8`).
- Control collection: the one collection whose max feature report length is **91 bytes**
  (report id + 90), same as V2. On the V3 it lives on interface **MI_03** (usage page
  `0x0001`, usage `0x0003`); on the V2 it was MI_00. **Never key on interface number or
  usage — the 91-byte feature length is the selector**, exactly what `FindControlPaths`
  already does.
- Transaction id `0x1f` (first in `TransactionIdProbeSet`) works.
- Full parity, verified with write round-trips (originals restored):
  - Battery `0x07/0x80` + charging `0x07/0x84` — battery read 60% on both links; charging
    flipped 0→1 when the cable went in.
  - DPI get `0x04/0x85` / set `0x04/0x05` — 800×800 written, read back, 1600×1600 restored.
  - Buttons `0x02/0x8c`/`0x02/0x0c` — grid ids `0x40..0x4b` all answer; keyboard binding
    written to the volatile direct profile, read back byte-exact, original restored. The
    factory default binding is category `0x02` with a single `0x00` data byte (differs from
    the V2 factory default, harmless — raw category/data round-trips are what the app relies
    on, per the v2.3 snapshot/undo model).
  - Profiles `0x05` — list `0x81` (capacity 5), new `0x02`, delete `0x03`, and **this
    project's own** active-slot get `0x84` / set `0x04` all work on V3 firmware
    (create slot 2 → switch → verify → switch back → delete, every step acked `0x02`).
  - Device mode get `0x00/0x84` → normal (`0x00`).
- Same 91-byte report frame, same status codes (`0x02` success, `0x01` busy), same CRC.
- When the cable is plugged in, the `0x00E8` (dongle) collections disappear from enumeration
  and `0x00E7` appears — the OS-level flip mirrors V2 behavior.

## 4. Design

### 4.1 Model table (`Hid/RazerProtocol.cs`)

A small readonly record + table; command constants stay shared (proven identical):

```csharp
public readonly record struct MouseModel(int WiredPid, int WirelessPid, string DisplayName);

public static readonly MouseModel[] SupportedMice =
{
    new(0x00E7, 0x00E8, "Naga V3 Pro"),   // daily driver first
    new(MousePidWired, MousePidWireless, "Naga V2 Pro"),
};
```

The existing `MousePidWired`/`MousePidWireless`/`DockPid` constants stay (probe docs, dock
tool, and the V2 row reference them). `DockPid` is untouched — the Mouse Dock Pro remains a
V2-era closed topic.

### 4.2 Device scan (`Hid/RazerDevice.cs`)

`FindControlPaths` keeps its `(int Pid, string Path)` shape and iterates a flat
`RazerProtocol.ScanPids` order — **every model's wired PID first** (V3, then V2), **then every
model's wireless PID** (V3, then V2) — with model name and link type resolved from the PID via
`RazerProtocol.ModelNameForPid`/`IsWiredPid` at commit time (amended 2026-08-19 in the plan
debate; functionally identical to the tuple shape this section first sketched). Rules preserved:

- Wired always beats wireless — a cabled mouse must win over a still-plugged-in receiver
  that can no longer reach it (existing rule, unchanged).
- The 91-byte feature-length filter selects the control collection.
- `EnsureConnectedAsync` already confirms a candidate collection with a real battery query before
  committing (`RazerDevice.cs:74`); that same check is what rejects the user's still-plugged
  V2 receiver when the V2 is idle. No new logic.

`RazerDevice` records the winning model's `DisplayName` alongside the existing `_wired` flag
(`_wired` becomes `RazerProtocol.IsWiredPid(pid)`).

### 4.3 Model-name plumbing

- `BatteryReading` gains `string? Model` (null when absent) next to `IsWired`.
- `DeviceState` gains `string? Model` next to `Wired`.
- `BatteryMonitor` copies it through (`DeviceState.Online(...)`).
- Consumers replace the four hardcoded "Naga V2 Pro" strings:
  - `Ui/Dashboard/DashboardWindow.xaml:21` header → bound to a new `DashboardViewModel`
    property.
  - `Ui/PopupWindow.xaml:14` header → same via `PopupViewModel`.
  - `Ui/TrayIconController.cs:69-70` tooltip.
  - `Ui/Notifications.cs:10` toast title.
- Fallback when offline or model unknown: **"Razer Naga"**.
- The UI screenshot probe's fake states carry a model name so probe renders and README
  screenshots stay deterministic ("Naga V3 Pro" as the shown default).

### 4.4 Probes (`Diagnostics/`)

`ProbeCommand` (3 PID loops) and `ProfileProbeCommand` (1 loop + the wired/wireless label at
`ProfileProbeCommand.cs:867`) iterate `RazerProtocol.ScanPids` instead of the two V2
constants, and print the model display name + PID they found. The wired/wireless label
derives from the matched model's pids, not from the V2 constants. `--probe-dock` is
untouched.

### 4.5 Out of scope (this pass)

- V3 stage art / per-model art switching (waits for a user-supplied render).
- Any protocol addition (DPI stages, polling rate) — next roadmap item, after this merges.
- Repo/app rename, README branding beyond "supports V2 Pro + V3 Pro".
- Dock relay (closed, V2-era; re-open only via `--probe-dock` evidence).

## 5. What does NOT change (hard-rule audit)

- No new timers, threads, or polling paths; the scan-order change runs inside the existing
  on-demand `EnsureConnectedAsync`. Idle CPU / memory / input-feel gates untouched (rule 1).
- Button snapshot + raw-undo path untouched (rule 2) — and the V3's odd factory default is
  exactly why it must stay: undo restores raw bytes the app can't model.
- No theming changes (rule 3).
- README + screenshots ride this branch (rule 4): supported-models line, regenerated
  dashboard/popup screenshots (header text changes to the probe default "Naga V3 Pro").
- Startup registration untouched (rule 5).
- TDD through `IRazerDevice`/`FakeRazerDevice` (rule 6); `InternalsVisibleTo` untouched.

## 6. Testing

- **Unit (TDD, new):** model-table lookup (PID → model/wired/name); `BatteryReading` /
  `DeviceState` model plumbing through `BatteryMonitor`; view-model header fallbacks
  (offline → "Razer Naga", online → detected name) for dashboard, popup, tooltip text
  builder; scan-order property (all wired PIDs precede all wireless PIDs; V3 precedes V2
  within a link).
- **Existing suite:** must stay green untouched — it locks V2 behavior.
- **Hardware acceptance (user, installed build, gates the merge):** V3 over dongle → tray %,
  dashboard header "Naga V3 Pro · Wireless", DPI set, remap a thumb button + undo, profile
  switch; plug cable → header flips to Wired without restart; unplug → flips back.
  V2 re-test: optional, if the user cares to power it up.
- **Screenshot probe:** regenerate `docs/images/` on this branch.

## 7. Rollout

Branch `feature/v3pro-dual-support`. Conventional commits. User's visual acceptance of the
installed build gates the merge (merge → delete branch → ledger entry). Push to origin only
on the user's word.
