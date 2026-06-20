# Naga Battery Tray

A tiny Windows system-tray app that shows the **Razer Naga V2 Pro** mouse battery
percentage and charging status — a lightweight replacement for Razer Synapse when
all you want is the battery number.

- **Tray icon** draws the battery % as a number, colored by level (green → amber → red),
  green while charging.
- **Click the icon** for a compact popup: big %, charging chip, level bar, Refresh.
- **Low-battery toast** when the charge drops to/below the threshold (default 15%) on battery.
- Reads the mouse directly over HID — **no Razer Synapse required**.

## Footprint

Measured on the installed build, idle in the tray:

| Metric | Value |
| --- | --- |
| Idle CPU | **0%** |
| Private working set (real RAM) | **~23 MB** |
| Full working set | ~90 MB (mostly *shared* runtime pages Windows reclaims) |
| Polling | every 60 s (15 s while charging) |

The app is published **self-contained**: it bundles its own copy of the .NET 10 runtime,
so it depends on nothing being installed on the machine and keeps working through any
future .NET changes. That trades a larger on-disk size and some *shared* RAM pages for
zero runtime dependencies — private RAM is still ~23 MB.

## Install / update

Requires the .NET 10 SDK to build (no admin needed — a per-user SDK install works).

```powershell
.\scripts\install.ps1
```

This publishes a Release build, copies it to
`%LOCALAPPDATA%\Programs\NagaBatteryTray\`, registers it to run at login (per-user
`HKCU\...\Run` key), and launches it. Re-run it any time after a code change to update.

> The install folder holds one ~188 MB `NagaBatteryTray.exe` plus five small
> `*_cor3.dll` WPF native libraries. **Those DLLs must stay next to the exe** — WPF
> loads them as native libraries, and the single-file bundle leaves them beside the exe
> by design. (Copying the exe alone crashes with `DllNotFoundException` at startup.)

### Run at login

`install.ps1` sets it up automatically. You can also toggle it from the tray icon's
right-click menu ("Run at startup"), which writes the same `HKCU` Run key.

## Uninstall

```powershell
.\scripts\uninstall.ps1
```

Stops the app, removes the run-at-login entry, and deletes the install folder.
Settings at `%APPDATA%\NagaBatteryTray\settings.json` are left in place — delete that
folder too if you want a clean wipe.

## Diagnostics

If the battery never reads (mouse asleep, different firmware, etc.), run the HID probe:

```powershell
& "$env:LOCALAPPDATA\Programs\NagaBatteryTray\NagaBatteryTray.exe" --probe
```

It enumerates the Razer HID collections, tries each known transaction id, and prints
the raw battery reply so you can see what the mouse returns.

## How it works

- **`Hid/RazerProtocol.cs`** — pure protocol: builds the 90-byte feature report, the XOR
  CRC, and parses replies. Battery = class `0x07`/id `0x80`; charging = `0x07`/`0x84`;
  value at reply byte 9, scaled `×100/255`.
- **`Hid/RazerDevice.cs`** — opens the mouse's HID collection with zero-access
  `CreateFile` + `HidD_SetFeature`/`HidD_GetFeature` (the feature report lives on the
  OS-owned mouse collection, which HidSharp's stream can't open), probes transaction ids,
  and caches the one that works.
- **`Monitoring/BatteryMonitor.cs`** — polling timer + state machine (online/unknown,
  low-battery edge logic, staleness → Unknown).
- **`Ui/`** — `IconRenderer` (GDI+ number icon, DPI-aware, supersampled), `TrayIconController`
  (NotifyIcon + menu), `PopupWindow` (compact WPF card, multi-monitor placement),
  `Notifications` (toast).
- **`AppHost.cs` / `Program.cs`** — single-instance mutex, lifecycle wiring, power/session
  resume refresh.

See `docs/superpowers/specs/` and `docs/superpowers/plans/` for the full design and
implementation plan.
