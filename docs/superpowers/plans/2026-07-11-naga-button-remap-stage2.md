# Phase B Stage 2 — Resident Button Remap (re-apply model) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user bind any of the 12 thumb-grid buttons to a keyboard key (+modifiers) or disable
it, from a new Settings-window "Buttons" section — persisted in `settings.json` and re-asserted on
connect (the §6-decided **re-apply model**; the mouse's onboard profiles are never written).

**Architecture:** All writes target the **volatile direct profile (0x00)** via the spike-proven
`0x02/0x0c` command and are re-asserted on startup + device-change (existing debounced path — no new
timers). The button model + `NagaV2ProButtons` id table live in `Hid/`; `RazerDevice` gains raw
get/set-button over the existing `ExchangeAsync`; `BatteryMonitor` gains locked pass-throughs + a batch
`ApplyRemapsAsync`; `AppSettings` gains a position-keyed table whose entries also carry a **stock-action
snapshot** (read before the first-ever write of each button) so "Default" restores instantly. UI is an
MVP 12-row list with key capture, per the spec.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), C#, WPF + WPF-UI 4.3.0 (`CardExpander`,
`ui:Button`), System.Text.Json, xUnit via `FakeRazerDevice`.

**Spec:** `docs/superpowers/specs/2026-06-21-naga-button-remap-design.md` §5.3–§5.5, §6 (PASS results:
grid ids `0x40..0x4b`; re-apply model), §7–§9. Stage 1 is complete and hardware-verified.

## Global Constraints

- **HARD GATING (spec §3.1):** one passive `HidD_SetFeature` per changed button on the control endpoint;
  never claim the input collection; **no new background timers/threads** (re-apply piggybacks the
  existing startup + `DeviceChangeWatcher` debounced refresh); all HID I/O off the UI thread via
  `Task.Run`, serialized on the **existing `BatteryMonitor._readLock`**; writes are user-action-triggered
  or connect-time re-apply only. **With no remaps configured, behaviour is byte-for-byte identical to
  today** (empty table ⇒ zero device calls).
- **Onboard-risk rule (user requirement):** Stage 2 **never writes onboard profiles** — volatile profile
  `0x00` only. No profile create/delete/list calls in the resident app.
- **Protocol (hardware-verified §6):** grid ids `0x40..0x4b` (position 1→12); SET `0x02/0x0c`, GET
  `0x02/0x8c`, data_size `0x0a`, args `[profile, buttonId, hypershift=0x00, category, dataLen, d0..d4]`;
  categories `0x00` disabled / `0x02` keyboard (`data=[modifierBitmask, hidUsage]`); modifier bits
  `0x01 LCtrl, 0x02 LShift, 0x04 LAlt, 0x08 LGUI`; tid auto-resolved (`tid != 0` gating as battery/DPI).
- **SET acks:** validate **status only** (`reply[1] == 0x02`, the spike-proven check); correctness comes
  from the read-back verify. GET replies get the full `ParseButtonReply` echo+CRC guard.
- **Testing boundary (spec §9):** logic layers TDD'd through `FakeRazerDevice`; `RazerDevice` HID
  transport, WPF window, and tray remain manual (installed build + probes).
- **Build/test (user-local SDK):** build `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`,
  test `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test` (single suite: append
  `--filter "FullyQualifiedName~<ClassName>"`).
- **No new dependencies. Conventional commits. Surgical changes.** Tests reach `internal` via
  `InternalsVisibleTo.cs` (everything added here is `public`, matching existing style).

## File structure

```
Task 1  src/NagaBatteryTray/Hid/ButtonBinding.cs        ButtonActionKind, ButtonBinding(+ToWire), RawButtonAction, NagaV2ProButtons
        tests/.../ButtonBindingTests.cs                 wire-format + id-table tests
        docs/.../2026-06-21-naga-button-remap-design.md §5.3/§5.4/§7 refinements (raw seam, stock snapshot)
Task 2  src/NagaBatteryTray/Hid/IRazerDevice.cs         + SetButtonAsync(raw)/GetButtonAsync
        src/NagaBatteryTray/Hid/RazerDevice.cs          implement over ExchangeAsync
        tests/.../Fakes/FakeRazerDevice.cs              write log + canned reads
Task 3  src/NagaBatteryTray/Monitoring/BatteryMonitor.cs + locked pass-throughs + ApplyRemapsAsync
        tests/.../BatteryMonitorTests.cs                routing/batch/empty/skip tests
Task 4  src/NagaBatteryTray/Settings/ButtonBindingSetting.cs  persisted entry (binding + stock snapshot)
        src/NagaBatteryTray/Settings/AppSettings.cs     + ButtonBindings table
        tests/.../SettingsStoreTests.cs                 round-trip + back-compat tests
Task 5  src/NagaBatteryTray/Ui/KeyToHidUsage.cs         WPF Key ↔ HID usage + display names
        tests/.../KeyToHidUsageTests.cs
Task 6  src/NagaBatteryTray/Ui/ButtonRowViewModel.cs    row VM + ButtonOp (pending-change model)
        src/NagaBatteryTray/Ui/SettingsViewModel.cs     + Buttons rows, GetPendingButtonOps, ButtonsStatus
        tests/.../ButtonRowViewModelTests.cs, tests/.../SettingsViewModelTests.cs
Task 7  src/NagaBatteryTray/Ui/SettingsWindow.xaml(.cs) "Buttons" CardExpander, key capture, apply event
        src/NagaBatteryTray/AppHost.cs                  apply orchestration + re-apply on startup/device-change
Task 8  hardware acceptance (user) + CLAUDE.md/README docs + §3.1 gates
```

---

### Task 1: Button model — `ButtonBinding`, `RawButtonAction`, `NagaV2ProButtons` (TDD)

**Files:**
- Create: `src/NagaBatteryTray/Hid/ButtonBinding.cs`
- Test: `tests/NagaBatteryTray.Tests/ButtonBindingTests.cs` (new file)
- Modify: `docs/superpowers/specs/2026-06-21-naga-button-remap-design.md` (3 small refinements, step 5)

**Interfaces:**
- Consumes: `RazerProtocol.FnDisabled`/`FnKeyboard` (existing).
- Produces (all later tasks rely on these exact names):
  `public enum ButtonActionKind { Default, Disabled, Key }`;
  `public readonly record struct RawButtonAction(byte Category, byte[] Data)`;
  `public readonly record struct ButtonBinding(byte ButtonId, ButtonActionKind Kind, byte Modifiers, byte HidUsage)` with `public (byte Category, byte[] Data) ToWire()` (throws `InvalidOperationException` on `Default`);
  `public static class NagaV2ProButtons { public const int Count = 12; public static byte IdForPosition(int position); }`.

- [ ] **Step 1: Write the failing tests**

Create `tests/NagaBatteryTray.Tests/ButtonBindingTests.cs`:

```csharp
using NagaBatteryTray.Hid;
using Xunit;

public class ButtonBindingTests
{
    [Fact]
    public void ToWire_key_binding_yields_keyboard_category_and_payload()
    {
        var b = new ButtonBinding(0x40, ButtonActionKind.Key, Modifiers: 0x01, HidUsage: 0x06); // Ctrl+C
        var (category, data) = b.ToWire();
        Assert.Equal(RazerProtocol.FnKeyboard, category);
        Assert.Equal(new byte[] { 0x01, 0x06 }, data);
    }

    [Fact]
    public void ToWire_disabled_yields_disabled_category_and_empty_payload()
    {
        var b = new ButtonBinding(0x41, ButtonActionKind.Disabled, 0, 0);
        var (category, data) = b.ToWire();
        Assert.Equal(RazerProtocol.FnDisabled, category);
        Assert.Empty(data);
    }

    [Fact]
    public void ToWire_default_throws()
    {
        // a Default binding is a marker (drop from the table) and must never reach the device
        var b = new ButtonBinding(0x40, ButtonActionKind.Default, 0, 0);
        Assert.Throws<InvalidOperationException>(() => b.ToWire());
    }

    [Theory]
    [InlineData(1, 0x40)]
    [InlineData(12, 0x4b)]
    public void IdForPosition_maps_grid_position_to_firmware_id(int position, byte expected)
    {
        Assert.Equal(expected, NagaV2ProButtons.IdForPosition(position));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void IdForPosition_rejects_out_of_range(int position)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NagaV2ProButtons.IdForPosition(position));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~ButtonBindingTests"`
