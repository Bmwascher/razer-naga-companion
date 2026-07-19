# Dashboard polish — type roles, DPI pills, profile identity, rail alignment

Date: 2026-07-19 · Branch: `dashboard-polish` · Status: designed, user-approved scope

## 1. Motivation

Post-v2.3 screenshot review (user, 2026-07-19). Four defects, all in the right rail and card
typography; the stage, chips, and header are explicitly out of scope ("what's working — don't
touch it"). This pass also closes the three recorded DPI-card gripes from the roadmap
(hovered preset row reads as a text-input box, the hover-revealed ✕ floats far from the value,
a failed DPI apply is silent).

Hard constraints carried over: no new device I/O, no timers, no polling; no
`DropShadowEffect`/`BitmapEffect`; no hardcoded colors in themed XAML (new colors enter as
theme-independent `DesignSystem.xaml` keys, the `Status.*` precedent).

## 2. Scope

- **A — CardTitle type role**: card/section headers get a real header style; the 10px
  small-caps `LabelText` stops doing title duty.
- **B — DPI card**: preset rows become content-sized pill segments; a status line surfaces
  failed applies.
- **C — Profile card**: slots are renameable (app-side), the LED colour becomes a dot in the
  dropdown rows plus a caption under the box.
- **D — Rail alignment**: the rail shares a top datum with the chip columns.

Out of scope: onboard DPI stage table / polling rate (own roadmap item), slot create/delete,
lighting, any stage/chip/header change. Names are **not** written to hardware — the firmware
stores no slot names (a slot is a number + LED colour), so a name is an app-side label in
`settings.json`.

## 3. A — CardTitle type role

Problem: `LabelText` is `FontSize 10` + `Typography.Capitals="AllSmallCaps"`, and every consumer
feeds it already-uppercase text — all-small-caps renders those glyphs near x-height, so the
*effective* letter height is ~7px. Fine for a unit suffix; wrong for the only header a card has.

- `DesignSystem.xaml` — new style:

  ```xml
  <Style x:Key="CardTitle" TargetType="TextBlock">
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Setter Property="Foreground" Value="{DynamicResource App.TextSecondary}"/>
    <Setter Property="Typography.Capitals" Value="SmallCaps"/>
  </Style>
  ```

  `SmallCaps` (not `AllSmallCaps`): with mixed-case input the leading capital keeps full 12px
  height and the rest render as small caps — a real header. Uppercase input ("DPI") passes
  through at full 12px caps, so the same style serves the unit suffix.
- Consumers (text changes to mixed case where noted):
  - `MouseStageView` Profile header: `LabelText "PROFILE"` → `CardTitle "Profile"`.
  - `MouseStageView` DPI suffix next to the numeral: `LabelText` → `CardTitle`, text `DPI`
    unchanged; keep `VerticalAlignment="Bottom"`, margin becomes `4,0,0,5` (baseline-align to
    the 34px numeral).
  - `SettingsView` section headers → `CardTitle` with "Theme", "Tray icon", "General",
    "Battery polling (seconds)", "Buttons".
- `LabelText` itself is untouched; its one remaining consumer is the chip-number column
  (which already overrides to 12px).

## 4. B — DPI card: pill presets + failure status

### 4.1 Pills

Replaces the vertical rows (full-width transparent Buttons + docked ✕ + dot/✓ indicators).
Root cause of both gripes: the row Button stretches the full ~200px card width, so the themed
hover background paints a giant bar behind ~50px of content, and the ✕ docks to the far edge.

- `ItemsControl` panel → `WrapPanel`; item margin `0,0,6,6`; list margin `0,8,0,0`.
- Each pill is a `Button` with its **own ControlTemplate** (escaping the WPF-UI themed hover
  background is the fix for the "reads as a text input" gripe):
  - Template root: `Border CornerRadius="8" Padding="8,3"` + ContentPresenter.
  - Rest: `Background App.ChipFill`, `BorderBrush App.ChipStroke`, thickness 1, value text
    `BodyText` (12) `App.TextPrimary`.
  - Hover / keyboard focus: `BorderBrush App.Accent` (border only — hover must read as
    "hoverable", not "selected").
  - Active (`IsActive`): `Background App.AccentSoft` + `BorderBrush App.Accent`. The grey dot
    and trailing ✓ retire — the pill state is the indicator (this also removes the per-row
    text-width jitter the ✓ caused).
  - Disabled (`!DevicePresent`): Opacity 0.45 on the template root.
- Pill content: value text + nested remove `Button` (`✕`, `FontSize 9`, width 14,
  margin `4,0,0,0`, Opacity 0.6). Remove is `Visibility=Hidden` at rest → `Visible` on pill
  `IsMouseOver` — **Hidden, not Collapsed**, so the width is reserved and pills never resize
  under the pointer (the app's reserved-space idiom). The nested button captures its own
  click, so remove never also applies. Deliberate consequence (branch review, accepted): the
  nested ✕ inherits the pill's `!DevicePresent` disable, so presets can't be removed while the
  mouse is offline — the old layout's offline removal was accidental, and whole-pill disable
  matches how the rest of the dashboard treats an absent device.
- Keyboard: pills are Buttons — Tab reaches them, Enter/Space applies; the focus state shows
  the accent border.
- "+ Save 1600": restyled as a **ghost pill** on its own line under the preset row (margin
  `0,0,0,0` — the 6px wrap gap separates it): same geometry (`CornerRadius 8, Padding 8,3`),
  `Background Transparent`, `BorderBrush App.ChipStroke`, text `App.TextSecondary`. Keeps the
  existing `+ Save {Dpi}` TextBlock-child binding, disabled-dimming mirror, `CanSavePreset`
  gate, and tooltip.
- Handlers (`OnApplyPreset`/`OnRemovePreset`/`OnSavePreset`) and the VM preset model
  (`DpiPresetItem`, `AddPreset`, `RemovePreset`, `RefreshActive`) are unchanged.

### 4.2 Failure status line

The old Settings window said "Couldn't confirm — wiggle the mouse and retry"; the card lost
that surface (recorded gripe — a failed apply is currently silent: `AppHost.ApplyDpiAsync`
just re-reads and moves on).

- VM: `string DpiStatus` (INPC, default `""`) + `void SetDpiStatus(string s)`.
- `AppHost.ApplyDpiAsync`: clear (`""`) at the start of every apply; on the verified-success
  path leave cleared; on the failure path set
  `"Couldn't confirm — wiggle the mouse and retry"` (after the re-read dispatch).
- XAML: TextBlock at the card bottom (after the Save pill, margin `0,6,0,0`):
  `SubtleText` base, `Foreground {StaticResource Status.Warning}`, `TextWrapping Wrap`,
  Collapsed while `DpiStatus` is `""` (style DataTrigger). Appearing shifts the card ~16px —
  acceptable for a rare event (same behavior as the profile note).

## 5. C — Profile card: rename + LED colour

### 5.1 Slot colour brushes

`DesignSystem.xaml`, theme-independent (the `Status.*` precedent — these are hardware LED
identities, not theme accents):

```xml
<SolidColorBrush x:Key="Slot.White" Color="#F2F2F2"/>
<SolidColorBrush x:Key="Slot.Red"   Color="#E5484D"/>
<SolidColorBrush x:Key="Slot.Green" Color="#43D675"/>
<SolidColorBrush x:Key="Slot.Blue"  Color="#3E7BFA"/>
<SolidColorBrush x:Key="Slot.Cyan"  Color="#29C8DE"/>
```

### 5.2 Model

- `ProfileSlotItem`: gains `Name` (INPC string). The `Label` property is **deleted** (its
  only consumer was `DisplayMemberPath`; tests assert `Name` instead); `Colour` stays
  (feeds the caption), `SlotColour` stays.
- `DashboardViewModel`: holds `Dictionary<byte, string> _profileNames`, seeded from
  `AppSettings.ProfileNames` in the ctor. `RebuildSlots` resolves each item's `Name` from the
  map, default `$"Slot {n}"`. `ApplyTo` writes the map back
  (`target.ProfileNames = new(_profileNames)`) — persistence rides the existing
  save-on-close / overlay-dismiss path, like `DpiPresets`.
- `AppSettings`: `public Dictionary<byte, string> ProfileNames { get; set; } = new();`
  (System.Text.Json handles numeric keys — `ButtonBindings` is the in-repo precedent).

### 5.3 Dropdown template

`DisplayMemberPath` → `ItemTemplate` (WPF reuses it for the closed box):

- Horizontal StackPanel: `Ellipse 8×8` + TextBlock `{Binding Name}` margin `6,0,0,0`.
- Ellipse: `Stroke {DynamicResource App.ChipStroke}` `StrokeThickness 1` (the ring keeps the
  white dot visible on Porcelain/light cards); `Fill` via style DataTriggers on `Number`
  (1→`Slot.White` … 5→`Slot.Cyan`, `StaticResource` — the brushes are theme-independent).

### 5.4 Rename (app-side label)

- Header row: ✎ button (`ui:Button`, `Padding 6,2`, `Opacity 0.6`) docked right, left of ↻;
  `IsEnabled {Binding DeviceOnline}` (offline ⇒ no selection ⇒ nothing to rename).
- VM state machine:
  - `bool IsRenamingProfile` (INPC), `string ProfileNameDraft` (INPC, two-way).
  - `BeginRename()`: no-op unless `SelectedProfileSlot` non-null; draft = selected `Name`;
    `IsRenamingProfile = true`.
  - `CommitRename()`: trim; clamp to 24 chars; empty/whitespace **or** equal to the default
    `"Slot {n}"` → remove the map entry and reset `Name` to default; else map[`Number`] =
    name and `Name` = name. `IsRenamingProfile = false`.
  - `CancelRename()`: `IsRenamingProfile = false`, draft discarded.
  - `SyncSelection()` cancels an open rename (a slot switch/inventory change mid-edit
    retargets the box — never let a draft land on a different slot).
- XAML: the ComboBox collapses while renaming; a TextBox
  (`MaxLength 24`, `UpdateSourceTrigger=PropertyChanged`) shows in its place. Code-behind:
  Enter → `CommitRename()`, Esc → `CancelRename()`, LostFocus → `CommitRename()`;
  focus + select-all when revealed.

### 5.5 LED caption (the "circle + text below the box")

- New row under the dropdown: `Ellipse 8×8` (same style as §5.3) + `SubtleText`
  `{Binding SelectedProfileSlot.Colour, StringFormat={}{0} — LED on the mouse}`.
- Visible only when `ShowLedCaption` — a VM bool (INPC):
  `ProfileDetail == "" && SelectedProfileSlot != null`, re-notified wherever either input
  changes. The existing transient `ProfileDetail` TextBlock keeps its slot; the two are
  complementary (a note — Switching…/failure/unreachable — replaces the caption).
- With the dot carrying the colour, the `"Slot {n} · {colour}"` label text retires (a rename
  would make the colour word read as part of the name).

## 6. D — Rail alignment

The rail (`Grid.Column 3`) centers independently of the chip columns, so its card edges align
with nothing (screenshot: DPI card top ~68px above the first chip row). Shared top datum via a
computed margin — the rail stays Top-aligned and **height-unconstrained**:

```xml
<StackPanel Grid.Column="3" VerticalAlignment="Top">
  <StackPanel.Margin>
    <MultiBinding Converter="{StaticResource RailTopMargin}">
      <Binding Path="ActualHeight" RelativeSource="{RelativeSource AncestorType=Grid}"/>
      <Binding Path="ActualHeight" ElementName="LeftChips"/>
    </MultiBinding>
  </StackPanel.Margin>
  <!-- existing two cards -->
</StackPanel>
```

`RailTopMarginConverter` returns `Thickness(8, max(0, (columnH − chipsH) / 2), 0, 0)` — the
offset the centered chips column sits at — and tolerates `UnsetValue` during the first layout
pass. Rail content taller than the chips band hangs below.

> **Superseded first cut (hardware run 2026-07-19):** the original design here bound a wrapper
> Grid's `Height` to the chips' height with top-aligned content, claiming "Grid doesn't clip."
> Wrong: WPF applies a **layout clip** to a child arranged smaller than its desired size, which
> amputated everything past the chips band — the Profile card rendered as a bare header (the
> user's screenshot). Never reintroduce a bound Height on the rail.

The rail column also widens 230 → 248: three preset pills, each reserving ✕ width, need ~206px
of card content width; at 230 the third pill wrapped to its own line.

## 7. Testing

Logic layers only (convention — XAML is verified on the installed build):

- `DashboardViewModelTests`: `DpiStatus` set/clear; slot `Name` default + custom seed from
  settings; rename flow — begin/commit/cancel, trim, 24-char clamp, empty → default,
  default-name commit removes the map entry, commit updates `ApplyTo` round-trip,
  selection sync mid-rename cancels; `ShowLedCaption` truth table (detail note, no
  selection, steady state).
- `JsonSettingsStoreTests`: `ProfileNames` round-trip; corrupt file still resets to defaults
  (empty map).

## 8. Acceptance

- Visual pass on the installed build across all 5 themes (especially the white slot dot on
  Porcelain, and pill contrast on Ember/Razer).
- DPI: pill hover highlights the pill only (no full-width bar); ✕ sits inside the pill next
  to its value; a failed apply (unplug, then drag) shows the retry line; success clears it.
- Profile: rename survives app restart (settings.json); dropdown dots match the mouse's
  bottom-button LED colours; caption/note swap correctly during a switch and on failure.
- Footprint + input latency unchanged (no device I/O added — nothing to re-measure beyond
  the standard idle check).
