# Mouse Dock Pro Charger Support — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface the Razer Mouse Dock Pro's battery/charging in the tray app by reading the dock (PID `0x00A4`) as a second passive HID endpoint, so the percentage/charging stay live while the mouse sleeps or charges on the dock.

**Architecture:** Spike-gated, two stages. **Stage 1** adds a `--probe-dock` diagnostic that verifies, on real hardware, whether the dock relays the docked mouse's battery (`0x07/0x80`) and charging (`0x07/0x84`) and in which states. **Stage 2** (built only if Stage 1 confirms a relay) parameterizes `RazerDevice` by target PID, then has `BatteryMonitor` read the mouse first and fall back to the dock **only when the mouse read is `Absent`**, piggybacked on the existing battery poll under the one shared read lock. An **optional** popup line shows dock presence/charging.

**Tech Stack:** C#, .NET 10 (`net10.0-windows10.0.19041.0`), WPF + WinForms, WPF-UI 4.3.0, HidSharp, xUnit. User-local .NET SDK at `%LOCALAPPDATA%\Microsoft\dotnet`.

## Status — 2026-06-21: CLOSED (Stage 1 done; Stage 2 NOT built)

- **Task 1** (`--probe-dock`) — done, committed.
- **Task 2 GATE — NO-GO.** The spike, including a definitive re-test with the mouse charging *through* the dock, showed the dock (`0x00A4`) **never** returns `0x02` to a battery/charging relay query — in any of four states (off-dock, docked-asleep, docked-awake-idle, docked-awake-charging). The relay does not exist on this firmware. See spec §6.
- **Stage 2 (Tasks 3–7) — dropped, not built.** Goal 2 (battery via dock relay) is non-viable; goal 1 (charging-on-dock) is already delivered by the existing mouse `0x84` read, so no dock code is needed. The Stage 2 tasks below are retained only as a record of the design the gate ruled out. **Note:** Task 3's `FindControlPath` rewrite was overtaken by the unrelated wired/USB-C fix (commit `932398e`), which already reworked that method (now `FindControlPaths`, wired-first + verify) — so the Task 3 snippets no longer match `RazerDevice.cs`.

## Global Constraints

- **HARD GATING (never regress):** stay lightweight (~0% idle CPU, ~23 MB private working set) AND zero mouse input-latency regression. Passive HID **feature reports** only (`HidD_Set/GetFeature`, USB control endpoint); open **zero desired access** + `FILE_SHARE_READ|WRITE`; never claim the input collection. **No new timer/thread** — the dock read piggybacks the existing battery poll (cadence floor 15 s). Dock and mouse transfers **serialize through the single existing `_readLock`**. **Zero dock I/O when the mouse is reachable** (the dock is feature-queried only when the mouse read returns `Absent`). Dock handle opened **on demand**. DPI untouched, never polled.
- **No new dependencies.** Target `net10.0-windows10.0.19041.0`; WPF + WinForms; WPF-UI 4.3.0.
- **HID facts:** VID `0x1532`; mouse PID `0x00A8`/`0x00A7`, **dock PID `0x00A4`**; 90-byte report in a 91-byte feature buffer; CRC XOR `[2..87]`; reply value at `buffer[10]`; battery `0x07/0x80`, charging `0x07/0x84`, `data_size 0x02`.
- **Reference-only prior art (all GPL):** re-derive protocol bytes; never copy code; never adopt the interface-claiming libusb transport.
- **DRY, YAGNI, TDD, frequent commits, conventional-commit messages, surgical changes.** Read the FULL file before editing.
- **Build:** `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`  **Test:** `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`  (single class: append `--filter "FullyQualifiedName~BatteryMonitorTests"`).
- Spec: `docs/superpowers/specs/2026-06-20-naga-dock-pro-design.md` (§6 = the spike state matrix; §3.1 = gating invariants).

---

## Stage 1 — Feasibility spike (gating; must pass before Stage 2)

### Task 1: `--probe-dock` diagnostic

**Files:**
- Modify: `src/NagaBatteryTray/Hid/RazerProtocol.cs` (add `DockPid` constant)
- Modify: `src/NagaBatteryTray/Diagnostics/ProbeCommand.cs` (add `RunDock` + `DockOneShot`)
- Modify: `src/NagaBatteryTray/Program.cs` (dispatch `--probe-dock`)