Expected: **build error** — `ButtonBinding`/`ButtonActionKind`/`NagaV2ProButtons` not defined.

- [ ] **Step 3: Implement**

Create `src/NagaBatteryTray/Hid/ButtonBinding.cs`:

```csharp
namespace NagaBatteryTray.Hid;

public enum ButtonActionKind { Default, Disabled, Key }

/// <summary>A grid button's raw onboard action as read from the device (category + data bytes).
/// Round-trips categories the app doesn't model (mouse, DPI-stage, …) — Default-restore needs that.</summary>
public readonly record struct RawButtonAction(byte Category, byte[] Data);

/// <summary>One thumb-grid button binding. Kind=Default is a marker (absent from the remap table);
/// it is never written to the device.</summary>
public readonly record struct ButtonBinding(byte ButtonId, ButtonActionKind Kind, byte Modifiers, byte HidUsage)
{
    /// <summary>Wire form for the 0x02/0x0c SET (spec §5.1). Throws on Default — an untouched/default
    /// button must never be written (§3.1 discipline).</summary>
    public (byte Category, byte[] Data) ToWire() => Kind switch
    {
        ButtonActionKind.Disabled => (RazerProtocol.FnDisabled, Array.Empty<byte>()),
        ButtonActionKind.Key => (RazerProtocol.FnKeyboard, new[] { Modifiers, HidUsage }),
        _ => throw new InvalidOperationException("Default bindings are never written to the device."),
    };
}

/// <summary>Naga V2 Pro thumb grid: 12 buttons, firmware ids 0x40..0x4b contiguous in physical order
/// (hardware-verified 2026-07-11; spec §6).</summary>
public static class NagaV2ProButtons
{
    public const int Count = 12;

    public static byte IdForPosition(int position) =>
        position is >= 1 and <= Count
            ? (byte)(0x3f + position)
            : throw new ArgumentOutOfRangeException(nameof(position));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~ButtonBindingTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Refine the spec to match the raw seam + stock snapshot**

In `docs/superpowers/specs/2026-06-21-naga-button-remap-design.md`, three edits:

Replace:
```
- `IRazerDevice` gains `Task<bool> SetButtonAsync(ButtonBinding b, CancellationToken ct)` and (for verify)
  `Task<ButtonBinding?> GetButtonAsync(byte buttonId, CancellationToken ct)`. `FakeRazerDevice` implements
  both with assertion fields (last write, call counts) — the unit seam, mirroring the DPI fakes.
```
with:
```
- `IRazerDevice` gains `Task<bool> SetButtonAsync(byte buttonId, byte category, byte[] data, CancellationToken ct)`
  and `Task<RawButtonAction?> GetButtonAsync(byte buttonId, CancellationToken ct)` — **raw category+data
  both ways**: a Key-only `ButtonBinding` cannot carry the stock mouse-category actions that
  Default-restore and read-back need. `FakeRazerDevice` implements both with assertion fields (write
  log, canned reads) — the unit seam, mirroring the DPI fakes.
```

Replace:
```
- `ButtonBinding` (a small value type: `buttonId`, `ActionKind { Default, Disabled, Key }`, `modifiers`,
  `hidUsage`) and a 12-entry `RemapTable` keyed by grid position. Persisted in `settings.json` (Roaming),
  corrupt/missing → defaults, exactly as today. The discovered `NagaV2ProButtons` ID table is a baked-in
  constant (filled from §6), so the stored table is position-indexed and firmware-id-stable.
