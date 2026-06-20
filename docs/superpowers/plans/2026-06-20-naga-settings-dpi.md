# Phase 2-A: Settings Window + Active Mouse DPI — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Fluent Settings window to Razer Naga Companion that surfaces app settings and lets the user read/set the mouse's active hardware DPI — without adding weight or mouse-input latency.

**Architecture:** Extend the existing layers with thin, DRY additions: `RazerProtocol` gains DPI build/parse (via extracted `BuildReport`/`ValidateReply`); `RazerDevice` gains `Get/SetDpiAsync` over an extracted `ExchangeAsync` transport; `BatteryMonitor` gains blocking-lock DPI pass-throughs; a new `SettingsWindow`/`SettingsViewModel` (WPF-UI `FluentWindow`) is opened from the popup button + tray menu and wired in `AppHost`. DPI is device state (never persisted to JSON); it is read on open and written on an explicit Apply with read-back confirmation.

**Tech Stack:** C# / .NET 10 (`net10.0-windows10.0.19041.0`), WPF + WinForms, WPF-UI 4.3.0 (already referenced), HidSharp, xUnit. No new dependencies.

**Spec:** `docs/superpowers/specs/2026-06-20-naga-settings-dpi-design.md`.

**Branch:** Do this work on a feature branch `implement/settings-dpi-v1` (the executor creates it via superpowers:using-git-worktrees / executing-plans before Task 1). `master` is the base.

## Global Constraints

Every task implicitly includes these (verbatim from the spec):

- **Lightweight + zero mouse-input-latency are HARD, GATING requirements (spec §3.1).** No new background timers/threads; DPI I/O is **on-demand only** (no DPI polling); talk to the mouse only via HID **feature reports** (control endpoint) and never claim the input collection (we already open zero-access + `FILE_SHARE_READ|WRITE`); battery poll cadence floor **15 s**; DPI/battery I/O serialized through the single read lock; blocking HID calls run **off the UI thread** (`Task.Run`). Footprint returns to baseline (~0% idle CPU, ~23 MB private) and measured input latency is unchanged — both are acceptance gates (Task 6).
- **Target framework** `net10.0-windows10.0.19041.0`; WPF + WinForms; WPF-UI **4.3.0**. No new NuGet dependencies.
- **DPI HID protocol (OpenRazer-verified):** command_class `0x04`; GET id `0x85` (request `arg[0]=0x00` NOSTORE; reply X=`buffer[10..11]` BE, Y=`buffer[12..13]` BE); SET id `0x05` (`arg[0]=0x01` VARSTORE persist, X=`arg[1..2]` BE, Y=`arg[3..4]` BE); `data_size 0x07`; transaction id `0x1f`; CRC = XOR of report `[2..87]`; clamp 100–30000.
- **DPI is device state** — never written to `settings.json`; read live on window open.
- **DRY, YAGNI, TDD, frequent commits.** Conventional-commit messages. Surgical changes that preserve existing style (record structs for value types; `Set<T>`/`OnChanged` for view-models).

## File Structure

```
Create:
  src/NagaBatteryTray/Hid/DpiSetting.cs              value type {int X, int Y}
  src/NagaBatteryTray/Ui/SettingsViewModel.cs        INotifyPropertyChanged VM (pure, tested)
  src/NagaBatteryTray/Ui/DoubleIntConverter.cs       double?(NumberBox/Slider) <-> int(VM) binding bridge
  src/NagaBatteryTray/Ui/SettingsWindow.xaml(.cs)    ui:FluentWindow + CardControl rows
  tests/NagaBatteryTray.Tests/SettingsViewModelTests.cs
Modify:
  src/NagaBatteryTray/Hid/RazerProtocol.cs           DPI constants; BuildReport/ValidateReply extractions; Build/Parse DPI
  src/NagaBatteryTray/Hid/IRazerDevice.cs            + Get/SetDpiAsync
  src/NagaBatteryTray/Hid/RazerDevice.cs             ExchangeAsync extraction; Get/SetDpiAsync; CloseHandle on false
  src/NagaBatteryTray/Monitoring/BatteryMonitor.cs   Get/SetDpiAsync pass-throughs (blocking lock)
  src/NagaBatteryTray/Diagnostics/ProbeCommand.cs    + RunDpi() raw GET-DPI hex dump (gating offset check)
  src/NagaBatteryTray/Program.cs                     + "--probe-dpi" dispatch
  src/NagaBatteryTray/Ui/PopupWindow.xaml(.cs)       enable Settings button; SettingsRequested
  src/NagaBatteryTray/Ui/TrayIconController.cs        Settings menu item; SettingsRequested
  src/NagaBatteryTray/AppHost.cs                      single-window wiring; open/save/startup/apply-DPI; lifecycle
  tests/NagaBatteryTray.Tests/RazerProtocolTests.cs  DPI build/parse/clamp/range tests
  tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs  implement new interface members + assertion fields
```

`AppSettings.cs` is intentionally **not** modified — threshold + poll fields already exist; theme is cut; DPI lives on the mouse.

Build/test commands (user-local SDK):
- Build: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build C:\Users\Brandon\naga-battery-tray`
- Test: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test C:\Users\Brandon\naga-battery-tray --nologo`

---

## Task 1: Protocol — DRY extractions + DPI build/parse (pure, TDD)

**Files:**
- Modify: `src/NagaBatteryTray/Hid/RazerProtocol.cs`
- Test: `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs`

**Interfaces:**
- Consumes: existing `ComputeCrc`, `ReportLength`, `BufferLength`, `ReplyResult`.
- Produces: `BuildGetDpiBuffer(byte transactionId) : byte[]`, `BuildSetDpiBuffer(byte transactionId, int dpiX, int dpiY) : byte[]`, `ParseDpiReply(byte[] buffer91, out int dpiX, out int dpiY) : ReplyResult`, and constants `CommandClassDpi=0x04`, `CommandIdGetDpi=0x85`, `CommandIdSetDpi=0x05`, `DataSizeDpi=0x07`, `DpiMin=100`, `DpiMax=30000`. `BuildFeatureBuffer`/`ParseReply` keep their exact existing signatures.

- [ ] **Step 1: Write failing tests for the DPI buffers and parse**

Add to `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs` (inside the class):