**Interfaces:**
- Consumes: `RazerProtocol.BuildFeatureBuffer(byte transactionId, byte commandId)`, `RazerProtocol.CommandIdBattery` (`0x80`), `RazerProtocol.CommandIdCharging` (`0x84`), `RazerProtocol.ParseReply(byte[], out byte)`, `RazerProtocol.ScaleBattery(byte)`, `RazerProtocol.VendorId`, `RazerProtocol.BufferLength`.
- Produces: `RazerProtocol.DockPid` (`int = 0x00A4`); `ProbeCommand.RunDock()` (`int`); CLI flag `--probe-dock`.

- [ ] **Step 1: Add the dock PID constant**

In `src/NagaBatteryTray/Hid/RazerProtocol.cs`, add `DockPid` next to the existing PID constants (after the `MousePidWired` line):

```csharp
    public const int VendorId = 0x1532;
    public const int MousePidWireless = 0x00A8;
    public const int MousePidWired = 0x00A7;
    public const int DockPid = 0x00A4;       // Razer Mouse Dock Pro (separate USB device)
    public const int UsagePageVendor = 0xFF00;
```

- [ ] **Step 2: Add `RunDock` + `DockOneShot` to `ProbeCommand`**

In `src/NagaBatteryTray/Diagnostics/ProbeCommand.cs`, add these two methods (place `RunDock` after `RunDpi`, and `DockOneShot` next to the existing `OneShot` helper). They reuse the file's existing `CreateFile`/`HidD_SetFeature`/`HidD_GetFeature` P/Invokes and `Mi` helper:

```csharp
    public static int RunDock()
    {
        Console.WriteLine("Naga Battery Tray - Mouse Dock Pro probe (PID 0x00A4, battery + charging)\n");

        bool any = false;
        foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, RazerProtocol.DockPid))
        {
            int max = -1;
            try { max = dev.GetMaxFeatureReportLength(); } catch { }
            if (max != RazerProtocol.BufferLength) continue; // only the 90+1 feature-report collection
            any = true;

            Console.WriteLine($"DOCK 0x{RazerProtocol.DockPid:x4} {Mi(dev.DevicePath)} max={max} -> raw zero-access open");
            using var h = CreateFile(dev.DevicePath, 0, 0x3, IntPtr.Zero, 0x3, 0, IntPtr.Zero);
            if (h.IsInvalid) { Console.WriteLine($"  CreateFile failed err={Marshal.GetLastWin32Error()}"); continue; }
            Console.WriteLine("  opened OK (zero-access)");

            foreach (byte tid in new byte[] { 0x1f, 0xff })
            {
                Console.WriteLine($"  tid 0x{tid:x2} battery : {DockOneShot(h, tid, RazerProtocol.CommandIdBattery)}");
                Console.WriteLine($"  tid 0x{tid:x2} charging: {DockOneShot(h, tid, RazerProtocol.CommandIdCharging)}");
            }
        }

        if (!any)
            Console.WriteLine($"No dock collection found (VID 0x{RazerProtocol.VendorId:x4} PID 0x{RazerProtocol.DockPid:x4}, feature len {RazerProtocol.BufferLength}).");
        Console.WriteLine("\nRun in each state: mouse off-dock / docked+charging / docked+asleep / dock present not charging.");
        Console.WriteLine("Legend: status 0x02=success, 0x01=busy(asleep/no relay), other=fail. battery raw 0..255; charging 0/1 at byte[10].");
        return 0;
    }

    private static string DockOneShot(SafeFileHandle h, byte tid, byte commandId)
    {
        try
        {
            var buf = RazerProtocol.BuildFeatureBuffer(tid, commandId);
            if (!HidD_SetFeature(h, buf, buf.Length))
                return $"SetFeature failed err={Marshal.GetLastWin32Error()}";
            Thread.Sleep(400);
            var reply = new byte[RazerProtocol.BufferLength];
            reply[0] = 0;
            if (!HidD_GetFeature(h, reply, reply.Length))
                return $"GetFeature failed err={Marshal.GetLastWin32Error()}";
            var hex = string.Join(" ", reply.Take(16).Select(b => b.ToString("x2")));
            var r = RazerProtocol.ParseReply(reply, out byte v);
            string decoded = commandId == RazerProtocol.CommandIdBattery
                ? $"raw={v} ({RazerProtocol.ScaleBattery(v)}%)"
                : $"charging={v}";
            return $"status=0x{reply[1]:x2} {r} {decoded}  [{hex}]";
        }
        catch (Exception ex) { return $"EXC {ex.Message}"; }
    }
```

