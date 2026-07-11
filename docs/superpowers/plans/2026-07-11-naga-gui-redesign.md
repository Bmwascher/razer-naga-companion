# GUI Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Settings window with an Apple-esque themed dashboard (vector Naga + instant-apply
binding chips + DPI presets + profile card), restyle the popup, and add a battery ring to the tray icon.

**Architecture:** A semantic-brush design system (`Ui/Themes/`, 5 preset dictionaries swapped at runtime
by `ThemeManager`) under a shell+views dashboard (`Ui/Dashboard/`). Bindings edit via a per-button
`CalloutViewModel` state machine that writes through the existing Phase B pipeline (ensure app slot →
write → read-back verify → persist) with a 5 s undo. Old `SettingsWindow`/`SettingsViewModel`/
`ButtonRowViewModel` are deleted. Spec: `docs/superpowers/specs/2026-07-11-naga-gui-redesign-design.md`.

**Tech Stack:** .NET 10 WPF + WPF-UI 4.3.0 (FluentWindow), GDI+ (tray icon), xUnit.

## Global Constraints

- ~0% idle CPU, ~23 MB private working set (Release install), zero input-latency regression — gates.
- No new dependencies; no new background timers/threads (transient one-shots OK); device I/O on-demand
  only via `BatteryMonitor`'s `_readLock`, off the UI thread (`Task.Run`); never poll DPI/buttons/liveness.
- **No `DropShadowEffect`/`BitmapEffect` anywhere** (software-rendered) — glows are gradient brushes.
  No looping/idle animations; one-shot transitions ≤300 ms, skipped when
  `SystemParameters.ClientAreaAnimation` is false.
- **No hardcoded colors in new XAML** — semantic keys via `DynamicResource` only. Status colors
  (positive/warning/critical) are theme-independent constants.
- Dashboard releases on close (`_dashboard = null`); popup stays the cached singleton, zero added I/O.
- Phase B invariants untouched: writes only to the app-owned onboard slot; user slots never written.
- Build: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`; test: `… test`. Dev-run via dotnet
  host (SAC): `Start-Process -WindowStyle Hidden "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" -ArgumentList '"C:\Users\Brandon\naga-battery-tray\src\NagaBatteryTray\bin\Debug\net10.0-windows10.0.19041.0\NagaBatteryTray.dll"'`.
- Conventional commits; every commit message ends with the standard co-author + session trailers.

---

### Task 1: Settings — `Theme` + `DpiPresets`

**Files:**
- Modify: `src/NagaBatteryTray/Settings/AppSettings.cs`
- Test: `tests/NagaBatteryTray.Tests/SettingsStoreTests.cs`

**Interfaces:**
- Produces: `AppSettings.Theme` (`string`, default `"Porcelain"`); `AppSettings.DpiPresets`
  (`List<int>`, default `[800, 1600, 3200]`). Consumed by Tasks 3, 6, 9.

- [ ] **Step 1: Write the failing tests** (append to `SettingsStoreTests`)

```csharp
    [Fact]
    public void Theme_defaults_porcelain_and_round_trips()
    {
        var path = TempFile();
        var store = new JsonSettingsStore(path);
        Assert.Equal("Porcelain", store.Settings.Theme);
        store.Settings.Theme = "Ember";
        store.Save();
        Assert.Equal("Ember", new JsonSettingsStore(path).Settings.Theme);
    }

    [Fact]
    public void DpiPresets_default_and_round_trip()
    {
        var path = TempFile();
        var store = new JsonSettingsStore(path);
        Assert.Equal(new[] { 800, 1600, 3200 }, store.Settings.DpiPresets);
        store.Settings.DpiPresets = new List<int> { 400, 12000 };
        store.Save();
        Assert.Equal(new[] { 400, 12000 }, new JsonSettingsStore(path).Settings.DpiPresets);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: FAIL — CS1061 `Theme`/`DpiPresets` not defined.

- [ ] **Step 3: Implement** — append to `AppSettings` (after `OnboardSlot`):

```csharp
    /// <summary>Active theme preset name (Ui/Themes). Unknown value → Porcelain at apply time.</summary>
    public string Theme { get; set; } = "Porcelain";

    /// <summary>App-side one-click DPI presets shown in the dashboard's DPI card.</summary>
    public List<int> DpiPresets { get; set; } = new() { 800, 1600, 3200 };
```

- [ ] **Step 4: Run tests — PASS**, full suite still green (`… test` → 103 + 2 pass).
- [ ] **Step 5: Commit** — `feat(settings): Theme + DpiPresets`

---

### Task 2: Tray ring around the digits

**Files:**
- Modify: `src/NagaBatteryTray/Ui/IconRenderer.cs`
- Test: `tests/NagaBatteryTray.Tests/IconRendererTests.cs`

**Interfaces:**
- Consumes: existing `IconRenderer.Render(DeviceState, int dpi)` / `ColorForLevel`.
- Produces: same public API — ring is internal to `Render`. **Digits-win rule:** ring drawn FIRST
  (behind), digit layout code untouched, so digits render at exactly today's size.

- [ ] **Step 1: Write the failing test** (append to `IconRendererTests` — match its existing style; it
  already renders icons, so mirror the smoke pattern):

```csharp
    [Theory]
    [InlineData(96, 100, false)]   // 16 px
    [InlineData(120, 87, false)]   // 20 px
    [InlineData(144, 38, true)]    // 24 px, charging
    [InlineData(192, 12, false)]   // 32 px, low
    public void Render_with_ring_smokes_at_all_sizes(int dpi, int percent, bool charging)
    {
        var icon = IconRenderer.Render(DeviceState.Online(percent, charging, false), dpi);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
        IconRenderer.Destroy(icon);
    }

    [Fact]
    public void Render_unknown_state_smokes()
    {
        var icon = IconRenderer.Render(DeviceState.Unknown, 96);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
        IconRenderer.Destroy(icon);
    }
```

- [ ] **Step 2: Run — these pass already** (Render smokes today). That's fine: they lock the contract
  before the change; the ring itself is verified visually in Step 5.

- [ ] **Step 3: Implement the ring.** In `Render`, inside the `using (var g = …)` block, immediately
  after `g.Clear(Color.Transparent);` and BEFORE the digit-path code, insert:

```csharp
            // Battery ring, drawn BEHIND the digits (digits-win rule: digit layout is untouched).
            // Depletes clockwise from 12 o'clock; track is a faint full circle. ~1 px at 16 px tray size.
            float ringW = Math.Max(2f, render * 0.07f);
            var ringRect = new RectangleF(ringW / 2f, ringW / 2f, render - ringW, render - ringW);
            using (var track = new Pen(Color.FromArgb(45, 255, 255, 255), ringW))
                g.DrawEllipse(track, ringRect);
            int pct = state.Status == DeviceStatus.Unknown ? 0 : Math.Clamp(state.Percent, 0, 100);
            if (pct > 0)
            {
                if (state.Status != DeviceStatus.Unknown && state.Charging)
                    using (var glow = new Pen(Color.FromArgb(70, color), ringW * 2f))
                        g.DrawArc(glow, ringRect, -90f, 360f * pct / 100f);
                using var arc = new Pen(color, ringW) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, ringRect, -90f, 360f * pct / 100f);
            }
```

  (`color` is the existing local; full 100% arc: `DrawArc` with sweep 360 renders a full circle.)

- [ ] **Step 4: Run the full suite — PASS.**
- [ ] **Step 5: Visual check** — build, launch dev instance via the dotnet host, confirm in the real
  tray: ring visible at the edge, digits unchanged in size, colors track level, charging shows the
  brighter green treatment. Kill the dev instance after.
- [ ] **Step 6: Commit** — `feat(tray): battery ring behind the digits (digits-win rule)`

---

### Task 3: Theme system — DesignSystem + 5 presets + ThemeManager

**Files:**
- Create: `src/NagaBatteryTray/Ui/Themes/DesignSystem.xaml`, `Porcelain.xaml`, `Razer.xaml`,
  `Ice.xaml`, `Ultraviolet.xaml`, `Ember.xaml`, `ThemeManager.cs`
- Modify: `src/NagaBatteryTray/AppHost.cs` (Start(): merge + apply)
- Test: Create `tests/NagaBatteryTray.Tests/ThemeManagerTests.cs`

**Interfaces:**
- Produces: `ThemeManager.PresetNames` (`string[]`), `ThemeManager.Resolve(string?) → string`,
  `ThemeManager.DictionaryUri(string?) → Uri`, `ThemeManager.Apply(Application, string?)`.
  Semantic brush keys (every theme dictionary defines ALL of them):
  `App.CanvasBrush, App.CardFill, App.CardStroke, App.ChipFill, App.ChipStroke, App.Accent,
  App.AccentSoft, App.TextPrimary, App.TextSecondary, App.Numeral, App.GlowSoft` + marker string
  `App.ThemeName`. DesignSystem adds theme-independent `Status.Positive (#43D675),
  Status.Warning (#E6A23C), Status.Critical (#E5484D)` brushes and styles
  `CardBorder, ChipBorder, LabelText, NumeralText, BodyText, SubtleText`.

- [ ] **Step 1: Failing tests** (`ThemeManagerTests.cs`):

```csharp
using NagaBatteryTray.Ui;
using Xunit;

public class ThemeManagerTests
{
    [Theory]
    [InlineData("Porcelain")] [InlineData("Razer")] [InlineData("Ice")]
    [InlineData("Ultraviolet")] [InlineData("Ember")]
    public void Known_names_resolve_to_themselves(string name) =>
        Assert.Equal(name, ThemeManager.Resolve(name));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("Neon")] [InlineData("porcelain")]
    public void Unknown_names_fall_back_to_porcelain(string? name) =>
        Assert.Equal("Porcelain", ThemeManager.Resolve(name));

    [Fact]
    public void DictionaryUri_points_into_ui_themes() =>
        Assert.Equal("pack://application:,,,/Ui/Themes/Ember.xaml",
            ThemeManager.DictionaryUri("Ember").ToString());
}
```

- [ ] **Step 2: Run — FAIL** (type not found).

- [ ] **Step 3: Implement `ThemeManager.cs`:**

```csharp
using System.Windows;

namespace NagaBatteryTray.Ui;

/// <summary>Runtime theme preset switching: a preset is one brush ResourceDictionary in Ui/Themes
/// carrying the marker key "App.ThemeName". Apply() swaps the marked dictionary in place.</summary>
public static class ThemeManager
{
    public static readonly string[] PresetNames = { "Porcelain", "Razer", "Ice", "Ultraviolet", "Ember" };

    public static string Resolve(string? name) =>
        Array.IndexOf(PresetNames, name) >= 0 ? name! : "Porcelain";

    public static Uri DictionaryUri(string? name) =>
        new($"pack://application:,,,/Ui/Themes/{Resolve(name)}.xaml");

    public static void Apply(Application app, string? name)
    {
        var next = new ResourceDictionary { Source = DictionaryUri(name) };
        var dicts = app.Resources.MergedDictionaries;
        for (int i = 0; i < dicts.Count; i++)
            if (dicts[i].Contains("App.ThemeName")) { dicts[i] = next; return; }
        dicts.Add(next);
    }
}
```

- [ ] **Step 4: Create the five preset dictionaries.** `Porcelain.xaml` (the template — others differ
  only in color values):

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="clr-namespace:System;assembly=System.Runtime">
  <sys:String x:Key="App.ThemeName">Porcelain</sys:String>
  <RadialGradientBrush x:Key="App.CanvasBrush" Center="0.25,0" GradientOrigin="0.25,0" RadiusX="1.2" RadiusY="1.4">
    <GradientStop Color="#17181C" Offset="0"/><GradientStop Color="#0E0F12" Offset="0.6"/><GradientStop Color="#0A0B0D" Offset="1"/>
  </RadialGradientBrush>
  <LinearGradientBrush x:Key="App.CardFill" StartPoint="0,0" EndPoint="0,1">
    <GradientStop Color="#0DFFFFFF" Offset="0"/><GradientStop Color="#04FFFFFF" Offset="1"/>
  </LinearGradientBrush>
  <SolidColorBrush x:Key="App.CardStroke" Color="#1AFFFFFF"/>
  <LinearGradientBrush x:Key="App.ChipFill" StartPoint="0,0" EndPoint="0,1">
    <GradientStop Color="#12FFFFFF" Offset="0"/><GradientStop Color="#06FFFFFF" Offset="1"/>
  </LinearGradientBrush>
  <SolidColorBrush x:Key="App.ChipStroke" Color="#24FFFFFF"/>
  <SolidColorBrush x:Key="App.Accent" Color="#FFFFFF"/>
  <SolidColorBrush x:Key="App.AccentSoft" Color="#26FFFFFF"/>
  <SolidColorBrush x:Key="App.TextPrimary" Color="#F0F1F4"/>
  <SolidColorBrush x:Key="App.TextSecondary" Color="#8AF0F1F4"/>
  <SolidColorBrush x:Key="App.Numeral" Color="#FFFFFF"/>
  <RadialGradientBrush x:Key="App.GlowSoft" Center="0.5,0.5" GradientOrigin="0.5,0.5" RadiusX="0.5" RadiusY="0.5">
    <GradientStop Color="#20FFFFFF" Offset="0"/><GradientStop Color="#00FFFFFF" Offset="1"/>
  </RadialGradientBrush>
</ResourceDictionary>
```

  The other four: copy, change `App.ThemeName` and these values (same keys, same structure):
  - **Razer.xaml** — canvas stops `#131613/#0B0D0B/#080908`; accent `#44D62C`; accent-soft `#2E44D62C`;
    numeral `#7EE76A`; card/chip strokes `#2444D62C`/`#3344D62C`; glow center `#2A44D62C`.
  - **Ice.xaml** — canvas `#10182B/#0A0E1A/#070A12`; accent `#548CFF`; accent-soft `#2E548CFF`;
    numeral `#9DB9FF`; strokes `#2478A0FF`/`#3378A0FF`; glow center `#2A548CFF`.
  - **Ultraviolet.xaml** — canvas `#181026/#0F0A18/#0A0710`; accent `#A06BFF`; accent-soft `#2EA06BFF`;
    numeral `#C3A2FF`; strokes `#24A06BFF`/`#33A06BFF`; glow center `#2AA06BFF`.
  - **Ember.xaml** — canvas `#241410/#160D0A/#100907`; accent `#FF9C57`; accent-soft `#2EFF9C57`;
    numeral `#FFBE8C`; strokes `#24FF9C57`/`#33FF9C57`; glow center `#2AFF9C57`.
  Text primary/secondary and card fill stay identical across presets.

- [ ] **Step 5: Create `DesignSystem.xaml`** (structural styles; theme-independent status brushes):

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="Status.Positive" Color="#43D675"/>
  <SolidColorBrush x:Key="Status.Warning" Color="#E6A23C"/>
  <SolidColorBrush x:Key="Status.Critical" Color="#E5484D"/>

  <Style x:Key="CardBorder" TargetType="Border">
    <Setter Property="Background" Value="{DynamicResource App.CardFill}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource App.CardStroke}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="CornerRadius" Value="12"/>
    <Setter Property="Padding" Value="14"/>
  </Style>
  <Style x:Key="ChipBorder" TargetType="Border">
    <Setter Property="Background" Value="{DynamicResource App.ChipFill}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource App.ChipStroke}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="CornerRadius" Value="9"/>
    <Setter Property="Padding" Value="8,5"/>
  </Style>
  <Style x:Key="LabelText" TargetType="TextBlock">
    <Setter Property="FontSize" Value="10"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Setter Property="Foreground" Value="{DynamicResource App.TextSecondary}"/>
    <Setter Property="Typography.Capitals" Value="AllSmallCaps"/>
  </Style>
  <Style x:Key="NumeralText" TargetType="TextBlock">
    <Setter Property="FontSize" Value="34"/>
    <Setter Property="FontWeight" Value="ExtraLight"/>
    <Setter Property="Foreground" Value="{DynamicResource App.Numeral}"/>
  </Style>
  <Style x:Key="BodyText" TargetType="TextBlock">
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="Foreground" Value="{DynamicResource App.TextPrimary}"/>
  </Style>
  <Style x:Key="SubtleText" TargetType="TextBlock">
    <Setter Property="FontSize" Value="11"/>
    <Setter Property="Foreground" Value="{DynamicResource App.TextSecondary}"/>
  </Style>
</ResourceDictionary>
```

  All six .xaml files need `<Page … />` build action fixed to `Resource`? No — WPF SDK projects treat
  `.xaml` under the project as `Page` automatically, which compiles to BAML and works with
  pack URIs; no csproj edit needed.

- [ ] **Step 6: Wire startup.** In `AppHost.Start()`, after
  `_app.Resources.MergedDictionaries.Add(new ControlsDictionary());` add:

```csharp
        _app.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri("pack://application:,,,/Ui/Themes/DesignSystem.xaml") });
        ThemeManager.Apply(_app, _settings.Settings.Theme);
