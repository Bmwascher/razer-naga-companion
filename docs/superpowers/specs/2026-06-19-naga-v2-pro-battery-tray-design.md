# Naga V2 Pro Battery Tray — Design Spec

- **Date:** 2026-06-19
- **Status:** Approved design (v1) — ready for implementation planning
- **Working repo name:** `naga-battery-tray` (rename freely; internal identifier `NagaBatteryTray`)
- **Target hardware:** Razer Naga V2 Pro (wireless, on Razer Mouse Dock Pro), Windows 11

---

## 1. Goal & motivation

Replace Razer Synapse **for the single purpose of seeing the Naga V2 Pro's battery level and charging state** from the Windows system tray. Synapse runs several background services at a combined ~150–400 MB+ just to surface a battery number. This tool does that one job in a fraction of the footprint, with no Synapse dependency.

This is **v1 of a deliberately expandable base**: the user may later grow it into a polished config GUI and key remapping (a Synapse-lite). v1 builds none of that, but is architected so those bolt on without a rewrite.

## 2. Success criteria

A build is "done" when:

1. The tray icon **always shows the current battery %** as a readable number.
2. Clicking the icon opens a **popup** showing battery %, charging status, and last-updated time.
3. A **low-battery desktop toast** fires once when the level crosses a configurable threshold (default 15%), and re-arms after recharge.
4. **Idle CPU ≈ 0%** (one HID poll every ~60 s); resident RAM in the tens of MB.
5. **Runs at login**, **single instance**, **no admin/UAC**.
6. Reads battery with **Synapse not installed/running**.
7. Degrades gracefully when the mouse is asleep/offline (shows an "unknown" state, never crashes or shows a stale number as if live).

## 3. Non-goals (v1) and designed-for-later

**Out of scope for v1 (YAGNI):** button/key remapping, DPI/polling-rate/lighting control, profiles, battery-history graph, a multi-device switcher UI, light theme, installer/auto-update.

**Architected so these are cheap later (do not build now):**
- **Multi-device:** the device layer is a *list*, not a single hardcoded device. An "adaptive caret" device switcher can light up automatically when a 2nd **battery** device appears. (The Mouse Dock Pro is USB-powered and has **no battery**, so it is never a switcher entry.)
- **Config GUI:** the popup's **Settings** button is the on-ramp to a future settings/config window.
- **Key remapping:** rides on the same `RazerDevice` HID layer (host-side low-level hooks, or on-device onboard-memory writes) — a future `RemapEngine` + window, with nothing above it rewritten.

## 4. Architecture

### 4.1 Process model — one process, two modes

A single tray application (.NET 8, Windows-only):

- **Resident mode (always running):** a `NotifyIcon` in the tray + a `System.Threading.Timer` that polls battery on an interval. **No window object is constructed.** This is the near-0 idle state.
- **On-demand window:** the WPF popup is constructed only when the user clicks the tray icon, and hidden/released when it loses focus. The heavy WPF surface is not paid for until the user looks at it.

### 4.2 Layers

```
  AppHost          single-instance mutex, run-at-login, settings, lifecycle, DI wiring
  TrayIconController renders battery % onto the icon bitmap, tooltip, right-click menu
  PopupWindow (WPF)  on-demand Fluent panel (the A+D hybrid)
  Notifications      edge-triggered low-battery toast
  BatteryMonitor     polling timer, current DeviceState, charging detection, low-batt edge
  RazerDevice        HID transport + Razer protocol; exposes a device LIST (battery/charging now; buttons later)
```

Each layer has one responsibility and a narrow interface. `BatteryMonitor` depends on `RazerDevice`; `TrayIconController`, `PopupWindow`, and `Notifications` subscribe to `BatteryMonitor`'s state-changed events. No layer above `RazerDevice` knows the HID details.

### 4.3 Footprint strategy & honest numbers

- Publish as **self-contained, single-file, trimmed** .NET 8 (no runtime install needed).
- **NativeAOT is NOT used** — it is unsupported for WPF (COM/reflection). Trimmed single-file is the lever instead.
- **Realistic footprint:** ~**45–55 MB** working-set RAM (WPF + WPF-UI Fluent popup pulls in `PresentationFramework`), ~**30–45 MB** on disk. Idle CPU ~0% (one poll/60 s). Still **3–8× lighter than Synapse**.
- If footprint ever needs to drop toward ~30–40 MB, the fallback is a WinForms shell with a hand-styled popup — explicitly **not** chosen here because it is a weaker base for the future GUI.

## 5. Razer HID protocol (verified)

Verified against the openrazer kernel driver (read twice), the RazerBatteryTaskbar device table, and the hsutungyu Python tool, then reconciled. Confidence is **high** on the protocol; the items in §12 are confirmed empirically at first run.

### 5.1 Target device & interface

