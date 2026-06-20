# Naga V2 Pro Battery Tray — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A lightweight Windows system-tray app that shows the Razer Naga V2 Pro's battery % and charging status, with a Fluent click-popup and a low-battery toast — replacing Razer Synapse for this one job.

**Architecture:** One process, two modes — an always-resident WinForms `NotifyIcon` + polling timer (near-0 idle), and an on-demand WPF popup. Layered: `RazerDevice` (HID transport primitive + battery facade) → `BatteryMonitor` (timer, state machine, low-battery edge logic) → UI consumers (`TrayIconController`, `PopupWindow`, `Notifications`). `AppHost` owns lifecycle, single-instance, run-at-login, power/session events, and an injected `ISettingsStore`.

**Tech Stack:** C# / .NET 10 (`net10.0-windows10.0.17763.0`), WPF + WinForms (NotifyIcon), HidSharp (HID), wpf-ui (Fluent popup), CommunityToolkit.WinUI.Notifications (toast). xUnit for tests.

## Global Constraints

Project-wide requirements — every task implicitly includes these (values copied verbatim from the spec):

- **Target framework:** `net10.0-windows10.0.17763.0`; `<UseWPF>true</UseWPF>` **and** `<UseWindowsForms>true</UseWindowsForms>`.
- **No admin/UAC.** Per-user only. Single instance via `Mutex` named `Local\NagaBatteryTray-b3f1c2d4-5a6e-4f80-9c1a-2e7d8b4f6a90`.
- **Display strings:** device = `Naga V2 Pro`; app/toast display name = `Naga Battery Tray`; toast AUMID = `NagaBatteryTray`.
- **HID target:** VID `0x1532`, mouse PID `0x00A8` (wireless; `0x00A7` wired fallback), vendor control collection **usage page `0xFF00`** (mandatory filter, not first-match). Query the **mouse**, never the dock `0x00A4`.
- **Razer report:** 90 bytes; HID **feature** buffer is 91 bytes (`buffer[0]=0x00` report id, then the 90). **`buffer[i] = report byte[i-1]`.**
- **Commands:** battery class `0x07` id `0x80`; charging class `0x07` id `0x84`; `data_size = 0x02`.
- **CRC:** `crc = 0; for (i = 2; i <= 87; i++) crc ^= report[i];` Battery-query CRC = `0x85`, charging-query CRC = `0x81`.
- **Transaction id:** `0x1f` (probe set `0x1f,0x3f,0x00,0xff,0x08,0x88,0x1d,0x9f`); `cachedTransactionId` default `null` = unprobed.
- **Reply parse:** `status = buffer[1]`; reply CRC = XOR `buffer[3..88]` vs `buffer[89]`; value = `buffer[10]`; battery `percent = round(value * 100 / 255)`.
- **Poll cadence:** 60 s; 15 s while charging. SET→GET delay default 400 ms, floor ~150 ms.
- **Low-battery:** inclusive threshold (fires at `percent <= threshold`, default 15); charging suppresses firing and does not re-arm; re-arm only when `percent > threshold` again. Staleness: > 3 consecutive missed reads → Unknown.
- **DRY, YAGNI, TDD, frequent commits.** Conventional-commit messages.

---

## File Structure

```
naga-battery-tray/
  NagaBatteryTray.sln
  src/NagaBatteryTray/
    NagaBatteryTray.csproj
    app.manifest                       PerMonitorV2 DPI awareness
    Program.cs                         entry point: STAThread, mutex, --probe dispatch, WPF Application host
    AppHost.cs                         lifecycle, DI wiring, run-at-login menu, power/session events, shutdown
    Settings/AppSettings.cs            settings POCO (defaults)
    Settings/ISettingsStore.cs         typed load/save + cached-transaction-id accessors
    Settings/JsonSettingsStore.cs      JSON impl at %APPDATA%\NagaBatteryTray\settings.json
    Hid/RazerProtocol.cs               constants, report builder, CRC, reply parse (PURE)
    Hid/BatteryReading.cs              record struct {Percent, IsCharging, IsPresent, Timestamp}
    Hid/IRazerDevice.cs                ReadAsync(ct) : IDisposable
    Hid/RazerDevice.cs                 HidSharp transport + battery facade + probe + error handling
    Monitoring/DeviceState.cs          {Status: Unknown|Online, Percent, Charging}
    Monitoring/BatteryMonitor.cs       timer, semaphore, ProcessReading state machine, events
    Ui/IconRenderer.cs                 GDI+ % bitmap, DPI sizing, dynamic color (color map PURE)
    Ui/TrayIconController.cs           NotifyIcon, tooltip, menu, left-click, DestroyIcon hygiene
    Ui/PopupPlacement.cs               multi-monitor/DPI placement math (PURE)
    Ui/PopupWindow.xaml(.cs)           WPF FluentWindow, A+D hybrid
    Ui/PopupViewModel.cs               popup bindings
    Ui/Notifications.cs                low-battery toast
    Startup/StartupRegistration.cs     HKCU Run enable/disable/query
    Diagnostics/ProbeCommand.cs        --probe diagnostic (enumerate, try transaction ids, print)
  tests/NagaBatteryTray.Tests/
    NagaBatteryTray.Tests.csproj
    Fakes/FakeRazerDevice.cs
    RazerProtocolTests.cs
    SettingsStoreTests.cs
    BatteryMonitorTests.cs
    IconColorTests.cs
    PopupPlacementTests.cs
    StartupRegistrationTests.cs
```

---

## Task 1: Solution & project scaffold

**Files:**
- Create: `NagaBatteryTray.sln`
- Create: `src/NagaBatteryTray/NagaBatteryTray.csproj`
- Create: `src/NagaBatteryTray/app.manifest`
- Create: `src/NagaBatteryTray/Program.cs`
- Create: `tests/NagaBatteryTray.Tests/NagaBatteryTray.Tests.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces: a buildable solution; `Program.Main` entry point; a test project that runs (0 tests).

- [ ] **Step 1: Create the app csproj**

`src/NagaBatteryTray/NagaBatteryTray.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <RootNamespace>NagaBatteryTray</RootNamespace>
    <AssemblyName>NagaBatteryTray</AssemblyName>
    <StartupObject>NagaBatteryTray.Program</StartupObject>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="HidSharp" Version="2.6.4" />
    <PackageReference Include="WPF-UI" Version="4.3.0" />
    <PackageReference Include="CommunityToolkit.WinUI.Notifications" Version="7.1.2" />
  </ItemGroup>
</Project>
```
> **Versions verified current as of June 2026:** .NET 10 SDK (LTS → Nov 2028), HidSharp 2.6.4, WPF-UI 4.3.0, CommunityToolkit.WinUI.Notifications 7.1.2. During execution prefer `dotnet add package <name>` (no version) to lock the current stable, and scaffold the test project via `dotnet new xunit` so its tooling matches the installed SDK. WPF-UI 4.x kept the core controls/theming in the `wpf-ui` package (abstractions/DI split into separate packages we don't reference) — confirm `FluentWindow` / `ApplicationThemeManager` / `Markup.ThemesDictionary` / `Markup.ControlsDictionary` names at first build.

- [ ] **Step 2: Create the DPI manifest**

`src/NagaBatteryTray/app.manifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

- [ ] **Step 3: Create a minimal Program.cs that compiles**

`src/NagaBatteryTray/Program.cs`:
```csharp
namespace NagaBatteryTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        return 0;
    }
}
```

- [ ] **Step 4: Create the test csproj**

`tests/NagaBatteryTray.Tests/NagaBatteryTray.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\NagaBatteryTray\NagaBatteryTray.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Create the solution and add projects**

Run:
```bash
cd /c/Users/Brandon/naga-battery-tray
dotnet new sln -n NagaBatteryTray
dotnet sln add src/NagaBatteryTray/NagaBatteryTray.csproj
dotnet sln add tests/NagaBatteryTray.Tests/NagaBatteryTray.Tests.csproj
```

- [ ] **Step 6: Build to verify the scaffold**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Run the (empty) test project**

Run: `dotnet test`
Expected: "No test is available" / 0 tests — but the command succeeds.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "chore: scaffold NagaBatteryTray solution and projects"
```