```

- [ ] **Step 7: Test + build — PASS** (ThemeManager tests green, app builds, existing UI unaffected).
- [ ] **Step 8: Commit** — `feat(ui): design system + 5 theme presets + ThemeManager`

---

### Task 4: `CalloutViewModel` — instant-apply state machine with undo

**Files:**
- Create: `src/NagaBatteryTray/Ui/Dashboard/CalloutViewModel.cs`
- Test: Create `tests/NagaBatteryTray.Tests/CalloutViewModelTests.cs`

**Interfaces:**
- Consumes: `KeyToHidUsage.Describe(byte, byte)`, `NagaV2ProButtons.FactoryBindingForPosition(int)`,
  `ButtonActionKind`.
- Produces (used by Tasks 6, 8):
  `CalloutViewModel(int position, CalloutViewModel.WriteBinding write, Func<Task>? undoWindow = null)`;
  `delegate Task<bool> WriteBinding(int position, ButtonActionKind kind, byte modifiers, byte usage)`;
  members `Position`, `Label`, `BindingText`, `IsCapturing`, `IsBusy`, `CanUndo`, `Status`,
  `IsHighlighted`, `SetApplied(ButtonActionKind, byte, byte)`, `BeginCapture()`, `CancelCapture()`,
  `Task CaptureAsync(byte modifiers, byte usage)`, `Task DisableAsync()`, `Task DefaultAsync()`,
  `Task UndoAsync()`.

- [ ] **Step 1: Failing tests** (`CalloutViewModelTests.cs`):

```csharp
using NagaBatteryTray.Hid;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public class CalloutViewModelTests
{
    private sealed class Recorder
    {
        public readonly List<(int Pos, ButtonActionKind Kind, byte Mods, byte Usage)> Writes = new();
        public bool Result = true;
        public Task<bool> Write(int p, ButtonActionKind k, byte m, byte u)
        { Writes.Add((p, k, m, u)); return Task.FromResult(Result); }
    }

    private static (CalloutViewModel vm, Recorder rec, TaskCompletionSource undo) NewVm(int pos = 1)
    {
        var rec = new Recorder();
        var tcs = new TaskCompletionSource();
        return (new CalloutViewModel(pos, rec.Write, () => tcs.Task), rec, tcs);
    }

    [Fact]
    public void Untouched_shows_factory_key_name()
    {
        var (vm, _, _) = NewVm(3);
        Assert.Equal("3", vm.BindingText); // factory digit for position 3
    }

    [Fact]
    public async Task Capture_writes_and_confirms_and_offers_undo()
    {
        var (vm, rec, _) = NewVm();
        vm.BeginCapture();
        Assert.True(vm.IsCapturing);
        await vm.CaptureAsync(0x01, 0x06); // Ctrl+C
        Assert.False(vm.IsCapturing);
        Assert.Equal(("Ctrl+C"), vm.BindingText);
        Assert.True(vm.CanUndo);
        Assert.Equal((1, ButtonActionKind.Key, (byte)0x01, (byte)0x06), rec.Writes.Single());
    }

    [Fact]
    public async Task Failed_write_shows_failure_and_keeps_prior_binding()
    {
        var (vm, rec, _) = NewVm();
        rec.Result = false;
        vm.BeginCapture();
        await vm.CaptureAsync(0x00, 0x3a);
        Assert.Equal("3? — not applied, retry", $"{vm.BindingText}? — {vm.Status}".Replace("Not applied — wiggle the mouse and retry", "not applied, retry")); // see simplified asserts below
    }
```

  Write the failure test plainly instead (the above is illustrative-bad — use this):

```csharp
    [Fact]
    public async Task Failed_write_keeps_prior_binding_and_no_undo()
    {
        var (vm, rec, _) = NewVm(1);
        rec.Result = false;
        vm.BeginCapture();
        await vm.CaptureAsync(0x00, 0x3a); // F1
        Assert.Equal("1", vm.BindingText);            // still factory
        Assert.False(vm.CanUndo);
        Assert.Contains("Not applied", vm.Status);
    }

    [Fact]
    public async Task Undo_rewrites_previous_binding_and_expires()
    {
        var (vm, rec, undo) = NewVm(2);
        vm.SetApplied(ButtonActionKind.Key, 0x00, 0x3a);       // seeded F1
        vm.BeginCapture();
        await vm.CaptureAsync(0x01, 0x06);                     // now Ctrl+C
        Assert.True(vm.CanUndo);
        await vm.UndoAsync();                                  // back to F1
        Assert.Equal("F1", vm.BindingText);
        Assert.False(vm.CanUndo);                              // undo is one-shot
        Assert.Equal(ButtonActionKind.Key, rec.Writes[^1].Kind);
        Assert.Equal((byte)0x3a, rec.Writes[^1].Usage);
    }

    [Fact]
    public async Task Undo_window_expiry_clears_CanUndo()
    {
        var rec = new Recorder();
        var tcs = new TaskCompletionSource();
        var vm = new CalloutViewModel(1, rec.Write, () => tcs.Task);
        vm.BeginCapture();
        await vm.CaptureAsync(0x00, 0x04); // A
        Assert.True(vm.CanUndo);
        tcs.SetResult();                   // the 5 s window elapses
        await Task.Yield();
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public async Task Undo_of_a_previously_default_button_restores_factory()
    {
        var (vm, rec, _) = NewVm(4);       // untouched → applied state is Default
        vm.BeginCapture();
        await vm.CaptureAsync(0x00, 0x04); // A
        await vm.UndoAsync();
        Assert.Equal(ButtonActionKind.Default, rec.Writes[^1].Kind); // AppHost maps Default → factory write + table remove
        Assert.Equal("4", vm.BindingText);
    }

    [Fact]
    public async Task Disable_and_default_write_through()
    {
        var (vm, rec, _) = NewVm(5);
        await vm.DisableAsync();
        Assert.Equal("Disabled", vm.BindingText);
        await vm.DefaultAsync();
        Assert.Equal("5", vm.BindingText);
        Assert.Equal(ButtonActionKind.Disabled, rec.Writes[0].Kind);
        Assert.Equal(ButtonActionKind.Default, rec.Writes[1].Kind);
    }

    [Fact]
    public void CancelCapture_returns_to_idle()
    {
        var (vm, rec, _) = NewVm();
        vm.BeginCapture();
        vm.CancelCapture();
        Assert.False(vm.IsCapturing);
        Assert.Empty(rec.Writes);
    }
}
```

- [ ] **Step 2: Run — FAIL** (type not found).

- [ ] **Step 3: Implement `CalloutViewModel.cs`:**

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Ui.Dashboard;

/// <summary>One thumb-grid button chip: instant-apply state machine
/// (Idle → Capturing → Writing → Confirmed | Failed) with a one-shot undo window after each
/// verified write. The write delegate is AppHost's slot pipeline; Kind=Default means
/// "write the factory action and drop the table entry".</summary>
public sealed class CalloutViewModel : INotifyPropertyChanged
{
    public delegate Task<bool> WriteBinding(int position, ButtonActionKind kind, byte modifiers, byte usage);

    private readonly WriteBinding _write;
    private readonly Func<Task> _undoWindow; // one-shot delay; injectable for tests (default 5 s)

    private ButtonActionKind _kind = ButtonActionKind.Default;
    private byte _mods, _usage;
    private (ButtonActionKind Kind, byte Mods, byte Usage) _prev;
    private int _undoVersion;
    private bool _isCapturing, _isBusy, _canUndo, _isHighlighted;
    private string _status = "";

    public CalloutViewModel(int position, WriteBinding write, Func<Task>? undoWindow = null)
    {
        Position = position;
        _write = write;
        _undoWindow = undoWindow ?? (() => Task.Delay(TimeSpan.FromSeconds(5)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Position { get; }
    public string Label => Position.ToString();

    public string BindingText => _kind switch
    {
        ButtonActionKind.Disabled => "Disabled",
        ButtonActionKind.Key => KeyToHidUsage.Describe(_mods, _usage),
        _ => KeyToHidUsage.Describe(0, NagaV2ProButtons.FactoryBindingForPosition(Position).HidUsage),
    };

    public bool IsCapturing { get => _isCapturing; private set { if (Set(ref _isCapturing, value)) Notify(nameof(BindingText)); } }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool CanUndo { get => _canUndo; private set => Set(ref _canUndo, value); }
    public bool IsHighlighted { get => _isHighlighted; set => Set(ref _isHighlighted, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>Seed from the persisted table (dashboard open).</summary>
    public void SetApplied(ButtonActionKind kind, byte modifiers, byte usage)
    {
        _kind = kind; _mods = modifiers; _usage = usage;
        Notify(nameof(BindingText));
    }

    public void BeginCapture() { Status = ""; IsCapturing = true; }
    public void CancelCapture() => IsCapturing = false;

    public Task CaptureAsync(byte modifiers, byte usage)
    {
        IsCapturing = false;
        return ApplyAsync(ButtonActionKind.Key, modifiers, usage, offerUndo: true);
    }

    public Task DisableAsync() => ApplyAsync(ButtonActionKind.Disabled, 0, 0, offerUndo: true);
    public Task DefaultAsync() => ApplyAsync(ButtonActionKind.Default, 0, 0, offerUndo: true);

    public Task UndoAsync()
    {
        if (!CanUndo) return Task.CompletedTask;
        CanUndo = false;
        var (k, m, u) = _prev;
        return ApplyAsync(k, m, u, offerUndo: false);
    }

    private async Task ApplyAsync(ButtonActionKind kind, byte modifiers, byte usage, bool offerUndo)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "";
        bool ok = await _write(Position, kind, modifiers, usage);
        IsBusy = false;
        if (!ok) { Status = "Not applied — wiggle the mouse and retry"; return; }

        _prev = (_kind, _mods, _usage);
        SetApplied(kind, modifiers, usage);
        Status = "Applied";
        if (offerUndo) _ = OpenUndoWindowAsync();
    }

    private async Task OpenUndoWindowAsync()
    {
        int version = ++_undoVersion;
        CanUndo = true;
        await _undoWindow();
        if (version == _undoVersion) CanUndo = false;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

  Note: delete the illustrative-bad test block from Step 1 if it was pasted — only the plain tests run.

- [ ] **Step 4: Run — PASS.**
- [ ] **Step 5: Commit** — `feat(dashboard): CalloutViewModel instant-apply state machine with undo`

---

### Task 5: `ProfileLiveness` pure comparer

**Files:**
- Create: `src/NagaBatteryTray/Ui/Dashboard/ProfileLiveness.cs`
- Test: Create `tests/NagaBatteryTray.Tests/ProfileLivenessTests.cs`

**Interfaces:**
- Produces: `enum ProfileLivenessState { NotAdopted, Unchecked, Unknown, Live, NotLive }`;
  `ProfileLiveness.Evaluate(int? slot, (byte Category, byte[] Data)? expected, RawButtonAction? effective) → ProfileLivenessState`.

- [ ] **Step 1: Failing tests:**

```csharp
using NagaBatteryTray.Hid;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public class ProfileLivenessTests
{
    private static readonly (byte, byte[]) CtrlC = ((byte)0x02, new byte[] { 0x01, 0x06 });

    [Fact] public void No_slot_is_NotAdopted() =>
        Assert.Equal(ProfileLivenessState.NotAdopted, ProfileLiveness.Evaluate(null, CtrlC, null));

    [Fact] public void No_expected_binding_is_Unchecked() =>
        Assert.Equal(ProfileLivenessState.Unchecked, ProfileLiveness.Evaluate(3, null, null));

    [Fact] public void Unreadable_effective_is_Unknown() =>
        Assert.Equal(ProfileLivenessState.Unknown, ProfileLiveness.Evaluate(3, CtrlC, null));

    [Fact] public void Matching_effective_is_Live() =>
        Assert.Equal(ProfileLivenessState.Live,
            ProfileLiveness.Evaluate(3, CtrlC, new RawButtonAction(0x02, new byte[] { 0x01, 0x06 })));

    [Fact] public void Mismatched_effective_is_NotLive() =>
        Assert.Equal(ProfileLivenessState.NotLive,
            ProfileLiveness.Evaluate(3, CtrlC, new RawButtonAction(0x02, new byte[] { 0x00, 0x1e })));
}
```

- [ ] **Step 2: Run — FAIL.**
- [ ] **Step 3: Implement:**

```csharp
using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Ui.Dashboard;

public enum ProfileLivenessState { NotAdopted, Unchecked, Unknown, Live, NotLive }

/// <summary>Pure logic behind the Profile card: is the mouse currently ON the app's slot? Compares
/// one remapped button's EFFECTIVE action (a profile-0 read — reads through to the active profile,
/// hardware-verified in the Phase B spike) against what the app slot should hold.</summary>
public static class ProfileLiveness
{
    public static ProfileLivenessState Evaluate(
        int? slot, (byte Category, byte[] Data)? expected, RawButtonAction? effective)
    {
        if (slot is null) return ProfileLivenessState.NotAdopted;
        if (expected is not { } e) return ProfileLivenessState.Unchecked;
        if (effective is not { } a) return ProfileLivenessState.Unknown;
        return a.Category == e.Category && a.Data.AsSpan().SequenceEqual(e.Data)
            ? ProfileLivenessState.Live
            : ProfileLivenessState.NotLive;
    }
}
```

- [ ] **Step 4: Run — PASS.**
- [ ] **Step 5: Commit** — `feat(dashboard): ProfileLiveness comparer`

---

### Task 6: `DashboardViewModel`

**Files:**
- Create: `src/NagaBatteryTray/Ui/Dashboard/DashboardViewModel.cs`
- Test: Create `tests/NagaBatteryTray.Tests/DashboardViewModelTests.cs`

**Interfaces:**
- Consumes: Task 4 `CalloutViewModel` (+`WriteBinding`), Task 5 `ProfileLivenessState`,
  `AppSettings` (incl. Task 1 fields), `DeviceState`, `DpiSetting`, `RazerProtocol.DpiMin/DpiMax`,
  `ThemeManager.PresetNames`.
- Produces (used by Tasks 8, 9):
  `DashboardViewModel(AppSettings source, bool runAtStartup, CalloutViewModel.WriteBinding write)`;
  `Callouts` (`IReadOnlyList<CalloutViewModel>`, 12), `Callout(int position)`;
  `ApplyState(DeviceState)` → `StatusDotBrushKey` (`string`: "Status.Positive"/"Status.Critical"),
  `HeaderSubtitle`, `BatteryChipText`, `DeviceOnline` (bool);
  `Dpi` (int, clamped), `DpiText`, `SetCurrentDpi(DpiSetting?)`, `DevicePresent`;
  `Presets` (`ObservableCollection<DpiPresetItem>` where `DpiPresetItem { int Value; string ColorHex;
  bool IsActive }`), `AddPreset(int)`, `RemovePreset(DpiPresetItem)`;
  `SetLiveness(ProfileLivenessState)` → `ProfileTitle`, `ProfileDetail`;
  settings edits `RunAtStartup, LowBatteryThreshold, LowBatteryNotify, PollSeconds,
  PollChargingSeconds, Theme` + `ApplyTo(AppSettings)` (ports the 15 s cadence floor + 1..100
  threshold clamps from the old `SettingsViewModel.ApplyTo`); `ThemeNames`.

- [ ] **Step 1: Failing tests:**

```csharp
using System.Windows.Threading;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public class DashboardViewModelTests
{
    private static Task<bool> NoWrite(int p, ButtonActionKind k, byte m, byte u) => Task.FromResult(true);

    private static AppSettings Seeded()
    {
        var s = new AppSettings { OnboardSlot = 3 };
        s.ButtonBindings[1] = new ButtonBindingSetting { Kind = ButtonActionKind.Key, Modifiers = 0x01, HidUsage = 0x06 };
        return s;
    }

    [Fact]
    public void Callouts_seed_from_the_table()
    {
        var vm = new DashboardViewModel(Seeded(), runAtStartup: false, NoWrite);
        Assert.Equal(12, vm.Callouts.Count);
        Assert.Equal("Ctrl+C", vm.Callout(1).BindingText);
        Assert.Equal("2", vm.Callout(2).BindingText); // untouched → factory digit
    }

    [Fact]
    public void Preset_checkmark_follows_current_dpi()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        vm.SetCurrentDpi(new DpiSetting(1600, 1600));
        Assert.True(vm.Presets.Single(p => p.Value == 1600).IsActive);
        Assert.False(vm.Presets.Single(p => p.Value == 800).IsActive);
        vm.Dpi = 800; // slider move re-evaluates
        Assert.True(vm.Presets.Single(p => p.Value == 800).IsActive);
    }

    [Fact]
    public void AddPreset_sorts_dedupes_and_RemovePreset_removes()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        vm.AddPreset(1200);
        vm.AddPreset(1200); // dupe ignored
        Assert.Equal(new[] { 800, 1200, 1600, 3200 }, vm.Presets.Select(p => p.Value));
        vm.RemovePreset(vm.Presets.Single(p => p.Value == 3200));
        Assert.Equal(new[] { 800, 1200, 1600 }, vm.Presets.Select(p => p.Value));
    }

    [Theory]
    [InlineData(ProfileLivenessState.NotAdopted, "No app profile yet")]
    [InlineData(ProfileLivenessState.Live, "bindings live")]
    [InlineData(ProfileLivenessState.NotLive, "another profile")]
    [InlineData(ProfileLivenessState.Unchecked, "Slot 3")]
    [InlineData(ProfileLivenessState.Unknown, "unknown")]
    public void Profile_card_text_tracks_state(ProfileLivenessState state, string fragment)
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        vm.SetLiveness(state);
        Assert.Contains(fragment, vm.ProfileTitle + " " + vm.ProfileDetail);
    }

    [Fact]
    public void ApplyTo_clamps_cadences_and_threshold()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite)
        { PollSeconds = 3, PollChargingSeconds = 1, LowBatteryThreshold = 0 };
        var target = new AppSettings();
        vm.ApplyTo(target);
        Assert.Equal(15, target.PollIntervalSeconds);
        Assert.Equal(15, target.PollIntervalChargingSeconds);
        Assert.Equal(1, target.LowBatteryThreshold);
    }

    [Fact]
    public void ApplyState_maps_online_and_offline()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        vm.ApplyState(DeviceState.Online(87, true, false));
        Assert.True(vm.DeviceOnline);
        Assert.Contains("87", vm.BatteryChipText);
        vm.ApplyState(DeviceState.Unknown);
        Assert.False(vm.DeviceOnline);
    }
}
```

- [ ] **Step 2: Run — FAIL.**
- [ ] **Step 3: Implement `DashboardViewModel.cs`:**

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Ui.Dashboard;

public sealed class DpiPresetItem : INotifyPropertyChanged
{
    private bool _isActive;
    public DpiPresetItem(int value, string colorHex) { Value = value; ColorHex = colorHex; }
    public int Value { get; }
    public string ColorHex { get; }
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value;
              PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DashboardViewModel : INotifyPropertyChanged
{
    // Fixed identification palette for preset dots, assigned by list index (theme-independent, §4.1).
    private static readonly string[] DotPalette =
        { "#E6A23C", "#5AA9FF", "#43D675", "#E5484D", "#B678FF", "#4DD0C7" };

    private readonly int? _slot;
    private int _dpi = RazerProtocol.DpiMin;
    private bool _devicePresent, _deviceOnline, _runAtStartup, _lowBatteryNotify;
    private int _lowBatteryThreshold, _pollSeconds, _pollChargingSeconds;
    private string _headerSubtitle = "", _batteryChipText = "—", _statusDotBrushKey = "Status.Critical";
    private string _profileTitle = "", _profileDetail = "", _theme = "Porcelain";

    public DashboardViewModel(AppSettings source, bool runAtStartup, CalloutViewModel.WriteBinding write)
    {
        _slot = source.OnboardSlot;
        _runAtStartup = runAtStartup;
        _lowBatteryThreshold = source.LowBatteryThreshold;
        _lowBatteryNotify = source.LowBatteryNotify;
        _pollSeconds = source.PollIntervalSeconds;
        _pollChargingSeconds = source.PollIntervalChargingSeconds;
        _theme = source.Theme;

        var callouts = new List<CalloutViewModel>(NagaV2ProButtons.Count);
        for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
        {
            var c = new CalloutViewModel(pos, write);
            if (source.ButtonBindings.TryGetValue(pos, out var b))
                c.SetApplied(b.Kind, b.Modifiers, b.HidUsage);
            callouts.Add(c);
        }
        Callouts = callouts;

        Presets = new ObservableCollection<DpiPresetItem>();
        foreach (int v in source.DpiPresets.Distinct().OrderBy(v => v)) Presets.Add(NewItem(v));
        RefreshDots();
        SetLiveness(_slot is null ? ProfileLivenessState.NotAdopted : ProfileLivenessState.Unchecked);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- callouts ----
    public IReadOnlyList<CalloutViewModel> Callouts { get; }
    public CalloutViewModel Callout(int position) => Callouts[position - 1];

    // ---- header ----
    public bool DeviceOnline { get => _deviceOnline; private set => Set(ref _deviceOnline, value); }
    public string StatusDotBrushKey { get => _statusDotBrushKey; private set => Set(ref _statusDotBrushKey, value); }
    public string HeaderSubtitle { get => _headerSubtitle; private set => Set(ref _headerSubtitle, value); }
    public string BatteryChipText { get => _batteryChipText; private set => Set(ref _batteryChipText, value); }

    public void ApplyState(DeviceState s)
    {
        DeviceOnline = s.Status == DeviceStatus.Online;
        StatusDotBrushKey = DeviceOnline ? "Status.Positive" : "Status.Critical";
        string link = !DeviceOnline ? "offline" : s.Wired ? "Wired" : "Wireless";
        string slot = _slot is { } n ? $" · Profile {n} · {SlotColour(n)}" : "";
        HeaderSubtitle = $"{link}{slot}";
        BatteryChipText = DeviceOnline ? $"{s.Percent}%{(s.Charging ? " ⚡" : "")}" : "—";
    }

    internal static string SlotColour(int slot) => slot switch
    { 1 => "white", 2 => "red", 3 => "green", 4 => "blue", 5 => "cyan", _ => $"#{slot}" };

    // ---- DPI ----
    public bool DevicePresent { get => _devicePresent; set => Set(ref _devicePresent, value); }
    public int Dpi
    {
        get => _dpi;
        set { if (Set(ref _dpi, Math.Clamp(value, RazerProtocol.DpiMin, RazerProtocol.DpiMax)))
              { Notify(nameof(DpiText)); RefreshActive(); } }
    }
    public string DpiText => DevicePresent ? Dpi.ToString() : "—";

    public void SetCurrentDpi(DpiSetting? dpi)
    {
        DevicePresent = dpi is not null;
        if (dpi is { } d) Dpi = d.X;
        Notify(nameof(DpiText));
        RefreshActive();
    }

    public ObservableCollection<DpiPresetItem> Presets { get; }

    public void AddPreset(int value)
    {
        value = Math.Clamp(value, RazerProtocol.DpiMin, RazerProtocol.DpiMax);
        if (Presets.Any(p => p.Value == value)) return;
        int at = 0;
        while (at < Presets.Count && Presets[at].Value < value) at++;
        Presets.Insert(at, NewItem(value));
        RefreshDots();
        RefreshActive();
    }

    public void RemovePreset(DpiPresetItem item)
    {
        Presets.Remove(item);
        RefreshDots();
    }

    private DpiPresetItem NewItem(int v) => new(v, DotPalette[0]); // dot fixed in RefreshDots
    private readonly Dictionary<DpiPresetItem, string> _dots = new();
    private void RefreshDots()
    {
        // ColorHex is by index — rebuild wrappers cheaply by replacing items whose color changed
        for (int i = 0; i < Presets.Count; i++)
        {
            string want = DotPalette[i % DotPalette.Length];
            if (Presets[i].ColorHex != want)
                Presets[i] = new DpiPresetItem(Presets[i].Value, want) { IsActive = Presets[i].IsActive };
        }
    }
    private void RefreshActive()
    { foreach (var p in Presets) p.IsActive = DevicePresent && p.Value == Dpi; }

    // ---- profile card ----
    public string ProfileTitle { get => _profileTitle; private set => Set(ref _profileTitle, value); }
    public string ProfileDetail { get => _profileDetail; private set => Set(ref _profileDetail, value); }

    public void SetLiveness(ProfileLivenessState state)
    {
        string identity = _slot is { } n ? $"Slot {n} · {SlotColour(n)}" : "";
        (ProfileTitle, ProfileDetail) = state switch
        {
            ProfileLivenessState.NotAdopted => ("No app profile yet", "Remap any button to create one."),
            ProfileLivenessState.Live => (identity, "● bindings live"),
            ProfileLivenessState.NotLive => (identity, "○ Mouse is on another profile — press the bottom button until the LED is " + (_slot is { } m ? SlotColour(m) : "right") + "."),
            ProfileLivenessState.Unknown => (identity, "state unknown — mouse unreachable"),
            _ => (identity, ""), // Unchecked: identity only, no liveness claim
        };
    }

    // ---- settings (overlay) ----
    public bool RunAtStartup { get => _runAtStartup; set => Set(ref _runAtStartup, value); }
    public bool LowBatteryNotify { get => _lowBatteryNotify; set => Set(ref _lowBatteryNotify, value); }
    public int LowBatteryThreshold { get => _lowBatteryThreshold; set => Set(ref _lowBatteryThreshold, value); }
    public int PollSeconds { get => _pollSeconds; set => Set(ref _pollSeconds, value); }
    public int PollChargingSeconds { get => _pollChargingSeconds; set => Set(ref _pollChargingSeconds, value); }
    public string Theme { get => _theme; set => Set(ref _theme, value); }
    public IReadOnlyList<string> ThemeNames => Ui.ThemeManager.PresetNames;

    /// <summary>Ports the old SettingsViewModel.ApplyTo clamps: cadence floor 15 s, threshold 1..100.</summary>
    public void ApplyTo(AppSettings target)
    {
        target.LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 1, 100);
        target.LowBatteryNotify = LowBatteryNotify;
        target.PollIntervalSeconds = Math.Max(15, PollSeconds);
        target.PollIntervalChargingSeconds = Math.Max(15, PollChargingSeconds);
        target.Theme = Ui.ThemeManager.Resolve(Theme);
        target.DpiPresets = Presets.Select(p => p.Value).ToList();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
    private void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

  Note `Presets[i] = new …` requires `ObservableCollection` index setter — it has one (`IList`), and
  it raises Replace notifications; `_dots` field is unused — delete it (left here to catch in review).

- [ ] **Step 4: Run — PASS** (delete the unused `_dots` dictionary; keep the suite green).
- [ ] **Step 5: Commit** — `feat(dashboard): DashboardViewModel (header/DPI presets/profile/settings)`

---

### Task 7: AppHost — single-binding write path, liveness check, reset-all

**Files:**
- Modify: `src/NagaBatteryTray/AppHost.cs`

**Interfaces:**
- Consumes: existing `EnsureOnboardSlotAsync()`, `_monitor`, `_settings`; Task 5 `ProfileLiveness`.
- Produces (used by Tasks 8/9):
  `Task<bool> WriteBindingAsync(int position, ButtonActionKind kind, byte modifiers, byte usage)`;
  `Task<ProfileLivenessState> CheckLivenessAsync()`; `Task ResetAllButtonsAsync(DashboardViewModel vm)`.
  (No unit tests — AppHost is the untested boundary per project conventions; all logic it calls is
  covered in Tasks 4-6 and the shipped Phase B pipeline.)

- [ ] **Step 1: Add the three methods** (place after `EnsureOnboardSlotAsync`; add
  `using NagaBatteryTray.Ui.Dashboard;`):

```csharp
    /// <summary>The dashboard's instant-apply write: one binding into the app-owned slot
    /// (ensure slot → write → read-back verify → persist). Kind=Default writes the factory action
    /// and drops the table entry. Returns false on any failure (nothing persisted).</summary>
    private async Task<bool> WriteBindingAsync(int position, ButtonActionKind kind, byte modifiers, byte usage)
    {
        if (await EnsureOnboardSlotAsync() is not { } slot) return false;

        var binding = kind == ButtonActionKind.Default
            ? NagaV2ProButtons.FactoryBindingForPosition(position)
            : new ButtonBinding(NagaV2ProButtons.IdForPosition(position), kind, modifiers, usage);
        var (category, data) = binding.ToWire();

        bool ok = await Task.Run(() => _monitor.SetButtonAsync(slot, binding.ButtonId, category, data));
        if (ok)
        {
            var readBack = await Task.Run(() => _monitor.GetButtonAsync(slot, binding.ButtonId));
            ok = readBack is { } r && r.Category == category && r.Data.AsSpan().SequenceEqual(data);
        }
        if (!ok) return false;

        if (kind == ButtonActionKind.Default) _settings.Settings.ButtonBindings.Remove(position);
        else _settings.Settings.ButtonBindings[position] = new ButtonBindingSetting
             { Kind = kind, Modifiers = modifiers, HidUsage = usage };
        _settings.Save();
        return true;
    }

    /// <summary>Profile-card liveness (spec §4.4): one effective read (profile 0) compared against the
    /// app slot's stored binding. Only called on dashboard open / explicit refresh — never polled.</summary>
    private async Task<ProfileLivenessState> CheckLivenessAsync()
    {
        var s = _settings.Settings;
        if (s.OnboardSlot is null) return ProfileLivenessState.NotAdopted;
        var probe = s.ButtonBindings.OrderBy(kv => kv.Key).FirstOrDefault();
        if (probe.Value is null) return ProfileLivenessState.Unchecked;

        var expected = new ButtonBinding(NagaV2ProButtons.IdForPosition(probe.Key),
            probe.Value.Kind, probe.Value.Modifiers, probe.Value.HidUsage).ToWire();
        var effective = await Task.Run(() =>
            _monitor.GetButtonAsync(RazerProtocol.ButtonProfileDirect, NagaV2ProButtons.IdForPosition(probe.Key)));
        return ProfileLiveness.Evaluate(s.OnboardSlot, expected, effective);
    }

    /// <summary>Settings-overlay "Reset all to factory": Default-write every grid button via the
    /// per-chip pipeline so each chip shows its own verified result.</summary>
    private async Task ResetAllButtonsAsync(DashboardViewModel vm)
    {
        for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
            await vm.Callout(pos).DefaultAsync();
    }
