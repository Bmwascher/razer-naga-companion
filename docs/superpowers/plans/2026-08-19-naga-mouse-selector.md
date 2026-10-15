# Mouse Selector + Per-Device State Isolation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user pick which Naga the app talks to, return to that pick automatically, and never carry one mouse's cached state onto another.

**Architecture:** The device layer owns a preferred-model name and re-checks it only on the battery path, so no new timer appears. A small `DeviceScopedState` object holds everything cached about one mouse and clears itself when the connected model changes, which removes the false "Not applied" failures and the red "Synapse action" chips at their source. The dashboard header's model label becomes a ComboBox fed by an explicit reachability probe.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), C#, WPF + WinForms, WPF-UI 4.3.0, HidSharp, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-19-naga-mouse-selector-design.md`

## Global Constraints

- **Hard rule 1 (GATING).** No new background timers or threads. No polling of buttons, DPI, or profiles. Device I/O stays on-demand or on the existing battery poll. All HID calls off the UI thread and serialized on `BatteryMonitor._readLock`. Idle footprint and mouse feel are acceptance gates.
- **Hard rule 2.** Never remove the button snapshot + raw-undo path (`AppHost.RestoreRawAsync` and the sweep snapshot feeding it).
- **Hard rule 3.** No `DropShadowEffect` / `BitmapEffect` anywhere. No hardcoded colours in themed XAML — always `DynamicResource App.*` / the existing style keys.
- **Hard rule 4.** README.md and `docs/images/` screenshots update in this same branch (Task 8).
- **Hard rule 6.** TDD through the `IRazerDevice` seam using `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs`. Do not tighten `internal` visibility and do not touch `InternalsVisibleTo.cs`.
- Build: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
- Test: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test` — single class: `--filter "FullyQualifiedName~<Class>"`
- Existing style in `RazerProtocol.cs` deliberately avoids LINQ. Keep it that way in that file.
- Every test file needs `using Xunit;`. The project enables `ImplicitUsings`, which does NOT cover xUnit, and there is no global-usings file — every existing test imports it explicitly.
- Hard rule 6's HID-transport test exemption now covers only `HidControlCollection` / `HidControlCollectionSource` (the two Win32 wrappers) and the WPF windows. The connection state machine above them is tested (product-owner decision, 2026-08-19).
- Branch: `feature/mouse-selector`. Commit after every task. Do not push.

---

## Execution order

Tasks run in the order they appear here, and that order is not the numbering's original one. The
product owner gated everything behind the connection restructure (Tasks 3 and 3b), so only its
genuine prerequisite runs first: Task 2's protocol helpers, which `MouseConnection` calls. The
settings work is unrelated to the restructure and moved behind it as Task 3c, keeping its original
content. Labels were left alone so the debate record and the cross-references in each task's
Interfaces block keep pointing at the same work.

---

## File Structure

| File | Responsibility |
| --- | --- |
| `src/NagaBatteryTray/Settings/AppSettings.cs` | new `PreferredMouse`, new `ProfileNamesByMouse`, retire `ProfileNames` |
| `src/NagaBatteryTray/Settings/JsonSettingsStore.cs` | one-time migration of legacy profile names |
| `src/NagaBatteryTray/Hid/RazerProtocol.cs` | preference-aware scan order helpers (pure, testable) |
| `src/NagaBatteryTray/Hid/ConnectionPolicy.cs` | **new** — the reselect decision, pure and testable |
| `src/NagaBatteryTray/Hid/IControlCollection.cs` | **new** — the transport seam: the only place Win32 appears in the connection path |
| `src/NagaBatteryTray/Hid/IControlCollectionSource.cs` | **new** — finds and opens candidate collections |
| `src/NagaBatteryTray/Hid/HidControlCollection.cs` | **new** — the real seam: `CreateFile` + `HidD_*`, moved out of `RazerDevice` |
| `src/NagaBatteryTray/Hid/MouseConnection.cs` | **new** — the connection state machine: preference order, reselect, rollback, `ConnectionId` |
| `src/NagaBatteryTray/Hid/IRazerDevice.cs` | `PreferredModel`, `DiscoverReachableAsync` |
| `src/NagaBatteryTray/Hid/RazerDevice.cs` | protocol only now; all handle bookkeeping delegates to `MouseConnection` |
| `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs` | lock-serialized preference, discovery, and the atomic resolve-write-verify |
| `src/NagaBatteryTray/Monitoring/DeviceScopedState.cs` | **new** — per-mouse cache that clears on model change |
| `src/NagaBatteryTray/AppHost.cs` | wire the state object, the write-slot guard, the selector handler |
| `src/NagaBatteryTray/Ui/Dashboard/CalloutViewModel.cs` | render the 1-byte stub as "Not set" |
| `src/NagaBatteryTray/Ui/Dashboard/DashboardViewModel.cs` | `Mice`, `SelectedMouse`, per-mouse names, new-mouse reset |
| `src/NagaBatteryTray/Ui/Dashboard/DashboardWindow.xaml(.cs)` | header ComboBox |
| `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs` | implement the two new interface members |

---

## Task 2: Preference-aware scan order in the protocol layer

**Files:**
- Modify: `src/NagaBatteryTray/Hid/RazerProtocol.cs`
- Test: `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs`

**Interfaces:**
- Consumes: `RazerProtocol.SupportedMice`, `RazerProtocol.ScanPids`.
- Produces: `RazerProtocol.ScanPidsFor(string? preferredModel)` returning `int[]`, and `RazerProtocol.PidsForModel(string? model)` returning `int[]` (empty when unknown). Task 3 consumes both.

- [ ] **Step 1: Write the failing tests**

Append to `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs`:

```csharp
    [Fact]
    public void ScanPidsForNullPreferenceIsTheDefaultOrder()
    {
        Assert.Equal(RazerProtocol.ScanPids, RazerProtocol.ScanPidsFor(null));
    }

    [Fact]
    public void ScanPidsForUnknownModelIsTheDefaultOrder()
    {
        Assert.Equal(RazerProtocol.ScanPids, RazerProtocol.ScanPidsFor("Naga Imaginary"));
    }

    [Fact]
    public void ScanPidsForPreferredModelPutsItsWiredThenWirelessFirst()
    {
        var order = RazerProtocol.ScanPidsFor("Naga V2 Pro");

        Assert.Equal(RazerProtocol.MousePidWired, order[0]);
        Assert.Equal(RazerProtocol.MousePidWireless, order[1]);
        Assert.Equal(RazerProtocol.MousePidV3Wired, order[2]);
        Assert.Equal(RazerProtocol.MousePidV3Wireless, order[3]);
    }

    [Fact]
    public void ScanPidsForIsAPermutationOfScanPids()
    {
        foreach (var m in RazerProtocol.SupportedMice)
        {
            var order = RazerProtocol.ScanPidsFor(m.DisplayName);
            Assert.Equal(RazerProtocol.ScanPids.Length, order.Length);
            Assert.Equal(RazerProtocol.ScanPids.OrderBy(p => p), order.OrderBy(p => p));
        }
    }

    [Fact]
    public void ScanPidsForIsCaseInsensitive()
    {
        Assert.Equal(RazerProtocol.ScanPidsFor("Naga V2 Pro"), RazerProtocol.ScanPidsFor("naga v2 pro"));
    }

    [Fact]
    public void PidsForModelReturnsWiredThenWireless()
    {
        Assert.Equal(new[] { RazerProtocol.MousePidV3Wired, RazerProtocol.MousePidV3Wireless },
            RazerProtocol.PidsForModel("Naga V3 Pro"));
    }

    [Fact]
    public void PidsForModelIsEmptyForUnknownOrNull()
    {
        Assert.Empty(RazerProtocol.PidsForModel("Naga Imaginary"));
        Assert.Empty(RazerProtocol.PidsForModel(null));
    }

    [Fact]
    public void NormalizeModelNameCanonicalisesAKnownName()
    {
        Assert.Equal("Naga V3 Pro", RazerProtocol.NormalizeModelName("naga v3 pro"));
    }

    [Fact]
    public void NormalizeModelNameDropsAnUnknownName()
    {
        // an unknown preference must read as NO preference, or the device detaches and rolls back
        // on every battery read chasing a mouse that cannot exist
        Assert.Null(RazerProtocol.NormalizeModelName("Naga Imaginary"));
        Assert.Null(RazerProtocol.NormalizeModelName(null));
    }
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerProtocolTests"`
Expected: compile error — `ScanPidsFor` and `PidsForModel` do not exist.

- [ ] **Step 3: Implement the helpers**

Add to `RazerProtocol.cs`, directly after the `ScanPids` declaration. No LINQ — this file spells its order out on purpose:

```csharp
    /// <summary>Index of a model in SupportedMice by display name, or -1. Case-insensitive because
    /// the name round-trips through settings.json.</summary>
    private static int IndexOfModel(string? displayName)
    {
        if (displayName is null) return -1;
        for (int i = 0; i < SupportedMice.Length; i++)
            if (string.Equals(SupportedMice[i].DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>The two PIDs of one model, wired first. Empty when the name is null or unknown.</summary>
    public static int[] PidsForModel(string? displayName)
    {
        int i = IndexOfModel(displayName);
        return i < 0 ? Array.Empty<int>() : new[] { SupportedMice[i].WiredPid, SupportedMice[i].WirelessPid };
    }

    /// <summary>The canonical display name for a model name that came from settings, or null when it
    /// names no supported mouse. An unknown preference must behave EXACTLY like no preference
    /// (spec §4.1), and normalizing once at the boundary is what makes that true everywhere.</summary>
    public static string? NormalizeModelName(string? displayName)
    {
        int i = IndexOfModel(displayName);
        return i < 0 ? null : SupportedMice[i].DisplayName;
    }

    /// <summary>Scan order under a user preference (spec 2026-08-19 §4.2): the preferred model's
    /// wired then wireless PID, then every other model's wired PIDs, then their wireless ones. The
    /// wired-before-wireless rule still holds within each model — a cabled mouse must beat a
    /// still-plugged receiver that can no longer reach it. Null or unknown = ScanPids unchanged.</summary>
    public static int[] ScanPidsFor(string? preferredModel)
    {
        int p = IndexOfModel(preferredModel);
        if (p < 0) return ScanPids;

        var order = new int[SupportedMice.Length * 2];
        int n = 0;
        order[n++] = SupportedMice[p].WiredPid;
        order[n++] = SupportedMice[p].WirelessPid;
        for (int i = 0; i < SupportedMice.Length; i++)
            if (i != p) order[n++] = SupportedMice[i].WiredPid;
        for (int i = 0; i < SupportedMice.Length; i++)
            if (i != p) order[n++] = SupportedMice[i].WirelessPid;
        return order;
    }
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerProtocolTests"`
Expected: all passing, including the pre-existing scan-order tests.

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Hid/RazerProtocol.cs tests/NagaBatteryTray.Tests/RazerProtocolTests.cs
git commit -m "feat(hid): preference-aware scan order helpers"
```

---

## Task 3: The transport seam and the connection state machine

**Files:**
- Create: `src/NagaBatteryTray/Hid/IControlCollection.cs`
- Create: `src/NagaBatteryTray/Hid/IControlCollectionSource.cs`
- Create: `src/NagaBatteryTray/Hid/HidControlCollection.cs`
- Create: `src/NagaBatteryTray/Hid/MouseConnection.cs`
- Create: `src/NagaBatteryTray/Hid/ConnectionPolicy.cs`
- Test: `tests/NagaBatteryTray.Tests/ConnectionPolicyTests.cs` (create)
- Test: `tests/NagaBatteryTray.Tests/MouseConnectionTests.cs` (create)
- Test: `tests/NagaBatteryTray.Tests/Fakes/FakeControlCollectionSource.cs` (create)

**Interfaces:**
- Consumes: `RazerProtocol.ScanPidsFor`, `PidsForModel`, `NormalizeModelName`, `SupportedMice` (Task 2).
- Produces: `IControlCollection`, `IControlCollectionSource`, `MouseConnection` (with `EnsureAsync`, `DiscoverAsync`, `Close`, `Current`, `TransactionId`, `Wired`, `Model`, `ConnectionId`, `PreferredModel`), `ConnectionPolicy.Decide`. Task 3b rewires `RazerDevice` onto them.

**Why this task exists.** The connection state machine decides which mouse the whole app talks to, and it had no automated coverage because it was tangled with `CreateFile` and `HidD_*`. This task separates them: every Win32 call moves behind `IControlCollection`, and everything else — preference ordering, when a live handle may be reselected, rollback, disposing exactly the losing handle, and the `ConnectionId` increment — becomes ordinary testable code. The project rule that keeps HID transport out of the unit suite (hard rule 6) still holds, and it now covers only the two thin Win32 wrappers. Product-owner decision, 2026-08-19, overriding the session's recommendation to defer it.

**Do not change protocol behaviour in this task.** Buffer layout, transaction-id probing, the busy retry, and the fast-read ladder all stay exactly as they are; only where the bytes are handed to Windows moves.

- [ ] **Step 1: Write the failing policy tests**

Create `tests/NagaBatteryTray.Tests/ConnectionPolicyTests.cs`:

```csharp
using NagaBatteryTray.Hid;
using Xunit;

public sealed class ConnectionPolicyTests
{
    [Fact]
    public void NoHandleAlwaysScans()
    {
        Assert.Equal(ConnectionDecision.FullScan,
            ConnectionPolicy.Decide(false, null, "Naga V3 Pro", reselect: true));
        Assert.Equal(ConnectionDecision.FullScan,
            ConnectionPolicy.Decide(false, null, null, reselect: false));
    }

    // the sticky-selection fix: the battery path is the ONLY caller that may reselect
    [Fact]
    public void ANonBatteryCallNeverReselectsALiveHandle()
    {
        Assert.Equal(ConnectionDecision.KeepLive,
            ConnectionPolicy.Decide(true, "Naga V2 Pro", "Naga V3 Pro", reselect: false));
    }

    [Fact]
    public void TheBatteryPathProbesWhenTheLiveMouseIsNotThePreferredOne()
    {
        Assert.Equal(ConnectionDecision.ProbePreferred,
            ConnectionPolicy.Decide(true, "Naga V2 Pro", "Naga V3 Pro", reselect: true));
    }

    [Fact]
    public void AlreadyOnThePreferredMouseCostsNothing()
    {
        Assert.Equal(ConnectionDecision.KeepLive,
            ConnectionPolicy.Decide(true, "Naga V3 Pro", "Naga V3 Pro", reselect: true));
        Assert.Equal(ConnectionDecision.KeepLive,
            ConnectionPolicy.Decide(true, "naga v3 pro", "Naga V3 Pro", reselect: true));
    }

