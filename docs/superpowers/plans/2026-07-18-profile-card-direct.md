# Profile Card v2 (Direct Active-Slot Read + Activate) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The dashboard's Profile card shows the mouse's actual active slot via the hardware-verified `0x05/0x84` read and offers an Activate button (`0x05/0x04` write) when the mouse is not on the app's slot — replacing the effective-action inference.

**Architecture:** Spec §13 of `docs/superpowers/specs/2026-07-18-naga-profile-probe-design.md`. Three layers: device (`IRazerDevice`/`RazerDevice` + `BatteryMonitor` pass-throughs), view-model state mapping (`DashboardViewModel`, retiring `ProfileLiveness`), and wiring (AppHost flows + the card's XAML).

**Tech Stack:** .NET 10 WPF, xUnit, existing `RazerProtocol.BuildGetActiveProfileBuffer`/`ParseActiveProfileReply`/`BuildSetActiveProfileBuffer` (all hardware-verified 2026-07-18).

## Global Constraints

- Perf gate (CLAUDE.md): reads/writes **only on the existing event-driven triggers** (dashboard open via `SeedDashboardAsync`, the card's ↻ button, explicit Activate click) — never polled, no new timers/threads; blocking HID work stays off the UI thread via `Task.Run`, results marshal back via `Dispatch`.
- All device I/O serializes on `BatteryMonitor._readLock` exactly like the existing DPI/button/profile pass-throughs.
- No hardcoded colors in themed XAML — `DynamicResource App.*`/`Status.*` keys only; no `DropShadowEffect`.
- Failure is visible, never silent (the card's detail line carries the message).
- Tests: logic layers only, via `FakeRazerDevice`; `InternalsVisibleTo` stays.
- Conventional commits with the repo's two trailers.
- Build/test: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build|test`. Suite baseline: 216.

**File map:**
- Modify: `src/NagaBatteryTray/Hid/IRazerDevice.cs`, `src/NagaBatteryTray/Hid/RazerDevice.cs`, `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs`, `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs`
- Modify: `src/NagaBatteryTray/Ui/Dashboard/DashboardViewModel.cs`; Delete: `src/NagaBatteryTray/Ui/Dashboard/ProfileLiveness.cs`, `tests/NagaBatteryTray.Tests/ProfileLivenessTests.cs` (if present — verify name by glob)
- Modify: `src/NagaBatteryTray/AppHost.cs`, `src/NagaBatteryTray/Ui/Dashboard/MouseStageView.xaml`, `src/NagaBatteryTray/Ui/Dashboard/MouseStageView.xaml.cs`, `src/NagaBatteryTray/Ui/Dashboard/DashboardWindow.xaml.cs` (event forwarding — mirror how `LivenessRefreshRequested` is forwarded today), `CLAUDE.md`
- Test: `tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs`, `tests/NagaBatteryTray.Tests/DashboardViewModelTests.cs`

---

### Task 1: device + monitor layer

**Files:** the four in the first file-map line, plus `tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs`.

**Interfaces:**
- Produces: `Task<byte?> IRazerDevice.GetActiveProfileAsync(CancellationToken ct)`; `Task<bool> IRazerDevice.SetActiveProfileAsync(byte slot, CancellationToken ct)`; `Task<byte?> BatteryMonitor.GetActiveProfileAsync()`; `Task<bool> BatteryMonitor.SetActiveProfileAsync(byte slot)`; `FakeRazerDevice.ActiveProfile` (`byte?`), `.SetActiveProfileResult` (`bool`, default true), `.ActiveProfileSets` (`List<byte>`).

- [ ] **Step 1: Write the failing tests** — append to `BatteryMonitorTests.cs` (read it first; construct the monitor exactly as its existing pass-through tests do):

```csharp
[Fact]
public async Task GetActiveProfile_passes_through_to_the_device()
{
    var device = new FakeRazerDevice { ActiveProfile = 3 };
    using var monitor = NewMonitor(device); // match the file's existing factory/ctor idiom
    Assert.Equal((byte)3, await monitor.GetActiveProfileAsync());
}

[Fact]
public async Task SetActiveProfile_forwards_slot_and_result()
{
    var device = new FakeRazerDevice { SetActiveProfileResult = true };
    using var monitor = NewMonitor(device);
    Assert.True(await monitor.SetActiveProfileAsync(2));
    Assert.Equal((byte)2, device.ActiveProfileSets.Single());
}
```

- [ ] **Step 2: Run** `…dotnet.exe test --filter "FullyQualifiedName~BatteryMonitorTests"` — expect compile failure (members missing).

- [ ] **Step 3: Implement.**

`IRazerDevice.cs` — after `CreateProfileAsync`:

```csharp
    /// <summary>Read which onboard slot is ACTIVE (0x05/0x84, hardware-verified 2026-07-18,
    /// echo-checked parse). Null = unreachable or invalid reply.</summary>
    Task<byte?> GetActiveProfileAsync(CancellationToken ct);

    /// <summary>Switch the active onboard slot (0x05/0x04, hardware-verified 2026-07-18; persists
    /// across power-cycles — bottom-button parity). Only ever targets the app's adopted slot.
    /// True = firmware acked (status 0x02).</summary>
    Task<bool> SetActiveProfileAsync(byte slot, CancellationToken ct);
```

`RazerDevice.cs` — mirror `GetDpiAsync`/`SetDpiAsync` verbatim in shape (read them first; same try/catch → `CloseHandle()`+`LogOnce` on exception, same tid gate, same ack idiom as `SetDpiAsync` uses for its reply):

```csharp
    public async Task<byte?> GetActiveProfileAsync(CancellationToken ct)
    {
        try
        {
            byte tid = await EnsureConnectedAsync(ct);
            if (tid == 0) return null;
            var reply = await ExchangeAsync(RazerProtocol.BuildGetActiveProfileBuffer(tid), ct);
            if (reply is null) return null;
            if (RazerProtocol.ParseActiveProfileReply(reply, out byte slot) != ReplyResult.Success) return null;
            return slot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { CloseHandle(); LogOnce(ex); return null; }
    }

    public async Task<bool> SetActiveProfileAsync(byte slot, CancellationToken ct)
    {
        try
        {
            byte tid = await EnsureConnectedAsync(ct);
            if (tid == 0) return false;
            var reply = await ExchangeAsync(RazerProtocol.BuildSetActiveProfileBuffer(tid, slot), ct);
            return reply is not null && reply[1] == 0x02; // align with SetDpiAsync's ack check — verify against it
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { CloseHandle(); LogOnce(ex); return false; }
    }
```

`BatteryMonitor.cs` — after `CreateProfileAsync`, copying `GetProfileListAsync`'s pattern exactly:

```csharp
    /// <summary>Read the active onboard slot. Blocks for the read lock.</summary>
    public async Task<byte?> GetActiveProfileAsync()
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return null; }
        try { return await _device.GetActiveProfileAsync(_cts.Token); }
        catch (OperationCanceledException) { return null; }
        finally { _readLock.Release(); }
    }

    /// <summary>Switch the active onboard slot (write-on-action only). Blocks for the read lock.</summary>
    public async Task<bool> SetActiveProfileAsync(byte slot)
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return false; }
        try { return await _device.SetActiveProfileAsync(slot, _cts.Token); }
        catch (OperationCanceledException) { return false; }
        finally { _readLock.Release(); }
    }
```

`FakeRazerDevice.cs` — after the profile members:

```csharp
    public byte? ActiveProfile { get; set; }
    public bool SetActiveProfileResult { get; set; } = true;
    public List<byte> ActiveProfileSets { get; } = new();

    public Task<byte?> GetActiveProfileAsync(CancellationToken ct) => Task.FromResult(ActiveProfile);

    public Task<bool> SetActiveProfileAsync(byte slot, CancellationToken ct)
    {
        ActiveProfileSets.Add(slot);
        if (SetActiveProfileResult) ActiveProfile = slot; // realistic: a successful set changes the active slot
        return Task.FromResult(SetActiveProfileResult);
    }
```

- [ ] **Step 4: Run the full suite** — expect 218 (216 + 2), all green.
- [ ] **Step 5: Commit** — `feat(profile): active-slot get/set through device + monitor (hardware-verified 0x05/0x84 + 0x05/0x04)` + trailers.

---

### Task 2: view-model state mapping (retire ProfileLiveness)

**Files:** `DashboardViewModel.cs`; delete `ProfileLiveness.cs` and its test file; `tests/NagaBatteryTray.Tests/DashboardViewModelTests.cs`.

**Interfaces:**
- Produces: `void DashboardViewModel.SetActiveSlot(byte? active)`; `bool CanActivate` (INPC); `void SetProfileNote(string note)` (overwrites `ProfileDetail` only); enum `ProfileLivenessState` moves INTO `DashboardViewModel.cs` (file scope, same names — `NotAdopted, Unchecked, Unknown, Live, NotLive`) so deleting `ProfileLiveness.cs` doesn't break the namespace.
- Consumes: nothing from Task 1 (pure VM) — Task 3 connects them.

- [ ] **Step 1: Write the failing tests** — in `DashboardViewModelTests.cs` (read it first; use its existing `AppSettings` fixture idiom; a VM with `OnboardSlot = 2` unless stated):

```csharp
[Fact]
public void SetActiveSlot_maps_states_from_slot_equality()
{
    var vm = NewVm(onboardSlot: 2);           // match the file's fixture helper
    vm.SetActiveSlot(2);
    Assert.Contains("Slot 2", vm.ProfileTitle);
    Assert.Contains("live", vm.ProfileDetail); // Live: "● active on the mouse"-style line contains "live"
    Assert.False(vm.CanActivate);

    vm.SetActiveSlot(3);
    Assert.Contains("Slot 3", vm.ProfileDetail); // NotLive: names where the mouse actually is
    Assert.True(vm.CanActivate);

    vm.SetActiveSlot(null);                    // Unknown: unreachable
    Assert.Contains("unknown", vm.ProfileDetail);
    Assert.False(vm.CanActivate);
}

[Fact]
public void SetActiveSlot_without_adopted_slot_stays_NotAdopted()
{
    var vm = NewVm(onboardSlot: null);
    vm.SetActiveSlot(1);
    Assert.Contains("No app profile", vm.ProfileTitle);
    Assert.False(vm.CanActivate);
}

[Fact]
public void SetProfileNote_overwrites_detail_only()
{
    var vm = NewVm(onboardSlot: 2);
    vm.SetActiveSlot(3);
    vm.SetProfileNote("Couldn't switch — wiggle the mouse and retry");
    Assert.Contains("wiggle", vm.ProfileDetail);
    Assert.Contains("Slot 2", vm.ProfileTitle); // title untouched
}
```

- [ ] **Step 2: Run** the DashboardViewModelTests filter — compile failure expected.

- [ ] **Step 3: Implement** in `DashboardViewModel.cs` — replace the `SetLiveness` block:

```csharp
// file scope, above the classes (moved from the deleted ProfileLiveness.cs):
public enum ProfileLivenessState { NotAdopted, Unchecked, Unknown, Live, NotLive }
```

```csharp
    // ---- profile card (direct active-slot read, spec §13) ----
    private byte? _activeSlot;
    public string ProfileTitle { get => _profileTitle; private set => Set(ref _profileTitle, value); }
    public string ProfileDetail { get => _profileDetail; private set => Set(ref _profileDetail, value); }

    /// <summary>Activate is offered only when we KNOW the mouse is on another slot — adopted slot
    /// present, active slot read successfully, and they differ.</summary>
    public bool CanActivate { get => _canActivate; private set => Set(ref _canActivate, value); }
    private bool _canActivate;

    /// <summary>Feed the card a fresh 0x05/0x84 read (null = unreachable). Drives the whole
    /// state machine — Live/NotLive are slot equality now, not byte inference.</summary>
    public void SetActiveSlot(byte? active)
    {
        _activeSlot = active;
        ApplyProfileState(_slot is null ? ProfileLivenessState.NotAdopted
            : active is null ? ProfileLivenessState.Unknown
            : active == _slot ? ProfileLivenessState.Live
            : ProfileLivenessState.NotLive);
    }

    /// <summary>Transient card status ("Switching…", failure text) — detail line only.</summary>
    public void SetProfileNote(string note) => ProfileDetail = note;

    private void ApplyProfileState(ProfileLivenessState state)
    {
        string identity = _slot is { } n ? $"Slot {n} · {SlotColour(n)}" : "";
        (ProfileTitle, ProfileDetail) = state switch
        {
            ProfileLivenessState.NotAdopted => ("No app profile yet", "Remap any button to create one."),
            ProfileLivenessState.Live => (identity, "● live — active on the mouse"),
            ProfileLivenessState.NotLive => (identity,
                $"○ Mouse is on Slot {_activeSlot} · {SlotColour(_activeSlot!.Value)}"),
            ProfileLivenessState.Unknown => (identity, "state unknown — mouse unreachable"),
            _ => (identity, ""), // Unchecked: identity only, no claim
        };
        CanActivate = state == ProfileLivenessState.NotLive;
    }
```

Then: ctor's `SetLiveness(...)` call becomes `ApplyProfileState(_slot is null ? ProfileLivenessState.NotAdopted : ProfileLivenessState.Unchecked)`; `SetAdoptedSlot` calls `ApplyProfileState(ProfileLivenessState.Unchecked)`; `SlotColour` gains no changes. Delete `SetLiveness` and the standalone files `ProfileLiveness.cs` + its test file; fix any other `SetLiveness`/`ProfileLiveness.Evaluate` references found by grep (AppHost's are rewritten in Task 3 — if the solution won't build between Tasks 2 and 3, fold the minimal AppHost call-site rename into this commit and note it).

- [ ] **Step 4: Full suite** — green (count = 218 + 3 new − however many ProfileLivenessTests were deleted; report the real number).
- [ ] **Step 5: Commit** — `feat(dashboard): profile card state from the direct active-slot read - ProfileLiveness retired` + trailers.

---

### Task 3: AppHost + XAML wiring

**Files:** `AppHost.cs`, `MouseStageView.xaml`, `MouseStageView.xaml.cs`, `DashboardWindow.xaml.cs`, `CLAUDE.md`.

**Interfaces:**
- Consumes: Task 1's `BatteryMonitor.GetActiveProfileAsync/SetActiveProfileAsync`, Task 2's `vm.SetActiveSlot/SetProfileNote/CanActivate`.
- Produces: `MouseStageView.ActivateProfileRequested` event (`Action`), forwarded by `DashboardWindow` exactly like `LivenessRefreshRequested` is today.

- [ ] **Step 1: AppHost rewiring.** Replace `CheckLivenessAsync` + `RefreshLivenessAsync` with:

```csharp
    /// <summary>Profile card refresh (spec §13): ONE direct active-slot read (0x05/0x84). Only
    /// called on dashboard open / explicit refresh / after Activate — never polled.</summary>
    private async Task RefreshProfileAsync(DashboardViewModel vm)
    {
        var active = await Task.Run(() => _monitor.GetActiveProfileAsync());
        Dispatch(() => vm.SetActiveSlot(active));
    }

    /// <summary>Activate button: switch the mouse to the app's slot (0x05/0x04, write-on-action),
    /// then re-read to confirm. Failure is visible on the card, never silent.</summary>
    private async Task ActivateProfileAsync(DashboardViewModel vm)
    {
        if (_settings.Settings.OnboardSlot is not int slot) return;
        Dispatch(() => vm.SetProfileNote("Switching…"));
        bool ok = await Task.Run(() => _monitor.SetActiveProfileAsync((byte)slot));
        if (!ok) { Dispatch(() => vm.SetProfileNote("Couldn't switch — wiggle the mouse and retry")); return; }
        await RefreshProfileAsync(vm);
    }
```

Wire in `OpenDashboard`: the existing `win.LivenessRefreshRequested += () => _ = RefreshLivenessAsync(vm);` becomes `+= () => _ = RefreshProfileAsync(vm);` and add `win.ActivateProfileRequested += () => _ = ActivateProfileAsync(vm);` beside it. `SeedDashboardAsync`'s `await RefreshLivenessAsync(vm)` → `await RefreshProfileAsync(vm)`. Remove now-unused usings/refs if any.

- [ ] **Step 2: View events.** `MouseStageView.xaml.cs`: add `public event Action? ActivateProfileRequested;` and `private void OnActivateProfile(object s, RoutedEventArgs e) => ActivateProfileRequested?.Invoke();` next to `OnRefreshLiveness`. `DashboardWindow.xaml.cs`: forward it exactly as `LivenessRefreshRequested` is forwarded (read the file; mirror the same pattern, same naming).

- [ ] **Step 3: XAML.** In `MouseStageView.xaml`'s profile card (after the `ProfileDetail` TextBlock):

```xml
          <ui:Button Content="Activate" Margin="0,6,0,0" Padding="10,4"
                     HorizontalAlignment="Left" Click="OnActivateProfile"
                     Visibility="{Binding CanActivate, Converter={StaticResource BoolToVis}}"/>
```

(Themed by WPF-UI's accent automatically; no literal colors. `BoolToVis` is the app-wide converter in DesignSystem.xaml.)

- [ ] **Step 4: Build + full suite** — green, count unchanged from Task 2.
- [ ] **Step 5: CLAUDE.md** — in the `Ui/Dashboard/` architecture bullet: replace the `ProfileLiveness` sentence (the "pure comparer" description) with one line: the Profile card reads the active slot directly (`0x05/0x84`) on open/refresh and offers Activate (`0x05/0x04`, write-on-action); `ProfileLiveness` deleted. Also update the Conventions test-list (`ProfileLiveness` entry → remove).
- [ ] **Step 6: Commit** — `feat(dashboard): Activate button + direct-read wiring; bottom-button step retired` + trailers.
- [ ] **Step 7: Install** (`.\scripts\install.ps1`) and verify the gates (idle CPU ~0%, working set settles to baseline) plus a live look at the card: Live state when the mouse is on the app slot; switch slots with the bottom button → ↻ → NotLive + Activate appears; Activate → card returns to Live and the LED changes.

## Self-review (at write time)

- Spec §13 coverage: device+monitor (Task 1), state mapping + retirement (Task 2), triggers/Activate/visible failure/XAML/no-polling (Task 3). Popup untouched ✓. Perf constraints repeated in Global Constraints ✓.
- No placeholders: all code inline; the two "read it first / mirror" directives target existing idioms the implementer must match, with the pattern named exactly (SetDpiAsync ack, LivenessRefreshRequested forwarding, test fixture helpers).
- Type consistency: `SetActiveSlot(byte?)`, `CanActivate`, `SetProfileNote(string)`, `GetActiveProfileAsync() → Task<byte?>`, `SetActiveProfileAsync(byte) → Task<bool>` used identically across tasks.