- [ ] **Step 3: Dispatch the flag in `Program.cs`**

In `src/NagaBatteryTray/Program.cs`, add a third dispatch block immediately after the existing `--probe-dpi` block (before the `using var mutex` line):

```csharp
        if (args.Length > 0 && args[0] == "--probe-dock")
        {
            AllocConsoleIfNeeded();
            return Diagnostics.ProbeCommand.RunDock();
        }
```

- [ ] **Step 4: Build**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 5: Smoke-run the command (does not require Stage-1 results yet)**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run --project src/NagaBatteryTray -- --probe-dock`
Expected: it prints the `DOCK 0x00a4 ...` header (dock connected) or the `No dock collection found` line — either proves the command path works. (Interpreting the per-state readings is Task 2.)

- [ ] **Step 6: Commit**

```bash
git add src/NagaBatteryTray/Hid/RazerProtocol.cs src/NagaBatteryTray/Diagnostics/ProbeCommand.cs src/NagaBatteryTray/Program.cs
git commit -m "feat(diag): add --probe-dock to probe the Mouse Dock Pro (0x00A4) battery+charging"
```

---

### Task 2: Run the spike on hardware — GATE (no code)

This task is the **go/no-go for Stage 2**. Do not start Task 3 until this table is filled and the relay decision is recorded.

**Interfaces:**
- Consumes: `--probe-dock` from Task 1.
- Produces: a recorded state-matrix result + a decision (relay confirmed → proceed; not confirmed → revise per spec §6 contingency).

- [ ] **Step 1: Run `--probe-dock` in each state and capture output**

For each state, physically arrange the hardware, run
`& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run --project src/NagaBatteryTray -- --probe-dock`,
and paste the output under the matching row:

| State | Arrange | Record: which tid + status + battery%/charging |
| --- | --- | --- |
| Mouse active, **off** dock | mouse in hand, awake | |
| Mouse docked, **charging** | mouse on dock, charging | |
| Mouse docked, **asleep** | mouse on dock, leave idle until radio sleeps | |
| Dock present, **not charging** (current) | dock plugged, mouse not charging on it | |

- [ ] **Step 2: Record the decision**

Decide and write one line into the spec file under §6 (commit it):
- **Relay confirmed** if the dock returns `status=0x02` with a sane battery (and `0/1` charging) at some tid (expected `0x1f`) in at least the docked states → **proceed to Stage 2**; note the working tid.
- **Relay NOT confirmed** (dock only ever returns busy/fail, or battery is garbage) → **stop**. Per spec §6, drop goal #2; keep only what works (e.g. dock-presence hint). Re-open brainstorming for the reduced scope before writing more code.

- [ ] **Step 3: Commit the recorded findings**

```bash
git add docs/superpowers/specs/2026-06-20-naga-dock-pro-design.md
git commit -m "docs(spec): record Phase C dock spike results + relay go/no-go"
```

---

## Stage 2 — Dock-aware fallback (only if Task 2 confirmed the relay)

### Task 3: Parameterize `RazerDevice` by target PID (device profile)

Generalizes the device so the *same* passive transport can target the dock. The mouse path is preserved byte-for-byte (verified by the existing suite staying green). No unit test is added here (the device layer is hardware I/O and untested by design); the test cycle is "build + full suite green".

**Files:**
- Create: `src/NagaBatteryTray/Hid/RazerDeviceProfile.cs`
- Modify: `src/NagaBatteryTray/Hid/RazerDevice.cs` (ctor, `_profile` field, `FindControlPath`, `ResolveTransactionIdAsync`)

**Interfaces:**
- Consumes: `RazerProtocol.MousePidWireless/MousePidWired/DockPid/TransactionIdProbeSet`.
- Produces: `RazerDeviceProfile` (record with `int[] Pids`, `byte[] TransactionIds`, `bool CacheTransactionId`; static `Mouse`/`Dock`); `RazerDevice(ISettingsStore, RazerDeviceProfile)` ctor (existing `RazerDevice(ISettingsStore)` preserved, = mouse profile).

- [ ] **Step 1: Create the profile type**

Create `src/NagaBatteryTray/Hid/RazerDeviceProfile.cs`:

```csharp
namespace NagaBatteryTray.Hid;

