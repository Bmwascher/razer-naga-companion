# Phase B Stage 1 — `--probe-buttons` Feasibility Spike Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and run the `--probe-buttons` diagnostic that decides whether Phase B (thumb-grid button
remapping) is viable on the Naga V2 Pro: prove the firmware accepts the Basilisk-derived remap command,
discover the 12 thumb-grid button IDs, and record which write modes persist — filling the spec's §6
results table (the gate for Stage 2).

**Architecture:** Pure protocol builders/parsers go into `Hid/RazerProtocol.cs` (TDD'd now against
published Basilisk V3 byte vectors — no hardware needed). The interactive spike itself extends
`Diagnostics/ProbeCommand.cs` exactly like the existing `--probe`/`--probe-dpi`/`--probe-dock` one-shots:
raw zero-access `CreateFile` + `HidD_Set/GetFeature`, synchronous, console-driven, **not** unit-tested,
never touching the resident runtime. A small JSON capture file under `%APPDATA%\NagaBatteryTray` records
discovered IDs and each button's previous action so `--probe-buttons --reset` can restore without a replug.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), C#, xUnit, HidSharp (device enumeration only),
raw P/Invoke HID feature reports. No new dependencies.

**Spec:** `docs/superpowers/specs/2026-06-21-naga-button-remap-design.md` (Stage 1 = §5.1 protocol +
§5.2 spike; results land in §6). Stage 2 is **out of scope for this plan** — it gets its own plan after
the spike fills §6.

## Global Constraints

- **HARD GATING (spec §3.1):** HID **feature reports only** on the USB control endpoint; open
  **zero-access** + `FILE_SHARE_READ|WRITE`; **never** claim the mouse's input collection; no new
  background timers/threads; zero mouse input-latency regression. The spike is a one-shot CLI —
  no resident behavior changes at all.
- **Report envelope:** VID `0x1532`; mouse PIDs wired `0x00A7` / wireless `0x00A8`; 90-byte report in a
  91-byte feature buffer; `report[1]`=tid, `[5]`=data_size, `[6]`=class, `[7]`=id, args from `[8]`;
  CRC = XOR of report `[2..87]` at `report[88]`. Reply status `buffer[1]`: `0x01` busy, `0x02` success.
  Transaction id `0x1f` (probes hardcode it, like `RunDpi`).
- **Remap command (Basilisk V3-derived, hardware-verified by this spike):** class `0x02`, set `0x0c` /
  get `0x8c`, data_size `0x0a`, args `[profile, buttonId, hypershift, category, dataLen, d0..d4]`.
  Profile `0x00` = volatile "direct"; `0x01..0x05` = onboard slots. Categories (MVP): `0x00` disabled,
  `0x02` keyboard (`data = [modifierBitmask, hidUsage]`).
- **Spike aux commands:** device mode get `0x00/0x84` / set `0x00/0x04`, data_size `0x02`
  (`0x00` normal, `0x03` driver); profile list `0x05/0x81` data_size `0x06`; profile create `0x05/0x02` /
  delete `0x05/0x03`, data_size `0x01`.
- **Testing boundary (spec §9):** protocol builders/parsers in `RazerProtocol.cs` are TDD'd;
  `ProbeCommand` is **not** unit-tested (same boundary as the existing probes).
- **Volatile-write discipline (spec §5.2):** every discovery write targets profile `0x00`; the
  persistence test makes the spike's one deliberate onboard-slot write (plus profile creation if the
  firmware requires it). Max scan writes: **200**.
- **Build/test (user-local SDK, not on PATH):**
  build `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`,
  test `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerProtocolTests"`.
- **Smart App Control:** launching a fresh Debug build can be vetoed by hash (`0x800711C7`). Launch via
  the signed host: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" "src\NagaBatteryTray\bin\Debug\net10.0-windows10.0.19041.0\NagaBatteryTray.dll" --probe-buttons`;
  if vetoed, rebuild with `-p:Deterministic=false` and retry.
- **Style:** conventional-commit messages, frequent commits, surgical changes, no GPL code copying
  (protocol bytes re-derived from documentation only).
- Tests reach `internal` members through `InternalsVisibleTo.cs` (not needed here — everything added to
  `RazerProtocol` is `public`, matching its existing members).

---

### Task 1: `BuildSetButtonBuffer` + button-command constants (TDD vs Basilisk vectors)

**Files:**
- Modify: `src/NagaBatteryTray/Hid/RazerProtocol.cs` (insert after the DPI region: consts after line 26, builder after `BuildSetDpiBuffer`)
- Test: `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs` (append)

**Interfaces:**
- Consumes: existing private `BuildReport(byte transactionId, byte dataSize, byte commandClass, byte commandId, ReadOnlySpan<byte> payload)` and `ComputeCrc`.
- Produces (later tasks rely on these exact names):
  `public const byte CommandClassButton = 0x02, CommandIdSetButton = 0x0c, CommandIdGetButton = 0x8c, DataSizeButton = 0x0a, ButtonProfileDirect = 0x00, FnDisabled = 0x00, FnKeyboard = 0x02;`
  `public static byte[] BuildSetButtonBuffer(byte transactionId, byte profile, byte buttonId, byte hypershift, byte category, ReadOnlySpan<byte> data)` — returns the 91-byte feature buffer; **throws `ArgumentOutOfRangeException` when `data.Length > 5`**.

- [ ] **Step 1: Write the failing tests**

Append to `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs` (inside the existing class):

```csharp
    // --- Phase B: button remap (Basilisk V3 oracle vectors; spec §5.1/§6) ---

    [Fact]
    public void SetButton_reproduces_basilisk_ctrl_c_vector()
    {
        // tilt-wheel-left (0x34) -> Ctrl+C on profile 1: args = 01 34 00 02 02 01 06 00 00 00
        byte[] buf = RazerProtocol.BuildSetButtonBuffer(0x1f, 0x01, 0x34, 0x00,
            RazerProtocol.FnKeyboard, new byte[] { 0x01, 0x06 });
        Assert.Equal(91, buf.Length);
        Assert.Equal(0x1f, buf[2]);  // transaction_id
        Assert.Equal(0x0a, buf[6]);  // data_size
        Assert.Equal(0x02, buf[7]);  // command_class
        Assert.Equal(0x0c, buf[8]);  // command_id (SET)
        byte[] expectedArgs = { 0x01, 0x34, 0x00, 0x02, 0x02, 0x01, 0x06, 0x00, 0x00, 0x00 };
        for (int i = 0; i < 10; i++) Assert.Equal(expectedArgs[i], buf[9 + i]);
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        Assert.Equal(crc, buf[89]);
    }

    [Fact]
    public void SetButton_reproduces_basilisk_ctrl_v_vector()
    {
        // tilt-wheel-right (0x35) -> Ctrl+V on profile 1: args = 01 35 00 02 02 01 19 00 00 00
        byte[] buf = RazerProtocol.BuildSetButtonBuffer(0x1f, 0x01, 0x35, 0x00,
            RazerProtocol.FnKeyboard, new byte[] { 0x01, 0x19 });
        byte[] expectedArgs = { 0x01, 0x35, 0x00, 0x02, 0x02, 0x01, 0x19, 0x00, 0x00, 0x00 };
        for (int i = 0; i < 10; i++) Assert.Equal(expectedArgs[i], buf[9 + i]);
    }

    [Fact]
    public void SetButton_disabled_has_zero_length_data()
    {
        byte[] buf = RazerProtocol.BuildSetButtonBuffer(0x1f, RazerProtocol.ButtonProfileDirect,
            0x03, 0x00, RazerProtocol.FnDisabled, ReadOnlySpan<byte>.Empty);
        Assert.Equal(0x00, buf[12]); // category = disabled
        Assert.Equal(0x00, buf[13]); // dataLen = 0
        for (int i = 14; i <= 18; i++) Assert.Equal(0x00, buf[i]);
    }

    [Fact]
    public void SetButton_throws_on_data_longer_than_5()
    {
        // a truncated binding must never reach the device
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RazerProtocol.BuildSetButtonBuffer(0x1f, 0x00, 0x03, 0x00, RazerProtocol.FnKeyboard, new byte[6]));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerProtocolTests"`
Expected: **build error** — `'RazerProtocol' does not contain a definition for 'BuildSetButtonBuffer'` (and the new consts). A compile failure is this cycle's red.