```

- [ ] **Step 2: Build — PASS** (methods unused yet → expect CS unused warnings only if any; fine).
- [ ] **Step 3: Run the full suite — PASS.**
- [ ] **Step 4: Commit** — `feat(remap): single-binding write path + liveness check + reset-all (AppHost)`

---

### Task 8: Dashboard window — shell + mouse stage

**Files:**
- Create: `src/NagaBatteryTray/Ui/Dashboard/DashboardWindow.xaml(.cs)`,
  `src/NagaBatteryTray/Ui/Dashboard/MouseStageView.xaml(.cs)`
- (Not yet wired into AppHost — Task 9 swaps it in. Build stays green throughout.)

**Interfaces:**
- Consumes: `DashboardViewModel` (Task 6) as `DataContext`; `KeyToHidUsage.TryGetUsage/ToModifierBits`;
  design-system styles/keys (Task 3).
- Produces: `DashboardWindow(DashboardViewModel vm)`; events `event Action? SettingsOverlayRequested;`
  `event Action<int>? ApplyDpiRequested;` `event Action? LivenessRefreshRequested;`;
  `void ShowSettingsOverlay(SettingsView view)` host slot (Task 9 fills it).

- [ ] **Step 1: `MouseStageView.xaml`** — the stage: chips left/right, silhouette + 12 keys center,
  static callout lines, right rail (DPI + profile). Key structure (complete file):

```xml
<UserControl x:Class="NagaBatteryTray.Ui.Dashboard.MouseStageView"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
  <UserControl.Resources>
    <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    <DataTemplate x:Key="ChipTemplate">
      <Border Style="{DynamicResource ChipBorder}" Margin="0,3" MinWidth="150"
              MouseEnter="OnChipEnter" MouseLeave="OnChipLeave" Cursor="Hand"
              Focusable="True" KeyboardNavigation.IsTabStop="True"
              AutomationProperties.Name="{Binding Label, StringFormat=Button {0}}"
              MouseLeftButtonUp="OnChipClick" KeyUp="OnChipKeyUp">
        <Border.Style>
          <Style TargetType="Border" BasedOn="{StaticResource ChipBorder}">
            <Style.Triggers>
              <DataTrigger Binding="{Binding IsHighlighted}" Value="True">
                <Setter Property="BorderBrush" Value="{DynamicResource App.Accent}"/>
              </DataTrigger>
              <DataTrigger Binding="{Binding IsCapturing}" Value="True">
                <Setter Property="BorderBrush" Value="{DynamicResource App.Accent}"/>
                <Setter Property="Background" Value="{DynamicResource App.AccentSoft}"/>
              </DataTrigger>
            </Style.Triggers>
          </Style>
        </Border.Style>
        <StackPanel>
          <DockPanel>
            <TextBlock Style="{DynamicResource LabelText}" Text="{Binding Label}"/>
            <TextBlock DockPanel.Dock="Right" HorizontalAlignment="Right" FontSize="11"
                       Foreground="{DynamicResource Status.Positive}" Text="✓"
                       Visibility="{Binding CanUndo, Converter={StaticResource BoolToVis}}"/>
          </DockPanel>
          <TextBlock Style="{DynamicResource BodyText}" TextTrimming="CharacterEllipsis"
                     ToolTip="{Binding BindingText}">
            <TextBlock.Style>
              <Style TargetType="TextBlock" BasedOn="{StaticResource BodyText}">
                <Setter Property="Text" Value="{Binding BindingText}"/>
                <Style.Triggers>
                  <DataTrigger Binding="{Binding IsCapturing}" Value="True">
                    <Setter Property="Text" Value="press a key…"/>
                  </DataTrigger>
                </Style.Triggers>
              </Style>
            </TextBlock.Style>
          </TextBlock>
          <TextBlock Style="{DynamicResource SubtleText}" Text="{Binding Status}" FontSize="10"/>
          <StackPanel Orientation="Horizontal" Margin="0,3,0,0">
            <Button Content="Undo" Click="OnUndo" FontSize="10" Padding="6,1"
                    Visibility="{Binding CanUndo, Converter={StaticResource BoolToVis}}"/>
            <Button Content="Disable" Click="OnDisable" FontSize="10" Padding="6,1" Margin="4,0,0,0"/>
            <Button Content="Default" Click="OnDefault" FontSize="10" Padding="6,1" Margin="4,0,0,0"/>
          </StackPanel>
        </StackPanel>
      </Border>
    </DataTemplate>
  </UserControl.Resources>

  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="180"/>
      <ColumnDefinition Width="*"/>
      <ColumnDefinition Width="180"/>
      <ColumnDefinition Width="230"/>
    </Grid.ColumnDefinitions>

    <ItemsControl Grid.Column="0" x:Name="LeftChips" VerticalAlignment="Center"
                  ItemTemplate="{StaticResource ChipTemplate}"/>

    <!-- stage: silhouette + grid keys -->
    <Viewbox Grid.Column="1" Stretch="Uniform" MaxHeight="440">
      <Canvas Width="240" Height="360">
        <!-- side-view body: simple bezier silhouette, thumb side facing viewer -->
        <Path Canvas.Left="30" Canvas.Top="10" StrokeThickness="1.5"
              Stroke="{DynamicResource App.CardStroke}">
          <Path.Fill>
            <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
              <GradientStop Color="#2A2C31" Offset="0"/><GradientStop Color="#131418" Offset="0.7"/>
            </LinearGradientBrush>
          </Path.Fill>
          <Path.Data>
            M 60,0 C 130,-6 180,40 180,150 C 180,260 150,340 90,340 C 30,340 0,280 0,190 C 0,80 10,6 60,0 Z
          </Path.Data>
        </Path>
        <Ellipse Canvas.Left="20" Canvas.Top="40" Width="200" Height="290"
                 Fill="{DynamicResource App.GlowSoft}" IsHitTestVisible="False"/>
        <!-- 12-key thumb grid, 3 cols × 4 rows -->
        <ItemsControl x:Name="GridKeys" Canvas.Left="52" Canvas.Top="90">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate><UniformGrid Columns="3"/></ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
          <ItemsControl.ItemTemplate>
            <DataTemplate>
              <Border Width="26" Height="26" Margin="3" CornerRadius="5"
                      MouseEnter="OnKeyEnter" MouseLeave="OnKeyLeave" MouseLeftButtonUp="OnChipClick">
                <Border.Style>
                  <Style TargetType="Border">
                    <Setter Property="Background" Value="{DynamicResource App.ChipFill}"/>
                    <Setter Property="BorderBrush" Value="{DynamicResource App.ChipStroke}"/>
                    <Setter Property="BorderThickness" Value="1"/>
                    <Style.Triggers>
                      <DataTrigger Binding="{Binding IsHighlighted}" Value="True">
                        <Setter Property="BorderBrush" Value="{DynamicResource App.Accent}"/>
                        <Setter Property="Background" Value="{DynamicResource App.AccentSoft}"/>
                      </DataTrigger>
                    </Style.Triggers>
                  </Style>
                </Border.Style>
                <TextBlock Text="{Binding Label}" FontSize="10" HorizontalAlignment="Center"
                           VerticalAlignment="Center" Foreground="{DynamicResource App.TextSecondary}"/>
              </Border>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </Canvas>
    </Viewbox>

    <ItemsControl Grid.Column="2" x:Name="RightChips" VerticalAlignment="Center"
                  ItemTemplate="{StaticResource ChipTemplate}"/>

    <!-- right rail -->
    <StackPanel Grid.Column="3" VerticalAlignment="Top" Margin="8,0,0,0">
      <Border Style="{DynamicResource CardBorder}" Margin="0,0,0,10">
        <StackPanel>
          <TextBlock Style="{DynamicResource LabelText}" Text="DPI"/>
          <TextBlock Style="{DynamicResource NumeralText}" Text="{Binding DpiText}"/>
          <Slider Minimum="100" Maximum="30000" TickFrequency="100" IsSnapToTickEnabled="True"
                  Value="{Binding Dpi, Mode=TwoWay}" IsEnabled="{Binding DevicePresent}"
                  Thumb.DragCompleted="OnDpiDragCompleted" KeyUp="OnDpiSliderKeyUp"/>
          <ItemsControl ItemsSource="{Binding Presets}" Margin="0,8,0,0">
            <ItemsControl.ItemTemplate>
              <DataTemplate>
                <DockPanel Margin="0,2">
                  <Button DockPanel.Dock="Right" Content="✕" FontSize="9" Padding="4,0"
                          Click="OnRemovePreset" Opacity="0.5"/>
                  <Button Click="OnApplyPreset" Background="Transparent" BorderThickness="0"
                          HorizontalAlignment="Stretch" HorizontalContentAlignment="Left"
                          IsEnabled="{Binding DataContext.DevicePresent, RelativeSource={RelativeSource AncestorType=UserControl}}">
                    <StackPanel Orientation="Horizontal">
                      <Ellipse Width="7" Height="7" VerticalAlignment="Center">
                        <Ellipse.Fill>
                          <SolidColorBrush Color="{Binding ColorHex}"/>
                        </Ellipse.Fill>
                      </Ellipse>
                      <TextBlock Style="{DynamicResource BodyText}" Text="{Binding Value}" Margin="7,0,0,0"/>
                      <TextBlock Style="{DynamicResource BodyText}" Text=" ✓" Foreground="{DynamicResource App.Accent}"
                                 Visibility="{Binding IsActive, Converter={StaticResource BoolToVis}}"/>
                    </StackPanel>
                  </Button>
                </DockPanel>
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
          <DockPanel Margin="0,6,0,0">
            <Button DockPanel.Dock="Right" Content="+" Click="OnAddPreset" Padding="8,2" Margin="6,0,0,0"/>
            <ui:NumberBox x:Name="NewPresetBox" Minimum="100" Maximum="30000" SmallChange="50"
                          MaxDecimalPlaces="0" PlaceholderText="add preset"/>
          </DockPanel>
        </StackPanel>
      </Border>
      <Border Style="{DynamicResource CardBorder}">
        <StackPanel>
          <DockPanel>
            <TextBlock Style="{DynamicResource LabelText}" Text="PROFILE"/>
            <Button DockPanel.Dock="Right" Content="↻" FontSize="10" Padding="4,0"
                    Click="OnRefreshLiveness" Opacity="0.6"/>
          </DockPanel>
          <TextBlock Style="{DynamicResource BodyText}" Text="{Binding ProfileTitle}" FontWeight="SemiBold"/>
          <TextBlock Style="{DynamicResource SubtleText}" Text="{Binding ProfileDetail}" TextWrapping="Wrap"/>
        </StackPanel>
      </Border>
    </StackPanel>
  </Grid>