/// <summary>Which Razer USB device a <see cref="RazerDevice"/> targets and how it resolves the Razer
/// transaction id. The mouse auto-probes a set and caches the winner; the dock uses a fixed try-order
/// (0x1f then 0xff) and never caches (so it can't clobber the mouse's cached id).</summary>
public sealed record RazerDeviceProfile(int[] Pids, byte[] TransactionIds, bool CacheTransactionId)
{
    public static RazerDeviceProfile Mouse { get; } = new(
        new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidWired },
        RazerProtocol.TransactionIdProbeSet,
        CacheTransactionId: true);

    public static RazerDeviceProfile Dock { get; } = new(
        new[] { RazerProtocol.DockPid },
        new byte[] { 0x1f, 0xff },
        CacheTransactionId: false);
}
```

- [ ] **Step 2: Thread the profile through `RazerDevice`**

In `src/NagaBatteryTray/Hid/RazerDevice.cs`:

(a) Add the field next to `_settings`:

```csharp
    private readonly ISettingsStore _settings;
    private readonly RazerDeviceProfile _profile;
```

(b) Replace the single-line constructor `public RazerDevice(ISettingsStore settings) => _settings = settings;` with:

```csharp
    public RazerDevice(ISettingsStore settings) : this(settings, RazerDeviceProfile.Mouse) { }

    public RazerDevice(ISettingsStore settings, RazerDeviceProfile profile)
    {
        _settings = settings;
        _profile = profile;
    }
```

(c) Replace `private static string? FindControlPath()` with an instance method that iterates the profile's PIDs (body otherwise unchanged):

```csharp
    /// <summary>The control collection is the device's HID interface that exposes the 90+1 byte feature report.</summary>
    private string? FindControlPath()
    {
        foreach (int pid in _profile.Pids)
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                int max;
                try { max = dev.GetMaxFeatureReportLength(); } catch { continue; }
                if (max == RazerProtocol.BufferLength) return dev.DevicePath;
            }
        return null;
    }
```

(d) Replace `ResolveTransactionIdAsync` so the probe set and caching come from the profile:

```csharp
    /// <summary>Returns cached id (mouse only), else probes the profile's set and (mouse only) caches the winner. 0 = unresolved.</summary>
    private async Task<byte> ResolveTransactionIdAsync(CancellationToken ct)
    {
        if (_profile.CacheTransactionId)
        {
            var cached = _settings.GetCachedTransactionId();
            if (cached is not null) return cached.Value;
        }

        foreach (byte tid in _profile.TransactionIds)
        {
            var value = await QueryAsync(tid, RazerProtocol.CommandIdBattery, ct);
            if (value is not null && RazerProtocol.ScaleBattery(value.Value) is >= 0 and <= 100)
            {
                if (_profile.CacheTransactionId) _settings.SetCachedTransactionId(tid);
                return tid;
            }
        }
        return 0;
    }
```

- [ ] **Step 3: Build**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
Expected: `Build succeeded`, 0 errors. (`new RazerDevice(store)` in `AppHost`/`ProbeCommand` still compiles — it now delegates to the mouse profile.)

- [ ] **Step 4: Run the full suite (no mouse-path regression)**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: `Passed!` — all existing tests pass (34), 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Hid/RazerDeviceProfile.cs src/NagaBatteryTray/Hid/RazerDevice.cs
git commit -m "refactor(hid): parameterize RazerDevice by device profile (mouse vs dock PID)"
```

---

### Task 4: Mouse-first / dock-fallback read in `BatteryMonitor` (TDD)

Adds the fallback: read the mouse; only if it's `Absent` and a dock device is configured, read the dock. The read stays inside `PollAsync`'s existing `_readLock` acquisition (no new lock, no concurrency).

**Files:**
- Modify: `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs` (add `ReadCount`)
- Modify: `tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs` (3 new tests)
- Modify: `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs` (`_dock` field, ctor param, `ReadWithFallbackAsync`, `PollAsync`)

**Interfaces:**
- Consumes: `IRazerDevice.ReadAsync`, `BatteryReading`.
- Produces: `BatteryMonitor(IRazerDevice device, ISettingsStore settings, Action<Action> dispatch, IRazerDevice? dock = null)`; `internal Task<BatteryReading> ReadWithFallbackAsync(CancellationToken ct)`.

- [ ] **Step 1: Add a read counter to the fake**

