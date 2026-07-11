<div align="center">

# 🐍 Razer Naga Companion

### Your Razer Naga V2 Pro battery, right in the system tray — **no Razer Synapse required.**

[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Synapse](https://img.shields.io/badge/Razer%20Synapse-not%20required-44D62C)](#why)
[![Idle RAM](https://img.shields.io/badge/idle%20RAM-~23%20MB-brightgreen)](#-footprint)
[![Idle CPU](https://img.shields.io/badge/idle%20CPU-0%25-brightgreen)](#-footprint)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

</div>

---

## Why?

Razer Synapse is **hundreds of megabytes** and several always-running background
services just to tell you your mouse's battery level. Razer Naga Companion does that
**one job** — and does it in **~23 MB of RAM at 0% idle CPU**, reading the mouse
directly over HID with no Synapse, no driver, and no admin rights.

> Built for the **Razer Naga V2 Pro** (wireless). Other Razer mice may work if they
> speak the same HID battery protocol, but only the Naga V2 Pro is verified.

---

## ✨ Features

- 🔋 **Battery % in the tray** — a battery number that stays sharp at any display
  scaling (100%/150%/200%), fills the icon, and recolors by level (🟢 green → 🟡 amber →
  🔴 red), turning green while charging.
- 🖱️ **Click for details** — a compact popup with the exact %, a charging chip, a level
  bar, the active link (Wired / Wireless / On battery), and Refresh + Settings.
- 🎯 **Active DPI control** — read and set the mouse's current hardware DPI from the
  Settings window; the change is written to the device — no Synapse round-trip.
- ⚙️ **Settings window** — low-battery threshold, poll cadence, and DPI, all per-user.
- 🔔 **Low-battery toast** — a native Windows notification when you drop to/below your
  threshold (default 15%) while on battery.
- ⚡ **Charging aware** — polls faster while charging (15 s vs 60 s), suppresses nagging
  notifications, and flips charge status **instantly** when you plug/unplug the cable.
- 🔗 **Wired & wireless** — reads battery over the USB-C cable as well as the dongle, and
  shows which link is active.
- 🪶 **Featherweight** — single background process, ~0 idle CPU, ~23 MB private RAM.
- 🔌 **No Synapse** — talks to the mouse directly via HID feature reports.
- 🚀 **Runs at login**, single-instance, fully per-user (no UAC / admin prompt ever).

---

## 👀 What it looks like

```
Tray:  [95]   ← battery %, fills the icon, recolored by level — click to open ↓

┌──────────────────────────────────┐
│  Naga V2 Pro               Wired │
│                                  │
│  95%    [ Charging ]             │
│  ███████████████████████████░    │
│                                  │
│  [    Refresh    ][   Settings  ]│
└──────────────────────────────────┘
```

---

## 📊 Footprint

Measured on the installed build, sitting idle in the tray:

| Metric | Value |
| --- | --- |
| **Idle CPU** | **0 %** |
| **Private working set** (real RAM cost) | **~23 MB** |
| Full working set | ~90 MB *(mostly shared runtime pages Windows reclaims)* |
| Poll cadence | 60 s on battery · 15 s while charging |
| On-disk install | ~196 MB *(bundles the .NET 10 runtime — see [below](#a-note-on-size))* |

---

## 🚀 Install

**Prerequisite:** the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build it.
A per-user SDK install works fine — **no admin required**.

```powershell
git clone https://github.com/Bmwascher/razer-naga-companion.git
cd razer-naga-companion
.\scripts\install.ps1
```

`install.ps1` publishes a Release build, copies it to
`%LOCALAPPDATA%\Programs\NagaBatteryTray\`, registers it to **run at login**, and
launches it. Re-run it any time to update after pulling new code.

### Run at login

`install.ps1` wires this up automatically (a per-user logon **scheduled task** with a
1-minute delay — not the `HKCU\…\Run` key, which fires too early in boot for Smart App
Control to clear an unsigned binary). You can also toggle it from the tray icon's
right-click menu → **Run at startup**.

### 🔄 Update

```powershell
git pull
.\scripts\install.ps1
```

### 🗑️ Uninstall

```powershell
.\scripts\uninstall.ps1
```

Stops the app, removes the run-at-login entry, and deletes the install folder. Your
settings at `%APPDATA%\NagaBatteryTray\settings.json` are left in place — delete that
folder too for a clean wipe.

---

## 🩺 Diagnostics

If the battery never shows up (mouse asleep, different firmware, etc.), run the built-in
HID probe — it enumerates the Razer HID collections, tries each known transaction id, and
prints the raw battery reply:

```powershell
& "$env:LOCALAPPDATA\Programs\NagaBatteryTray\NagaBatteryTray.exe" --probe       # battery
& "$env:LOCALAPPDATA\Programs\NagaBatteryTray\NagaBatteryTray.exe" --probe-dpi   # active DPI
```

---

## 🧠 How it works

```mermaid
flowchart LR
    HID["Razer Naga V2 Pro<br/>HID feature reports"] --> Dev["RazerDevice<br/>open · probe · read · DPI"]
    Dev --> Mon["BatteryMonitor<br/>poll timer + state machine"]
    Mon --> Tray["TrayIcon<br/>GUID-stable icon"]
    Mon --> Popup["PopupWindow<br/>compact card"]
    Mon --> Toast["Notifications<br/>low-battery toast"]
    Mon --> Set["SettingsWindow<br/>threshold · cadence · DPI"]
    DevChg["DeviceChangeWatcher<br/>USB plug/unplug"] --> Mon
    Cfg[("settings.json")] -.-> Mon
    Host["AppHost / Program<br/>lifecycle · single-instance · run-at-login"] --- Mon
```

- **`Hid/RazerProtocol.cs`** — pure protocol: builds the 90-byte feature report, computes
  the XOR CRC, parses replies. Battery = class `0x07` / id `0x80`; value at reply byte 9,
  scaled `× 100 / 255`.
- **`Hid/RazerDevice.cs`** — opens the mouse's HID collection with zero-access
  `CreateFile` + `HidD_SetFeature` / `HidD_GetFeature` (the feature report lives on the
  OS-owned mouse collection, which a normal HID stream can't open), probes transaction
  ids and caches the one that works, picks whichever interface is live (wired or wireless),
  and reads/writes the active DPI.
- **`Monitoring/BatteryMonitor.cs`** — polling timer + state machine (online/unknown,
  low-battery edge logic, staleness → unknown); also the DPI read/set pass-through.
- **`Ui/`** — `IconRenderer` (GDI+ number icon, DPI-aware, supersampled, fills the icon
  height), `TrayIcon` + `TrayIconController` (Win32 `Shell_NotifyIcon` with a stable GUID so
  the taskbar position survives restarts), `PopupWindow` (compact WPF card, multi-monitor
  placement), `SettingsWindow` (threshold / cadence / active DPI), `DeviceChangeWatcher`
  (instant refresh on USB plug/unplug), `Notifications` (toast).
- **`AppHost.cs` / `Program.cs`** — single-instance mutex, lifecycle wiring, and refresh
  on power-resume / session-unlock / USB device-change.

Full design and implementation notes live in [`docs/superpowers/`](docs/superpowers/).

---

## 🗺️ Roadmap

- [x] **v1 — Battery tray**: tray %, popup, low-battery toast, charging detection,
      run-at-login.
- [x] **Settings + active DPI**: a real Settings window for threshold and poll cadence,
      plus reading and setting the mouse's hardware DPI on the device.
- [x] **Reliability & polish**: wired/USB-C battery, instant charge-status on plug/unplug,
      GUID tray icon (taskbar position persists), larger tray digits, refreshed popup.
- [—] **C — Dock charger support**: *closed — the Mouse Dock Pro relay is non-viable on
      this firmware (it never answers a battery query). Charging while docked already shows
      via the mouse's own read.*
- [ ] **B — Button remapping**: remap the Naga's side buttons *(feasibility spike first —
      the V2 Pro's remap protocol isn't publicly documented).*

---

## 🛠️ Building from source

```powershell
dotnet build                                   # Debug (framework-dependent, fast inner loop)
dotnet test                                    # run the xUnit suite
dotnet publish src/NagaBatteryTray -c Release  # full self-contained single-file exe
```

#### A note on size

The Release build is **self-contained**: it bundles its own copy of the .NET 10 runtime,
so the installed app depends on nothing being installed on the machine and keeps working
through any future .NET changes. That's the ~196 MB on disk — but the *private* RAM cost
is still only ~23 MB.

> ⚠️ The publish output is one ~188 MB `NagaBatteryTray.exe` **plus five small
> `*_cor3.dll` WPF native libraries**. Those DLLs must stay next to the exe — WPF loads
> them natively, and single-file publishing leaves them beside the exe by design. (The
> install script handles this; copying the exe alone crashes with `DllNotFoundException`.)

---

## 🧪 Tech stack

C# · .NET 10 (`net10.0-windows`) · WPF + WinForms · Win32 `Shell_NotifyIcon` (GUID tray) ·
HidSharp · [WPF-UI](https://github.com/lepoco/wpfui) (Fluent styling) ·
CommunityToolkit.WinUI.Notifications · xUnit.

---

## ⚠️ Disclaimer

This is an independent, community project. It is **not affiliated with, endorsed by, or
sponsored by Razer Inc.** "Razer" and "Naga" are trademarks of Razer Inc., used here only
to describe hardware compatibility. Use at your own risk.

---

## 📄 License

[MIT](LICENSE) © Brandon Wascher
