# Naga V3 Pro Dual-Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The app drives both the Razer Naga V2 Pro and the Naga V3 Pro through one device layer and shows the detected model name everywhere the UI currently hardcodes "Naga V2 Pro".

**Architecture:** A static model table in `RazerProtocol` (wired PID, wireless PID, display name) feeds a flat PID scan order (all wired PIDs before all wireless; V3 before V2 within a link). `RazerDevice` records the winning model's name and carries it through `BatteryReading` → `BatteryMonitor` → `DeviceState` → the four UI consumers. Probe loops iterate the same table. All command constants stay shared — the 2026-08-19 hardware probe proved the two protocols byte-identical.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), C#, WPF + WinForms, HidSharp, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-19-naga-v3pro-dual-support-design.md`

## Global Constraints

- Hard rule 1 (GATING): no new timers/threads/polling; device I/O stays on-demand inside `EnsureConnectedAsync`; ~0% idle CPU, ~23 MB private WS, zero input-feel regression.
- Hard rule 2: `AppHost.RestoreRawAsync` and the sweep snapshot are untouched.
- Hard rule 3: no `DropShadowEffect`/`BitmapEffect`; no hardcoded colors in themed XAML.
- Hard rule 4: README + `docs/images/` screenshots update on THIS branch before merge.
- Hard rule 5: run-at-login stays the delayed scheduled task (`StartupRegistration.cs`) — no task touches it; keep it that way.
- Hard rule 6: TDD through `IRazerDevice`/`FakeRazerDevice`; `InternalsVisibleTo.cs` untouched; `RazerDevice` transport and probe commands are the deliberate no-unit-test exceptions.
- Offline / unknown-model UI fallback string is exactly **"Razer Naga"**; V3 display name is exactly **"Naga V3 Pro"**; V2 is **"Naga V2 Pro"** (spec §2).
- V3 PIDs: wired `0x00E7`, wireless `0x00E8` (spec §3). Do not touch `DockPid`.
- Build: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build` · Test: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test` (dotnet is NOT on PATH).
- Conventional commits. Push to origin only when the user says so.
- Read the FULL file before editing it.

---

### Task 1: Model table + PID helpers in RazerProtocol

**Files:**
- Modify: `src/NagaBatteryTray/Hid/RazerProtocol.cs` (constants block, lines 7-11 area)
- Test: `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs`

**Interfaces:**
- Consumes: existing constants `MousePidWired` (0x00A7), `MousePidWireless` (0x00A8).
- Produces (later tasks rely on these exact names):
  - `public readonly record struct MouseModel(int WiredPid, int WirelessPid, string DisplayName);`
  - `public static readonly MouseModel[] SupportedMice` — V3 row first, then V2.
  - `public static int[] ScanPids { get; }` — `[0x00E7, 0x00A7, 0x00E8, 0x00A8]`.
  - `public static bool IsWiredPid(int pid)`
  - `public static string? ModelNameForPid(int pid)` — null for unknown PIDs.
  - Constants `MousePidV3Wired = 0x00E7`, `MousePidV3Wireless = 0x00E8`.

- [ ] **Step 1: Create the branch**

```bash
git checkout -b feature/v3pro-dual-support
```

- [ ] **Step 2: Write the failing tests**

Read `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs` in full, then append inside the existing `RazerProtocolTests` class:

```csharp
    [Fact]
    public void Scan_order_is_all_wired_then_all_wireless_v3_first()
    {
        Assert.Equal(new[] { 0x00E7, 0x00A7, 0x00E8, 0x00A8 }, RazerProtocol.ScanPids);
    }

    [Fact]
    public void IsWiredPid_matches_only_wired_pids()
    {
        Assert.True(RazerProtocol.IsWiredPid(0x00E7));
        Assert.True(RazerProtocol.IsWiredPid(0x00A7));
        Assert.False(RazerProtocol.IsWiredPid(0x00E8));
        Assert.False(RazerProtocol.IsWiredPid(0x00A8));
        Assert.False(RazerProtocol.IsWiredPid(0x00A4)); // dock is not a mouse
    }

    [Fact]
    public void ModelNameForPid_maps_both_links_and_rejects_unknown()
    {
        Assert.Equal("Naga V3 Pro", RazerProtocol.ModelNameForPid(0x00E7));
        Assert.Equal("Naga V3 Pro", RazerProtocol.ModelNameForPid(0x00E8));
        Assert.Equal("Naga V2 Pro", RazerProtocol.ModelNameForPid(0x00A7));
        Assert.Equal("Naga V2 Pro", RazerProtocol.ModelNameForPid(0x00A8));
        Assert.Null(RazerProtocol.ModelNameForPid(0x00A4));
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerProtocolTests"
```