- [ ] **Step 3: Implement constants + builder**

In `src/NagaBatteryTray/Hid/RazerProtocol.cs`, after the DPI constants block (after `public const int DpiMax = 30000;`), add:

```csharp
    public const byte CommandClassButton = 0x02;
    public const byte CommandIdSetButton = 0x0c;   // write a button's onboard function
    public const byte CommandIdGetButton = 0x8c;   // read it back
    public const byte DataSizeButton = 0x0a;       // 10 arg bytes
    public const byte ButtonProfileDirect = 0x00;  // volatile "direct" profile; 0x01..0x05 = onboard slots

    // MVP function categories only (deferred mouse/DPI/media categories are spec §6 prose)
    public const byte FnDisabled = 0x00;
    public const byte FnKeyboard = 0x02;           // data = [modifierBitmask, hidUsage]
```

After `BuildSetDpiBuffer`, add:

```csharp
    /// <summary>SET a button's onboard function. args = [profile, buttonId, hypershift, category, dataLen, d0..d4].
    /// hypershift is a fixed wire-format byte (0x00 this phase). Throws if data exceeds 5 bytes —
    /// a truncated binding must never reach the device.</summary>
    public static byte[] BuildSetButtonBuffer(byte transactionId, byte profile, byte buttonId, byte hypershift,
                                              byte category, ReadOnlySpan<byte> data)
    {
        if (data.Length > 5) throw new ArgumentOutOfRangeException(nameof(data));
        Span<byte> args = stackalloc byte[10];
        args[0] = profile; args[1] = buttonId; args[2] = hypershift;
        args[3] = category; args[4] = (byte)data.Length;
        for (int i = 0; i < data.Length; i++) args[5 + i] = data[i];
        return BuildReport(transactionId, DataSizeButton, CommandClassButton, CommandIdSetButton, args);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerProtocolTests"`
Expected: PASS (all existing + 4 new).

- [ ] **Step 5: Commit**

```powershell
git add src/NagaBatteryTray/Hid/RazerProtocol.cs tests/NagaBatteryTray.Tests/RazerProtocolTests.cs
git commit -m "feat(hid): BuildSetButtonBuffer verified against Basilisk V3 vectors"
```

---

### Task 2: `BuildGetButtonBuffer` + `ParseButtonReply` with echo check (TDD)

**Files:**
- Modify: `src/NagaBatteryTray/Hid/RazerProtocol.cs` (after `BuildSetButtonBuffer`)
- Test: `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs` (append)

**Interfaces:**
- Consumes: Task 1 consts; existing private `ValidateReply(byte[] buffer91)`.
- Produces:
  `public static byte[] BuildGetButtonBuffer(byte transactionId, byte profile, byte buttonId, byte hypershift)`;
  `public static ReplyResult ParseButtonReply(byte[] buffer91, byte profile, byte buttonId, byte hypershift, out byte category, out byte[] data)` — echo check: reply args `[0..2]` must equal the requested profile/buttonId/hypershift and `dataLen <= 5`, else `Failed`. Arg offsets in the 91-byte buffer: profile `[9]`, buttonId `[10]`, hypershift `[11]`, category `[12]`, dataLen `[13]`, data `[14..18]`.

- [ ] **Step 1: Write the failing tests**

Append to `RazerProtocolTests.cs`:

```csharp
    private static byte[] MakeButtonReply(byte status, byte profile, byte buttonId, byte hypershift,
                                          byte category, byte[] data)
    {
        var buf = new byte[91];
        buf[1] = status;              // report[0]
        buf[2] = 0x1f;                // transaction_id
        buf[6] = 0x0a;                // data_size
        buf[7] = 0x02;                // command_class
        buf[8] = 0x8c;                // command_id (GET)
        buf[9] = profile; buf[10] = buttonId; buf[11] = hypershift; // echoed request args
        buf[12] = category; buf[13] = (byte)data.Length;
        for (int i = 0; i < data.Length; i++) buf[14 + i] = data[i];
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i]; // reply crc over buffer[3..88]
        buf[89] = crc;
        return buf;
    }

    [Fact]
    public void GetButton_buffer_has_correct_layout_and_crc()
    {
        byte[] buf = RazerProtocol.BuildGetButtonBuffer(0x1f, 0x01, 0x34, 0x00);
        Assert.Equal(0x0a, buf[6]);  // data_size (same 10-byte frame as SET)
        Assert.Equal(0x02, buf[7]);  // command_class
        Assert.Equal(0x8c, buf[8]);  // command_id (GET)
        Assert.Equal(0x01, buf[9]);  // profile
        Assert.Equal(0x34, buf[10]); // buttonId
        Assert.Equal(0x00, buf[11]); // hypershift
        for (int i = 12; i <= 18; i++) Assert.Equal(0x00, buf[i]); // zero-padded
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        Assert.Equal(crc, buf[89]);
    }

    [Fact]
    public void ParseButtonReply_success_decodes_category_and_data()
    {
        var reply = MakeButtonReply(0x02, 0x00, 0x34, 0x00, 0x02, new byte[] { 0x01, 0x06 });
        var r = RazerProtocol.ParseButtonReply(reply, 0x00, 0x34, 0x00, out byte category, out byte[] data);
        Assert.Equal(ReplyResult.Success, r);
        Assert.Equal(0x02, category);
        Assert.Equal(new byte[] { 0x01, 0x06 }, data);
    }

    [Fact]
    public void ParseButtonReply_busy_is_busy()
    {
        var reply = MakeButtonReply(0x01, 0x00, 0x34, 0x00, 0x02, new byte[] { 0x01, 0x06 });
        Assert.Equal(ReplyResult.Busy, RazerProtocol.ParseButtonReply(reply, 0x00, 0x34, 0x00, out _, out _));
    }

    [Fact]
    public void ParseButtonReply_echo_mismatch_is_failed()
    {
        // reply echoes buttonId 0x35 but we asked about 0x34 -> wrong-layout guard trips
        var reply = MakeButtonReply(0x02, 0x00, 0x35, 0x00, 0x02, new byte[] { 0x01, 0x06 });
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseButtonReply(reply, 0x00, 0x34, 0x00, out _, out _));
    }

    [Fact]
    public void ParseButtonReply_datalen_over_5_is_failed()
    {
        var reply = MakeButtonReply(0x02, 0x00, 0x34, 0x00, 0x02, new byte[] { 0x01, 0x06 });
        reply[13] = 6;                                    // corrupt dataLen
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= reply[i];    // re-seal crc so only the guard trips
        reply[89] = crc;
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseButtonReply(reply, 0x00, 0x34, 0x00, out _, out _));
    }

    [Fact]
    public void ParseButtonReply_bad_crc_is_failed()
    {
        var reply = MakeButtonReply(0x02, 0x00, 0x34, 0x00, 0x02, new byte[] { 0x01, 0x06 });
        reply[89] ^= 0xFF;
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseButtonReply(reply, 0x00, 0x34, 0x00, out _, out _));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerProtocolTests"`
Expected: **build error** — `BuildGetButtonBuffer`/`ParseButtonReply` not defined.

- [ ] **Step 3: Implement**

In `RazerProtocol.cs`, after `BuildSetButtonBuffer`:

```csharp
    /// <summary>GET a button's onboard function. Request carries [profile, buttonId, hypershift] in a
    /// 10-byte zero-padded frame (same data_size as SET); the reply fills category + data.</summary>
    public static byte[] BuildGetButtonBuffer(byte transactionId, byte profile, byte buttonId, byte hypershift)
    {
        Span<byte> args = stackalloc byte[10];
        args[0] = profile; args[1] = buttonId; args[2] = hypershift;
        return BuildReport(transactionId, DataSizeButton, CommandClassButton, CommandIdGetButton, args);
    }

    /// <summary>Validates a get-button reply. Echo check: reply args [0..2] must match the requested
    /// profile/buttonId/hypershift and dataLen must be ≤ 5, else Failed (buttons have no DPI-style
    /// numeric range to validate against).</summary>
    public static ReplyResult ParseButtonReply(byte[] buffer91, byte profile, byte buttonId, byte hypershift,
                                               out byte category, out byte[] data)
    {
        category = 0; data = Array.Empty<byte>();
        var r = ValidateReply(buffer91);
        if (r != ReplyResult.Success) return r;
        if (buffer91[9] != profile || buffer91[10] != buttonId || buffer91[11] != hypershift)
            return ReplyResult.Failed;
        int len = buffer91[13];
        if (len > 5) return ReplyResult.Failed;
        category = buffer91[12];
        data = new byte[len];
        Array.Copy(buffer91, 14, data, 0, len);
        return ReplyResult.Success;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerProtocolTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/NagaBatteryTray/Hid/RazerProtocol.cs tests/NagaBatteryTray.Tests/RazerProtocolTests.cs
git commit -m "feat(hid): get-button buffer + ParseButtonReply with request-echo guard"
```

---

### Task 3: Aux protocol — device mode + profile lifecycle (TDD)

**Files:**
- Modify: `src/NagaBatteryTray/Hid/RazerProtocol.cs` (consts after the Task 1 button consts; builders after `ParseButtonReply`)
- Test: `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs` (append)

**Interfaces:**
- Consumes: private `BuildReport`, `ValidateReply`.
- Produces:
  `public const byte CommandClassInfo = 0x00, CommandIdSetDeviceMode = 0x04, CommandIdGetDeviceMode = 0x84, DataSizeDeviceMode = 0x02, DeviceModeNormal = 0x00, DeviceModeDriver = 0x03;`
  `public const byte CommandClassProfile = 0x05, CommandIdGetProfileList = 0x81, CommandIdNewProfile = 0x02, CommandIdDeleteProfile = 0x03, DataSizeProfileList = 0x06, DataSizeProfileEdit = 0x01;`
  `public static byte[] BuildGetDeviceModeBuffer(byte transactionId)`;
  `public static byte[] BuildSetDeviceModeBuffer(byte transactionId, byte mode)`;
  `public static ReplyResult ParseDeviceModeReply(byte[] buffer91, out byte mode)` — mode at buffer `[9]`;
  `public static byte[] BuildGetProfileListBuffer(byte transactionId)`;
  `public static ReplyResult ParseProfileListReply(byte[] buffer91, out byte capacity, out byte[] slots)` — capacity at `[9]`, non-zero slot numbers from `[10..10+capacity)`; capacity > 5 → `Failed`;
  `public static byte[] BuildNewProfileBuffer(byte transactionId, byte slot)`;
  `public static byte[] BuildDeleteProfileBuffer(byte transactionId, byte slot)`.

- [ ] **Step 1: Write the failing tests**

Append to `RazerProtocolTests.cs`:

```csharp
    [Fact]
    public void DeviceMode_get_and_set_buffers_have_correct_layout()
    {
        byte[] get = RazerProtocol.BuildGetDeviceModeBuffer(0x1f);
        Assert.Equal(0x02, get[6]); // data_size
        Assert.Equal(0x00, get[7]); // class (info)
        Assert.Equal(0x84, get[8]); // id (GET)

        byte[] set = RazerProtocol.BuildSetDeviceModeBuffer(0x1f, RazerProtocol.DeviceModeNormal);
        Assert.Equal(0x02, set[6]);
        Assert.Equal(0x00, set[7]);
        Assert.Equal(0x04, set[8]); // id (SET)
        Assert.Equal(0x00, set[9]);  // mode
        Assert.Equal(0x00, set[10]); // param
    }

    [Fact]
    public void ParseDeviceModeReply_decodes_mode()
    {
        var buf = new byte[91];
        buf[1] = 0x02; buf[2] = 0x1f; buf[6] = 0x02; buf[7] = 0x00; buf[8] = 0x84;
        buf[9] = 0x03; // driver mode
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        buf[89] = crc;
        Assert.Equal(ReplyResult.Success, RazerProtocol.ParseDeviceModeReply(buf, out byte mode));
        Assert.Equal(RazerProtocol.DeviceModeDriver, mode);
    }

    [Fact]
    public void ProfileList_buffer_and_parse_roundtrip()
    {
        byte[] req = RazerProtocol.BuildGetProfileListBuffer(0x1f);
        Assert.Equal(0x06, req[6]); // data_size = 1 capacity byte + up to 5 slot bytes
        Assert.Equal(0x05, req[7]); // class (profile)
        Assert.Equal(0x81, req[8]); // id (list)

        var reply = new byte[91];
        reply[1] = 0x02; reply[2] = 0x1f; reply[6] = 0x06; reply[7] = 0x05; reply[8] = 0x81;
        reply[9] = 5;                     // capacity
        reply[10] = 1; reply[11] = 3;     // slots 1 and 3 exist; rest zero
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= reply[i];
        reply[89] = crc;
        Assert.Equal(ReplyResult.Success, RazerProtocol.ParseProfileListReply(reply, out byte cap, out byte[] slots));
        Assert.Equal(5, cap);
        Assert.Equal(new byte[] { 1, 3 }, slots);
    }

    [Fact]
    public void ParseProfileListReply_capacity_over_5_is_failed()
    {
        var reply = new byte[91];
        reply[1] = 0x02; reply[2] = 0x1f; reply[6] = 0x06; reply[7] = 0x05; reply[8] = 0x81;
        reply[9] = 200; // nonsense capacity -> wrong-layout guard
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= reply[i];
        reply[89] = crc;
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseProfileListReply(reply, out _, out _));
    }

    [Fact]
    public void Profile_new_and_delete_buffers_have_correct_layout()
    {
        byte[] create = RazerProtocol.BuildNewProfileBuffer(0x1f, 0x01);
        Assert.Equal(0x01, create[6]); // data_size
        Assert.Equal(0x05, create[7]); // class
        Assert.Equal(0x02, create[8]); // id (new)
        Assert.Equal(0x01, create[9]); // slot

        byte[] del = RazerProtocol.BuildDeleteProfileBuffer(0x1f, 0x01);
        Assert.Equal(0x03, del[8]);    // id (delete)
        Assert.Equal(0x01, del[9]);    // slot
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerProtocolTests"`
Expected: **build error** — the new members are not defined.

- [ ] **Step 3: Implement**

In `RazerProtocol.cs`, after the Task 1 button consts:

```csharp
    // Spike aux commands (spec §5.2 steps 1 & 4). Device mode: openrazer razerchromacommon.c.
    // Profile lifecycle: razerqdhid cmd_profile (no active-profile get/set is documented).
    public const byte CommandClassInfo = 0x00;
    public const byte CommandIdSetDeviceMode = 0x04;
    public const byte CommandIdGetDeviceMode = 0x84;
    public const byte DataSizeDeviceMode = 0x02;
    public const byte DeviceModeNormal = 0x00;
    public const byte DeviceModeDriver = 0x03;

    public const byte CommandClassProfile = 0x05;
    public const byte CommandIdGetProfileList = 0x81;
    public const byte CommandIdNewProfile = 0x02;
    public const byte CommandIdDeleteProfile = 0x03;
    public const byte DataSizeProfileList = 0x06;  // 1 capacity byte + up to 5 slot numbers
    public const byte DataSizeProfileEdit = 0x01;
```

After `ParseButtonReply`:

```csharp
    public static byte[] BuildGetDeviceModeBuffer(byte transactionId)
    {
        Span<byte> args = stackalloc byte[2]; // zeroed
        return BuildReport(transactionId, DataSizeDeviceMode, CommandClassInfo, CommandIdGetDeviceMode, args);
    }

    public static byte[] BuildSetDeviceModeBuffer(byte transactionId, byte mode)
    {
        Span<byte> args = stackalloc byte[2];
        args[0] = mode; // args[1] = 0x00 param
        return BuildReport(transactionId, DataSizeDeviceMode, CommandClassInfo, CommandIdSetDeviceMode, args);
    }

    /// <summary>Decodes a get-device-mode reply; mode is report arg[0] (buffer[9]).</summary>
    public static ReplyResult ParseDeviceModeReply(byte[] buffer91, out byte mode)
    {
        mode = 0;
        var r = ValidateReply(buffer91);
        if (r != ReplyResult.Success) return r;
        mode = buffer91[9];
        return ReplyResult.Success;
    }

    public static byte[] BuildGetProfileListBuffer(byte transactionId)
    {
        Span<byte> args = stackalloc byte[6]; // zeroed
        return BuildReport(transactionId, DataSizeProfileList, CommandClassProfile, CommandIdGetProfileList, args);
    }

    /// <summary>Decodes a profile-list reply: capacity at buffer[9], then the existing (non-zero)
    /// slot numbers. Capacity beyond the 5 the frame can carry is treated as wrong-layout → Failed
    /// (the probe still prints raw hex, so nothing is lost on a surprise).</summary>
    public static ReplyResult ParseProfileListReply(byte[] buffer91, out byte capacity, out byte[] slots)
    {
        capacity = 0; slots = Array.Empty<byte>();
        var r = ValidateReply(buffer91);
        if (r != ReplyResult.Success) return r;
        capacity = buffer91[9];
        if (capacity > 5) return ReplyResult.Failed;
        var list = new List<byte>();
        for (int i = 0; i < capacity; i++)
            if (buffer91[10 + i] != 0) list.Add(buffer91[10 + i]);
        slots = list.ToArray();
        return ReplyResult.Success;
    }

    public static byte[] BuildNewProfileBuffer(byte transactionId, byte slot)
    {
        Span<byte> args = stackalloc byte[1];
        args[0] = slot;
        return BuildReport(transactionId, DataSizeProfileEdit, CommandClassProfile, CommandIdNewProfile, args);
    }

    public static byte[] BuildDeleteProfileBuffer(byte transactionId, byte slot)
    {
        Span<byte> args = stackalloc byte[1];
        args[0] = slot;
        return BuildReport(transactionId, DataSizeProfileEdit, CommandClassProfile, CommandIdDeleteProfile, args);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerProtocolTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/NagaBatteryTray/Hid/RazerProtocol.cs tests/NagaBatteryTray.Tests/RazerProtocolTests.cs
git commit -m "feat(hid): device-mode + profile-lifecycle buffers for the button spike"
```

---

### Task 4: Probe plumbing — session, exchange, capture file, dispatch, device-mode step

**Files:**
- Modify: `src/NagaBatteryTray/Program.cs` (new dispatch branch after the `--probe-dock` branch)
- Modify: `src/NagaBatteryTray/Diagnostics/ProbeCommand.cs` (append new members; no changes to existing probes)

**Interfaces:**
- Consumes: Tasks 1–3 protocol members; existing P/Invokes `CreateFile`/`HidD_SetFeature`/`HidD_GetFeature` and `Mi()` already in `ProbeCommand`.
- Produces (Tasks 5–7 rely on these exact names, all `private` members of `ProbeCommand` unless noted):
  `public static int RunButtons()`;
  `private const byte ButtonsTid = 0x1f;`
  `private sealed class MouseSession : IDisposable { SafeFileHandle? Handle; int Pid; bool Open(); bool WaitForReconnect(); }`
  `private static byte[]? Exchange(SafeFileHandle h, byte[] request)` — SET→wait→GET with busy retry, null on transport failure;
  `private static string Hex(byte[] buf, int n)` / `private static string Hex2(byte[] data)`;
  `private sealed record CapturedAction(byte Category, byte[] Data);`
  `private sealed class ButtonCaptureFile` with properties `Dictionary<int, byte> PositionToId`, `Dictionary<byte, CapturedAction> PreviousActions`, `byte? DeviceModeAtStart`, `bool? SetAccepted`, `bool? AcceptancePassed`, `bool? Profile0Volatile`, `bool? SlotPersisted`, `byte? SlotTested`, `byte? SlotButtonId`, `CapturedAction? SlotPreviousAction`, `bool SlotWasCreated`, `string? ProfileNotes`, methods `void Save()`, `static ButtonCaptureFile? Load()`, `static string PathFor()`;
  `private static void CheckDeviceMode(MouseSession s, ButtonCaptureFile capture)`.

- [ ] **Step 1: Add the dispatch branch**

In `Program.cs`, after the `--probe-dock` branch:

```csharp
        if (args.Length > 0 && args[0] == "--probe-buttons")
        {
            AllocConsoleIfNeeded();
            return Diagnostics.ProbeCommand.RunButtons();
        }
```

(The `--reset` variant is dispatched in Task 7, when `RunButtonsReset` exists.)

- [ ] **Step 2: Add plumbing + device-mode step to `ProbeCommand`**

Append inside the `ProbeCommand` class (below `RunDock`, above the private helpers):