---

## Task 2: Settings store

**Files:**
- Create: `src/NagaBatteryTray/Settings/AppSettings.cs`
- Create: `src/NagaBatteryTray/Settings/ISettingsStore.cs`
- Create: `src/NagaBatteryTray/Settings/JsonSettingsStore.cs`
- Test: `tests/NagaBatteryTray.Tests/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `AppSettings` POCO with the §8 fields/defaults.
  - `interface ISettingsStore { AppSettings Settings { get; } void Save(); byte? GetCachedTransactionId(); void SetCachedTransactionId(byte id); }`
  - `JsonSettingsStore(string filePath)` implementing it.

- [ ] **Step 1: Write the failing tests**

`tests/NagaBatteryTray.Tests/SettingsStoreTests.cs`:
```csharp
using NagaBatteryTray.Settings;
using Xunit;

public class SettingsStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"naga-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void Missing_file_yields_defaults_and_writes_them()
    {
        var path = TempFile();
        var store = new JsonSettingsStore(path);

        Assert.Equal(60, store.Settings.PollIntervalSeconds);
        Assert.Equal(15, store.Settings.PollIntervalChargingSeconds);
        Assert.Equal(15, store.Settings.LowBatteryThreshold);
        Assert.True(store.Settings.LowBatteryNotify);
        Assert.Null(store.Settings.CachedTransactionId);
        Assert.Equal(400, store.Settings.SetReadDelayMs);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void GetCachedTransactionId_is_null_when_unprobed()
    {
        var store = new JsonSettingsStore(TempFile());
        Assert.Null(store.GetCachedTransactionId());
    }

    [Fact]
    public void SetCachedTransactionId_persists_and_parses_hex()
    {
        var path = TempFile();
        new JsonSettingsStore(path).SetCachedTransactionId(0x1f);

        var reloaded = new JsonSettingsStore(path);
        Assert.Equal((byte)0x1f, reloaded.GetCachedTransactionId());
        Assert.Equal("0x1f", reloaded.Settings.CachedTransactionId);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter SettingsStoreTests`
Expected: FAIL — `JsonSettingsStore`/`AppSettings` do not exist (compile error).

- [ ] **Step 3: Implement AppSettings**

`src/NagaBatteryTray/Settings/AppSettings.cs`:
```csharp
namespace NagaBatteryTray.Settings;

public sealed class AppSettings
{
    public int PollIntervalSeconds { get; set; } = 60;
    public int PollIntervalChargingSeconds { get; set; } = 15;
    public int LowBatteryThreshold { get; set; } = 15;
    public bool LowBatteryNotify { get; set; } = true;
    public string? CachedTransactionId { get; set; } = null; // e.g. "0x1f"; null = unprobed
    public int SetReadDelayMs { get; set; } = 400;
}
```

- [ ] **Step 4: Implement ISettingsStore**

`src/NagaBatteryTray/Settings/ISettingsStore.cs`:
```csharp
namespace NagaBatteryTray.Settings;

public interface ISettingsStore
{
    AppSettings Settings { get; }
    void Save();
    byte? GetCachedTransactionId();
    void SetCachedTransactionId(byte id);
}
```

- [ ] **Step 5: Implement JsonSettingsStore**

`src/NagaBatteryTray/Settings/JsonSettingsStore.cs`:
```csharp
using System.Text.Json;

namespace NagaBatteryTray.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public AppSettings Settings { get; }

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
                Settings = new AppSettings(); // corrupt file → defaults
            }
        }
        else
        {
            Settings = new AppSettings();
            Save();
        }
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NagaBatteryTray", "settings.json");

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(Settings, Options));
    }

    public byte? GetCachedTransactionId()
    {
        var s = Settings.CachedTransactionId;
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return Convert.ToByte(s, 16); } catch { return null; }
    }

    public void SetCachedTransactionId(byte id)
    {
        Settings.CachedTransactionId = $"0x{id:x2}";
        Save();
    }
}
```
> `Convert.ToByte("0x1f", 16)` handles the `0x` prefix in .NET.

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test --filter SettingsStoreTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: typed JSON settings store with cached-transaction-id accessors"
```

---

## Task 3: Razer report builder + CRC

**Files:**
- Create: `src/NagaBatteryTray/Hid/RazerProtocol.cs`
- Test: `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (all `static` on `RazerProtocol`):
  - constants `VendorId=0x1532`, `MousePidWireless=0x00A8`, `MousePidWired=0x00A7`, `UsagePageVendor=0xFF00`, `CommandClassPower=0x07`, `CommandIdBattery=0x80`, `CommandIdCharging=0x84`, `byte[] TransactionIdProbeSet`.
  - `byte ComputeCrc(byte[] report90)` — XOR of report[2..87].
  - `byte[] BuildFeatureBuffer(byte transactionId, byte commandId)` — returns the 91-byte feature buffer for a power-class query.

- [ ] **Step 1: Write the failing tests**

`tests/NagaBatteryTray.Tests/RazerProtocolTests.cs`:
```csharp
using NagaBatteryTray.Hid;
using Xunit;

public class RazerProtocolTests
{
    [Fact]
    public void Battery_query_buffer_has_correct_layout_and_crc()
    {
        byte[] buf = RazerProtocol.BuildFeatureBuffer(0x1f, RazerProtocol.CommandIdBattery);

        Assert.Equal(91, buf.Length);
        Assert.Equal(0x00, buf[0]);  // report id
        Assert.Equal(0x00, buf[1]);  // status (report[0])
        Assert.Equal(0x1f, buf[2]);  // transaction_id (report[1])
        Assert.Equal(0x02, buf[6]);  // data_size (report[5])
        Assert.Equal(0x07, buf[7]);  // command_class (report[6])
        Assert.Equal(0x80, buf[8]);  // command_id (report[7])
        Assert.Equal(0x85, buf[89]); // crc (report[88])
        Assert.Equal(0x00, buf[90]); // reserved (report[89])
    }

    [Fact]
    public void Charging_query_crc_is_0x81()
    {
        byte[] buf = RazerProtocol.BuildFeatureBuffer(0x1f, RazerProtocol.CommandIdCharging);
        Assert.Equal(0x84, buf[8]);   // command_id
        Assert.Equal(0x81, buf[89]);  // crc
    }

