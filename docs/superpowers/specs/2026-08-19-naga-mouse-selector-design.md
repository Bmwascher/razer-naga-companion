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

Every other device call (button read, button write, DPI, profile) keeps `reselect: false`, so no
other call ever **reselects a live handle**. They can still reconnect when the handle has dropped —
that is existing, wanted behaviour — which is exactly why connection identity (4.10) is needed:
a reconnect mid-sweep can land on a different mouse without any battery read publishing the change.

Adoption of the preferred mouse must be rollback-safe. The live handle is detached from the field
before the probe so the probe's own failure path cannot dispose it, and it is restored in a
`finally`, so an exception or a cancellation cannot leave the app with no connection and a leaked
handle.

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
- **It republishes the connected model before returning.** Discovery reconnects at the end, and
  that reconnect can adopt a different mouse than the one `BatteryMonitor.State` still names —
  `State.Model` only ever updates from a battery read. So `DiscoverMiceAsync` performs one battery
  read and processes it inside the same lock acquisition, and only then returns. Without this the
  selector, the profile-name key, and the scoped cache would all describe the wrong mouse.

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
  card and button grid, because the new mouse holds entirely different state. The refresh behind a
  pick **waits for the read lock**; the ordinary poll's skip-when-busy behaviour would otherwise
  report a perfectly reachable mouse as unreachable whenever another read happened to be in flight.
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
- the dashboard view model's profile inventory, DPI reading (back to "unknown", not the old
  mouse's number), grid chips and undo snapshots

If the dashboard is open, the reset is followed by a **probe-free** reseed, so the card and grid
refill from the new mouse without re-running discovery.

Model-change invalidation alone is not sufficient, because the model is published only by a battery
read. Connection identity (4.10) closes the gap.

### 4.10 Connection identity

`RazerDevice` exposes a `ConnectionId` that increases every time it adopts a collection, including
a reconnect to the same model. It is the app's only reliable answer to "is the mouse I am talking
to now the mouse I was talking to a moment ago", because:

- a non-battery call can reconnect after a dropped handle and land on a different mouse, and
  nothing publishes that;
- `BatteryMonitor` keeps reporting the previous model for three consecutive failed reads, so
  `State.Model` lags reality; and
- a multi-step operation releases the read lock between steps, so a battery-path reselect can land
  between them.

Two consequences:

1. **The button write is atomic.** Resolving the active slot, writing, and reading back happen
   inside one `_readLock` acquisition, in one `BatteryMonitor` method. Split across three
   acquisitions, a reselect can land between resolve and write, and the app writes one mouse's slot
   number onto another — the exact failure this whole pass exists to prevent.
2. **Late results are discarded.** The DPI read, the profile read and every button read in the grid
   sweep carry the `ConnectionId` they ran under; a result whose id no longer matches is dropped
   rather than painted onto the UI. The sweep's existing generation counter is not a substitute for
   this: it moves on a *published* model change, a dashboard close, or a new sweep, and an
   unannounced reconnect is none of those.
3. **Recovery republishes identity before reloading.** When a connection change is detected, a
   lock-waiting battery read runs first, so `State.Model` names the mouse now connected. Reloading
   before that would file the new mouse's DPI, profiles and profile names under the old model.

### 4.7 Write-slot guard

A write never targets a remembered slot. The atomic write (4.10) reads the active slot from the
device inside its own lock acquisition and writes to that, so the target always belongs to the mouse
being written to and an unreadable slot fails the write outright rather than falling back on a
cached number.

On top of that, `AppHost` retains the created-slot list from the last profile-list read, cleared with
the rest of the per-device state, and the write is refused before it starts when that list is
**known** and the slot is absent from it. An unknown list never blocks a write. With 4.6 and 4.10 in
place this guard should never fire; it exists so a write can never again be aimed into a slot the
mouse does not have.

### 4.8 Unset-button display

`CalloutViewModel.DisplayRaw` gains a case before the fallback: category `0x02` with fewer than
2 data bytes displays as `"Not set"`. It is a normal display state, not a failure, so `Failed`
stays false and the chip does not turn red.

Strictly fewer than 2. A longer `0x02` reply has never been measured on either mouse, and calling an
unmodelled binding "Not set" would tell the user an assigned button is empty and invite an
overwrite. Unmodelled shapes keep the existing "Synapse action" label.

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

The connection path is split so that only Win32 stays untested. `IControlCollection` and
`IControlCollectionSource` wrap `CreateFile` and `HidD_SetFeature`/`HidD_GetFeature` and nothing
else; `ConnectionPolicy` holds the reselect decision; `MouseConnection` holds the state machine —
preference order, adoption, rollback, disposal and `ConnectionId`. Tests substitute a fake source, so
all of that runs with no hardware. Hard rule 6's transport exemption then covers only the two Win32
wrappers and the WPF windows. (Product-owner decision, 2026-08-19, taking the reviewer's
recommendation over the session's advice to defer.)

- preference honoured when the preferred mouse is reachable
- fallback to another mouse when the preferred one is not
- a live handle is reselected only on the battery path, never by a button, DPI or profile call
- an unknown preferred-mouse name normalizes to "no preference" and never causes a probe
- adoption of the preferred mouse rolls back on failure, on an exception, and on cancellation,
  restoring collection, transaction id, link state and model together, and disposing exactly the
  losing collection
- a candidate that does not answer is disposed and skipped; a live connection is reused without
  opening anything
- `ConnectionId` increases on every adoption, including a reconnect to the same model
- the atomic write reads the active slot from the mouse it is about to write to, writes and
  verifies, all under one lock, and reports the connection it ran under
- a button write is refused outright when the active slot cannot be read
- the grid sweep abandons its results when the connection changes mid-sweep
- a DPI or profile result from a superseded connection is discarded, not displayed
- discovery lists only mice that answered, and republishes the connected model before returning
- an explicit mouse pick waits for the read lock instead of skipping when busy
- model change clears the active slot, the created-slot list, the DPI reading and the sweep
  generation
- the write-slot guard refuses a slot the known inventory does not contain
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