    [Fact]
    public void NoPreferenceNeverProbes()
    {
        Assert.Equal(ConnectionDecision.KeepLive,
            ConnectionPolicy.Decide(true, "Naga V2 Pro", null, reselect: true));
    }

    // MouseConnection.PreferredModel normalizes on set, so an unknown settings value arrives here as
    // null and cannot cause a detach-and-rollback on every battery read
    [Fact]
    public void AnUnknownPreferenceNormalisesToNoPreference()
    {
        Assert.Equal(ConnectionDecision.KeepLive,
            ConnectionPolicy.Decide(true, "Naga V2 Pro",
                RazerProtocol.NormalizeModelName("Naga Imaginary"), reselect: true));
    }
}
```

- [ ] **Step 2: Implement the policy**

Create `src/NagaBatteryTray/Hid/ConnectionPolicy.cs`:

```csharp
namespace NagaBatteryTray.Hid;

/// <summary>What a device call should do with the connection it already has.</summary>
internal enum ConnectionDecision
{
    /// <summary>Keep the live collection untouched. Every non-battery call lands here.</summary>
    KeepLive,
    /// <summary>Live, but on the wrong mouse and the caller may reselect: probe the preferred model
    /// only, and keep the live connection if it does not answer.</summary>
    ProbePreferred,
    /// <summary>No usable collection: scan every candidate in preference order.</summary>
    FullScan,
}

/// <summary>The reselect rule, with no handles and no Win32 in sight (spec 2026-08-19 §4.3).</summary>
internal static class ConnectionPolicy
{
    internal static ConnectionDecision Decide(bool hasLiveCollection, string? currentModel, string? preferredModel, bool reselect)
    {
        if (!hasLiveCollection) return ConnectionDecision.FullScan;
        if (!reselect || preferredModel is null) return ConnectionDecision.KeepLive;
        return string.Equals(currentModel, preferredModel, StringComparison.OrdinalIgnoreCase)
            ? ConnectionDecision.KeepLive
            : ConnectionDecision.ProbePreferred;
    }
}
```

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~ConnectionPolicyTests"`
Expected: 6 passing.

- [ ] **Step 3: Define the seam**

Create `src/NagaBatteryTray/Hid/IControlCollection.cs`:

```csharp
namespace NagaBatteryTray.Hid;

/// <summary>One open Razer control collection: the 90+1 byte feature-report endpoint, and the ONLY
/// place Win32 appears in the connection path. Everything above this interface — preference order,
/// reselect rules, rollback, disposal, connection identity — is ordinary testable code
/// (spec 2026-08-19 §6, product-owner decision 2026-08-19).</summary>
internal interface IControlCollection : IDisposable
{
    /// <summary>USB product id this collection was opened from; identifies model and link.</summary>
    int Pid { get; }

    /// <summary>False once a feature call has failed or the collection has been disposed. Callers
    /// check it before every exchange, exactly as the old code checked its handle for null: a failed
    /// HID call means the collection is finished, and probing it further is wasted Win32 calls on a
    /// dead handle that can also make an intermittent failure look like a working candidate.</summary>
    bool IsOpen { get; }

    /// <summary>HidD_SetFeature. False = the call failed and this collection is finished.</summary>
    bool SetFeature(byte[] buffer);

    /// <summary>HidD_GetFeature. False = the call failed and this collection is finished.</summary>
    bool GetFeature(byte[] buffer);
}
```

Create `src/NagaBatteryTray/Hid/IControlCollectionSource.cs`:

```csharp
using System.Collections.Generic;

namespace NagaBatteryTray.Hid;

/// <summary>Finds and opens control collections. The real implementation enumerates with HidSharp
/// and opens with CreateFile; tests substitute a fake so the selection logic runs with no hardware.</summary>
internal interface IControlCollectionSource
{
    /// <summary>Open each candidate collection exposing the 91-byte feature report, in the given PID
    /// order. Lazy: the caller stops at the first that verifies, so later candidates are never opened.
    /// A candidate that cannot be opened is skipped, not yielded.</summary>
    IEnumerable<IControlCollection> Open(int[] pids);
}
```

- [ ] **Step 4: Move the Win32 code behind it**

Create `src/NagaBatteryTray/Hid/HidControlCollection.cs`, holding the `CreateFile` / `HidD_SetFeature` / `HidD_GetFeature` declarations moved verbatim out of `RazerDevice` (same signatures, same `FileShareReadWrite`/`OpenExisting` constants), plus the source:

```csharp
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace NagaBatteryTray.Hid;

/// <summary>The real collection: a Windows HID handle. The 90-byte Razer feature report lives on the
/// mouse HID collection (mi_00 on the V2, mi_03 on the V3 — never key on interface number;
/// GetMaxFeatureReportLength == 91 is the selector), which Windows owns, so it must be opened with
/// CreateFile(dwDesiredAccess = 0) and exchanged via HidD_SetFeature/HidD_GetFeature.</summary>
internal sealed class HidControlCollection : IControlCollection
{
    private readonly SafeFileHandle _handle;

    internal HidControlCollection(int pid, SafeFileHandle handle) { Pid = pid; _handle = handle; }

    public int Pid { get; }
    public bool IsOpen { get; private set; } = true;

    public bool SetFeature(byte[] buffer) => Feature(HidD_SetFeature(_handle, buffer, buffer.Length));
    public bool GetFeature(byte[] buffer) => Feature(HidD_GetFeature(_handle, buffer, buffer.Length));

    private bool Feature(bool ok)
    {
        if (!ok) IsOpen = false;
        return ok;
    }

    public void Dispose() { IsOpen = false; _handle.Dispose(); }

    // the marshalling attributes are load-bearing: they define the native success/failure boundary
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
}

/// <summary>The real source: HidSharp enumeration plus CreateFile.</summary>
internal sealed class HidControlCollectionSource : IControlCollectionSource
{
    public IEnumerable<IControlCollection> Open(int[] pids)
    {
        foreach (int pid in pids)
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                int max;
                try { max = dev.GetMaxFeatureReportLength(); } catch { continue; }
                if (max != RazerProtocol.BufferLength) continue;

                var h = CreateFile(dev.DevicePath, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); continue; }
                yield return new HidControlCollection(pid, h);
            }
    }

    private const uint FileShareReadWrite = 0x3; // FILE_SHARE_READ | FILE_SHARE_WRITE
    private const uint OpenExisting = 0x3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
}
```

- [ ] **Step 5: Write the failing state-machine tests**

Create `tests/NagaBatteryTray.Tests/Fakes/FakeControlCollectionSource.cs`:

```csharp
using NagaBatteryTray.Hid;

/// <summary>A scriptable stand-in for the Windows HID layer: which PIDs exist, which of them answer,
/// and which blow up. Records every collection it hands out so a test can assert disposal.
/// <para>internal, not public: it implements the internal IControlCollectionSource, and a public type
/// cannot implement a less accessible interface. The test project reaches it through
/// InternalsVisibleTo, same as every other internal it touches.</para></summary>
internal sealed class FakeControlCollectionSource : IControlCollectionSource
{
    internal sealed class FakeCollection : IControlCollection
    {
        public FakeCollection(int pid) => Pid = pid;
        public int Pid { get; }
        public bool Disposed { get; private set; }
        public bool IsOpen { get; private set; } = true;

        /// <summary>When true every feature call fails and closes the collection, like a dead handle.</summary>
        public bool FailFeature { get; set; }

        /// <summary>How many feature calls this collection actually received.</summary>
        public int FeatureCalls { get; private set; }

        /// <summary>True for a collection whose mouse is awake: its replies parse.</summary>
        public bool Answers { get; set; }

        private byte[] _lastRequest = Array.Empty<byte>();

        public bool SetFeature(byte[] buffer)
        {
            if (!Feature()) return false;
            _lastRequest = (byte[])buffer.Clone();
            return true;
        }

        public bool GetFeature(byte[] buffer)
        {
            if (!Feature()) return false;
            if (!Answers) return true;   // leaves the buffer as it found it: nothing will parse
            WriteCompletedReply(buffer);
            return true;
        }

        /// <summary>Echo the request back as a completed reply the production parsers accept: status
        /// 0x02, the class/cmd echo they check, a mid-scale data byte, and a valid CRC. A zero-filled
        /// buffer parses as nothing, so without this an "answering" collection can never be adopted.</summary>
        private void WriteCompletedReply(byte[] buffer)
        {
            Array.Copy(_lastRequest, buffer, Math.Min(_lastRequest.Length, buffer.Length));
            buffer[1] = 0x02;    // status: command completed
            buffer[10] = 0x80;   // ~50% for a power query; harmless filler for other classes

            var report = new byte[RazerProtocol.ReportLength];
            Array.Copy(buffer, 1, report, 0, RazerProtocol.ReportLength);
            report[88] = RazerProtocol.ComputeCrc(report);
            Array.Copy(report, 0, buffer, 1, RazerProtocol.ReportLength);
        }

        private bool Feature()
        {
            FeatureCalls++;
            if (!FailFeature) return true;
            IsOpen = false;
            return false;
        }

        public void Dispose() { Disposed = true; IsOpen = false; }
    }

    /// <summary>PIDs present on the machine. Order here does not matter: Open honours the caller's.</summary>
    public HashSet<int> Present { get; } = new();

    /// <summary>PIDs whose collection verifies (i.e. the mouse is awake and answering).</summary>
    public HashSet<int> Answering { get; } = new();

    /// <summary>PIDs whose collection fails every feature call, standing in for a dead handle.</summary>
    public HashSet<int> FeatureFailing { get; } = new();

    /// <summary>Thrown from Open when set, to exercise the rollback path.</summary>
    public Exception? ThrowOnOpen { get; set; }

    public List<FakeCollection> Handed { get; } = new();
    public List<int> OpenedPidOrder { get; } = new();

    public IEnumerable<IControlCollection> Open(int[] pids)
    {
        if (ThrowOnOpen is { } ex) throw ex;
        foreach (int pid in pids)
        {
            if (!Present.Contains(pid)) continue;
            OpenedPidOrder.Add(pid);
            var c = new FakeCollection(pid)
            {
                FailFeature = FeatureFailing.Contains(pid),
                Answers = Answering.Contains(pid),
            };
            Handed.Add(c);
            yield return c;
        }
    }
}
```

Create `tests/NagaBatteryTray.Tests/MouseConnectionTests.cs`:

```csharp
using NagaBatteryTray.Hid;
using Xunit;

public sealed class MouseConnectionTests
{
    // never retype these: the V2's are 0x00A7/0x00A8, and a wrong literal makes every scenario below
    // silently unreachable through ScanPidsFor
    private const int V3Wired = RazerProtocol.MousePidV3Wired, V3Wireless = RazerProtocol.MousePidV3Wireless;
    private const int V2Wired = RazerProtocol.MousePidWired, V2Wireless = RazerProtocol.MousePidWireless;

    private static MouseConnection Build(FakeControlCollectionSource source, Func<int, bool>? answers = null)
    {
        answers ??= pid => source.Answering.Contains(pid);
        return new MouseConnection(source,
            (c, ct) => Task.FromResult(answers(c.Pid) ? (byte)0x1f : (byte)0));
    }

    [Fact]
    public async Task ItAdoptsTheFirstCollectionThatAnswers()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V3Wireless); source.Answering.Add(V3Wireless);
        var conn = Build(source);

        Assert.Equal((byte)0x1f, await conn.EnsureAsync(CancellationToken.None));
        Assert.Equal("Naga V3 Pro", conn.Model);
        Assert.False(conn.Wired);
        Assert.Equal(1, conn.ConnectionId);
    }

    [Fact]
    public async Task ACollectionThatDoesNotAnswerIsDisposedAndSkipped()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V3Wireless);                 // present but asleep
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);

        await conn.EnsureAsync(CancellationToken.None);

        Assert.Equal("Naga V2 Pro", conn.Model);
        Assert.True(source.Handed.Single(c => c.Pid == V3Wireless).Disposed);
        Assert.False(source.Handed.Single(c => c.Pid == V2Wireless).Disposed);
    }

    [Fact]
    public async Task ALiveConnectionIsReusedWithoutOpeningAnything()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V3Wireless); source.Answering.Add(V3Wireless);
        var conn = Build(source);
        await conn.EnsureAsync(CancellationToken.None);
        source.OpenedPidOrder.Clear();

        await conn.EnsureAsync(CancellationToken.None);

        Assert.Empty(source.OpenedPidOrder);
        Assert.Equal(1, conn.ConnectionId);   // no adoption, so no increment
    }

    // the sticky-selection defect, as a test
    [Fact]
    public async Task TheBatteryPathHopsBackToThePreferredMouse()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);
        conn.PreferredModel = "Naga V3 Pro";
        await conn.EnsureAsync(CancellationToken.None);
        Assert.Equal("Naga V2 Pro", conn.Model);

        source.Present.Add(V3Wireless); source.Answering.Add(V3Wireless);   // the V3 wakes up
        await conn.EnsureAsync(CancellationToken.None, reselect: true);

        Assert.Equal("Naga V3 Pro", conn.Model);
        Assert.Equal(2, conn.ConnectionId);
    }

    [Fact]
    public async Task ANonBatteryCallDoesNotHopBack()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);
        conn.PreferredModel = "Naga V3 Pro";
        await conn.EnsureAsync(CancellationToken.None);
        source.Present.Add(V3Wireless); source.Answering.Add(V3Wireless);

        await conn.EnsureAsync(CancellationToken.None);   // reselect defaults to false

        Assert.Equal("Naga V2 Pro", conn.Model);
    }

    [Fact]
    public async Task AFailedHopKeepsTheLiveConnection()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        source.Present.Add(V3Wireless);                  // present but asleep
        var conn = Build(source);
        conn.PreferredModel = "Naga V3 Pro";
        await conn.EnsureAsync(CancellationToken.None);
        var live = source.Handed.Single(c => c.Pid == V2Wireless);

        byte tid = await conn.EnsureAsync(CancellationToken.None, reselect: true);

        Assert.Equal((byte)0x1f, tid);
        Assert.Equal("Naga V2 Pro", conn.Model);
        Assert.False(live.Disposed);      // the live handle survived the failed probe
        Assert.Equal(1, conn.ConnectionId);
    }

    [Fact]
    public async Task AThrowingHopKeepsTheLiveConnection()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);
        conn.PreferredModel = "Naga V3 Pro";
        await conn.EnsureAsync(CancellationToken.None);
        var live = source.Handed.Single(c => c.Pid == V2Wireless);
        source.ThrowOnOpen = new InvalidOperationException("enumeration blew up");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => conn.EnsureAsync(CancellationToken.None, reselect: true));

        Assert.Equal("Naga V2 Pro", conn.Model);
        Assert.Equal((byte)0x1f, conn.TransactionId);
        Assert.False(live.Disposed);      // rolled back, not leaked
    }

    [Fact]
    public async Task ACancelledHopKeepsTheLiveConnection()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = new MouseConnection(source, (c, ct) =>
            c.Pid == V3Wireless ? Task.FromCanceled<byte>(new CancellationToken(true)) : Task.FromResult((byte)0x1f));
        conn.PreferredModel = "Naga V3 Pro";
        await conn.EnsureAsync(CancellationToken.None);   // adopts the V2 while the V3 is absent
        var live = source.Handed.Single(c => c.Pid == V2Wireless);
        source.Present.Add(V3Wireless);                  // only now can the hop be attempted, and cancel

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => conn.EnsureAsync(CancellationToken.None, reselect: true));

        Assert.Equal("Naga V2 Pro", conn.Model);
        Assert.False(live.Disposed);
    }

    [Fact]
    public async Task AWinningHopDisposesExactlyTheLoser()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);
        conn.PreferredModel = "Naga V3 Pro";
        await conn.EnsureAsync(CancellationToken.None);
        var old = source.Handed.Single(c => c.Pid == V2Wireless);
        source.Present.Add(V3Wireless); source.Answering.Add(V3Wireless);

        await conn.EnsureAsync(CancellationToken.None, reselect: true);

        Assert.True(old.Disposed);
        Assert.False(source.Handed.Single(c => c.Pid == V3Wireless).Disposed);
    }

    [Fact]
    public async Task ThePreferredModelIsTriedFirstWiredBeforeWireless()
    {
        var source = new FakeControlCollectionSource();
        foreach (int pid in new[] { V2Wired, V2Wireless, V3Wired, V3Wireless }) source.Present.Add(pid);
        source.Answering.Add(V2Wireless);
        var conn = Build(source);
        conn.PreferredModel = "Naga V2 Pro";

        await conn.EnsureAsync(CancellationToken.None);

        Assert.Equal(new[] { V2Wired, V2Wireless }, source.OpenedPidOrder);
    }

    [Fact]
    public async Task AnUnknownPreferenceBehavesLikeNoPreference()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);
        conn.PreferredModel = "Naga Imaginary";

        Assert.Null(conn.PreferredModel);

        await conn.EnsureAsync(CancellationToken.None);
        source.OpenedPidOrder.Clear();
        await conn.EnsureAsync(CancellationToken.None, reselect: true);

        Assert.Empty(source.OpenedPidOrder);   // no pointless probe on every battery read
    }

    [Fact]
    public async Task DiscoverListsOnlyMiceThatAnswerAndLeavesAWorkingConnection()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V3Wireless);                                  // present but asleep
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);

        var found = await conn.DiscoverAsync(CancellationToken.None);

        Assert.Equal(new[] { "Naga V2 Pro" }, found);
        Assert.Equal("Naga V2 Pro", conn.Model);
        Assert.Equal((byte)0x1f, conn.TransactionId);
    }

    [Fact]
    public async Task CloseDisposesTheLiveCollectionAndForcesARescan()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V3Wireless); source.Answering.Add(V3Wireless);
        var conn = Build(source);
        await conn.EnsureAsync(CancellationToken.None);
        var first = source.Handed.Single();

        conn.Close();
        Assert.True(first.Disposed);
        Assert.Equal((byte)0, conn.TransactionId);

        await conn.EnsureAsync(CancellationToken.None);
        Assert.Equal(2, conn.ConnectionId);   // a reconnect is an adoption, even to the same model
    }
}
```

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~MouseConnectionTests"`
Expected: compile error — `MouseConnection` does not exist.