```csharp
    // ---- Phase B Stage 1: --probe-buttons feasibility spike (spec §5.2) ----

    private const byte ButtonsTid = 0x1f;

    public static int RunButtons()
    {
        Console.WriteLine("Naga Battery Tray - button remap feasibility spike (--probe-buttons)\n");
        Console.WriteLine("Writes below target the VOLATILE direct profile unless stated; an unplug/replug");
        Console.WriteLine("(or power-cycle via the switch underneath) restores normal behaviour at any time.\n");

        using var s = new MouseSession();
        if (!s.Open())
        {
            Console.WriteLine("No live mouse collection found (connected? awake? tray app closed?).");
            return 1;
        }
        Console.WriteLine($"Live collection: PID 0x{s.Pid:x4}\n");

        var capture = new ButtonCaptureFile();
        CheckDeviceMode(s, capture);
        capture.Save();
        Console.WriteLine("(Steps 2-5 land in the next tasks.)");
        return 0;
    }

    /// <summary>Spike step 1 — read (and offer to normalize) the device mode. Driver mode is Synapse's
    /// software-resident model; a leftover would make good onboard writes look dead (false FAIL).</summary>
    private static void CheckDeviceMode(MouseSession s, ButtonCaptureFile capture)
    {
        Console.WriteLine("[1/5] Device-mode check (0x00/0x84; 0x00 = normal, 0x03 = driver)");
        var reply = Exchange(s.Handle!, RazerProtocol.BuildGetDeviceModeBuffer(ButtonsTid));
        if (reply is null)
        {
            Console.WriteLine("  no reply - record 'device mode: unreadable' in spec §6 and continue.\n");
            return;
        }
        var r = RazerProtocol.ParseDeviceModeReply(reply, out byte mode);
        Console.WriteLine($"  status=0x{reply[1]:x2} {r} mode=0x{mode:x2}  [{Hex(reply, 16)}]");
        if (r == ReplyResult.Success)
        {
            capture.DeviceModeAtStart = mode;
            if (mode == RazerProtocol.DeviceModeDriver)
            {
                Console.WriteLine("  DRIVER MODE detected (a Synapse leftover) - onboard bindings may not fire.");
                Console.Write("  Set back to normal mode now? [y/N] ");
                var k = Console.ReadKey(intercept: true);
                Console.WriteLine();
                if (k.Key == ConsoleKey.Y)
                {
                    var set = Exchange(s.Handle!, RazerProtocol.BuildSetDeviceModeBuffer(ButtonsTid, RazerProtocol.DeviceModeNormal));
                    Console.WriteLine($"  set-normal: status=0x{(set is null ? 0 : set[1]):x2}");
                }
            }
        }
        Console.WriteLine();
    }

    /// <summary>Opens the live mouse control collection: wired PID first, then wireless (a stale dongle
    /// collection stays enumerated when the mouse goes wired), verifying each candidate answers a
    /// battery query before committing - mirrors RazerDevice.EnsureConnectedAsync.</summary>
    private static SafeFileHandle? OpenLiveMouse(out int pidOpened)
    {
        foreach (int pid in new[] { RazerProtocol.MousePidWired, RazerProtocol.MousePidWireless })
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                int max = -1;
                try { max = dev.GetMaxFeatureReportLength(); } catch { }
                if (max != RazerProtocol.BufferLength) continue;
                var h = CreateFile(dev.DevicePath, 0, 0x3, IntPtr.Zero, 0x3, 0, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); continue; }
                var probe = Exchange(h, RazerProtocol.BuildFeatureBuffer(ButtonsTid, RazerProtocol.CommandIdBattery));
                if (probe is not null && probe[1] == 0x02) { pidOpened = pid; return h; }
                h.Dispose();
            }
        pidOpened = 0;
        return null;
    }

    /// <summary>SET -> wait -> GET with busy retry (same pacing as DockOneShot). Null on transport failure.</summary>
    private static byte[]? Exchange(SafeFileHandle h, byte[] request)
    {
        if (!HidD_SetFeature(h, request, request.Length)) return null;
        for (int tries = 0; tries < 10; tries++)
        {
            Thread.Sleep(tries == 0 ? 400 : 200);
            var reply = new byte[RazerProtocol.BufferLength];
            if (!HidD_GetFeature(h, reply, reply.Length)) return null;
            if (reply[1] != 0x01) return reply; // not busy
        }
        return null;
    }

    private static string Hex(byte[] buf, int n) => string.Join(" ", buf.Take(n).Select(b => b.ToString("x2")));
    private static string Hex2(byte[] data) => string.Join(" ", data.Select(b => b.ToString("x2")));

    private sealed class MouseSession : IDisposable
    {
        public SafeFileHandle? Handle { get; private set; }
        public int Pid { get; private set; }

        public bool Open()
        {
            Handle?.Dispose();
            Handle = OpenLiveMouse(out int pid);
            Pid = pid;
            return Handle is not null;
        }

        /// <summary>Blocks until the mouse answers again after an unplug/replug (1 s poll, 60 s cap).</summary>
        public bool WaitForReconnect()
        {
            Console.WriteLine("  waiting for the mouse to come back...");
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(1000);
                if (Open()) { Console.WriteLine($"  reconnected (PID 0x{Pid:x4})."); return true; }
            }
            Console.WriteLine("  mouse did not come back within 60 s.");
            return false;
        }

        public void Dispose() => Handle?.Dispose();
    }

    private sealed record CapturedAction(byte Category, byte[] Data);

    /// <summary>Spike results persisted to %APPDATA%\NagaBatteryTray\probe-buttons.json so
    /// --probe-buttons --reset works across processes (best-effort; replug is canonical).</summary>
    private sealed class ButtonCaptureFile
    {
        public Dictionary<int, byte> PositionToId { get; set; } = new();          // grid position 1..12 -> button id
        public Dictionary<byte, CapturedAction> PreviousActions { get; set; } = new(); // volatile-profile pre-write reads
        public byte? DeviceModeAtStart { get; set; }
        public bool? SetAccepted { get; set; }       // firmware answered 0x02 to the SET
        public bool? AcceptancePassed { get; set; }  // ...and the bound key actually fired
        public bool? Profile0Volatile { get; set; }
        public bool? SlotPersisted { get; set; }
        public byte? SlotTested { get; set; }
        public byte? SlotButtonId { get; set; }
        public CapturedAction? SlotPreviousAction { get; set; }
        public bool SlotWasCreated { get; set; }
        public string? ProfileNotes { get; set; }

        public static string PathFor() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NagaBatteryTray", "probe-buttons.json");

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFor())!);
            File.WriteAllText(PathFor(), System.Text.Json.JsonSerializer.Serialize(this));
        }

        public static ButtonCaptureFile? Load()
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<ButtonCaptureFile>(File.ReadAllText(PathFor())); }
            catch { return null; }
        }
    }
```

- [ ] **Step 3: Build**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Smoke-run (mouse optional)**

Quit the resident tray app first (tray icon → Exit) so probe exchanges don't interleave with the battery poll. Then:

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" "src\NagaBatteryTray\bin\Debug\net10.0-windows10.0.19041.0\NagaBatteryTray.dll" --probe-buttons`
Expected: with the mouse connected, prints `Live collection: PID 0x...`, the `[1/5]` device-mode line
(expect `mode=0x00`), and `(Steps 2-5 land in the next tasks.)`. Without a mouse: the friendly
"No live mouse collection found" line, exit code 1. (If SAC vetoes the load: rebuild with
`-p:Deterministic=false` and retry.)

- [ ] **Step 5: Commit**

```powershell
git add src/NagaBatteryTray/Program.cs src/NagaBatteryTray/Diagnostics/ProbeCommand.cs
git commit -m "feat(probe): --probe-buttons plumbing + device-mode check (spike step 1)"
```

---

### Task 5: Acceptance + volatility probe (spike step 2)

**Files:**
- Modify: `src/NagaBatteryTray/Diagnostics/ProbeCommand.cs` (`RunButtons` body + one new method)

**Interfaces:**
- Consumes: `MouseSession`, `Exchange`, `Hex`/`Hex2`, `ButtonCaptureFile`, Task 1–2 protocol members.
- Produces: `private static bool RunAcceptanceProbe(MouseSession s, ButtonCaptureFile capture)` —
  returns `false` only when the firmware rejects the SET outright (spike aborts). Sets
  `capture.AcceptancePassed` and `capture.Profile0Volatile`.

- [ ] **Step 1: Extend `RunButtons` and add the method**

Replace the `RunButtons` body's tail (from `CheckDeviceMode(s, capture);` down to `return 0;`) with:

```csharp
        CheckDeviceMode(s, capture);
        capture.Save();
        if (!RunAcceptanceProbe(s, capture)) { capture.Save(); return 1; }
        capture.Save();
        Console.WriteLine("(Steps 3-5 land in the next tasks.)");
        return 0;
```

Add the method after `CheckDeviceMode`:

```csharp
    /// <summary>Spike step 2 — bind a KNOWN Basilisk id (wheel-click 0x03) -> F13 on volatile profile 0.
    /// Disambiguates "firmware rejects the command" from "wrong grid id" before any guessing, then
    /// checks whether profile 0 survives a replug (selects the discovery loop's restore strategy).</summary>
    private static bool RunAcceptanceProbe(MouseSession s, ButtonCaptureFile capture)
    {
        Console.WriteLine("[2/5] Acceptance + volatility probe (wheel-click 0x03 -> F13, volatile profile 0)");

        // read the current action first (restore data + first exercise of the GET command)
        byte catBefore = 0; byte[] dataBefore = Array.Empty<byte>(); bool haveBefore = false;
        var get = Exchange(s.Handle!, RazerProtocol.BuildGetButtonBuffer(ButtonsTid, RazerProtocol.ButtonProfileDirect, 0x03, 0x00));
        if (get is not null)
        {
            var gr = RazerProtocol.ParseButtonReply(get, RazerProtocol.ButtonProfileDirect, 0x03, 0x00, out catBefore, out dataBefore);
            haveBefore = gr == ReplyResult.Success;
            Console.WriteLine($"  get-before: status=0x{get[1]:x2} {gr} category=0x{catBefore:x2} data=[{Hex2(dataBefore)}]  [{Hex(get, 20)}]");
        }
        else Console.WriteLine("  get-before: no reply");

        // the write under test: F13 = HID usage 0x68, no modifiers
        var set = Exchange(s.Handle!, RazerProtocol.BuildSetButtonBuffer(ButtonsTid, RazerProtocol.ButtonProfileDirect,
            0x03, 0x00, RazerProtocol.FnKeyboard, new byte[] { 0x00, 0x68 }));
        if (set is null || set[1] != 0x02)
        {
            Console.WriteLine($"  SET REJECTED (status=0x{(set is null ? 0 : set[1]):x2}) - record FAIL in spec §6.");
            Console.WriteLine("  Replug the mouse to clear anything partial. Spike aborts here.");
            capture.SetAccepted = false;
            capture.AcceptancePassed = false;
            return false;
        }
        capture.SetAccepted = true;
        Console.WriteLine($"  set: status=0x{set[1]:x2} (accepted)");

        var back = Exchange(s.Handle!, RazerProtocol.BuildGetButtonBuffer(ButtonsTid, RazerProtocol.ButtonProfileDirect, 0x03, 0x00));
        if (back is not null && RazerProtocol.ParseButtonReply(back, RazerProtocol.ButtonProfileDirect, 0x03, 0x00,
                out byte cat, out byte[] data) == ReplyResult.Success)
            Console.WriteLine($"  read-back: category=0x{cat:x2} data=[{Hex2(data)}] (expect 02 / 00 68)");

        Console.WriteLine("\n  Click the MOUSE WHEEL (middle-click) once, now.");
        Console.WriteLine("  (If nothing appears within a beat, press Esc yourself.)");
        var key = Console.ReadKey(intercept: true);
        bool fired = key.Key == ConsoleKey.F13;
        capture.AcceptancePassed = fired;
        Console.WriteLine(fired
            ? "  -> F13 captured: the V2 Pro APPLIES a volatile button write. ACCEPTANCE PASS."
            : $"  -> captured {key.Key}: write accepted but did not fire - note it in spec §6.");

        Console.WriteLine("\n  Now unplug/replug the mouse (wired) or power-cycle it (switch underneath),");
        Console.WriteLine("  then press Enter.");
        Console.ReadLine();
        if (!s.WaitForReconnect()) return false;

        Console.WriteLine("  Click the MOUSE WHEEL again. (Esc = it middle-clicked normally / nothing typed)");
        var key2 = Console.ReadKey(intercept: true);
        bool cleared = key2.Key != ConsoleKey.F13;
        capture.Profile0Volatile = cleared;
        Console.WriteLine(cleared
            ? "  -> bind cleared on replug: profile 0 is VOLATILE. Discovery can proceed replug-safe."
            : "  -> F13 SURVIVED the replug: profile 0 persists on this firmware. Discovery will restore each candidate after probing it.");
        if (!cleared && haveBefore)
        {
            var restore = Exchange(s.Handle!, RazerProtocol.BuildSetButtonBuffer(ButtonsTid, RazerProtocol.ButtonProfileDirect,
                0x03, 0x00, catBefore, dataBefore));
            Console.WriteLine($"  restored wheel-click from get-before: status=0x{(restore is null ? 0 : restore[1]):x2}");
        }
        Console.WriteLine();
        return true;
    }
```

- [ ] **Step 2: Build**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the full unit suite (regression check)**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: PASS (probe code is manual-only; this guards against accidental breakage elsewhere).

- [ ] **Step 4: Commit**

```powershell
git add src/NagaBatteryTray/Diagnostics/ProbeCommand.cs
git commit -m "feat(probe): acceptance + volatility probe (spike step 2)"
```

---

### Task 6: Grid discovery — batched volatile scan (spike step 3)

**Files:**
- Modify: `src/NagaBatteryTray/Diagnostics/ProbeCommand.cs` (`RunButtons` body + discovery members)

**Interfaces:**
- Consumes: everything from Tasks 4–5.
- Produces:
  `private static void RunGridDiscovery(MouseSession s, ButtonCaptureFile capture)` — fills
  `capture.PositionToId` and `capture.PreviousActions`;
  `private static readonly (ConsoleKey Key, byte Usage)[] Markers` — 38 markers: `A..Z` (usages
  `0x04..0x1d`) + `F13..F24` (`0x68..0x73`); excludes the grid's factory emissions (digits/`-`/`=`)
  and anything modifier-chorded;
  `private const int MaxScanWrites = 200;`
  `private static IEnumerable<byte> CandidateIds()` — primary window `0x06..0x33` minus known wheel ids
  `0x09`/`0x0a` (44 ids), then fallback window `0x36..0x5f` (42 ids).

- [ ] **Step 1: Extend `RunButtons` and add discovery**

Replace the `RunButtons` tail (from `if (!RunAcceptanceProbe...` down to `return 0;`) with:

```csharp
        if (!RunAcceptanceProbe(s, capture)) { capture.Save(); return 1; }
        capture.Save();
        RunGridDiscovery(s, capture);
        capture.Save();
        Console.WriteLine("(Steps 4-5 land in the next task.)");
        return 0;
```

Add after `RunAcceptanceProbe`:

```csharp
    private const int MaxScanWrites = 200;

    // Marker alphabet: letters + F13..F24. Excludes the grid's factory emissions (1-9, 0, -, = -
    // digits are meaningful scan output, not markers) and console-hostile chords (a Ctrl+C marker
    // would SIGINT the probe mid-scan). 38 markers bound each batch.
    private static readonly (ConsoleKey Key, byte Usage)[] Markers = BuildMarkers();

    private static (ConsoleKey, byte)[] BuildMarkers()
    {
        var list = new List<(ConsoleKey, byte)>();
        for (int i = 0; i < 26; i++) list.Add(((ConsoleKey)((int)ConsoleKey.A + i), (byte)(0x04 + i))); // A..Z
        for (int i = 0; i < 12; i++) list.Add(((ConsoleKey)((int)ConsoleKey.F13 + i), (byte)(0x68 + i))); // F13..F24
        return list.ToArray();
    }

    /// <summary>Hard-bounded candidate ids: the gap between the known Basilisk ids (0x06..0x33,
    /// skipping wheel up/down 0x09/0x0a), then a fallback window 0x36..0x5f if the grid isn't found.</summary>
    private static IEnumerable<byte> CandidateIds()
    {
        for (byte id = 0x06; id <= 0x33; id++)
            if (id != 0x09 && id != 0x0a) yield return id;
        for (byte id = 0x36; id <= 0x5f; id++) yield return id;
    }

    /// <summary>Spike step 3 — batched volatile scan. Each batch binds up to 38 candidates to distinct
    /// markers; the user presses the 12 grid buttons in labeled order; markers decode position -> id.
    /// Esc skips a silent button (its id isn't in the batch); a digit means "factory emission - not in
    /// this batch". Between batches a replug clears the markers (or, when profile 0 turned out
    /// persistent, each candidate is restored from its recorded previous action).</summary>
    private static void RunGridDiscovery(MouseSession s, ButtonCaptureFile capture)
    {
        Console.WriteLine("[3/5] Grid discovery - batched volatile scan of candidate button ids");
        var positionToId = new Dictionary<int, byte>();
        int writes = 0;
        var pending = CandidateIds().ToList();
        bool volatile0 = capture.Profile0Volatile != false;

        while (pending.Count > 0 && positionToId.Count < 12 && writes < MaxScanWrites)
        {
            var batch = new Dictionary<ConsoleKey, byte>(); // marker -> candidate id
            int take = Math.Min(Markers.Length, pending.Count);
            foreach (byte id in pending.Take(take).ToArray())
            {
                var (marker, usage) = Markers[batch.Count];
                var get = Exchange(s.Handle!, RazerProtocol.BuildGetButtonBuffer(ButtonsTid, RazerProtocol.ButtonProfileDirect, id, 0x00));
                if (get is not null && RazerProtocol.ParseButtonReply(get, RazerProtocol.ButtonProfileDirect, id, 0x00,
                        out byte cat, out byte[] data) == ReplyResult.Success)
                    capture.PreviousActions[id] = new CapturedAction(cat, data);

                var set = Exchange(s.Handle!, RazerProtocol.BuildSetButtonBuffer(ButtonsTid, RazerProtocol.ButtonProfileDirect,
                    id, 0x00, RazerProtocol.FnKeyboard, new byte[] { 0x00, usage }));
                writes++;
                if (set is null || set[1] != 0x02) { Console.WriteLine($"  id 0x{id:x2}: SET rejected (skipped)"); continue; }
                batch[marker] = id;
            }
            pending.RemoveRange(0, take);

            Console.WriteLine($"\n  Batch bound ({batch.Count} candidates, {writes} writes so far).");
            Console.WriteLine("  For each grid button below: press it once. Esc = nothing typed (skip).");
            for (int pos = 1; pos <= 12; pos++)
            {
                if (positionToId.ContainsKey(pos)) continue;
                Console.Write($"    grid button {pos,2}: press it now... ");
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Escape) { Console.WriteLine("skipped"); continue; }
                if (batch.TryGetValue(key.Key, out byte id))
                {
                    positionToId[pos] = id;
                    Console.WriteLine($"marker {key.Key} -> id 0x{id:x2}");
                }
                else
                    Console.WriteLine($"'{key.Key}' is not a marker (factory emission - id not in this batch)");
            }

            if (pending.Count > 0 && positionToId.Count < 12)
            {
                if (volatile0)
                {
                    Console.WriteLine("\n  Replug/power-cycle the mouse to clear this batch, then press Enter.");
                    Console.ReadLine();
                    if (!s.WaitForReconnect()) break;
                }
                else
                {
                    foreach (var (marker, id) in batch)
                        if (capture.PreviousActions.TryGetValue(id, out var prev))
                        {
                            Exchange(s.Handle!, RazerProtocol.BuildSetButtonBuffer(ButtonsTid, RazerProtocol.ButtonProfileDirect,
                                id, 0x00, prev.Category, prev.Data));
                            writes++;
                        }
                }
            }
        }

        capture.PositionToId = positionToId;
        Console.WriteLine($"\n  Discovery done: {positionToId.Count}/12 identified, {writes} writes.");
        foreach (var (pos, id) in positionToId.OrderBy(kv => kv.Key))
            Console.WriteLine($"    position {pos,2} -> 0x{id:x2}");
        Console.WriteLine();
    }
```

