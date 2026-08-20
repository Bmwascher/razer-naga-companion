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
- Branch: `feature/mouse-selector`. Commit after every task. Do not push.

---

## File Structure

| File | Responsibility |
| --- | --- |
| `src/NagaBatteryTray/Settings/AppSettings.cs` | new `PreferredMouse`, new `ProfileNamesByMouse`, retire `ProfileNames` |
| `src/NagaBatteryTray/Settings/JsonSettingsStore.cs` | one-time migration of legacy profile names |
| `src/NagaBatteryTray/Hid/RazerProtocol.cs` | preference-aware scan order helpers (pure, testable) |
| `src/NagaBatteryTray/Hid/ConnectionPolicy.cs` | **new** — the reselect decision, extracted so it is testable without Win32 |
| `src/NagaBatteryTray/Hid/IRazerDevice.cs` | `PreferredModel`, `DiscoverReachableAsync` |
| `src/NagaBatteryTray/Hid/RazerDevice.cs` | preference-ordered connect, reselect on the battery path, discovery |
| `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs` | lock-serialized preference, discovery, and the atomic resolve-write-verify |
| `src/NagaBatteryTray/Monitoring/DeviceScopedState.cs` | **new** — per-mouse cache that clears on model change |
| `src/NagaBatteryTray/AppHost.cs` | wire the state object, the write-slot guard, the selector handler |
| `src/NagaBatteryTray/Ui/Dashboard/CalloutViewModel.cs` | render the 1-byte stub as "Not set" |
| `src/NagaBatteryTray/Ui/Dashboard/DashboardViewModel.cs` | `Mice`, `SelectedMouse`, per-mouse names, new-mouse reset |
| `src/NagaBatteryTray/Ui/Dashboard/DashboardWindow.xaml(.cs)` | header ComboBox |
| `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs` | implement the two new interface members |

---

## Task 1: Settings — preference, per-mouse profile names, migration

**Files:**
- Modify: `src/NagaBatteryTray/Settings/AppSettings.cs`
- Modify: `src/NagaBatteryTray/Settings/JsonSettingsStore.cs`
- Test: `tests/NagaBatteryTray.Tests/SettingsMigrationTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `AppSettings.PreferredMouse` (`string?`), `AppSettings.ProfileNamesByMouse` (`Dictionary<string, Dictionary<byte, string>>`). Later tasks read both.

- [ ] **Step 1: Write the failing tests**

Create `tests/NagaBatteryTray.Tests/SettingsMigrationTests.cs`:

```csharp
using System.Text.Json;
using NagaBatteryTray.Settings;

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

- [ ] **Step 6: Run the whole suite**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: all green. `DashboardViewModel` still compiles because it reads `source.ProfileNames`, which still exists (Task 6 moves it).

- [ ] **Step 7: Commit**

```bash
git add src/NagaBatteryTray/Settings tests/NagaBatteryTray.Tests/SettingsMigrationTests.cs
git commit -m "feat(settings): preferred mouse + per-mouse profile names with one-time migration"
```

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

## Task 3: Device layer — preference, reselect on the battery path, discovery

**Files:**
- Modify: `src/NagaBatteryTray/Hid/IRazerDevice.cs`
- Modify: `src/NagaBatteryTray/Hid/RazerDevice.cs:62-101` (`EnsureConnectedAsync`, `FindControlPaths`)
- Modify: `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs`

**Interfaces:**
- Consumes: `RazerProtocol.ScanPidsFor`, `RazerProtocol.PidsForModel` (Task 2).
- Produces on `IRazerDevice`: `string? PreferredModel { get; set; }` and `Task<IReadOnlyList<string>> DiscoverReachableAsync(CancellationToken ct)`. Tasks 4 and 7 consume both.

`RazerDevice` wraps real Win32 HID calls and is a deliberate no-unit-test exception (hard rule 6). That exception covers the transport, not the decision of which mouse to talk to. This task therefore splits the two: the **decision** moves into a pure `ConnectionPolicy` that Step 1 tests exhaustively, and the **rollback** is made structural with `try/finally` rather than left to a test that cannot reach it. Only "return to the preferred mouse on the next battery read" remains acceptance-only (acceptance step 2).

- [ ] **Step 1: Extract and test the connection decision**

Create `src/NagaBatteryTray/Hid/ConnectionPolicy.cs`:

```csharp
namespace NagaBatteryTray.Hid;

/// <summary>What a device call should do with the connection it already has. Pure decision, no
/// handles and no Win32, so the rule that fixed the sticky-selection defect is testable while the
/// transport stays a no-unit-test exception (spec 2026-08-19 §4.3).</summary>
internal enum ConnectionDecision
{
    /// <summary>Keep the live handle untouched. Every non-battery call lands here.</summary>
    KeepLive,
    /// <summary>Live, but on the wrong mouse and the caller may reselect: probe the preferred model
    /// only, and keep the live connection if it does not answer.</summary>
    ProbePreferred,
    /// <summary>No usable handle: scan every candidate in preference order.</summary>
    FullScan,
}

internal static class ConnectionPolicy
{
    internal static ConnectionDecision Decide(bool hasLiveHandle, string? currentModel, string? preferredModel, bool reselect)
    {
        if (!hasLiveHandle) return ConnectionDecision.FullScan;
        if (!reselect || preferredModel is null) return ConnectionDecision.KeepLive;
        return string.Equals(currentModel, preferredModel, StringComparison.OrdinalIgnoreCase)
            ? ConnectionDecision.KeepLive
            : ConnectionDecision.ProbePreferred;
    }
}
```

Create `tests/NagaBatteryTray.Tests/ConnectionPolicyTests.cs`:

```csharp
using NagaBatteryTray.Hid;

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
}
```

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~ConnectionPolicyTests"`
Expected: fails to compile first, then 5 passing once `ConnectionPolicy.cs` exists.

- [ ] **Step 2: Extend the interface**

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
    /// (spec 2026-08-19 §4.10).</summary>
    int ConnectionId { get; }
```

- [ ] **Step 3: Implement in RazerDevice — split connect out of EnsureConnectedAsync**

Replace `EnsureConnectedAsync` and `FindControlPaths` in `RazerDevice.cs` with:

```csharp
    /// <summary>Which model to prefer (spec 2026-08-19 §4.1). Null = scan order.</summary>
    public string? PreferredModel { get; set; }

    /// <summary>Bumped on every adoption — see IRazerDevice.ConnectionId (spec §4.10).</summary>
    public int ConnectionId { get; private set; }

    /// <summary>Ensures <see cref="_handle"/> points at a collection that actually answers, and returns its
    /// working transaction id (0 = none reachable). Reuses a live connection cheaply; on (re)connect it tries
    /// each candidate collection — preferred model first, wired before wireless — and keeps the first that
    /// replies to a battery query. The verify step is essential: the wireless receiver stays enumerated when
    /// the mouse switches to wired, so picking by enumeration order alone would lock onto a collection that
    /// can no longer reach the mouse.
    /// <para>reselect (battery path only) re-checks the preference on a LIVE handle — the fix for the
    /// sticky-selection defect, which was this method returning early forever. Every other caller leaves it
    /// false, so button/DPI/profile calls never trigger a scan (spec §4.3).</para></summary>
    private async Task<byte> EnsureConnectedAsync(CancellationToken ct, bool reselect = false)
    {
        bool live = _handle is { IsInvalid: false } && _tid != 0;
        switch (ConnectionPolicy.Decide(live, _model, PreferredModel, reselect))
        {
            case ConnectionDecision.KeepLive:
                return _tid;
            case ConnectionDecision.ProbePreferred:
                return await AdoptPreferredAsync(ct);
            default:
                CloseHandle();
                return await ConnectAsync(RazerProtocol.ScanPidsFor(PreferredModel), ct);
        }
    }

    /// <summary>Running on a non-preferred mouse: probe ONLY the preferred model. Adopt it if it answers,
    /// otherwise put the live connection back untouched — a sleeping preference must never cost the user
    /// the mouse that is actually awake (spec §4.3).
    /// <para>The rollback is a finally, not an if: an exception or a cancellation between the detach and
    /// the adoption would otherwise leave the app with no connection AND leak the detached handle.</para></summary>
    private async Task<byte> AdoptPreferredAsync(CancellationToken ct)
    {
        var liveHandle = _handle;
        byte liveTid = _tid;
        bool liveWired = _wired;
        string? liveModel = _model;
        bool adopted = false;

        _handle = null; // detach so ConnectAsync's failure path can't dispose the live handle
        _tid = 0;
        try
        {
            byte tid = await ConnectAsync(RazerProtocol.PidsForModel(PreferredModel), ct);
            adopted = tid != 0;
            return adopted ? tid : liveTid;
        }
        finally
        {
            if (adopted)
            {
                liveHandle?.Dispose(); // the loser, and only the loser
            }
            else
            {
                CloseHandle();          // drop anything a partial attempt left behind
                _handle = liveHandle;
                _tid = liveTid;
                _wired = liveWired;
                _model = liveModel;
            }
        }
    }

    /// <summary>Open and verify the first collection among <paramref name="pids"/> that answers.
    /// 0 = none did; the handle is left closed.</summary>
    private async Task<byte> ConnectAsync(int[] pids, CancellationToken ct)
    {
        foreach (var (pid, path) in FindControlPaths(pids))
        {
            var h = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); continue; }
            _handle = h;

            byte tid = await ResolveTransactionIdAsync(ct);
            // ResolveTransactionIdAsync short-circuits on a cached id without touching this handle, so confirm
            // this collection truly answers before committing to it; otherwise fall through to the next.
            if (tid != 0 && await QueryAsync(tid, RazerProtocol.CommandIdBattery, ct) is not null)
            {
                _tid = tid;
                _wired = RazerProtocol.IsWiredPid(pid);
                _model = RazerProtocol.ModelNameForPid(pid);
                ConnectionId++; // every adoption, same model or not (spec §4.10)
                return tid;
            }
            CloseHandle();
        }
        return 0;
    }

    /// <summary>Mouse HID collections exposing the 90+1 byte feature report (with their PID), in the
    /// given PID order. The PID is returned so the caller can report the active link and model.</summary>
    private static IEnumerable<(int Pid, string Path)> FindControlPaths(int[] pids)
    {
        foreach (int pid in pids)
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                int max;
                try { max = dev.GetMaxFeatureReportLength(); } catch { continue; }
                if (max == RazerProtocol.BufferLength) yield return (pid, dev.DevicePath);
            }
    }

    /// <summary>Reachability probe for the dashboard's mouse selector (spec §4.4). A HID enumeration
    /// cannot answer this: the receiver stays enumerated while the mouse is asleep, so each model has
    /// to be asked. Ends by restoring the normal connection.</summary>
    public async Task<IReadOnlyList<string>> DiscoverReachableAsync(CancellationToken ct)
    {
        var reachable = new List<string>();
        try
        {
            CloseHandle();
            foreach (var m in RazerProtocol.SupportedMice)
            {
                if (await ConnectAsync(new[] { m.WiredPid, m.WirelessPid }, ct) != 0) reachable.Add(m.DisplayName);
                CloseHandle();
            }
            await EnsureConnectedAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseHandle();
            LogOnce(ex);
        }
        return reachable;
    }
