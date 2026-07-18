# Read-only Profile Probe (`--probe-profile`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A read-only CLI diagnostic that inventories the mouse's onboard profiles and hunts, via diff-across-states, for an undocumented command that reports the active slot.

**Architecture:** One new protocol builder (`RazerProtocol.BuildProfileGetProbeBuffer`, get-half ids only), one pure analysis module (`ProfileProbeAnalysis`: fingerprint selection + hit rules, fully unit-tested), and one new interactive probe command (`ProfileProbeCommand`) that reuses `ProbeCommand`'s transport helpers (promoted to `internal`). Spec: `docs/superpowers/specs/2026-07-18-naga-profile-probe-design.md`.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), C#, HidSharp (enumeration only), raw `HidD_*Feature` P/Invoke, xUnit.

## Global Constraints

- **Zero writes of any kind** (spec §2): only get-half command ids (`>= 0x80`) may ever reach the device; the new builder throws on `commandId < 0x80`; `ProfileProbeCommand` never calls any `Build*Set*`/`BuildNewProfileBuffer`/`BuildDeleteProfileBuffer`.
- **Perf gate** (CLAUDE.md): no new background timers/threads; the probe is a one-shot CLI path that never runs in the resident tray app.
- Transaction id resolved via cached id then `RazerProtocol.TransactionIdProbeSet` — never hardcode `0x1f` in the new code (spec §4.1). SET→GET pacing comes from `AppSettings.SetReadDelayMs` (spec §4.1).
- Tests cover logic layers only (`RazerProtocol` builder bytes, `ProfileProbeAnalysis`); the interactive flow is hardware-exercised. Tests reach `internal` via the existing `InternalsVisibleTo.cs` — don't tighten visibility.
- Offsets in all output use **90-byte report indexing** (args at report `[8..87]`); the 91-byte HID buffer prepends the report id, so `buffer[i] == report[i-1]` (spec §9).
- Build/test with the user-local SDK: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build|test`.
- Conventional commits; every commit message ends with the two trailers used on this repo (Co-Authored-By + Claude-Session).

**File map:**
- Modify: `src/NagaBatteryTray/Hid/RazerProtocol.cs` (one builder)
- Modify: `src/NagaBatteryTray/Diagnostics/ProbeCommand.cs` (promote `Exchange`/`CreateFile`/`HidD_*`/`Hex`/`Hex2` to `internal`; add delay parameter to `Exchange`)
- Create: `src/NagaBatteryTray/Diagnostics/ProfileProbeAnalysis.cs` (pure: fingerprint + hit rules)
- Create: `src/NagaBatteryTray/Diagnostics/ProfileProbeCommand.cs` (interactive flow + markdown capture)
- Modify: `src/NagaBatteryTray/Program.cs` (`--probe-profile` switch)
- Modify: `CLAUDE.md` (HID diagnostics line)
- Create: `tests/NagaBatteryTray.Tests/ProfileProbeAnalysisTests.cs`
- Modify: `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs` (builder tests)

---

### Task 1: `RazerProtocol.BuildProfileGetProbeBuffer`

**Files:**
- Modify: `src/NagaBatteryTray/Hid/RazerProtocol.cs` (add after `BuildDeleteProfileBuffer`, ~line 205)
- Test: `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs`

**Interfaces:**
- Consumes: existing private `BuildReport(byte tid, byte dataSize, byte class, byte id, ReadOnlySpan<byte> payload)` and `CommandClassProfile` (`0x05`).
- Produces: `public static byte[] BuildProfileGetProbeBuffer(byte transactionId, byte commandId, byte dataSize, ReadOnlySpan<byte> args)` — Task 5 builds every candidate request with it.

- [ ] **Step 1: Write the failing tests**

Append to `RazerProtocolTests` (match the file's existing test style):

```csharp
[Fact]
public void ProfileGetProbe_places_class_id_size_args_and_crc()
{
    var buf = RazerProtocol.BuildProfileGetProbeBuffer(0x1f, 0x84, 0x06, new byte[] { 0x01, 0x02 });
    Assert.Equal(91, buf.Length);
    Assert.Equal(0x00, buf[0]); // report id
    Assert.Equal(0x1f, buf[2]); // tid at report[1]
    Assert.Equal(0x06, buf[6]); // data_size at report[5]
    Assert.Equal(0x05, buf[7]); // class at report[6]
    Assert.Equal(0x84, buf[8]); // command id at report[7]
    Assert.Equal(0x01, buf[9]); // args at report[8..]
    Assert.Equal(0x02, buf[10]);
    byte crc = 0;
    for (int i = 3; i <= 88; i++) crc ^= buf[i]; // XOR over report[2..87]
    Assert.Equal(crc, buf[89]);
}

[Fact]
public void ProfileGetProbe_throws_on_set_half_command_ids()
{
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        RazerProtocol.BuildProfileGetProbeBuffer(0x1f, 0x02, 0x01, new byte[] { 0x01 }));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerProtocolTests"`
Expected: FAIL — CS0117 `RazerProtocol` has no definition for `BuildProfileGetProbeBuffer` (build error counts as the failing state).

- [ ] **Step 3: Implement the builder**

Add to `RazerProtocol.cs` after `BuildDeleteProfileBuffer`:

```csharp
/// <summary>Read-only class-0x05 probe GET (--probe-profile, spec 2026-07-18 §5.3/§7). Throws
/// unless commandId has the get bit (>= 0x80) — the profile probe must be UNABLE to compose a
/// write. dataSize/args are caller-specified because candidates carry their own documented shapes.</summary>
public static byte[] BuildProfileGetProbeBuffer(byte transactionId, byte commandId, byte dataSize, ReadOnlySpan<byte> args)
{
    if (commandId < 0x80)
        throw new ArgumentOutOfRangeException(nameof(commandId), "get-half ids only (>= 0x80): the profile probe is read-only by construction");
    if (args.Length > 80) throw new ArgumentOutOfRangeException(nameof(args));
    return BuildReport(transactionId, dataSize, CommandClassProfile, commandId, args);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerProtocolTests"`
Expected: PASS (all, including the two new).

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Hid/RazerProtocol.cs tests/NagaBatteryTray.Tests/RazerProtocolTests.cs
git commit -m "feat(probe): class-0x05 get-only probe builder (read-only by construction)"
```

---

### Task 2: `ProfileProbeAnalysis` — fingerprint selection + match

**Files:**
- Create: `src/NagaBatteryTray/Diagnostics/ProfileProbeAnalysis.cs`
- Test: `tests/NagaBatteryTray.Tests/ProfileProbeAnalysisTests.cs` (create)

**Interfaces:**
- Consumes: `RawButtonAction(byte Category, byte[] Data)` and `NagaV2ProButtons.Count` from `NagaBatteryTray.Hid`.
- Produces (all `internal`, reached by tests via `InternalsVisibleTo`):
  - `readonly record struct SlotActions(byte Slot, RawButtonAction?[] Actions)` — `Actions` has 12 entries, index = grid position − 1, `null` = read failed.
  - `static int[]? SelectFingerprint(IReadOnlyList<SlotActions> inventory)` — smallest set of 1-based positions distinguishing every slot; `null` if < 2 slots or none exists.
  - `static byte? MatchFingerprint(IReadOnlyList<SlotActions> inventory, int[] positions, RawButtonAction?[] observed)` — the unique matching slot, else `null`.

**Important:** `RawButtonAction` is a record struct holding a `byte[]` — its generated equality compares the array by *reference*. All comparisons must go through the `SameAction` helper below (Category + `SequenceEqual`).

- [ ] **Step 1: Write the failing tests**

Create `tests/NagaBatteryTray.Tests/ProfileProbeAnalysisTests.cs`:

```csharp
using NagaBatteryTray.Diagnostics;
using NagaBatteryTray.Hid;
using Xunit;