- [ ] **Step 6: Implement the state machine**

Create `src/NagaBatteryTray/Hid/MouseConnection.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NagaBatteryTray.Hid;

/// <summary>Owns which mouse the app is connected to. Every Win32 call it makes goes through
/// IControlCollection, so the parts that used to be untestable — preference order, when a live
/// connection may be reselected, rollback, disposing exactly the losing handle, and connection
/// identity — are ordinary code with tests (spec 2026-08-19 §4.1-§4.4, §4.10).
/// <para>Protocol stays out of here: the caller supplies <see cref="Verify"/>, which resolves a
/// transaction id on a candidate and confirms it answers. 0 = it does not.</para></summary>
internal sealed class MouseConnection : IDisposable
{
    /// <summary>Resolve + confirm a candidate. Returns the working transaction id, or 0.</summary>
    internal delegate Task<byte> Verify(IControlCollection collection, CancellationToken ct);

    private readonly IControlCollectionSource _source;
    private readonly Verify _verify;
    private IControlCollection? _current;
    private string? _preferred;

    internal MouseConnection(IControlCollectionSource source, Verify verify)
    {
        _source = source;
        _verify = verify;
    }

    /// <summary>The live collection, or null. Callers exchange bytes through it.</summary>
    internal IControlCollection? Current => _current;

    /// <summary>Working transaction id on the live collection; 0 = not connected.</summary>
    internal byte TransactionId { get; private set; }

    /// <summary>True when the live collection is a wired PID.</summary>
    internal bool Wired { get; private set; }

    /// <summary>Display name of the connected model, or null. Deliberately NOT cleared by
    /// <see cref="Close"/>: after a dropped handle it is still the best answer to "which mouse was
    /// this", which the write path compares against (spec §4.10).</summary>
    internal string? Model { get; private set; }

    /// <summary>Increases on every adoption, including a reconnect to the same model.</summary>
    internal int ConnectionId { get; private set; }

    /// <summary>Preferred model, normalized on set: a name matching no supported mouse must behave
    /// EXACTLY like no preference, or every battery read detaches and rolls back chasing a mouse
    /// that cannot exist.</summary>
    internal string? PreferredModel
    {
        get => _preferred;
        set => _preferred = RazerProtocol.NormalizeModelName(value);
    }

    /// <summary>Ensure a collection that actually answers, and return its transaction id (0 = none
    /// reachable). reselect is passed ONLY by the battery path: it is what lets the app hop back to
    /// the preferred mouse, and what every other call must not do (spec §4.3).</summary>
    internal async Task<byte> EnsureAsync(CancellationToken ct, bool reselect = false)
    {
        switch (ConnectionPolicy.Decide(_current is not null && TransactionId != 0, Model, _preferred, reselect))
        {
            case ConnectionDecision.KeepLive:
                return TransactionId;
            case ConnectionDecision.ProbePreferred:
                return await AdoptPreferredAsync(ct);
            default:
                Close();
                return await ConnectAsync(RazerProtocol.ScanPidsFor(_preferred), ct);
        }
    }

    /// <summary>Which supported mice answer right now, in SupportedMice order, leaving a normal
    /// connection behind (spec §4.4).</summary>
    internal async Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct)
    {
        var reachable = new List<string>();
        Close();
        foreach (var m in RazerProtocol.SupportedMice)
        {
            if (await ConnectAsync(new[] { m.WiredPid, m.WirelessPid }, ct) != 0) reachable.Add(m.DisplayName);
            Close();
        }
        await EnsureAsync(ct);
        return reachable;
    }

    /// <summary>Drop the live collection so the next EnsureAsync re-selects.</summary>
    internal void Close()
    {
        _current?.Dispose();
        _current = null;
        TransactionId = 0;
    }

    public void Dispose() => Close();

    /// <summary>Running on a non-preferred mouse: probe ONLY the preferred model. Adopt it if it
    /// answers, otherwise put the live connection back untouched — a sleeping preference must never
    /// cost the user the mouse that is actually awake.
    /// <para>The rollback is a finally, not an if: an exception or a cancellation between the detach
    /// and the adoption would otherwise leave the app with no connection AND leak the detached
    /// collection.</para></summary>
    private async Task<byte> AdoptPreferredAsync(CancellationToken ct)
    {
        var live = _current;
        byte liveTid = TransactionId;
        bool liveWired = Wired;
        string? liveModel = Model;
        bool adopted = false;

        _current = null;   // detach so ConnectAsync's failure path cannot dispose the live collection
        TransactionId = 0;
        try
        {
            byte tid = await ConnectAsync(RazerProtocol.PidsForModel(_preferred), ct);
            adopted = tid != 0;
            return adopted ? tid : liveTid;
        }
        finally
        {
            if (adopted)
            {
                live?.Dispose();   // the loser, and only the loser
            }
            else
            {
                Close();           // drop anything a partial attempt left behind
                _current = live;
                TransactionId = liveTid;
                Wired = liveWired;
                Model = liveModel;
            }
        }
    }

    /// <summary>Open and verify the first candidate among <paramref name="pids"/> that answers.
    /// 0 = none did, and nothing is left open. The verify step is essential: the wireless receiver
    /// stays enumerated when the mouse switches to wired, so picking by enumeration order alone
    /// would lock onto a collection that can no longer reach the mouse.</summary>
    private async Task<byte> ConnectAsync(int[] pids, CancellationToken ct)
    {
        foreach (var candidate in _source.Open(pids))
        {
            byte tid;
            try { tid = await _verify(candidate, ct); }
            catch { candidate.Dispose(); throw; }

            if (tid != 0)
            {
                _current = candidate;
                TransactionId = tid;
                Wired = RazerProtocol.IsWiredPid(candidate.Pid);
                Model = RazerProtocol.ModelNameForPid(candidate.Pid);
                ConnectionId++;
                return tid;
            }
            candidate.Dispose();
        }
        return 0;
    }
}
```

- [ ] **Step 7: Run the state-machine tests**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~MouseConnectionTests"`
Expected: 13 passing.

`InternalsVisibleTo.cs` already exposes internals to the test assembly (hard rule 6) — do not change it.

- [ ] **Step 8: Commit**

```bash
git add src/NagaBatteryTray/Hid tests/NagaBatteryTray.Tests
git commit -m "feat(hid): transport seam + tested connection state machine"
```

---

## Task 3b: RazerDevice adopts the connection

**Files:**
- Modify: `src/NagaBatteryTray/Hid/IRazerDevice.cs`
- Modify: `src/NagaBatteryTray/Hid/RazerDevice.cs`
- Modify: `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs`

**Interfaces:**
- Consumes: `MouseConnection`, `IControlCollection` (Task 3).
- Produces on `IRazerDevice`: `string? PreferredModel { get; set; }`, `Task<IReadOnlyList<string>> DiscoverReachableAsync(CancellationToken ct)`, `int ConnectionId { get; }`. Tasks 4, 4b and 6 consume them.

`RazerDevice` keeps every byte of protocol behaviour and loses all of its handle bookkeeping.

- [ ] **Step 1: Extend the interface**

In `IRazerDevice.cs`, add above `void Reset();`:

```csharp
    /// <summary>Which model the app should talk to, as a RazerProtocol.SupportedMice display name.
    /// Null = scan order. Only the battery path (ReadAsync) acts on a change, so setting this never
    /// stalls a button or DPI call (spec 2026-08-19 §4.3).</summary>
    string? PreferredModel { get; set; }

    /// <summary>Display names of the supported mice that actually answer right now, in
    /// SupportedMice order. Costs one probe per model, so callers use it on explicit user actions
    /// only — never on a timer. Leaves the connection on the preferred/first reachable mouse.</summary>
    Task<IReadOnlyList<string>> DiscoverReachableAsync(CancellationToken ct);

    /// <summary>Increases every time a collection is adopted, including a reconnect to the same
    /// model. The app's only reliable "am I still talking to the same mouse": a non-battery call can
    /// reconnect onto a different mouse after a dropped handle, and nothing publishes that
    /// (spec §4.10).</summary>
    int ConnectionId { get; }
```

- [ ] **Step 2: Replace the handle fields with the connection**

In `RazerDevice.cs`, delete the `_handle`, `_tid`, `_wired`, `_model` fields, the `CloseHandle` method, the `CreateFile` / `HidD_SetFeature` / `HidD_GetFeature` declarations and the `FileShareReadWrite` / `OpenExisting` constants (they now live in `HidControlCollection.cs`), and `FindControlPaths`. Add:

```csharp
    private readonly MouseConnection _conn;

    public RazerDevice(ISettingsStore settings)
        : this(settings, new HidControlCollectionSource()) { }

    internal RazerDevice(ISettingsStore settings, IControlCollectionSource source)
    {
        _settings = settings;
        _conn = new MouseConnection(source, VerifyCollectionAsync);
    }

    public string? PreferredModel
    {
        get => _conn.PreferredModel;
        set => _conn.PreferredModel = value;
    }

    public int ConnectionId => _conn.ConnectionId;

    /// <summary>MouseConnection's protocol hook: resolve a transaction id on a candidate and confirm
    /// it answers a battery query. Runs against the candidate directly, because it is not the live
    /// collection yet.</summary>
    private async Task<byte> VerifyCollectionAsync(IControlCollection candidate, CancellationToken ct)
    {
        byte tid = await ResolveTransactionIdAsync(candidate, ct);
        if (tid == 0) return 0;
        return await QueryAsync(candidate, tid, RazerProtocol.CommandIdBattery, ct) is not null ? tid : (byte)0;
    }
```

- [ ] **Step 3: Point the exchanges at a collection**

`ExchangeAsync`, `FastReadAsync`, `QueryAsync` and `ResolveTransactionIdAsync` take an `IControlCollection` instead of reading the field, and their bodies change only in how the bytes are handed over:

- `if (_handle is null || _handle.IsInvalid) return null;` becomes `if (!collection.IsOpen) return null;` — the same guard, asked of the collection instead of the handle. Dropping it entirely would change behaviour: today a failed HID call nulls the handle and every later exchange returns immediately, so a dead collection receives no further Win32 calls.
- `HidD_SetFeature(_handle, request, request.Length)` becomes `collection.SetFeature(request)`.
- `HidD_GetFeature(_handle, reply, reply.Length)` becomes `collection.GetFeature(reply)`.
- `CloseHandle()` on a failed call becomes `_conn.Close()`.

`ResolveTransactionIdAsync` needs the same stop, and it is the one place where losing it would bite: it probes the transaction-id set in a loop, so on a dead collection it would keep calling into Windows and could accept a candidate after an intermittent failure. Add the check to its loop:

```csharp
        foreach (byte tid in RazerProtocol.TransactionIdProbeSet)
        {
            if (!collection.IsOpen) return 0;   // transport is gone; this candidate is finished
            var value = await QueryAsync(collection, tid, RazerProtocol.CommandIdBattery, ct);
            ...
        }
```

Every public method gains the same two lines at the top of its `try`, replacing `byte tid = await EnsureConnectedAsync(ct);`:

```csharp
            byte tid = await _conn.EnsureAsync(ct);          // ReadAsync passes reselect: true
            if (tid == 0 || _conn.Current is not { } c) return <the method's failure value>;
```

and then pass `c` to the exchange. Delays, the busy retry, the fast-read ladder, the echo checks and every parse stay byte-for-byte as they are.

- [ ] **Step 4: The battery path is the one reselect trigger**

In `ReadAsync` only:

```csharp
            byte tid = await _conn.EnsureAsync(ct, reselect: true);
```

Every other method keeps the default. Be precise about what that buys: those calls never **reselect a live connection**, but they can still **reconnect** a dropped one, and that reconnect uses preference order, so a button read mid-sweep can land on a different mouse with no battery read to announce it. `ConnectionId` is what makes that detectable; Tasks 4b and 6 act on it.

Also make the write refuse a connection that moved. In `SetButtonAsync`:

```csharp
            string? before = _conn.Model;
            byte tid = await _conn.EnsureAsync(ct);
            if (tid == 0 || _conn.Current is not { } c) return false;
            // a reconnect inside this call may have landed on ANOTHER mouse, and the caller's profile
            // number was computed for the previous one. Refuse rather than write it there. Close()
            // leaves Model set, so a dropped handle still leaves a name to compare against.
            if (before is not null && !string.Equals(before, _conn.Model, StringComparison.OrdinalIgnoreCase))
                return false;
```

That is the structural belt. Task 4b's always-resolve rule is the half that carries automated tests.

- [ ] **Step 5: The remaining public members**

```csharp
    public Task<IReadOnlyList<string>> DiscoverReachableAsync(CancellationToken ct) => _conn.DiscoverAsync(ct);

    public void Reset() => _conn.Close();

    public void Dispose() => _conn.Dispose();
```

`ReadAsync` keeps reporting `_conn.Wired` and `_conn.Model` in its `BatteryReading`.

- [ ] **Step 6: Extend the fake**

In `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs`, add before `Reset()`:

```csharp
    public string? PreferredModel { get; set; }

    /// <summary>What DiscoverReachableAsync returns; tests set it per scenario.</summary>
    public List<string> Reachable { get; } = new();
    public int DiscoverCount { get; private set; }

    public Task<IReadOnlyList<string>> DiscoverReachableAsync(CancellationToken ct)
    {
        DiscoverCount++;
        return Task.FromResult<IReadOnlyList<string>>(Reachable.ToList());
    }

    /// <summary>Settable so a test can simulate the mouse changing underneath an operation.</summary>
    public int ConnectionId { get; set; }
```

and, in `GetButtonAsync`, as the first line of the method body:

```csharp
        BeforeGetButton?.Invoke();
```

with, beside `GetButtonCount`:

```csharp
    /// <summary>Runs just before a button read — lets a test simulate the connection changing
    /// in the middle of a multi-step operation.</summary>
    public Action? BeforeGetButton { get; set; }
```

- [ ] **Step 7: Test the transport-loss path**

This test only became possible with the seam, and it pins the one behaviour the move could quietly
change. Create `tests/NagaBatteryTray.Tests/RazerDeviceTransportTests.cs`:

```csharp
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;
using Xunit;

public sealed class RazerDeviceTransportTests
{
    private static JsonSettingsStore FreshStore() =>   // no cached transaction id: probing must run
        new(Path.Combine(Path.GetTempPath(), $"naga-{Guid.NewGuid():N}.json"));

    [Fact]
    public async Task ADeadCollectionIsNotProbedFurther()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(RazerProtocol.MousePidV3Wireless);
        source.FeatureFailing.Add(RazerProtocol.MousePidV3Wireless);
        using var device = new RazerDevice(FreshStore(), source);

        var reading = await device.ReadAsync(CancellationToken.None);

        Assert.False(reading.IsPresent);
        var dead = source.Handed.Single();
        Assert.Equal(1, dead.FeatureCalls);   // one failed call, then no more Win32 on a dead handle
        Assert.True(dead.Disposed);
    }

    [Fact]
    public async Task AWorkingCollectionIsReportedWithItsModelAndLink()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(RazerProtocol.MousePidV3Wired);
        source.Answering.Add(RazerProtocol.MousePidV3Wired);
        using var device = new RazerDevice(FreshStore(), source);

        var reading = await device.ReadAsync(CancellationToken.None);

        Assert.True(reading.IsPresent);
        Assert.Equal("Naga V3 Pro", reading.Model);
        Assert.True(reading.IsWired);
        Assert.Equal(1, device.ConnectionId);
    }

    [Fact]
    public async Task ThePreferredMouseIsAdoptedThroughTheRealDevice()
    {
        var source = new FakeControlCollectionSource();
        foreach (int pid in new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidV3Wireless })
        {
            source.Present.Add(pid);
            source.Answering.Add(pid);
        }
        using var device = new RazerDevice(FreshStore(), source) { PreferredModel = "Naga V2 Pro" };

        var reading = await device.ReadAsync(CancellationToken.None);

        Assert.Equal("Naga V2 Pro", reading.Model);
    }
}
```

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~RazerDeviceTransportTests"`
Expected: 3 passing. `Answers` is what makes this work: an answering fake collection echoes the
request back as a completed reply with a valid CRC, because the production parsers reject a
zero-filled buffer and no candidate could ever be adopted.

- [ ] **Step 8: Run the whole suite**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: all green.

- [ ] **Step 9: Verify on hardware before going further**

This task rewrote the transport's plumbing, so prove it against a real mouse before any UI work builds on it. With the V3 connected, run the probe suite from a fresh build and confirm each still reports what it did before:

```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build
```

then run `--probe`, `--probe-dpi`, `--probe-buttons` and `--probe-profile` against the built binary. A difference in any of them means the plumbing move changed behaviour and must be fixed here, not later.

- [ ] **Step 10: Commit**

```bash
git add src/NagaBatteryTray/Hid tests/NagaBatteryTray.Tests
git commit -m "refactor(hid): RazerDevice drives MouseConnection; protocol unchanged"
```

---

## Task 3c: Settings — preference, per-mouse profile names, migration

**Files:**
- Modify: `src/NagaBatteryTray/Settings/AppSettings.cs`
- Modify: `src/NagaBatteryTray/Settings/JsonSettingsStore.cs`
- Test: `tests/NagaBatteryTray.Tests/SettingsMigrationTests.cs` (create)

**Interfaces:**
- Consumes: nothing. Placed after the restructure because the product owner gated everything behind it; it has no dependency on Tasks 2, 3 or 3b.
- Produces: `AppSettings.PreferredMouse` (`string?`), `AppSettings.ProfileNamesByMouse` (`Dictionary<string, Dictionary<byte, string>>`). Later tasks read both.

- [ ] **Step 1: Write the failing tests**

Create `tests/NagaBatteryTray.Tests/SettingsMigrationTests.cs`:

```csharp
using System.Text.Json;
using NagaBatteryTray.Settings;
using Xunit;

public sealed class SettingsMigrationTests
{
    private static string WriteTemp(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"naga-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void LegacyProfileNamesMigrateToTheV2()
    {
        string path = WriteTemp("""{"ProfileNames":{"1":"World of Warcraft"}}""");
        var store = new JsonSettingsStore(path);

        Assert.Equal("World of Warcraft", store.Settings.ProfileNamesByMouse["Naga V2 Pro"][1]);
        Assert.Empty(store.Settings.ProfileNames);
    }

    [Fact]
    public void MigrationDoesNotOverwriteExistingPerMouseNames()
    {
        string path = WriteTemp("""
            {"ProfileNames":{"1":"Legacy"},"ProfileNamesByMouse":{"Naga V3 Pro":{"1":"Kept"}}}
            """);
        var store = new JsonSettingsStore(path);

        Assert.Equal("Kept", store.Settings.ProfileNamesByMouse["Naga V3 Pro"][1]);
        Assert.False(store.Settings.ProfileNamesByMouse.ContainsKey("Naga V2 Pro"));
    }

    [Fact]
    public void PreferredMouseDefaultsToNullAndRoundTrips()
    {
        string path = WriteTemp("{}");
        var store = new JsonSettingsStore(path);
        Assert.Null(store.Settings.PreferredMouse);

        store.Settings.PreferredMouse = "Naga V3 Pro";
        store.Save();

        var reloaded = new JsonSettingsStore(path);
        Assert.Equal("Naga V3 Pro", reloaded.Settings.PreferredMouse);
    }

    [Fact]
    public void PerMouseNamesRoundTrip()
    {
        string path = WriteTemp("{}");
        var store = new JsonSettingsStore(path);
        store.Settings.ProfileNamesByMouse["Naga V3 Pro"] = new Dictionary<byte, string> { [2] = "Raid" };
        store.Save();

        var reloaded = new JsonSettingsStore(path);
        Assert.Equal("Raid", reloaded.Settings.ProfileNamesByMouse["Naga V3 Pro"][2]);
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~SettingsMigrationTests"`
Expected: compile error — `ProfileNamesByMouse` and `PreferredMouse` do not exist.

- [ ] **Step 3: Add the settings fields**

In `AppSettings.cs`, replace the `ProfileNames` block with:

```csharp
    /// <summary>Retired (2026-08-19): profile names are per-mouse now (ProfileNamesByMouse) — a slot
    /// number means a different slot on a different model. Kept only so older settings.json files
    /// round-trip; migrated once at load, then never read or written.</summary>
    public Dictionary<byte, string> ProfileNames { get; set; } = new();

    /// <summary>App-side labels for onboard profile slots, keyed by mouse display name
    /// (RazerProtocol.SupportedMice) then slot number (1..5). The firmware stores no names — a slot
    /// is a number + LED colour — so these exist only in the dashboard's Profile card.</summary>
    public Dictionary<string, Dictionary<byte, string>> ProfileNamesByMouse { get; set; } = new();

    /// <summary>Which mouse the app should talk to, as a RazerProtocol.SupportedMice display name.
    /// Null (the default) means "use scan order", which already prefers the newest model. An unknown
    /// name behaves as null (spec 2026-08-19 §4.1).</summary>
    public string? PreferredMouse { get; set; } = null;
```

- [ ] **Step 4: Add the migration**

In `JsonSettingsStore.cs`, add the call at the end of both constructor branches, then the method. The constructor becomes:

```csharp
    public JsonSettingsStore(string path)
    {
        _path = path;
        if (File.Exists(_path))
        {
            try
            {
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
            }
            catch
            {
                Settings = new AppSettings(); // corrupt file -> defaults
            }
            MigrateProfileNames();
        }
        else
        {
            Settings = new AppSettings();
            Save();
        }
    }

    /// <summary>One-time move of slot-keyed profile names under the only mouse that could have
    /// written them. Per-mouse names ship with the 2026-08-19 selector change, so a file carrying
    /// legacy names predates dual support and belongs to the V2 Pro (spec §4.9).</summary>
    private void MigrateProfileNames()
    {
        if (Settings.ProfileNames.Count == 0 || Settings.ProfileNamesByMouse.Count > 0) return;
        Settings.ProfileNamesByMouse["Naga V2 Pro"] = new Dictionary<byte, string>(Settings.ProfileNames);
        Settings.ProfileNames.Clear();
        Save();
    }
```

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~SettingsMigrationTests"`
Expected: 4 passing.

- [ ] **Step 6: Update the existing test the migration invalidates**

`tests/NagaBatteryTray.Tests/SettingsStoreTests.cs:144-157` asserts that legacy `ProfileNames`
survive a save and reload. They no longer do — the migration is what moves them — so the test now
asserts the migration instead. Replace it with:

```csharp
    [Fact]
    public void ProfileNames_migrate_to_the_V2_on_reload()
    {
        var path = TempFile();
        var store = new JsonSettingsStore(path);
        store.Settings.ProfileNames[2] = "Work";
        store.Settings.ProfileNames[5] = "MMO night";
        store.Save();

        var reloaded = new JsonSettingsStore(path);

        // slot numbers mean different slots on different mice, so a legacy name belongs to the only
        // mouse that could have written it (spec §4.9)
        Assert.Empty(reloaded.Settings.ProfileNames);
        Assert.Equal("Work", reloaded.Settings.ProfileNamesByMouse["Naga V2 Pro"][2]);
        Assert.Equal("MMO night", reloaded.Settings.ProfileNamesByMouse["Naga V2 Pro"][5]);
    }
```

- [ ] **Step 7: Run the whole suite**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: all green. `DashboardViewModel` still compiles because it reads `source.ProfileNames`, which still exists (Task 7 moves it).

- [ ] **Step 8: Commit**

```bash
git add src/NagaBatteryTray/Settings tests/NagaBatteryTray.Tests
git commit -m "feat(settings): preferred mouse + per-mouse profile names with one-time migration"
```

---

## Task 4: BatteryMonitor pass-throughs

**Files:**
- Modify: `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs`
- Test: `tests/NagaBatteryTray.Tests/BatteryMonitorSelectionTests.cs` (create)

**Interfaces:**
- Consumes: `IRazerDevice.PreferredModel`, `IRazerDevice.DiscoverReachableAsync` (Task 3).
- Produces: `BatteryMonitor.PreferredMouse` (`string?` property) and `BatteryMonitor.DiscoverMiceAsync()` returning `Task<IReadOnlyList<string>>`. Task 7 consumes both.

- [ ] **Step 1: Write the failing tests**

Create `tests/NagaBatteryTray.Tests/BatteryMonitorSelectionTests.cs`:

```csharp
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using Xunit;

public sealed class BatteryMonitorSelectionTests
{
    private static BatteryMonitor Build(FakeRazerDevice device) =>
        new(device, new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"naga-{Guid.NewGuid():N}.json")),
            a => a());

    [Fact]
    public void PreferredMouseReachesTheDevice()
    {
        var device = new FakeRazerDevice();
        var monitor = Build(device);

        monitor.PreferredMouse = "Naga V3 Pro";

        Assert.Equal("Naga V3 Pro", device.PreferredModel);
        Assert.Equal("Naga V3 Pro", monitor.PreferredMouse);
    }

    [Fact]
    public async Task DiscoverMiceReturnsWhatTheDeviceFound()
    {
        var device = new FakeRazerDevice();
        device.Reachable.Add("Naga V3 Pro");
        var monitor = Build(device);

        var found = await monitor.DiscoverMiceAsync();

        Assert.Equal(new[] { "Naga V3 Pro" }, found);
        Assert.Equal(1, device.DiscoverCount);
    }

    [Fact]
    public async Task DiscoverMiceRepublishesTheConnectedModel()
    {
        var device = new FakeRazerDevice();
        device.Reachable.Add("Naga V3 Pro");
        device.Enqueue(new BatteryReading(70, false, true, DateTimeOffset.Now, false, "Naga V3 Pro"));
        var monitor = Build(device);

        await monitor.DiscoverMiceAsync();

        Assert.Equal("Naga V3 Pro", monitor.State.Model);
    }

    [Fact]
    public async Task AnExplicitRefreshWaitsInsteadOfSkipping()
    {
        var device = new FakeRazerDevice();
        device.Enqueue(new BatteryReading(55, false, true, DateTimeOffset.Now, false, "Naga V2 Pro"));
        var monitor = Build(device);

        await monitor.RefreshNowWaitingAsync();

        Assert.Equal("Naga V2 Pro", monitor.State.Model);
    }

    [Fact]
    public async Task DiscoverMiceReturnsEmptyAfterDispose()
    {
        var device = new FakeRazerDevice();
        device.Reachable.Add("Naga V3 Pro");
        var monitor = Build(device);
        monitor.Dispose();

        Assert.Empty(await monitor.DiscoverMiceAsync());
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~BatteryMonitorSelectionTests"`
Expected: compile error — `PreferredMouse` and `DiscoverMiceAsync` do not exist.