    [Fact]
    public void Crc_is_xor_of_bytes_2_to_87()
    {
        var report = new byte[90];
        report[5] = 0x02; report[6] = 0x07; report[7] = 0x80;
        Assert.Equal((byte)0x85, RazerProtocol.ComputeCrc(report));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter RazerProtocolTests`
Expected: FAIL — `RazerProtocol` does not exist.

- [ ] **Step 3: Implement RazerProtocol (builder + CRC)**

`src/NagaBatteryTray/Hid/RazerProtocol.cs`:
```csharp
namespace NagaBatteryTray.Hid;

public static class RazerProtocol
{
    public const int VendorId = 0x1532;
    public const int MousePidWireless = 0x00A8;
    public const int MousePidWired = 0x00A7;
    public const int UsagePageVendor = 0xFF00;

    public const int ReportLength = 90;
    public const int BufferLength = 91; // report id + 90

    public const byte CommandClassPower = 0x07;
    public const byte CommandIdBattery = 0x80;
    public const byte CommandIdCharging = 0x84;
    public const byte DataSize = 0x02;

    public static readonly byte[] TransactionIdProbeSet =
        { 0x1f, 0x3f, 0x00, 0xff, 0x08, 0x88, 0x1d, 0x9f };

    /// <summary>XOR of report bytes [2..87] inclusive.</summary>
    public static byte ComputeCrc(byte[] report90)
    {
        byte crc = 0;
        for (int i = 2; i <= 87; i++) crc ^= report90[i];
        return crc;
    }

    /// <summary>Builds the 91-byte HID feature buffer (report id 0x00 + 90-byte report) for a power-class query.</summary>
    public static byte[] BuildFeatureBuffer(byte transactionId, byte commandId)
    {
        var report = new byte[ReportLength];
        report[0] = 0x00;               // status
        report[1] = transactionId;      // transaction_id
        report[5] = DataSize;           // data_size
        report[6] = CommandClassPower;  // command_class
        report[7] = commandId;          // command_id
        report[88] = ComputeCrc(report);
        report[89] = 0x00;              // reserved

        var buffer = new byte[BufferLength];
        buffer[0] = 0x00;               // HID report id
        Array.Copy(report, 0, buffer, 1, ReportLength);
        return buffer;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test --filter RazerProtocolTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: Razer report builder and XOR CRC (battery 0x85 / charging 0x81)"
```

---

## Task 4: Reply parsing & validation

**Files:**
- Modify: `src/NagaBatteryTray/Hid/RazerProtocol.cs` (add reply parsing)
- Test: `tests/NagaBatteryTray.Tests/RazerProtocolTests.cs` (add cases)

**Interfaces:**
- Consumes: `RazerProtocol.ComputeCrc`.
- Produces:
  - `enum ReplyResult { Success, Busy, Failed }`
  - `static ReplyResult ParseReply(byte[] buffer91, out byte value)` — validates `status==0x02` and reply CRC over `buffer[3..88]==buffer[89]`; `0x01`→Busy; else Failed. `value = buffer[10]` on Success.
  - `static int ScaleBattery(byte raw)` — `round(raw*100/255)`.

- [ ] **Step 1: Write the failing tests (append to RazerProtocolTests.cs)**

```csharp
    private static byte[] MakeReply(byte status, byte value)
    {
        var buf = new byte[91];
        buf[1] = status;        // report[0]
        buf[2] = 0x1f;          // transaction_id
        buf[6] = 0x02;          // data_size
        buf[7] = 0x07;          // class
        buf[8] = 0x80;          // id
        buf[10] = value;        // report[9] data byte
        // reply crc over buffer[3..88] -> buffer[89]
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        buf[89] = crc;
        return buf;
    }

    [Fact]
    public void ParseReply_success_returns_value()
    {
        var reply = MakeReply(0x02, 220);
        var result = RazerProtocol.ParseReply(reply, out byte value);
        Assert.Equal(ReplyResult.Success, result);
        Assert.Equal((byte)220, value);
    }

    [Fact]
    public void ParseReply_busy_status_is_busy()
    {
        Assert.Equal(ReplyResult.Busy, RazerProtocol.ParseReply(MakeReply(0x01, 0), out _));
    }

    [Fact]
    public void ParseReply_bad_crc_is_failed()
    {
        var reply = MakeReply(0x02, 100);
        reply[89] ^= 0xFF; // corrupt crc
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseReply(reply, out _));
    }

    [Theory]
    [InlineData(255, 100)]
    [InlineData(0, 0)]
    [InlineData(220, 86)]   // 220*100/255 = 86.27 -> 86
    public void ScaleBattery_maps_0_255_to_percent(byte raw, int expected)
    {
        Assert.Equal(expected, RazerProtocol.ScaleBattery(raw));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter RazerProtocolTests`
Expected: FAIL — `ParseReply`/`ReplyResult`/`ScaleBattery` not defined.

- [ ] **Step 3: Implement reply parsing (append to RazerProtocol.cs)**

```csharp
public enum ReplyResult { Success, Busy, Failed }
```
Add to `RazerProtocol`:
```csharp
    public static ReplyResult ParseReply(byte[] buffer91, out byte value)
    {
        value = 0;
        byte status = buffer91[1];
        if (status == 0x01) return ReplyResult.Busy;
        if (status != 0x02) return ReplyResult.Failed;

        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buffer91[i]; // reply report[2..87]
        if (crc != buffer91[89]) return ReplyResult.Failed;

        value = buffer91[10]; // report byte[9]
        return ReplyResult.Success;
    }

    public static int ScaleBattery(byte raw) => (int)Math.Round(raw * 100.0 / 255.0);
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test --filter RazerProtocolTests`
Expected: PASS (all RazerProtocol tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: Razer reply parsing, CRC validation, and battery scaling"
```

---

## Task 5: RazerDevice (HID) + `--probe` diagnostic — hardware de-risk milestone

> This is the milestone that confirms the §11 empirical unknowns on the real mouse. Build the device layer + a `--probe` command, then **run it against the actual Naga before continuing**.

**Files:**
- Create: `src/NagaBatteryTray/Hid/BatteryReading.cs`
- Create: `src/NagaBatteryTray/Hid/IRazerDevice.cs`
- Create: `src/NagaBatteryTray/Hid/RazerDevice.cs`
- Create: `src/NagaBatteryTray/Diagnostics/ProbeCommand.cs`
- Modify: `src/NagaBatteryTray/Program.cs` (dispatch `--probe`)

**Interfaces:**
- Consumes: `RazerProtocol`, `ISettingsStore`.
- Produces:
  - `readonly record struct BatteryReading(int Percent, bool IsCharging, bool IsPresent, DateTimeOffset Timestamp)`
  - `interface IRazerDevice : IDisposable { Task<BatteryReading> ReadAsync(CancellationToken ct); }`
  - `RazerDevice(ISettingsStore settings)` implementing it.
  - `ProbeCommand.Run()` returning an int exit code and printing findings.

- [ ] **Step 1: Define BatteryReading and IRazerDevice**

`src/NagaBatteryTray/Hid/BatteryReading.cs`:
```csharp
namespace NagaBatteryTray.Hid;

public readonly record struct BatteryReading(int Percent, bool IsCharging, bool IsPresent, DateTimeOffset Timestamp)
{
    public static BatteryReading Absent(DateTimeOffset now) => new(0, false, false, now);
}
```

`src/NagaBatteryTray/Hid/IRazerDevice.cs`:
```csharp
namespace NagaBatteryTray.Hid;

public interface IRazerDevice : IDisposable
{
    Task<BatteryReading> ReadAsync(CancellationToken ct);
}
```

- [ ] **Step 2: Implement RazerDevice (HidSharp transport + facade)**

`src/NagaBatteryTray/Hid/RazerDevice.cs`:
```csharp
using HidSharp;
using HidSharp.Reports;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Hid;

public sealed class RazerDevice : IRazerDevice
{
    private readonly ISettingsStore _settings;
    private HidStream? _stream;
    private bool _loggedError;

    public RazerDevice(ISettingsStore settings) => _settings = settings;

    public async Task<BatteryReading> ReadAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        try
        {
            if (!EnsureOpen()) return BatteryReading.Absent(now);

            byte tid = await ResolveTransactionIdAsync(ct);
            if (tid == 0) return BatteryReading.Absent(now);

            var battery = await QueryAsync(tid, RazerProtocol.CommandIdBattery, ct);
            if (battery is null) return BatteryReading.Absent(now);

            var charging = await QueryAsync(tid, RazerProtocol.CommandIdCharging, ct);
            // charging is best-effort; if it fails, default to false but still report battery
            int percent = RazerProtocol.ScaleBattery(battery.Value);
            bool isCharging = charging is not null && charging.Value != 0;
            _loggedError = false;
            return new BatteryReading(percent, isCharging, true, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            CloseStream();
            LogOnce(ex);
            return BatteryReading.Absent(now);
        }
    }

    private bool EnsureOpen()
    {
        if (_stream is not null) return true;
        var device = FindControlDevice();
        if (device is null) return false;
        if (!device.TryOpen(out _stream)) { _stream = null; return false; }
        _stream.ReadTimeout = 1000;
        _stream.WriteTimeout = 1000;
        return true;
    }

    /// <summary>Enumerate VID 0x1532 mouse PIDs and pick the vendor 0xFF00 control collection.</summary>
    private static HidDevice? FindControlDevice()
    {
        foreach (int pid in new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidWired })
        {
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                if (HasVendorUsagePage(dev)) return dev;
            }
        }
        return null;
    }

    private static bool HasVendorUsagePage(HidDevice dev)
    {
        try
        {
            var descriptor = dev.GetReportDescriptor();
            foreach (var item in descriptor.DeviceItems)
                foreach (uint usage in item.Usages.GetAllValues())
                    if ((usage >> 16) == RazerProtocol.UsagePageVendor) return true;
        }
        catch { /* some collections refuse descriptor read */ }
        return false;
    }

    /// <summary>Returns cached id, else probes the set and caches the winner. 0 = could not resolve.</summary>
    private async Task<byte> ResolveTransactionIdAsync(CancellationToken ct)
    {
        var cached = _settings.GetCachedTransactionId();
        if (cached is not null) return cached.Value;

        foreach (byte tid in RazerProtocol.TransactionIdProbeSet)
        {
            var value = await QueryAsync(tid, RazerProtocol.CommandIdBattery, ct);
            if (value is not null && value.Value <= 255 && RazerProtocol.ScaleBattery(value.Value) is >= 0 and <= 100)
            {
                _settings.SetCachedTransactionId(tid);
                return tid;
            }
        }
        return 0;
    }

    /// <summary>One SET→wait→GET round-trip with one busy retry. Returns the data byte or null on failure.</summary>
    private async Task<byte?> QueryAsync(byte transactionId, byte commandId, CancellationToken ct)
    {
        if (_stream is null) return null;
        var buffer = RazerProtocol.BuildFeatureBuffer(transactionId, commandId);
        _stream.SetFeature(buffer);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(_settings.Settings.SetReadDelayMs, ct);
            var reply = new byte[RazerProtocol.BufferLength];
            reply[0] = 0x00;
            _stream.GetFeature(reply);

            var result = RazerProtocol.ParseReply(reply, out byte value);
            if (result == ReplyResult.Success) return value;
            if (result == ReplyResult.Failed) return null;
            // Busy: loop once more with a shorter extra wait
            await Task.Delay(200, ct);
        }
        return null;
    }

    private void CloseStream() { _stream?.Dispose(); _stream = null; }

    private void LogOnce(Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        Console.Error.WriteLine($"[RazerDevice] {ex.GetType().Name}: {ex.Message}");
    }

    public void Dispose() => CloseStream();
}
```
> **API note for the implementer:** the `0xFF00` filter uses HidSharp's `GetReportDescriptor().DeviceItems[*].Usages.GetAllValues()` (a `uint` whose high word is the usage page). If your HidSharp version exposes a simpler top-level-usage accessor, use it — the filter requirement (vendor `0xFF00`, not first-match) is what matters. Confirm `SetFeature`/`GetFeature` buffer length equals `GetMaxFeatureReportLength()` (expect 91) during Step 5.

- [ ] **Step 3: Implement the probe command**

`src/NagaBatteryTray/Diagnostics/ProbeCommand.cs`:
```csharp
using HidSharp;
using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Diagnostics;

public static class ProbeCommand
{
    public static int Run()
    {
        Console.WriteLine("Naga Battery Tray — HID probe");
        Console.WriteLine("Enumerating VID 0x1532 devices:");
        foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId))
        {
            int maxFeature = -1;
            try { maxFeature = dev.GetMaxFeatureReportLength(); } catch { }
            Console.WriteLine($"  PID 0x{dev.ProductID:x4}  maxFeature={maxFeature}  {dev.GetFriendlyName() ?? "?"}");
        }

        foreach (int pid in new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidWired })
        {
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                if (!dev.TryOpen(out var stream)) continue;
                using (stream)
                {
                    foreach (byte tid in RazerProtocol.TransactionIdProbeSet)
                    {
                        var battery = OneShot(stream, tid, RazerProtocol.CommandIdBattery);
                        if (battery is null) continue;
                        var charging = OneShot(stream, tid, RazerProtocol.CommandIdCharging);
                        Console.WriteLine(
                            $"PID 0x{pid:x4} transaction 0x{tid:x2} => battery raw {battery} " +
                            $"({RazerProtocol.ScaleBattery(battery.Value)}%), charging={(charging is null ? "?" : (charging != 0).ToString())}");
                    }
                }
            }
        }
        Console.WriteLine("Done. The transaction id whose battery % is plausible is the right one.");
        return 0;
    }

    private static byte? OneShot(HidStream stream, byte tid, byte commandId)
    {
        try
        {
            stream.SetFeature(RazerProtocol.BuildFeatureBuffer(tid, commandId));
            Thread.Sleep(400);
            var reply = new byte[RazerProtocol.BufferLength];
            stream.GetFeature(reply);
            return RazerProtocol.ParseReply(reply, out byte value) == ReplyResult.Success ? value : null;
        }
        catch { return null; }
    }
}
```

- [ ] **Step 4: Wire `--probe` into Program.cs**

Replace `src/NagaBatteryTray/Program.cs` body of `Main`:
```csharp
        if (args.Length > 0 && args[0] == "--probe")
        {
            AllocConsoleIfNeeded();
            return NagaBatteryTray.Diagnostics.ProbeCommand.Run();
        }
        return 0;
```
Add helper to `Program`:
```csharp
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    private static void AllocConsoleIfNeeded()
    {
        if (Console.OpenStandardOutput() == Stream.Null) AllocConsole();
    }
```
> A WinExe has no console by default; `AllocConsole` gives `--probe` visible output when launched from a terminal that didn't attach one. If you run `--probe` from an existing console it already prints.

- [ ] **Step 5: Build, then run the probe against the real mouse**

Run:
```bash
dotnet build
dotnet run --project src/NagaBatteryTray -- --probe
```
Expected (with the Naga awake/connected): a line like
`PID 0x00a8 transaction 0x1f => battery raw 222 (87%), charging=True`
**Confirm:** the plausible % matches reality, transaction `0x1f` wins, `maxFeature` is 91, and charging flips correctly when you lift/seat the mouse on the dock. Record the winning transaction id. If `0x00A8` doesn't answer but another PID does, note it — update `RazerProtocol.MousePid*` accordingly.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: RazerDevice HID transport + --probe diagnostic (verified on hardware)"
```

---

## Task 6: BatteryMonitor (state machine)

**Files:**
- Create: `src/NagaBatteryTray/Monitoring/DeviceState.cs`
- Create: `src/NagaBatteryTray/Monitoring/BatteryMonitor.cs`
- Create: `tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs`
- Test: `tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs`

**Interfaces:**
- Consumes: `IRazerDevice`, `ISettingsStore`, `BatteryReading`.
- Produces:
  - `enum DeviceStatus { Unknown, Online }`
  - `readonly record struct DeviceState(DeviceStatus Status, int Percent, bool Charging)` with `DeviceState.Unknown` and `DeviceState.Online(int,bool)` factories.
  - `BatteryMonitor(IRazerDevice device, ISettingsStore settings, Action<Action> dispatch)` with `DeviceState State`, `event EventHandler<DeviceState> StateChanged`, `event EventHandler<int> LowBatteryCrossed`, `void Start()`, `Task RefreshNowAsync()`, `Dispose()`, and internal `void ProcessReading(BatteryReading)`.

- [ ] **Step 1: Write the failing tests**

`tests/NagaBatteryTray.Tests/Fakes/FakeRazerDevice.cs`:
```csharp
using NagaBatteryTray.Hid;

public sealed class FakeRazerDevice : IRazerDevice
{
    private readonly Queue<BatteryReading> _queue = new();
    public void Enqueue(BatteryReading r) => _queue.Enqueue(r);
    public Task<BatteryReading> ReadAsync(CancellationToken ct) =>
        Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : BatteryReading.Absent(DateTimeOffset.Now));
    public void Dispose() { }
}
```

`tests/NagaBatteryTray.Tests/BatteryMonitorTests.cs`:
```csharp
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using Xunit;

public class BatteryMonitorTests
{
    private static BatteryMonitor NewMonitor(out List<int> lowFires, ISettingsStore? store = null)
    {
        store ??= new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"naga-{Guid.NewGuid():N}.json"));
        var monitor = new BatteryMonitor(new FakeRazerDevice(), store, a => a()); // synchronous dispatch
        var fires = new List<int>();
        monitor.LowBatteryCrossed += (_, pct) => fires.Add(pct);
        lowFires = fires;
        return monitor;
    }

    private static BatteryReading Online(int pct, bool charging) =>
        new(pct, charging, true, DateTimeOffset.Now);

    [Fact]
    public void Online_reading_sets_online_state()
    {
        var m = NewMonitor(out _);
        m.ProcessReading(Online(87, true));
        Assert.Equal(DeviceStatus.Online, m.State.Status);
        Assert.Equal(87, m.State.Percent);
        Assert.True(m.State.Charging);
    }

    [Fact]
    public void Low_battery_fires_once_at_or_below_threshold_while_discharging()
    {
        var m = NewMonitor(out var fires);
        m.ProcessReading(Online(80, false)); // armed
        m.ProcessReading(Online(15, false)); // fire (inclusive)
        m.ProcessReading(Online(10, false)); // no second fire
        Assert.Equal(new[] { 15 }, fires);
    }

    [Fact]
    public void Charging_suppresses_and_does_not_rearm()
    {
        var m = NewMonitor(out var fires);
        m.ProcessReading(Online(12, true));  // plugged in below threshold: no fire
        m.ProcessReading(Online(12, false)); // unplugged still below: no fire (never recovered)
        Assert.Empty(fires);
    }

    [Fact]
    public void Rearms_only_after_recovering_above_threshold()
    {
        var m = NewMonitor(out var fires);
        m.ProcessReading(Online(80, false)); // armed
        m.ProcessReading(Online(15, false)); // fire
        m.ProcessReading(Online(50, false)); // re-arm (>threshold)
        m.ProcessReading(Online(14, false)); // fire again
        Assert.Equal(new[] { 15, 14 }, fires);
    }

    [Fact]
    public void Staleness_goes_unknown_after_more_than_three_misses()
    {
        var m = NewMonitor(out _);
        m.ProcessReading(Online(50, false));
        var absent = BatteryReading.Absent(DateTimeOffset.Now);
        m.ProcessReading(absent); // miss 1 — keep last
        Assert.Equal(DeviceStatus.Online, m.State.Status);
        m.ProcessReading(absent); // 2
        m.ProcessReading(absent); // 3
        m.ProcessReading(absent); // 4 (>3) -> Unknown
        Assert.Equal(DeviceStatus.Unknown, m.State.Status);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter BatteryMonitorTests`
Expected: FAIL — `DeviceState`/`BatteryMonitor` not defined.

- [ ] **Step 3: Implement DeviceState**

`src/NagaBatteryTray/Monitoring/DeviceState.cs`:
```csharp
namespace NagaBatteryTray.Monitoring;

public enum DeviceStatus { Unknown, Online }

public readonly record struct DeviceState(DeviceStatus Status, int Percent, bool Charging)
{
    public static DeviceState Unknown { get; } = new(DeviceStatus.Unknown, 0, false);
    public static DeviceState Online(int percent, bool charging) => new(DeviceStatus.Online, percent, charging);
}
```

- [ ] **Step 4: Implement BatteryMonitor**

`src/NagaBatteryTray/Monitoring/BatteryMonitor.cs`:
```csharp
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Monitoring;

public sealed class BatteryMonitor : IDisposable
{
    private readonly IRazerDevice _device;
    private readonly ISettingsStore _settings;
    private readonly Action<Action> _dispatch;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private Timer? _timer;
    private bool _armed = true;
    private int _consecutiveMisses;

    public DeviceState State { get; private set; } = DeviceState.Unknown;
    public event EventHandler<DeviceState>? StateChanged;
    public event EventHandler<int>? LowBatteryCrossed;

    public BatteryMonitor(IRazerDevice device, ISettingsStore settings, Action<Action> dispatch)
    {
        _device = device;
        _settings = settings;
        _dispatch = dispatch;
    }

    public void Start()
    {
        _timer = new Timer(_ => _ = PollAsync(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    public Task RefreshNowAsync() => PollAsync();

    private async Task PollAsync()
    {
        if (!await _readLock.WaitAsync(0)) return; // a read is already in flight; skip
        try
        {
            var reading = await _device.ReadAsync(_cts.Token);
            ProcessReading(reading);
            ScheduleNext(reading);
        }
        catch (OperationCanceledException) { }
        finally { _readLock.Release(); }
    }

    private void ScheduleNext(BatteryReading reading)
    {
        int seconds = reading is { IsPresent: true, IsCharging: true }
            ? _settings.Settings.PollIntervalChargingSeconds
            : _settings.Settings.PollIntervalSeconds;
        _timer?.Change(TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
    }

    internal void ProcessReading(BatteryReading r)
    {
        int threshold = _settings.Settings.LowBatteryThreshold;

        if (!r.IsPresent)
        {
            _consecutiveMisses++;
            if (_consecutiveMisses > 3) SetState(DeviceState.Unknown);
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

        SetState(DeviceState.Online(r.Percent, r.IsCharging));
    }

    private void SetState(DeviceState next)
    {
        if (next == State) return;
        State = next;
        _dispatch(() => StateChanged?.Invoke(this, next));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _readLock.Wait(1000); // let any in-flight read finish before the device is disposed elsewhere
        _readLock.Release();
        _cts.Dispose();
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test --filter BatteryMonitorTests`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: BatteryMonitor state machine with low-battery edge logic and staleness"
```

---

## Task 7: IconRenderer (dynamic color + DPI bitmap)

**Files:**
- Create: `src/NagaBatteryTray/Ui/IconRenderer.cs`
- Test: `tests/NagaBatteryTray.Tests/IconColorTests.cs`

**Interfaces:**
- Consumes: `DeviceState`.
- Produces:
  - `static System.Drawing.Color IconRenderer.ColorForLevel(int percent, bool charging)` — green > 50, amber 21–50, red ≤ 20; charging → Razer green.
  - `System.Drawing.Icon IconRenderer.Render(DeviceState state, int dpi)` — `–` when Unknown; the number otherwise; **caller must `DestroyIcon` the previous handle** (Task 8).

- [ ] **Step 1: Write the failing color tests**

`tests/NagaBatteryTray.Tests/IconColorTests.cs`:
```csharp
using System.Drawing;
using NagaBatteryTray.Ui;
using Xunit;

public class IconColorTests
{
    [Theory]
    [InlineData(87, false, 0x44, 0xD6, 0x2C)] // healthy green
    [InlineData(40, false, 0xE0, 0xA2, 0x3E)] // amber
    [InlineData(10, false, 0xE0, 0x47, 0x3E)] // red
    public void ColorForLevel_maps_bands(int pct, bool charging, int r, int g, int b)
    {
        var c = IconRenderer.ColorForLevel(pct, charging);
        Assert.Equal((r, g, b), (c.R, c.G, c.B));
    }

    [Fact]
    public void Charging_is_always_razer_green()
    {
        var c = IconRenderer.ColorForLevel(10, true); // low but charging
        Assert.Equal((0x44, 0xD6, 0x2C), (c.R, c.G, c.B));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter IconColorTests`
Expected: FAIL — `IconRenderer` not defined.

- [ ] **Step 3: Implement IconRenderer**

`src/NagaBatteryTray/Ui/IconRenderer.cs`:
```csharp
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using NagaBatteryTray.Monitoring;

namespace NagaBatteryTray.Ui;

public static class IconRenderer
{
    private static readonly Color Green = Color.FromArgb(0x44, 0xD6, 0x2C);
    private static readonly Color Amber = Color.FromArgb(0xE0, 0xA2, 0x3E);
    private static readonly Color Red = Color.FromArgb(0xE0, 0x47, 0x3E);

    public static Color ColorForLevel(int percent, bool charging)
    {
        if (charging) return Green;
        if (percent <= 20) return Red;
        if (percent <= 50) return Amber;
        return Green;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static void Destroy(Icon? icon)
    {
        if (icon is not null) DestroyIcon(icon.Handle);
    }

    public static Icon Render(DeviceState state, int dpi)
    {
        int size = Math.Max(16, dpi * 16 / 96); // SM_CXSMICON scales with DPI
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            string text = state.Status == DeviceStatus.Unknown ? "–" : state.Percent.ToString();
            Color color = state.Status == DeviceStatus.Unknown
                ? Color.Gray
                : ColorForLevel(state.Percent, state.Charging);

            float emSize = text.Length >= 3 ? size * 0.5f : size * 0.72f;
            using var font = new Font("Segoe UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, brush, new RectangleF(0, 0, size, size), fmt);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test --filter IconColorTests`
Expected: PASS (4 tests). (Bitmap rendering is verified manually in Task 8.)

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: tray icon renderer with dynamic-by-level color and DPI sizing"
```

---

## Task 8: TrayIconController (manual verification)

**Files:**
- Create: `src/NagaBatteryTray/Ui/TrayIconController.cs`

**Interfaces:**
- Consumes: `BatteryMonitor`, `IconRenderer`, `DeviceState`.
- Produces:
  - `TrayIconController(BatteryMonitor monitor)` with `void Show()`, events `event Action LeftClicked`, `event Action RefreshRequested`, `event Action<bool> StartupToggled`, `event Action QuitRequested`, `void SetStartupChecked(bool)`, `Dispose()`.

- [ ] **Step 1: Implement TrayIconController**

`src/NagaBatteryTray/Ui/TrayIconController.cs`:
```csharp
using System.Windows.Forms;
using NagaBatteryTray.Monitoring;

namespace NagaBatteryTray.Ui;

public sealed class TrayIconController : IDisposable
{
    private readonly BatteryMonitor _monitor;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _startupItem;
    private System.Drawing.Icon? _current;

    public event Action? LeftClicked;
    public event Action? RefreshRequested;
    public event Action<bool>? StartupToggled;
    public event Action? QuitRequested;

    public TrayIconController(BatteryMonitor monitor)
    {
        _monitor = monitor;
        _icon = new NotifyIcon { Visible = false, Text = "Naga V2 Pro" };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, (_, _) => RefreshRequested?.Invoke());
        _startupItem = new ToolStripMenuItem("Run at startup") { CheckOnClick = true };
        _startupItem.CheckedChanged += (_, _) => StartupToggled?.Invoke(_startupItem.Checked);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) LeftClicked?.Invoke(); };
        _monitor.StateChanged += (_, state) => Update(state);
    }

    public void Show()
    {
        Update(_monitor.State);
        _icon.Visible = true;
    }

    public void SetStartupChecked(bool value)
    {
        _startupItem.CheckedChanged -= OnStartupChanged; // avoid feedback loop on programmatic set
        _startupItem.Checked = value;
        _startupItem.CheckedChanged += OnStartupChanged;
    }
    private void OnStartupChanged(object? s, EventArgs e) => StartupToggled?.Invoke(_startupItem.Checked);

    private void Update(DeviceState state)
    {
        int dpi = (int)Math.Round(96 * GetDpiScale());
        var next = IconRenderer.Render(state, dpi);
        _icon.Icon = next;
        IconRenderer.Destroy(_current);
        _current = next;
        _icon.Text = Tooltip(state);
    }

    private static string Tooltip(DeviceState s) => s.Status == DeviceStatus.Unknown
        ? "Naga V2 Pro — no response"
        : $"Naga V2 Pro — {s.Percent}%{(s.Charging ? " (charging)" : "")}";

    private static double GetDpiScale()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96.0;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        IconRenderer.Destroy(_current);
    }
}
```
> The `SetStartupChecked` wiring above replaces the inline `CheckedChanged` lambda from the constructor with the `OnStartupChanged` method so programmatic sets don't re-fire the toggle. When implementing, register `OnStartupChanged` (not a lambda) in the constructor for consistency.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded. (Full visual check happens in Task 12 once `AppHost` wires it up — this task has no standalone runnable UI yet.)

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat: tray NotifyIcon controller with menu, tooltip, and icon updates"
```

---

## Task 9: Popup — placement math (TDD) + WPF window (manual)

**Files:**
- Create: `src/NagaBatteryTray/Ui/PopupPlacement.cs`
- Create: `src/NagaBatteryTray/Ui/PopupViewModel.cs`
- Create: `src/NagaBatteryTray/Ui/PopupWindow.xaml`
- Create: `src/NagaBatteryTray/Ui/PopupWindow.xaml.cs`
- Test: `tests/NagaBatteryTray.Tests/PopupPlacementTests.cs`

**Interfaces:**
- Consumes: `DeviceState`, `BatteryMonitor`.
- Produces:
  - `static System.Drawing.Point PopupPlacement.Compute(System.Drawing.Rectangle anchorPx, System.Drawing.Rectangle workAreaPx, System.Drawing.Size popupPx)` — places the popup above the anchor, clamped to the work area.
  - `PopupWindow` WPF window bound to `PopupViewModel`, with `void ShowFor(DeviceState state)` / `void Toggle(...)`.

- [ ] **Step 1: Write the failing placement tests**

`tests/NagaBatteryTray.Tests/PopupPlacementTests.cs`:
```csharp
using System.Drawing;
using NagaBatteryTray.Ui;
using Xunit;

public class PopupPlacementTests
{
    private static readonly Rectangle WorkArea = new(0, 0, 1920, 1040); // taskbar 40px at bottom

    [Fact]
    public void Places_above_the_tray_anchor()
    {
        var anchor = new Rectangle(1850, 1042, 24, 24); // tray icon near bottom-right
        var pos = PopupPlacement.Compute(anchor, WorkArea, new Size(260, 180));
        Assert.True(pos.Y + 180 <= WorkArea.Bottom); // fully above the taskbar
        Assert.True(pos.X + 260 <= WorkArea.Right);  // not off the right edge
        Assert.True(pos.X >= WorkArea.Left);
    }

    [Fact]
    public void Clamps_to_left_edge_when_anchor_is_far_left()
    {
        var anchor = new Rectangle(2, 1042, 24, 24);
        var pos = PopupPlacement.Compute(anchor, WorkArea, new Size(260, 180));
        Assert.Equal(WorkArea.Left, pos.X);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter PopupPlacementTests`
Expected: FAIL — `PopupPlacement` not defined.

- [ ] **Step 3: Implement PopupPlacement**

`src/NagaBatteryTray/Ui/PopupPlacement.cs`:
```csharp
using System.Drawing;

namespace NagaBatteryTray.Ui;

public static class PopupPlacement
{
    private const int Margin = 8;

    /// <summary>Position (in physical px) for a popup anchored above a tray icon, clamped to the work area.</summary>
    public static Point Compute(Rectangle anchorPx, Rectangle workAreaPx, Size popupPx)
    {
        int x = anchorPx.Right - popupPx.Width;            // right-align to the icon
        int y = anchorPx.Top - popupPx.Height - Margin;    // above the icon

        x = Math.Clamp(x, workAreaPx.Left, Math.Max(workAreaPx.Left, workAreaPx.Right - popupPx.Width));
        if (y < workAreaPx.Top) y = anchorPx.Bottom + Margin; // fall below if no room above
        y = Math.Clamp(y, workAreaPx.Top, Math.Max(workAreaPx.Top, workAreaPx.Bottom - popupPx.Height));
        return new Point(x, y);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test --filter PopupPlacementTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Implement PopupViewModel**

`src/NagaBatteryTray/Ui/PopupViewModel.cs`:
```csharp
using System.ComponentModel;
using System.Windows.Media;
using NagaBatteryTray.Monitoring;

namespace NagaBatteryTray.Ui;

public sealed class PopupViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _percentText = "–";
    private string _status = "no response";
    private double _barFraction;
    private Brush _accent = Brushes.Gray;
    private bool _charging;

    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public double BarFraction { get => _barFraction; private set => Set(ref _barFraction, value); }
    public Brush Accent { get => _accent; private set => Set(ref _accent, value); }
    public bool Charging { get => _charging; private set => Set(ref _charging, value); }

    public void Apply(DeviceState s)
    {
        if (s.Status == DeviceStatus.Unknown)
        {
            PercentText = "–"; Status = "no response"; BarFraction = 0;
            Accent = Brushes.Gray; Charging = false; return;
        }
        PercentText = $"{s.Percent}%";
        Status = s.Charging ? "Charging" : "On battery";
        BarFraction = s.Percent / 100.0;
        Charging = s.Charging;
        var c = IconRenderer.ColorForLevel(s.Percent, s.Charging);
        Accent = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
```

- [ ] **Step 6: Implement PopupWindow.xaml (A+D hybrid)**

`src/NagaBatteryTray/Ui/PopupWindow.xaml`:
```xml
<ui:FluentWindow x:Class="NagaBatteryTray.Ui.PopupWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
    WindowStyle="None" ResizeMode="NoResize" ShowInTaskbar="False"
    WindowBackdropType="Mica" ExtendsContentIntoTitleBar="True"
    SizeToContent="WidthAndHeight" Topmost="True" Width="260">
  <Border Padding="14" CornerRadius="14">
    <StackPanel>
      <DockPanel>
        <TextBlock Text="Naga V2 Pro" FontWeight="SemiBold" FontSize="13"/>
        <TextBlock Text="{Binding Status}" DockPanel.Dock="Right" HorizontalAlignment="Right"
                   Foreground="#7c828a" FontSize="11"/>
      </DockPanel>
      <StackPanel Orientation="Horizontal" Margin="0,10,0,8">
        <TextBlock Text="{Binding PercentText}" FontSize="40" FontWeight="Bold"
                   Foreground="{Binding Accent}"/>
        <Border Background="#22FFFFFF" CornerRadius="20" Padding="9,4" Margin="12,6,0,0"
                VerticalAlignment="Center" Visibility="{Binding Charging, Converter={StaticResource BoolToVis}}">
          <TextBlock Text="⚡ Charging" FontSize="11"/>
        </Border>
      </StackPanel>
      <Border Height="7" CornerRadius="4" Background="#2a2c31">
        <Border CornerRadius="4" Background="{Binding Accent}" HorizontalAlignment="Left"
                Width="{Binding BarPixelWidth}"/>
      </Border>
      <StackPanel Orientation="Horizontal" Margin="0,13,0,0">
        <ui:Button Content="Refresh" Click="OnRefresh" Margin="0,0,8,0"/>
        <ui:Button Content="Settings" IsEnabled="False" ToolTip="Coming soon"/>
      </StackPanel>
    </StackPanel>
  </Border>
</ui:FluentWindow>
```
> The bar uses a fixed-pixel inner width (`BarPixelWidth`) rather than a fractional width, because a `Border` can't bind `Width` to a fraction of its parent directly. Add `public double BarPixelWidth => BarFraction * 232;` to `PopupViewModel` (232 ≈ 260 − padding) and raise it in `Apply`. Add a `BooleanToVisibilityConverter` resource keyed `BoolToVis` in the window resources.

- [ ] **Step 7: Implement PopupWindow.xaml.cs**

`src/NagaBatteryTray/Ui/PopupWindow.xaml.cs`:
```csharp
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using NagaBatteryTray.Monitoring;
using Wpf.Ui.Controls;

namespace NagaBatteryTray.Ui;

public partial class PopupWindow : FluentWindow
{
    private readonly PopupViewModel _vm = new();
    public event Action? RefreshRequested;

    public PopupWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Deactivated += (_, _) => Hide();
    }

    public void ShowAt(DeviceState state, Rectangle anchorPx, Rectangle workAreaPx)
    {
        _vm.Apply(state);
        // measure at current size, then place in physical px converted to DIPs
        var sizePx = new Size((int)(Width * Dpi()), (int)(ActualHeight > 0 ? ActualHeight * Dpi() : 180 * Dpi()));
        var pt = PopupPlacement.Compute(anchorPx, workAreaPx, sizePx);
        Left = pt.X / Dpi();
        Top = pt.Y / Dpi();
        Show();
        Activate();
    }

    public void ApplyState(DeviceState state) => _vm.Apply(state);

    private double Dpi()
    {
        var src = PresentationSource.FromVisual(this);
        return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
}
```
> `Width * Dpi()` uses the WPF `Width` (DIPs) → px; height falls back to a nominal 180 DIP until first render. This is good enough for placement; refine if the popup is clipped on a specific monitor.

- [ ] **Step 8: Build**

Run: `dotnet build`
Expected: Build succeeded. (Visual check in Task 12.)

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: Fluent popup window, view model, and DPI-aware placement"
```

---

## Task 10: Notifications (low-battery toast)

**Files:**
- Create: `src/NagaBatteryTray/Ui/Notifications.cs`

**Interfaces:**
- Consumes: nothing (called from `AppHost` on `LowBatteryCrossed`).
- Produces: `static void Notifications.LowBattery(int percent)`.

- [ ] **Step 1: Implement Notifications**

`src/NagaBatteryTray/Ui/Notifications.cs`:
```csharp
using CommunityToolkit.WinUI.Notifications;

namespace NagaBatteryTray.Ui;

public static class Notifications
{
    public static void LowBattery(int percent)
    {
        new ToastContentBuilder()
            .AddText("Naga V2 Pro")
            .AddText($"Battery at {percent}% — time to charge.")
            .Show();
    }
}
```
> `ToastNotificationManagerCompat` (invoked under the hood by `.Show()`) auto-registers the COM activator + AUMID `NagaBatteryTray` on first use, so no Start-menu shortcut is needed for this unpackaged app. Confirm the package target `net10.0-windows10.0.17763.0` exposes the WinRT toast types during build.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded. (Toast is visually verified in Task 12 by forcing a low reading.)

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat: low-battery toast via CommunityToolkit notifications"
```

---

## Task 11: StartupRegistration (run at login)

**Files:**
- Create: `src/NagaBatteryTray/Startup/StartupRegistration.cs`
- Test: `tests/NagaBatteryTray.Tests/StartupRegistrationTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `StartupRegistration(string valueName, string subKey)` (defaults: `"NagaBatteryTray"`, `@"Software\Microsoft\Windows\CurrentVersion\Run"`) with `bool IsEnabled()`, `void Enable()`, `void Disable()`.

- [ ] **Step 1: Write the failing tests (use a temp subkey to avoid polluting real Run)**

`tests/NagaBatteryTray.Tests/StartupRegistrationTests.cs`:
```csharp
using NagaBatteryTray.Startup;
using Xunit;

public class StartupRegistrationTests
{
    private static StartupRegistration NewReg() =>
        new($"NagaTest-{Guid.NewGuid():N}", @"Software\NagaBatteryTrayTests\Run");

    [Fact]
    public void Enable_then_IsEnabled_is_true_then_Disable_is_false()
    {
        var reg = NewReg();
        Assert.False(reg.IsEnabled());
        reg.Enable();
        Assert.True(reg.IsEnabled());
        reg.Disable();
        Assert.False(reg.IsEnabled());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter StartupRegistrationTests`
Expected: FAIL — `StartupRegistration` not defined.

- [ ] **Step 3: Implement StartupRegistration**

`src/NagaBatteryTray/Startup/StartupRegistration.cs`:
```csharp
using Microsoft.Win32;

namespace NagaBatteryTray.Startup;

public sealed class StartupRegistration
{
    private readonly string _valueName;
    private readonly string _subKey;

    public StartupRegistration(
        string valueName = "NagaBatteryTray",
        string subKey = @"Software\Microsoft\Windows\CurrentVersion\Run")
    {
        _valueName = valueName;
        _subKey = subKey;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey);
        return key?.GetValue(_valueName) is not null;
    }

    public void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKey);
        key.SetValue(_valueName, $"\"{Environment.ProcessPath}\"");
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test --filter StartupRegistrationTests`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: HKCU Run startup registration with injectable key for tests"
```

---

## Task 12: AppHost + Program wiring (integration, manual verification)

**Files:**
- Create: `src/NagaBatteryTray/AppHost.cs`
- Modify: `src/NagaBatteryTray/Program.cs` (mutex, WPF Application host, AppHost startup)

**Interfaces:**
- Consumes: every prior component.
- Produces: `AppHost(System.Windows.Application app)` with `void Start()`; `Program.Main` owning the single-instance mutex, `--probe` dispatch, and the WPF message loop.

- [ ] **Step 1: Implement AppHost**

`src/NagaBatteryTray/AppHost.cs`:
```csharp
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Startup;
using NagaBatteryTray.Ui;
using Application = System.Windows.Application;

namespace NagaBatteryTray;

public sealed class AppHost
{
    private readonly Application _app;
    private readonly ISettingsStore _settings = new JsonSettingsStore(JsonSettingsStore.DefaultPath());
    private readonly StartupRegistration _startup = new();

    private RazerDevice _device = null!;
    private BatteryMonitor _monitor = null!;
    private TrayIconController _tray = null!;
    private PopupWindow? _popup;

    public AppHost(Application app) => _app = app;

    public void Start()
    {
        // WPF-UI controls require its theme + controls resource dictionaries merged into
        // Application.Resources. With no App.xaml we add them in code, or FluentWindow / ui:Button
        // render unstyled or throw at runtime. (Confirm the exact Wpf.Ui.Markup type names against
        // the installed wpf-ui version at build time — verification was deferred due to a session limit.)
        _app.Resources.MergedDictionaries.Add(
            new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        _app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);

        _device = new RazerDevice(_settings);
        _monitor = new BatteryMonitor(_device, _settings, Dispatch);
        _tray = new TrayIconController(_monitor);

        _monitor.LowBatteryCrossed += (_, pct) => Notifications.LowBattery(pct);
        _tray.LeftClicked += TogglePopup;
        _tray.RefreshRequested += () => _ = _monitor.RefreshNowAsync();
        _tray.StartupToggled += SetStartup;
        _tray.QuitRequested += Quit;

        _tray.SetStartupChecked(_startup.IsEnabled());
        _tray.Show();

        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) _ = _monitor.RefreshNowAsync(); };
        SystemEvents.SessionSwitch += (_, e) => { if (e.Reason == SessionSwitchReason.SessionUnlock) _ = _monitor.RefreshNowAsync(); };

        _monitor.Start();
    }

    private void Dispatch(Action action) => _app.Dispatcher.Invoke(action);

    private void TogglePopup()
    {
        _popup ??= new PopupWindow();
        if (_popup.IsVisible) { _popup.Hide(); return; }
        _popup.RefreshRequested -= OnPopupRefresh;
        _popup.RefreshRequested += OnPopupRefresh;

        var anchor = TrayAnchorRect();
        var work = Screen.FromRectangle(anchor).WorkingArea;
        _popup.ShowAt(_monitor.State, anchor, work);
    }
    private void OnPopupRefresh() => _ = _monitor.RefreshNowAsync();

    private static Rectangle TrayAnchorRect()
    {
        // Fallback: bottom-right of the primary work area. Refine with Shell_NotifyIconGetRect if needed.
        var wa = Screen.PrimaryScreen!.WorkingArea;
        return new Rectangle(wa.Right - 24, wa.Bottom, 24, 24);
    }

    private void SetStartup(bool enable)
    {
        if (enable) _startup.Enable(); else _startup.Disable();
    }

    private void Quit()
    {
        _monitor.Dispose();
        _device.Dispose();
        _tray.Dispose();
        _app.Shutdown();
    }
}
```
> `TrayAnchorRect` ships as a primary-work-area fallback. The §6.4 `Shell_NotifyIconGetRect` upgrade (exact icon rect on the correct monitor) is a follow-up refinement — wire it in if the popup lands wrong on a multi-monitor setup during Step 3.

- [ ] **Step 2: Finalize Program.cs (mutex + WPF host)**

`src/NagaBatteryTray/Program.cs`:
```csharp
using System.Windows;
using Application = System.Windows.Application;

namespace NagaBatteryTray;

internal static class Program
{
    private const string MutexName = @"Local\NagaBatteryTray-b3f1c2d4-5a6e-4f80-9c1a-2e7d8b4f6a90";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe")
        {
            AllocConsoleIfNeeded();
            return Diagnostics.ProbeCommand.Run();
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew) return 0; // already running

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var host = new AppHost(app);
        host.Start();
        return app.Run();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    private static void AllocConsoleIfNeeded()
    {
        try { if (Console.OpenStandardOutput() == Stream.Null) AllocConsole(); } catch { AllocConsole(); }
    }
}
```

- [ ] **Step 3: Build and run — full manual verification**

Run: `dotnet run --project src/NagaBatteryTray`
Verify each:
- Tray icon appears showing the battery number, colored by level.
- Hover tooltip shows `Naga V2 Pro — NN% (charging)`.
- Left-click toggles the Fluent popup near the tray; it dismisses on focus loss.
- Right-click menu: Refresh now (updates), Run at startup (toggles + persists — check `HKCU\…\Run`), Quit (exits cleanly).
- Launch a 2nd instance → it exits immediately (single instance).
- Seat/lift the mouse on the dock → charging state and color update within ~15 s.
- Temporarily set `lowBatteryThreshold` high (e.g. 95) in `%APPDATA%\NagaBatteryTray\settings.json`, restart, let it read while discharging → low-battery toast fires once. Restore to 15.
- Sleep/resume the PC → reading refreshes promptly (not after a full 60 s).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: AppHost wiring, single-instance, popup toggle, power/session refresh"
```

---

## Task 13: Publish, footprint check, trim fallback

**Files:**
- Modify: `src/NagaBatteryTray/NagaBatteryTray.csproj` (publish properties)

**Interfaces:**
- Consumes: the whole app.
- Produces: a self-contained single-file exe; a verified footprint.

- [ ] **Step 1: Add publish properties to the csproj `<PropertyGroup>`**

```xml
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>partial</TrimMode>
```

- [ ] **Step 2: Publish**

Run:
```bash
dotnet publish src/NagaBatteryTray -c Release
```
Expected: a single `NagaBatteryTray.exe` under `src/NagaBatteryTray/bin/Release/net10.0-windows10.0.17763.0/win-x64/publish/`. Note any IL2xxx trim warnings.

- [ ] **Step 3: Run the published exe and verify it behaves like Task 12**

Launch the published `NagaBatteryTray.exe` directly (not via `dotnet run`). Verify: tray icon shows the number, popup opens and is styled (Mica/Fluent — **if the popup is unstyled or throws at runtime, the trimmer stripped wpf-ui/XAML resources**: set `<PublishTrimmed>false</PublishTrimmed>` and re-publish), toast works.

- [ ] **Step 4: Check footprint**

In Task Manager, after opening the popup once: confirm idle CPU ~0% and working-set RAM roughly in the 45–55 MB range (≤ ~80 MB acceptable). Note the on-disk exe size.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "build: self-contained single-file trimmed publish with trim fallback note"
```

---

## Self-Review

**Spec coverage** (each spec section → task):
- §2 success criteria → tray number (T7/T8), popup toggle (T9/T12), low-battery toast (T6/T10/T12), footprint (T13), startup/single-instance/no-admin (T11/T12), works w/o Synapse (T5 manual), graceful offline (T6 staleness + T5 error handling). ✓
- §5 protocol (report, CRC, reply, probe, transport) → T3/T4/T5. ✓
- §6 components → Settings T2, RazerDevice T5, BatteryMonitor T6, TrayIcon T7/T8, Popup T9, Notifications T10, AppHost T12. ✓
- §8 settings → T2. §9 stack → T1 csproj. §10 build → T13. §11 empirical → T5 probe. §12 tests → T2/T3/T4/T6/T7/T9/T11. §13 risks (probe subcommand) → T5. ✓

**Placeholder scan:** no "TBD/handle edge cases/similar to Task N"; every code step has real code. API-uncertainty notes (HidSharp usage filter, wpf-ui bar binding, trim fallback) are called out explicitly with the concrete primary approach, not left blank. ✓

**Type consistency:** `BatteryReading`, `DeviceState`/`DeviceStatus`, `IRazerDevice.ReadAsync(ct)`, `ISettingsStore` members, `IconRenderer.ColorForLevel/Render/Destroy`, `BatteryMonitor` events/`ProcessReading`, `PopupPlacement.Compute`, `StartupRegistration` members are defined once and consumed with matching signatures across tasks. ✓

---

*Plan generated 2026-06-19 from the approved+hardened spec. Pure-logic tasks (2,3,4,6,7,9,11) are TDD with real xUnit tests; device/UI/host tasks (5,8,10,12,13) use concrete code plus explicit manual verification, with Task 5 as the hardware de-risk gate.*