```
with:
```
- `ButtonBinding` (a small value type: `buttonId`, `ActionKind { Default, Disabled, Key }`, `modifiers`,
  `hidUsage`) and a sparse `RemapTable` keyed by grid position (only non-Default buttons are stored).
  Each stored entry also carries a **stock-action snapshot** (`category`+`data`, read from the direct
  profile **before that button's first-ever write**) so "Default" can restore instantly and offline.
  Persisted in `settings.json` (Roaming), corrupt/missing → defaults, exactly as today. The discovered
  `NagaV2ProButtons` ID table is a baked-in constant (`0x40..0x4b`, §6), so the stored table is
  position-indexed and firmware-id-stable.
```

Replace:
```
- **Unknown/again-default button:** "Default" rewrites the stock action recorded in §6; a never-touched
  button is never written (flash-wear discipline).
```
with:
```
- **Unknown/again-default button:** "Default" rewrites the button's **stock action snapshotted at its
  first-ever remap** (read from the direct profile before our first write; persisted beside the binding);
  if that snapshot read failed, Default = drop from the table + effective on next reconnect. A
  never-touched button is never written (flash-wear/§3.1 discipline).
```

- [ ] **Step 6: Commit**

```powershell
git add src/NagaBatteryTray/Hid/ButtonBinding.cs tests/NagaBatteryTray.Tests/ButtonBindingTests.cs docs/superpowers/specs/2026-06-21-naga-button-remap-design.md
git commit -m "feat(hid): button binding model + NagaV2ProButtons id table (spec: raw seam, stock snapshot)"
```

---

### Task 2: Device seam — raw `SetButtonAsync`/`GetButtonAsync`

**Files:**
- Modify: `src/NagaBatteryTray/Hid/IRazerDevice.cs`
- Modify: `src/NagaBatteryTray/Hid/RazerDevice.cs` (after `SetDpiAsync`)
- Modify: `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs`

**Interfaces:**
- Consumes: Task 1 `RawButtonAction`; existing `EnsureConnectedAsync`, `ExchangeAsync`, `CloseHandle`, `LogOnce`; `RazerProtocol.BuildSetButtonBuffer`/`BuildGetButtonBuffer`/`ParseButtonReply`/`ButtonProfileDirect`.
- Produces:
  `IRazerDevice`: `Task<bool> SetButtonAsync(byte buttonId, byte category, byte[] data, CancellationToken ct)`; `Task<RawButtonAction?> GetButtonAsync(byte buttonId, CancellationToken ct)`.
  `FakeRazerDevice`: `public sealed record ButtonWrite(byte ButtonId, byte Category, byte[] Data);` `public List<ButtonWrite> ButtonWrites { get; }` (every write, in order); `public bool SetButtonResult { get; set; } = true;` `public Dictionary<byte, RawButtonAction> ButtonActions { get; }` (canned GET results).

This task has no new unit tests of its own (`RazerDevice` is the manual HID boundary; the fake is
exercised by Task 3's tests) — its gate is a clean build with the existing suite green.

- [ ] **Step 1: Extend the interface**

In `src/NagaBatteryTray/Hid/IRazerDevice.cs`, after `SetDpiAsync`:

```csharp
    /// <summary>Write one raw button action (category + data) to the VOLATILE direct profile (0x00).
    /// Never touches onboard profiles. True = the firmware acked the SET (status 0x02).</summary>
    Task<bool> SetButtonAsync(byte buttonId, byte category, byte[] data, CancellationToken ct);

    /// <summary>Read a button's current effective action from the direct profile. Null = unreachable
    /// or invalid reply.</summary>
    Task<RawButtonAction?> GetButtonAsync(byte buttonId, CancellationToken ct);
```

- [ ] **Step 2: Implement in `RazerDevice`**

In `src/NagaBatteryTray/Hid/RazerDevice.cs`, after `SetDpiAsync`:

```csharp
    public async Task<bool> SetButtonAsync(byte buttonId, byte category, byte[] data, CancellationToken ct)
    {
        try
        {
            byte tid = await EnsureConnectedAsync(ct);
            if (tid == 0) return false;
            var reply = await ExchangeAsync(RazerProtocol.BuildSetButtonBuffer(
                tid, RazerProtocol.ButtonProfileDirect, buttonId, 0x00, category, data), ct);
            // SET ack: status-only (the spike-proven check); correctness is covered by read-back verify.
            return reply is not null && reply[1] == 0x02;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseHandle();
            LogOnce(ex);
            return false;
        }
    }

    public async Task<RawButtonAction?> GetButtonAsync(byte buttonId, CancellationToken ct)
    {
        try
        {
            byte tid = await EnsureConnectedAsync(ct);
            if (tid == 0) return null;
            var reply = await ExchangeAsync(RazerProtocol.BuildGetButtonBuffer(
                tid, RazerProtocol.ButtonProfileDirect, buttonId, 0x00), ct);
            if (reply is null) return null;
            if (RazerProtocol.ParseButtonReply(reply, RazerProtocol.ButtonProfileDirect, buttonId, 0x00,
                    out byte category, out byte[] data) != ReplyResult.Success)
                return null;
            return new RawButtonAction(category, data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseHandle();
            LogOnce(ex);
            return null;
        }
    }
```

- [ ] **Step 3: Extend the fake**

In `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs`, after the DPI members:

```csharp
    public sealed record ButtonWrite(byte ButtonId, byte Category, byte[] Data);
    public List<ButtonWrite> ButtonWrites { get; } = new();
    public bool SetButtonResult { get; set; } = true;
    public Dictionary<byte, RawButtonAction> ButtonActions { get; } = new();

    public Task<bool> SetButtonAsync(byte buttonId, byte category, byte[] data, CancellationToken ct)
    {
        ButtonWrites.Add(new ButtonWrite(buttonId, category, data));
        return Task.FromResult(SetButtonResult);
    }

    public Task<RawButtonAction?> GetButtonAsync(byte buttonId, CancellationToken ct) =>
        Task.FromResult(ButtonActions.TryGetValue(buttonId, out var a) ? a : (RawButtonAction?)null);
```

- [ ] **Step 4: Build + full suite green**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build` then `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: Build succeeded, 0 errors; all tests PASS (57 = 50 + 7 from Task 1).

- [ ] **Step 5: Commit**

```powershell
git add src/NagaBatteryTray/Hid/IRazerDevice.cs src/NagaBatteryTray/Hid/RazerDevice.cs tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs
git commit -m "feat(hid): raw get/set-button device seam over the existing exchange"
```

---

### Task 3: `BatteryMonitor` — locked pass-throughs + `ApplyRemapsAsync` (TDD)

**Files:**
- Modify: `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs` (after `SetDpiAsync`)
- Test: `tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs` (append)

**Interfaces:**
- Consumes: Task 1 `ButtonBinding`/`RawButtonAction`, Task 2 seam + fake fields.
- Produces:
  `public Task<bool> SetButtonAsync(byte buttonId, byte category, byte[] data)`;
  `public Task<RawButtonAction?> GetButtonAsync(byte buttonId)`;
  `public Task ApplyRemapsAsync(IReadOnlyList<ButtonBinding> bindings)` — batch under **one** `_readLock` hold; empty list ⇒ zero device calls; `Default` entries skipped defensively; individual failures ignored (best-effort; the next connect retries).

- [ ] **Step 1: Write the failing tests**

Append to `tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs`:

```csharp
    [Fact]
    public async Task SetButtonAsync_routes_raw_bytes_to_device_and_returns_result()
    {
        var fake = new FakeRazerDevice { SetButtonResult = true };
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        bool ok = await m.SetButtonAsync(0x40, 0x02, new byte[] { 0x01, 0x06 });
        Assert.True(ok);
        var w = Assert.Single(fake.ButtonWrites);
        Assert.Equal(0x40, w.ButtonId);
        Assert.Equal(0x02, w.Category);
        Assert.Equal(new byte[] { 0x01, 0x06 }, w.Data);
    }

    [Fact]
    public async Task GetButtonAsync_returns_device_value_or_null()
    {
        var fake = new FakeRazerDevice();
        fake.ButtonActions[0x40] = new RawButtonAction(0x02, new byte[] { 0x00, 0x3d });
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        var hit = await m.GetButtonAsync(0x40);
        Assert.Equal(new RawButtonAction(0x02, fake.ButtonActions[0x40].Data), hit);
        Assert.Null(await m.GetButtonAsync(0x41));
    }

    [Fact]
    public async Task ApplyRemapsAsync_writes_every_binding_in_wire_form()
    {
        var fake = new FakeRazerDevice();
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        await m.ApplyRemapsAsync(new[]
        {
            new ButtonBinding(0x40, ButtonActionKind.Key, 0x01, 0x06),  // Ctrl+C
            new ButtonBinding(0x41, ButtonActionKind.Disabled, 0, 0),
        });
        Assert.Equal(2, fake.ButtonWrites.Count);
        Assert.Equal(new byte[] { 0x01, 0x06 }, fake.ButtonWrites[0].Data);
        Assert.Equal(RazerProtocol.FnKeyboard, fake.ButtonWrites[0].Category);
        Assert.Equal(RazerProtocol.FnDisabled, fake.ButtonWrites[1].Category);
        Assert.Empty(fake.ButtonWrites[1].Data);
    }

    [Fact]
    public async Task ApplyRemapsAsync_empty_table_makes_zero_device_calls()
    {
        // protects the no-extra-I/O invariant: no remaps configured => byte-for-byte today's behaviour
        var fake = new FakeRazerDevice();
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        await m.ApplyRemapsAsync(Array.Empty<ButtonBinding>());
        Assert.Empty(fake.ButtonWrites);
    }

    [Fact]
    public async Task ApplyRemapsAsync_skips_default_entries()
    {
        var fake = new FakeRazerDevice();
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        await m.ApplyRemapsAsync(new[] { new ButtonBinding(0x40, ButtonActionKind.Default, 0, 0) });
        Assert.Empty(fake.ButtonWrites);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~BatteryMonitorTests"`
Expected: **build error** — `BatteryMonitor` lacks the three methods.

- [ ] **Step 3: Implement**

In `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs`, after `SetDpiAsync`:

```csharp
    /// <summary>Write one raw button action to the volatile direct profile. Blocks for the read lock
    /// (serializes against battery poll + DPI on the single HID handle).</summary>
    public async Task<bool> SetButtonAsync(byte buttonId, byte category, byte[] data)
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return false; }
        try { return await _device.SetButtonAsync(buttonId, category, data, _cts.Token); }
        catch (OperationCanceledException) { return false; }
        finally { _readLock.Release(); }
    }

    /// <summary>Read a button's current effective action from the direct profile. Blocks for the read lock.</summary>
    public async Task<RawButtonAction?> GetButtonAsync(byte buttonId)
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return null; }
        try { return await _device.GetButtonAsync(buttonId, _cts.Token); }
        catch (OperationCanceledException) { return null; }
        finally { _readLock.Release(); }
    }

    /// <summary>Re-assert the configured remaps (connect-time re-apply model, spec §5.3). Best-effort
    /// batch under one lock hold — an individual failure is retried at the next connect, not here. An
    /// empty table makes zero device calls; Default entries are skipped (never written).</summary>
    public async Task ApplyRemapsAsync(IReadOnlyList<ButtonBinding> bindings)
    {
        if (bindings.Count == 0) return;
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            foreach (var b in bindings)
            {
                if (b.Kind == ButtonActionKind.Default) continue;
                var (category, data) = b.ToWire();
                await _device.SetButtonAsync(b.ButtonId, category, data, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally { _readLock.Release(); }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~BatteryMonitorTests"`
Expected: PASS (13 = 8 existing + 5 new).

- [ ] **Step 5: Commit**

```powershell
git add src/NagaBatteryTray/Monitoring/BatteryMonitor.cs tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs
git commit -m "feat(monitor): button pass-throughs + batch re-apply under the shared read lock"
```

---

### Task 4: Settings — `ButtonBindingSetting` + `AppSettings.ButtonBindings` (TDD)

**Files:**
- Create: `src/NagaBatteryTray/Settings/ButtonBindingSetting.cs`
- Modify: `src/NagaBatteryTray/Settings/AppSettings.cs`
- Test: `tests/NagaBatteryTray.Tests/SettingsStoreTests.cs` (append)
- Modify: `docs/superpowers/specs/2026-06-21-naga-button-remap-design.md` (file-structure line, step 5)

**Interfaces:**
- Consumes: Task 1 `ButtonActionKind`.
- Produces:
  `public sealed class ButtonBindingSetting { ButtonActionKind Kind; byte Modifiers; byte HidUsage; byte StockCategory; byte[] StockData; bool HasStock; }` (Kind serialized as a string);
  `AppSettings.ButtonBindings : Dictionary<int, ButtonBindingSetting>` (keyed by grid position 1..12, **non-null, default empty**; a missing/old settings.json loads as empty).

- [ ] **Step 1: Write the failing tests**

Append to `tests/NagaBatteryTray.Tests/SettingsStoreTests.cs`:

```csharp
    [Fact]
    public void ButtonBindings_default_is_empty_and_old_files_load_without_the_field()
    {
        var path = TempFile();
        File.WriteAllText(path, """{ "PollIntervalSeconds": 60 }"""); // pre-Stage-2 settings file
        var store = new JsonSettingsStore(path);
        Assert.NotNull(store.Settings.ButtonBindings);
        Assert.Empty(store.Settings.ButtonBindings);
    }

    [Fact]
    public void ButtonBindings_round_trip_through_save_and_reload()
    {
        var path = TempFile();
        var store = new JsonSettingsStore(path);
        store.Settings.ButtonBindings[1] = new ButtonBindingSetting
        {
            Kind = NagaBatteryTray.Hid.ButtonActionKind.Key,
            Modifiers = 0x01,
            HidUsage = 0x06, // Ctrl+C
            StockCategory = 0x02,
            StockData = new byte[] { 0x00, 0x3a }, // stock was F1
            HasStock = true,
        };
        store.Settings.ButtonBindings[5] = new ButtonBindingSetting
        {
            Kind = NagaBatteryTray.Hid.ButtonActionKind.Disabled,
        };
        store.Save();

        var reloaded = new JsonSettingsStore(path);
        Assert.Equal(2, reloaded.Settings.ButtonBindings.Count);
        var b1 = reloaded.Settings.ButtonBindings[1];
        Assert.Equal(NagaBatteryTray.Hid.ButtonActionKind.Key, b1.Kind);
        Assert.Equal(0x01, b1.Modifiers);
        Assert.Equal(0x06, b1.HidUsage);
        Assert.True(b1.HasStock);
        Assert.Equal(new byte[] { 0x00, 0x3a }, b1.StockData);
        Assert.Equal(NagaBatteryTray.Hid.ButtonActionKind.Disabled, reloaded.Settings.ButtonBindings[5].Kind);
        Assert.Contains("\"Kind\": \"Key\"", File.ReadAllText(path)); // enum stored as a readable string
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: **build error** — `ButtonBindingSetting`/`ButtonBindings` not defined.

- [ ] **Step 3: Implement**

Create `src/NagaBatteryTray/Settings/ButtonBindingSetting.cs`:

```csharp
using System.Text.Json.Serialization;
using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Settings;

/// <summary>One remapped grid button as persisted in settings.json (keyed by grid position 1..12 in
/// <see cref="AppSettings.ButtonBindings"/>; a button absent from the table is Default and is never
/// written). Stock* holds the action read from the direct profile before this button's first-ever
/// write, so "Default" restores instantly; HasStock=false means that read failed (Default then takes
/// effect at the next reconnect instead).</summary>
public sealed class ButtonBindingSetting
{
    [JsonConverter(typeof(JsonStringEnumConverter<ButtonActionKind>))]
    public ButtonActionKind Kind { get; set; } = ButtonActionKind.Key; // Key | Disabled (never Default)
    public byte Modifiers { get; set; }
    public byte HidUsage { get; set; }
    public byte StockCategory { get; set; }
    public byte[] StockData { get; set; } = Array.Empty<byte>();
    public bool HasStock { get; set; }
}
```

In `src/NagaBatteryTray/Settings/AppSettings.cs`, add after `SetReadDelayMs`:

```csharp
    /// <summary>Thumb-grid remaps keyed by grid position (1..12); sparse — only non-Default buttons.</summary>
    public Dictionary<int, ButtonBindingSetting> ButtonBindings { get; set; } = new();
```

(`AppSettings.cs` needs `using System.Collections.Generic;` only if implicit usings were off — they are
on in this project, so no using is required.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: PASS (5 = 3 existing + 2 new).

- [ ] **Step 5: Fix the spec's test-file name**

In the spec's file-structure block, replace:
```
  tests/NagaBatteryTray.Tests/JsonSettingsStoreTests.cs  RemapTable persistence round-trip + corrupt-JSON → defaults
```
with:
```
  tests/NagaBatteryTray.Tests/SettingsStoreTests.cs      RemapTable persistence round-trip + corrupt-JSON → defaults (existing store-test file)
```

- [ ] **Step 6: Commit**

```powershell
git add src/NagaBatteryTray/Settings/ButtonBindingSetting.cs src/NagaBatteryTray/Settings/AppSettings.cs tests/NagaBatteryTray.Tests/SettingsStoreTests.cs docs/superpowers/specs/2026-06-21-naga-button-remap-design.md
git commit -m "feat(settings): sparse button remap table with per-entry stock snapshot"
```

---

### Task 5: `KeyToHidUsage` — WPF Key ↔ HID usage map (TDD)

**Files:**
- Create: `src/NagaBatteryTray/Ui/KeyToHidUsage.cs`
- Test: `tests/NagaBatteryTray.Tests/KeyToHidUsageTests.cs` (new file)

**Interfaces:**
- Consumes: `System.Windows.Input.Key`/`ModifierKeys` (WPF, already referenced).
- Produces:
  `public static class KeyToHidUsage` with
  `public const byte ModCtrl = 0x01, ModShift = 0x02, ModAlt = 0x04, ModWin = 0x08;`
  `public static bool TryGetUsage(Key key, out byte usage)`;
  `public static byte ToModifierBits(ModifierKeys mods)`;
  `public static string Describe(byte modifiers, byte usage)` — e.g. `"Ctrl+Shift+F5"`; an unmapped usage renders as hex (`"0x68"`).

- [ ] **Step 1: Write the failing tests**

Create `tests/NagaBatteryTray.Tests/KeyToHidUsageTests.cs`:

```csharp
using System.Windows.Input;
using NagaBatteryTray.Ui;
using Xunit;

public class KeyToHidUsageTests
{
    [Theory]
    [InlineData(Key.A, 0x04)]
    [InlineData(Key.Z, 0x1d)]
    [InlineData(Key.D1, 0x1e)]
    [InlineData(Key.D0, 0x27)]
    [InlineData(Key.F1, 0x3a)]
    [InlineData(Key.F12, 0x45)]
    [InlineData(Key.F13, 0x68)]
    [InlineData(Key.F24, 0x73)]
    [InlineData(Key.Enter, 0x28)]
    [InlineData(Key.Space, 0x2c)]
    [InlineData(Key.OemMinus, 0x2d)]
    [InlineData(Key.Home, 0x4a)]
    [InlineData(Key.Up, 0x52)]
    public void TryGetUsage_maps_supported_keys(Key key, byte expected)
    {
        Assert.True(KeyToHidUsage.TryGetUsage(key, out byte usage));
        Assert.Equal(expected, usage);
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.LWin)]
    [InlineData(Key.ImeConvert)]
    public void TryGetUsage_rejects_unsupported_keys(Key key)
    {
        Assert.False(KeyToHidUsage.TryGetUsage(key, out _));
    }

    [Fact]
    public void ToModifierBits_maps_wpf_modifiers_to_hid_bits()
    {
        Assert.Equal(0x00, KeyToHidUsage.ToModifierBits(ModifierKeys.None));
        Assert.Equal(0x01, KeyToHidUsage.ToModifierBits(ModifierKeys.Control));
        Assert.Equal(0x07, KeyToHidUsage.ToModifierBits(
            ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt));
        Assert.Equal(0x08, KeyToHidUsage.ToModifierBits(ModifierKeys.Windows));
    }

    [Theory]
    [InlineData(0x00, 0x06, "C")]
    [InlineData(0x01, 0x06, "Ctrl+C")]
    [InlineData(0x07, 0x3e, "Ctrl+Shift+Alt+F5")]
    [InlineData(0x08, 0x2c, "Win+Space")]
    public void Describe_formats_modifiers_and_key_name(byte mods, byte usage, string expected)
    {
        Assert.Equal(expected, KeyToHidUsage.Describe(mods, usage));
    }

    [Fact]
    public void Describe_unknown_usage_renders_hex()
    {
        Assert.Equal("0x74", KeyToHidUsage.Describe(0x00, 0x74));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~KeyToHidUsageTests"`
Expected: **build error** — `KeyToHidUsage` not defined. (If instead `Key`/`ModifierKeys` fail to
resolve in the test project, add `<UseWPF>true</UseWPF>` to the `PropertyGroup` of
`tests/NagaBatteryTray.Tests/NagaBatteryTray.Tests.csproj` — the WPF framework reference usually flows
transitively from the app project, but this pins it.)

- [ ] **Step 3: Implement**

Create `src/NagaBatteryTray/Ui/KeyToHidUsage.cs`:

```csharp
using System.Windows.Input;

namespace NagaBatteryTray.Ui;

/// <summary>WPF <see cref="Key"/> ↔ USB HID keyboard usage (HUT 1.5 ch. 10) for the remap MVP, plus the
/// left-side modifier bits and display formatting. Only keys in this table can be captured; anything
/// else is rejected at capture time.</summary>
public static class KeyToHidUsage
{
    public const byte ModCtrl = 0x01, ModShift = 0x02, ModAlt = 0x04, ModWin = 0x08;

    // Single source for both lookup directions (Key -> usage, usage -> display name).
    private static readonly (Key Key, byte Usage, string Name)[] Map = BuildMap();

    private static (Key, byte, string)[] BuildMap()
    {
        var list = new List<(Key, byte, string)>();
        for (int i = 0; i < 26; i++) list.Add((Key.A + i, (byte)(0x04 + i), ((char)('A' + i)).ToString()));
        for (int i = 0; i < 9; i++) list.Add((Key.D1 + i, (byte)(0x1e + i), ((char)('1' + i)).ToString()));
        list.Add((Key.D0, 0x27, "0"));
        for (int i = 0; i < 12; i++) list.Add((Key.F1 + i, (byte)(0x3a + i), $"F{1 + i}"));
        for (int i = 0; i < 12; i++) list.Add((Key.F13 + i, (byte)(0x68 + i), $"F{13 + i}"));
        list.AddRange(new (Key, byte, string)[]
        {
            (Key.Enter, 0x28, "Enter"), (Key.Escape, 0x29, "Esc"), (Key.Back, 0x2a, "Backspace"),
            (Key.Tab, 0x2b, "Tab"), (Key.Space, 0x2c, "Space"),
            (Key.OemMinus, 0x2d, "-"), (Key.OemPlus, 0x2e, "="),
            (Key.OemOpenBrackets, 0x2f, "["), (Key.OemCloseBrackets, 0x30, "]"),
            (Key.OemPipe, 0x31, "\\"), (Key.OemSemicolon, 0x33, ";"), (Key.OemQuotes, 0x34, "'"),
            (Key.OemTilde, 0x35, "`"), (Key.OemComma, 0x36, ","), (Key.OemPeriod, 0x37, "."),
            (Key.OemQuestion, 0x38, "/"),
            (Key.PrintScreen, 0x46, "PrtSc"), (Key.Scroll, 0x47, "ScrollLock"), (Key.Pause, 0x48, "Pause"),
            (Key.Insert, 0x49, "Insert"), (Key.Home, 0x4a, "Home"), (Key.PageUp, 0x4b, "PgUp"),
            (Key.Delete, 0x4c, "Delete"), (Key.End, 0x4d, "End"), (Key.PageDown, 0x4e, "PgDn"),
            (Key.Right, 0x4f, "Right"), (Key.Left, 0x50, "Left"), (Key.Down, 0x51, "Down"), (Key.Up, 0x52, "Up"),
        });
        return list.ToArray();
    }

    public static bool TryGetUsage(Key key, out byte usage)
    {
        foreach (var (k, u, _) in Map)
            if (k == key) { usage = u; return true; }
        usage = 0;
        return false;
    }

    public static byte ToModifierBits(ModifierKeys mods) => (byte)(
        ((mods & ModifierKeys.Control) != 0 ? ModCtrl : 0) |
        ((mods & ModifierKeys.Shift) != 0 ? ModShift : 0) |
        ((mods & ModifierKeys.Alt) != 0 ? ModAlt : 0) |
        ((mods & ModifierKeys.Windows) != 0 ? ModWin : 0));

    /// <summary>"Ctrl+Shift+F5"-style display text; an unmapped usage renders as hex.</summary>
    public static string Describe(byte modifiers, byte usage)
    {
        string name = $"0x{usage:x2}";
        foreach (var (_, u, n) in Map)
            if (u == usage) { name = n; break; }
        var parts = new List<string>(5);
        if ((modifiers & ModCtrl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(name);
        return string.Join("+", parts);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~KeyToHidUsageTests"`
Expected: PASS (22 test cases).

- [ ] **Step 5: Commit**

```powershell
git add src/NagaBatteryTray/Ui/KeyToHidUsage.cs tests/NagaBatteryTray.Tests/KeyToHidUsageTests.cs
git commit -m "feat(ui): WPF key to HID usage map with modifier bits + display names"
```

---

### Task 6: Row view-model + pending ops (TDD)

**Files:**
- Create: `src/NagaBatteryTray/Ui/ButtonRowViewModel.cs`
- Modify: `src/NagaBatteryTray/Ui/SettingsViewModel.cs`
- Test: `tests/NagaBatteryTray.Tests/ButtonRowViewModelTests.cs` (new), `tests/NagaBatteryTray.Tests/SettingsViewModelTests.cs` (append)

**Interfaces:**
- Consumes: Task 1 `ButtonActionKind`, Task 4 `ButtonBindingSetting`/`AppSettings.ButtonBindings`, Task 5 `KeyToHidUsage.Describe`.
- Produces:
  `public enum ButtonOpKind { Apply, RestoreDefault }`;
  `public readonly record struct ButtonOp(int Position, ButtonOpKind OpKind, ButtonActionKind Kind, byte Modifiers, byte HidUsage);`
  `public sealed class ButtonRowViewModel : INotifyPropertyChanged` — `int Position`, `string Label`, `string CurrentText`, `string Status`, `bool IsCapturing`, `void SetApplied(ButtonActionKind, byte, byte)`, `void StageKey(byte modifiers, byte usage)`, `void StageDisabled()`, `void StageDefault()`, `ButtonOp? ToOp()`, `void MarkApplied()`, `void MarkFailed(string)`;
  `SettingsViewModel`: `IReadOnlyList<ButtonRowViewModel> Buttons` (12 rows seeded from `AppSettings.ButtonBindings`), `List<ButtonOp> GetPendingButtonOps()`, `ButtonRowViewModel Row(int position)`, `string ButtonsStatus` (bindable).

- [ ] **Step 1: Write the failing tests**

Create `tests/NagaBatteryTray.Tests/ButtonRowViewModelTests.cs`:

```csharp
using NagaBatteryTray.Hid;
using NagaBatteryTray.Ui;
using Xunit;

public class ButtonRowViewModelTests
{
    [Fact]
    public void Untouched_row_produces_no_op_and_shows_default()
    {
        var row = new ButtonRowViewModel(3);
        Assert.Equal("Button 3", row.Label);
        Assert.Equal("Default", row.CurrentText);
        Assert.Null(row.ToOp());
    }

    [Fact]
    public void Staged_key_produces_apply_op_with_wire_bytes()
    {
        var row = new ButtonRowViewModel(1);
        row.StageKey(0x01, 0x06); // Ctrl+C
        var op = row.ToOp();
        Assert.NotNull(op);
        Assert.Equal(new ButtonOp(1, ButtonOpKind.Apply, ButtonActionKind.Key, 0x01, 0x06), op!.Value);
        Assert.Contains("Ctrl+C", row.CurrentText);
        Assert.Contains("pending", row.CurrentText);
    }

    [Fact]
    public void Staged_disabled_produces_apply_op()
    {
        var row = new ButtonRowViewModel(2);
        row.StageDisabled();
        Assert.Equal(new ButtonOp(2, ButtonOpKind.Apply, ButtonActionKind.Disabled, 0, 0), row.ToOp());
    }

    [Fact]
    public void Default_on_a_remapped_row_produces_restore_op()
    {
        var row = new ButtonRowViewModel(4);
        row.SetApplied(ButtonActionKind.Key, 0x01, 0x06);
        row.StageDefault();
        var op = row.ToOp();
        Assert.NotNull(op);
        Assert.Equal(ButtonOpKind.RestoreDefault, op!.Value.OpKind);
    }

    [Fact]
    public void Default_on_an_untouched_row_produces_no_op()
    {
        // an untouched button is never written (flash/§3.1 discipline)
        var row = new ButtonRowViewModel(4);
        row.StageDefault();
        Assert.Null(row.ToOp());
    }

    [Fact]
    public void Staging_back_to_the_applied_binding_produces_no_op()
    {
        var row = new ButtonRowViewModel(5);
        row.SetApplied(ButtonActionKind.Key, 0x01, 0x06);
        row.StageKey(0x01, 0x06); // same as applied
        Assert.Null(row.ToOp());
    }

    [Fact]
    public void MarkApplied_promotes_pending_to_applied()
    {
        var row = new ButtonRowViewModel(6);
        row.StageKey(0x00, 0x3a); // F1
        row.MarkApplied();
        Assert.Null(row.ToOp());
        Assert.Equal("F1", row.CurrentText);
        Assert.Equal("Applied", row.Status);
    }

    [Fact]
    public void MarkFailed_keeps_pending_and_sets_status()
    {
        var row = new ButtonRowViewModel(7);
        row.StageDisabled();
        row.MarkFailed("Not applied");
        Assert.NotNull(row.ToOp()); // still pending — user can retry
        Assert.Equal("Not applied", row.Status);
    }
}
```

Append to `tests/NagaBatteryTray.Tests/SettingsViewModelTests.cs`:

```csharp
    [Fact]
    public void Buttons_are_seeded_from_the_settings_table()
    {
        var settings = Sample();
        settings.ButtonBindings[2] = new ButtonBindingSetting
        {
            Kind = ButtonActionKind.Key, Modifiers = 0x01, HidUsage = 0x06,
        };
        settings.ButtonBindings[7] = new ButtonBindingSetting { Kind = ButtonActionKind.Disabled };
        var vm = new SettingsViewModel(settings, false);

        Assert.Equal(12, vm.Buttons.Count);
        Assert.Equal("Ctrl+C", vm.Row(2).CurrentText);
        Assert.Equal("Disabled", vm.Row(7).CurrentText);
        Assert.Equal("Default", vm.Row(1).CurrentText);
        Assert.Empty(vm.GetPendingButtonOps()); // freshly seeded — nothing pending
    }

    [Fact]
    public void GetPendingButtonOps_collects_only_changed_rows()
    {
        var vm = new SettingsViewModel(Sample(), false);
        vm.Row(1).StageKey(0x00, 0x04);  // A
        vm.Row(12).StageDisabled();
        var ops = vm.GetPendingButtonOps();
        Assert.Equal(2, ops.Count);
        Assert.Equal(1, ops[0].Position);
        Assert.Equal(12, ops[1].Position);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~ButtonRowViewModelTests|FullyQualifiedName~SettingsViewModelTests"`
Expected: **build error** — `ButtonRowViewModel`/`ButtonOp` and the new `SettingsViewModel` members not defined.

- [ ] **Step 3: Implement the row VM**

Create `src/NagaBatteryTray/Ui/ButtonRowViewModel.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Ui;

public enum ButtonOpKind { Apply, RestoreDefault }

/// <summary>One staged change for a grid button, produced by the Buttons UI and consumed by AppHost.</summary>
public readonly record struct ButtonOp(int Position, ButtonOpKind OpKind, ButtonActionKind Kind, byte Modifiers, byte HidUsage);

/// <summary>Pending-change model for one grid button row: an applied state (mirrors the persisted
/// table) plus an optional staged edit. Only staged rows produce ops — an untouched button is never
/// written (§3.1 discipline).</summary>
public sealed class ButtonRowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private ButtonActionKind _appliedKind = ButtonActionKind.Default;
    private byte _appliedModifiers, _appliedUsage;
    private ButtonActionKind? _pendingKind;
    private byte _pendingModifiers, _pendingUsage;
    private string _status = "";
    private bool _isCapturing;

    public ButtonRowViewModel(int position) => Position = position;

    public int Position { get; }
    public string Label => $"Button {Position}";

    public bool IsCapturing
    {
        get => _isCapturing;
        set { if (_isCapturing == value) return; _isCapturing = value; Notify(); Notify(nameof(CurrentText)); }
    }

    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; Notify(); }
    }

    public string CurrentText =>
        IsCapturing ? "press a key…"
        : _pendingKind is { } p ? $"{Describe(p, _pendingModifiers, _pendingUsage)} (pending)"
        : Describe(_appliedKind, _appliedModifiers, _appliedUsage);

    private static string Describe(ButtonActionKind kind, byte modifiers, byte usage) => kind switch
    {
        ButtonActionKind.Default => "Default",
        ButtonActionKind.Disabled => "Disabled",
        _ => KeyToHidUsage.Describe(modifiers, usage),
    };

    /// <summary>Seed the applied state from the persisted table (window open).</summary>
    public void SetApplied(ButtonActionKind kind, byte modifiers, byte usage)
    {
        _appliedKind = kind; _appliedModifiers = modifiers; _appliedUsage = usage;
        _pendingKind = null;
        Notify(nameof(CurrentText));
    }

    public void StageKey(byte modifiers, byte usage) => Stage(ButtonActionKind.Key, modifiers, usage);
    public void StageDisabled() => Stage(ButtonActionKind.Disabled, 0, 0);
    public void StageDefault() => Stage(ButtonActionKind.Default, 0, 0);

    private void Stage(ButtonActionKind kind, byte modifiers, byte usage)
    {
        IsCapturing = false;
        if (kind == _appliedKind && modifiers == _appliedModifiers && usage == _appliedUsage)
            _pendingKind = null; // staged back to what's already applied — nothing to do
        else
        {
            _pendingKind = kind; _pendingModifiers = modifiers; _pendingUsage = usage;
        }
        Status = "";
        Notify(nameof(CurrentText));
    }

    /// <summary>The op Apply should perform for this row; null = nothing staged. Default staged on a
    /// row that was never remapped is a no-op.</summary>
    public ButtonOp? ToOp()
    {
        if (_pendingKind is not { } kind) return null;
        if (kind == ButtonActionKind.Default)
            return new ButtonOp(Position, ButtonOpKind.RestoreDefault, ButtonActionKind.Default, 0, 0);
        return new ButtonOp(Position, ButtonOpKind.Apply, kind, _pendingModifiers, _pendingUsage);
    }

    public void MarkApplied()
    {
        if (_pendingKind is { } k)
        {
            _appliedKind = k; _appliedModifiers = _pendingModifiers; _appliedUsage = _pendingUsage;
            _pendingKind = null;
        }
        Status = "Applied";
        Notify(nameof(CurrentText));
    }

    public void MarkFailed(string message) => Status = message;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

Note the `StageDefault`-on-untouched-row case: `Stage` compares against the applied state
(`Default`, 0, 0) and clears the pending change, so `ToOp()` returns null — exactly the "never write an
untouched button" test.

- [ ] **Step 4: Extend `SettingsViewModel`**

In `src/NagaBatteryTray/Ui/SettingsViewModel.cs`:

Add a field + property near the other fields:

```csharp
    private string _buttonsStatus = "";
```

```csharp
    public string ButtonsStatus { get => _buttonsStatus; set => Set(ref _buttonsStatus, value); }

    public IReadOnlyList<ButtonRowViewModel> Buttons { get; }

    public ButtonRowViewModel Row(int position) => Buttons[position - 1];

    /// <summary>Ops for every staged row (empty when nothing changed) — the Apply button's payload.</summary>
    public List<ButtonOp> GetPendingButtonOps()
    {
        var ops = new List<ButtonOp>();
        foreach (var row in Buttons)
            if (row.ToOp() is { } op) ops.Add(op);
        return ops;
    }
```

And in the constructor, after the existing assignments:

```csharp
        var rows = new List<ButtonRowViewModel>(NagaV2ProButtons.Count);
        for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
        {
            var row = new ButtonRowViewModel(pos);
            if (source.ButtonBindings.TryGetValue(pos, out var b))
                row.SetApplied(b.Kind, b.Modifiers, b.HidUsage);
            rows.Add(row);
        }
        Buttons = rows;
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: full suite PASS (all prior + 8 row tests + 2 VM tests).

- [ ] **Step 6: Commit**

```powershell
git add src/NagaBatteryTray/Ui/ButtonRowViewModel.cs src/NagaBatteryTray/Ui/SettingsViewModel.cs tests/NagaBatteryTray.Tests/ButtonRowViewModelTests.cs tests/NagaBatteryTray.Tests/SettingsViewModelTests.cs
git commit -m "feat(ui): button row view-model with staged ops + settings seeding"
```

---

### Task 7: Buttons UI + AppHost orchestration (manual boundary)

**Files:**
- Modify: `src/NagaBatteryTray/Ui/SettingsWindow.xaml` (new CardExpander between the DPI card and the Advanced expander)
- Modify: `src/NagaBatteryTray/Ui/SettingsWindow.xaml.cs`
- Modify: `src/NagaBatteryTray/AppHost.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–6.
- Produces:
  `SettingsWindow`: `event Action<IReadOnlyList<ButtonOp>>? ApplyButtonsRequested;` `ButtonRowViewModel ButtonRow(int position);` `void SetButtonsStatus(string text);`
  `AppHost`: `private Task ApplyButtonsAsync(SettingsWindow win, IReadOnlyList<ButtonOp> ops)` (snapshot-on-first-apply → write → read-back verify → persist); `private Task ReapplyBindingsAsync()` wired to **startup** (after `_monitor.Start()`) and the **existing `OnDeviceChanged` debounced refresh** (after `RefreshNowAsync`).

This is the WPF/manual boundary (spec §9): gate = build + full suite green + a UI smoke run.

- [ ] **Step 1: Add the Buttons section to the XAML**

In `src/NagaBatteryTray/Ui/SettingsWindow.xaml`, insert between the `<!-- Mouse DPI -->` card's closing
`</ui:CardControl>` and the `<!-- Advanced: polling cadence -->` comment:

```xml
        <!-- Buttons: thumb-grid remap -->
        <ui:CardExpander Margin="0,0,0,8" Icon="{ui:SymbolIcon AppsList24}">
          <ui:CardExpander.Header>
            <StackPanel>
              <TextBlock Text="Buttons" FontWeight="Medium"/>
              <TextBlock Text="Remap the 12-button thumb grid" Opacity="0.7" FontSize="12"/>
            </StackPanel>
          </ui:CardExpander.Header>
          <StackPanel Margin="0,4,0,0">
            <TextBlock Text="Rebind captures the next key you press (hold modifiers with it). Bindings live on the mouse and are re-applied when it reconnects. Synapse must not be running."
                       Opacity="0.7" FontSize="12" TextWrapping="Wrap" Margin="0,0,0,8"/>
            <ItemsControl ItemsSource="{Binding Buttons}">
              <ItemsControl.ItemTemplate>
                <DataTemplate>
                  <Grid Margin="0,2">
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width="70"/>
                      <ColumnDefinition Width="*"/>
                      <ColumnDefinition Width="Auto"/>
                      <ColumnDefinition Width="Auto"/>
                      <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Text="{Binding Label}" VerticalAlignment="Center"/>
                    <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="4,0">
                      <TextBlock Text="{Binding CurrentText}" FontSize="12"/>
                      <TextBlock Text="{Binding Status}" FontSize="11" Opacity="0.7"/>
                    </StackPanel>
                    <ui:Button Grid.Column="2" Content="Rebind" Margin="4,0,0,0" FontSize="12" Padding="8,4" Click="OnRebindButton"/>
                    <ui:Button Grid.Column="3" Content="Disable" Margin="4,0,0,0" FontSize="12" Padding="8,4" Click="OnDisableButton"/>
                    <ui:Button Grid.Column="4" Content="Default" Margin="4,0,0,0" FontSize="12" Padding="8,4" Click="OnDefaultButton"/>
                  </Grid>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
            <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
              <ui:Button Content="Apply buttons" Appearance="Primary" Click="OnApplyButtons"
                         IsEnabled="{Binding DevicePresent}"/>
              <TextBlock Text="{Binding ButtonsStatus}" VerticalAlignment="Center" Margin="10,0,0,0"
                         Opacity="0.8" FontSize="12"/>
            </StackPanel>
          </StackPanel>
        </ui:CardExpander>
```

- [ ] **Step 2: Wire capture + events in the code-behind**

In `src/NagaBatteryTray/Ui/SettingsWindow.xaml.cs`:

Add `using System.Windows.Input;` and `using System.Collections.Generic;` to the usings. Add to the class:

```csharp
    public event Action<IReadOnlyList<ButtonOp>>? ApplyButtonsRequested; // raised on "Apply buttons"

    private ButtonRowViewModel? _capturingRow;

    public ButtonRowViewModel ButtonRow(int position) => _vm.Row(position);
    public void SetButtonsStatus(string text) => _vm.ButtonsStatus = text;

    private void OnRebindButton(object sender, RoutedEventArgs e)
    {
        if (_capturingRow is { } prev) prev.IsCapturing = false;
        var row = (ButtonRowViewModel)((FrameworkElement)sender).DataContext;
        _capturingRow = row;
        row.IsCapturing = true;
        Focus(); // take focus off the clicked button so the next key lands in the window
    }

    private void OnDisableButton(object sender, RoutedEventArgs e) =>
        ((ButtonRowViewModel)((FrameworkElement)sender).DataContext).StageDisabled();

    private void OnDefaultButton(object sender, RoutedEventArgs e) =>
        ((ButtonRowViewModel)((FrameworkElement)sender).DataContext).StageDefault();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturingRow is not { } row) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt-chords arrive as Key.System
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return; // a bare modifier — keep capturing until the real key arrives
        _capturingRow = null;
        if (key == Key.Escape) { row.IsCapturing = false; return; } // cancel (Esc is not bindable)
        if (!KeyToHidUsage.TryGetUsage(key, out byte usage))
        {
            row.IsCapturing = false;
            row.Status = $"{key} can't be bound";
            return;
        }
        row.StageKey(KeyToHidUsage.ToModifierBits(Keyboard.Modifiers), usage);
    }

    private void OnApplyButtons(object sender, RoutedEventArgs e)
    {
        var ops = _vm.GetPendingButtonOps();
        if (ops.Count == 0) { _vm.ButtonsStatus = "No changes"; return; }
        ApplyButtonsRequested?.Invoke(ops);
    }
```

- [ ] **Step 3: Orchestrate in `AppHost`**

In `src/NagaBatteryTray/AppHost.cs`:

In `Start()`, after `_monitor.Start();` add:

```csharp
        _ = Task.Run(ReapplyBindingsAsync); // re-assert remaps once at startup (no-op when table empty)
```

In `OnDeviceChanged()`'s `Task.Run` body, after `await _monitor.RefreshNowAsync();` add:

```csharp
            await ReapplyBindingsAsync(); // volatile remaps clear on replug — re-assert them
```

In `OpenSettings()`, after the `win.ApplyDpiRequested += ...` line add:

```csharp
        win.ApplyButtonsRequested += ops => _ = ApplyButtonsAsync(win, ops);
```

Add the two methods after `ApplyDpiAsync`:

```csharp
    /// <summary>Re-assert configured remaps (re-apply model). Rides startup + the debounced
    /// device-change refresh — never a new timer; an empty table makes zero device calls.</summary>
    private async Task ReapplyBindingsAsync()
    {
        var table = _settings.Settings.ButtonBindings;
        if (table.Count == 0) return;
        var bindings = new List<ButtonBinding>(table.Count);
        foreach (var (pos, b) in table)
            bindings.Add(new ButtonBinding(NagaV2ProButtons.IdForPosition(pos), b.Kind, b.Modifiers, b.HidUsage));
        await _monitor.ApplyRemapsAsync(bindings);
    }

    /// <summary>Apply staged button ops: snapshot the stock action at a button's first-ever remap,
    /// write the binding (volatile profile), read-back verify, persist. Default restores the snapshot
    /// and drops the entry. Every HID call runs off the UI thread via the monitor's shared lock.</summary>
    private async Task ApplyButtonsAsync(SettingsWindow win, IReadOnlyList<ButtonOp> ops)
    {
        Dispatch(() => win.SetButtonsStatus("Applying…"));
        var table = _settings.Settings.ButtonBindings;
        int okCount = 0;

        foreach (var op in ops)
        {
            var row = win.ButtonRow(op.Position);
            byte id = NagaV2ProButtons.IdForPosition(op.Position);
            bool ok;

            if (op.OpKind == ButtonOpKind.RestoreDefault)
            {
                table.TryGetValue(op.Position, out var entry);
                bool deferred = entry is not null && !entry.HasStock;
                ok = entry is null || !entry.HasStock
                     || await Task.Run(() => _monitor.SetButtonAsync(id, entry.StockCategory, entry.StockData));
                if (ok)
                {
                    table.Remove(op.Position);
                    Dispatch(() =>
                    {
                        row.MarkApplied();
                        if (deferred) row.Status = "Default after next reconnect";
                    });
                }
                else Dispatch(() => row.MarkFailed("Restore failed — retry"));
            }
            else
            {
                if (!table.TryGetValue(op.Position, out var entry))
                {
                    entry = new ButtonBindingSetting();
                    // first-ever remap of this button: it has never been written, so the direct-profile
                    // read returns its stock action — snapshot it for instant Default later
                    var stock = await Task.Run(() => _monitor.GetButtonAsync(id));
                    if (stock is { } s)
                    {
                        entry.StockCategory = s.Category;
                        entry.StockData = s.Data;
                        entry.HasStock = true;
                    }
                }
                var binding = new ButtonBinding(id, op.Kind, op.Modifiers, op.HidUsage);
                var (category, data) = binding.ToWire();
                ok = await Task.Run(() => _monitor.SetButtonAsync(id, category, data));
                if (ok)
                {
                    var readBack = await Task.Run(() => _monitor.GetButtonAsync(id));
                    ok = readBack is { } r && r.Category == category && r.Data.AsSpan().SequenceEqual(data);
                }
                if (ok)
                {
                    entry.Kind = op.Kind; entry.Modifiers = op.Modifiers; entry.HidUsage = op.HidUsage;
                    table[op.Position] = entry;
                    Dispatch(row.MarkApplied);
                }
                else Dispatch(() => row.MarkFailed("Not applied — wiggle the mouse and retry"));
            }
            if (ok) okCount++;
        }

        _settings.Save();
        Dispatch(() => win.SetButtonsStatus(okCount == ops.Count ? "Applied" : $"{okCount}/{ops.Count} applied"));
    }
```

- [ ] **Step 4: Build + full suite green**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build` then `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: Build succeeded, 0 errors; all tests PASS.

- [ ] **Step 5: UI smoke run (no mouse required)**

Quit the installed tray app, then:
`& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" "src\NagaBatteryTray\bin\Debug\net10.0-windows10.0.19041.0\NagaBatteryTray.dll"`
Open Settings from the tray icon → the "Buttons" expander shows 12 rows saying "Default"; Rebind shows
"press a key…" and captures e.g. Ctrl+F5 as "Ctrl+F5 (pending)"; Esc cancels a capture; "Apply buttons"
with the mouse absent reports "Not applied — wiggle the mouse and retry" (row status) rather than a false
success. Quit the dev instance and relaunch the installed app when done.

- [ ] **Step 6: Commit**

```powershell
git add src/NagaBatteryTray/Ui/SettingsWindow.xaml src/NagaBatteryTray/Ui/SettingsWindow.xaml.cs src/NagaBatteryTray/AppHost.cs
git commit -m "feat(ui): Buttons section with key capture + apply/re-apply orchestration"
```

---

### Task 8: Hardware acceptance + §3.1 gates + docs (user at the keyboard)

**Files:**
- Modify: `CLAUDE.md` (roadmap + architecture blurb), `README.md` (feature list)

**Interfaces:**
- Consumes: the finished feature. **The acceptance checks need Brandon with the mouse** (Synapse absent).

- [ ] **Step 1: Functional acceptance (spec §3 Stage 2)**

With the dev build running and the mouse connected:
1. Settings → Buttons → Rebind button 1 → press `Ctrl+F5` → Apply. Row shows "Applied"; pressing
   **grid button 1 emits Ctrl+F5** in any app.
2. Disable button 2 → Apply → pressing grid button 2 does nothing.
3. Power-cycle the mouse → within ~1 s of reconnect (existing debounce) both bindings work again
   **without opening Settings** (re-apply path).
4. Button 1 → Default → Apply → button 1 emits its stock action again instantly.
5. Restart the app → bindings still listed (persisted) and re-applied on startup.
6. `settings.json` shows the sparse `ButtonBindings` table; deleting it restores no-remap behaviour.

- [ ] **Step 2: §3.1 gating acceptance (spec §9)**

After a batch of applies + a reconnect re-apply: Task Manager shows idle CPU back to ~0% and private
working set ~23 MB; mouse movement/click feel unchanged at idle, during Apply, and during a
connect-time re-apply. **A failure here is not shippable** — stop and investigate.

- [ ] **Step 3: Update the docs**

In `CLAUDE.md`: mark the roadmap line `- [ ] B — Button remapping` as
`- [x] B — Button remapping (MVP: key+modifiers/disable, volatile re-apply model — shipped YYYY-MM-DD)`
using the date the acceptance run passed, and
add one architecture sentence alongside the DPI notes: buttons are written to the **volatile direct
profile** (`0x02/0x0c`, ids `0x40..0x4b`) and re-applied on startup/device-change; onboard profiles are
never written. In `README.md`: add button remapping to the feature list.

- [ ] **Step 4: Run the full suite one last time**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add CLAUDE.md README.md
git commit -m "docs: Phase B button remapping shipped (volatile re-apply model)"
```

Afterwards: `.\scripts\install.ps1` to update the installed app, and finish the branch
(merge `phase-b-button-remap` → `master`, push) via the finishing-a-development-branch flow.