public class ProfileProbeAnalysisTests
{
    private static RawButtonAction Key(byte usage) => new(0x02, new byte[] { 0x00, usage });

    /// <summary>A 12-entry factory-like row; overrides replace individual positions (1-based).</summary>
    private static RawButtonAction?[] Row(params (int Pos, RawButtonAction? A)[] overrides)
    {
        var row = new RawButtonAction?[12];
        for (int p = 1; p <= 12; p++) row[p - 1] = Key((byte)(0x1e + p - 1));
        foreach (var (pos, a) in overrides) row[pos - 1] = a;
        return row;
    }

    [Fact]
    public void Fingerprint_single_differing_position_suffices()
    {
        var inv = new List<SlotActions>
        {
            new(1, Row()),
            new(2, Row((3, Key(0x68)))), // slot 2 differs only at position 3
        };
        Assert.Equal(new[] { 3 }, ProfileProbeAnalysis.SelectFingerprint(inv));
    }

    [Fact]
    public void Fingerprint_needs_two_positions_when_no_single_one_splits_all()
    {
        // 1 vs 2 differ only at pos 2; 2 vs 3 differ only at pos 7; 1 vs 3 differ at both
        var inv = new List<SlotActions>
        {
            new(1, Row((2, Key(0x68)), (7, Key(0x69)))),
            new(2, Row((7, Key(0x69)))),
            new(3, Row((2, Key(0x68)))),
        };
        var fp = ProfileProbeAnalysis.SelectFingerprint(inv);
        Assert.NotNull(fp);
        Assert.Equal(2, fp!.Length);
        Assert.Contains(2, fp);
        Assert.Contains(7, fp);
    }

    [Fact]
    public void Fingerprint_null_when_slots_identical()
    {
        var inv = new List<SlotActions> { new(1, Row()), new(2, Row()) };
        Assert.Null(ProfileProbeAnalysis.SelectFingerprint(inv));
    }

    [Fact]
    public void Fingerprint_null_when_fewer_than_two_slots()
    {
        Assert.Null(ProfileProbeAnalysis.SelectFingerprint(new List<SlotActions> { new(1, Row()) }));
    }

    [Fact]
    public void Fingerprint_ignores_positions_with_a_failed_read()
    {
        // the only differing position (3) failed to read on slot 1 -> ineligible -> no fingerprint
        var inv = new List<SlotActions>
        {
            new(1, Row((3, null))),
            new(2, Row((3, Key(0x68)))),
        };
        Assert.Null(ProfileProbeAnalysis.SelectFingerprint(inv));
    }

    [Fact]
    public void Fingerprint_equality_is_by_value_not_array_reference()
    {
        // identical bindings in DISTINCT arrays must compare equal (no false fingerprint)
        var inv = new List<SlotActions>
        {
            new(1, Row((5, new RawButtonAction(0x02, new byte[] { 0x00, 0x22 })))),
            new(2, Row((5, new RawButtonAction(0x02, new byte[] { 0x00, 0x22 })))),
        };
        Assert.Null(ProfileProbeAnalysis.SelectFingerprint(inv));
    }

    [Fact]
    public void Match_returns_the_unique_slot()
    {
        var inv = new List<SlotActions> { new(1, Row()), new(2, Row((3, Key(0x68)))) };
        var observed = Row((3, Key(0x68)));
        Assert.Equal((byte)2, ProfileProbeAnalysis.MatchFingerprint(inv, new[] { 3 }, observed));
    }