Expected: compile error — `ScanPids`, `IsWiredPid`, `ModelNameForPid` do not exist. (A compile failure of the test project is this codebase's "failing test" for new API.)

- [ ] **Step 4: Implement**

Read `src/NagaBatteryTray/Hid/RazerProtocol.cs` in full. Replace the constants block

```csharp
    public const int VendorId = 0x1532;
    public const int MousePidWireless = 0x00A8;
    public const int MousePidWired = 0x00A7;
    public const int DockPid = 0x00A4;       // Razer Mouse Dock Pro (separate USB device)
    public const int UsagePageVendor = 0xFF00;
```

with

```csharp
    public const int VendorId = 0x1532;
    public const int MousePidWireless = 0x00A8;      // Naga V2 Pro via HyperSpeed dongle
    public const int MousePidWired = 0x00A7;         // Naga V2 Pro cabled
    public const int MousePidV3Wireless = 0x00E8;    // Naga V3 Pro via HyperSpeed dongle (probe 2026-08-19)
    public const int MousePidV3Wired = 0x00E7;       // Naga V3 Pro cabled (probe 2026-08-19)
    public const int DockPid = 0x00A4;       // Razer Mouse Dock Pro (separate USB device)
    public const int UsagePageVendor = 0xFF00;

    /// <summary>A supported mouse: its two link PIDs and the name the UI shows. Command constants are
    /// shared — the 2026-08-19 probe verified the V3 Pro speaks the identical protocol (spec §3).</summary>
    public readonly record struct MouseModel(int WiredPid, int WirelessPid, string DisplayName);

    /// <summary>Preference order within a link type: V3 first (daily driver), then V2.</summary>
    public static readonly MouseModel[] SupportedMice =
    {
        new(MousePidV3Wired, MousePidV3Wireless, "Naga V3 Pro"),
        new(MousePidWired, MousePidWireless, "Naga V2 Pro"),
    };

    /// <summary>Device-scan order: every model's wired PID before any wireless PID — a cabled mouse
    /// must always win over a still-plugged-in receiver that can no longer reach it.</summary>
    public static int[] ScanPids { get; } =
        { MousePidV3Wired, MousePidWired, MousePidV3Wireless, MousePidWireless };

    public static bool IsWiredPid(int pid)
    {
        foreach (var m in SupportedMice) if (m.WiredPid == pid) return true;
        return false;
    }

    /// <summary>Display name for a mouse PID (either link), null for unknown PIDs (e.g. the dock).</summary>
    public static string? ModelNameForPid(int pid)
    {
        foreach (var m in SupportedMice)
            if (m.WiredPid == pid || m.WirelessPid == pid) return m.DisplayName;
        return null;
    }
```

Note: `ScanPids` is spelled out rather than derived from `SupportedMice` with LINQ — the file uses no LINQ, and the order IS the contract; the test locks the two in sync.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerProtocolTests"
```

Expected: PASS (all, including the pre-existing protocol tests).

- [ ] **Step 6: Commit**

```bash
git add src/NagaBatteryTray/Hid/RazerProtocol.cs tests/NagaBatteryTray.Tests/RazerProtocolTests.cs
git commit -m "feat(hid): model table + V3 Pro PIDs (0x00E7/0x00E8) with wired-first scan order"
```

---

### Task 2: BatteryReading.Model + RazerDevice scan/plumbing

**Files:**
- Modify: `src/NagaBatteryTray/Hid/BatteryReading.cs`
- Modify: `src/NagaBatteryTray/Hid/RazerDevice.cs` (class doc comment, fields, `EnsureConnectedAsync`, `ReadAsync`, `FindControlPaths`)
- Test: none (RazerDevice is the transport layer — the deliberate no-unit-test exception, hard rule 6; `BatteryReading`'s new field is exercised by Task 3's monitor test)

**Interfaces:**
- Consumes: `RazerProtocol.ScanPids`, `RazerProtocol.IsWiredPid`, `RazerProtocol.ModelNameForPid` (Task 1).
- Produces: `BatteryReading` gains `string? Model = null` as the LAST positional parameter:
  `public readonly record struct BatteryReading(int Percent, bool IsCharging, bool IsPresent, DateTimeOffset Timestamp, bool IsWired = false, string? Model = null)`.
  `RazerDevice.ReadAsync` fills it with the connected model's display name.

- [ ] **Step 1: Extend BatteryReading**

Read `src/NagaBatteryTray/Hid/BatteryReading.cs` (6 lines). Change the record declaration to:

```csharp
public readonly record struct BatteryReading(int Percent, bool IsCharging, bool IsPresent, DateTimeOffset Timestamp, bool IsWired = false, string? Model = null)
{
    public static BatteryReading Absent(DateTimeOffset now) => new(0, false, false, now);
}
```

- [ ] **Step 2: Plumb the model through RazerDevice**

Read `src/NagaBatteryTray/Hid/RazerDevice.cs` in full. Five edits:

(a) Class doc comment (line ~12): replace the first sentence

```
Reads battery/charging from a Razer Naga V2 Pro. The 90-byte Razer feature report lives on the
mouse HID collection (mi_00, GetMaxFeatureReportLength == 91), which Windows owns — so it must be
```

with

```
Reads battery/charging from a supported Razer Naga (V2 Pro / V3 Pro, RazerProtocol.SupportedMice).
The 90-byte Razer feature report lives on the mouse HID collection (mi_00 on V2, mi_03 on V3 —
never key on interface number; GetMaxFeatureReportLength == 91 is the selector), which Windows owns — so it must be
```

(b) Fields: replace

```csharp
    private bool _wired; // true when the live collection is the wired interface (0x00A7), false for wireless (0x00A8)
```

with

```csharp
    private bool _wired; // true when the live collection is a wired PID (RazerProtocol.IsWiredPid)
    private string? _model; // display name of the connected model (RazerProtocol.ModelNameForPid)
```

(c) In `EnsureConnectedAsync`, replace

```csharp
                _wired = pid == RazerProtocol.MousePidWired;
```

with

```csharp
                _wired = RazerProtocol.IsWiredPid(pid);
                _model = RazerProtocol.ModelNameForPid(pid);
```

(d) In `ReadAsync`, replace

```csharp
            return new BatteryReading(percent, isCharging, true, now, _wired);
```

with

```csharp
            return new BatteryReading(percent, isCharging, true, now, _wired, _model);
```

(e) In `FindControlPaths`, replace the loop header and its doc comment:

```csharp
    /// <summary>Mouse HID collections exposing the 90+1 byte feature report (with their PID), wired (0x00A7)
    /// before wireless (0x00A8): a cabled mouse must win over a still-plugged-in receiver that can no longer
    /// reach it. The PID is returned so the caller can report the active link (wired vs wireless).</summary>
    private static IEnumerable<(int Pid, string Path)> FindControlPaths()
    {
        foreach (int pid in new[] { RazerProtocol.MousePidWired, RazerProtocol.MousePidWireless })
```

with

```csharp
    /// <summary>Mouse HID collections exposing the 90+1 byte feature report (with their PID), in
    /// RazerProtocol.ScanPids order — every wired PID before any wireless one: a cabled mouse must win
    /// over a still-plugged-in receiver that can no longer reach it. The PID is returned so the caller
    /// can report the active link and model.</summary>
    private static IEnumerable<(int Pid, string Path)> FindControlPaths()
    {
        foreach (int pid in RazerProtocol.ScanPids)
```

The battery-query confirmation at the call site (`EnsureConnectedAsync`) already rejects candidate collections that don't answer — e.g. the user's still-plugged V2 receiver while the V2 is off. Do not add any new logic there.

- [ ] **Step 3: Build + full test suite (regression check)**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test
```

Expected: PASS — no existing test knew about the trailing optional parameter, so nothing breaks.

- [ ] **Step 4: Commit**

```bash
git add src/NagaBatteryTray/Hid/BatteryReading.cs src/NagaBatteryTray/Hid/RazerDevice.cs
git commit -m "feat(hid): RazerDevice scans both models and reports the detected model name"
```

---

### Task 3: DeviceState.Model + BatteryMonitor pass-through

**Files:**
- Modify: `src/NagaBatteryTray/Monitoring/DeviceState.cs`
- Modify: `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs` (one line, ~172)
- Test: `tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs`

**Interfaces:**
- Consumes: `BatteryReading.Model` (Task 2).
- Produces: `DeviceState` gains `string? Model = null` as the LAST positional parameter;
  `DeviceState.Online(int percent, bool charging, bool wired = false, string? model = null)`.
  UI tasks (4-6) read `s.Model`.

- [ ] **Step 1: Write the failing test**

Read `tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs` in full, then append inside the class:

```csharp
    [Fact]
    public void Model_name_flows_from_reading_to_state()
    {
        var m = NewMonitor(out _);
        m.ProcessReading(new BatteryReading(70, false, true, DateTimeOffset.Now, IsWired: true, Model: "Naga V3 Pro"));
        Assert.Equal("Naga V3 Pro", m.State.Model);
        Assert.True(m.State.Wired);
    }
```

- [ ] **Step 2: Run it to verify it fails**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "Name=Model_name_flows_from_reading_to_state"
```

Expected: compile error — `DeviceState` has no `Model`.

- [ ] **Step 3: Implement**

Read `src/NagaBatteryTray/Monitoring/DeviceState.cs` (9 lines). Replace the record + factory:

```csharp
public readonly record struct DeviceState(DeviceStatus Status, int Percent, bool Charging, bool Wired = false, string? Model = null)
{
    public static DeviceState Unknown { get; } = new(DeviceStatus.Unknown, 0, false);
    public static DeviceState Online(int percent, bool charging, bool wired = false, string? model = null) => new(DeviceStatus.Online, percent, charging, wired, model);
}
```

Read `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs` in full. Replace (line ~172)

```csharp
        SetState(DeviceState.Online(r.Percent, r.IsCharging, r.IsWired));
```

with

```csharp
        SetState(DeviceState.Online(r.Percent, r.IsCharging, r.IsWired, r.Model));
```

- [ ] **Step 4: Run the full suite**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Monitoring/DeviceState.cs src/NagaBatteryTray/Monitoring/BatteryMonitor.cs tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs
git commit -m "feat(monitoring): DeviceState carries the detected model name"
```

---

### Task 4: Dashboard header shows the detected model

**Files:**
- Modify: `src/NagaBatteryTray/Ui/Dashboard/DashboardViewModel.cs` (header region, ~lines 100-113)
- Modify: `src/NagaBatteryTray/Ui/Dashboard/DashboardWindow.xaml` (line ~21)
- Test: `tests/NagaBatteryTray.Tests/DashboardViewModelTests.cs`

**Interfaces:**
- Consumes: `DeviceState.Model` (Task 3).
- Produces: `DashboardViewModel.ModelName` (string, notifying property, never null — falls back to `"Razer Naga"`).

- [ ] **Step 1: Write the failing tests**

Read `tests/NagaBatteryTray.Tests/DashboardViewModelTests.cs` in full, then append inside the class (`NewVm()` is that file's existing factory: `new DashboardViewModel(new AppSettings(), runAtStartup: false, NoWrite, NoRawWrite)`):

```csharp
    [Fact]
    public void Header_shows_detected_model_when_online()
    {
        var vm = NewVm();
        vm.ApplyState(DeviceState.Online(50, charging: false, wired: true, model: "Naga V3 Pro"));
        Assert.Equal("Naga V3 Pro", vm.ModelName);
    }

    [Fact]
    public void Header_falls_back_to_razer_naga_when_offline_or_unnamed()
    {
        var vm = NewVm();
        vm.ApplyState(DeviceState.Online(50, charging: false, wired: true, model: "Naga V3 Pro"));
        vm.ApplyState(DeviceState.Unknown);
        Assert.Equal("Razer Naga", vm.ModelName);

        vm.ApplyState(DeviceState.Online(50, charging: false)); // online but no model name
        Assert.Equal("Razer Naga", vm.ModelName);
    }
```

- [ ] **Step 2: Run to verify failure**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~DashboardViewModelTests"
```

Expected: compile error — no `ModelName`.

- [ ] **Step 3: Implement**

Read `src/NagaBatteryTray/Ui/Dashboard/DashboardViewModel.cs` in full. In the `// ---- header ----` region add a backing field beside the other header fields (match the file's field-declaration placement) and the property:

```csharp
    private string _modelName = "Razer Naga";
```

```csharp
    public string ModelName { get => _modelName; private set => Set(ref _modelName, value); }
```

In `ApplyState`, after the `DeviceOnline = ...` line, add:

```csharp
        ModelName = s.Model ?? "Razer Naga";
```

Read `src/NagaBatteryTray/Ui/Dashboard/DashboardWindow.xaml` in full. Replace (line ~21)

```xml
            <TextBlock Style="{DynamicResource BodyText}" FontSize="14" FontWeight="SemiBold" Text="Naga V2 Pro"/>
```

with

```xml
            <TextBlock Style="{DynamicResource BodyText}" FontSize="14" FontWeight="SemiBold" Text="{Binding ModelName}"/>
```

(The sibling `HeaderSubtitle` TextBlock already binds against the same DataContext, so the binding path is proven.)

- [ ] **Step 4: Run the full suite**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test
```

Expected: PASS (includes the real-window `DpiPillInteractionTests` — they must stay green).

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Ui/Dashboard/DashboardViewModel.cs src/NagaBatteryTray/Ui/Dashboard/DashboardWindow.xaml tests/NagaBatteryTray.Tests/DashboardViewModelTests.cs
git commit -m "feat(dashboard): header shows detected model name, offline fallback 'Razer Naga'"
```

---

### Task 5: Popup header shows the detected model

**Files:**
- Modify: `src/NagaBatteryTray/Ui/PopupViewModel.cs`
- Modify: `src/NagaBatteryTray/Ui/PopupWindow.xaml` (line ~14)
- Test: `tests/NagaBatteryTray.Tests/PopupViewModelTests.cs`

**Interfaces:**
- Consumes: `DeviceState.Model` (Task 3).
- Produces: `PopupViewModel.ModelName` (string, notifying property, never null — falls back to `"Razer Naga"`).

- [ ] **Step 1: Write the failing tests**

Read `tests/NagaBatteryTray.Tests/PopupViewModelTests.cs` in full, then append inside the class:

```csharp
    [Fact]
    public void Apply_shows_detected_model()
    {
        var vm = new PopupViewModel();
        vm.Apply(DeviceState.Online(64, charging: false, wired: true, model: "Naga V3 Pro"));
        Assert.Equal("Naga V3 Pro", vm.ModelName);
    }

    [Fact]
    public void Apply_unknown_resets_model_to_fallback()
    {
        var vm = new PopupViewModel();
        vm.Apply(DeviceState.Online(64, charging: false, wired: true, model: "Naga V3 Pro"));
        vm.Apply(DeviceState.Unknown);
        Assert.Equal("Razer Naga", vm.ModelName);
    }
```

- [ ] **Step 2: Run to verify failure**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~PopupViewModelTests"
```

Expected: compile error — no `ModelName`.

- [ ] **Step 3: Implement**

Read `src/NagaBatteryTray/Ui/PopupViewModel.cs` in full (49 lines). Add beside the other backing fields:

```csharp
    private string _modelName = "Razer Naga";
```

Add beside the other properties:

```csharp
    public string ModelName { get => _modelName; private set => Set(ref _modelName, value); }
```

In `Apply`, inside the `Unknown` early-return block add (before `return;`):

```csharp
            ModelName = "Razer Naga";
```

and in the online path (after `PercentText = ...`):

```csharp
        ModelName = s.Model ?? "Razer Naga";
```

Read `src/NagaBatteryTray/Ui/PopupWindow.xaml` in full. Replace (line ~14)

```xml
          <TextBlock Text="Naga V2 Pro" FontWeight="SemiBold" FontSize="13" Foreground="{DynamicResource App.TextPrimary}"
                     VerticalAlignment="Center" Margin="5,0,0,0"/>
```

with

```xml
          <TextBlock Text="{Binding ModelName}" FontWeight="SemiBold" FontSize="13" Foreground="{DynamicResource App.TextPrimary}"
                     VerticalAlignment="Center" Margin="5,0,0,0"/>
```

(The `{Binding Status}` sibling proves the DataContext.)

- [ ] **Step 4: Run the full suite**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Ui/PopupViewModel.cs src/NagaBatteryTray/Ui/PopupWindow.xaml tests/NagaBatteryTray.Tests/PopupViewModelTests.cs
git commit -m "feat(popup): header shows detected model name"
```

---

### Task 6: Tray tooltip + low-battery toast use the detected model

**Files:**
- Modify: `src/NagaBatteryTray/Ui/TrayIconController.cs` (`Tooltip`, lines ~68-70)
- Modify: `src/NagaBatteryTray/Ui/Notifications.cs`
- Modify: `src/NagaBatteryTray/AppHost.cs` (line ~53, the `LowBatteryCrossed` handler)
- Test: Create `tests/NagaBatteryTray.Tests/TrayTooltipTests.cs`

**Interfaces:**
- Consumes: `DeviceState.Model` (Task 3); `BatteryMonitor.State` (existing property, read in the AppHost handler).
- Produces: `TrayIconController.Tooltip(DeviceState)` becomes `internal static` (tests reach internals via the existing `InternalsVisibleTo.cs`); `Notifications.LowBattery(int percent, string? model)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/NagaBatteryTray.Tests/TrayTooltipTests.cs`:

```csharp
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Ui;
using Xunit;

public class TrayTooltipTests
{
    [Fact]
    public void Tooltip_uses_detected_model_when_online()
    {
        var s = DeviceState.Online(72, charging: true, wired: false, model: "Naga V3 Pro");
        Assert.Equal("Naga V3 Pro - 72% (charging)", TrayIconController.Tooltip(s));
    }

    [Fact]
    public void Tooltip_falls_back_when_offline()
    {
        Assert.Equal("Razer Naga - no response", TrayIconController.Tooltip(DeviceState.Unknown));
    }

    [Fact]
    public void Tooltip_falls_back_when_online_without_model()
    {
        var s = DeviceState.Online(50, charging: false);
        Assert.Equal("Razer Naga - 50%", TrayIconController.Tooltip(s));
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~TrayTooltipTests"
```

Expected: compile error — `Tooltip` is private.

- [ ] **Step 3: Implement**

Read `src/NagaBatteryTray/Ui/TrayIconController.cs` in full. Replace

```csharp
    private static string Tooltip(DeviceState s) => s.Status == DeviceStatus.Unknown
        ? "Naga V2 Pro - no response"
        : $"Naga V2 Pro - {s.Percent}%{(s.Charging ? " (charging)" : "")}";
```

with

```csharp
    internal static string Tooltip(DeviceState s) => s.Status == DeviceStatus.Unknown
        ? $"{s.Model ?? "Razer Naga"} - no response"
        : $"{s.Model ?? "Razer Naga"} - {s.Percent}%{(s.Charging ? " (charging)" : "")}";
```

Read `src/NagaBatteryTray/Ui/Notifications.cs` in full (14 lines). Replace the method:

```csharp
    public static void LowBattery(int percent, string? model)
    {
        new ToastContentBuilder()
            .AddText(model ?? "Razer Naga")
            .AddText($"Battery at {percent}% - time to charge.")
            .Show();
    }
```

Read `src/NagaBatteryTray/AppHost.cs` in full. Replace (line ~53)

```csharp
        _monitor.LowBatteryCrossed += (_, pct) => Notifications.LowBattery(pct);
```

with

```csharp
        _monitor.LowBatteryCrossed += (_, pct) => Notifications.LowBattery(pct, _monitor.State.Model);
```

(Ordering fact, verified: `LowBatteryCrossed` fires at `BatteryMonitor.cs:167-168` BEFORE `SetState` at `:172`, and `AppHost.Dispatch` is a synchronous `Dispatcher.Invoke` — so the handler reads the PRIOR state, not the one that triggered it. That is still safe: the crossing requires `_armed`, which only a present reading sets, so the prior state is an online state carrying the connected model. The one benign edge: after 4+ consecutive misses `State` is `Unknown` (Model null) while `_armed` survives; a reappearance below threshold then toasts the "Razer Naga" fallback. Accepted in the plan debate 2026-08-19 — the null fallback covers it; do not restructure the event.)

- [ ] **Step 4: Run the full suite**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Ui/TrayIconController.cs src/NagaBatteryTray/Ui/Notifications.cs src/NagaBatteryTray/AppHost.cs tests/NagaBatteryTray.Tests/TrayTooltipTests.cs
git commit -m "feat(tray): tooltip and low-battery toast use the detected model name"
```

---

### Task 7: Probe suite scans both models

**Files:**
- Modify: `src/NagaBatteryTray/Diagnostics/ProbeCommand.cs` (three PID loops: `Run` ~line 18, `RunDpi` ~line 53, `OpenLiveMouse` ~line 548; plus the model-name report lines at ~71 and the two identical banners at ~128/~508)
- Modify: `src/NagaBatteryTray/Diagnostics/ProfileProbeCommand.cs` (`ProfileSession.Open` ~line 445, `ProfileCapture` header ~line 867)
- Test: none new (probe commands run against real hardware — the deliberate exception; `ProfileProbeCommandTests` covers the pure-logic layers and must stay green)

**Interfaces:**
- Consumes: `RazerProtocol.ScanPids`, `RazerProtocol.IsWiredPid`, `RazerProtocol.ModelNameForPid` (Task 1).
- Produces: nothing new for later tasks — this closes spec §4.4.

- [ ] **Step 1: Update ProbeCommand**

Read `src/NagaBatteryTray/Diagnostics/ProbeCommand.cs` in full. Five edits:

(a) In `Run` (~line 18), replace

```csharp
        foreach (int pid in new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidWired })
```

with

```csharp
        foreach (int pid in RazerProtocol.ScanPids)
```

and, in that loop, extend the per-collection banner line so the model is visible; replace

```csharp
                Console.WriteLine($"PID 0x{pid:x4} {Mi(dev.DevicePath)} max={max} -> raw zero-access open");
```

with

```csharp
                Console.WriteLine($"PID 0x{pid:x4} ({RazerProtocol.ModelNameForPid(pid)}, {(RazerProtocol.IsWiredPid(pid) ? "wired" : "wireless")}) {Mi(dev.DevicePath)} max={max} -> raw zero-access open");
```

(b) In `RunDpi` (~line 53), replace the identical `foreach (int pid in new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidWired })` loop header with `foreach (int pid in RazerProtocol.ScanPids)`.

(c) In `OpenLiveMouse` (~line 548), replace

```csharp
        foreach (int pid in new[] { RazerProtocol.MousePidWired, RazerProtocol.MousePidWireless })
```

with

```csharp
        foreach (int pid in RazerProtocol.ScanPids)
```

The doc comment above `OpenLiveMouse` ("wired PID first, then wireless…") stays accurate — `ScanPids` preserves exactly that rule across both models.

(d) Spec §4.4 requires the probes to print the model they found, so name it at the two remaining PID-only report lines. In `RunDpi` (~line 71), replace

```csharp
                Console.WriteLine($"PID 0x{pid:x4} status=0x{reply[1]:x2}");
```

with

```csharp
                Console.WriteLine($"PID 0x{pid:x4} ({RazerProtocol.ModelNameForPid(pid)}) status=0x{reply[1]:x2}");
```

(e) The "live collection" banner appears **twice**, byte-identical, at ~line 128 (`--probe-buttons`) and ~line 508 (the slot test). Both take the SAME replacement, so use a replace-all edit. Replace

```csharp
        Console.WriteLine($"Live collection: PID 0x{s.Pid:x4}\n");
```

with

```csharp
        Console.WriteLine($"Live collection: {RazerProtocol.ModelNameForPid(s.Pid) ?? "unknown"} PID 0x{s.Pid:x4} ({(RazerProtocol.IsWiredPid(s.Pid) ? "wired" : "wireless")})\n");
```

- [ ] **Step 2: Update ProfileProbeCommand**

Read `src/NagaBatteryTray/Diagnostics/ProfileProbeCommand.cs` in full. Two edits:

(a) In `ProfileSession.Open` (~line 445), replace

```csharp
            foreach (int pid in new[] { RazerProtocol.MousePidWired, RazerProtocol.MousePidWireless })
```

with

```csharp
            foreach (int pid in RazerProtocol.ScanPids)
```

(b) In the `ProfileCapture` constructor (~line 867), replace

```csharp
                $"- PID: 0x{pid:x4} ({(pid == RazerProtocol.MousePidWired ? "wired" : "wireless")})\n" +
```

with

```csharp
                $"- PID: 0x{pid:x4} ({RazerProtocol.ModelNameForPid(pid) ?? "unknown"}, {(RazerProtocol.IsWiredPid(pid) ? "wired" : "wireless")})\n" +
```

- [ ] **Step 3: Build + full suite**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test
```

Expected: PASS (`ProfileProbeCommandTests` untouched and green).

- [ ] **Step 4: Commit**

```bash
git add src/NagaBatteryTray/Diagnostics/ProbeCommand.cs src/NagaBatteryTray/Diagnostics/ProfileProbeCommand.cs
git commit -m "feat(diagnostics): probe suite scans both supported models via ScanPids"
```

---

### Task 8: Screenshot-probe states, README, regenerated screenshots

**Files:**
- Modify: `tests/NagaBatteryTray.Tests/DashboardScreenshotProbe.cs` (the `DeviceState.Online(...)` fixture calls, ~lines 63 and 80)
- Modify: `README.md` (model naming + supported-devices wording)
- Regenerate: `docs/images/*` (via the UI screenshot probe)

**Interfaces:**
- Consumes: `DeviceState.Online(..., model:)` (Task 3); `ModelName` bindings (Tasks 4-5).
- Produces: probe renders + README that say "Naga V3 Pro" / "supports V2 Pro and V3 Pro" — closes hard rule 4 for this branch.

- [ ] **Step 1: Give the probe fixtures a model name**

Read `tests/NagaBatteryTray.Tests/DashboardScreenshotProbe.cs` in full. Update the two online fixture states so screenshots render the V3 name (the offline state stays as-is and exercises the "Razer Naga" fallback):

Replace (~line 63)

```csharp
        pop.ApplyState(DeviceState.Online(95, charging: true, wired: true));
```

with

```csharp
        pop.ApplyState(DeviceState.Online(95, charging: true, wired: true, model: "Naga V3 Pro"));
```

Replace (~line 80)

```csharp
        vm.ApplyState(DeviceState.Online(48, charging: false, wired: false));
```

with

```csharp
        vm.ApplyState(DeviceState.Online(48, charging: false, wired: false, model: "Naga V3 Pro"));
```

- [ ] **Step 2: Update README wording**

Read `README.md` in full. Then:
- `grep -n "V2 Pro" README.md` and update every hit that presents the app as V2-only. The tagline/subtitle becomes "…for the **Razer Naga V2 Pro and Naga V3 Pro**" (keep the sentence's existing shape).
- Add one line to whatever section describes hardware support (or the intro if none): `Supported mice: Razer Naga V2 Pro (wired 0x00A7 / wireless 0x00A8) and Razer Naga V3 Pro (wired 0x00E7 / wireless 0x00E8) — auto-detected, model name shown in the dashboard.`
- Do NOT bulk-rewrite feature history or screenshots' captions beyond what the new renders change.
- `docs/architecture.md:93` describes the scan order as "wired `0x00A7` first, then wireless `0x00A8`" — update that one sentence to say the scan iterates `RazerProtocol.ScanPids` (every supported model's wired PID before any wireless one; V3 before V2 within a link). Leave the openrazer prior-art bullet at `docs/architecture.md:289` alone — it describes openrazer's driver and stays true.

- [ ] **Step 3: Regenerate the screenshots**

`NAGA_UI_PROBE_OUT` is a single PNG **file path**, not a directory (`File.Create(outPath)` at `tests/NagaBatteryTray.Tests/DashboardScreenshotProbe.cs:120`), and one invocation renders one image. The README references six images (`README.md:17,79,85-88`), so the probe runs six times. Run this exactly:

```powershell
$env:NAGA_UI_PROBE = "1"
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
$filter = "FullyQualifiedName~DashboardScreenshotProbe"

# 1. dashboard.png - dashboard, Ultraviolet, steady (README:17 alt text names Ultraviolet)
$env:NAGA_UI_PROBE_TARGET = "dashboard"; $env:NAGA_UI_PROBE_THEME = "Ultraviolet"; $env:NAGA_UI_PROBE_STATE = "steady"
$env:NAGA_UI_PROBE_OUT = "$PWD\docs\images\dashboard.png"; & $dotnet test --filter $filter

# 2. popup.png - popup target ignores STATE (it seeds its own 95% charging wired state at DashboardScreenshotProbe.cs:63)
$env:NAGA_UI_PROBE_TARGET = "popup"; $env:NAGA_UI_PROBE_THEME = "Ultraviolet"
$env:NAGA_UI_PROBE_OUT = "$PWD\docs\images\popup.png"; & $dotnet test --filter $filter

# 3-6. the four theme shots - dashboard, steady, one per preset
$env:NAGA_UI_PROBE_TARGET = "dashboard"; $env:NAGA_UI_PROBE_STATE = "steady"
foreach ($t in "Porcelain", "Razer", "Ice", "Ember") {
    $env:NAGA_UI_PROBE_THEME = $t
    $env:NAGA_UI_PROBE_OUT = "$PWD\docs\images\theme-$($t.ToLower()).png"
    & $dotnet test --filter $filter
}

Remove-Item Env:NAGA_UI_PROBE, Env:NAGA_UI_PROBE_OUT, Env:NAGA_UI_PROBE_THEME, Env:NAGA_UI_PROBE_STATE, Env:NAGA_UI_PROBE_TARGET
```

Then check `git status`: all six files under `docs/images/` must show as modified. Open `docs/images/dashboard.png` and confirm the header card reads "Naga V3 Pro"; open `docs/images/popup.png` and confirm the same. Theme names are the dictionary filenames in `src/NagaBatteryTray/Ui/Themes/`; the lowercase `theme-*.png` names match the existing files.

- [ ] **Step 4: Full suite (probe gated off)**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/NagaBatteryTray.Tests/DashboardScreenshotProbe.cs README.md docs/images/ docs/architecture.md
git commit -m "docs(readme): dual V2/V3 Pro support + regenerated probe screenshots"
```

---

### Task 9: Full verification + install + hardware acceptance handoff

**Files:**
- No source changes. Runs the gates.

**Interfaces:**
- Consumes: everything above.
- Produces: an installed build for the user's visual acceptance (which gates the merge).

- [ ] **Step 1: Full clean test run**

```bash
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test
```

Expected: all tests PASS (baseline was 256 + the ~10 added here). Paste the summary line as evidence.

- [ ] **Step 2: Install the build**

```powershell
.\scripts\install.ps1
```

Note: SAC may veto the fresh unsigned exe by hash (`0x800711C7`). If launch fails with that code, rebuild with `-p:Deterministic=false` and re-run the install (see CLAUDE.md Commands).

- [ ] **Step 3: Hand the acceptance checklist to the user (merge gate — do NOT merge without their word)**

Ask the user to verify on the installed build, V3 Pro connected:

1. Tray icon shows battery %; tooltip says "Naga V3 Pro - NN%".
2. Dashboard header: "Naga V3 Pro", subtitle "Wireless" on the dongle.
3. Plug the cable → subtitle flips to "Wired" without restart; unplug → flips back.
4. Popup header says "Naga V3 Pro".
5. DPI read/set works; remap one thumb button and undo it; switch profile slot and back.
6. Optional: power up the V2 Pro (V3 off/unplugged) → app finds it and shows "Naga V2 Pro".

- [ ] **Step 4: After user acceptance — merge and clean up (only on their word)**

```bash
git checkout master
git merge --no-ff feature/v3pro-dual-support -m "merge: Naga V3 Pro dual support (probe-verified parity)"
git branch -d feature/v3pro-dual-support
```

Append a ledger entry to `.superpowers/sdd/progress.md` (gitignored — never stage it). Push to origin ONLY when the user says so.

---

## Debate record

**Panel (user-invoked, 2026-08-19).** Composition: Kimi (cross-vendor) + Fable (same-harness).
Valid per references/panels.md — the panel carries at least one cross-vendor lane; Fable is never
one with a Claude driver, which is why it is not the only reviewer. Harness floor checked before
dispatching the Fable seat: Claude Code 2.1.236, at or above the 2.1.216 floor.

**Participants:** Opus 5 (session driver) / Kimi K3 (kimi-code 0.31.1, cross-vendor lane, sessions
`session_a5f1a984-10d4-40a5-b681-df9ee72654b0` rounds 1-3 and `session_b147b6e1-d17f-4d2b-b17d-8dbbe452c7a5`
round 4) / Fable 5 (parallax:fable-panel-reviewer, same-harness lane, agent `a99f802da31ebed91`, rounds 1-3)
**Rounds used:** Kimi 4 of 4 / Fable 3 of 4
**Outcome:** converged — both lanes' terminal verdicts are PASS on the FINAL subject revision
**Verification status:** FULL
**Degradation:** quota-exhausted (the primary codex/Sol lane sat at 100% of its weekly window; the panel ran without it)
**Authorized by:** user before round 1 (Kimi lane requested explicitly), and again before the Fable lane
**Raw rounds:** docs/superpowers/plans/rounds/2026-08-19-naga-v3pro-dual-support/

### Subject revision history

| Stage | Plan blob | Head |
| --- | --- | --- |
| Kimi rounds 1-3 (initial freeze) | b7ccd090941989012560de2139f8400ab9326cae | bb9e07812b10c21c850ac13bb27399342b3dc2ad |
| After Fable rounds 1-2 amendments | b39621e59eaee8aeacaa0f85c2fc17b65805b039 | ae555cd1177f6d0ef4a50f0f31e757de11aa8f6d |
| **FINAL — both lanes' terminal PASS** | **a9a2c844f7cda3a059f2d8f2789fa3ec4c3d6e1a** | **1774e9a8868582097393ac8a5368e11ad408b190** |

Kimi's round-3 PASS was bound to the first blob and became INPUT, not terminal, once the Fable
amendments moved the subject (panels.md: a verdict against a stale revision is never terminal).
Round 4 re-opened the Kimi lane on the final blob for that reason; its PASS there is the terminal
cross-vendor verdict. Spec blob `c08127ad79f05a7fe3c0bf343fd6ddb82dcf76a4` was unchanged from the
first freeze onward.

### Resolved points

Lanes are anonymized as A and B in this table because the panel ran blind: neither reviewer knew
which lane raised what, and the driver relayed every finding without attribution.

| # | Claim | Raised by | Outcome | Evidence |
|---|-------|-----------|---------|----------|
| 1 | Task 6's rationale said LowBatteryCrossed fires after SetState; it fires BEFORE, via synchronous Dispatcher.Invoke, so the handler reads the PRIOR state | lane A | accepted; rationale corrected, event deliberately not restructured | src/NagaBatteryTray/Monitoring/BatteryMonitor.cs:167-172, src/NagaBatteryTray/AppHost.cs:73 |
| 2 | Hard rule 5 missing from Global Constraints | lane A | accepted; added | CLAUDE.md hard rule 5 |
| 3 | An architecture.md line goes stale; lane cited :289 | lane A | accepted with corrected citation — :289 is openrazer prior art and stays true; the stale line is :93 | docs/architecture.md:93 vs :289 |
| 4 | Spec section 4.2 tuple shape contradicted the plan's (int Pid, string Path) + helpers | lane A | accepted; spec amended to match the plan | spec section 4.2 |
| 5 | Task 8 Step 5 did not stage the docs/architecture.md its own Step 2 edits | lane A | accepted; path added | plan Task 8 Step 5 |
| 6 | Spec section 4.4 said probes iterate SupportedMice — that order would try a V3 dongle before a V2 cable, breaking wired-beats-wireless | lane A | accepted; changed to ScanPids | spec sections 4.2/4.4 |
| 7 | Spec naming slips: EnsureOpen twice, and the `_wired` predicate phrasing | lane A | accepted; corrected to EnsureConnectedAsync / IsWiredPid(pid) | spec sections 4.2, 5 |
| 8 | **Blocking.** Task 8 Step 3's regeneration command fails as written: NAGA_UI_PROBE_OUT is a single PNG FILE path (File.Create), it was set to a directory, one run renders one image, and six are referenced — and the step made the implementer reconstruct the theme/state/target matrix | lane B | accepted; Step 3 rewritten as six explicit invocations with an explicit mapping | tests/NagaBatteryTray.Tests/DashboardScreenshotProbe.cs:120, README.md:17,79,85-88 |
| 9 | Spec section 4.4 promises the probes print the model name, but the DPI line and the "Live collection" banner still printed a bare PID | lane B | accepted toward the spec; Task 7 gained edits (d) and (e), with the byte-identical banner at both sites handled by a replace-all | src/NagaBatteryTray/Diagnostics/ProbeCommand.cs:71, :128, :508 |
| 10 | Cosmetic miscounts: Task 2 "Four edits" listing five; two wrong file line counts | lane B | accepted; corrected to Five / 49 / 14 | plan Task 2, :502, :630 |
| 11 | The fix for #9 reintroduced the #10 defect class — Task 7 Step 1 then said "Three edits" while listing five | lane B | accepted; corrected, and the driver swept all three count preambles rather than the one cited | plan:190, :686, :754 |
| 12 | Whether `public static int[] ScanPids { get; } = { ... };` compiles (raised UNVERIFIED by lane B, never a finding) | lane B | resolved by driver compile evidence, no plan change: that exact shape compiled and ran on the pinned SDK, printing 231,167,232,168 = 0xE7,0xA7,0xE8,0xA8 in plan order | plan:127-128 |

No convergent blind findings: the two lanes' findings were disjoint — lane A on rationale and
spec-plan consistency, lane B on executability of the command blocks. Every point above was
accepted; none was refuted or struck, and one lane citation (#3) was corrected by the driver
against the tree before acceptance.

### Escalated points (user-decided)

| # | Question | Session position | Reviewer position | Owner's call |
|---|----------|------------------|-------------------|--------------|
| — | none | | | |

### Lane and evidence notes

- Lane substitution is not degradation. The cross-vendor seat ran on the backup lane at the user's
  explicit request; the primary (codex/GPT-5.6 Sol) was independently quota-exhausted at 100% of its
  weekly window throughout, so it dispatched no round.
- Review mirrors: `%TEMP%\kerev123` (Kimi rounds 1-3) and `%TEMP%\kerev124` (Kimi round 4), each
  rebuilt at the head under review and identity-verified before every dispatch. Final mirror identity:
  source_head = mirror_head = 1774e9a8868582097393ac8a5368e11ad408b190,
  source_status_sha256 = 54502b92955a7e3afe9852b7c000fb442a848dea5bad6a7a4aa528f881356207.
  Back-channel enumeration (AGENTS.md, `.agents/`, `.kimi-code/`) was empty in the repo at every
  build, so no remediation commit was needed and source_head equalled mirror_head throughout.
- Kimi containment: committed `kimi-reviewer-agent.md` (five-tool read-only allowlist); the write-probe
  ran before round 1 of BOTH debates and PASSED each time (explicit refusal, marker absent, mirror
  status delta empty). Mirror status delta after every round: baseline plus nothing.
- Kimi per-round evidence: `read-kimi-round-evidence.ps1` status clean on all 4 rounds.
  toolsHash `3174a328b87a197903a223344c2acc973389f87147e3529a7185dc8d99678777` and systemPromptHash
  `f4410bdc723b724082088ba5b82ea44a108e6d84953b38d0288537334f2a048d` held identical across both
  debates, evidencing an unchanged agent surface. One validator re-run occurred in debate 1 round 1:
  the first invocation failed on the session inventory's path spelling (leaf names instead of full
  paths); inventory membership was unchanged and the corrected re-run returned clean.
- Fable per-round evidence class is dispatch metadata: the round-1 dispatch named the model pin, and
  the resume surface carries no model parameter, so the pin could not be swapped mid-debate. Round
  continuity was evidenced by transcript recall across three rounds. Self-reported identity was not
  treated as evidence.
- Blind relay held in both directions: Fable received lane A's findings anonymized and re-verified
  them against the tree; Kimi's round 4 received lane B's findings anonymized. Neither lane learned
  the other's identity, and the driver verified every relayed claim against the repo before relaying.
- Environment notes: `~/.codex/AGENTS.md` exists (primary-lane note; that lane dispatched nothing here).
  `~/.agents/skills` holds 27 entries (count only; `--skills-dir` passed on every Kimi call). Kimi debate
  home config: `default_effort = "high"` (confirmed per call), `extra_skill_dirs = []` (builder-written),
  `[thinking] enabled = true` (config-asserted only, not runtime-verifiable on kimi-code 0.31.1).
- Effective route confirmed under the strictest-lane rule: every Kimi round's evidence matched the
  backup lane's canonical declarations (kimi-code/k3-256k, provider kimi, effort high), and the Fable
  lane's dispatch metadata matched its seat pin on every round.