```csharp
    private static byte[] MakeDpiReply(byte status, int x, int y)
    {
        var buf = new byte[91];
        buf[1] = status;          // report[0]
        buf[2] = 0x1f;            // transaction_id
        buf[6] = 0x07;            // data_size
        buf[7] = 0x04;            // command_class
        buf[8] = 0x85;            // command_id (GET DPI)
        buf[9] = 0x00;            // arg[0] varstore echo
        buf[10] = (byte)(x >> 8); buf[11] = (byte)x;   // X big-endian
        buf[12] = (byte)(y >> 8); buf[13] = (byte)y;   // Y big-endian
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        buf[89] = crc;
        return buf;
    }

    [Fact]
    public void GetDpi_buffer_has_correct_layout_and_crc()
    {
        byte[] buf = RazerProtocol.BuildGetDpiBuffer(0x1f);
        Assert.Equal(91, buf.Length);
        Assert.Equal(0x1f, buf[2]);  // transaction_id
        Assert.Equal(0x07, buf[6]);  // data_size
        Assert.Equal(0x04, buf[7]);  // command_class
        Assert.Equal(0x85, buf[8]);  // command_id (GET)
        Assert.Equal(0x00, buf[9]);  // arg[0] NOSTORE
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        Assert.Equal(crc, buf[89]);
    }

    [Fact]
    public void SetDpi_buffer_encodes_xy_big_endian_with_varstore()
    {
        byte[] buf = RazerProtocol.BuildSetDpiBuffer(0x1f, 1600, 1600); // 1600 = 0x0640
        Assert.Equal(0x07, buf[6]);  // data_size
        Assert.Equal(0x04, buf[7]);  // command_class
        Assert.Equal(0x05, buf[8]);  // command_id (SET)
        Assert.Equal(0x01, buf[9]);  // VARSTORE
        Assert.Equal(0x06, buf[10]); Assert.Equal(0x40, buf[11]); // X
        Assert.Equal(0x06, buf[12]); Assert.Equal(0x40, buf[13]); // Y
        Assert.Equal(0x00, buf[14]); Assert.Equal(0x00, buf[15]);
    }

    [Fact]
    public void SetDpi_buffer_clamps_to_100_30000()
    {
        byte[] lo = RazerProtocol.BuildSetDpiBuffer(0x1f, 50, 50);    // -> 100 = 0x0064
        Assert.Equal(0x00, lo[10]); Assert.Equal(0x64, lo[11]);
        byte[] hi = RazerProtocol.BuildSetDpiBuffer(0x1f, 99999, 99999); // -> 30000 = 0x7530
        Assert.Equal(0x75, hi[10]); Assert.Equal(0x30, hi[11]);
    }

    [Fact]
    public void ParseDpiReply_success_decodes_xy()
    {
        var r = RazerProtocol.ParseDpiReply(MakeDpiReply(0x02, 1600, 800), out int x, out int y);
        Assert.Equal(ReplyResult.Success, r);
        Assert.Equal(1600, x);
        Assert.Equal(800, y);
    }

    [Fact]
    public void ParseDpiReply_busy_and_bad_crc_and_out_of_range_fail()
    {
        Assert.Equal(ReplyResult.Busy, RazerProtocol.ParseDpiReply(MakeDpiReply(0x01, 1600, 1600), out _, out _));
        var bad = MakeDpiReply(0x02, 1600, 1600); bad[89] ^= 0xFF;
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseDpiReply(bad, out _, out _));
        // valid CRC but decoded X below DpiMin -> Failed (guards against wrong-layout firmware)
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseDpiReply(MakeDpiReply(0x02, 50, 1600), out _, out _));
    }
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test C:\Users\Brandon\naga-battery-tray --nologo`
Expected: FAIL — `BuildGetDpiBuffer` / `BuildSetDpiBuffer` / `ParseDpiReply` do not exist (compile error).

- [ ] **Step 3: Add constants + the DRY extractions + DPI methods**

In `src/NagaBatteryTray/Hid/RazerProtocol.cs`, add the constants after `public const byte DataSize = 0x02;`:

```csharp
    public const byte CommandClassDpi = 0x04;
    public const byte CommandIdGetDpi = 0x85;
    public const byte CommandIdSetDpi = 0x05;
    public const byte DataSizeDpi = 0x07;
    public const int DpiMin = 100;
    public const int DpiMax = 30000;
```

Replace the body of `BuildFeatureBuffer` and add `BuildReport` + the DPI builders. Replace the existing `BuildFeatureBuffer` method (lines ~31-47) with:

```csharp
    /// <summary>Assembles a 90-byte report (payload args start at report[8]) into the 91-byte feature buffer.
    /// report[0] (status) and report[89] (reserved) and buffer[0] (report id) stay 0x00.</summary>
    private static byte[] BuildReport(byte transactionId, byte dataSize, byte commandClass, byte commandId, ReadOnlySpan<byte> payload)
    {
        var report = new byte[ReportLength];
        report[1] = transactionId;
        report[5] = dataSize;
        report[6] = commandClass;
        report[7] = commandId;
        for (int i = 0; i < payload.Length; i++) report[8 + i] = payload[i];
        report[88] = ComputeCrc(report);

        var buffer = new byte[BufferLength];
        Array.Copy(report, 0, buffer, 1, ReportLength);
        return buffer;
    }

    /// <summary>Builds the 91-byte HID feature buffer (report id 0x00 + 90-byte report) for a power-class query.</summary>
    public static byte[] BuildFeatureBuffer(byte transactionId, byte commandId) =>
        BuildReport(transactionId, DataSize, CommandClassPower, commandId, ReadOnlySpan<byte>.Empty);

    /// <summary>GET active DPI (X/Y). Request arg[0]=0x00 (NOSTORE).</summary>
    public static byte[] BuildGetDpiBuffer(byte transactionId)
    {
        Span<byte> args = stackalloc byte[7]; // all zero
        return BuildReport(transactionId, DataSizeDpi, CommandClassDpi, CommandIdGetDpi, args);
    }

    /// <summary>SET active DPI (X/Y), persisted to onboard memory (VARSTORE). Values clamped 100..30000.</summary>
    public static byte[] BuildSetDpiBuffer(byte transactionId, int dpiX, int dpiY)
    {
        dpiX = Math.Clamp(dpiX, DpiMin, DpiMax);
        dpiY = Math.Clamp(dpiY, DpiMin, DpiMax);
        Span<byte> args = stackalloc byte[7];
        args[0] = 0x01;                                     // VARSTORE = persist
        args[1] = (byte)(dpiX >> 8); args[2] = (byte)dpiX;  // X big-endian
        args[3] = (byte)(dpiY >> 8); args[4] = (byte)dpiY;  // Y big-endian
        return BuildReport(transactionId, DataSizeDpi, CommandClassDpi, CommandIdSetDpi, args);
    }
```

Replace the existing `ParseReply` (lines ~49-63) with the extracted `ValidateReply` + refactored `ParseReply` + new `ParseDpiReply`:

```csharp
    /// <summary>Validates a 91-byte reply: status byte then XOR CRC over buffer[3..88] vs buffer[89].</summary>
    private static ReplyResult ValidateReply(byte[] buffer91)
    {
        byte status = buffer91[1];
        if (status == 0x01) return ReplyResult.Busy;
        if (status != 0x02) return ReplyResult.Failed;
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buffer91[i];
        if (crc != buffer91[89]) return ReplyResult.Failed;
        return ReplyResult.Success;
    }

    /// <summary>Validates a feature reply. value = report byte[9] (buffer[10]) on success.</summary>
    public static ReplyResult ParseReply(byte[] buffer91, out byte value)
    {
        value = 0;
        var r = ValidateReply(buffer91);
        if (r != ReplyResult.Success) return r;
        value = buffer91[10];
        return ReplyResult.Success;
    }

    /// <summary>Validates a DPI reply and decodes X=buffer[10..11], Y=buffer[12..13] (big-endian).
    /// A decoded value outside 100..30000 is treated as Failed (defends against wrong-layout replies).</summary>
    public static ReplyResult ParseDpiReply(byte[] buffer91, out int dpiX, out int dpiY)
    {
        dpiX = 0; dpiY = 0;
        var r = ValidateReply(buffer91);
        if (r != ReplyResult.Success) return r;
        int x = (buffer91[10] << 8) | buffer91[11];
        int y = (buffer91[12] << 8) | buffer91[13];
        if (x < DpiMin || x > DpiMax || y < DpiMin || y > DpiMax) return ReplyResult.Failed;
        dpiX = x; dpiY = y;
        return ReplyResult.Success;
    }
```

- [ ] **Step 4: Run all tests to verify they pass (incl. the byte-identical regression)**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test C:\Users\Brandon\naga-battery-tray --nologo`
Expected: PASS — all tests green (existing suite + 5 new DPI tests). The existing `Battery_query_buffer_has_correct_layout_and_crc` / `Charging_query_crc_is_0x81` passing **proves the `BuildReport` refactor is byte-identical** for battery.

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Hid/RazerProtocol.cs tests/NagaBatteryTray.Tests/RazerProtocolTests.cs
git commit -m "feat(hid): DPI get/set report build + parse with DRY BuildReport/ValidateReply"
```

---

## Task 2: Device — DPI methods + transport extraction + gating offset check

**Files:**
- Create: `src/NagaBatteryTray/Hid/DpiSetting.cs`
- Modify: `src/NagaBatteryTray/Hid/IRazerDevice.cs`, `src/NagaBatteryTray/Hid/RazerDevice.cs`, `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs`, `src/NagaBatteryTray/Diagnostics/ProbeCommand.cs`, `src/NagaBatteryTray/Program.cs`

**Interfaces:**
- Consumes: `RazerProtocol.BuildGetDpiBuffer/BuildSetDpiBuffer/ParseDpiReply` (Task 1); existing `EnsureOpen`, `ResolveTransactionIdAsync`, `CloseHandle`, `LogOnce`, `HidD_SetFeature/GetFeature`.
- Produces: `record struct DpiSetting(int X, int Y)`; `IRazerDevice.GetDpiAsync(CancellationToken) : Task<DpiSetting?>`, `IRazerDevice.SetDpiAsync(int dpiX, int dpiY, CancellationToken) : Task<bool>`; `RazerDevice` implementations; `FakeRazerDevice` with `DpiSetting? Dpi`, `bool SetDpiResult`, `int LastSetX`, `int LastSetY`.

- [ ] **Step 1: Create the value type**

Create `src/NagaBatteryTray/Hid/DpiSetting.cs`:

```csharp
namespace NagaBatteryTray.Hid;

public readonly record struct DpiSetting(int X, int Y);
```

- [ ] **Step 2: Extend the interface**

In `src/NagaBatteryTray/Hid/IRazerDevice.cs`, add to the interface body (after `ReadAsync`):

```csharp
    Task<DpiSetting?> GetDpiAsync(CancellationToken ct);
    Task<bool> SetDpiAsync(int dpiX, int dpiY, CancellationToken ct);
```

- [ ] **Step 3: Update the fake so the test project compiles**

Replace `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs` with:

```csharp
using NagaBatteryTray.Hid;

public sealed class FakeRazerDevice : IRazerDevice
{
    private readonly Queue<BatteryReading> _queue = new();
    public void Enqueue(BatteryReading r) => _queue.Enqueue(r);
    public Task<BatteryReading> ReadAsync(CancellationToken ct) =>
        Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : BatteryReading.Absent(DateTimeOffset.Now));

    public DpiSetting? Dpi { get; set; }
    public bool SetDpiResult { get; set; } = true;
    public int LastSetX { get; private set; }
    public int LastSetY { get; private set; }
    public Task<DpiSetting?> GetDpiAsync(CancellationToken ct) => Task.FromResult(Dpi);
    public Task<bool> SetDpiAsync(int dpiX, int dpiY, CancellationToken ct)
    {
        LastSetX = dpiX; LastSetY = dpiY;
        return Task.FromResult(SetDpiResult);
    }

    public void Dispose() { }
}
```

- [ ] **Step 4: Extract `ExchangeAsync` and add the DPI methods in `RazerDevice`**

In `src/NagaBatteryTray/Hid/RazerDevice.cs`, replace the existing `QueryAsync` method (lines ~96-116) with the extracted transport + a thin `QueryAsync`:

```csharp
    /// <summary>One SET->wait->GET round-trip with one busy retry. Returns the raw 91-byte reply or null on failure.
    /// On a failed HID call the handle is closed so the next EnsureOpen re-acquires.</summary>
    private async Task<byte[]?> ExchangeAsync(byte[] request, CancellationToken ct)
    {
        if (_handle is null || _handle.IsInvalid) return null;
        if (!HidD_SetFeature(_handle, request, request.Length)) { CloseHandle(); return null; }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(_settings.Settings.SetReadDelayMs, ct);
            var reply = new byte[RazerProtocol.BufferLength];
            reply[0] = 0x00;
            if (!HidD_GetFeature(_handle, reply, reply.Length)) { CloseHandle(); return null; }
            if (reply[1] == 0x01) { await Task.Delay(200, ct); continue; } // Busy: wait, retry GET once
            return reply;
        }
        return null; // still busy after retries
    }

    /// <summary>SET->GET a power-class query and return the data byte, or null on failure.</summary>
    private async Task<byte?> QueryAsync(byte transactionId, byte commandId, CancellationToken ct)
    {
        var reply = await ExchangeAsync(RazerProtocol.BuildFeatureBuffer(transactionId, commandId), ct);
        if (reply is null) return null;
        return RazerProtocol.ParseReply(reply, out byte value) == ReplyResult.Success ? value : null;
    }

    public async Task<DpiSetting?> GetDpiAsync(CancellationToken ct)
    {
        try
        {
            if (!EnsureOpen()) return null;
            byte tid = await ResolveTransactionIdAsync(ct);
            if (tid == 0) return null;
            var reply = await ExchangeAsync(RazerProtocol.BuildGetDpiBuffer(tid), ct);
            if (reply is null) return null;
            if (RazerProtocol.ParseDpiReply(reply, out int x, out int y) != ReplyResult.Success) return null;
            return new DpiSetting(x, y);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseHandle();
            LogOnce(ex);
            return null;
        }
    }

    public async Task<bool> SetDpiAsync(int dpiX, int dpiY, CancellationToken ct)
    {
        try
        {
            if (!EnsureOpen()) return false;
            byte tid = await ResolveTransactionIdAsync(ct);
            if (tid == 0) return false;
            var reply = await ExchangeAsync(RazerProtocol.BuildSetDpiBuffer(tid, dpiX, dpiY), ct);
            if (reply is null) return false;
            return RazerProtocol.ParseDpiReply(reply, out _, out _) == ReplyResult.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseHandle();
            LogOnce(ex);
            return false;
        }
    }
```

