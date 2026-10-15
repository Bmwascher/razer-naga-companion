# Clearing the preferred mouse — design

Date: 2026-10-15
Status: proposed
Follows `2026-08-19-naga-mouse-selector-design.md` (§4.5 is the control this fixes).

## 1. Problem

`AppSettings.PreferredMouse` can be written but not unwritten. Once it names a mouse that is no
longer around, the user has no way to clear it from the app.

Observed on the user's machine during the 2026-10-15 perf pass: `settings.json` held
`PreferredMouse = "Naga V2 Pro"`, and the V2's receiver was then unplugged for good. It was worked
around by hand — the app stopped, the value set to `null`, a `settings.json.bak` left beside it —
not fixed.

Two facts combine into the trap:

- The dashboard header's mouse `ComboBox` is the **only** writer of `PreferredMouse`
  (`AppHost.SelectMouseAsync`, `AppHost.cs:432`).
- `DashboardViewModel.CanPickMouse => Mice.Count > 1` (`DashboardViewModel.cs:245`) disables that
  box whenever one mouse is reachable — which is exactly the situation a stale preference creates.

So the setting is write-once-and-stuck from the user's point of view. A preference can be moved
from one reachable mouse to another reachable mouse, and nothing else.

### 1.1 How the stale value is reached, and what it costs

A failed pick is enough to create it. `SelectMouseAsync` persists the name **before** it tries the
mouse; the unreachable path only posts "Couldn't reach that mouse" and snaps the box back. The
preference stays pointed at the mouse that did not answer. That is correct on its own terms —
§4.3's whole mechanism is that the app keeps trying until the preferred mouse wakes — but it means
one failed pick, or one receiver unplugged afterwards, leaves a preference the UI can no longer
reach.

What the stale value then costs, per battery poll, while the app runs on another mouse
(`ConnectionPolicy.Decide` → `ProbePreferred` → `MouseConnection.AdoptPreferredAsync`):

| State of the preferred mouse | Cost of each poll's probe |
| --- | --- |
| Receiver unplugged (the observed case) | one HID enumeration, no device I/O — `Open` yields no candidate and the probe rolls back at once |
| Receiver present, mouse asleep or gone | a full open + verify under `ReadPatience` before the rollback |

Neither is a footprint emergency, and the second is the documented intended behaviour of the
2026-08-19 design (§8) **for as long as the preference is wanted**. The defect is not the cost. The
defect is that the user cannot say "I no longer want it".

Clearing costs nothing to act on: `ConnectionPolicy.Decide` with `preferredModel == null` returns
`KeepLive`, so a cleared preference stops the probe at the next poll with no reconnect and no
extra I/O.

## 2. Goals

1. Any value `PreferredMouse` can hold is clearable from the UI, including one naming a mouse that
   is not reachable and never will be again.
2. The current value is legible — the user can see that a preference exists and which mouse it
   names, without opening `settings.json`.
3. No new background timer, no added probing, no device I/O on the clear path (hard rule 1).
4. The header selector's behaviour is unchanged, including "only reachable mice are listed"
   (recorded user decision, 2026-08-19).

## 3. Non-goals

- No change to what the header lists. See §4.1 option C — that decision is the user's to revisit,
  and this pass does not pre-empt it.
- No way to *set* a preference for a mouse that is not currently reachable. Nothing reported needs
  it, and the header already sets a preference for any mouse that is (YAGNI).
- No auto-expiry of a preference. See §4.1 option D.
- No change to the probe, the scan order, or `ConnectionPolicy`.

## 4. Design

### 4.1 Options considered

| | Option | Verdict |
| --- | --- | --- |
| A | An "Automatic" entry at the top of the header `ComboBox` | **Rejected.** That box is the header's identity element: its selection means "the mouse I am talking to", and it is resynced to the connected model on every state change. An entry that is never selectable-and-kept would be picked, then visibly snap back to "Naga V3 Pro". It also makes `Mice.Count > 1` permanently true, which retires the deliberate reads-like-a-label behaviour for everyone, to serve a rare state. |
| B | A "Preferred mouse" row in the settings overlay, with an action that returns it to automatic | **Chosen.** See §4.2. |
| C | List the preferred mouse in the header even when unreachable, marked as such | **Not taken here — the user's call.** It is the most direct fix, and it is narrow: it would only ever add one entry, only while a preference is set and unreachable, and it would be labelled. But "only reachable mice are listed" is a recorded decision from 2026-08-19 (§4.4: accuracy over speed, a sleeping mouse must never appear), and reversing it is not this pass's to make. Recorded in §8 as an open question. |
| D | Clear the preference automatically after N failed probes | **Rejected.** It throws away an explicit choice because a dongle spent a day in a drawer. The 2026-08-19 spec (§8) states the retry is the mechanism, and giving up would strand the user on the wrong mouse. |
| E | Relax `CanPickMouse` to `Mice.Count > 1 \|\| a preference is set` | **Rejected as insufficient.** With one mouse listed the enabled box offers only the mouse already selected, and `SelectedMouse`'s own rule suppresses re-picking the connected model. There is nothing to click. It only works in combination with C, and then C is doing the work. |

B wins on four counts: it reaches every value the setting can hold (C only reaches the one the
header can show, and only for supported models), it needs no device I/O, it leaves the recorded
header decision intact, and a persisted preference belongs in the panel where the other persisted
settings live. Its one weakness against C is discoverability — the user must open the gear — which
§4.2's always-visible row answers as well as a settings panel can.

### 4.2 The settings-overlay row

A new "Mouse" group in `SettingsView.xaml`, between "General" and "Battery polling":