</UserControl>
```

  Note: `SolidColorBrush Color="{Binding ColorHex}"` binds a string to Color — that fails; use a
  small `StringToBrushConverter` instead (code-behind step adds it) — the converter is the one
  hardcoded-color exception (it materializes preset dot colors, which are data, not theme).

- [ ] **Step 2: `MouseStageView.xaml.cs`:**

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace NagaBatteryTray.Ui.Dashboard;

public sealed class StringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString((string)value));
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public partial class MouseStageView : UserControl
{
    public event Action<CalloutViewModel>? CaptureRequested; // window owns the keyboard hook
    public event Action<int>? ApplyDpiRequested;
    public event Action? LivenessRefreshRequested;

    public MouseStageView() => InitializeComponent();

    /// <summary>Split the 12 callouts 6 left / 6 right and seed the grid keys.</summary>
    public void Bind(DashboardViewModel vm)
    {
        DataContext = vm;
        LeftChips.ItemsSource = vm.Callouts.Take(6).ToList();
        RightChips.ItemsSource = vm.Callouts.Skip(6).ToList();
        GridKeys.ItemsSource = vm.Callouts;
    }

    private static CalloutViewModel Vm(object sender) =>
        (CalloutViewModel)((FrameworkElement)sender).DataContext;

    private void OnChipEnter(object s, RoutedEventArgs e) => Vm(s).IsHighlighted = true;
    private void OnChipLeave(object s, RoutedEventArgs e) => Vm(s).IsHighlighted = false;
    private void OnKeyEnter(object s, RoutedEventArgs e) => Vm(s).IsHighlighted = true;
    private void OnKeyLeave(object s, RoutedEventArgs e) => Vm(s).IsHighlighted = false;

    private void OnChipClick(object s, RoutedEventArgs e) => CaptureRequested?.Invoke(Vm(s));
    private void OnChipKeyUp(object s, System.Windows.Input.KeyEventArgs e)
    { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
        CaptureRequested?.Invoke(Vm(s)); }

    private void OnUndo(object s, RoutedEventArgs e) => _ = Vm(s).UndoAsync();
    private void OnDisable(object s, RoutedEventArgs e) => _ = Vm(s).DisableAsync();
    private void OnDefault(object s, RoutedEventArgs e) => _ = Vm(s).DefaultAsync();

    private void OnDpiDragCompleted(object s, RoutedEventArgs e) =>
        ApplyDpiRequested?.Invoke(((DashboardViewModel)DataContext).Dpi);
    private void OnDpiSliderKeyUp(object s, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == System.Windows.Input.Key.Enter)
        ApplyDpiRequested?.Invoke(((DashboardViewModel)DataContext).Dpi); }

    private void OnApplyPreset(object s, RoutedEventArgs e)
    {
        var item = (DpiPresetItem)((FrameworkElement)s).DataContext;
        ((DashboardViewModel)DataContext).Dpi = item.Value;
        ApplyDpiRequested?.Invoke(item.Value);
    }
    private void OnRemovePreset(object s, RoutedEventArgs e) =>
        ((DashboardViewModel)DataContext).RemovePreset((DpiPresetItem)((FrameworkElement)s).DataContext);
    private void OnAddPreset(object s, RoutedEventArgs e)
    { if (NewPresetBox.Value is { } v) ((DashboardViewModel)DataContext).AddPreset((int)v); }

    private void OnRefreshLiveness(object s, RoutedEventArgs e) => LivenessRefreshRequested?.Invoke();
}
```

  Register the converter in `MouseStageView.xaml` resources
  (`<local:StringToBrushConverter x:Key="HexBrush"/>` + `xmlns:local` and use
  `Fill="{Binding ColorHex, Converter={StaticResource HexBrush}}"` on the Ellipse — replace the
  broken direct binding from Step 1).