- [ ] **Step 2: Build**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the full unit suite (regression check)**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: PASS.

- [ ] **Step 4: Commit**

```powershell
git add src/NagaBatteryTray/Diagnostics/ProbeCommand.cs
git commit -m "feat(probe): batched grid-id discovery with previous-action capture (spike step 3)"
```

---

### Task 7: Persistence test, restore, `--reset`, §6 template (spike steps 4–5)

**Files:**
- Modify: `src/NagaBatteryTray/Diagnostics/ProbeCommand.cs` (`RunButtons` body + three methods + `RunButtonsReset`)
- Modify: `src/NagaBatteryTray/Program.cs` (extend the `--probe-buttons` branch for `--reset`)

**Interfaces:**
- Consumes: everything from Tasks 4–6.
- Produces:
  `private static void RunPersistenceTest(MouseSession s, ButtonCaptureFile capture)`;
  `private static void RunRestore(MouseSession s, ButtonCaptureFile capture)`;
  `private static void PrintResultsTemplate(ButtonCaptureFile c)`;
  `public static int RunButtonsReset()`.

- [ ] **Step 1: Finalize `RunButtons` and add the methods**

Replace the `RunButtons` tail (from `RunGridDiscovery(s, capture);` down to `return 0;`) with:

```csharp
        RunGridDiscovery(s, capture);
        capture.Save();
        RunPersistenceTest(s, capture);
        capture.Save();
        RunRestore(s, capture);
        capture.Save();
        PrintResultsTemplate(capture);
        return 0;
```

Add after `RunGridDiscovery`:

```csharp
    /// <summary>Spike step 4 — profile-lifecycle-aware persistence test. Queries which onboard slots
    /// exist (creating one if none - the likely "preamble"), has the user align the ACTIVE slot via
    /// the bottom button + LED colour (no active-profile command is documented), then tests whether
    /// a slot write survives a Synapse-free replug. The spike's one deliberate onboard-slot write.</summary>
    private static void RunPersistenceTest(MouseSession s, ButtonCaptureFile capture)
    {
        Console.WriteLine("[4/5] Persistence test (volatile vs active onboard slot)");
        if (capture.PositionToId.Count == 0)
        {
            Console.WriteLine("  no discovered grid ids - skipping.\n");
            return;
        }
        var first = capture.PositionToId.OrderBy(kv => kv.Key).First();
        int gridPos = first.Key;
        byte gridId = first.Value;

        byte slot = 0x01;
        bool created = false;
        var list = Exchange(s.Handle!, RazerProtocol.BuildGetProfileListBuffer(ButtonsTid));
        if (list is not null && RazerProtocol.ParseProfileListReply(list, out byte cap, out byte[] slots) == ReplyResult.Success)
        {
            capture.ProfileNotes = $"capacity={cap} existing=[{Hex2(slots)}]";
            Console.WriteLine($"  profile list: {capture.ProfileNotes}");
            if (slots.Length > 0) slot = slots[0];
            else
            {
                var create = Exchange(s.Handle!, RazerProtocol.BuildNewProfileBuffer(ButtonsTid, slot));
                created = create is not null && create[1] == 0x02;
                capture.ProfileNotes += $"; created slot {slot}: {(created ? "ok" : "REJECTED")}";
                Console.WriteLine($"  no slots existed - created slot {slot}: status=0x{(create is null ? 0 : create[1]):x2} (likely the §6 'preamble')");
            }
        }
        else
        {
            capture.ProfileNotes = "profile list unreadable";
            Console.WriteLine($"  profile list unreadable [{(list is null ? "no reply" : Hex(list, 16))}] - trying slot 1 blind.");
        }

        Console.WriteLine($"\n  Use the BOTTOM profile button until the profile LED shows slot {slot}'s colour");
        Console.WriteLine("  (1=white 2=red 3=green 4=blue 5=cyan), then press Enter.");
        Console.ReadLine();

        var getPrev = Exchange(s.Handle!, RazerProtocol.BuildGetButtonBuffer(ButtonsTid, slot, gridId, 0x00));
        if (getPrev is not null && RazerProtocol.ParseButtonReply(getPrev, slot, gridId, 0x00,
                out byte pc, out byte[] pd) == ReplyResult.Success)
            capture.SlotPreviousAction = new CapturedAction(pc, pd);

        var set = Exchange(s.Handle!, RazerProtocol.BuildSetButtonBuffer(ButtonsTid, slot, gridId, 0x00,
            RazerProtocol.FnKeyboard, new byte[] { 0x00, 0x6a })); // F15 = usage 0x6a, the slot marker
        Console.WriteLine($"  slot write (grid pos {gridPos}, id 0x{gridId:x2} -> F15): status=0x{(set is null ? 0 : set[1]):x2}");
        capture.SlotTested = slot;
        capture.SlotButtonId = gridId;
        capture.SlotWasCreated = created;
        capture.Save();

        Console.WriteLine("\n  Unplug/replug (or power-cycle) the mouse - with NO Razer software running -");
        Console.WriteLine("  then press Enter.");
        Console.ReadLine();
        if (!s.WaitForReconnect()) return;

        Console.WriteLine($"  Press grid button {gridPos} now. (Esc = nothing typed)");
        var key = Console.ReadKey(intercept: true);
        bool persisted = key.Key == ConsoleKey.F15;
        capture.SlotPersisted = persisted;
        Console.WriteLine(persisted
            ? "  -> F15 SURVIVED: the onboard slot persists across replug (onboard-slot model viable)."
            : $"  -> captured {key.Key}: the slot write did NOT survive, or the slot isn't active (re-apply model).");
        Console.WriteLine();
    }

    /// <summary>Spike step 5 — put the mouse back. Volatile binds clear on replug (canonical restore);
    /// the slot test is undone here (delete the slot we created, or rewrite its previous action).</summary>
    private static void RunRestore(MouseSession s, ButtonCaptureFile capture)
    {
        Console.WriteLine("[5/5] Restore");
        if (capture.SlotTested is byte slot && capture.SlotButtonId is byte id)
        {
            if (capture.SlotWasCreated)
            {
                var del = Exchange(s.Handle!, RazerProtocol.BuildDeleteProfileBuffer(ButtonsTid, slot));
                Console.WriteLine($"  deleted the slot the spike created ({slot}): status=0x{(del is null ? 0 : del[1]):x2}");
            }
            else if (capture.SlotPreviousAction is { } prev)
            {
                var res = Exchange(s.Handle!, RazerProtocol.BuildSetButtonBuffer(ButtonsTid, slot, id, 0x00, prev.Category, prev.Data));
                Console.WriteLine($"  rewrote slot {slot} button 0x{id:x2}'s previous action: status=0x{(res is null ? 0 : res[1]):x2}");
            }
            else
                Console.WriteLine($"  slot {slot}'s previous action was unreadable - if button 0x{id:x2} still types F15 on that profile, rerun --probe-buttons --reset or fix it in Synapse once.");
        }
        Console.WriteLine("  Volatile-profile binds: one final unplug/replug clears them (canonical restore).");
        Console.WriteLine($"  Capture saved to {ButtonCaptureFile.PathFor()} (used by --probe-buttons --reset).\n");
    }

    /// <summary>Prints the spec §6 results rows, ready to paste.</summary>
    private static void PrintResultsTemplate(ButtonCaptureFile c)
    {
        static string YN(bool? v) => v is null ? "_TBD_" : (v.Value ? "**yes**" : "**no**");
        Console.WriteLine("== Spec §6 results (paste into the table) ==");
        Console.WriteLine($"| Device mode at spike start | {(c.DeviceModeAtStart is byte m ? $"0x{m:x2}" : "_TBD_")} |");
        Console.WriteLine($"| Does the V2 Pro accept 0x020c? | {YN(c.SetAccepted)} |");
        string ids = c.PositionToId.Count == 0 ? "_TBD_"
            : string.Join(", ", c.PositionToId.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}→0x{kv.Value:x2}"));
        Console.WriteLine($"| The 12 thumb-grid button IDs | {ids} |");
        Console.WriteLine($"| Volatile (profile 0) applies instantly? | {YN(c.AcceptancePassed)} |");
        Console.WriteLine($"| Profile 0 clears on replug (volatile)? | {YN(c.Profile0Volatile)} |");
        Console.WriteLine($"| Onboard profile list / creation needed? | {c.ProfileNotes ?? "_TBD_"} |");
        Console.WriteLine($"| Active onboard slot persists across replug? | {YN(c.SlotPersisted)} |");
        Console.WriteLine("| Required preamble/handshake / input-feel | (from the run notes: device mode + profile creation lines above) |");
    }

    /// <summary>--probe-buttons --reset: best-effort no-replug restore from the capture file.</summary>
    public static int RunButtonsReset()
    {
        Console.WriteLine("Naga Battery Tray - --probe-buttons --reset (best-effort; a replug is the canonical restore)\n");
        var capture = ButtonCaptureFile.Load();
        if (capture is null || (capture.PreviousActions.Count == 0 && capture.SlotTested is null))
        {
            Console.WriteLine("No usable capture file - unplug/replug the mouse instead (volatile binds clear on replug).");
            return 1;
        }
        using var s = new MouseSession();
        if (!s.Open()) { Console.WriteLine("No live mouse collection found."); return 1; }

        int ok = 0, fail = 0;
        foreach (var (id, prev) in capture.PreviousActions)
        {
            var res = Exchange(s.Handle!, RazerProtocol.BuildSetButtonBuffer(ButtonsTid, RazerProtocol.ButtonProfileDirect,
                id, 0x00, prev.Category, prev.Data));
            if (res is not null && res[1] == 0x02) ok++;
            else { fail++; Console.WriteLine($"  id 0x{id:x2}: restore failed (status=0x{(res is null ? 0 : res[1]):x2})"); }
        }
        RunRestore(s, capture);
        Console.WriteLine($"Rewrote {ok} previous actions ({fail} failed). If anything is still odd: unplug/replug.");
        return fail == 0 ? 0 : 1;
    }
```

- [ ] **Step 2: Extend the dispatch**

In `Program.cs`, replace the Task 4 branch with:

```csharp
        if (args.Length > 0 && args[0] == "--probe-buttons")
        {
            AllocConsoleIfNeeded();
            return args.Length > 1 && args[1] == "--reset"
                ? Diagnostics.ProbeCommand.RunButtonsReset()
                : Diagnostics.ProbeCommand.RunButtons();
        }
```

- [ ] **Step 3: Build**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Run the full unit suite (regression check)**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: PASS — all suites green (protocol tests from Tasks 1–3 included).

- [ ] **Step 5: Commit**

```powershell
git add src/NagaBatteryTray/Diagnostics/ProbeCommand.cs src/NagaBatteryTray/Program.cs
git commit -m "feat(probe): persistence test, restore + --reset, spec §6 output (spike steps 4-5)"
```

---

### Task 8: Run the spike on hardware and fill spec §6 (THE GATE — user at the keyboard)

**Files:**
- Modify: `docs/superpowers/specs/2026-06-21-naga-button-remap-design.md` (§6 results table rows)

**Interfaces:**
- Consumes: the finished `--probe-buttons` from Tasks 4–7.
- Produces: a filled §6 table and the Stage 2 GO/NO-GO decision. **This task is interactive — Brandon
  must run it in his own terminal with the mouse in hand** (`Console.ReadKey` + pressing physical
  buttons cannot be driven by an agent).

- [ ] **Step 1: Prepare the run**

Quit the resident tray app (tray icon → Exit) and confirm no Razer software is running. Build fresh:

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
Expected: Build succeeded.

- [ ] **Step 2: Run the spike (user-driven, interactive)**

In a normal terminal window (the console must have focus for `ReadKey`):

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" "src\NagaBatteryTray\bin\Debug\net10.0-windows10.0.19041.0\NagaBatteryTray.dll" --probe-buttons`
(If SAC vetoes the load with `0x800711C7`: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build -p:Deterministic=false` and retry.)

Follow the prompts through all five steps. Save the complete console output (right-click title bar →
Edit → Select All → Copy) — it is the spike's raw evidence.

- [ ] **Step 3: Fill spec §6**

Paste the `== Spec §6 results ==` rows into the §6 table of
`docs/superpowers/specs/2026-06-21-naga-button-remap-design.md`, replacing the `_TBD_` values.
Add any run notes (rejections, odd statuses, input-feel observations during writes) to the
preamble/handshake row. Update the spec's **Status** line: `DRAFT — spike-gated` →
`SPIKE RUN <date> — PASS` or `— FAIL (close-out)` per the §6 gate definition.

- [ ] **Step 4: Verify the mouse is back to normal**

All 12 grid buttons type `1-9, 0, -, =` again (or the user's own Synapse-era bindings); wheel-click
middle-clicks. If not: run `--probe-buttons --reset`, then unplug/replug.

- [ ] **Step 5: Commit the results**

```powershell
git add docs/superpowers/specs/2026-06-21-naga-button-remap-design.md
git commit -m "docs(spec): record Stage 1 spike results (gate decision)"
```

**Gate:** PASS (command accepted + 12 IDs captured + at least the volatile write honored) → write the
Stage 2 plan (persistence model per §6 rows). FAIL → close out Phase B as non-viable on this firmware
(Phase C precedent), keeping `--probe-buttons` as the re-test tool.