In `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs`, replace the `_queue`/`Enqueue`/`ReadAsync` block at the top of the class body with this (adds a `ReadCount` so callers can assert the dock was/wasn't queried):

```csharp
    private readonly Queue<BatteryReading> _queue = new();
    public int ReadCount { get; private set; }
    public void Enqueue(BatteryReading r) => _queue.Enqueue(r);
    public Task<BatteryReading> ReadAsync(CancellationToken ct)
    {
        ReadCount++;
        return Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : BatteryReading.Absent(DateTimeOffset.Now));
    }
```

- [ ] **Step 2: Write the failing tests**

In `tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs`, add these three tests (they reference `ReadWithFallbackAsync`, which does not exist yet, and the new 4-arg ctor):

```csharp
    [Fact]
    public async Task Read_falls_back_to_dock_when_mouse_absent()
    {
        var mouse = new FakeRazerDevice();                                   // empty -> Absent
        var dock = new FakeRazerDevice();
        dock.Enqueue(new BatteryReading(50, true, true, DateTimeOffset.Now)); // dock relays the mouse battery
        using var m = new BatteryMonitor(mouse, TempStore(), a => a(), dock);

        var reading = await m.ReadWithFallbackAsync(CancellationToken.None);

        Assert.True(reading.IsPresent);
        Assert.Equal(50, reading.Percent);
        Assert.True(reading.IsCharging);
        Assert.Equal(1, dock.ReadCount);
    }

    [Fact]
    public async Task Dock_not_queried_when_mouse_reachable()
    {
        var mouse = new FakeRazerDevice();
        mouse.Enqueue(new BatteryReading(80, false, true, DateTimeOffset.Now));
        var dock = new FakeRazerDevice();
        using var m = new BatteryMonitor(mouse, TempStore(), a => a(), dock);

        var reading = await m.ReadWithFallbackAsync(CancellationToken.None);

        Assert.Equal(80, reading.Percent);
        Assert.Equal(0, dock.ReadCount); // zero dock I/O when the mouse answers
    }

    [Fact]
    public async Task No_dock_configured_returns_mouse_result()
    {
        var mouse = new FakeRazerDevice();                                   // Absent
        using var m = new BatteryMonitor(mouse, TempStore(), a => a());      // dock = null

        var reading = await m.ReadWithFallbackAsync(CancellationToken.None);

        Assert.False(reading.IsPresent);
    }
```

- [ ] **Step 3: Run the new tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~BatteryMonitorTests"`
Expected: FAIL — compile error, `'BatteryMonitor' does not contain a definition for 'ReadWithFallbackAsync'` and no 4-argument constructor.

- [ ] **Step 4: Implement the fallback in `BatteryMonitor`**

In `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs`:

(a) Add the field next to `_device`:

```csharp
    private readonly IRazerDevice _device;
    private readonly IRazerDevice? _dock;
    private readonly ISettingsStore _settings;
```

(b) Replace the constructor with one that accepts an optional dock device:

```csharp
    public BatteryMonitor(IRazerDevice device, ISettingsStore settings, Action<Action> dispatch, IRazerDevice? dock = null)
    {
        _device = device;
        _settings = settings;
        _dispatch = dispatch;
        _dock = dock;
    }
```

(c) Add the fallback method and call it from `PollAsync`. Replace the body of `PollAsync` so the read goes through `ReadWithFallbackAsync`, and add the new method directly below it:

```csharp
    private async Task PollAsync()
    {
        if (!await _readLock.WaitAsync(0)) return; // a read is already in flight; skip
        try
        {
            var reading = await ReadWithFallbackAsync(_cts.Token);
            ProcessReading(reading);
            ScheduleNext(reading);
        }
        catch (OperationCanceledException) { }
        finally { _readLock.Release(); }
    }

    /// <summary>Read the mouse; only if it is Absent and a dock is configured, fall back to the dock relay.
    /// Runs inside the caller's <see cref="_readLock"/> hold — no concurrent feature transfers.</summary>
    internal async Task<BatteryReading> ReadWithFallbackAsync(CancellationToken ct)
    {
        var reading = await _device.ReadAsync(ct);
        if (!reading.IsPresent && _dock is not null)
            reading = await _dock.ReadAsync(ct);
        return reading;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: `Passed!` — all tests pass (34 existing + 3 new = 37), 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/NagaBatteryTray/Monitoring/BatteryMonitor.cs tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs
git commit -m "feat(monitor): fall back to dock battery read when the mouse is unreachable"
```

---

### Task 5: Wire the dock device into `AppHost`

Constructs the dock `RazerDevice` (dock profile), passes it to the monitor, and disposes it on quit. The dock handle is opened lazily by `RazerDevice` only on the first fallback read.

**Files:**
- Modify: `src/NagaBatteryTray/AppHost.cs` (`_dock` field, construction, monitor wiring, dispose)

**Interfaces:**
- Consumes: `RazerDevice(ISettingsStore, RazerDeviceProfile)`, `RazerDeviceProfile.Dock`, `BatteryMonitor(..., IRazerDevice? dock)`.
- Produces: nothing new (internal wiring).

- [ ] **Step 1: Add the dock field**

In `src/NagaBatteryTray/AppHost.cs`, add the field next to `_device`:

```csharp
    private RazerDevice _device = null!;
    private RazerDevice _dock = null!;
    private BatteryMonitor _monitor = null!;
```

- [ ] **Step 2: Construct the dock and pass it to the monitor**

In `Start()`, replace the device/monitor construction lines:

```csharp
        _device = new RazerDevice(_settings);
        _monitor = new BatteryMonitor(_device, _settings, Dispatch);
```

with:

```csharp
        _device = new RazerDevice(_settings);
        _dock = new RazerDevice(_settings, RazerDeviceProfile.Dock);
        _monitor = new BatteryMonitor(_device, _settings, Dispatch, _dock);
```

- [ ] **Step 3: Dispose the dock on quit**

In `Quit()`, add `_dock.Dispose();` next to the existing device disposal:

```csharp
        _monitor.Dispose();
        _device.Dispose();
        _dock.Dispose();
        _tray.Dispose();
```

- [ ] **Step 4: Build**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 5: Run the full suite**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: `Passed!`, 0 failed.

- [ ] **Step 6: Manual gating check (footprint + latency)**

Install the Release build (`.\scripts\install.ps1`). With the mouse awake, confirm idle CPU ~0% and private working set ~23 MB (no regression — dock is never queried while the mouse answers). Let the mouse sleep on the dock and confirm the battery stays live (fallback works) and there is no perceptible mouse input-lag change. Record the numbers in the spec §9 acceptance notes.

- [ ] **Step 7: Commit**

```bash
git add src/NagaBatteryTray/AppHost.cs
git commit -m "feat(app): read dock battery as fallback when the mouse is asleep/unreachable"
```

---

## Stage 2 (optional) — Dock status line in the popup

Build only if you want the visual indicator (per the spec, a post-spike nice-to-have). It adds a `Dock: connected/charging` line driven by passive USB enumeration + the existing charging read — **no extra dock feature I/O**.

### Task 6 (optional): Compute `DockStatus` in the monitor (TDD)

**Files:**
- Modify: `src/NagaBatteryTray/Hid/RazerDevice.cs` (static `IsDockPresent`)
- Modify: `src/NagaBatteryTray/Monitoring/DeviceState.cs` (`DockStatus` enum + `Dock` member)
- Modify: `tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs` (2 new tests)
- Modify: `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs` (`_isDockPresent` predicate, compute `Dock` in `ProcessReading`)

**Interfaces:**
- Consumes: `BatteryReading`, `DeviceState`.
- Produces: `DockStatus { None, Connected, Charging }`; `DeviceState.Dock` (defaulted member); `RazerDevice.IsDockPresent()` (`static bool`); `BatteryMonitor(..., IRazerDevice? dock = null, Func<bool>? isDockPresent = null)`.

- [ ] **Step 1: Add the enum + `DeviceState.Dock` member**

In `src/NagaBatteryTray/Monitoring/DeviceState.cs`, add the enum and a defaulted `Dock` member (the default keeps every existing `new(...)`/factory call valid):

```csharp
namespace NagaBatteryTray.Monitoring;

public enum DeviceStatus { Unknown, Online }

public enum DockStatus { None, Connected, Charging }

public readonly record struct DeviceState(DeviceStatus Status, int Percent, bool Charging, DockStatus Dock = DockStatus.None)
{
    public static DeviceState Unknown { get; } = new(DeviceStatus.Unknown, 0, false);
    public static DeviceState Online(int percent, bool charging) => new(DeviceStatus.Online, percent, charging);
}
```

- [ ] **Step 2: Add the passive presence check to `RazerDevice`**

In `src/NagaBatteryTray/Hid/RazerDevice.cs`, add a static enumeration-only helper (no device open, no feature I/O). Place it next to `FindControlPath`:

```csharp
    /// <summary>True if a Mouse Dock Pro (0x00A4) is enumerated. Passive: walks the HID device list only,
    /// never opens a handle — so it adds no feature-report traffic.</summary>
    public static bool IsDockPresent()
    {
        foreach (var _ in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, RazerProtocol.DockPid))
            return true;
        return false;
    }
```

- [ ] **Step 3: Write the failing tests**

In `tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs`, add two tests using an injected presence predicate (so they don't depend on real hardware):

```csharp
    [Fact]
    public void Dock_present_and_charging_sets_dock_charging()
    {
        var m = new BatteryMonitor(new FakeRazerDevice(), TempStore(), a => a(), null, () => true);
        m.ProcessReading(Online(90, true));
        Assert.Equal(DockStatus.Charging, m.State.Dock);
    }

    [Fact]
    public void Dock_present_not_charging_sets_dock_connected()
    {
        var m = new BatteryMonitor(new FakeRazerDevice(), TempStore(), a => a(), null, () => true);
        m.ProcessReading(Online(90, false));
        Assert.Equal(DockStatus.Connected, m.State.Dock); // presence never implies charging
    }
```

- [ ] **Step 4: Run the new tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~BatteryMonitorTests"`
Expected: FAIL — compile error: no 5-argument `BatteryMonitor` constructor (Steps 1–2 already added `DockStatus`/`DeviceState.Dock`/`IsDockPresent`; the ctor and `ComputeDock` arrive in Step 5).

- [ ] **Step 5: Implement the predicate + dock computation**

In `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs`:

(a) Add the field next to `_dock`:

```csharp
    private readonly IRazerDevice? _dock;
    private readonly Func<bool> _isDockPresent;
```

(b) Replace the constructor to accept the predicate (defaulting to "no dock present" when not supplied):

```csharp
    public BatteryMonitor(IRazerDevice device, ISettingsStore settings, Action<Action> dispatch,
                          IRazerDevice? dock = null, Func<bool>? isDockPresent = null)
    {
        _device = device;
        _settings = settings;
        _dispatch = dispatch;
        _dock = dock;
        _isDockPresent = isDockPresent ?? (static () => false);
    }
```

(c) Add the compute helper and stamp `Dock` onto both states in `ProcessReading`. Replace the two `SetState(...)` calls in `ProcessReading` (`SetState(DeviceState.Unknown)` and `SetState(DeviceState.Online(r.Percent, r.IsCharging))`) with the `with`-stamped versions, and add `ComputeDock`:

```csharp
    internal void ProcessReading(BatteryReading r)
    {
        int threshold = _settings.Settings.LowBatteryThreshold;
        DockStatus dock = ComputeDock(r);

        if (!r.IsPresent)
        {
            _consecutiveMisses++;
            if (_consecutiveMisses > 3) SetState(DeviceState.Unknown with { Dock = dock });
            return;
        }
        _consecutiveMisses = 0;

        if (!r.IsCharging)
        {
            if (r.Percent > threshold)
            {
                _armed = true;
            }
            else if (_armed && r.Percent <= threshold)
            {
                _armed = false;
                if (_settings.Settings.LowBatteryNotify)
                    _dispatch(() => LowBatteryCrossed?.Invoke(this, r.Percent));
            }
        }

        SetState(DeviceState.Online(r.Percent, r.IsCharging) with { Dock = dock });
    }

    /// <summary>Dock line state: presence from passive enumeration; "charging" only when the device
    /// actually reports charging (presence alone is never "charging"). No extra dock feature I/O.</summary>
    private DockStatus ComputeDock(BatteryReading r)
    {
        if (!_isDockPresent()) return DockStatus.None;
        return r is { IsPresent: true, IsCharging: true } ? DockStatus.Charging : DockStatus.Connected;
    }
```

- [ ] **Step 6: Run the full suite**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: `Passed!` — all tests pass (37 + 2 new = 39), 0 failed.

- [ ] **Step 7: Wire the real predicate in `AppHost`**

In `src/NagaBatteryTray/AppHost.cs`, pass the live presence check to the monitor — replace the monitor construction line from Task 5:

```csharp
        _monitor = new BatteryMonitor(_device, _settings, Dispatch, _dock, RazerDevice.IsDockPresent);
```

- [ ] **Step 8: Build + commit**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build` (Expected: `Build succeeded`.)

```bash
git add src/NagaBatteryTray/Monitoring/DeviceState.cs src/NagaBatteryTray/Monitoring/BatteryMonitor.cs src/NagaBatteryTray/Hid/RazerDevice.cs src/NagaBatteryTray/AppHost.cs tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs
git commit -m "feat(monitor): compute dock presence/charging status (passive enumeration)"
```

---

### Task 7 (optional): Show the dock line in the popup

**Files:**
- Modify: `src/NagaBatteryTray/Ui/PopupViewModel.cs` (`DockText`, `DockVisible`, set in `Apply`)
- Modify: `src/NagaBatteryTray/Ui/PopupWindow.xaml` (one bound `TextBlock`)

**Interfaces:**
- Consumes: `DeviceState.Dock` (`DockStatus`).
- Produces: `PopupViewModel.DockText` (`string`), `PopupViewModel.DockVisible` (`bool`).

- [ ] **Step 1: Add the bound properties + set them in `Apply`**

In `src/NagaBatteryTray/Ui/PopupViewModel.cs`, add backing fields + properties next to `_charging`/`Charging`:

```csharp
    private bool _charging;
    private string _dockText = "";
    private bool _dockVisible;
```

```csharp
    public bool Charging { get => _charging; private set => Set(ref _charging, value); }
    public string DockText { get => _dockText; private set => Set(ref _dockText, value); }
    public bool DockVisible { get => _dockVisible; private set => Set(ref _dockVisible, value); }
```

Then, in `Apply(DeviceState s)`, set the dock line at the **top** of the method (before the `Unknown` early-return, so the line shows even when the mouse is not responding but the dock is plugged in):

```csharp
    public void Apply(DeviceState s)
    {
        DockVisible = s.Dock != DockStatus.None;
        DockText = s.Dock switch
        {
            DockStatus.Charging => "Dock: charging",
            DockStatus.Connected => "Dock: connected",
            _ => "",
        };

        if (s.Status == DeviceStatus.Unknown)
        {
            PercentText = "-";
            Status = "no response";
            BarFraction = 0;
            Accent = Media.Brushes.Gray;
            Charging = false;
            return;
        }

        PercentText = $"{s.Percent}%";
        Status = s.Charging ? "Charging" : "On battery";
        BarFraction = s.Percent / 100.0;
        Charging = s.Charging;
        var c = IconRenderer.ColorForLevel(s.Percent, s.Charging); // System.Drawing.Color
        Accent = new Media.SolidColorBrush(Media.Color.FromRgb(c.R, c.G, c.B));
    }
```

- [ ] **Step 2: Add the `TextBlock` to the popup**

In `src/NagaBatteryTray/Ui/PopupWindow.xaml`, add a dock line inside the inner `StackPanel`, immediately after the progress-bar `Border` (the one with `Height="6"`) and before the button row's `StackPanel`:

```xml
      <TextBlock Text="{Binding DockText}" FontSize="11" Foreground="#9AA0A6" Margin="0,8,0,0"
                 Visibility="{Binding DockVisible, Converter={StaticResource BoolToVis}}"/>
```

- [ ] **Step 3: Build**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 4: Manual UI check**

Run the app (`.\scripts\install.ps1` or `run --project`). With the dock plugged in, open the popup (left-click tray): the `Dock: connected` line shows (upgrading to `Dock: charging` when the mouse charges on it). Unplug the dock → line disappears.

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Ui/PopupViewModel.cs src/NagaBatteryTray/Ui/PopupWindow.xaml
git commit -m "feat(ui): show a dock connected/charging line in the popup when a dock is present"
```

---

## Notes for the implementer

- **Stop at the Task 2 gate.** Everything in Stage 2 assumes the spike confirmed the dock relays battery (expected tid `0x1f`). If it didn't, do not build Stage 2 as written — return to the spec §6 contingency.
- **Dock handle lifecycle:** `RazerDevice` opens the dock handle lazily on the first fallback read and keeps it until a failed HID call or `Dispose`. An idle open handle carries no traffic and no CPU, so it does not violate the lightweight invariant; the invariant that matters — *no dock feature I/O while the mouse is reachable* — is enforced by `ReadWithFallbackAsync` (mouse-first, dock only on `Absent`).
- **Why `ReadWithFallbackAsync` is `internal`:** the test project sees it via the existing `InternalsVisibleTo("NagaBatteryTray.Tests")`. Don't make it (or `ProcessReading`) private.
- **Tasks 6–7 are optional** and isolated; skipping them leaves goals #1/#2 fully delivered (battery/charging stay live while docked) with no UI change.
```
