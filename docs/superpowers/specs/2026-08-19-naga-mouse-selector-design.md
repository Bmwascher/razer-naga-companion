# Mouse selector + per-device state isolation — design

Date: 2026-08-19
Status: approved (user, 2026-08-19)
Follows `2026-08-19-naga-v3pro-dual-support-design.md`.

## 1. Problem

Acceptance testing of the V3 Pro dual-support merge surfaced three defects (A, B, C in
`CLAUDE.md`'s backlog). Investigation on real hardware showed **A, B and C are one defect**:
the app caches state that belongs to one mouse and keeps using it after the mouse underneath
changes.

- **A — sticky selection.** `RazerDevice.EnsureConnectedAsync` short-circuits on a live handle
  and never re-checks whether a more-preferred mouse became reachable. Pulling the V3's cable
  made the app re-scan while the V3's wireless link was still coming up; the V2 (awake on its
  dock) answered first and the app stayed on it indefinitely.
- **B — false "Not applied".** `AppHost.WriteVerifiedAsync` writes, reads back, and demands an
  exact match. When the write targets a profile slot the connected mouse has not created, the
  read-back can never match, so every write reports failure.
- **C — red "Synapse action (0x02)".** `CalloutViewModel.DisplayRaw` decodes category `0x02`
  only with exactly 2 data bytes; the uncreated-slot reply carries 1.

### 1.1 Evidence (read-only spike, 2026-08-19, V3 Pro wireless)

Reading a button from each profile slot on the V3 (`profiles: capacity=5 created=[1] active=1`):

| Slot | Reply |
| --- | --- |
| 0 (direct) | `cat=0x02 len=2 [00 45]` |
| 1 (created) | `cat=0x02 len=2 [00 45]` |
| 2, 3, 4, 5 (not created) | `cat=0x02 len=1 [00]` |

All 12 grid buttons on the active slot read clean 2-byte values. So the firmware answers
uncreated-slot reads with a 1-byte stub instead of an error, and that single stub produces both
B (a read-back that can never match) and C (a category the display cannot decode).

The app reaches an uncreated slot because `AppHost._lastActive` (and the slot handed to the grid
sweep) is read once per dashboard open and never invalidated when the device changes. The user's
V2 is on slot 3; after the connection flipped to the V3 mid-session, slot 3 was still cached, and
the V3 has no slot 3.

Two earlier hypotheses were killed by measurement and are recorded so they are not re-tried:
button reads are not flaky (36/36 through the production path, median 61 ms), and the verify read
does not race the commit (20/20 first reads returned the freshly written value).

## 2. Goals

1. The user picks which mouse the app talks to, from the dashboard header.
2. The choice persists, and the app returns to it on its own when that mouse becomes reachable.
3. No state from one mouse is ever applied to another.
4. No new background timer, no polling, no change to mouse input feel (hard rule 1).

## 3. Non-goals

- No selector in the tray icon or popup. The dashboard is where the choice is made.
- No support for two mice being driven at once. One connection, one active mouse.
- No create-profile UI (the V3 ships with one slot). Logged separately.
- No change to the write/verify protocol itself; it is proven correct.

## 4. Design

### 4.1 Preference in the device layer

`RazerDevice` owns the preference, because it already owns the scan, the handle and the
`IRazerDevice` test seam. A selector class above it would split one decision across two files.

- `AppSettings.PreferredMouse` — `string?`, default `null`. Holds a display name from
  `RazerProtocol.SupportedMice` (e.g. `"Naga V3 Pro"`).
- `null` means "use scan order", and `SupportedMice`/`ScanPids` already put the V3 Pro first, so
  the required default (prefer the V3) needs no special case.
- An unknown or retired name behaves as `null`.

`IRazerDevice` gains:

```csharp
string? PreferredModel { get; set; }
Task<IReadOnlyList<string>> DiscoverReachableAsync(CancellationToken ct);
```

### 4.2 Scan order under a preference

`FindControlPaths` currently yields every wired PID before any wireless one, so a cabled mouse
beats a still-plugged receiver that can no longer reach it. Under an explicit preference, the
preference dominates and the wired-first rule applies within each model:

1. preferred model, wired PID
2. preferred model, wireless PID
3. remaining models, wired PIDs, in `SupportedMice` order
4. remaining models, wireless PIDs, in `SupportedMice` order

With no preference this is byte-for-byte today's order.

### 4.3 Re-evaluation, without a new timer

`EnsureConnectedAsync` gains a `reselect` parameter, default `false`. Only `ReadAsync` passes
`true`. `ReadAsync` is the battery path, which runs on the existing poll and on the existing
device-change refresh, so re-evaluation rides moments the app already has.

Behaviour when `reselect` is true:

- The live handle is already the preferred model, or no preference is set: return immediately.
  This is the steady state and costs nothing.
- Otherwise: try the preferred model's collections first. If one answers a battery query, adopt
  it. If none answers, keep the current connection.

Every other device call (button read, button write, DPI, profile) keeps `reselect: false` and
never triggers a scan, so the 12-button sweep and instant-apply writes are unchanged.

Worst-case extra cost: one probe (~400 ms of HID I/O, off the UI thread, serialized on
`BatteryMonitor._readLock`) per battery poll, and only while running on a non-preferred mouse.

### 4.4 Discovering reachable mice

`DiscoverReachableAsync` tries each supported model in `SupportedMice` order, keeping the first
collection per model that answers a battery query, and returns the display names that answered.
It then re-runs normal selection so the connection ends on the preferred/current mouse.

- Exposed through `BatteryMonitor.DiscoverMiceAsync()`, serialized on `_readLock` like every
  other pass-through.