- [ ] **Step 3: `DashboardWindow.xaml`** (shell):

```xml
<ui:FluentWindow x:Class="NagaBatteryTray.Ui.Dashboard.DashboardWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
    xmlns:local="clr-namespace:NagaBatteryTray.Ui.Dashboard"
    Title="Razer Naga Companion"
    Width="1010" Height="680" MinWidth="980" MinHeight="640"
    WindowStartupLocation="CenterScreen"
    ExtendsContentIntoTitleBar="True" WindowBackdropType="None" WindowCornerPreference="Round">
  <Grid Background="{DynamicResource App.CanvasBrush}">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/>
    </Grid.RowDefinitions>
    <ui:TitleBar Grid.Row="0" Title="Razer Naga Companion"/>
    <!-- header card -->
    <Border Grid.Row="1" Style="{DynamicResource CardBorder}" Margin="16,4,16,10" Padding="12,10">
      <DockPanel>
        <StackPanel Orientation="Horizontal">
          <Ellipse Width="9" Height="9" VerticalAlignment="Center" x:Name="StatusDot"/>
          <StackPanel Margin="10,0,0,0">
            <TextBlock Style="{DynamicResource BodyText}" FontSize="14" FontWeight="SemiBold" Text="Naga V2 Pro"/>
            <TextBlock Style="{DynamicResource SubtleText}" Text="{Binding HeaderSubtitle}"/>
          </StackPanel>
        </StackPanel>
        <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" HorizontalAlignment="Right">
          <Border Style="{DynamicResource ChipBorder}" VerticalAlignment="Center" Padding="10,3">
            <TextBlock Style="{DynamicResource BodyText}" FontWeight="SemiBold" Text="{Binding BatteryChipText}"/>
          </Border>
          <ui:Button Icon="{ui:SymbolIcon Settings24}" Click="OnGear" Margin="8,0,0,0" Padding="8,6"/>
        </StackPanel>
      </DockPanel>
    </Border>
    <!-- stage -->
    <local:MouseStageView Grid.Row="2" x:Name="Stage" Margin="16,0,16,16"/>
    <!-- settings overlay host (Task 9 fills) -->
    <Grid Grid.Row="1" Grid.RowSpan="2" x:Name="OverlayHost" Visibility="Collapsed"
          Background="#A0000000"/>
  </Grid>
</ui:FluentWindow>
```