- **Vendor:** `0x1532`. **Query the MOUSE, not the dock.** When docked, the Naga still enumerates as its own USB device (**wireless PID `0x00A8`**, wired `0x00A7`); the Mouse Dock Pro (`0x00A4`) is separate and does **not** proxy the mouse's battery. The user's machine confirms the Naga enumerates as **`0x00A8`**.
- **Interface/collection:** the vendor control collection, **usage page `0xFF00`** (the interface Windows labels "Razer Naga V2 Pro", `MI_02`). In HidSharp there is no usage-page enumeration overload — select it in code: `(device.GetTopLevelUsage() >> 16) == 0xFF00`.
- **Bluetooth is not supported** by this method (no USB PID over BT). Moot here — the user is on the dongle/dock.

### 5.2 Packet — 90-byte Razer report (0-indexed)

| Byte    | Field             | Request value                                  |
|---------|-------------------|------------------------------------------------|
| 0       | status            | `0x00` (new command)                           |
| 1       | transaction_id    | `0x1f` (Naga V2 Pro)                            |
| 2–3     | remaining_packets | `0x0000` (big-endian)                          |
| 4       | protocol_type     | `0x00`                                          |
| 5       | data_size         | `0x02`                                          |
| 6       | command_class     | `0x07` (power)                                  |
| 7       | command_id        | `0x80` battery level / `0x84` charging status   |
| 8–87    | arguments[80]     | all `0x00` on request                          |
| 88      | crc               | XOR of bytes `[2..87]`                          |
| 89      | reserved          | `0x00`                                          |

- **Battery level:** class `0x07`, id `0x80`, data_size `0x02`. Reply value at **byte `[9]`** is raw 0–255 → `percent = round(reply[9] * 100 / 255)`.
- **Charging status:** class `0x07`, id `0x84`, data_size `0x02`. Reply byte `[9]` ≠ 0 → charging.
- **CRC:** simple XOR fold, **not** polynomial: `crc = 0; for (i = 2; i < 88; i++) crc ^= report[i];` (excludes status, transaction_id, crc, reserved). transaction_id is **not** in the CRC.

### 5.3 Transport sequence

1. Build the 91-byte HID **feature** buffer: `buffer[0] = 0x00` (report id), `buffer[1..90]` = the 90-byte report above.
2. **`SetFeature(buffer)`** (HID SET_REPORT).
3. **Wait ~300–500 ms** (wireless relay latency; tune down empirically; too short → busy/stale `status==0x01`).
4. **`GetFeature(buffer)`** (HID GET_REPORT), read the value at **`buffer[10]`** (= report byte `[9]`, shifted by the +1 report-id offset).
5. **Validate the reply:** `status (buffer[1]) == 0x02` (success) and reply CRC valid. On failure, treat as "unknown".

### 5.4 Transaction-id probe (robustness)

`0x1f` is high-confidence, but at first run probe an ordered set and cache the first that yields a **valid reply CRC + a plausible 0–100 value**:

```
0x1f, 0x3f, 0x00, 0xff, 0x08, 0x88, 0x1d, 0x9f
```

Cache the winner in settings so subsequent runs skip the probe.

## 6. Components

### 6.1 `RazerDevice` (HID + protocol)
- Enumerates `0x1532` devices, selects the `0xFF00` collection, opens an `HidStream`.
- `Task<BatteryReading> ReadAsync()` → `{ percent, isCharging, isPresent, timestamp }`. Builds/sends the feature report, applies the probe, validates, maps the reply.
- Exposes a **device list** abstraction (`IReadOnlyList<RazerDeviceInfo>`) even though v1 has one entry — so multi-device is a list-growth change, not a redesign.
- Owns reconnection: if the handle goes stale (mouse slept/undocked), transparently re-enumerate on next read.

### 6.2 `BatteryMonitor`
- Holds current `DeviceState` (`Unknown | Online{percent,charging}`).
- `System.Threading.Timer`: **60 s** default; **15 s** while charging (so the climb is visible). Configurable.
- Raises `StateChanged` on meaningful change; raises `LowBatteryCrossed` (edge-triggered) when crossing below threshold while **not** charging; re-arms when level recovers above threshold or charging starts.

### 6.3 `TrayIconController`
- Renders the % onto a DPI-aware bitmap each update via `System.Drawing` (GDI+): the number, **color per the Dynamic-by-level scheme**, and a small **bolt overlay/tint when charging**.
- Tooltip: `Naga V2 Pro — 87% (charging) · updated 12s ago`. Unknown state → `–` icon + "no response" tooltip.
- Right-click menu: **Refresh now** · **Run at startup** (checkable) · **Quit**.
- Left-click toggles the popup.

### 6.4 `PopupWindow` (WPF, on-demand) — the A+D hybrid
- Borderless Fluent panel (WPF-UI `FluentWindow`, Mica), positioned above the tray, dismissed on focus-loss.
- Header: status dot + "Naga V2 Pro" + "online/offline". Body: large % (Dynamic color), slim level bar, neutral **⚡ charging chip**, mini red→amber→green scale, "updated Ns ago". Footer: **Refresh** and **Settings** buttons (Settings is a stub/no-op in v1, reserved for the future config window).
- Constructed lazily; released after close.