- Called only on dashboard open and on the dashboard's refresh. Never on a timer.
- Cost is one probe per supported model, about a second in total today.

A HID enumeration alone cannot answer this: the receiver stays enumerated while the mouse is off,
so a list built from enumeration would show a sleeping mouse. The user chose accuracy over speed.

### 4.5 Dashboard header selector

- The header's model-name `TextBlock` (`DashboardWindow.xaml:21`) becomes a `ComboBox` bound to a
  new `Mice` collection, mirroring the Profile card's existing `ComboBox`
  (`MouseStageView.xaml:596`) so the styling stays consistent. Themed brushes only, no effects
  (hard rule 3).
- The list holds only mice that answered discovery. With one entry it reads like today's label.
- Until discovery lands, the box shows the connected model and is disabled.
- The wired/wireless caption (`HeaderSubtitle`) is unchanged.
- Picking a mouse: save `PreferredMouse`, force reselection, then reload battery, DPI, profile
  card and button grid, because the new mouse holds entirely different state.
- A pick that fails to connect restores the selection to the mouse actually connected and shows a
  short failure note, matching the existing profile-switch failure pattern.
- Tray icon, tooltip and popup follow the active mouse through the existing `DeviceState.Model`.

### 4.6 Per-device state invalidation

This is what removes B and C at the source.

`AppHost` already observes state changes for the tray. It compares the incoming
`DeviceState.Model` with the last one seen; when it differs (including to and from `null`), it
resets everything scoped to a mouse:

- `_lastActive` to `null`
- the grid sweep generation, so any sweep in flight for the old mouse is superseded
- the created-slot list (4.7)
- the dashboard view model's profile inventory, DPI reading, grid chips and undo snapshots

If the dashboard is open, the reset is followed by the normal seed path, so the card and grid
refill from the new mouse.

### 4.7 Write-slot guard

`AppHost` retains the created-slot list from the last profile-list read, cleared with the rest of
the per-device state. `ResolveWriteSlotAsync` returns `null` — a visible failure, no write — when
that list is **known** and the resolved slot is absent from it. An unknown list never blocks a
write: the dashboard resolves the active slot on demand before its first profile read, and that
path must keep working. With 4.6 in place this guard should never fire; it exists so a write can
never again be aimed into a slot the mouse does not have.

### 4.8 Unset-button display

`CalloutViewModel.DisplayRaw` gains a case before the fallback: category `0x02` with fewer than
2 data bytes displays as `"Not set"`. It is a normal display state, not a failure, so `Failed`
stays false and the chip does not turn red.

### 4.9 Per-mouse profile names

`AppSettings.ProfileNames` is keyed by slot number only, so the V2's "World of Warcraft" on slot 1
also labels the V3's slot 1.

- New field `ProfileNamesByMouse` — `Dictionary<string, Dictionary<byte, string>>`, keyed by model
  display name.
- `ProfileNames` is marked retired and kept for JSON round-trip, matching how `ButtonBindings` and
  `OnboardSlot` were retired.
- One-time migration at load in `JsonSettingsStore`: when `ProfileNames` is non-empty and
  `ProfileNamesByMouse` is empty, the legacy names move under `"Naga V2 Pro"`. That is the only
  mouse that could have written them, since per-mouse names ship with this change.
- `DashboardViewModel` reads and writes the map for the currently connected model. An unknown
  model name gets its own empty map rather than sharing another mouse's.

## 5. Settings schema

| Field | Type | Default | Note |
| --- | --- | --- | --- |
| `PreferredMouse` | `string?` | `null` | display name; null = scan order (V3 first) |
| `ProfileNamesByMouse` | `Dictionary<string, Dictionary<byte,string>>` | empty | replaces `ProfileNames` |
| `ProfileNames` | `Dictionary<byte,string>` | empty | retired, round-trip only |

Old settings files load unchanged: absent fields take their defaults and the migration runs once.

## 6. Testing

Through the `IRazerDevice` seam with `FakeRazerDevice` (hard rule 6):

- preference honoured when the preferred mouse is reachable
- fallback to another mouse when the preferred one is not
- return to the preferred mouse on the next battery read once it becomes reachable
- button reads, writes, DPI and profile calls never trigger a re-scan
- discovery lists only mice that answered
- model change clears the active slot, the created-slot list and the sweep generation
- the write-slot guard refuses a slot not in the created list
- `DisplayRaw` renders 1-byte category `0x02` as "Not set" without setting `Failed`
- legacy `ProfileNames` migrate to the V2 and per-mouse names round-trip

README screenshots regenerate through the UI probe on this branch (hard rule 4).

## 7. Acceptance (installed build, real hardware)

1. With both mice reachable, the header lists both and defaults to the V3 Pro.
2. Pull the V3's cable. The app may land on the V2 while the V3's link comes up, and must return
   to the V3 on its own within one battery interval.
3. Immediately after that swap, set several thumb buttons. No false "Not applied".
4. No chip shows a red "Synapse action (0x02)".
5. Profile names set on the V2 do not appear on the V3.
6. Idle footprint and mouse feel unchanged (hard rule 1).

## 8. Risks and limitations

- `CachedTransactionId` is stored once, not per model. Both supported mice use `0x1f` (measured),
  so this is correct today; a future model with a different id would need the cache keyed by model.
  Noted, not fixed (YAGNI).
- Discovery adds about a second to dashboard open. Accepted by the user in place of a list that
  shows sleeping mice.
- While running on a non-preferred mouse, each battery poll pays one extra probe. It stops as soon
  as the preferred mouse is adopted.