- [ ] **Step 4: `DashboardWindow.xaml.cs`** (capture hook ported from the old SettingsWindow):

```csharp
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace NagaBatteryTray.Ui.Dashboard;

public partial class DashboardWindow : FluentWindow
{
    private readonly DashboardViewModel _vm;
    private CalloutViewModel? _capturing;

    public event Action? SettingsOverlayRequested;
    public event Action<int>? ApplyDpiRequested;
    public event Action? LivenessRefreshRequested;

    public DashboardWindow(DashboardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Stage.Bind(vm);
        Stage.CaptureRequested += BeginCapture;
        Stage.ApplyDpiRequested += dpi => ApplyDpiRequested?.Invoke(dpi);
        Stage.LivenessRefreshRequested += () => LivenessRefreshRequested?.Invoke();
        vm.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(vm.StatusDotBrushKey)) UpdateDot(); };
        UpdateDot();
        PreviewMouseDown += (_, _) => { if (_capturing is { } c && !c.IsCapturing) _capturing = null; };
    }

    private void UpdateDot() =>
        StatusDot.Fill = (Brush)FindResource(_vm.StatusDotBrushKey);

    public void ShowOverlay(UIElement content)
    {
        OverlayHost.Children.Clear();
        OverlayHost.Children.Add(content);
        OverlayHost.Visibility = Visibility.Visible;
    }
    public void HideOverlay() => OverlayHost.Visibility = Visibility.Collapsed;

    private void OnGear(object s, RoutedEventArgs e) => SettingsOverlayRequested?.Invoke();

    private void BeginCapture(CalloutViewModel target)
    {
        if (_capturing is { } prev) prev.CancelCapture();
        _capturing = target;
        target.BeginCapture();
        Focus(); // pull focus off the clicked chip so the next key lands on the window
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturing is not { } chip || !chip.IsCapturing) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt-chords arrive as Key.System
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return; // bare modifier — keep capturing
        _capturing = null;
        if (key == Key.Escape) { chip.CancelCapture(); return; }
        if (!KeyToHidUsage.TryGetUsage(key, out byte usage))
        { chip.CancelCapture(); chip.Status = $"{key} can't be bound"; return; }
        _ = chip.CaptureAsync(KeyToHidUsage.ToModifierBits(Keyboard.Modifiers), usage);
    }
}
```