- [ ] **Step 3: Implement**

Add to `BatteryMonitor.cs`, beside the other pass-throughs:

```csharp
    /// <summary>Which mouse the app should talk to (spec 2026-08-19 §4.1). Set-only plumbing: the
    /// device acts on it at its next battery read, so this never blocks and never takes the lock.</summary>
    public string? PreferredMouse
    {
        get => _device.PreferredModel;
        set => _device.PreferredModel = value;
    }

    /// <summary>Which mouse the app is talking to right now, as an identity that changes on every
    /// adoption (spec §4.10). Callers capture it before a multi-step operation and discard results
    /// whose id no longer matches.</summary>
    public int ConnectionId => _device.ConnectionId;

    /// <summary>Which supported mice answer right now, for the dashboard's selector. Blocks for the
    /// read lock like every other pass-through — this is several device probes, so it must not
    /// interleave with the battery poll. Called on dashboard open / refresh only, never polled.
    /// <para>Republishes State before returning: discovery reconnects at the end and can land on a
    /// different mouse than State names, and State only ever updates from a battery read. Without
    /// this the selector and the profile-name key describe the wrong mouse (spec §4.4).</para></summary>
    public async Task<IReadOnlyList<string>> DiscoverMiceAsync()
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return Array.Empty<string>(); }
        try
        {
            var found = await _device.DiscoverReachableAsync(_cts.Token);
            ProcessReading(await _device.ReadAsync(_cts.Token)); // same acquisition: no one can slip between
            return found;
        }
        catch (OperationCanceledException) { return Array.Empty<string>(); }
        finally { _readLock.Release(); }
    }

    /// <summary>Refresh behind an EXPLICIT user action (a mouse pick). Identical to RefreshNowAsync
    /// except that it WAITS for the read lock instead of skipping when one is busy — the skip is
    /// right for a timer tick and wrong for a user, who would see a perfectly reachable mouse
    /// reported as unreachable because a sweep happened to be in flight (spec §4.5).</summary>
    public Task RefreshNowWaitingAsync() => PollAsync(reconnect: true, waitForLock: true);
```

and change `PollAsync`'s acquisition (`src/NagaBatteryTray/Monitoring/BatteryMonitor.cs:124`):

```csharp
    private async Task PollAsync(bool reconnect = false, bool waitForLock = false)
    {
        if (waitForLock)
        {
            try { await _readLock.WaitAsync(_cts.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
        }
        else if (!await _readLock.WaitAsync(0)) return; // a read is already in flight; skip
```

The rest of `PollAsync` is unchanged.

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~BatteryMonitorSelectionTests"`
Expected: 5 passing.

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Monitoring/BatteryMonitor.cs tests/NagaBatteryTray.Tests/BatteryMonitorSelectionTests.cs
git commit -m "feat(monitoring): serialize mouse preference and discovery through the read lock"
```

---

## Task 4b: The atomic button write

**Files:**
- Modify: `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs`
- Modify: `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs`
- Test: `tests/NagaBatteryTray.Tests/AtomicButtonWriteTests.cs` (create)

**Interfaces:**
- Consumes: `BatteryMonitor.ConnectionId` (Task 4).
- Produces: `ButtonWriteResult(bool Verified, byte? Slot, bool ConnectionChanged)` and `BatteryMonitor.WriteButtonVerifiedAsync(byte[]? createdSlots, byte buttonId, byte category, byte[] data)`. Task 6 consumes both.

**Why this task exists.** Today `WriteVerifiedAsync` takes the read lock three separate times — once to resolve the slot, once to write, once to verify (`src/NagaBatteryTray/AppHost.cs:318-327`). A battery-path reselect can land between the resolve and the write, and then the app writes one mouse's slot number onto another. Clearing a cache cannot undo that: the slot is already a local variable. The sequence has to be atomic.

- [ ] **Step 1: Write the failing tests**

Create `tests/NagaBatteryTray.Tests/AtomicButtonWriteTests.cs`:

```csharp
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using Xunit;