### 6.5 `Notifications`
- `CommunityToolkit.WinUI.Notifications` toast on `LowBatteryCrossed`: "Naga V2 Pro battery at 14%." Auto-registers the COM activator/AUMID on first `.Show()` (no Start-menu shortcut needed for an unpackaged app). One toast per crossing; not repeated until re-armed.

### 6.6 `AppHost`
- Single instance via named `Mutex` (`Global\NagaBatteryTray-{guid}`); second launch exits.
- Run-at-login via `HKCU\…\Run` value = `Environment.ProcessPath` (quoted), toggled from the tray menu. Off by default; user opts in.
- Loads/saves settings; wires the layers; owns shutdown.

## 7. UI design (locked)

- **Tray icon:** the live battery number, recolored by level (green/amber/red), bolt/tint when charging.
- **Popup:** A+D hybrid, **Dynamic-by-level** color language. Razer green is the "healthy/charging" tone; amber mid; red low. Charging shown as a neutral bolt chip so green keeps meaning "level."
- **States shown:** Online (number + charging), Charging (bolt + faster updates), Unknown/offline (`–`, muted, "no response").
- Dark theme only in v1 (light theme is a future option).

## 8. Settings & persistence

Minimal JSON at `%APPDATA%\NagaBatteryTray\settings.json`:

```json
{
  "pollIntervalSeconds": 60,
  "pollIntervalChargingSeconds": 15,
  "lowBatteryThreshold": 15,
  "lowBatteryNotify": true,
  "cachedTransactionId": "0x1f",
  "setReadDelayMs": 400
}
```

Run-at-startup state lives in the registry (source of truth), not duplicated here. Settings are loaded at start; missing file → defaults written.

## 9. Tech stack & dependencies (exact)

- **Runtime:** .NET 8, `net8.0-windows10.0.17763.0` (for WinRT toast APIs), `<UseWPF>true</UseWPF>`.
- **HID:** `HidSharp` (NuGet) — `DeviceList.Local.GetHidDevices(0x1532, …)`, `HidStream.SetFeature/GetFeature`, 91-byte feature buffer (incl. report-id byte). CRC computed by us.
- **Fluent UI:** `wpf-ui` (`Wpf.Ui.Controls.FluentWindow`, Mica). Tray icon via a hosted WinForms `NotifyIcon` (icon bitmap drawing is easiest there) or WPF-UI's tray — to be settled in planning.
- **Toasts:** `CommunityToolkit.WinUI.Notifications` (`ToastContentBuilder`, `ToastNotificationManagerCompat`).
- **Startup/single-instance:** BCL only — `Microsoft.Win32.Registry`, `System.Threading.Mutex`.

## 10. Build & distribution

- `dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=true`.
- Output: one `.exe`, no installer in v1 (drop it anywhere; tray menu toggles run-at-login).
- No code signing in v1 (SmartScreen may warn on first run — acceptable for personal use).

## 11. Empirical unknowns — confirm at first run (build a tiny probe first)

These are confirmed on the real device early in implementation (a throwaway console probe before the UI):

1. Which PID actually answers (expect `0x00A8`); confirm the `0xFF00` collection accepts SetFeature/GetFeature.
2. That transaction id `0x1f` returns `status==0x02`, a valid reply CRC, and a plausible 0–100 value.
3. The battery scaling: confirm raw byte `[9]` is 0–255 (×100/255), not already a 0–100 value (avoid a 2.55× error).
4. The charging command (`0x07/0x84`) returns a meaningful flag on this device (only openrazer implements it — no independent client confirmation).
5. Minimum reliable SET→GET delay (start ~400 ms, tune down).
6. That battery still reads from `0x00A8` while docked/charging.

## 12. Testing & verification

- **Protocol unit tests:** report-builder byte layout, CRC values (battery query CRC = `0x85`, charging = `0x81`), reply parsing/scaling — pure functions, no device needed.
- **State-machine tests:** `BatteryMonitor` low-battery edge logic (cross, re-arm, charging suppresses), unknown/offline transitions — with a faked `RazerDevice`.
- **Manual device checks (the §11 probe):** real reads while charging/discharging/undocked/asleep.
- **Footprint check:** confirm idle CPU ~0% and RAM in target range after the popup has been opened once.

## 13. Risks

- **Transaction-id / PID drift across firmware** → mitigated by the runtime probe + list-based enumeration.
- **Charging flag unverified by a 2nd client** → confirm at first run; if unreliable, fall back to showing battery only (still meets the core success criterion).
- **WPF footprint** higher than a pure-native app → accepted trade for the expandable GUI base; fallback noted in §4.3.
- **Toast on unpackaged app** quirks (generic app name on older Win10) → minor; set display name; user is on Win11.

---

*Approved via brainstorming session 2026-06-19. Next: implementation plan (writing-plans).*