- [ ] **Step 5: Build + full suite — PASS** (window compiles; not yet reachable at runtime).
- [ ] **Step 6: Commit** — `feat(dashboard): shell window + mouse stage (not yet wired)`

---

### Task 9: SettingsView overlay, AppHost wiring swap, delete the old Settings UI

**Files:**
- Create: `src/NagaBatteryTray/Ui/Dashboard/SettingsView.xaml(.cs)`
- Modify: `src/NagaBatteryTray/AppHost.cs` (OpenSettings → OpenDashboard; delete `ApplyButtonsAsync`;
  keep `ApplyDpiAsync`/`LoadDpiAsync` reworked for the VM)
- Delete: `src/NagaBatteryTray/Ui/SettingsWindow.xaml`, `SettingsWindow.xaml.cs`,
  `SettingsViewModel.cs`, `ButtonRowViewModel.cs`;
  `tests/NagaBatteryTray.Tests/SettingsViewModelTests.cs`, `ButtonRowViewModelTests.cs`

**Interfaces:**
- Consumes: everything above. Produces: the app runs the new dashboard end-to-end.

- [ ] **Step 1: `SettingsView.xaml`** (overlay content: a right-docked panel):

```xml
<UserControl x:Class="NagaBatteryTray.Ui.Dashboard.SettingsView"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
    HorizontalAlignment="Right" Width="340">
  <Border Style="{DynamicResource CardBorder}" Margin="0,8,12,12" Background="{DynamicResource App.CanvasBrush}">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
      <StackPanel Margin="4">
        <DockPanel Margin="0,0,0,10">
          <TextBlock Style="{DynamicResource BodyText}" FontSize="15" FontWeight="SemiBold" Text="Settings"/>
          <ui:Button DockPanel.Dock="Right" HorizontalAlignment="Right" Content="✕" Click="OnClose" Padding="8,3"/>
        </DockPanel>

        <TextBlock Style="{DynamicResource LabelText}" Text="THEME"/>
        <ListBox x:Name="ThemeList" ItemsSource="{Binding ThemeNames}" SelectedItem="{Binding Theme, Mode=TwoWay}"
                 SelectionChanged="OnThemeChanged" Margin="0,4,0,12" BorderThickness="0" Background="Transparent"/>

        <TextBlock Style="{DynamicResource LabelText}" Text="GENERAL"/>
        <DockPanel Margin="0,6">
          <TextBlock Style="{DynamicResource BodyText}" Text="Run at startup" VerticalAlignment="Center"/>
          <ui:ToggleSwitch DockPanel.Dock="Right" HorizontalAlignment="Right"
                           IsChecked="{Binding RunAtStartup, Mode=TwoWay}"
                           Checked="OnStartupToggled" Unchecked="OnStartupToggled"/>
        </DockPanel>
        <DockPanel Margin="0,6">
          <TextBlock Style="{DynamicResource BodyText}" Text="Low-battery alert at (%)" VerticalAlignment="Center"/>
          <ui:NumberBox DockPanel.Dock="Right" HorizontalAlignment="Right" MinWidth="110"
                        Value="{Binding LowBatteryThreshold, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                        Minimum="1" Maximum="100" SmallChange="5" MaxDecimalPlaces="0"/>
        </DockPanel>
        <DockPanel Margin="0,6">
          <TextBlock Style="{DynamicResource BodyText}" Text="Notify on low battery" VerticalAlignment="Center"/>
          <ui:ToggleSwitch DockPanel.Dock="Right" HorizontalAlignment="Right"
                           IsChecked="{Binding LowBatteryNotify, Mode=TwoWay}"/>
        </DockPanel>

        <TextBlock Style="{DynamicResource LabelText}" Text="BATTERY POLLING (SECONDS)" Margin="0,8,0,0"/>
        <DockPanel Margin="0,6">
          <TextBlock Style="{DynamicResource BodyText}" Text="On battery" VerticalAlignment="Center"/>
          <ui:NumberBox DockPanel.Dock="Right" HorizontalAlignment="Right" MinWidth="110"
                        Value="{Binding PollSeconds, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                        Minimum="15" Maximum="3600" SmallChange="15" MaxDecimalPlaces="0"/>
        </DockPanel>
        <DockPanel Margin="0,6">
          <TextBlock Style="{DynamicResource BodyText}" Text="While charging" VerticalAlignment="Center"/>
          <ui:NumberBox DockPanel.Dock="Right" HorizontalAlignment="Right" MinWidth="110"
                        Value="{Binding PollChargingSeconds, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                        Minimum="15" Maximum="3600" SmallChange="15" MaxDecimalPlaces="0"/>
        </DockPanel>

        <TextBlock Style="{DynamicResource LabelText}" Text="BUTTONS" Margin="0,8,0,0"/>
        <ui:Button Content="Reset all to factory" Click="OnResetAll" Margin="0,6,0,0"/>
        <TextBlock x:Name="ResetNote" Style="{DynamicResource SubtleText}" Text="" TextWrapping="Wrap"/>

        <TextBlock Style="{DynamicResource SubtleText}" Margin="0,14,0,0"
                   Text="Razer Naga Companion — github.com/Bmwascher/razer-naga-companion"/>
      </StackPanel>
    </ScrollViewer>
  </Border>
</UserControl>
```

- [ ] **Step 2: `SettingsView.xaml.cs`:**

```csharp
using System.Windows;
using System.Windows.Controls;

namespace NagaBatteryTray.Ui.Dashboard;

public partial class SettingsView : UserControl
{
    public event Action? CloseRequested;
    public event Action<bool>? StartupToggled;
    public event Action<string>? ThemeChanged;
    public event Action? ResetAllRequested;

    public SettingsView() => InitializeComponent();

    public void SetResetNote(string text) => ResetNote.Text = text;

    private void OnClose(object s, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void OnStartupToggled(object s, RoutedEventArgs e) =>
        StartupToggled?.Invoke(((DashboardViewModel)DataContext).RunAtStartup);
    private void OnThemeChanged(object s, SelectionChangedEventArgs e)
    { if (ThemeList.SelectedItem is string name) ThemeChanged?.Invoke(name); }
    private void OnResetAll(object s, RoutedEventArgs e) => ResetAllRequested?.Invoke();
}
```

  Reset-all confirmation: AppHost shows a `System.Windows.MessageBox.Show(..., MessageBoxButton.YesNo)`
  before running (spec §6 requires confirm).

- [ ] **Step 3: AppHost wiring swap.** Replace the `_settingsWindow` field with
  `private DashboardWindow? _dashboard;`, replace `OpenSettings()` with:

```csharp
    private void OpenDashboard()
    {
        if (_dashboard is { IsVisible: true }) { _dashboard.Activate(); return; }

        var vm = new DashboardViewModel(_settings.Settings, _startup.IsEnabled(), WriteBindingAsync);
        var win = new DashboardWindow(vm);
        win.ApplyDpiRequested += dpi => _ = ApplyDpiAsync(vm, dpi);
        win.LivenessRefreshRequested += () => _ = RefreshLivenessAsync(vm);
        win.SettingsOverlayRequested += () => ShowSettingsOverlay(win, vm);
        win.Closed += (_, _) =>
        {
            vm.ApplyTo(_settings.Settings);
            _settings.Save();
            _dashboard = null; // release-on-close: idle memory returns to baseline
        };
        vm.ApplyState(_monitor.State);
        _monitor.StateChanged += (_, state) => Dispatch(() => { if (_dashboard == win) vm.ApplyState(state); });
        _dashboard = win;
        win.Show();
        _ = SeedDashboardAsync(vm);
    }

    private async Task SeedDashboardAsync(DashboardViewModel vm)
    {
        var dpi = await Task.Run(() => _monitor.GetDpiAsync());
        Dispatch(() => vm.SetCurrentDpi(dpi));
        await RefreshLivenessAsync(vm);
    }

    private async Task RefreshLivenessAsync(DashboardViewModel vm)
    {
        var state = await CheckLivenessAsync();
        Dispatch(() => vm.SetLiveness(state));
    }

    private void ShowSettingsOverlay(DashboardWindow win, DashboardViewModel vm)
    {
        var view = new SettingsView { DataContext = vm };
        view.CloseRequested += () => { vm.ApplyTo(_settings.Settings); _settings.Save(); win.HideOverlay(); };
        view.StartupToggled += enable => { SetStartup(enable); _tray.SetStartupChecked(enable); };
        view.ThemeChanged += name =>
        { ThemeManager.Apply(_app, name); _settings.Settings.Theme = ThemeManager.Resolve(name); _settings.Save(); };
        view.ResetAllRequested += () =>
        {
            var pick = System.Windows.MessageBox.Show(
                "Rewrite all 12 grid buttons to their factory keys?", "Reset buttons",
                System.Windows.MessageBoxButton.YesNo);
            if (pick == System.Windows.MessageBoxResult.Yes) _ = ResetAllButtonsAsync(vm);
        };
        win.ShowOverlay(view);
    }
```

  Rework `ApplyDpiAsync(SettingsWindow, int)` → `ApplyDpiAsync(DashboardViewModel vm, int dpi)`
  (same body; status surfaces via `vm.SetCurrentDpi(readBack)`; on failure call
  `Dispatch(() => vm.SetCurrentDpi(null))` then re-seed with `SeedDashboardAsync`? No — keep simple:
  on failure just `vm.SetCurrentDpi(await Task.Run(() => _monitor.GetDpiAsync()))`). Delete
  `LoadDpiAsync`, `ApplyButtonsAsync`, and `SlotColour` (moved to `DashboardViewModel`). Update the
  three call sites `_tray.SettingsRequested += OpenSettings;` → `OpenDashboard`,
  `p.SettingsRequested += OpenSettings;` → `OpenDashboard`, `_settingsWindow?.Close()` in `Quit()` →
  `_dashboard?.Close()`.

- [ ] **Step 4: Delete the four old UI files + two old test files.** Full suite + build.
Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: PASS (old tests removed; new tests from Tasks 1-6 green).

- [ ] **Step 5: Manual smoke via dev host** — open dashboard from tray, verify: header live-updates,
  capture works incl. Alt-chords + Esc cancel, undo appears/expires, DPI slider + presets apply,
  profile card states, gear overlay (theme switch is instant + persists, cadence clamps, reset-all
  confirms), close window → process working set returns near baseline.
- [ ] **Step 6: Commit** — `feat(dashboard)!: dashboard replaces the Settings window`

---

### Task 10: Popup restyle + "Dashboard" naming

**Files:**
- Modify: `src/NagaBatteryTray/Ui/PopupWindow.xaml` (restyle, no DropShadowEffect, profile line,
  button rename), `src/NagaBatteryTray/Ui/PopupViewModel.cs` (ProfileText), `src/NagaBatteryTray/Ui/TrayIconController.cs`
  (menu item text), `src/NagaBatteryTray/AppHost.cs` (feed profile text)
- Test: `tests/NagaBatteryTray.Tests/` — PopupViewModel has no test file today; add the ProfileText
  cases to a new `PopupViewModelTests.cs`

**Interfaces:**
- Produces: `PopupViewModel.SetProfile(int? slot)` → `ProfileText` ("Profile 3 · green") +
  `HasProfile` (false when slot null — line collapses, spec §4.5).

- [ ] **Step 1: Failing tests** (`PopupViewModelTests.cs`):

```csharp
using NagaBatteryTray.Ui;
using Xunit;

public class PopupViewModelTests
{
    [Fact]
    public void SetProfile_null_hides_the_line()
    {
        var vm = new PopupViewModel();
        vm.SetProfile(null);
        Assert.False(vm.HasProfile);
    }

    [Fact]
    public void SetProfile_shows_slot_identity()
    {
        var vm = new PopupViewModel();
        vm.SetProfile(3);
        Assert.True(vm.HasProfile);
        Assert.Equal("Profile 3 · green", vm.ProfileText);
    }
}
```

- [ ] **Step 2: Run — FAIL.** Implement in `PopupViewModel`:

```csharp
    private string _profileText = "";
    private bool _hasProfile;
    public string ProfileText { get => _profileText; private set => Set(ref _profileText, value); }
    public bool HasProfile { get => _hasProfile; private set => Set(ref _hasProfile, value); }

    public void SetProfile(int? slot)
    {
        HasProfile = slot is not null;
        ProfileText = slot is { } n ? $"Profile {n} · {Dashboard.DashboardViewModel.SlotColour(n)}" : "";
    }
```

  (Make `DashboardViewModel.SlotColour` `internal static` — tests reach internals via
  `InternalsVisibleTo`; the popup is in the same assembly so `internal` is fine.)

- [ ] **Step 3: Restyle `PopupWindow.xaml`.** Replace the root `Border` block: background
  `{DynamicResource App.CanvasBrush}`, border `{DynamicResource App.CardStroke}`, **delete the
  `<Border.Effect>` DropShadowEffect entirely** (global constraint), digits block: PercentText gets
  `FontWeight="ExtraLight" FontSize="40" Foreground="{DynamicResource App.Numeral}"` (drop the bold
  + Accent binding; the charging pill and bar keep the level `Accent` brush from the VM — those ARE
  status colors), title/status text → `{DynamicResource App.TextPrimary}`/`App.TextSecondary`.
  Insert before the buttons Grid:

```xml
      <TextBlock Text="{Binding ProfileText}" FontSize="10" Margin="0,8,0,0"
                 Foreground="{DynamicResource App.TextSecondary}"
                 Visibility="{Binding HasProfile, Converter={StaticResource BoolToVis}}"/>
```

  Rename the second button: `Content="Open dashboard"`. In `TrayIconController`, change
  `menu.Items.Add("Settings", …)` → `menu.Items.Add("Dashboard", …)`. In `AppHost.CreatePopup()`,
  after `var p = new PopupWindow();` add `p.SetProfile(_settings.Settings.OnboardSlot);` and in
  `WriteBindingAsync` after `_settings.Save();` add
  `Dispatch(() => _popup?.SetProfile(_settings.Settings.OnboardSlot));` (keeps the line correct after
  first adoption). Expose `SetProfile` on `PopupWindow`:
  `public void SetProfile(int? slot) => _vm.SetProfile(slot);`

- [ ] **Step 4: Full suite + build — PASS.** Manual: popup opens instantly, styled, profile line
  present (or absent pre-adoption), no shadow artifacts.
- [ ] **Step 5: Commit** — `feat(ui): popup restyled to the design language; Settings → Dashboard naming`

---

### Task 11: Ship gates, docs, finish the branch

**Files:**
- Modify: `CLAUDE.md`, `README.md`

- [ ] **Step 1: Full suite** — `… test` → PASS.
- [ ] **Step 2: USER acceptance (Brandon at the keyboard, dev build):** the §8 manual list — all five
  themes look right (contrast, focus states), hover pairing works both directions, capture/undo feel
  right, reduced-motion honored (Windows animations off → no transitions), popup + tray ring look
  right on the real tray.
- [ ] **Step 3: Release install + §3.1 gates** — `.\scripts\install.ps1`; measure: idle CPU ~0%
  (20 s sample), private working set ~23 MB **after opening and closing the dashboard once**
  (`Get-Counter '\Process(NagaBatteryTray)\Working Set - Private'`), input feel unchanged.
  A failure here is not shippable — stop and investigate.
- [ ] **Step 4: Docs.** `CLAUDE.md`: header sentence gains "themed dashboard UI"; Architecture `Ui/`
  bullet: SettingsWindow/SettingsViewModel references → `Ui/Dashboard/` (DashboardWindow shell +
  MouseStageView + SettingsView overlay + CalloutViewModel/DashboardViewModel/ProfileLiveness),
  `Ui/Themes/` (DesignSystem + 5 presets + ThemeManager, no-DropShadowEffect rule, no-hardcoded-colors
  rule), IconRenderer ring note (digits-win), popup profile line + Dashboard naming; Conventions test
  list swaps `SettingsViewModel`→`DashboardViewModel`, `ButtonRowViewModel`→`CalloutViewModel` and adds
  `ThemeManager`, `ProfileLiveness`. `README.md`: features add themed dashboard + themes + DPI presets
  + ring; "What it looks like" popup sketch gains the profile line; roadmap adds a checked GUI-redesign
  line.
- [ ] **Step 5: Commit docs** — `docs: GUI redesign shipped (dashboard, themes, tray ring)`
- [ ] **Step 6: Finish** — use superpowers:finishing-a-development-branch (verify suite, present
  merge options for `gui-redesign` → `master`).

---

## Self-review notes (already applied)

- Spec coverage: §4.1→T8/9, §4.2→T3, §4.3→T4/7/8, §4.4→T5/6/7, §4.5→T10, §4.6→T2, §4.7→T8 (focusable
  chips, AutomationProperties, DataTriggers only — no animations added anywhere, satisfying
  reduced-motion trivially; if transitions get added later they must check
  `SystemParameters.ClientAreaAnimation`), §5 deletions→T9, §6→T4/6/7/9, §8→T1-6/10 tests + T11 manual.
- Type consistency: `CalloutViewModel.WriteBinding` == AppHost `WriteBindingAsync` signature;
  `DpiPresetItem` shared T6/T8; `SlotColour` lives on `DashboardViewModel` (internal static), used by
  popup VM; `ProfileLivenessState.Unchecked` handled in `SetLiveness`.
- Known simplifications: no literal callout connector lines (hover pairing + flanking layout carries
  the mapping; lines can be added later as pure XAML polish). Status colors used by the popup's
  level bar remain VM-computed brushes (they are semantics, not theme).