public sealed class AtomicButtonWriteTests
{
    private static BatteryMonitor Build(FakeRazerDevice device) =>
        new(device, new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"naga-{Guid.NewGuid():N}.json")),
            a => a());

    private static readonly byte[] F1 = { 0x00, 0x3a };

    [Fact]
    public async Task TheSlotComesFromTheMouseBeingWrittenTo()
    {
        var device = new FakeRazerDevice { ActiveProfile = 2 };
        device.ButtonActions[(2, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, F1);
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.True(r.Verified);
        Assert.Equal((byte)2, r.Slot);
        Assert.Single(device.ButtonWrites);
        Assert.Equal((byte)2, device.ButtonWrites[0].Profile);
    }

    [Fact]
    public async Task AnUnreadableActiveSlotWritesNothing()
    {
        var device = new FakeRazerDevice { ActiveProfile = null };
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.False(r.Verified);
        Assert.Empty(device.ButtonWrites);   // never guess a slot we could not read
    }

    [Fact]
    public async Task ASlotTheMouseDoesNotHaveIsNeverWritten()
    {
        var device = new FakeRazerDevice { ActiveProfile = 3 };
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(new byte[] { 1 }, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.False(r.Verified);
        Assert.Empty(device.ButtonWrites);   // refused before any write
    }

    [Fact]
    public async Task AReadBackThatDoesNotMatchFailsTheWrite()
    {
        var device = new FakeRazerDevice { ActiveProfile = 1 };
        device.ButtonActions[(1, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, new byte[] { 0x00 });
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.False(r.Verified);
    }

    [Fact]
    public async Task AMouseChangeDuringTheOperationIsReported()
    {
        var device = new FakeRazerDevice { ActiveProfile = 1 };
        device.ButtonActions[(1, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, F1);
        device.BeforeGetButton = () => device.ConnectionId++;   // reconnect landed mid-operation
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.True(r.ConnectionChanged);
        Assert.False(r.Verified);   // a changed connection is never a verified write
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~AtomicButtonWriteTests"`
Expected: compile error — `WriteButtonVerifiedAsync` and `BeforeGetButton` do not exist.

- [ ] **Step 3: Check the fake's hook is already there**

`BeforeGetButton` and its call in `GetButtonAsync` were added in Task 3b Step 6. Confirm they are
present and add nothing — applying that step again duplicates the invocation.

- [ ] **Step 4: Implement the atomic write**

Add to `BatteryMonitor.cs`:

```csharp
    /// <summary>Outcome of one atomic button write. Slot is the slot actually used (null = it could
    /// not be established). ConnectionChanged = the mouse underneath changed mid-operation, so the
    /// caller must re-seed rather than trust anything it cached.</summary>
    public readonly record struct ButtonWriteResult(bool Verified, byte? Slot, bool ConnectionChanged);

    /// <summary>Read the active slot, write, and read back to verify - all inside ONE read-lock
    /// acquisition. Split across three acquisitions (as it was before 2026-08-19) a reselect can land
    /// between the resolve and the write, and one mouse's slot number gets written to another;
    /// clearing a cache cannot undo that, because the slot is already a local (spec 4.10).
    /// <para>The slot is ALWAYS read here rather than taken from the caller's cache. That is what makes
    /// the target belong to the mouse being written to: the read forces any pending reconnect to happen
    /// BEFORE a slot is committed to, at the cost of one extra device read per write. A failed read
    /// fails the write - writing a cached slot to a mouse we could not interrogate is the whole
    /// defect.</para>
    /// <para>createdSlots null = inventory unknown, which never blocks; a known inventory that lacks
    /// the slot refuses before any write.</para></summary>
    public async Task<ButtonWriteResult> WriteButtonVerifiedAsync(
        byte[]? createdSlots, byte buttonId, byte category, byte[] data)
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return new(false, null, false); }
        try
        {
            // this read also forces any pending reconnect, so the id captured after it is the
            // connection the write will actually use
            if (await _device.GetActiveProfileAsync(_cts.Token) is not { } slot) return new(false, null, false);
            int connection = _device.ConnectionId;
            if (createdSlots is not null && Array.IndexOf(createdSlots, slot) < 0) return new(false, slot, false);

            bool ok = await _device.SetButtonAsync(slot, buttonId, category, data, _cts.Token);
            if (ok)
            {
                var readBack = await _device.GetButtonAsync(slot, buttonId, _cts.Token);
                ok = readBack is { } r && r.Category == category && r.Data.AsSpan().SequenceEqual(data);
            }
            bool changed = _device.ConnectionId != connection;
            return new(ok && !changed, slot, changed);
        }
        catch (OperationCanceledException) { return new(false, null, false); }
        finally { _readLock.Release(); }
    }
```

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~AtomicButtonWriteTests"`
Expected: 5 passing.

- [ ] **Step 6: Commit**

```bash
git add src/NagaBatteryTray/Monitoring/BatteryMonitor.cs tests/NagaBatteryTray.Tests
git commit -m "feat(monitoring): atomic resolve-write-verify so a reselect cannot cross mice"
```

---

## Task 5: DeviceScopedState — the per-mouse cache that clears itself

**Files:**
- Create: `src/NagaBatteryTray/Monitoring/DeviceScopedState.cs`
- Test: `tests/NagaBatteryTray.Tests/DeviceScopedStateTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `DeviceScopedState` with `byte? ActiveSlot`, `byte[]? CreatedSlots`, `bool Observe(string? model)`, `void Clear()`, `bool IsBlocked(byte slot)`. Task 6 wires it into `AppHost`.

This is the fix for the false "Not applied" failures. It lives in its own class so the rule is testable — `AppHost` itself is WPF-bound and outside the unit suite.

- [ ] **Step 1: Write the failing tests**

Create `tests/NagaBatteryTray.Tests/DeviceScopedStateTests.cs`:

```csharp
using NagaBatteryTray.Monitoring;
using Xunit;

public sealed class DeviceScopedStateTests
{
    [Fact]
    public void FirstObservationIsNotAChange()
    {
        var s = new DeviceScopedState();
        Assert.False(s.Observe("Naga V3 Pro"));
    }

    [Fact]
    public void SameModelObservedAgainKeepsTheCache()
    {
        var s = new DeviceScopedState();
        s.Observe("Naga V3 Pro");
        s.ActiveSlot = 1;
        s.CreatedSlots = new byte[] { 1 };

        Assert.False(s.Observe("Naga V3 Pro"));
        Assert.Equal((byte)1, s.ActiveSlot);
    }

    [Fact]
    public void ADifferentModelClearsEverything()
    {
        var s = new DeviceScopedState();
        s.Observe("Naga V2 Pro");
        s.ActiveSlot = 3;              // the V2's active slot
        s.CreatedSlots = new byte[] { 1, 2, 3 };

        Assert.True(s.Observe("Naga V3 Pro"));
        Assert.Null(s.ActiveSlot);     // the V3 has no slot 3 — this is the whole defect
        Assert.Null(s.CreatedSlots);
    }

    [Fact]
    public void GoingOfflineAndBackClearsToo()
    {
        var s = new DeviceScopedState();
        s.Observe("Naga V2 Pro");
        s.ActiveSlot = 3;

        Assert.True(s.Observe(null));
        Assert.Null(s.ActiveSlot);
    }

    [Fact]
    public void UnknownInventoryNeverBlocksAWrite()
    {
        var s = new DeviceScopedState();
        s.Observe("Naga V3 Pro");
        Assert.False(s.IsBlocked(1));
        Assert.False(s.IsBlocked(3));
    }

    [Fact]
    public void KnownInventoryBlocksASlotTheMouseDoesNotHave()
    {
        var s = new DeviceScopedState();
        s.Observe("Naga V3 Pro");
        s.CreatedSlots = new byte[] { 1 };

        Assert.False(s.IsBlocked(1));
        Assert.True(s.IsBlocked(3));
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~DeviceScopedStateTests"`
Expected: compile error — `DeviceScopedState` does not exist.

- [ ] **Step 3: Implement**

Create `src/NagaBatteryTray/Monitoring/DeviceScopedState.cs`:

```csharp
namespace NagaBatteryTray.Monitoring;

/// <summary>Everything the app caches about ONE mouse. Feeding it the connected model on every
/// state change makes a model swap clear the cache, which is the fix for the 2026-08-19
/// acceptance defects: the V2's active slot 3 does not exist on a V3, and a button write aimed
/// there reads back the firmware's 1-byte stub, so it can never verify and reports "Not applied"
/// on a write the user watched succeed elsewhere (spec 2026-08-19 §1.1, §4.6).</summary>
public sealed class DeviceScopedState
{
    private string? _model;
    private bool _seen;

    /// <summary>Last active-slot read (0x05/0x84). Null = unknown, resolve on demand.</summary>
    public byte? ActiveSlot { get; set; }

    /// <summary>Slots the connected mouse has actually created (0x05/0x81). Null = not read yet.</summary>
    public byte[]? CreatedSlots { get; set; }

    /// <summary>Feed every observed model name, including null when the mouse goes away. True = the
    /// mouse changed and the cache was dropped, so the caller must supersede in-flight work and
    /// re-seed anything on screen.</summary>
    public bool Observe(string? model)
    {
        if (_seen && string.Equals(_model, model, StringComparison.Ordinal)) return false;
        bool changed = _seen;
        _seen = true;
        _model = model;
        Clear();
        return changed;
    }

    public void Clear()
    {
        ActiveSlot = null;
        CreatedSlots = null;
    }

    /// <summary>True when the inventory is KNOWN and the slot is absent from it — the one case where
    /// a write must be refused rather than aimed into nothing. An unknown inventory never blocks:
    /// the dashboard resolves the active slot on demand before its first profile read, and that path
    /// has to keep working (spec §4.7).</summary>
    public bool IsBlocked(byte slot) => CreatedSlots is { } s && Array.IndexOf(s, slot) < 0;
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~DeviceScopedStateTests"`
Expected: 6 passing.

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/Monitoring/DeviceScopedState.cs tests/NagaBatteryTray.Tests/DeviceScopedStateTests.cs
git commit -m "feat(monitoring): per-mouse state cache that clears on a model change"
```

---

## Task 6: AppHost — adopt the scoped state, guard the write slot

**Files:**
- Modify: `src/NagaBatteryTray/AppHost.cs` (`Start`, `RefreshProfileAsync`, `ResolveWriteSlotAsync`, `WriteVerifiedAsync`, the `_lastActive` field)

**Interfaces:**
- Consumes: `DeviceScopedState` (Task 5), `BatteryMonitor.PreferredMouse` (Task 4), `AppSettings.PreferredMouse` (Task 3c).
- Produces: `AppHost.OnDeviceStateChanged(DeviceState)`, `AppHost.ReseedDeviceAsync`, `AppHost.RecoverFromReconnectAsync`, `DashboardViewModel.ResetForNewMouse()`, and `_scoped` replacing `_lastActive`. Task 7 calls into the same handler for the selector.

No new unit tests: `AppHost` constructs WPF windows and is outside the suite by design (hard rule 6). Its logic now lives in `DeviceScopedState`, which Task 5 covers.

- [ ] **Step 1: Apply the saved preference at startup**

In `Start()`, immediately after `_device = new RazerDevice(_settings);`, add:

```csharp
        _device.PreferredModel = _settings.Settings.PreferredMouse;
```

- [ ] **Step 2: Replace the `_lastActive` field with the scoped state**

Delete the field at `AppHost.cs:168`:

```csharp
    private byte? _lastActive; // last active-slot read result; null = unknown (see ResolveWriteSlotAsync)
```

and add beside `_gridSweep`:

```csharp
    /// <summary>Everything cached about the CONNECTED mouse. Cleared automatically when the model
    /// changes, which is what stops the V2's slot number being written to a V3 (spec §4.6).</summary>
    private readonly DeviceScopedState _scoped = new();
```

Then update every former `_lastActive` use:

- `RefreshProfileAsync`: `_lastActive = active;` becomes:

```csharp
        _scoped.ActiveSlot = active;
        _scoped.CreatedSlots = list?.Slots;
```

- `ResolveWriteSlotAsync` is **deleted**. Slot resolution moves inside the atomic write (Task 4b), because resolving it here and writing it there is precisely the gap a reselect slips through.

- `WriteVerifiedAsync` becomes a thin caller of the atomic write:

```csharp
    private async Task<bool> WriteVerifiedAsync(byte buttonId, byte category, byte[] data)
    {
        bool resolvedOnDemand = _scoped.ActiveSlot is null;
        var r = await Task.Run(() => _monitor.WriteButtonVerifiedAsync(
            _scoped.CreatedSlots, buttonId, category, data));

        if (r.Slot is { } used) _scoped.ActiveSlot = used;
        // the mouse changed underneath the write: nothing cached describes the device any more,
        // and the write's outcome is not attributable to either mouse (spec §4.10)
        if (r.ConnectionChanged)
        {
            _scoped.Clear();
            Interlocked.Increment(ref _gridSweep);
            if (_dashboardVm is { } changedVm) _ = RecoverFromReconnectAsync(changedVm);
            return false;
        }
        // the slot was only just learned (the card said "state unknown"): let the card + grid
        // catch up so the other chips stop showing placeholders and gain undo snapshots
        if (r.Verified && resolvedOnDemand && _dashboardVm is { } dvm) _ = RefreshProfileAsync(dvm);
        return r.Verified;
    }
```

- [ ] **Step 3: Observe the model on every state change**

In `Start()`, after the `_monitor.LowBatteryCrossed` subscription, add:

```csharp
        _monitor.StateChanged += (_, state) => OnDeviceStateChanged(state);
```

and add the handler beside `OnDeviceChanged`:

```csharp
    /// <summary>Every battery state change passes the connected model through the scoped cache. A
    /// swap (either direction, including offline) drops everything the app knew about the previous
    /// mouse, supersedes any grid sweep still running for it, and re-seeds an open dashboard from
    /// the new one. Without this the app writes the old mouse's slot number to the new mouse and
    /// reports every write as failed (spec §4.6).</summary>
    private void OnDeviceStateChanged(DeviceState state)
    {
        if (!_scoped.Observe(state.Model)) return;
        Interlocked.Increment(ref _gridSweep);
        if (_dashboardVm is not { } vm) return;
        Dispatch(() => vm.ResetForNewMouse());
        _ = ReseedDeviceAsync(vm);
    }
```

**`ReseedDeviceAsync`, not `SeedDashboardAsync`, and this matters.** `SeedDashboardAsync` runs the
reachability probe, and that probe reconnects at the end, which can itself change the reported
model and re-enter this handler — an endless discovery loop. Split the seed in Task 7 so the
probe runs only when the dashboard opens or the user picks a mouse:

```csharp
    /// <summary>Reload everything that describes the CONNECTED mouse. No reachability probe: the
    /// probe reconnects when it finishes, which would re-enter OnDeviceStateChanged and loop.</summary>
    private async Task ReseedDeviceAsync(DashboardViewModel vm)
    {
        var dpi = await Task.Run(() => _monitor.GetDpiAsync());
        Dispatch(() => vm.SetCurrentDpi(dpi));
        await RefreshProfileAsync(vm);
    }
```

Except for one addition, `ReseedDeviceAsync` is today's `SeedDashboardAsync` body; Task 7 rewrites
`SeedDashboardAsync` as "probe, then call `ReseedDeviceAsync`".

The addition is discarding late results. Both reads here release the lock and can outlive the mouse
they were issued against, so each one carries the connection it ran under and is dropped if that
connection is gone:

```csharp
    private async Task ReseedDeviceAsync(DashboardViewModel vm)
    {
        int connection = _monitor.ConnectionId;
        var dpi = await Task.Run(() => _monitor.GetDpiAsync());
        if (_monitor.ConnectionId != connection || !ReferenceEquals(_dashboardVm, vm)) return;
        Dispatch(() => vm.SetCurrentDpi(dpi));
        await RefreshProfileAsync(vm);
    }
```

Both connection-change branches recover through one helper, and the ORDER in it is the point. A
probe-free reseed straight after an unannounced reconnect reads the new mouse's DPI and profiles
while `State.Model` — and therefore the profile-name key and the scoped cache — still names the old
one, so the new mouse's data gets filed under the old model. Republishing identity has to come
first:

```csharp
    /// <summary>Recover from a reconnect that nothing announced. A lock-waiting battery read
    /// republishes the connection's identity FIRST; only then is it safe to reload anything, because
    /// State.Model is what keys the scoped cache and the profile names. If the model did change,
    /// OnDeviceStateChanged has already reset and reseeded, so this does not do it twice.</summary>
    private async Task RecoverFromReconnectAsync(DashboardViewModel vm)
    {
        string? before = _monitor.State.Model;
        await Task.Run(() => _monitor.RefreshNowWaitingAsync());
        if (!ReferenceEquals(_dashboardVm, vm)) return;
        if (string.Equals(_monitor.State.Model, before, StringComparison.Ordinal)) await ReseedDeviceAsync(vm);
    }
```

Apply the same guard in `RefreshProfileAsync`: capture `_monitor.ConnectionId` before the two
profile reads and return without touching `_scoped` or the view model if it changed.

`ReadGridAsync` needs it too, and its existing generation counter is not a substitute: `_gridSweep`
moves on a PUBLISHED model change, a dashboard close, or a new sweep, and a mid-sweep reconnect onto
another mouse publishes nothing. Without this check a single grid can mix two mice's bindings and
arm undo snapshots against the wrong one. Capture the connection at sweep start and re-check it
beside the existing generation check:

```csharp
        int connection = _monitor.ConnectionId;
        for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
        {
            if (gen != Volatile.Read(ref _gridSweep) || !ReferenceEquals(_dashboardVm, vm)) return;
            int p = pos;
            var raw = await Task.Run(() => _monitor.GetButtonAsync(slot, NagaV2ProButtons.IdForPosition(p)));
            if (gen != Volatile.Read(ref _gridSweep) || !ReferenceEquals(_dashboardVm, vm)) return;
            if (_monitor.ConnectionId != connection)
            {
                // reconnected mid-sweep, possibly onto another mouse: abandon the half-read grid
                // rather than mixing two mice in it, drop the snapshots, and start over
                _scoped.Clear();
                Dispatch(() => vm.ResetForNewMouse());
                _ = RecoverFromReconnectAsync(vm);
                return;
            }
            Dispatch(() => vm.Callout(p).SetFromDevice(raw));
        }
```

`ResetForNewMouse` is a self-contained view-model method with no dependency on the selector, so it
lands here rather than in Task 7 — otherwise this task commits a tree that does not build, which
poisons `git bisect` for no benefit. Add to `DashboardViewModel`:

```csharp
    /// <summary>The connected mouse changed: drop everything that described the old one. The profile
    /// card, the DPI reading and every chip snapshot belong to one mouse and are meaningless on
    /// another (spec §4.6). A fresh seed refills them.</summary>
    public void ResetForNewMouse()
    {
        ProfileSlots.Clear();
        _activeSlot = null;
        SetProfileNote("");
        RefreshProfileDetail();
        SetCurrentDpi(null); // the old mouse's number must not stay on screen until the reseed lands
        foreach (var c in Callouts) c.SetPending(); // also voids each chip's undo snapshot
    }
```

`SetPending()` already clears `_currentRaw` and expires the undo window, so no snapshot survives a
mouse change (hard rule 2 is untouched: the snapshot/undo path itself is unchanged).

- [ ] **Step 4: Build and run the suite**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: green. A build error here is a missed `_lastActive` use.

- [ ] **Step 5: Commit**

```bash
git add src/NagaBatteryTray/AppHost.cs
git commit -m "feat(host): scope cached slot state to the connected mouse, guard the write slot"
```

---

## Task 7: Dashboard — mouse selector, per-mouse names, new-mouse reset, "Not set" chip

**Files:**
- Modify: `src/NagaBatteryTray/Ui/Dashboard/DashboardViewModel.cs`
- Modify: `src/NagaBatteryTray/Ui/Dashboard/CalloutViewModel.cs:126-134`
- Modify: `src/NagaBatteryTray/Ui/Dashboard/DashboardWindow.xaml:21`
- Modify: `src/NagaBatteryTray/AppHost.cs` (`OpenDashboard`, `SeedDashboardAsync`, new `SelectMouseAsync`)
- Test: `tests/NagaBatteryTray.Tests/MouseSelectorTests.cs` (create)
- Test: `tests/NagaBatteryTray.Tests/CalloutDisplayTests.cs` (create if absent, else append)

**Interfaces:**
- Consumes: `BatteryMonitor.DiscoverMiceAsync`, `BatteryMonitor.PreferredMouse` (Task 4); `AppSettings.ProfileNamesByMouse` (Task 3c); `DeviceScopedState` (Tasks 5, 6).
- Produces: `DashboardViewModel.Mice`, `.SelectedMouse`, `.MiceLoaded`, `.MouseSelectRequested`, `.SetMice(...)`, `.ResyncMouse()`. (`ResetForNewMouse` is Task 6's.)

- [ ] **Step 1: Write the failing tests**

Create `tests/NagaBatteryTray.Tests/MouseSelectorTests.cs`:

```csharp
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public sealed class MouseSelectorTests
{
    private static DashboardViewModel Build(AppSettings? settings = null) =>
        new(settings ?? new AppSettings(), false,
            (_, _, _, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(true));

    [Fact]
    public void SetMiceFillsTheListAndSelectsTheConnectedMouse()
    {
        var vm = Build();
        vm.SetMice(new[] { "Naga V3 Pro", "Naga V2 Pro" }, "Naga V2 Pro");

        Assert.Equal(new[] { "Naga V3 Pro", "Naga V2 Pro" }, vm.Mice);
        Assert.Equal("Naga V2 Pro", vm.SelectedMouse);
        Assert.True(vm.MiceLoaded);
    }

    [Fact]
    public void TheHeaderShowsTheConnectedMouseBeforeTheProbeLands()
    {
        var vm = Build();
        vm.ApplyState(DeviceState.Online(80, false, false, "Naga V3 Pro"));

        Assert.Equal(new[] { "Naga V3 Pro" }, vm.Mice);
        Assert.Equal("Naga V3 Pro", vm.SelectedMouse);
        Assert.False(vm.MiceLoaded);   // still disabled: nothing has been probed yet
    }

    [Fact]
    public void TheHeaderFollowsAnUnannouncedHopToAnotherMouse()
    {
        var vm = Build();
        vm.SetMice(new[] { "Naga V3 Pro", "Naga V2 Pro" }, "Naga V3 Pro");
        int picks = 0;
        vm.MouseSelectRequested += _ => picks++;

        vm.ApplyState(DeviceState.Online(60, false, false, "Naga V2 Pro"));

        Assert.Equal("Naga V2 Pro", vm.SelectedMouse);   // never claims a mouse it is not on
        Assert.Equal(0, picks);                          // and does not read as a user pick
    }

    [Fact]
    public void AProbeThatFindsNothingStillShowsTheConnectedMouse()
    {
        var vm = Build();
        vm.ApplyState(DeviceState.Online(60, false, false, "Naga V3 Pro"));

        vm.SetMice(Array.Empty<string>(), "Naga V3 Pro");

        Assert.Equal(new[] { "Naga V3 Pro" }, vm.Mice);
        Assert.False(vm.MiceLoaded);
    }

    [Fact]
    public void ProgrammaticSelectionDoesNotRaiseARequest()
    {
        var vm = Build();
        string? asked = null;
        vm.MouseSelectRequested += m => asked = m;

        vm.SetMice(new[] { "Naga V3 Pro" }, "Naga V3 Pro");

        Assert.Null(asked);
    }

    [Fact]
    public void AUserPickRaisesARequest()
    {
        var vm = Build();
        vm.SetMice(new[] { "Naga V3 Pro", "Naga V2 Pro" }, "Naga V3 Pro");
        string? asked = null;
        vm.MouseSelectRequested += m => asked = m;

        vm.SelectedMouse = "Naga V2 Pro";

        Assert.Equal("Naga V2 Pro", asked);
    }

    [Fact]
    public void RepickingTheConnectedMouseIsANoOp()
    {
        var vm = Build();
        vm.SetMice(new[] { "Naga V3 Pro", "Naga V2 Pro" }, "Naga V3 Pro");
        int calls = 0;
        vm.MouseSelectRequested += _ => calls++;

        vm.SelectedMouse = "Naga V3 Pro";

        Assert.Equal(0, calls);
    }

    [Fact]
    public void ResetForNewMouseDropsProfilesAndChipSnapshots()
    {
        var vm = Build();
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, 3);
        vm.Callout(1).SetFromDevice(new RawButtonAction(RazerProtocol.FnKeyboard, new byte[] { 0x00, 0x3a }));

        vm.ResetForNewMouse();

        Assert.Empty(vm.ProfileSlots);
        Assert.Equal("…", vm.Callout(1).BindingText);
        Assert.False(vm.DevicePresent);   // the old mouse's DPI is gone, not left on screen
    }

    [Fact]
    public void ProfileNamesAreScopedToTheConnectedMouse()
    {
        var settings = new AppSettings();
        settings.ProfileNamesByMouse["Naga V2 Pro"] = new Dictionary<byte, string> { [1] = "World of Warcraft" };
        var vm = Build(settings);

        vm.ApplyState(DeviceState.Online(80, false, false, "Naga V3 Pro"));
        vm.SetProfileInventory(new byte[] { 1 }, 1);

        Assert.Equal("Slot 1", vm.ProfileSlots[0].Name);
    }

    [Fact]
    public void ProfileNamesComeBackForTheMouseThatOwnsThem()
    {
        var settings = new AppSettings();
        settings.ProfileNamesByMouse["Naga V2 Pro"] = new Dictionary<byte, string> { [1] = "World of Warcraft" };
        var vm = Build(settings);

        vm.ApplyState(DeviceState.Online(80, false, false, "Naga V2 Pro"));
        vm.SetProfileInventory(new byte[] { 1 }, 1);

        Assert.Equal("World of Warcraft", vm.ProfileSlots[0].Name);
    }

    [Fact]
    public void ApplyToWritesNamesUnderTheConnectedMouse()
    {
        var vm = Build();
        vm.ApplyState(DeviceState.Online(80, false, false, "Naga V3 Pro"));
        vm.SetProfileInventory(new byte[] { 1 }, 1);
        vm.BeginRename();
        vm.ProfileNameDraft = "Raid";
        vm.CommitRename();

        var target = new AppSettings();
        vm.ApplyTo(target);

        Assert.Equal("Raid", target.ProfileNamesByMouse["Naga V3 Pro"][1]);
    }
}
```

Create `tests/NagaBatteryTray.Tests/CalloutDisplayTests.cs`:

```csharp
using NagaBatteryTray.Hid;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public sealed class CalloutDisplayTests
{
    private static CalloutViewModel Build() =>
        new(1, (_, _, _, _) => Task.FromResult(true), (_, _) => Task.FromResult(true));

    [Fact]
    public void TheOneByteStubReadsAsNotSet()
    {
        var c = Build();
        c.SetFromDevice(new RawButtonAction(RazerProtocol.FnKeyboard, new byte[] { 0x00 }));

        Assert.Equal("Not set", c.BindingText);
        Assert.False(c.Failed);
    }

    [Fact]
    public void ARealKeyStillDecodes()
    {
        var c = Build();
        c.SetFromDevice(new RawButtonAction(RazerProtocol.FnKeyboard, new byte[] { 0x00, 0x3a }));

        Assert.NotEqual("Not set", c.BindingText);
        Assert.False(c.Failed);
    }

    [Fact]
    public void AGenuinelyForeignCategoryStillSaysSynapse()
    {
        var c = Build();
        c.SetFromDevice(new RawButtonAction(0x05, new byte[] { 0x01, 0x02 }));

        Assert.Contains("Synapse", c.BindingText);
    }

    [Fact]
    public void AnOverlongKeyboardReplyIsNotCalledUnset()
    {
        var c = Build();
        c.SetFromDevice(new RawButtonAction(RazerProtocol.FnKeyboard, new byte[] { 0x00, 0x3a, 0x01 }));

        // never measured on either mouse; saying "Not set" about an assigned button invites an overwrite
        Assert.Contains("Synapse", c.BindingText);
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~MouseSelectorTests|FullyQualifiedName~CalloutDisplayTests"`
Expected: compile errors for the new members.

- [ ] **Step 3: The "Not set" chip**

In `CalloutViewModel.cs`, replace `DisplayRaw` with:

```csharp
    private void DisplayRaw(RawButtonAction r)
    {
        if (r.Category == RazerProtocol.FnKeyboard && r.Data.Length == 2)
        { SetApplied(ButtonActionKind.Key, r.Data[0], r.Data[1]); return; }
        if (r.Category == RazerProtocol.FnDisabled)
        { SetApplied(ButtonActionKind.Disabled, 0, 0); return; }
        // the firmware's SHORT keyboard form: a button with nothing assigned, and every button of a
        // profile slot the mouse has not created. Not a failure and not a Synapse macro — decoding it
        // as one painted whole grids red on the V3 (spec 2026-08-19 §1.1, §4.8).
        // Strictly shorter than 2: a LONGER 0x02 reply is a shape we have never measured, and calling
        // an unmodelled binding "Not set" tells the user an assigned button is empty and invites an
        // overwrite. Unmodelled shapes keep the existing Synapse label.
        if (r.Category == RazerProtocol.FnKeyboard && r.Data.Length < 2)
        { _deviceText = UnsetText; NotifyBinding(); return; }
        _deviceText = $"Synapse action (0x{r.Category:x2})";
        NotifyBinding();
    }
```

and add beside `PendingText`:

```csharp
    private const string UnsetText = "Not set";
```

- [ ] **Step 4: The view model's selector, reset, and per-mouse names**

In `DashboardViewModel.cs`:

Replace the constructor line `_profileNames = new Dictionary<byte, string>(source.ProfileNames);` with:

```csharp
        foreach (var (mouse, names) in source.ProfileNamesByMouse)
            _namesByMouse[mouse] = new Dictionary<byte, string>(names);
```

Replace the `private readonly Dictionary<byte, string> _profileNames;` field with:

```csharp
    // profile names are per-mouse: slot 1 on a V2 is not slot 1 on a V3 (spec 2026-08-19 §4.9)
    private readonly Dictionary<string, Dictionary<byte, string>> _namesByMouse = new();
    private string _namesKey = "Razer Naga";
    private Dictionary<byte, string> _profileNames
    {
        get
        {
            if (!_namesByMouse.TryGetValue(_namesKey, out var names))
                _namesByMouse[_namesKey] = names = new Dictionary<byte, string>();
            return names;
        }
    }
```

In `ApplyState`, after `ModelName = s.Model ?? "Razer Naga";` add:

```csharp
        _namesKey = ModelName;
        // The box IS the header's model indicator now, so it has to follow the mouse actually
        // connected — before the probe lands (it reads like the label it replaced) and after an
        // unannounced hop (otherwise it says "Naga V3 Pro" over the V2's battery and profile card,
        // and re-picking the displayed value is a no-op, so the user cannot even correct it).
        if (s.Model is { } connected && !string.Equals(SelectedMouse, connected, StringComparison.Ordinal))
        {
            if (!Mice.Contains(connected)) Mice.Add(connected);
            _syncingMouse = true;
            try { SelectedMouse = connected; }
            finally { _syncingMouse = false; }
        }
```

In `ApplyTo`, replace `target.ProfileNames = new Dictionary<byte, string>(_profileNames);` with:

```csharp
        target.ProfileNamesByMouse = new Dictionary<string, Dictionary<byte, string>>();
        foreach (var (mouse, names) in _namesByMouse)
            if (names.Count > 0) target.ProfileNamesByMouse[mouse] = new Dictionary<byte, string>(names);
```

Add the selector members beside the profile-card members:

```csharp
    // ---- header: mouse selector (spec 2026-08-19 §4.5) ----

    /// <summary>Mice that answered the reachability probe, in SupportedMice order. Only reachable
    /// mice are listed, so a sleeping mouse never appears (user decision, 2026-08-19).</summary>
    public ObservableCollection<string> Mice { get; } = new();

    /// <summary>False until the first probe lands — the ComboBox stays disabled and shows the
    /// connected model, so it reads exactly like the old label while discovery runs.</summary>
    public bool MiceLoaded { get => _miceLoaded; private set => Set(ref _miceLoaded, value); }
    private bool _miceLoaded;

    public event Action<string>? MouseSelectRequested;

    /// <summary>Two-way ComboBox binding, same guard as the profile dropdown: programmatic syncs run
    /// under _syncingMouse, so only a USER pick raises MouseSelectRequested.</summary>
    public string? SelectedMouse
    {
        get => _selectedMouse;
        set
        {
            if (!Set(ref _selectedMouse, value)) return;
            if (_syncingMouse || value is null) return;
            if (string.Equals(value, ModelName, StringComparison.Ordinal)) return;
            MouseSelectRequested?.Invoke(value);
        }
    }
    private string? _selectedMouse;
    private bool _syncingMouse;

    /// <summary>Feed the header a fresh reachability probe plus the model actually connected.</summary>
    public void SetMice(IReadOnlyList<string> mice, string? connected)
    {
        Mice.Clear();
        foreach (var m in mice) Mice.Add(m);
        // a probe that found nothing must not leave an empty box where the model label used to be
        if (Mice.Count == 0 && connected is not null) Mice.Add(connected);
        _syncingMouse = true;
        try { SelectedMouse = connected is not null && Mice.Contains(connected) ? connected : Mice.FirstOrDefault(); }
        finally { _syncingMouse = false; }
        MiceLoaded = mice.Count > 0;   // the PROBE's result, not the box's contents
    }

    /// <summary>Snap the dropdown back to the mouse actually connected, after a failed pick.</summary>
    public void ResyncMouse()
    {
        _syncingMouse = true;
        try { SelectedMouse = Mice.Contains(ModelName) ? ModelName : null; }
        finally { _syncingMouse = false; }
    }

```

`ResetForNewMouse` is not here: Task 6 added it, so that its own commit builds.

- [ ] **Step 5: The header ComboBox**

In `DashboardWindow.xaml`, replace line 21:

```xml
            <TextBlock Style="{DynamicResource BodyText}" FontSize="14" FontWeight="SemiBold" Text="{Binding ModelName}"/>
```

with:

```xml
            <ComboBox MinWidth="160" ItemsSource="{Binding Mice}"
                      SelectedItem="{Binding SelectedMouse, Mode=TwoWay}"
                      IsEnabled="{Binding MiceLoaded}"
                      ToolTip="Which mouse this app is controlling"/>
```

No converter and no second `TextBlock`: `ApplyState` seeds `Mice` with the connected model
(Step 4), so the box always has exactly one item to show before the probe lands, disabled, reading
just like the label it replaced. Themed resources only; no hardcoded colours, no effects
(hard rule 3).

- [ ] **Step 6: Wire the host**

In `AppHost.OpenDashboard`, after `vm.SwitchRequested += ...`, add:

```csharp
        vm.MouseSelectRequested += name => _ = SelectMouseAsync(vm, name);
```

Split the seed so only this path probes. Replace `SeedDashboardAsync` with:

```csharp
    // Task.Run keeps the blocking HidD_*Feature calls off the UI thread (no UI freeze; supports the
    // lightweight + zero-latency invariant). Results marshal back via Dispatch.
    private async Task SeedDashboardAsync(DashboardViewModel vm)
    {
        // Task.Run is load-bearing, not decoration: DiscoverMiceAsync takes the read lock, and when
        // the lock is free the wait completes synchronously, so enumeration, CreateFile and the first
        // feature reports would run on the UI thread this was called from (hard rule 1)
        var mice = await Task.Run(() => _monitor.DiscoverMiceAsync());
        Dispatch(() => vm.SetMice(mice, _monitor.State.Model));
        await ReseedDeviceAsync(vm);
    }
```

and add `ReseedDeviceAsync` exactly as Task 6 specifies it — the old `SeedDashboardAsync` body.
Nothing but dashboard-open and `SelectMouseAsync` may call `SeedDashboardAsync`; a model change
calls `ReseedDeviceAsync`, or the probe's own reconnect re-enters the handler forever.

Add the handler beside `SwitchProfileAsync`:

```csharp
    /// <summary>Header dropdown pick (spec §4.5): remember the choice, reconnect, and re-seed. The
    /// new mouse holds entirely different DPI, profiles and bindings, so nothing from the old one
    /// survives — OnDeviceStateChanged clears the cache as soon as the model change lands.</summary>
    private async Task SelectMouseAsync(DashboardViewModel vm, string name)
    {
        _settings.Settings.PreferredMouse = name;
        _settings.Save();
        _monitor.PreferredMouse = name;

        // WAITS for the read lock: the ordinary refresh skips when a read is in flight, which would
        // report the mouse the user just picked as unreachable (spec §4.5). Task.Run for the same
        // reason as in SeedDashboardAsync - an uncontended lock keeps the HID work on this thread.
        await Task.Run(() => _monitor.RefreshNowWaitingAsync());

        if (!string.Equals(_monitor.State.Model, name, StringComparison.Ordinal))
        {
            Dispatch(() =>
            {
                vm.SetProfileNote("Couldn't reach that mouse — wiggle it and retry");
                vm.ResyncMouse();
            });
            return;
        }
        await SeedDashboardAsync(vm);
    }
```

- [ ] **Step 7: Run the new tests**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~MouseSelectorTests|FullyQualifiedName~CalloutDisplayTests"`
Expected: all passing.

- [ ] **Step 8: Update the three existing tests this task invalidates**

These assert the behaviour the task deliberately replaces. Leaving them is not an option: the green
gate below cannot pass while they stand.

1. `tests/NagaBatteryTray.Tests/DashboardViewModelTests.cs:133-143` seeds `src.ProfileNames[2]`,
   which the constructor no longer reads. Seed the per-mouse map and tell the view model which mouse
   it is on:

```csharp
    [Fact]
    public void Slot_names_seed_from_settings_and_default_when_absent()
    {
        var src = new AppSettings();
        src.ProfileNamesByMouse["Naga V2 Pro"] = new Dictionary<byte, string> { [2] = "Work" };
        var vm = NewVm(src);
        vm.ApplyState(DeviceState.Online(80, false, false, "Naga V2 Pro"));
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, active: 1);

        Assert.Equal("Slot 1", vm.ProfileSlots.Single(p => p.Number == 1).Name);
        Assert.Equal("Work", vm.ProfileSlots.Single(p => p.Number == 2).Name);
        Assert.Equal("Slot 3", vm.ProfileSlots.Single(p => p.Number == 3).Name);
    }
```

2. In the rename test at `DashboardViewModelTests.cs:255`, `ApplyTo` no longer writes
   `target.ProfileNames`, so that line throws `KeyNotFoundException`. The test's view model is on the
   default "Razer Naga" unless it was given a state, so assert under whichever key it used:

```csharp
        Assert.Equal("Work", target.ProfileNamesByMouse[vm.ModelName][2]);
```

3. `tests/NagaBatteryTray.Tests/CalloutViewModelTests.cs:295-301` asserts that a one-byte category
   `0x02` renders as "Synapse action (0x02)" — the exact rendering this task abolishes. Delete it;
   `CalloutDisplayTests.TheOneByteStubReadsAsNotSet` is its replacement, and the neighbouring
   category-`0x01` test still covers the genuine Synapse label.

- [ ] **Step 9: Run the whole suite**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: all green, including `DpiPillInteractionTests` and the pre-existing dashboard tests.

- [ ] **Step 10: Render the dashboard through the UI probe and look at the header**

Run:

```powershell
$env:NAGA_UI_PROBE = "1"
$env:NAGA_UI_PROBE_OUT = "$env:TEMP\naga-header.png"
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~DashboardScreenshotProbe"
```

Expected: the header shows the mouse ComboBox with the link caption beneath it, and the layout is unchanged otherwise.

- [ ] **Step 11: Commit**

```bash
git add src/NagaBatteryTray tests/NagaBatteryTray.Tests
git commit -m "feat(dashboard): mouse selector in the header, per-mouse profile names, unset-button chip"
```

---

## Task 8: Docs and screenshots

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `CLAUDE.md`
- Modify: `docs/images/*.png` (regenerated)

- [ ] **Step 1: Regenerate the README screenshots**

Regenerate every image the header appears in, one probe run per image, using the existing variables (`NAGA_UI_PROBE_OUT` is a single file path, `NAGA_UI_PROBE_THEME`, `NAGA_UI_PROBE_STATE`, `NAGA_UI_PROBE_TARGET`). Write each straight over the file it replaces in `docs/images/`.

- [ ] **Step 2: Update README.md**

Add the mouse selector to the feature list and refresh any caption that describes the header as a static model label.

- [ ] **Step 3: Update docs/architecture.md**

Add a short subsection covering: the connection layering (`IControlCollection` as the Win32 boundary, `ConnectionPolicy` as the reselect rule, `MouseConnection` as the state machine) and why the transport exemption now covers only the two Win32 wrappers, the preference-ordered scan (`ScanPidsFor`), the battery-path-only reselect and why no other call may reselect a live handle (while any call may still reconnect a dropped one), `ConnectionId` and the unannounced-reconnect problem it solves, the reachability probe and why HID enumeration cannot replace it, `DeviceScopedState` and the defect it fixes, and the firmware's 1-byte stub for uncreated slots (with the slot table from the spec's §1.1).

- [ ] **Step 4: Update CLAUDE.md**

Move the "V3 acceptance follow-ups" backlog item into Shipped, dated 2026-08-19, linking `specs/2026-08-19-naga-mouse-selector-design.md`, and note that A, B and C shared one root cause. Leave the DPI stages item as the next entry. Add the create-profile UI as a new unscheduled item, since the V3 ships with a single slot.

- [ ] **Step 5: Commit**

```bash
git add README.md docs CLAUDE.md
git commit -m "docs: mouse selector, per-device state isolation, regenerated screenshots"
```

---

## Acceptance (installed build, real hardware)

Run `.\scripts\install.ps1`, then check the spec's §7 list:

1. Both mice reachable: the header lists both, defaulting to the V3 Pro.
2. Pull the V3's cable; the app may land on the V2 briefly and must return to the V3 on its own within one battery interval.
3. Set several thumb buttons right after that swap: no false "Not applied".
4. No chip shows a red "Synapse action (0x02)".
5. V2 profile names do not appear on the V3.
6. Idle footprint back to baseline and mouse feel unchanged (hard rule 1).

---

## Debate record

Cross-model plan verification, parallax multi-model-verify mode `plan`.

- Session lane: Claude Opus 5 (driver, live repo access).
- Reviewer lane: `gpt-5.6-sol` via codex-cli 0.144.1, effort `high`, sandbox `read-only`,
  provider `openai`. Effective route confirmed from the run header.
- Verification status: FULL. Repo instruction-channel enumeration clean (no `AGENTS.md`, no
  `.agents/`, no `.kimi-code/`), so no review mirror was needed. Client context probe: `status:
  clean`, 0 skills advertised, 0 repo-scoped and 0 plugin-cache-scoped instruction sources, 29
  home-scoped skills suppressed by the verified override. The user's global
  `~/.codex/AGENTS.md` is present and instructing the reviewer; recorded, not removed.
- Per-round evidence: `read-codex-round-evidence.ps1` returned `clean` for round 1 — client-echo
  evidence that the reply is bound to the brief this side sent.
- Fix-verify budget: 4 dispatched exchanges, declared before round 1.

### Round 1 — reviewer verdict FIX, nine findings

The session verified every finding against the live repo before adjudicating. All nine were real.

| Finding | Adjudication | Where it landed |
| --- | --- | --- |
| Claim 3's rationale named only one `_lastActive` assignment site | Accepted; the session's brief was wrong, the plan was already correct | no change |
| Resolve then write then verify spans three lock acquisitions, so a reselect can cross mice | Accepted | new Task 4b, spec 4.10 |
| "Never triggers a re-scan" overstates it; a dropped handle still reconnects | Accepted | spec 4.3, Task 3 Step 4 |
| `AdoptPreferredAsync` loses and leaks the live handle on exception or cancellation | Accepted | Task 3 Step 3, `try/finally` |
| Discovery cannot re-enter the handler, but leaves `State.Model` describing the wrong mouse | Accepted; the session's re-entrancy premise was refuted | spec 4.4, Task 4 |
| A pick can falsely fail because `PollAsync` skips when the lock is busy | Accepted | `RefreshNowWaitingAsync`, Tasks 4 and 7 |
| `ResetForNewMouse` leaves the old mouse's DPI on screen | Accepted | Task 7 Step 4 |
| Model-only invalidation misses a reconnect that changes mouse without a battery read | Accepted | `ConnectionId`, spec 4.10 |
| The connection state machine has no automated coverage | Partially accepted | new `ConnectionPolicy` + Task 3 Step 1 |

### Round 2 — reviewer verdict FIX, four more findings plus one escalation

| Finding | Adjudication | Where it landed |
| --- | --- | --- |
| The atomic write only *detects* a mid-write reconnect; `SetButtonAsync` can reconnect internally and the SET already went out | Accepted | Task 4b now always READS the active slot inside the lock, which forces any reconnect before a slot is committed to; `RazerDevice.SetButtonAsync` refuses a SET whose model changed mid-call |
| Two "never scan" statements still stood in the device doc comment and Task 8 | Accepted | both now say "never reselect a live handle" |
| The grid sweep has no connection check; `_gridSweep` moves only on *published* changes | Accepted | `ReadGridAsync` captures `ConnectionId` and re-checks after every read |
| `ConnectionPolicy` returns `ProbePreferred` for an unknown preference name, so a bad settings value probes on every battery read | Accepted — a real defect in the session's own new code, not a doc gap | `RazerProtocol.NormalizeModelName`, applied on `PreferredModel` set, with tests |
| Spec §6 promised an injectable connector the plan does not build | Accepted | §6 reconciled with the chosen coverage, seam recorded as open |
| `DiscoverMiceAsync` and `RefreshNowWaitingAsync` are awaited directly from the UI thread, and an uncontended `WaitAsync` completes synchronously, so enumeration and `CreateFile` run on it | Accepted | both wrapped in `Task.Run` |
| The connector seam itself | ESCALATED to the product owner | see below |

### Round 3 — reviewer verdict FIX, two findings

| Finding | Adjudication | Where it landed |
| --- | --- | --- |
| Both connection-change branches reseed *before* republishing identity, so the new mouse's DPI, profiles and profile names get filed under the old model | Accepted; the session had the order backwards | new `RecoverFromReconnectAsync`: lock-waiting battery refresh first, reseed after, and only if the model was unchanged |
| Spec §4.7 still described the deleted `ResolveWriteSlotAsync`, and §4.10 still called the sweep generation equivalent protection | Accepted | both passages rewritten |

### Round 4 — PASS

Reviewer: "No substantive plan risk remains." Overall verdict PASS. Dry round, no new finding, no
outstanding contested point. Fix-verify budget: 4 of 4 exchanges used.

### Round 7 — PASS

Both round-6 fixes verified. "No substantive plan risk remains." Dry round, no new finding, no
outstanding contested point. Seven rounds total: the declared budget of four, one round the product
owner authorized to check the restructure, and two more under a standing authorization to continue
until convergence.

### Session's final adjudication

Terminal verdict: **PASS on the plan.** Twenty-two findings across seven rounds, every one checked
against the live repo before it was accepted; all twenty-two were real. Two of the session's own
claims were refuted with evidence and are recorded as such: that `_lastActive` had a single
assignment site, and that discovery could re-enter the model-change handler.

Five of the findings were in code the session wrote *after* an earlier PASS — wrong V2 PIDs, a
cancellation test that could not reach its assertion, dropped marshalling attributes, a lost
transport-loss guard, and a fake that could never verify. That is the argument for re-reviewing
after amendments rather than treating a PASS as durable.

Nothing here is verified code. The plan remains unexecuted, `dotnet test` has not run against any of
it, and hardware acceptance (spec §7, plus the probe-suite gate at the end of Task 3b) is still what
decides whether it works.

### Second lane — Fable, three rounds, PASS

A same-vendor reviewer (`parallax:fable-panel-reviewer`) reviewed the plan after the cross-vendor
lane closed, at the product owner's direction. Each round ran in a fresh seat, so every claim was
re-verified against the tree rather than carried in memory. It found six things the cross-vendor
lane did not:

| Finding | Adjudication | Where it landed |
| --- | --- | --- |
| All nine new test files omitted `using Xunit;`; the project's `ImplicitUsings` does not cover xUnit and there is no global-usings file, so nothing would compile | Accepted | added to every file, plus a standing line in Global Constraints |
| Three EXISTING tests assert behaviour this pass abolishes, so the plan's own "all green" gates could not pass: the legacy profile-name round-trip, two dashboard name assertions, and the 1-byte-stub-as-Synapse test | Accepted | explicit rewrite steps in Task 3c and Task 7 |
| The header selector never resynced after an unannounced hop, so it would claim the V3 over the V2's battery and profile card, and re-picking the shown value is a no-op so the user could not correct it | Accepted | `ApplyState` resyncs under the sync guard, with two tests |
| A probe that found nothing left an empty ComboBox where the model label used to be | Accepted | `SetMice` keeps the connected model; `MiceLoaded` follows the probe, not the box |
| Task 6 deliberately committed a tree that does not build | Accepted | `ResetForNewMouse` moved into Task 6; its commit builds |
| `DisplayRaw` sent ANY non-2-byte `0x02` reply to "Not set", including longer ones never measured — telling the user an assigned button is empty invites an overwrite | Accepted | strictly `< 2`; longer shapes keep the Synapse label, with a test |

Two judgment calls were put to it and both came back PASS: recovery through a full rescan is
correct rather than wasteful, and the `RefreshNowWaitingAsync` reset semantics match every other
explicit refresh.

Round 3 verdict: PASS at commit `cac78f2`. Both lanes have now passed the plan.

### The escalated question, and how it was settled

The reviewer recommended isolating the Win32 handle lifecycle behind an injectable connector so that
adoption, rollback, disposal of the losing handle, and `ConnectionId` increments get automated tests.
The session recommended deferring it: `RazerDevice` is proven on real hardware, and hard rule 6 makes
the HID transport a deliberate no-unit-test exception.

**The product owner decided for the reviewer (2026-08-19): do the restructure first.** Tasks 3 and 3b
carry it. `IControlCollection` is now the only place Win32 appears in the connection path;
`MouseConnection` holds the state machine with thirteen tests covering adoption, skip-and-dispose,
live reuse, hopping back, refusing to hop on a non-battery call, and rollback on failure, on an
exception and on cancellation, plus wrong-loser disposal and the `ConnectionId` increment. Hard rule
6's exemption now covers only the two Win32 wrappers and the WPF windows.

Because this rewrites plumbing the hardware depends on, Task 3b ends with a probe-suite run against
a real mouse before any UI work builds on it.

### Round 5 — reviewer verdict FIX, five findings on the restructure

Authorized by the product owner beyond the declared budget, specifically to check the change the
session had argued against. It found five defects, every one confirmed against the repo:

| Finding | Adjudication | Where it landed |
| --- | --- | --- |
| The state-machine tests declared the V2's PIDs as `0x0085`/`0x0086`; they are `0x00A7`/`0x00A8`, so most scenarios were unreachable and the suite could not pass | Accepted | tests now use the `RazerProtocol` constants |
| `ACancelledHopKeepsTheLiveConnection` had the V3 present from the start, so the injected cancellation fired during the FIRST `EnsureAsync` and the test never established the connection it asserts about | Accepted | the V3 appears only after the V2 is adopted |
| The executable order still ran settings and protocol work before the restructure the owner gated everything behind | Accepted | settings moved behind it as Task 3c; only Task 2, a real prerequisite, runs first |
| `[return: MarshalAs(UnmanagedType.U1)]` was dropped from the moved P/Invoke declarations, changing the native success boundary on a "verbatim" move | Accepted | both attributes restored, with a comment saying they are load-bearing |
| Transport loss changed behaviour: today a failed HID call nulls the handle so nothing probes it again, but a candidate is not owned by the connection yet, so tid probing would keep calling into a dead collection | Accepted | `IControlCollection.IsOpen`, checked by every exchange and by the tid probe loop, plus a new `RazerDeviceTransportTests` asserting exactly one feature call reaches a dead collection |

The last one is the clearest argument for the restructure the session opposed: it is a real
behavioural regression in the move, and the seam is what makes the test for it possible at all.

### Round 6 — four PASS, two more findings, both in the session's own new test code

| Finding | Adjudication | Where it landed |
| --- | --- | --- |
| `FakeControlCollectionSource` was declared public while implementing the internal `IControlCollectionSource`, so the test project would not compile | Accepted | the fake and its nested collection are internal, reached through `InternalsVisibleTo` |
| `AWorkingCollectionIsReportedWithItsModelAndLink` assumed a zero-filled reply could verify a candidate, but the production parsers require status `0x02` and a valid CRC, so it could never pass | Accepted | the fake gained `Answers`, echoing the request back as a completed reply with a real CRC; the test now asserts `IsPresent`, `Model` and `IsWired` as well, and a third case covers preference through the real device |