```
Mouse
Preferred mouse                                  Naga V2 Pro
Not connected — the app is using Naga V3 Pro.
                                             [ Use automatic ]
```

- **The value line** reads the preference, or `Automatic` when it is `null`. Always shown, so the
  setting is legible before it ever goes stale.
- **The note** (`SubtleText`) appears only when a preference is set and names a mouse other than
  the connected one. It is phrased off the *connected model*, not off the reachable list, so it is
  correct at every moment — including before the dashboard's discovery sweep has landed, where the
  reachable list holds only the connected mouse and would wrongly call everything else unreachable.
  (The retired `MiceLoaded` flag is not coming back; it was removed deliberately in the 2026-08-19
  pass.)
- **"Use automatic"** is enabled only when a preference is set. It writes `null`, saves, pushes
  `null` to the monitor, and updates the row. No probe, no reconnect, no refresh.
- Themed brushes only, no effects (hard rule 3), matching the overlay's existing `CardTitle` +
  `DockPanel` row idiom.

### 4.3 What clearing does and does not do

Clearing stops the app **seeking** the preferred mouse. It does not move the connection: with no
preference, `Decide` returns `KeepLive`, so the app stays on the mouse it is on and scan order
(V3 first) applies the next time it chooses fresh. That is the right behaviour — a settings change
must not yank the open dashboard onto another mouse — and the note's wording ("the app is using
…") already tells the user which mouse that is.

### 4.4 Plumbing

The decision logic lives in the view model, where it is ordinary tested code (hard rule 6); the
host does the three side effects.

`DashboardViewModel`:

- `PreferredMouse` — `string?`, seeded from `AppSettings.PreferredMouse` in the constructor.
- `PreferredMouseLabel` — the preference, or `"Automatic"`.
- `HasMousePreference` — `PreferredMouse is not null`; drives the button's `IsEnabled`.
- `MousePreferenceNote` — `""`, or the "not connected" line when a preference is set and differs
  from `ModelName`. Recomputed when either side changes, so `ApplyState` notifies it too.
- `SetPreferredMouse(string?)` — the one writer; notifies the three derived properties.
- `ClearMousePreferenceRequested` event + `RequestClearMousePreference()` for the button.

`PreferredMouse` stays out of `ApplyTo`: it is written at the moment of the choice, by the host,
not on the overlay's save-on-close path. That keeps one writer per value and means a dismiss can
never resurrect a cleared preference.

`AppHost`:

- `SelectMouseAsync` calls `vm.SetPreferredMouse(name)` where it already persists the name — once,
  before the refresh, so the row is right on the failure path too (that path is how a stale
  preference gets created, §1.1).
- A new `ClearMousePreference(vm)` handler, wired in `OpenDashboard` beside `MouseSelectRequested`:
  write `null` to settings, `Save()`, `_monitor.PreferredMouse = null`, `vm.SetPreferredMouse(null)`.
  Synchronous — there is no device call to await.

## 5. Settings schema

Unchanged. `PreferredMouse` keeps its type, default and meaning; this pass only adds a second way
to write it.

## 6. Testing

Through the view model, with no hardware and no window (hard rule 6). New
`MousePreferenceTests.cs`, same `Build(AppSettings)` idiom as `MouseSelectorTests`:

- a VM built from settings holding a preference reports it, and `Automatic` when it is null
- `HasMousePreference` follows the value, so the button is disabled with nothing to clear
- the note is empty when the preference names the connected mouse, and names the connected mouse
  when it does not
- the note is empty whenever no preference is set, whatever is connected
- `ApplyState` on a hop recomputes the note (the preference did not change; the connection did)
- `SetPreferredMouse` raises `PropertyChanged` for label, enablement and note
- the button raises `ClearMousePreferenceRequested`

Plus, at the layer the clear acts on: clearing reaches the device as `null`
(`BatteryMonitor.PreferredMouse`). The other half of that claim — no preference means no probe —
is already covered by `ConnectionPolicyTests.NoPreferenceNeverProbes`, which the new test points
at rather than duplicating.

The host wiring (three assignments and an event subscription) is WPF-adjacent glue, verified by the
installed build and §7, as the convention allows.

The settings drawer had no probe coverage, because a dashboard capture shows it as a 340-wide
scroller with everything below "General" past the fold. `NAGA_UI_PROBE_TARGET=settings` renders the
panel alone at natural height, and `NAGA_UI_PROBE_STATE=preference` seeds the stuck state, so the
row is reviewable as a picture before the installed build is touched.

## 7. Acceptance (installed build)

1. With `PreferredMouse` set to a mouse that is **not** plugged in, open the dashboard → gear. The
   row names that mouse and the note says which mouse the app is actually using.
2. Click "Use automatic". The row reads `Automatic`, the button greys out, and `settings.json`
   holds `null` — with the app still running.
3. Close and reopen the app. The preference is still `null`; the header still works as before.
4. With both mice reachable, pick the other mouse in the header. The settings row follows it.
5. Idle footprint and mouse feel unchanged (hard rule 1).

## 8. Open question for the product owner

Option C — listing an unreachable *preferred* mouse in the header, marked — would put the escape
hatch at the exact control where the preference was made, which is where a user would look first.
It is a narrow reversal of the 2026-08-19 "only reachable mice are listed" decision: one extra
entry, only while a stale preference exists, explicitly labelled unreachable, never a mouse the
app merely hopes is there.

This pass does not take it. If it is wanted, it layers cleanly on top of what is built here — the
settings row stays useful either way, as the place the value is legible.