- [ ] **Step 5: Build and run the suite (no behavior change expected)**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test C:\Users\Brandon\naga-battery-tray --nologo`
Expected: PASS — all existing tests still green (battery path unchanged; `ExchangeAsync` preserves SET→GET→busy-retry semantics).

- [ ] **Step 6: Add a `--probe-dpi` diagnostic for the gating offset check**

In `src/NagaBatteryTray/Diagnostics/ProbeCommand.cs`, add `using System.Linq;` to the usings, and add this method to the class:

```csharp
    public static int RunDpi()
    {
        Console.WriteLine("Naga Battery Tray - GET DPI probe (raw hex for offset verification)\n");
        foreach (int pid in new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidWired })
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                int max = -1;
                try { max = dev.GetMaxFeatureReportLength(); } catch { }
                if (max != RazerProtocol.BufferLength) continue;

                using var h = CreateFile(dev.DevicePath, 0, 0x3, IntPtr.Zero, 0x3, 0, IntPtr.Zero);
                if (h.IsInvalid) { Console.WriteLine($"CreateFile failed err={Marshal.GetLastWin32Error()}"); continue; }

                byte tid = 0x1f;
                var buf = RazerProtocol.BuildGetDpiBuffer(tid);
                if (!HidD_SetFeature(h, buf, buf.Length)) { Console.WriteLine($"SetFeature failed err={Marshal.GetLastWin32Error()}"); continue; }
                Thread.Sleep(400);
                var reply = new byte[RazerProtocol.BufferLength];
                reply[0] = 0;
                if (!HidD_GetFeature(h, reply, reply.Length)) { Console.WriteLine($"GetFeature failed err={Marshal.GetLastWin32Error()}"); continue; }

                Console.WriteLine($"PID 0x{pid:x4} status=0x{reply[1]:x2}");
                Console.WriteLine("  reply[0..15]: " + string.Join(" ", reply.Take(16).Select(b => b.ToString("x2"))));
                int x = (reply[10] << 8) | reply[11];
                int y = (reply[12] << 8) | reply[13];
                Console.WriteLine($"  decoded @offsets[10..13]: X={x} Y={y}");
            }
        Console.WriteLine("\nIf you set DPI to 1600 in another tool first, expect reply[10..11] = 06 40 and X=1600.");
        return 0;
    }
```

In `src/NagaBatteryTray/Program.cs`, add the dispatch next to the existing `--probe` block (after it):

```csharp
        if (args.Length > 0 && args[0] == "--probe-dpi")
        {
            AllocConsoleIfNeeded();
            return Diagnostics.ProbeCommand.RunDpi();
        }
```

- [ ] **Step 7: GATING hardware verification of the DPI reply offsets**

Build the app, then (with the mouse awake; ideally set DPI to a known round value like 1600 in another tool or note the current value) run:

```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build C:\Users\Brandon\naga-battery-tray -c Debug
& "C:\Users\Brandon\naga-battery-tray\src\NagaBatteryTray\bin\Debug\net10.0-windows10.0.19041.0\NagaBatteryTray.exe" --probe-dpi
```

Expected: `status=0x02` and a sensible `X=`/`Y=` matching the mouse's real DPI (e.g. `X=1600` with `reply[10..11] = 06 40`).
**If the decoded X/Y are wrong** (garbage, or shifted), the reply layout differs from OpenRazer's generic getter — adjust the offsets in `RazerProtocol.ParseDpiReply` and the `MakeDpiReply` test helper to match the observed bytes, re-run Task 1 tests, then re-verify. Do not proceed until decoded DPI is correct.

- [ ] **Step 8: Commit**

```bash
git add src/NagaBatteryTray/Hid/DpiSetting.cs src/NagaBatteryTray/Hid/IRazerDevice.cs src/NagaBatteryTray/Hid/RazerDevice.cs src/NagaBatteryTray/Diagnostics/ProbeCommand.cs src/NagaBatteryTray/Program.cs tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs
git commit -m "feat(hid): RazerDevice Get/SetDpiAsync over extracted ExchangeAsync + --probe-dpi"
```

---

## Task 3: BatteryMonitor — DPI pass-throughs (blocking lock, TDD via fake)

**Files:**
- Modify: `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs`
- Test: `tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs`

**Interfaces:**
- Consumes: `IRazerDevice.GetDpiAsync/SetDpiAsync` (Task 2); existing `_readLock`, `_cts`, `_device`.
- Produces: `BatteryMonitor.GetDpiAsync() : Task<DpiSetting?>`, `BatteryMonitor.SetDpiAsync(int dpiX, int dpiY) : Task<bool>` — both serialize against the battery poll via the existing `_readLock` with a **blocking** wait (unlike `PollAsync`'s skip-if-busy).

- [ ] **Step 1: Write failing tests**

Add to `tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs`:

```csharp
    private static ISettingsStore TempStore() =>
        new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"naga-{Guid.NewGuid():N}.json"));

    [Fact]
    public async Task SetDpiAsync_routes_to_device_and_returns_result()
    {
        var fake = new FakeRazerDevice { SetDpiResult = true };
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        bool ok = await m.SetDpiAsync(1600, 1600);
        Assert.True(ok);
        Assert.Equal(1600, fake.LastSetX);
        Assert.Equal(1600, fake.LastSetY);
    }

    [Fact]
    public async Task GetDpiAsync_returns_device_value()
    {
        var fake = new FakeRazerDevice { Dpi = new DpiSetting(800, 800) };
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        Assert.Equal(new DpiSetting(800, 800), await m.GetDpiAsync());
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test C:\Users\Brandon\naga-battery-tray --nologo`
Expected: FAIL — `BatteryMonitor.SetDpiAsync`/`GetDpiAsync` do not exist.

- [ ] **Step 3: Add the pass-throughs**

In `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs`, add `using NagaBatteryTray.Hid;` is already present. Add these methods (e.g. after `RefreshNowAsync`):

```csharp
    /// <summary>Read the mouse's active DPI. Blocks for the read lock (never skips) so it can't be dropped
    /// mid-poll, and serializes against the battery poll on the single HID handle.</summary>
    public async Task<DpiSetting?> GetDpiAsync()
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return null; }
        try { return await _device.GetDpiAsync(_cts.Token); }
        catch (OperationCanceledException) { return null; }
        finally { _readLock.Release(); }
    }

    /// <summary>Set the mouse's active DPI (persisted on the device). Blocks for the read lock.</summary>
    public async Task<bool> SetDpiAsync(int dpiX, int dpiY)
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return false; }
        try { return await _device.SetDpiAsync(dpiX, dpiY, _cts.Token); }
        catch (OperationCanceledException) { return false; }
        finally { _readLock.Release(); }
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test C:\Users\Brandon\naga-battery-tray --nologo`
Expected: PASS — all tests green.

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Monitoring/BatteryMonitor.cs tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs
git commit -m "feat(monitor): DPI get/set pass-throughs serialized through the read lock"
```

