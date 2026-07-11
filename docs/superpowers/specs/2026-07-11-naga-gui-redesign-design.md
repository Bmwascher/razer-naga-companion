# GUI Redesign: Dashboard, Themes, Popup Restyle & Tray Ring — Design

**Date:** 2026-07-11
**Status:** DESIGN APPROVED (brainstormed with the visual companion, all sections user-approved
2026-07-11, including a 7-point self-critique pass folded in below).
**Builds on:** Phase 2-A §3.1 invariants (`2026-06-20-naga-settings-dpi-design.md`) bind **unchanged**.
Phase B (button remapping, onboard app-owned slot) is shipped and its apply pipeline is reused as-is.
**Reference look:** a user-supplied "OpenLogi" concept image (dark Apple-esque dashboard: device
header card, product-shot centerpiece with per-button callout cards, DPI rail, profile bar). We
reproduce the *structure and mood*, not the image: the centerpiece is our own vector art.

## 1. Goal

Replace the utilitarian Settings window with an Apple-esque **dashboard**: a dark, card-based main
window where the Naga is the centerpiece — a vector silhouette with the 12 thumb-grid buttons wired
to callout chips showing (and editing) their live bindings — flanked by a DPI card with one-click
presets and a Profile card. Restyle the tray popup to the same design language, and upgrade the tray
icon with a battery **ring around the existing digits**. Five preset themes, swappable at runtime.

Everything remains inside the hard gating constraint: ~0% idle CPU, ~23 MB private working set,
zero mouse input-latency regression, no new dependencies, no new background timers/threads.

## 2. Scope

**In scope:**
- **Design system** (`Ui/Themes/`): semantic-brush resource dictionaries + structural styles
  (cards, typography, metrics); glows via gradient brushes (never `DropShadowEffect` — WPF renders
  it in software).
- **Five preset themes** — Porcelain (default), Razer, Ice, Ultraviolet, Ember — runtime-switchable,
  persisted as `AppSettings.Theme`.
- **Dashboard window** (replaces `SettingsWindow`, which is deleted): single-stage layout (no nav),
  built as shell + swappable views so a sidebar could be retrofitted later without rework.
- **Instant-apply binding editing** on the callout chips (with a 5-second Undo chip — see §4.3).
- **App-side DPI presets** (`AppSettings.DpiPresets`, default 800/1600/3200) with one-click apply.
- **Profile card** with graceful degradation (pre-adoption / live / not-live / unknown states).
- **Popup restyle** to the design language (+ a static profile-identity line; behavior untouched).
- **Tray ring icon**: level-colored ring around the existing digits ("digits win" rule, §4.6).
- **Naming**: "Settings" becomes "Dashboard" everywhere (popup button, tray context menu).

**Out of scope (this phase):**
- The **`--probe-profile` spike** (get/set-active-profile discovery) — its own follow-up spec. The
  Profile card is designed to upgrade in place when/if it lands.
- Onboard DPI *stages* (device-held preset slots) — presets here are app-side only.
- Sidebar navigation (documented retrofit path only), lighting/RGB, light-mode themes (all five
  presets are dark), macros, per-app switching (permanently out per Phase B §3.1 reasoning).

## 3. Success criteria

- The dashboard visually matches the approved mockups (Porcelain: charcoal radial canvas, glassy
  1px-stroked rounded cards, big thin numerals, small-caps labels) and is fully theme-switchable
  at runtime with no restart.
- A binding edit end-to-end: click chip → press key → chip shows verified binding + Undo chip;
  the mouse emits the new key immediately (write path identical to Phase B: ensure slot → write →
  read-back verify → persist).
- DPI preset click applies + read-back-verifies within the existing DPI path; slider and preset
  checkmark reflect the device value.
- Popup keeps today's instant show (cached singleton) — only skinned; tray icon shows ring+digits
  at 16–32 px with digits no smaller than today at 16 px.
- **§3.1 gates (measured on the Release install):** idle CPU ~0%, private working set ~23 MB after
  the dashboard has been opened and closed, mouse input feel unchanged. Dashboard releases on close.
- All existing tests stay green; new logic (theme resolution, callout state machine, liveness
  comparer, settings round-trips) is unit-tested via the existing seams.

## 4. User experience

### 4.1 Dashboard layout (approved: "single stage")
One window, no navigation chrome. Minimum size ~980×640 (fixed floor so the stage never crushes).
- **Header card:** status dot (online/offline), "Naga V2 Pro", subtitle "Wireless · Profile 3 ·
  green" (link + app-slot identity), battery chip (⚡ + %), gear button (opens Settings overlay).
- **Stage (center):** vector side-view silhouette of the Naga (XAML `Path`s, subtle body gradient)
  with the 4×3 thumb grid as 12 named key elements. Six callout chips flank each side, connected by
  thin callout lines. **Hover pairing both directions:** hovering a chip lights its key on the
  silhouette; hovering a key highlights its chip.
- **Callout chips (compact by design — crowding was a flagged risk):** position number + binding
  text ("Ctrl+C", "3", "Disabled"), ellipsized with full text on tooltip. Keyboard-focusable.