    [Fact]
    public void Match_null_when_observed_read_failed_or_ambiguous()
    {
        var inv = new List<SlotActions> { new(1, Row()), new(2, Row((3, Key(0x68)))) };
        Assert.Null(ProfileProbeAnalysis.MatchFingerprint(inv, new[] { 3 }, Row((3, null))));
        var ambiguous = new List<SlotActions> { new(1, Row()), new(2, Row()) };
        Assert.Null(ProfileProbeAnalysis.MatchFingerprint(ambiguous, new[] { 3 }, Row()));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~ProfileProbeAnalysisTests"`
Expected: FAIL — CS0246 `SlotActions`/`ProfileProbeAnalysis` not found.

- [ ] **Step 3: Implement**

Create `src/NagaBatteryTray/Diagnostics/ProfileProbeAnalysis.cs`:

```csharp
using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Diagnostics;

/// <summary>Slot inventory row for --probe-profile analysis: the 12 grid actions read from one
/// onboard slot. Index = grid position - 1; null = that button's read failed.</summary>
internal readonly record struct SlotActions(byte Slot, RawButtonAction?[] Actions);

/// <summary>Pure analysis for --probe-profile (spec 2026-07-18 §4.3/§5.2): fingerprint selection
/// and diff-across-states hit detection. No I/O — an analyzer bug must never force recollecting
/// hardware evidence.</summary>
internal static class ProfileProbeAnalysis
{
    // RawButtonAction's generated equality compares Data by reference — always compare by value.
    private static bool SameAction(RawButtonAction? a, RawButtonAction? b) =>
        a is { } x && b is { } y && x.Category == y.Category && x.Data.AsSpan().SequenceEqual(y.Data);

    /// <summary>Smallest set of grid positions (1-based) whose action tuples uniquely distinguish
    /// every slot (spec §4.3 — the LED-independent oracle). A position is eligible only if it read
    /// successfully on every slot. Null when fewer than 2 slots or no discriminating set exists.</summary>
    internal static int[]? SelectFingerprint(IReadOnlyList<SlotActions> inventory)
    {
        if (inventory.Count < 2) return null;
        var eligible = new List<int>();
        for (int p = 1; p <= NagaV2ProButtons.Count; p++)
            if (inventory.All(s => s.Actions[p - 1] is not null)) eligible.Add(p);

        for (int k = 1; k <= eligible.Count; k++)
            foreach (var combo in Combinations(eligible, k))
                if (Discriminates(inventory, combo)) return combo;
        return null;
    }

    private static bool Discriminates(IReadOnlyList<SlotActions> inventory, int[] positions)
    {
        for (int i = 0; i < inventory.Count; i++)
            for (int j = i + 1; j < inventory.Count; j++)
                if (positions.All(p => SameAction(inventory[i].Actions[p - 1], inventory[j].Actions[p - 1])))
                    return false;
        return true;
    }

    private static IEnumerable<int[]> Combinations(List<int> items, int k)
    {
        if (k == 0 || k > items.Count) yield break;
        var idx = new int[k];
        for (int i = 0; i < k; i++) idx[i] = i;
        while (true)
        {
            yield return idx.Select(i => items[i]).ToArray();
            int pos = k - 1;
            while (pos >= 0 && idx[pos] == items.Count - k + pos) pos--;
            if (pos < 0) yield break;
            idx[pos]++;
            for (int i = pos + 1; i < k; i++) idx[i] = idx[i - 1] + 1;
        }
    }

    /// <summary>The slot whose inventory matches the observed effective actions at the fingerprint
    /// positions. Null unless exactly one slot matches (a failed observed read can never match).</summary>
    internal static byte? MatchFingerprint(IReadOnlyList<SlotActions> inventory, int[] positions, RawButtonAction?[] observed)
    {
        var matches = inventory.Where(s => positions.All(p => SameAction(s.Actions[p - 1], observed[p - 1]))).ToList();
        return matches.Count == 1 ? matches[0].Slot : null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~ProfileProbeAnalysisTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Diagnostics/ProfileProbeAnalysis.cs tests/NagaBatteryTray.Tests/ProfileProbeAnalysisTests.cs
git commit -m "feat(probe): profile fingerprint selection + match (pure, unit-tested)"
```

---

### Task 3: `ProfileProbeAnalysis` — diff-across-states hit rules

**Files:**
- Modify: `src/NagaBatteryTray/Diagnostics/ProfileProbeAnalysis.cs`
- Test: `tests/NagaBatteryTray.Tests/ProfileProbeAnalysisTests.cs`

**Interfaces:**
- Produces (all `internal`, same file):
  - `sealed record StateSamples(byte Slot, IReadOnlyList<byte[]> Replies)` — one candidate's full 91-byte reply buffers from one state visit; the tour's last visit revisits the first slot.
  - `enum OffsetClass { Hit, Noise }`
  - `sealed record OffsetFinding(int ReportOffset, OffsetClass Class, IReadOnlyDictionary<byte, byte> SlotToValue)` — `ReportOffset` in 90-byte report indexing.
  - `static IReadOnlyList<OffsetFinding> AnalyzeCandidate(IReadOnlyList<StateSamples> visits)`
- Hit rules (spec §5.2): a report offset in `[8..87]` is a **Hit** iff stable within every visit AND the slot→value mapping is bijective over ≥ 2 slots AND the revisited slot reproduces its first value; varies-but-fails-a-rule = **Noise**; constant offsets are not reported.

- [ ] **Step 1: Write the failing tests**

Append to `ProfileProbeAnalysisTests`:

```csharp
    /// <summary>91-byte reply with given (reportOffset, value) pairs set; buffer[i] = report[i-1].</summary>
    private static byte[] ReplyAt(params (int ReportOffset, byte Value)[] set)
    {
        var buf = new byte[91];
        buf[1] = 0x02; // status success at report[0]
        foreach (var (off, v) in set) buf[off + 1] = v;
        return buf;
    }

    private static StateSamples Visit(byte slot, byte value, byte value2nd = 0xff) =>
        new(slot, new List<byte[]>
        {
            ReplyAt((20, value)),
            ReplyAt((20, value2nd == 0xff ? value : value2nd)),
        });

    [Fact]
    public void Analyze_finds_a_zero_based_hit_at_the_varying_offset()
    {
        var visits = new List<StateSamples> { Visit(1, 0x00), Visit(2, 0x01), Visit(3, 0x02), Visit(1, 0x00) };
        var f = Assert.Single(ProfileProbeAnalysis.AnalyzeCandidate(visits));
        Assert.Equal(20, f.ReportOffset);
        Assert.Equal(OffsetClass.Hit, f.Class);
        Assert.Equal((byte)0x01, f.SlotToValue[2]); // encoding is 0-based — accepted as-is
    }

    [Fact]
    public void Analyze_accepts_bitmask_encodings()
    {
        var visits = new List<StateSamples> { Visit(1, 0x02), Visit(2, 0x04), Visit(3, 0x08), Visit(1, 0x02) };
        Assert.Equal(OffsetClass.Hit, Assert.Single(ProfileProbeAnalysis.AnalyzeCandidate(visits)).Class);
    }

    [Fact]
    public void Analyze_marks_revisit_mismatch_as_noise()
    {
        var visits = new List<StateSamples> { Visit(1, 0x10), Visit(2, 0x20), Visit(1, 0x30) };
        Assert.Equal(OffsetClass.Noise, Assert.Single(ProfileProbeAnalysis.AnalyzeCandidate(visits)).Class);
    }

    [Fact]
    public void Analyze_marks_non_bijective_mapping_as_noise()
    {
        var visits = new List<StateSamples> { Visit(1, 0x07), Visit(2, 0x07), Visit(3, 0x08), Visit(1, 0x07) };
        Assert.Equal(OffsetClass.Noise, Assert.Single(ProfileProbeAnalysis.AnalyzeCandidate(visits)).Class);
    }

    [Fact]
    public void Analyze_marks_instability_within_a_visit_as_noise()
    {
        var visits = new List<StateSamples> { Visit(1, 0x10, 0x11), Visit(2, 0x20), Visit(1, 0x10) };
        Assert.Equal(OffsetClass.Noise, Assert.Single(ProfileProbeAnalysis.AnalyzeCandidate(visits)).Class);
    }

    [Fact]
    public void Analyze_does_not_report_constant_offsets()
    {
        var visits = new List<StateSamples> { Visit(1, 0x42), Visit(2, 0x42), Visit(1, 0x42) };
        Assert.Empty(ProfileProbeAnalysis.AnalyzeCandidate(visits));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~ProfileProbeAnalysisTests"`
Expected: FAIL — CS0246 `StateSamples` not found.

- [ ] **Step 3: Implement**

Append to `ProfileProbeAnalysis.cs` (types above the class, method inside it):

```csharp
/// <summary>One candidate's replies (full 91-byte buffers) collected during one state visit
/// (spec §5.1). The tour's last visit re-visits the first slot; AnalyzeCandidate enforces
/// reproduction through the duplicated slot number.</summary>
internal sealed record StateSamples(byte Slot, IReadOnlyList<byte[]> Replies);

internal enum OffsetClass { Hit, Noise }

/// <summary>A reply byte that varies across states. ReportOffset uses 90-byte report indexing
/// (args live at report [8..87]); SlotToValue is the observed mapping (any encoding accepted —
/// bijectivity is required, literal slot numbers are not).</summary>
internal sealed record OffsetFinding(int ReportOffset, OffsetClass Class, IReadOnlyDictionary<byte, byte> SlotToValue);
```

```csharp
    /// <summary>Diff-across-states hit detection (spec §5.2). Hit = stable within every visit,
    /// bijective slot→value over ≥ 2 slots, reproduced on the revisit. Varies-but-fails = Noise
    /// (still reported — counters/echoes are worth seeing). Constant offsets are omitted.</summary>
    internal static IReadOnlyList<OffsetFinding> AnalyzeCandidate(IReadOnlyList<StateSamples> visits)
    {
        var findings = new List<OffsetFinding>();
        if (visits.Count == 0 ||
            visits.Any(v => v.Replies.Count == 0 || v.Replies.Any(r => r.Length != RazerProtocol.BufferLength)))
            return findings;

        for (int off = 8; off <= 87; off++)
        {
            int b = off + 1; // buffer prepends the report id
            bool stable = visits.All(v => v.Replies.All(r => r[b] == v.Replies[0][b]));
            var visitValues = visits.Select(v => (v.Slot, Value: v.Replies[0][b])).ToList();
            if (stable && visitValues.Select(x => x.Value).Distinct().Count() == 1) continue; // constant

            var slotToValue = new Dictionary<byte, byte>();
            bool reproduced = true;
            foreach (var (slot, value) in visitValues)
            {
                if (slotToValue.TryGetValue(slot, out byte prior)) { if (prior != value) reproduced = false; }
                else slotToValue[slot] = value;
            }
            bool bijective = slotToValue.Values.Distinct().Count() == slotToValue.Count && slotToValue.Count >= 2;
            var cls = stable && reproduced && bijective ? OffsetClass.Hit : OffsetClass.Noise;
            findings.Add(new OffsetFinding(off, cls, slotToValue));
        }
        return findings;
    }
```

- [ ] **Step 4: Run the full suite**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: PASS — 171 prior + 16 new = 187.

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Diagnostics/ProfileProbeAnalysis.cs tests/NagaBatteryTray.Tests/ProfileProbeAnalysisTests.cs
git commit -m "feat(probe): diff-across-states hit detection with stability/bijectivity/revisit rules"
```

---

### Task 4: probe scaffolding — session, capture, inventory, `--probe-profile` switch

**Files:**
- Modify: `src/NagaBatteryTray/Diagnostics/ProbeCommand.cs` (visibility + `Exchange` delay param only)
- Create: `src/NagaBatteryTray/Diagnostics/ProfileProbeCommand.cs`
- Modify: `src/NagaBatteryTray/Program.cs`
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: `ProbeCommand.Exchange(SafeFileHandle, byte[], int)`, `ProbeCommand.CreateFile(...)`, `ProbeCommand.Hex/Hex2`, `RazerProtocol.*` builders/parsers, `JsonSettingsStore.DefaultPath()`, `AppSettings.SetReadDelayMs`, `KeyToHidUsage.Describe(byte, byte)`, `NagaV2ProButtons`, Task 2's `SlotActions`.
- Produces: `internal static int ProfileProbeCommand.Run()`; private helpers Task 5 extends: `ProfileSession` (with `.Exchange(byte[])`, `.Alive()`, `.Tid`, `.Pid`), `ProfileCapture` (with `.Add(string)`, `.StampedPath`), `InventorySnapshot`, `ReadInventory`, `Describe`, `RenderInventory`.
- Deliverable: `NagaBatteryTray.exe --probe-profile` runs preflight → open/resolve-tid → inventory + device mode + read-through → fingerprint selection → capture file written. (State tours arrive in Task 5.)

- [ ] **Step 1: Promote `ProbeCommand`'s transport helpers**

In `ProbeCommand.cs`, change **only** these declarations (no body changes except the delay):

`Exchange` (line ~565) — `private` → `internal`, plus a first-delay parameter defaulting to the old constant so all existing call sites compile unchanged:

```csharp
    /// <summary>SET -> wait -> GET with busy retry (same pacing as DockOneShot). Null on transport
    /// failure. firstDelayMs defaults to the historical 400 ms; --probe-profile passes the
    /// configured SetReadDelayMs (spec 2026-07-18 §4.1).</summary>
    internal static byte[]? Exchange(SafeFileHandle h, byte[] request, int firstDelayMs = 400)
    {
        if (!HidD_SetFeature(h, request, request.Length)) return null;
        for (int tries = 0; tries < 10; tries++)
        {
            Thread.Sleep(tries == 0 ? firstDelayMs : 200);
            var reply = new byte[RazerProtocol.BufferLength];
            if (!HidD_GetFeature(h, reply, reply.Length)) return null;
            if (reply[1] != 0x01) return reply; // not busy
        }
        return null;
    }
```

`Hex` and `Hex2` (line ~578) — `private` → `internal`.
`CreateFile` P/Invoke (line ~703) — `private` → `internal`.
(`HidD_SetFeature`/`HidD_GetFeature` stay `private` — only `Exchange` touches them.)

- [ ] **Step 2: Create `ProfileProbeCommand.cs` (scaffolding + inventory)**

```csharp
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;
using HidSharp;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Ui;

namespace NagaBatteryTray.Diagnostics;

/// <summary>--probe-profile: read-only profile inventory + active-slot read hunt
/// (spec docs/superpowers/specs/2026-07-18-naga-profile-probe-design.md). ZERO writes: every
/// request is a get-half id — enforced by BuildProfileGetProbeBuffer's throw and by this file
/// never referencing a Set/New/Delete builder.</summary>
internal static class ProfileProbeCommand
{
    internal static int Run()
    {
        Console.WriteLine("Naga Battery Tray - read-only profile probe (--probe-profile)\n");
        Console.WriteLine("PREFLIGHT (spec §4.1): close the TRAY APP and ALL Razer software (Synapse etc.)");
        Console.WriteLine("- a concurrent HID client interleaves exchanges and can corrupt the evidence.");
        Console.Write("Both closed? Continue? [y/N] ");
        if (Console.ReadKey(intercept: true).Key != ConsoleKey.Y) { Console.WriteLine("\naborted."); return 1; }
        Console.WriteLine("\n");

        var store = new JsonSettingsStore(JsonSettingsStore.DefaultPath());
        int delay = store.Settings.SetReadDelayMs;

        using var s = ProfileSession.Open(store.GetCachedTransactionId(), delay);
        if (s is null) { Console.WriteLine("No live mouse collection answered (connected? awake?)."); return 1; }
        Console.WriteLine($"Live collection: PID 0x{s.Pid:x4}, tid 0x{s.Tid:x2} (resolved), SET->GET delay {delay} ms\n");

        var capture = new ProfileCapture(s.Pid, s.Tid, delay);
        Console.WriteLine($"Capture: {capture.StampedPath}\n");

        Console.WriteLine("[1/2] Inventory (verified commands only)");
        var inv = ReadInventory(s);
        capture.Add(RenderInventory(inv, "Inventory (before)"));
        PrintInventorySummary(inv);

        int[]? fingerprint = ProfileProbeAnalysis.SelectFingerprint(inv.Rows);
        capture.Add(fingerprint is null
            ? "- fingerprint: NONE — states will be LED-identified, not independently verified (spec §4.3)"
            : $"- fingerprint positions: [{string.Join(", ", fingerprint)}]");
        Console.WriteLine(fingerprint is null
            ? "  fingerprint: none (slots not uniquely distinguishable) - LED-identified states only"
            : $"  fingerprint positions: [{string.Join(", ", fingerprint)}]\n");

        Console.WriteLine("[2/2] Active-slot hunt: implemented in the next task (plan task 5).");
        return 0;
    }

    // ---- session ----

    private sealed class ProfileSession : IDisposable
    {
        public SafeFileHandle Handle { get; }
        public int Pid { get; }
        public byte Tid { get; }
        private readonly int _delayMs;

        private ProfileSession(SafeFileHandle h, int pid, byte tid, int delayMs)
        { Handle = h; Pid = pid; Tid = tid; _delayMs = delayMs; }

        /// <summary>Wired PID first, then wireless (a stale dongle collection stays enumerated when
        /// the mouse goes wired); per collection the tid is resolved — cached id first, then the
        /// probe set — and the first (handle, tid) whose battery query answers wins (spec §4.1).</summary>
        public static ProfileSession? Open(byte? cachedTid, int delayMs)
        {
            var tids = new List<byte>();
            if (cachedTid is byte c) tids.Add(c);
            foreach (byte t in RazerProtocol.TransactionIdProbeSet) if (!tids.Contains(t)) tids.Add(t);

            foreach (int pid in new[] { RazerProtocol.MousePidWired, RazerProtocol.MousePidWireless })
                foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
                {
                    int max = -1;
                    try { max = dev.GetMaxFeatureReportLength(); } catch { }
                    if (max != RazerProtocol.BufferLength) continue;
                    var h = ProbeCommand.CreateFile(dev.DevicePath, 0, 0x3, IntPtr.Zero, 0x3, 0, IntPtr.Zero);
                    if (h.IsInvalid) { h.Dispose(); continue; }
                    foreach (byte tid in tids)
                    {
                        var probe = ProbeCommand.Exchange(h, RazerProtocol.BuildFeatureBuffer(tid, RazerProtocol.CommandIdBattery), delayMs);
                        if (probe is not null && probe[1] == 0x02) return new ProfileSession(h, pid, tid, delayMs);
                    }
                    h.Dispose();
                }
            return null;
        }

        public byte[]? Exchange(byte[] request) => ProbeCommand.Exchange(Handle, request, _delayMs);

        /// <summary>Battery liveness sentinel (spec §6) — false aborts the current pass.</summary>
        public bool Alive()
        {
            var r = Exchange(RazerProtocol.BuildFeatureBuffer(Tid, RazerProtocol.CommandIdBattery));
            return r is not null && r[1] == 0x02;
        }

        public void Dispose() => Handle.Dispose();
    }

    // ---- inventory ----

    private sealed record InventorySnapshot(bool ListOk, byte Capacity, byte[] Slots, bool ModeOk, byte Mode,
        IReadOnlyList<SlotActions> Rows, RawButtonAction?[] Effective);

    private static InventorySnapshot ReadInventory(ProfileSession s)
    {
        var listReply = s.Exchange(RazerProtocol.BuildGetProfileListBuffer(s.Tid));
        byte cap = 0; byte[] slots = Array.Empty<byte>();
        bool listOk = listReply is not null &&
            RazerProtocol.ParseProfileListReply(listReply, out cap, out slots) == ReplyResult.Success;

        var modeReply = s.Exchange(RazerProtocol.BuildGetDeviceModeBuffer(s.Tid));
        byte mode = 0xff;
        bool modeOk = modeReply is not null &&
            RazerProtocol.ParseDeviceModeReply(modeReply, out mode) == ReplyResult.Success;

        var rows = new List<SlotActions>();
        foreach (byte slot in slots)
            rows.Add(new SlotActions(slot, ReadSlotActions(s, slot)));

        var effective = ReadSlotActions(s, RazerProtocol.ButtonProfileDirect); // profile-0 read-through
        return new InventorySnapshot(listOk, cap, slots, modeOk, mode, rows, effective);
    }

    private static RawButtonAction?[] ReadSlotActions(ProfileSession s, byte profile)
    {
        var actions = new RawButtonAction?[NagaV2ProButtons.Count];
        for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
        {
            byte id = NagaV2ProButtons.IdForPosition(pos);
            var rep = s.Exchange(RazerProtocol.BuildGetButtonBuffer(s.Tid, profile, id, 0x00));
            if (rep is not null && RazerProtocol.ParseButtonReply(rep, profile, id, 0x00,
                    out byte catg, out byte[] data) == ReplyResult.Success)
                actions[pos - 1] = new RawButtonAction(catg, data);
        }
        return actions;
    }

    /// <summary>Raw bytes always survive in the capture; decode only recognized categories
    /// (spec §4.2 — the Phase B run also observed mouse category 0x01, which the app doesn't model).</summary>
    private static string Describe(RawButtonAction? a) => a switch
    {
        null => "unreadable",
        { Category: RazerProtocol.FnDisabled } => "Disabled",
        { Category: RazerProtocol.FnKeyboard, Data.Length: 2 } k => KeyToHidUsage.Describe(k.Data[0], k.Data[1]),
        { } o => $"unknown category 0x{o.Category:x2}",
    };

    private static string RenderInventory(InventorySnapshot inv, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {title}");
        sb.AppendLine($"- profile list: {(inv.ListOk ? $"capacity={inv.Capacity} existing=[{ProbeCommand.Hex2(inv.Slots)}]" : "UNREADABLE")}");
        sb.AppendLine($"- device mode: {(inv.ModeOk ? $"0x{inv.Mode:x2}" : "UNREADABLE")}");
        foreach (var row in inv.Rows) AppendActionsTable(sb, $"slot {row.Slot}", row.Actions);
        AppendActionsTable(sb, "profile 0 read-through (effective — names the active slot's content)", inv.Effective);
        return sb.ToString();
    }

    private static void AppendActionsTable(StringBuilder sb, string title, RawButtonAction?[] actions)
    {
        sb.AppendLine($"\n### {title}");
        sb.AppendLine("| pos | category | data | decoded |");
        sb.AppendLine("|---|---|---|---|");
        for (int p = 1; p <= NagaV2ProButtons.Count; p++)
            sb.AppendLine(actions[p - 1] is { } a
                ? $"| {p} | 0x{a.Category:x2} | {ProbeCommand.Hex2(a.Data)} | {Describe(a)} |"
                : $"| {p} | - | - | unreadable |");
    }

    private static void PrintInventorySummary(InventorySnapshot inv)
    {
        Console.WriteLine($"  profile list: {(inv.ListOk ? $"capacity={inv.Capacity} existing=[{ProbeCommand.Hex2(inv.Slots)}]" : "UNREADABLE")}");
        Console.WriteLine($"  device mode: {(inv.ModeOk ? $"0x{inv.Mode:x2}" : "UNREADABLE")}");
        foreach (var row in inv.Rows)
        {
            int readable = row.Actions.Count(a => a is not null);
            Console.WriteLine($"  slot {row.Slot}: {readable}/12 buttons read");
        }
    }

    // ---- capture ----

    /// <summary>Markdown capture, checkpointed to disk on every Add (spec §4.6/§9): a timestamped
    /// file plus a -latest copy — never silently overwrite the only hardware record.</summary>
    private sealed class ProfileCapture
    {
        private readonly List<string> _blocks = new();
        public string StampedPath { get; }
        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NagaBatteryTray");

        public ProfileCapture(int pid, byte tid, int delayMs)
        {
            StampedPath = Path.Combine(Dir, $"probe-profile-{DateTime.Now:yyyyMMdd-HHmmss}.md");
            string ver = typeof(ProfileProbeCommand).Assembly.GetName().Version?.ToString() ?? "?";
            _blocks.Add(
                "# --probe-profile capture\n\n" +
                $"- date: {DateTime.Now:yyyy-MM-dd HH:mm}\n" +
                $"- app version: {ver}\n" +
                $"- PID: 0x{pid:x4} ({(pid == RazerProtocol.MousePidWired ? "wired" : "wireless")})\n" +
                $"- transaction id: 0x{tid:x2} (resolved)\n" +
                $"- SET->GET delay: {delayMs} ms\n" +
                "- offsets use 90-byte REPORT indexing (args at [8..87]); the 91-byte HID buffer prepends the report id\n");
            Save();
        }

        public void Add(string block) { _blocks.Add(block); Save(); }

        private void Save()
        {
            Directory.CreateDirectory(Dir);
            string text = string.Join("\n", _blocks) + "\n";
            File.WriteAllText(StampedPath, text);
            File.WriteAllText(Path.Combine(Dir, "probe-profile-latest.md"), text);
        }
    }
}
```

- [ ] **Step 3: Add the `--probe-profile` switch**

In `Program.cs`, after the `--probe-buttons` block (line ~40):

```csharp
        if (args.Length > 0 && args[0] == "--probe-profile")
        {
            AllocConsoleIfNeeded();
            return Diagnostics.ProfileProbeCommand.Run();
        }
```

- [ ] **Step 4: Build + full suite**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build` then `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: build clean; 187 tests PASS (this task adds none — the interactive flow is hardware-exercised per repo conventions).

- [ ] **Step 5: Update CLAUDE.md diagnostics line**

In the "Build / test / install" section, extend the HID diagnostics bullet:

```
- HID diagnostics: `NagaBatteryTray.exe --probe` (battery), `--probe-dpi` (raw DPI reply offsets),
  `--probe-buttons` (remap spike: acceptance/grid-discovery/persistence; `--reset` restores recorded
  actions, `--slot-test` re-runs the scratch-slot persistence test), `--probe-profile` (read-only
  profile inventory + active-slot read hunt; capture to `%APPDATA%\NagaBatteryTray\probe-profile-*.md`).
```

- [ ] **Step 6: Commit**

```bash
git add src/NagaBatteryTray/Diagnostics/ProbeCommand.cs src/NagaBatteryTray/Diagnostics/ProfileProbeCommand.cs src/NagaBatteryTray/Program.cs CLAUDE.md
git commit -m "feat(probe): --probe-profile scaffolding - session with tid resolution, inventory, checkpointed capture"
```

---

### Task 5: shortlist research, state tours, both passes, integrity re-check, verdict

**Files:**
- Modify: `src/NagaBatteryTray/Diagnostics/ProfileProbeCommand.cs`

**Interfaces:**
- Consumes: Task 4's `ProfileSession`/`ProfileCapture`/`InventorySnapshot`/`ReadInventory`/`ReadSlotActions`, Task 1's `BuildProfileGetProbeBuffer`, Task 2/3's `MatchFingerprint`/`StateSamples`/`AnalyzeCandidate`/`OffsetFinding`/`OffsetClass`.
- Produces: the finished `Run()`; no new public surface.

- [ ] **Step 1: Research the pass-1 shortlist (evidence-bearing, spec §5.3)**

Fetch and read for class-`0x05` **get** commands (id `>= 0x80`) and their request shapes:
- `https://github.com/geezmolycos/razerqdhid` — locate the profile command definitions (the repo that sourced our verified `0x81` list / `0x02` create / `0x03` delete; search its source for the profile/`0x05` command family).
- `https://raw.githubusercontent.com/openrazer/openrazer/master/driver/razerchromacommon.c` — search for `0x05` command-class constructors.

For each get-id found, record: id, data_size, argument bytes, exact source file/function, and status (`documented`). Add each as a `ProbeCandidate` in `Shortlist()` below with its `Source` string. **Rules:** no numeric id without a readable source; `0x05/0x80` may be added only if a source documents it (as an available-count control). **Fallback:** if the fetches fail or yield no class-0x05 gets beyond `0x81`, the shortlist is the control alone — pass 2 then carries the discovery load; note the collapse in the capture via the corpus table's source column.

- [ ] **Step 2: Add the candidate model + corpora**

```csharp
    /// <summary>A fully specified probe request (spec §5.3): shape + provenance. Status is
    /// hardware-verified | documented | speculative.</summary>
    private sealed record ProbeCandidate(string Key, byte CommandId, byte DataSize, byte[] Args, string Source, string Status);

    /// <summary>Pass-1 corpus. 0x05/0x81 rides as the hardware-verified control — it must answer in
    /// every state and must NOT track the slot (the existing-slot set doesn't change when the active
    /// slot does). Candidates below the control were added by the plan's research step, each with a
    /// readable source; if research found none, the control stands alone and pass 2 carries discovery.</summary>
    private static List<ProbeCandidate> Shortlist() => new()
    {
        new("0x05/0x81 ds06 (control)", 0x81, 0x06, new byte[6], "Phase B spike 2026-07-11 (hardware)", "hardware-verified"),
        // research-step candidates land here: new("0x05/0xNN dsNN", 0xNN, 0xNN, new byte[]{...}, "<repo>/<file> <function>", "documented"),
    };

    /// <summary>Pass-2 corpus (opt-in): class-0x05 ids 0x80..0x9f not already tried in this shape.
    /// One declared shape — ds 0x06, six zero args, mirroring the verified class-0x05 get — so a
    /// miss claims only "no hit for the zero-argument ds-0x06 form" (spec §5.3).</summary>
    private static List<ProbeCandidate> SweepCorpus(IReadOnlyList<ProbeCandidate> tried)
    {
        var seen = tried.Select(c => (c.CommandId, c.DataSize)).ToHashSet();
        var list = new List<ProbeCandidate>();
        for (byte id = 0x80; id <= 0x9f; id++)
            if (!seen.Contains((id, (byte)0x06)))
                list.Add(new ProbeCandidate($"0x05/0x{id:x2} ds06", id, 0x06, new byte[6], "blind sweep (opt-in)", "speculative"));
        return list;
    }

    private static string ColourFor(byte slot) => slot switch
    {
        1 => "white", 2 => "red", 3 => "green", 4 => "blue", 5 => "cyan", _ => "?" };
```

- [ ] **Step 3: Add the state tour**

```csharp
    private sealed record VisitRecord(byte AskedSlot, byte TypedSlot, byte? OracleSlot,
        Dictionary<string, List<byte[]>> Replies)
    {
        /// <summary>Slot identity for analysis: the fingerprint oracle when it resolved, else the
        /// user's typed LED colour (spec §4.3 — LED-identified when no oracle exists).</summary>
        public byte EffectiveSlot => OracleSlot ?? TypedSlot;
    }

    /// <summary>One full tour: every existing slot once, then a revisit of the first (spec §5.1);
    /// 2 samples per candidate per state; battery sentinel before each candidate; capture
    /// checkpointed after every completed state. Null = aborted (partial evidence already saved).</summary>
    private static List<VisitRecord>? RunTour(ProfileSession s, ProfileCapture capture, InventorySnapshot inv,
        int[]? fingerprint, IReadOnlyList<ProbeCandidate> corpus, string passName)
    {
        var order = inv.Slots.Concat(new[] { inv.Slots[0] }).ToArray();
        var visits = new List<VisitRecord>();
        for (int i = 0; i < order.Length; i++)
        {
            byte asked = order[i];
            bool revisit = i == order.Length - 1;
            Console.WriteLine($"\n[{passName}] Visit {i + 1}/{order.Length}{(revisit ? " (revisit)" : "")}: use the BOTTOM");
            Console.WriteLine($"  button until the profile LED shows slot {asked} ({ColourFor(asked)}), then press Enter.");
            Console.ReadLine();

            byte typed = ReadTypedSlot();
            if (typed != asked)
            {
                Console.WriteLine($"  typed {typed} but slot {asked} was requested - cycle again and retype.");
                typed = ReadTypedSlot();
            }
            byte? oracle = fingerprint is null ? null : OracleRead(s, inv.Rows, fingerprint);
            if (oracle is byte o && o != typed)
                Console.WriteLine($"  WARNING: fingerprint says slot {o}, LED typed {typed} - recording both; analysis uses {o}.");

            var byCandidate = new Dictionary<string, List<byte[]>>();
            foreach (var cand in corpus)
            {
                if (!s.Alive())
                {
                    capture.Add($"- **ABORT** during {passName} visit {i + 1} ({cand.Key}): battery sentinel went silent (spec §6). Partial evidence above stands.");
                    Console.WriteLine("  DEVICE STOPPED ANSWERING - aborting this pass (capture is checkpointed).");
                    return null;
                }
                var samples = new List<byte[]>();
                for (int rep = 0; rep < 2; rep++)
                    samples.Add(s.Exchange(RazerProtocol.BuildProfileGetProbeBuffer(s.Tid, cand.CommandId, cand.DataSize, cand.Args))
                                ?? Array.Empty<byte>());
                byCandidate[cand.Key] = samples;
            }

            var visit = new VisitRecord(asked, typed, oracle, byCandidate);
            visits.Add(visit);
            capture.Add(RenderVisit(passName, i + 1, visit, corpus));
            if (i == order.Length / 2) InputFeelPrompt(capture, $"mid {passName}");
        }
        return visits;
    }

    private static byte ReadTypedSlot()
    {
        Console.Write("  Which colour does the LED show? (1=white 2=red 3=green 4=blue 5=cyan): ");
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.KeyChar is >= '1' and <= '5') { Console.WriteLine(k.KeyChar); return (byte)(k.KeyChar - '0'); }
        }
    }

    /// <summary>Effective-action read of the fingerprint positions via profile 0 — the
    /// LED-independent oracle (spec §4.3).</summary>
    private static byte? OracleRead(ProfileSession s, IReadOnlyList<SlotActions> inventory, int[] fingerprint)
    {
        var observed = new RawButtonAction?[NagaV2ProButtons.Count];
        foreach (int pos in fingerprint)
        {
            byte id = NagaV2ProButtons.IdForPosition(pos);
            var rep = s.Exchange(RazerProtocol.BuildGetButtonBuffer(s.Tid, RazerProtocol.ButtonProfileDirect, id, 0x00));
            if (rep is not null && RazerProtocol.ParseButtonReply(rep, RazerProtocol.ButtonProfileDirect, id, 0x00,
                    out byte catg, out byte[] data) == ReplyResult.Success)
                observed[pos - 1] = new RawButtonAction(catg, data);
        }
        return ProfileProbeAnalysis.MatchFingerprint(inventory, fingerprint, observed);
    }

    private static string RenderVisit(string passName, int n, VisitRecord v, IReadOnlyList<ProbeCandidate> corpus)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {passName} visit {n}: asked slot {v.AskedSlot}, LED typed {v.TypedSlot}, " +
                      $"oracle {(v.OracleSlot is byte o ? $"slot {o}" : "n/a")} -> analysis slot {v.EffectiveSlot}");
        foreach (var cand in corpus)
            for (int i = 0; i < v.Replies[cand.Key].Count; i++)
            {
                var r = v.Replies[cand.Key][i];
                sb.AppendLine(r.Length == 0
                    ? $"- {cand.Key} sample {i + 1}: NO REPLY (transport)"
                    : $"- {cand.Key} sample {i + 1}: status=0x{r[1]:x2} [{ProbeCommand.Hex(r, 91)}]");
            }
        return sb.ToString();
    }

    private static void InputFeelPrompt(ProfileCapture capture, string when)
    {
        Console.Write($"\n  INPUT-FEEL CHECK ({when}, hard gate): move the mouse around now - any stutter/lag? [y/N] ");
        bool lag = Console.ReadKey(intercept: true).Key == ConsoleKey.Y;
        Console.WriteLine();
        capture.Add($"- input-feel ({when}): {(lag ? "**LAG REPORTED**" : "clean")}");
    }
```

- [ ] **Step 4: Add analysis recording, corpus table, and integrity re-check**

```csharp
    private static string RenderCorpus(ProfileSession s, IReadOnlyList<ProbeCandidate> corpus, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Corpus: {title}");
        sb.AppendLine("| key | data_size | args | request (91-byte hex, first 16) | source | status |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var c in corpus)
        {
            var req = RazerProtocol.BuildProfileGetProbeBuffer(s.Tid, c.CommandId, c.DataSize, c.Args);
            sb.AppendLine($"| {c.Key} | 0x{c.DataSize:x2} | {ProbeCommand.Hex2(c.Args)} | {ProbeCommand.Hex(req, 16)} | {c.Source} | {c.Status} |");
        }
        return sb.ToString();
    }

    /// <summary>Runs the pure analyzer per candidate and records the outcome. Only candidates whose
    /// every sample answered status 0x02 are analyzable (a failure reply's body is typically the
    /// echoed request — diffing it would manufacture noise).</summary>
    private static List<(ProbeCandidate Cand, OffsetFinding Hit)> AnalyzeAndRecord(
        ProfileCapture capture, IReadOnlyList<ProbeCandidate> corpus, List<VisitRecord> visits, string passName)
    {
        var hits = new List<(ProbeCandidate, OffsetFinding)>();
        var sb = new StringBuilder($"## Analysis: {passName}\n");
        foreach (var cand in corpus)
        {
            var states = new List<StateSamples>();
            bool complete = true;
            foreach (var v in visits)
            {
                var samples = v.Replies[cand.Key];
                if (samples.Any(r => r.Length != RazerProtocol.BufferLength || r[1] != 0x02)) { complete = false; break; }
                states.Add(new StateSamples(v.EffectiveSlot, samples));
            }
            if (!complete) { sb.AppendLine($"- {cand.Key}: not analyzable (missing/non-success replies — see visit tables)"); continue; }

            var findings = ProfileProbeAnalysis.AnalyzeCandidate(states);
            if (findings.Count == 0) sb.AppendLine($"- {cand.Key}: all reply bytes constant across states");
            foreach (var f in findings)
            {
                string map = string.Join(", ", f.SlotToValue.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}->0x{kv.Value:x2}"));
                sb.AppendLine($"- {cand.Key} report[{f.ReportOffset}]: **{f.Class}** ({map})");
                if (f.Class == OffsetClass.Hit)
                {
                    if (cand.Status == "hardware-verified" && cand.CommandId == RazerProtocol.CommandIdGetProfileList)
                        sb.AppendLine("  - **WARNING: the control tracked the slot — treat this run as suspect.**");
                    else hits.Add((cand, f));
                }
            }
        }
        capture.Add(sb.ToString());
        return hits;
    }

    /// <summary>Spec §4.5: byte-compare the observable profile surfaces before vs after. The
    /// profile-0 read-through view is deliberately excluded — it legitimately follows the active slot.</summary>
    private static List<string> CompareInventories(InventorySnapshot before, InventorySnapshot after)
    {
        var diffs = new List<string>();
        if (before.ListOk != after.ListOk || before.Capacity != after.Capacity || !before.Slots.SequenceEqual(after.Slots))
            diffs.Add("profile list changed");
        if (before.ModeOk != after.ModeOk || before.Mode != after.Mode) diffs.Add("device mode changed");
        foreach (var b in before.Rows)
        {
            var a = after.Rows.FirstOrDefault(r => r.Slot == b.Slot);
            if (a.Actions is null) { diffs.Add($"slot {b.Slot} missing after"); continue; }
            for (int p = 1; p <= NagaV2ProButtons.Count; p++)
            {
                var x = b.Actions[p - 1]; var y = a.Actions[p - 1];
                bool same = (x is null && y is null) ||
                    (x is { } xa && y is { } ya && xa.Category == ya.Category && xa.Data.SequenceEqual(ya.Data));
                if (!same) diffs.Add($"slot {b.Slot} pos {p} changed");
            }
        }
        return diffs;
    }
```

- [ ] **Step 5: Replace `Run()`'s tail (the `[2/2] … next task` placeholder) with the full hunt**

```csharp
        if (inv.Slots.Length < 2)
        {
            string why = $"only {inv.Slots.Length} existing slot(s): diff-across-states cannot discriminate (spec §5.1) - inventory-only run";
            Console.WriteLine($"  {why}");
            capture.Add($"- {why}\n\n## Verdict\nINVENTORY ONLY - create a second onboard slot (e.g. via the dashboard) and re-run to hunt.");
            return 0;
        }

        Console.WriteLine("[2/3] Pass 1 - sourced shortlist");
        var corpus1 = Shortlist();
        capture.Add(RenderCorpus(s, corpus1, "pass 1 (sourced shortlist)"));
        var visits1 = RunTour(s, capture, inv, fingerprint, corpus1, "pass 1");
        var hits = visits1 is null
            ? new List<(ProbeCandidate Cand, OffsetFinding Hit)>()
            : AnalyzeAndRecord(capture, corpus1, visits1, "pass 1");

        if (hits.Count == 0 && visits1 is not null)
        {
            Console.WriteLine("\nPass 1 found no active-slot read. Pass 2 blind-sweeps class-0x05 get ids 0x80..0x9f");
            Console.WriteLine("(ds 0x06, zero args). RESIDUAL RISK (spec §4.4): reads are proven side-effect-free only");
            Console.WriteLine("for verified ids; an undocumented id is probably inert but UNPROVEN on this firmware.");
            Console.Write("Run pass 2? [y/N] ");
            bool sweep = Console.ReadKey(intercept: true).Key == ConsoleKey.Y;
            Console.WriteLine();
            if (sweep)
            {
                var corpus2 = SweepCorpus(corpus1);
                capture.Add(RenderCorpus(s, corpus2, "pass 2 (blind sweep, opt-in)"));
                var visits2 = RunTour(s, capture, inv, fingerprint, corpus2, "pass 2");
                if (visits2 is not null) hits = AnalyzeAndRecord(capture, corpus2, visits2, "pass 2");
            }
            else capture.Add("- pass 2: declined by user (recorded)");
        }

        Console.WriteLine("\n[3/3] Integrity re-check (spec §4.5)");
        var after = ReadInventory(s);
        var diffs = CompareInventories(inv, after);
        capture.Add(RenderInventory(after, "Inventory (after)"));
        capture.Add(diffs.Count == 0
            ? "## Integrity re-check\nUNCHANGED - every observable profile surface byte-identical to the pre-run inventory."
            : "## Integrity re-check\n**CHANGED:** " + string.Join("; ", diffs));
        Console.WriteLine(diffs.Count == 0 ? "  UNCHANGED - all profile surfaces byte-identical." : $"  **CHANGED:** {string.Join("; ", diffs)}");

        InputFeelPrompt(capture, "final");

        string verdict = hits.Count > 0
            ? "## Verdict\n**HIT:** " + string.Join("; ", hits.Select(h =>
                  $"{h.Cand.Key} report[{h.Hit.ReportOffset}] ({string.Join(", ", h.Hit.SlotToValue.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}->0x{kv.Value:x2}"))})"))
              + "\nFollow-up: teach RazerDevice/BatteryMonitor the read; event-driven only (no polling)."
            : "## Verdict\nNO HIT in the enumerated corpus (see corpus tables above for the exact shapes tried). " +
              "The Profile card keeps the effective-action inference.";
        capture.Add(verdict);
        Console.WriteLine($"\n{verdict.Replace("## Verdict\n", "Verdict: ")}");
        Console.WriteLine($"\nCapture saved: {capture.StampedPath} (+ probe-profile-latest.md)");
        return 0;
```

- [ ] **Step 6: Build + full suite**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build` then `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: build clean; 187 tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/NagaBatteryTray/Diagnostics/ProfileProbeCommand.cs
git commit -m "feat(probe): --probe-profile passes 1+2 with sourced corpus, oracle-verified tours, integrity re-check"
```

---

### Task 6: install + hardware session handoff

**Files:** none (operational).

- [ ] **Step 1: Reinstall** — `.\scripts\install.ps1` (publishes Release and relaunches the tray; the probe runs from the installed exe).
- [ ] **Step 2: Hand off to the user for the hardware session** — the probe is interactive and needs the tray app *closed* plus the physical mouse: `& "$env:LOCALAPPDATA\Programs\NagaBatteryTray\NagaBatteryTray.exe" --probe-profile` from a normal console. The run's outputs: the console verdict + `%APPDATA%\NagaBatteryTray\probe-profile-<stamp>.md`.
- [ ] **Step 3: After the hardware run** (separate follow-up, not this plan): paste the capture's verdict into the spec's §10 outcome, flip the roadmap checkbox in CLAUDE.md, and — on a HIT — design the Profile-card consumption as its own change.

---

## Self-review (performed at write time)

- **Spec coverage:** §4.1 preflight/tid/delay → Task 4; §4.2 inventory/decode policy → Task 4; §4.3 fingerprint + LED-only fallback → Tasks 2/4/5; §4.4 passes + opt-in → Task 5; §4.5 integrity → Task 5; §4.6/§9 capture/checkpoint/indexing → Tasks 4/5; §5.1 tour/revisit/2-samples/≥2-slots → Task 5 (`RunTour`, slot-count guard); §5.2 hit rules → Task 3; §5.3 corpus tuples/sources/control/scoped miss → Task 5; §6 read-only construction/sentinel/input-feel → Tasks 1/5; §7 code layout → file map; §8 tests → Tasks 1–3; §10 outcomes → Task 5 verdict + Task 6 step 3.
- **Placeholders:** the one deliberate open list — pass-1 shortlist entries — is not a TBD: Task 5 step 1 defines the exact research procedure, the acceptance rule (readable source required), and the deterministic fallback (control-only shortlist).
- **Type consistency:** `SlotActions`/`StateSamples`/`OffsetFinding`/`OffsetClass` names and shapes match across Tasks 2–5; `ProbeCommand.Exchange(h, req, delayMs)` signature in Task 4 matches Task 5's uses via `ProfileSession.Exchange`; `RazerProtocol.BuildProfileGetProbeBuffer(tid, id, dataSize, args)` matches Tasks 1/5.

---

## Post-plan amendments (2026-07-18)

After execution, two review waves amended the delivered code beyond this plan's literal text — the
task step code blocks above are historical (what was first written), not current. Read the source
files, not this document, for the shipped behavior:

1. **Final-review fixes (commit `4eca372`)** — abort verdict branch (`RunTour` returning null now
   surfaces as an ABORTED verdict rather than falling through to NO HIT), the integrity re-check
   gated on `ProfileSession.Alive()` before re-reading the inventory, and capture-rendering polish.
2. **Cross-model-review fixes (commit `23ee0fb`)** — `OffsetFinding` gained a `Length` field and
   `int`-typed values (was `byte`) with an adjacent-pair tuple pass in `AnalyzeCandidate` (catches
   2-byte encodings bijective only as a pair); CRC-gated analyzability (a sample must be full-length,
   status-success, *and* CRC-valid to enter analysis); `CompareInventories` returns a
   `(Changed, Inconclusive)` pair instead of a flat diff list.
3. **ceb4f13** — symmetric readability classification in `CompareInventories` (a "changed" verdict
   now requires both readings to have succeeded and differ; a one-sided readability flip in either
   direction is Inconclusive, never Changed — closing the gap where an unreadable-before →
   readable-after transition could masquerade as a proven change); the CRC-gated analyzability
   predicate extracted to `internal static bool ProfileProbeCommand.SampleAnalyzable(byte[])`; and
   unit tests for both (`tests/NagaBatteryTray.Tests/ProfileProbeCommandTests.cs`).

The spec (`docs/superpowers/specs/2026-07-18-naga-profile-probe-design.md` §4.5, §5.2, §6) is the
authoritative record of current behavior — consult it, not this plan's code blocks, when the two
diverge.