---

## Task 4: SettingsViewModel (pure, TDD)

**Files:**
- Create: `src/NagaBatteryTray/Ui/SettingsViewModel.cs`
- Test: `tests/NagaBatteryTray.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: `AppSettings` (fields `LowBatteryThreshold`, `PollIntervalSeconds`, `PollIntervalChargingSeconds`, `CachedTransactionId`); `DpiSetting`; `RazerProtocol.DpiMin/DpiMax`.
- Produces: `SettingsViewModel(AppSettings source, bool runAtStartup)`; properties `int LowBatteryThreshold`, `int PollSeconds`, `int PollChargingSeconds`, `bool RunAtStartup`, `int Dpi` (clamped), `string CurrentDpiText`, `string DpiStatus`, `bool DevicePresent`; `void ApplyTo(AppSettings target)` (clamps: threshold 1..100, cadence floor 15); `void SetCurrentDpi(DpiSetting? dpi)`.

- [ ] **Step 1: Write failing tests**

Create `tests/NagaBatteryTray.Tests/SettingsViewModelTests.cs`:

```csharp
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Ui;
using Xunit;

public class SettingsViewModelTests
{
    private static AppSettings Sample() => new()
    {
        LowBatteryThreshold = 20,
        PollIntervalSeconds = 60,
        PollIntervalChargingSeconds = 15,
        CachedTransactionId = "0x1f",
    };

    [Fact]
    public void Ctor_copies_editable_fields()
    {
        var vm = new SettingsViewModel(Sample(), runAtStartup: true);
        Assert.Equal(20, vm.LowBatteryThreshold);
        Assert.Equal(60, vm.PollSeconds);
        Assert.Equal(15, vm.PollChargingSeconds);
        Assert.True(vm.RunAtStartup);
    }

    [Fact]
    public void ApplyTo_clamps_and_preserves_unedited_fields()
    {
        var vm = new SettingsViewModel(Sample(), false)
        {
            LowBatteryThreshold = 150, // -> 100
            PollSeconds = 2,           // -> 15 (floor)
            PollChargingSeconds = 9,   // -> 15 (floor)
        };
        var target = Sample();
        vm.ApplyTo(target);
        Assert.Equal(100, target.LowBatteryThreshold);
        Assert.Equal(15, target.PollIntervalSeconds);
        Assert.Equal(15, target.PollIntervalChargingSeconds);
        Assert.Equal("0x1f", target.CachedTransactionId); // untouched
    }

    [Fact]
    public void Dpi_setter_clamps_100_to_30000()
    {
        var vm = new SettingsViewModel(Sample(), false);
        vm.Dpi = 50;     Assert.Equal(100, vm.Dpi);
        vm.Dpi = 99999;  Assert.Equal(30000, vm.Dpi);
        vm.Dpi = 1600;   Assert.Equal(1600, vm.Dpi);
    }

    [Fact]
    public void SetCurrentDpi_seeds_value_and_marks_present()
    {
        var vm = new SettingsViewModel(Sample(), false);
        vm.SetCurrentDpi(new DpiSetting(1600, 1600));
        Assert.Equal(1600, vm.Dpi);
        Assert.Contains("1600", vm.CurrentDpiText);
        Assert.True(vm.DevicePresent);
    }

    [Fact]
    public void SetCurrentDpi_null_marks_unknown_and_absent()
    {
        var vm = new SettingsViewModel(Sample(), false);
        vm.SetCurrentDpi(null);
        Assert.Equal("Current: unknown", vm.CurrentDpiText);
        Assert.False(vm.DevicePresent);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test C:\Users\Brandon\naga-battery-tray --nologo`
Expected: FAIL — `SettingsViewModel` does not exist.

- [ ] **Step 3: Implement the view-model**

Create `src/NagaBatteryTray/Ui/SettingsViewModel.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Ui;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private int _lowBatteryThreshold;
    private int _pollSeconds;
    private int _pollChargingSeconds;
    private bool _runAtStartup;
    private int _dpi = RazerProtocol.DpiMin;
    private string _currentDpiText = "Current: unknown";
    private string _dpiStatus = "";
    private bool _devicePresent;

    public SettingsViewModel(AppSettings source, bool runAtStartup)
    {
        _lowBatteryThreshold = source.LowBatteryThreshold;
        _pollSeconds = source.PollIntervalSeconds;
        _pollChargingSeconds = source.PollIntervalChargingSeconds;
        _runAtStartup = runAtStartup;
    }

    public int LowBatteryThreshold { get => _lowBatteryThreshold; set => Set(ref _lowBatteryThreshold, value); }
    public int PollSeconds { get => _pollSeconds; set => Set(ref _pollSeconds, value); }
    public int PollChargingSeconds { get => _pollChargingSeconds; set => Set(ref _pollChargingSeconds, value); }
    public bool RunAtStartup { get => _runAtStartup; set => Set(ref _runAtStartup, value); }

    public int Dpi
    {
        get => _dpi;
        set => Set(ref _dpi, Math.Clamp(value, RazerProtocol.DpiMin, RazerProtocol.DpiMax));
    }

    public string CurrentDpiText { get => _currentDpiText; set => Set(ref _currentDpiText, value); }
    public string DpiStatus { get => _dpiStatus; set => Set(ref _dpiStatus, value); }
    public bool DevicePresent { get => _devicePresent; set => Set(ref _devicePresent, value); }

    /// <summary>Writes clamped edited values into the live settings instance (cadence floor 15 s, threshold 1..100).
    /// Leaves unedited fields (CachedTransactionId, SetReadDelayMs, LowBatteryNotify) untouched.</summary>
    public void ApplyTo(AppSettings target)
    {
        target.LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 1, 100);
        target.PollIntervalSeconds = Math.Max(15, PollSeconds);
        target.PollIntervalChargingSeconds = Math.Max(15, PollChargingSeconds);
    }