- **Right rail:** DPI card (small-caps label, big thin current-DPI numeral, slider, preset list
  with colored dots + checkmark on the active value, "+ add preset" / remove affordance) and the
  Profile card (§4.4). Preset dot colors are assigned by list index from a fixed 6-color palette
  (theme-independent; they identify, they don't mean anything).
- **Settings overlay (gear):** slide-over panel — run-at-startup toggle, low-battery threshold +
  notify toggle, poll cadences (battery/charging), **theme picker** (5 presets, live-preview swap),
  "Reset all buttons to factory" (12 factory writes, confirmation required), about line (version +
  repo link). `SetReadDelayMs` remains JSON-only.

### 4.2 Theming
All colors flow from semantic brush keys via `DynamicResource`; **hardcoded colors are forbidden**
in the new XAML. Status colors (positive/warning/critical — battery, live-dot) are constant across
themes; only canvas/card/accent/text/glow brushes vary. Unknown `Theme` value → Porcelain.
Presets: **Porcelain** (default; white-on-charcoal, no accent hue), **Razer** (signature green on
graphite), **Ice** (mockup-faithful blue on deep navy), **Ultraviolet** (violet), **Ember** (warm
amber). A preset = one brush dictionary (~25–30 keys); adding future presets is data, not code.

### 4.3 Binding edits: instant apply + Undo (critique fix #1)
Click a chip → capture mode ("press a key…", Esc or click-away cancels; bare modifiers don't
capture; unmappable keys show the existing reject message). On capture the binding **writes
immediately** (Phase B pipeline; chip shows a Writing state, then a confirmation tick on verified
read-back, or a Failed state with retry hint — never silent success). After a successful write the
chip shows an **Undo** affordance for ~5 s; clicking it rewrites the *previous* binding (from the
table; previously-Default buttons restore via the factory map). Disable / Default live in a small
hover menu on each chip; Default writes the factory action (stageable on any chip — repair path).
The undo window uses a transient one-shot UI delay (like the device-change debounce), not a
persistent timer.

### 4.4 Profile card states (critique fix #5 included)
- **Not adopted:** "No app profile yet — remap any button to create one." (no slot number shown).
- **Live:** "Slot 3 · green · ● bindings live".
- **Not live:** "○ Mouse is on another profile — press the bottom button until the LED is green."
- **Unknown:** mouse offline / liveness unreadable.
Liveness = one effective-read (`GetButton` on profile 0) compared against the app-slot table's
binding for one remapped button; checked **only** when ≥1 remap exists, **only** on dashboard open
and on the card's explicit refresh affordance. Never polled. When the future profile probe lands,
this card upgrades in place (readout or selector) with no layout change.

### 4.5 Popup restyle (critique fix #3 applied: no DPI line)
Same content and behavior as today (cached singleton, re-park off-screen, physical-px positioning,
instant), reskinned: name + link header, big thin percentage, charging pill, level bar, plus one
new **static** line: "Profile 3 · green" (identity from settings — zero I/O, no liveness claim;
the line is hidden entirely while `OnboardSlot` is null, i.e. pre-adoption).
Buttons: "Refresh" and "**Open dashboard**". The stale-prone DPI line was cut. Tray context menu
item renamed "Settings" → "Dashboard" (critique fix #6).

### 4.6 Tray ring (critique fix #4: the "digits win" rule)
GDI+ addition to `IconRenderer`: an arc ring depleting **clockwise from 12 o'clock**, colored by
the existing level semantics (green → amber → red; charging = green + a subtle glow, matching
today's color language). Digits remain the primary read: at 16 px the ring is 1 px and the digits
render **no smaller than today**; the ring thins before digits ever shrink. Verified with real
renders at 16/20/24/32 px during implementation, not after.

### 4.7 Accessibility & motion (critique fix #7)
Chips and controls keyboard-focusable with visible focus/capture states; capture state exposed via
`AutomationProperties`; state never conveyed by color alone (dots always paired with text). All
transitions are one-shot ≤300 ms and are skipped when Windows' animation setting is off
(`SystemParameters.ClientAreaAnimation`). Nothing animates at idle.

## 5. Architecture & components

```
Ui/Themes/DesignSystem.xaml     structural styles: Card, HeaderCard, Chip, NumeralText, LabelText,
                                SliderStyle… — all colors via DynamicResource semantic keys
Ui/Themes/Porcelain.xaml (+Razer/Ice/Ultraviolet/Ember.xaml)   brush dictionaries only
Ui/Themes/ThemeManager.cs       Apply(name): swaps the preset dictionary in Application.Resources;
                                unknown→Porcelain; persists AppSettings.Theme
Ui/Dashboard/DashboardWindow.xaml(.cs)   thin shell: header card, content host, overlay host;
                                releases on close (AppHost nulls it) — never resident
Ui/Dashboard/MouseStageView.xaml(.cs)    silhouette Paths, callout canvas + chips, right rail
Ui/Dashboard/SettingsView.xaml(.cs)      slide-over panel content
Ui/Dashboard/DashboardViewModel.cs       header/DPI/profile state; owns 12 CalloutViewModels
Ui/Dashboard/CalloutViewModel.cs         per-button state machine:
                                Idle → Capturing → Writing → Confirmed | Failed, + UndoAvailable
Ui/IconRenderer.cs (modify)     ring pass around existing digit rendering
Ui/PopupWindow (modify)         reskin + profile line + button rename; behavior untouched
Settings/AppSettings.cs (modify)  + Theme (string, default "Porcelain"),
                                  + DpiPresets (List<int>, default [800,1600,3200])
AppHost.cs (modify)             OpenSettings→OpenDashboard; popup/tray wiring renames; the
                                instant-apply write path reuses ApplyButtonsAsync's pipeline
                                (EnsureOnboardSlotAsync unchanged)
DELETED: Ui/SettingsWindow.xaml(.cs), Ui/SettingsViewModel.cs, Ui/ButtonRowViewModel.cs
         (CalloutViewModel supersedes the staged-op model; its tests are replaced too)
```

Data flow: dashboard opens on cached `BatteryMonitor.State` instantly; then off-thread (Task.Run,
monitor `_readLock`): one `GetDpiAsync` to seed the DPI card, one liveness read per §4.4. Captures
write through the monitor exactly as Phase B does. Theme switch = dictionary swap + settings save;
no window rebuild. The popup performs **zero** I/O beyond today's.

**Sidebar retrofit path (documented, not built):** the shell hosts views by contract; adding a nav
rail column and presenting `SettingsView` as a page instead of an overlay is a shell-only change.

## 6. Error handling & edge cases

- **Mouse offline:** dashboard opens on cached state; silhouette dims, chips/DPI disabled with a
  quiet "mouse offline — reconnect to edit" note; header dot + Profile card show it. No error dialogs.
- **Capture write fails / read-back mismatch:** chip → Failed state ("Not applied — wiggle the
  mouse and retry"); nothing persisted; other chips unaffected.
- **Slot adoption fails** (list unreadable / all slots foreign / create refused): chip → Failed with
  the existing honest message; user slots are never candidates (Phase B invariants untouched).
- **Undo pressed after a failed write:** impossible — Undo only appears on verified success.
- **Unknown theme name** in settings: Porcelain, silently. **Corrupt settings:** defaults (existing).
- **DPI preset applied while offline:** button disabled; nothing to fail.
- **Reset-all-to-factory:** confirmation required; per-button write + verify; per-chip results.

## 7. Threading & concurrency

Unchanged discipline: every HID exchange goes through `BatteryMonitor`'s single `_readLock` off the
UI thread; concurrent captures on different chips serialize on that lock (each chip's Writing state
prevents re-entry per button). UI updates marshal via the existing `Dispatch`. No new persistent
timers/threads — the Undo window and transitions are transient one-shots. The dashboard releases on
close so idle memory returns to baseline; the popup remains the lone cached UI singleton.

## 8. Testing strategy

Unit (existing seams, `FakeRazerDevice` where device-facing):
- `ThemeManager`: known names resolve, unknown → Porcelain, selection persists.
- `CalloutViewModel`: full state machine — capture→write→Confirmed; write fail→Failed (no persist);
  Esc/click-away cancel; bare-modifier and unmappable-key rejection; Undo restores prior binding
  (incl. previously-Default → factory) and expires; Disable/Default ops.
- Liveness comparer (pure function): match→Live, mismatch→NotLive, unreadable→Unknown, no
  remaps→skip.
- Settings: `Theme` + `DpiPresets` round-trip; old settings files (without the fields) load with
  defaults.
- `IconRenderer`: ring rendering at 16/20/24/32 px across levels + charging renders without error
  (same smoke style as existing icon tests).
Replaced: `ButtonRowViewModelTests`, `SettingsViewModelTests` (superseded by the above).
Manual: 5-theme visual pass (contrast, focus states), hover pairing, reduced-motion behavior, and
the §3.1 gates on the Release install (idle CPU ~0%, ~23 MB after dashboard open/close, input feel).

## 9. Out of scope / future

`--probe-profile` spike (next up, own spec); Profile card upgrade to readout/selector; sidebar
retrofit; onboard DPI stages; additional theme presets (pure data); light themes; per-button
lighting.

## 10. Global constraints (carried over)

- No admin/UAC; per-user; single-instance mutex unchanged; `net10.0-windows10.0.19041.0`;
  WPF + WinForms; WPF-UI 4.3.0 — **no new dependencies**.
- Lightweight + zero input-latency are **hard gates** (§3): device I/O on-demand only, one shared
  lock, off-UI; dashboard release-on-close; no `DropShadowEffect`/`BitmapEffect`; no idle animation;
  no new background timers/threads; never poll DPI/buttons/liveness.
- Phase B onboard-slot invariants unchanged: writes only to the app-owned slot; user slots never
  written; journal/verify discipline intact.
- DRY, YAGNI, TDD, frequent commits, conventional commits, surgical changes.