```

- [ ] **Step 4: Make the battery path the one reselect trigger**

In `RazerDevice.ReadAsync`, change the single line:

```csharp
            byte tid = await EnsureConnectedAsync(ct);
```

to:

```csharp
            byte tid = await EnsureConnectedAsync(ct, reselect: true);
```

Leave `GetDpiAsync`, `SetDpiAsync`, `SetButtonAsync`, `GetButtonAsync`, and every profile call exactly as they are.

Be precise about what that buys, because the loose version of this claim hides a real hazard. Those calls never **reselect a live handle**. They can still **reconnect** when the handle has dropped — existing, wanted behaviour — and that reconnect uses preference order, so a button read mid-sweep can land on a different mouse with no battery read to announce it. `ConnectionId` is what makes that detectable; Tasks 4b and 6 are what act on it.

- [ ] **Step 5: Extend the fake**

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

- [ ] **Step 6: Build and run the whole suite**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: all green. A red build here means a caller of `FindControlPaths()` was missed — it now takes a PID array.

- [ ] **Step 7: Commit**

```bash
git add src/NagaBatteryTray/Hid tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs
git commit -m "feat(hid): preferred-mouse selection, battery-path reselect, reachability probe"
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
Expected: 3 passing.

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
- Produces: `ButtonWriteResult` and `BatteryMonitor.WriteButtonVerifiedAsync(...)`. Task 6 consumes both.

**Why this task exists.** Today `WriteVerifiedAsync` takes the read lock three separate times — once to resolve the slot, once to write, once to verify (`src/NagaBatteryTray/AppHost.cs:318-327`). A battery-path reselect can land between the resolve and the write, and then the app writes one mouse's slot number onto another. Clearing a cache cannot undo that: the slot is already a local variable. The sequence has to be atomic.

- [ ] **Step 1: Write the failing tests**

Create `tests/NagaBatteryTray.Tests/AtomicButtonWriteTests.cs`:

```csharp
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;

public sealed class AtomicButtonWriteTests
{
    private static BatteryMonitor Build(FakeRazerDevice device) =>
        new(device, new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"naga-{Guid.NewGuid():N}.json")),
            a => a());

    private static readonly byte[] F1 = { 0x00, 0x3a };

    [Fact]
    public async Task AKnownSlotIsWrittenAndVerified()
    {
        var device = new FakeRazerDevice();
        device.ButtonActions[(1, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, F1);
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(1, null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.True(r.Verified);
        Assert.Equal((byte)1, r.Slot);
        Assert.Single(device.ButtonWrites);
        Assert.Equal((byte)1, device.ButtonWrites[0].Profile);
    }

    [Fact]
    public async Task AnUnknownSlotIsResolvedInsideTheSameOperation()
    {
        var device = new FakeRazerDevice { ActiveProfile = 2 };
        device.ButtonActions[(2, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, F1);
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(null, null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.True(r.Verified);
        Assert.Equal((byte)2, r.Slot);
    }

    [Fact]
    public async Task ASlotTheMouseDoesNotHaveIsNeverWritten()
    {
        var device = new FakeRazerDevice();
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(3, new byte[] { 1 }, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.False(r.Verified);
        Assert.Empty(device.ButtonWrites);   // refused before any device call
    }

    [Fact]
    public async Task AReadBackThatDoesNotMatchFailsTheWrite()
    {
        var device = new FakeRazerDevice();
        device.ButtonActions[(1, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, new byte[] { 0x00 });
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(1, null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.False(r.Verified);
    }

    [Fact]
    public async Task AMouseChangeDuringTheOperationIsReported()
    {
        var device = new FakeRazerDevice();
        device.ButtonActions[(1, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, F1);
        device.BeforeGetButton = () => device.ConnectionId++;   // reconnect landed mid-operation
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(1, null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.True(r.ConnectionChanged);
        Assert.False(r.Verified);   // a changed connection is never a verified write
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~AtomicButtonWriteTests"`
Expected: compile error — `WriteButtonVerifiedAsync` and `BeforeGetButton` do not exist.

- [ ] **Step 3: Add the fake's hook**

In `FakeRazerDevice.GetButtonAsync`, as the first line of the method body:

```csharp
        BeforeGetButton?.Invoke();
```

and beside `GetButtonCount`:

```csharp
    /// <summary>Runs just before a button read — lets a test simulate the connection changing
    /// in the middle of a multi-step operation.</summary>
    public Action? BeforeGetButton { get; set; }
```

- [ ] **Step 4: Implement the atomic write**

Add to `BatteryMonitor.cs`:

```csharp
    /// <summary>Outcome of one atomic button write. Slot is the slot actually used (null = it could
    /// not be established). ConnectionChanged = the mouse underneath changed mid-operation, so the
    /// caller must re-seed rather than trust anything it cached.</summary>
    public readonly record struct ButtonWriteResult(bool Verified, byte? Slot, bool ConnectionChanged);

    /// <summary>Resolve the target slot, write, and read back to verify — all inside ONE read-lock
    /// acquisition. Split across three acquisitions (as it was before 2026-08-19) a battery-path
    /// reselect can land between the resolve and the write, and one mouse's slot number gets written
    /// to another; clearing a cache cannot undo that, because the slot is already a local
    /// (spec §4.10). knownSlot null = resolve it here. createdSlots null = inventory unknown, which
    /// never blocks; a known inventory that lacks the slot refuses before any device call.</summary>
    public async Task<ButtonWriteResult> WriteButtonVerifiedAsync(
        byte? knownSlot, byte[]? createdSlots, byte buttonId, byte category, byte[] data)
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return new(false, null, false); }
        int entryConnection = _device.ConnectionId;
        try
        {
            byte? slot = knownSlot ?? await _device.GetActiveProfileAsync(_cts.Token);
            if (slot is not { } s) return new(false, null, false);
            if (createdSlots is not null && Array.IndexOf(createdSlots, s) < 0) return new(false, s, false);

            bool ok = await _device.SetButtonAsync(s, buttonId, category, data, _cts.Token);
            if (ok)
            {
                var readBack = await _device.GetButtonAsync(s, buttonId, _cts.Token);
                ok = readBack is { } r && r.Category == category && r.Data.AsSpan().SequenceEqual(data);
            }
            bool changed = _device.ConnectionId != entryConnection;
            return new(ok && !changed, s, changed);
        }
        catch (OperationCanceledException) { return new(false, knownSlot, false); }
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
- Consumes: `DeviceScopedState` (Task 5), `BatteryMonitor.PreferredMouse` (Task 4), `AppSettings.PreferredMouse` (Task 1).
- Produces: `AppHost.OnDeviceStateChanged(DeviceState)`, and `_scoped` replacing `_lastActive`. Task 7 calls into the same handler for the selector.

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
            _scoped.ActiveSlot, _scoped.CreatedSlots, buttonId, category, data));

        if (r.Slot is { } used) _scoped.ActiveSlot = used;
        // the mouse changed underneath the write: nothing cached describes the device any more,
        // and the write's outcome is not attributable to either mouse (spec §4.10)
        if (r.ConnectionChanged)
        {
            _scoped.Clear();
            Interlocked.Increment(ref _gridSweep);
            if (_dashboardVm is { } changedVm) _ = ReseedDeviceAsync(changedVm);
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

Apply the same guard in `RefreshProfileAsync`: capture `_monitor.ConnectionId` before the two
profile reads and return without touching `_scoped` or the view model if it changed. The grid sweep
already has its own generation counter and keeps it.

`vm.ResetForNewMouse()` arrives in Task 7. Until then this line will not compile, so Tasks 6 and 7 are committed in that order and the build is verified at the end of Task 7.

- [ ] **Step 4: Build**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build`
Expected: one error, `'DashboardViewModel' does not contain a definition for 'ResetForNewMouse'`. That is the only acceptable error at this step; anything else is a missed `_lastActive` use.

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
- Consumes: `BatteryMonitor.DiscoverMiceAsync`, `BatteryMonitor.PreferredMouse` (Task 4); `AppSettings.ProfileNamesByMouse` (Task 1); `DeviceScopedState` (Tasks 5, 6).
- Produces: `DashboardViewModel.Mice`, `.SelectedMouse`, `.MiceLoaded`, `.MouseSelectRequested`, `.SetMice(...)`, `.ResetForNewMouse()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/NagaBatteryTray.Tests/MouseSelectorTests.cs`:

```csharp
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Ui.Dashboard;

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
        // the firmware's short keyboard form: a button with nothing assigned, and every button of a
        // profile slot the mouse has not created. Not a failure and not a Synapse macro — decoding it
        // as one painted whole grids red on the V3 (spec 2026-08-19 §1.1, §4.8).
        if (r.Category == RazerProtocol.FnKeyboard)
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
        // seed the header box with the connected mouse so it reads like the label it replaced
        // while the reachability probe runs; SetMice replaces this the moment the probe lands
        if (Mice.Count == 0 && s.Model is { } connected)
        {
            Mice.Add(connected);
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
        _syncingMouse = true;
        try { SelectedMouse = connected is not null && Mice.Contains(connected) ? connected : null; }
        finally { _syncingMouse = false; }
        MiceLoaded = Mice.Count > 0;
    }

    /// <summary>Snap the dropdown back to the mouse actually connected, after a failed pick.</summary>
    public void ResyncMouse()
    {
        _syncingMouse = true;
        try { SelectedMouse = Mice.Contains(ModelName) ? ModelName : null; }
        finally { _syncingMouse = false; }
    }

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

`SetPending()` already clears `_currentRaw` and expires the undo window, so no snapshot survives a mouse change (hard rule 2 is untouched: the snapshot/undo path itself is unchanged).

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
        var mice = await _monitor.DiscoverMiceAsync();
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
        // report the mouse the user just picked as unreachable (spec §4.5)
        await _monitor.RefreshNowWaitingAsync();

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

- [ ] **Step 8: Run the whole suite**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: all green, including `DpiPillInteractionTests` and the pre-existing dashboard tests.

- [ ] **Step 9: Render the dashboard through the UI probe and look at the header**

Run:

```powershell
$env:NAGA_UI_PROBE = "1"
$env:NAGA_UI_PROBE_OUT = "$env:TEMP\naga-header.png"
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test --filter "FullyQualifiedName~DashboardScreenshotProbe"
```

Expected: the header shows the mouse ComboBox with the link caption beneath it, and the layout is unchanged otherwise.

- [ ] **Step 10: Commit**

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

Add a short subsection covering: the preference-ordered scan (`ScanPidsFor`), the battery-path-only reselect and why no other call may scan, the reachability probe and why HID enumeration cannot replace it, `DeviceScopedState` and the defect it fixes, and the firmware's 1-byte stub for uncreated slots (with the slot table from the spec's §1.1).

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

The one partial: the reviewer asked for the connection state machine to be isolated behind a
fakeable transport. The session extracted the decision (`ConnectionPolicy.Decide`) and tests it
exhaustively, but declined to put the Win32 handle lifecycle behind an injectable seam —
refactoring transport code proven on hardware costs more risk than the coverage buys, and the
rollback it would test is instead made structural with `try/finally`. Put to the reviewer in
round 2.