    public void SetCurrentDpi(DpiSetting? dpi)
    {
        if (dpi is { } d)
        {
            Dpi = d.X;
            CurrentDpiText = $"Current: {d.X} DPI";
            DevicePresent = true;
        }
        else
        {
            CurrentDpiText = "Current: unknown";
            DevicePresent = false;
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test C:\Users\Brandon\naga-battery-tray --nologo`
Expected: PASS — all tests green.

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Ui/SettingsViewModel.cs tests/NagaBatteryTray.Tests/SettingsViewModelTests.cs
git commit -m "feat(ui): SettingsViewModel with clamped settings + DPI state"
```

---

## Task 5: SettingsWindow (FluentWindow + code-behind, manual verify)

**Files:**
- Create: `src/NagaBatteryTray/Ui/DoubleIntConverter.cs`, `src/NagaBatteryTray/Ui/SettingsWindow.xaml`, `src/NagaBatteryTray/Ui/SettingsWindow.xaml.cs`

**Interfaces:**
- Consumes: `SettingsViewModel` (Task 4); `AppSettings`; `DpiSetting`.
- Produces: `DoubleIntConverter : IValueConverter` (bridges `double?`/`double` controls to `int` VM properties; null → `Binding.DoNothing`).
- Produces: `SettingsWindow(AppSettings source, bool runAtStartup)`; events `event Action? SaveRequested`, `event Action<bool>? StartupToggled`, `event Action<int>? ApplyDpiRequested`; methods `void ApplyTo(AppSettings target)`, `void SetCurrentDpi(DpiSetting? dpi)`, `void SetDpiStatus(string text)`, `void SetDevicePresent(bool present)`.

- [ ] **Step 1: Create the double↔int binding converter, then the XAML**

`ui:NumberBox.Value` is `double?` and `Slider.Value` is `double`; the VM properties are `int`. Bind through this converter so a transient empty/null box never zeroes an `int` property.

Create `src/NagaBatteryTray/Ui/DoubleIntConverter.cs`:

```csharp
using System.Globalization;
using System.Windows.Data;

namespace NagaBatteryTray.Ui;

/// <summary>Bridges WPF-UI NumberBox (double?) / Slider (double) to int view-model properties.
/// A null/blank entry is ignored (Binding.DoNothing) so a transient empty box never zeroes the value.</summary>
public sealed class DoubleIntConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i ? (double)i : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? (int)Math.Round(d) : Binding.DoNothing;
}
```

Create `src/NagaBatteryTray/Ui/SettingsWindow.xaml`:

```xml
<ui:FluentWindow x:Class="NagaBatteryTray.Ui.SettingsWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
    xmlns:local="clr-namespace:NagaBatteryTray.Ui"
    Title="Razer Naga Companion — Settings"
    Width="440" Height="560" MinWidth="400" MinHeight="460"
    WindowStartupLocation="CenterScreen"
    ExtendsContentIntoTitleBar="True"
    WindowBackdropType="None"
    WindowCornerPreference="Round">
  <ui:FluentWindow.Resources>
    <local:DoubleIntConverter x:Key="DoubleInt"/>
  </ui:FluentWindow.Resources>
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
    </Grid.RowDefinitions>

    <ui:TitleBar Grid.Row="0" Title="Settings"/>

    <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto" Padding="16,8,16,16">
      <StackPanel>

        <!-- Run at startup -->
        <ui:CardControl Margin="0,0,0,8" Icon="{ui:SymbolIcon Power24}">
          <ui:CardControl.Header>
            <StackPanel>
              <TextBlock Text="Run at startup" FontWeight="Medium"/>
              <TextBlock Text="Launch when you sign in" Opacity="0.7" FontSize="12"/>
            </StackPanel>
          </ui:CardControl.Header>
          <ui:ToggleSwitch x:Name="StartupToggle" IsChecked="{Binding RunAtStartup, Mode=TwoWay}"
                           Checked="OnStartupToggled" Unchecked="OnStartupToggled"/>
        </ui:CardControl>

        <!-- Low-battery threshold -->
        <ui:CardControl Margin="0,0,0,8" Icon="{ui:SymbolIcon BatteryWarning24}">
          <ui:CardControl.Header>
            <StackPanel>
              <TextBlock Text="Low-battery alert" FontWeight="Medium"/>
              <TextBlock Text="Notify at or below this percent" Opacity="0.7" FontSize="12"/>
            </StackPanel>
          </ui:CardControl.Header>
          <ui:NumberBox MinWidth="120" Value="{Binding LowBatteryThreshold, Mode=TwoWay, Converter={StaticResource DoubleInt}}"
                        Minimum="1" Maximum="100" SmallChange="5" MaxDecimalPlaces="0"/>
        </ui:CardControl>

        <!-- Mouse DPI -->
        <ui:CardControl Margin="0,0,0,8" Icon="{ui:SymbolIcon TopSpeed24}">
          <ui:CardControl.Header>
            <StackPanel>
              <TextBlock Text="Mouse DPI" FontWeight="Medium"/>
              <TextBlock Text="{Binding CurrentDpiText}" Opacity="0.7" FontSize="12"/>
            </StackPanel>
          </ui:CardControl.Header>
          <StackPanel MinWidth="220">
            <Slider Minimum="100" Maximum="30000" Value="{Binding Dpi, Mode=TwoWay, Converter={StaticResource DoubleInt}}"
                    IsEnabled="{Binding DevicePresent}" TickFrequency="100" LargeChange="1000" SmallChange="50"/>
            <ui:NumberBox Margin="0,8,0,0" Value="{Binding Dpi, Mode=TwoWay, Converter={StaticResource DoubleInt}}"
                          IsEnabled="{Binding DevicePresent}"
                          Minimum="100" Maximum="30000" SmallChange="50" MaxDecimalPlaces="0"/>
            <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
              <ui:Button Content="Apply DPI" Appearance="Primary" Click="OnApplyDpi"
                         IsEnabled="{Binding DevicePresent}"/>
              <TextBlock Text="{Binding DpiStatus}" VerticalAlignment="Center" Margin="10,0,0,0"
                         Opacity="0.8" FontSize="12"/>
            </StackPanel>
          </StackPanel>
        </ui:CardControl>

        <!-- Advanced: polling cadence -->
        <ui:CardExpander Margin="0,0,0,8" Icon="{ui:SymbolIcon Settings24}">
          <ui:CardExpander.Header>
            <StackPanel>
              <TextBlock Text="Advanced" FontWeight="Medium"/>
              <TextBlock Text="How often to read the battery" Opacity="0.7" FontSize="12"/>
            </StackPanel>
          </ui:CardExpander.Header>
          <StackPanel Margin="0,4,0,0">
            <Grid Margin="0,4">
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/>
              </Grid.ColumnDefinitions>
              <TextBlock Text="On battery (seconds)" VerticalAlignment="Center"/>
              <ui:NumberBox Grid.Column="1" MinWidth="120" Value="{Binding PollSeconds, Mode=TwoWay, Converter={StaticResource DoubleInt}}"
                            Minimum="15" Maximum="3600" SmallChange="15" MaxDecimalPlaces="0"/>
            </Grid>
            <Grid Margin="0,4">
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/>
              </Grid.ColumnDefinitions>
              <TextBlock Text="While charging (seconds)" VerticalAlignment="Center"/>
              <ui:NumberBox Grid.Column="1" MinWidth="120" Value="{Binding PollChargingSeconds, Mode=TwoWay, Converter={StaticResource DoubleInt}}"
                            Minimum="15" Maximum="3600" SmallChange="15" MaxDecimalPlaces="0"/>
            </Grid>
          </StackPanel>
        </ui:CardExpander>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,8,0,0">
          <ui:Button Content="Close" Click="OnClose"/>
        </StackPanel>

      </StackPanel>
    </ScrollViewer>
  </Grid>
</ui:FluentWindow>
```

- [ ] **Step 2: Create the code-behind**

Create `src/NagaBatteryTray/Ui/SettingsWindow.xaml.cs`:

```csharp
using System.Windows;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;
using Wpf.Ui.Controls;

namespace NagaBatteryTray.Ui;

public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsViewModel _vm;

    public event Action? SaveRequested;        // raised on close — persist threshold/cadence
    public event Action<bool>? StartupToggled;  // raised immediately on the toggle
    public event Action<int>? ApplyDpiRequested; // raised on the Apply button

    public SettingsWindow(AppSettings source, bool runAtStartup)
    {
        InitializeComponent();
        _vm = new SettingsViewModel(source, runAtStartup);
        DataContext = _vm;
        Closed += (_, _) => SaveRequested?.Invoke();
    }

    public void ApplyTo(AppSettings target) => _vm.ApplyTo(target);
    public void SetCurrentDpi(DpiSetting? dpi) => _vm.SetCurrentDpi(dpi);
    public void SetDpiStatus(string text) => _vm.DpiStatus = text;
    public void SetDevicePresent(bool present) => _vm.DevicePresent = present;

    private void OnStartupToggled(object sender, RoutedEventArgs e) => StartupToggled?.Invoke(_vm.RunAtStartup);
    private void OnApplyDpi(object sender, RoutedEventArgs e) => ApplyDpiRequested?.Invoke(_vm.Dpi);
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 3: Build**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build C:\Users\Brandon\naga-battery-tray`
Expected: build succeeds (the window isn't shown yet — wired in Task 6). Note: `Checked`/`Unchecked` on `ui:ToggleSwitch` and the bindings resolve against WPF-UI 4.3.0. If `ui:ToggleSwitch` lacks `Checked`/`Unchecked` routed events in this version, fall back to binding `IsChecked` two-way only and move the startup-apply into `OnClose`/`SaveRequested` (note for the implementer; verify against the built DLL).

- [ ] **Step 4: Commit**

```bash
git add src/NagaBatteryTray/Ui/DoubleIntConverter.cs src/NagaBatteryTray/Ui/SettingsWindow.xaml src/NagaBatteryTray/Ui/SettingsWindow.xaml.cs
git commit -m "feat(ui): SettingsWindow FluentWindow (threshold, startup, DPI, advanced cadence)"
```

---

## Task 6: Wiring + lifecycle + NFR acceptance (manual verify)

**Files:**
- Modify: `src/NagaBatteryTray/Ui/PopupWindow.xaml`, `src/NagaBatteryTray/Ui/PopupWindow.xaml.cs`, `src/NagaBatteryTray/Ui/TrayIconController.cs`, `src/NagaBatteryTray/AppHost.cs`

**Interfaces:**
- Consumes: `SettingsWindow` (Task 5); `BatteryMonitor.GetDpiAsync/SetDpiAsync` (Task 3); existing `_settings`, `_startup`, `_tray`, `_monitor`, `Dispatch`, `SetStartup`.
- Produces: `PopupWindow.SettingsRequested` (event Action); `TrayIconController.SettingsRequested` (event Action); `AppHost.OpenSettings()` + single-window lifecycle.

- [ ] **Step 1: Enable the popup Settings button**

In `src/NagaBatteryTray/Ui/PopupWindow.xaml`, replace the disabled Settings button:

```xml
        <ui:Button Content="Settings" IsEnabled="False" ToolTip="Coming soon"
                   Margin="8,0,0,0" Padding="12,5" FontSize="12"/>
```

with:

```xml
        <ui:Button Content="Settings" Click="OnSettings"
                   Margin="8,0,0,0" Padding="12,5" FontSize="12"/>
```

In `src/NagaBatteryTray/Ui/PopupWindow.xaml.cs`, add the event + handler next to the existing `RefreshRequested`/`OnRefresh`:

```csharp
    public event Action? SettingsRequested;
```
```csharp
    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
```

- [ ] **Step 2: Add the tray Settings menu item**

In `src/NagaBatteryTray/Ui/TrayIconController.cs`, add the event field next to the others:

```csharp
    public event Action? SettingsRequested;
```

Insert the menu item between the startup item and the separator (replace the two lines `menu.Items.Add(_startupItem);` / `menu.Items.Add(new ToolStripSeparator());`):

```csharp
        menu.Items.Add(_startupItem);
        menu.Items.Add("Settings", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
```

- [ ] **Step 3: Wire the single Settings window in AppHost**

In `src/NagaBatteryTray/AppHost.cs`, add a field next to `_popup`:

```csharp
    private SettingsWindow? _settingsWindow;
```

In `Start()`, after `_tray.QuitRequested += Quit;`, subscribe:

```csharp
        _tray.SettingsRequested += OpenSettings;
```

In `CreatePopup()`, after `p.RefreshRequested += ...`, add:

```csharp
        p.SettingsRequested += OpenSettings;
```

Add the open + DPI handlers (e.g. after `CreatePopup`):

```csharp
    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }

        var win = new SettingsWindow(_settings.Settings, _startup.IsEnabled());
        win.SaveRequested += () => { win.ApplyTo(_settings.Settings); _settings.Save(); };
        win.StartupToggled += enable => { SetStartup(enable); _tray.SetStartupChecked(enable); };
        win.ApplyDpiRequested += dpi => _ = ApplyDpiAsync(win, dpi);
        win.Closed += (_, _) => _settingsWindow = null;
        win.SetDevicePresent(_monitor.State.Status == DeviceStatus.Online);
        _settingsWindow = win;
        win.Show();
        _ = LoadDpiAsync(win); // read current DPI off the UI thread, then seed the UI
    }

    // Task.Run keeps the blocking HidD_*Feature calls off the UI thread (no UI freeze; supports the
    // lightweight + zero-latency invariant). Results marshal back via Dispatch.
    private async Task LoadDpiAsync(SettingsWindow win)
    {
        var dpi = await Task.Run(() => _monitor.GetDpiAsync());
        Dispatch(() => { win.SetCurrentDpi(dpi); win.SetDevicePresent(dpi is not null); });
    }

    private async Task ApplyDpiAsync(SettingsWindow win, int dpi)
    {
        Dispatch(() => win.SetDpiStatus("Applying…"));
        bool ok = await Task.Run(() => _monitor.SetDpiAsync(dpi, dpi));
        DpiSetting? readBack = ok ? await Task.Run(() => _monitor.GetDpiAsync()) : null;
        Dispatch(() =>
        {
            if (readBack is { } v && v.X == dpi)
            {
                win.SetCurrentDpi(v);
                win.SetDpiStatus($"Applied ({v.X} DPI)");
            }
            else
            {
                win.SetDpiStatus("Couldn't confirm — wiggle the mouse and retry");
            }
        });
    }
```

In `Quit()`, close the settings window before shutdown (add as the first line of `Quit`):

```csharp
        _settingsWindow?.Close();
```

(`AppHost` already has `using NagaBatteryTray.Hid;` and `using NagaBatteryTray.Monitoring;`, so `DpiSetting` and `DeviceStatus` resolve.)

- [ ] **Step 4: Build and run the app**

Run:
```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build C:\Users\Brandon\naga-battery-tray
& "C:\Users\Brandon\naga-battery-tray\src\NagaBatteryTray\bin\Debug\net10.0-windows10.0.19041.0\NagaBatteryTray.exe"
```
Expected: tray icon appears; the existing battery popup still works.

- [ ] **Step 5: Manual functional verification**

- Open Settings from the **popup** "Settings" button and from the **tray** "Settings" menu item — the same single window (opening again Activates it, no duplicate).
- Current DPI shows within ~1 s of opening; DPI controls enabled.
- Drag the slider / type in the number box — they stay in sync; nothing is written to the mouse on drag.
- Click **Apply DPI** → status shows "Applying…" then "Applied (N DPI)"; the mouse sensitivity changes.
- Reboot the PC, reopen Settings → the DPI persists (proves the VARSTORE onboard write).
- Toggle **Run at startup** → the tray checkmark updates and the `HKCU\…\Run` key is added/removed immediately.
- Change the threshold / Advanced cadence, **Close**, reopen → values persisted (`%APPDATA%\NagaBatteryTray\settings.json`); cadence never below 15 s.
- Unplug / sleep the mouse, open Settings → "Current: unknown", DPI controls disabled; Apply unavailable. Replug → reopen shows current DPI, controls enabled.
- Apply while the mouse is asleep → "Couldn't confirm — wiggle the mouse and retry" (no false success, no hang).

- [ ] **Step 6: GATING — NFR acceptance (lightweight + mouse latency)**

- **Footprint:** with the app running, open and close the Settings window a few times, let it idle ~30 s, then in Task Manager confirm idle CPU ~0% and private working set back to ~23 MB (no lasting growth, no new background activity). (Same measurement used in v1.)
- **Mouse latency:** using a click-latency / polling-rate tool (or a controlled in-game / cursor test), confirm **no measurable or perceptible** input-lag increase — at idle, during a battery poll, and during a DPI Apply. Compare before vs. after. This gate must pass before the feature is considered done.

If either gate regresses, stop and diagnose (superpowers:systematic-debugging) before proceeding.

- [ ] **Step 7: Run the full test suite once more**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test C:\Users\Brandon\naga-battery-tray --nologo`
Expected: PASS — all tests green.

- [ ] **Step 8: Commit**

```bash
git add src/NagaBatteryTray/Ui/PopupWindow.xaml src/NagaBatteryTray/Ui/PopupWindow.xaml.cs src/NagaBatteryTray/Ui/TrayIconController.cs src/NagaBatteryTray/AppHost.cs
git commit -m "feat(ui): wire Settings window (popup + tray), DPI apply/read-back, lifecycle"
```

---

## Self-Review

**1. Spec coverage** (each spec section → task):
- §2/§4 Settings window + entry points (popup button + tray) → T5 (window), T6 (wiring). ✓
- §4 threshold / run-at-startup / DPI / Advanced cadence → T4 (VM), T5 (XAML), T6 (apply). ✓
- §3.1 lightweight + zero-latency invariants → Global Constraints + T3 (blocking lock), T6 Step 3 (`Task.Run` off-UI-thread) + Step 6 (gating acceptance). ✓
- §5.1 protocol DRY extractions + DPI build/parse + range guard → T1. ✓
- §5.2 device DPI + ExchangeAsync + CloseHandle-on-false → T2. ✓
- §5.3 monitor blocking pass-throughs → T3. ✓
- §5.4 VM → T4; §5.5 wiring/single-window/lifecycle → T6; §5.6 persistence (no DPI in JSON; startup via registry; no new AppSettings fields) → T4 `ApplyTo` + T6. ✓
- §6 gating hardware offset check → T2 Step 7 (`--probe-dpi`). ✓
- §7 edge cases (asleep/offline, out-of-range, rapid apply) → T1 range guard, T3 lock, T6 read-back + disabled controls. ✓
- §9 tests → T1/T3/T4 pure + behavior tests; T2/T6 manual + gating. ✓

**2. Placeholder scan:** No TBD/TODO; every code step has complete code. The one conditional note (T5 Step 3, `ui:ToggleSwitch` `Checked`/`Unchecked` events) is a concrete verify-and-fallback instruction, not a placeholder.

**3. Type consistency:** `DpiSetting(int X, int Y)` used consistently (T2 create → T2/T3 device/monitor → T4 VM → T6). `GetDpiAsync()`/`SetDpiAsync(int,int)` on `BatteryMonitor` (no CT) vs `IRazerDevice.GetDpiAsync(ct)`/`SetDpiAsync(int,int,ct)` (with CT) — intentional: the monitor owns the CT (`_cts.Token`). `ParseDpiReply(byte[], out int, out int)`, `BuildGetDpiBuffer(byte)`, `BuildSetDpiBuffer(byte,int,int)` consistent T1↔T2. VM `ApplyTo(AppSettings)`, `SetCurrentDpi(DpiSetting?)` consistent T4↔T5↔T6. Event signatures `SaveRequested`/`StartupToggled(bool)`/`ApplyDpiRequested(int)` consistent T5↔T6.
