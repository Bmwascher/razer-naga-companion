# Naga V2 Pro Battery Tray — Design Spec

- **Date:** 2026-06-19
- **Status:** Approved design (v1) — ready for implementation planning
- **Working repo name:** `naga-battery-tray` (rename freely; internal identifier `NagaBatteryTray`)
- **Target hardware:** Razer Naga V2 Pro (wireless, on Razer Mouse Dock Pro), Windows 11
- **Display strings:** device = `Naga V2 Pro`; app/toast display name = `Naga Battery Tray`; toast AUMID = `NagaBatteryTray`.

---

## 1. Goal & motivation

Replace Razer Synapse **for the single purpose of seeing the Naga V2 Pro's battery level and charging state** from the Windows system tray. Synapse runs several background services at a combined ~150–400 MB+ just to surface a battery number. This tool does that one job in a fraction of the footprint, with no Synapse dependency. Because charging state is read from the mouse (not the dock), the app also doubles as a **dock-charging diagnostic** — relevant to the user's currently-misbehaving wireless charger: dock the mouse and watch whether it flips to "charging."

This is **v1 of a deliberately expandable base**: the user may later grow it into a polished config GUI and key remapping (a Synapse-lite). v1 builds none of that, but is architected so those bolt on with localized change.

## 2. Success criteria

A build is "done" when:

1. The tray icon **always shows the current battery %** as a readable number.
2. **Left-clicking the tray icon toggles** a popup showing battery %, charging status, and last-updated time.
3. A **low-battery desktop toast** fires once when the level crosses at/below a configurable threshold (default 15%) while not charging, and re-arms only after the level recovers above the threshold.
4. **Idle CPU ≈ 0%** (one HID poll every ~60 s); resident RAM in the tens of MB.
5. **Runs at login**, **single instance**, **no admin/UAC**.
6. Reads battery with **Synapse not installed/running**.
7. Degrades gracefully when the mouse is asleep/offline (shows an "unknown" state, never crashes, and never shows a stale number as if live).

## 3. Non-goals (v1) and designed-for-later

**Out of scope for v1 (YAGNI):** button/key remapping, DPI/polling-rate/lighting control, profiles, battery-history graph, a multi-device switcher UI, light theme, installer/auto-update.

**Architected so these are a localized change later (do not build now):**
- **Multi-device:** `RazerDevice` enumerates and exposes a *list*, so discovery already generalizes. **Honest scope note:** `BatteryMonitor` holds a single `DeviceState` and consumers subscribe to one `StateChanged`, so adding a 2nd **battery** device later means keying state/events by device id and updating subscribers — a small, contained change, not a rewrite, but not zero. (The Mouse Dock Pro is USB-powered, has **no battery**, and is never a device entry.) **Charging status — including "is the dock charging the mouse" — needs no multi-device plumbing:** it is read from the *mouse* (`0x84`), so single-device v1 fully delivers it.
- **Config GUI:** the popup's **Settings** button is the reserved on-ramp (disabled in v1; see §6.4).
- **Key remapping:** rides on `RazerDevice`'s **transport primitive** (open stream, build report, CRC, Set/GetFeature round-trip). A future `RemapEngine` reuses that primitive for on-device writes, plus a *separate* input-hook layer for host-side hooks. The battery facade is untouched.

## 4. Architecture

### 4.1 Process model — one process, two modes

A single tray application (.NET 8, Windows-only):

- **Resident mode (always running):** a WinForms `NotifyIcon` in the tray + a `System.Threading.Timer` polling battery. **No WPF popup / visual surface is constructed** in this state (a hosted `NotifyIcon` does keep a hidden message-pump window — that's the only window, and it's cheap). This is the near-0 idle state.
- **On-demand window:** the WPF popup is constructed only on first tray click, and hidden/released when it loses focus. The heavy WPF surface isn't paid for until the user looks at it.

### 4.2 Layers

```
  AppHost          single-instance mutex, run-at-login, ISettingsStore, power/session events, lifecycle, DI wiring
  TrayIconController renders battery % onto the icon bitmap, tooltip, right-click menu
  PopupWindow (WPF)  on-demand Fluent panel (the A+D hybrid)
  Notifications      edge-triggered low-battery toast
  BatteryMonitor     polling timer, current DeviceState, charging detection, low-batt edge, staleness
  RazerDevice        (a) transport primitive: HID open + report build + CRC + Set/GetFeature round-trip
                     (b) battery facade on top; exposes a device LIST (battery/charging now; buttons later)
```

Each layer has one responsibility and a narrow interface. `BatteryMonitor` depends on `RazerDevice`; `TrayIconController`, `PopupWindow`, and `Notifications` subscribe to `BatteryMonitor`'s events. No layer above `RazerDevice` knows the HID details. `ISettingsStore` (typed load/save of `settings.json`) is owned by `AppHost` and injected wherever needed (incl. `RazerDevice` for the cached transaction id / read delay — see §6.1), so no layer reaches across to the file directly.

### 4.3 Footprint strategy & honest numbers

- Publish as **self-contained, single-file, trimmed** .NET 8 (no runtime install needed).
- **NativeAOT is NOT used** — unsupported for WPF (COM/reflection). Trimmed single-file is the lever instead.
- **`PublishTrimmed` on WPF is best-effort:** expect IL2xxx trim warnings, and XAML / wpf-ui resource resolution can break at runtime. **Test the published exe early**; be ready to fall back to `TrimMode=partial` (with roots) or `PublishTrimmed=false`. The RAM/disk numbers below hold either way.
- **Realistic footprint:** ~**45–55 MB** working-set RAM (WPF + WPF-UI pulls in `PresentationFramework`), ~**30–45 MB** on disk. Idle CPU ~0% (one poll/60 s). Still **3–8× lighter than Synapse**.
- Fallback if footprint must drop toward ~30–40 MB: a WinForms-only shell with a hand-styled popup — explicitly **not** chosen (weaker base for the future GUI).

## 5. Razer HID protocol (verified)

Verified against the openrazer kernel driver (read twice), the RazerBatteryTaskbar device table, and the hsutungyu Python tool, then reconciled. Confidence **high** on the protocol; §11 items are confirmed empirically at first run.

### 5.1 Target device & interface

- **Vendor:** `0x1532`. **Query the MOUSE, not the dock.** When docked, the Naga still enumerates as its own USB device (**wireless PID `0x00A8`**, wired `0x00A7`); the Mouse Dock Pro (`0x00A4`) is separate and does **not** proxy the mouse's battery. The user's machine confirms the Naga enumerates as **`0x00A8`**.
- **Interface/collection:** the vendor control collection, **usage page `0xFF00`**. VID-only enumeration returns **multiple HID collections sharing PID `0x00A8`**; selecting the `0xFF00` collection via `(device.GetTopLevelUsage() >> 16) == 0xFF00` is **mandatory** (not first-match) — the other collections will reject or hang on feature reports.
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

- **Battery level:** class `0x07`, id `0x80`, data_size `0x02`. Reply value at **report byte `[9]`** is raw 0–255 → `percent = round(reply[9] * 100 / 255)`.
- **Charging status:** class `0x07`, id `0x84`, data_size `0x02`. Reply report byte `[9]` ≠ 0 → charging.
- **CRC (canonical):** `crc = 0; for (i = 2; i <= 87; i++) crc ^= report[i];` — bytes `[2..87]` inclusive; excludes byte0(status), byte1(transaction_id), byte88(crc), byte89(reserved). The §12 example values `0x85` (battery) / `0x81` (charging) derive from exactly this range.

### 5.3 Transport sequence

**Index rule:** the HID feature buffer prepends a report-id byte, so **`buffer[i] = report byte[i-1]`** (every buffer index = report index + 1).

1. Build the 91-byte HID **feature** buffer: `buffer[0] = 0x00` (report id), `buffer[1..90]` = the 90-byte report above.
2. **`SetFeature(buffer)`** (HID SET_REPORT).
3. **Wait ~400 ms** (wireless relay latency; tunable, see §8 — too short → busy/stale).
4. **`GetFeature(buffer)`** (HID GET_REPORT).
5. **Validate & parse the reply:**
   - `status = buffer[1]`. **Reply CRC** = XOR of `buffer[3..88]`, compare to `buffer[89]`.
   - **`status == 0x02`** (success) and CRC matches → value = `buffer[10]` (= report byte `[9]`). Use it.
   - **`status == 0x01`** (busy) → retry the GetFeature once after ~200 ms; still busy → **Unknown**.
   - any other status (`0x03`–`0x05`, …) or CRC mismatch → **Unknown** immediately.

### 5.4 Transaction-id probe (robustness)

`0x1f` is high-confidence, but resolve it empirically and cache it:

- Probe an ordered set, accepting the first that yields **`status==0x02` + valid reply CRC + a plausible 0–100 value**:
  `0x1f, 0x3f, 0x00, 0xff, 0x08, 0x88, 0x1d, 0x9f`
- **`cachedTransactionId` defaults to absent/null = "unprobed."** Probe runs lazily on the **first successful enumeration**, then writes the winner to settings; later runs reuse it and skip probing. If the mouse is offline at launch, **skip (don't fail) the probe** and retry on the next successful enumeration. Parse stored value with `Convert.ToByte(s, 16)`.

## 6. Components

### 6.1 `RazerDevice` (transport primitive + battery facade)
- **Transport primitive:** enumerates `0x1532` devices, applies the mandatory `0xFF00` filter (§5.1), opens an `HidStream`; builds the 90-byte report + CRC; runs the Set→wait→Get round-trip with the status/CRC validation of §5.3. **Implements `IDisposable`** and owns the `HidStream`.
- **Battery facade:** `Task<BatteryReading> ReadAsync(CancellationToken)` issues **both** the `0x80` (level) and `0x84` (charging) transactions sequentially and composes one `BatteryReading { percent, isCharging, isPresent, timestamp }`. If **either** transaction fails (IO or validation), `isPresent = false`.
- **Error handling (category):** wrap enumerate/open/SetFeature/GetFeature in try/catch for `IOException`, `UnauthorizedAccessException`, sharing violations (Synapse/second instance holding the handle), and device-disappeared-mid-read. On any of these, return `isPresent = false` (→ Unknown) and **log once** (not every tick). Swallow `ObjectDisposedException` from a read that loses the shutdown race (§6.6).
- **Tuning via injection:** reads `cachedTransactionId` and `setReadDelayMs` from the injected `ISettingsStore`, and persists a newly discovered transaction id back through it (no direct file access).
- Exposes a **device list** (`IReadOnlyList<RazerDeviceInfo>`) even though v1 has one entry.
- Owns reconnection: a stale handle (mouse slept/undocked) transparently re-enumerates on the next read.

### 6.2 `BatteryMonitor`
- Holds current **`DeviceState` = `Unknown` | `Online { percent, charging }`** (charging is a *field*, not a separate top-level state). `isPresent == false` **and** any failed/timeout read both map to `Unknown` (identical presentation); `isPresent` is informational/logged only.
- **`System.Threading.Timer`:** 60 s default; **15 s while charging** (interval for the *next* tick is chosen from the charging flag read in the *current* poll). Each poll runs both `0x80`+`0x84` (via `ReadAsync`).
- **Read serialization:** all reads (timer tick and "Refresh now") run behind a single `SemaphoreSlim(1,1)` so only one Set/GetFeature round-trip is ever in flight (HidStream is not concurrency-safe). `StateChanged` is **marshaled onto the UI dispatcher** before any subscriber touches the NotifyIcon/popup.
- **Low-battery edge logic:** *armed* when `Online && !charging && percent >= threshold`. **Fires** (raises `LowBatteryCrossed`) on the falling edge to `percent <= threshold` while `!charging`. **Re-arms only** when `percent >= threshold` again. Charging **suppresses** firing but does **not** re-arm (so plugging in below threshold then unplugging won't double-toast).
- **Staleness ceiling:** after **> 3 consecutive poll intervals** without a successful read, force `DeviceState = Unknown` (satisfies criterion 7 — never show a stale number as live).

### 6.3 `TrayIconController`
- Renders the % onto a **DPI-correct bitmap** each update via `System.Drawing` (GDI+): size from `GetSystemMetricsForDpi(SM_CXSMICON, dpi)`, re-rendered on DPI / display-settings change. Color per the Dynamic-by-level scheme; small **bolt overlay/tint when charging**.
- **GDI hygiene:** after assigning the new icon, call `DestroyIcon` on the previous `HICON` (from `Icon.FromHandle`) to avoid a handle leak.
- Tooltip: `Naga V2 Pro — 87% (charging) · updated 12s ago`. Unknown → `–` icon + "no response".
- **Left-click toggles the popup.** Right-click menu: **Refresh now** · **Run at startup** (checkable) · **Quit**.

### 6.4 `PopupWindow` (WPF, on-demand) — the A+D hybrid
- Borderless Fluent panel (WPF-UI `FluentWindow`, Mica), dismissed on focus-loss.
- **Placement (multi-monitor / per-monitor-DPI safe):** get the tray icon's physical rect (`Shell_NotifyIconGetRect`), choose the monitor under it (`Screen.FromPoint`), clamp the popup to that monitor's `WorkingArea`, and convert physical px → WPF DIPs at that monitor's DPI. App manifest declares **PerMonitorV2** DPI awareness.
- Content: header (status dot + "Naga V2 Pro" + online/offline); large % (Dynamic color); slim level bar; neutral **⚡ charging chip**; mini red→amber→green scale; "updated Ns ago". Footer: **Refresh** and **Settings** — **Settings is rendered disabled with a "Coming soon" tooltip in v1** (reserved for the future config window; not a dead-looking live button).
- Constructed lazily; released after close.

### 6.5 `Notifications`
- `CommunityToolkit.WinUI.Notifications` toast on `LowBatteryCrossed`: "Naga V2 Pro battery at 14%." `ToastNotificationManagerCompat` auto-registers the COM activator/AUMID (`NagaBatteryTray`) on first `.Show()` — no Start-menu shortcut needed for an unpackaged app. One toast per crossing; re-arm per §6.2.

### 6.6 `AppHost`
- **Single instance** via named `Mutex` = `Local\NagaBatteryTray-b3f1c2d4-5a6e-4f80-9c1a-2e7d8b4f6a90` (one fixed hardcoded GUID; **`Local\`** = per-user, matches no-admin intent). Second launch exits.
- **Run-at-login** via `HKCU\…\Run` value = `Environment.ProcessPath` (quoted), toggled from the tray menu. Off by default; user opts in.
- **Resume/session handling:** subscribe to `SystemEvents.PowerModeChanged` (Resume) and `SystemEvents.SessionSwitch` (SessionUnlock); on either, **force an immediate refresh + re-enumeration** (the post-sleep HID handle is dead and the timer would otherwise leave Unknown for up to a full interval).
- **Startup with mouse absent:** start in `Unknown`, skip the probe, run/cache it lazily on first successful enumeration; tray shows `–` until the device answers.
- Owns `ISettingsStore`; wires the layers; on **Quit**: stop the timer, cancel/await the in-flight read (via the read `SemaphoreSlim` / `CancellationToken`) **before** disposing `RazerDevice`, swallowing a losing-race `ObjectDisposedException`.

## 7. UI design (locked)

- **Tray icon:** the live battery number, recolored by level (green/amber/red), bolt/tint when charging.
- **Popup:** A+D hybrid, **Dynamic-by-level** color. Razer green = healthy/charging tone; amber mid; red low. Charging shown as a neutral bolt chip so green keeps meaning "level."
- **States (two):** **Unknown/offline** (`–`, muted, "no response") and **Online** (renders the number; when `charging == true`, show the bolt chip and poll at 15 s). Charging is a property of Online, not a third state.
- Dark theme only in v1 (light theme is a future option).

## 8. Settings & persistence

Typed `ISettingsStore` over JSON at `%APPDATA%\NagaBatteryTray\settings.json`:

```json
{
  "pollIntervalSeconds": 60,
  "pollIntervalChargingSeconds": 15,
  "lowBatteryThreshold": 15,
  "lowBatteryNotify": true,
  "cachedTransactionId": null,
  "setReadDelayMs": 400
}
```

- `cachedTransactionId`: `null` = unprobed (see §5.4).
- `setReadDelayMs`: default 400; tunable **down to a ~150 ms floor** (below that the wireless round-trip returns busy/stale).
- `lowBatteryThreshold`: inclusive — fires at `percent <= threshold` (15 fires at exactly 15).
- Run-at-startup state lives in the registry (source of truth), not duplicated here. Missing file → defaults written.

## 9. Tech stack & dependencies (exact)

- **Runtime:** .NET 8, `net8.0-windows10.0.17763.0` (for WinRT toast APIs). `<UseWPF>true</UseWPF>` **and** `<UseWindowsForms>true</UseWindowsForms>` (WinForms is referenced solely for `NotifyIcon` + `System.Drawing` icon rendering).
- **Tray host:** **WinForms `NotifyIcon`** (chosen over WPF-UI's tray — it's the native GDI+ bitmap + `Icon.FromHandle` path of §6.3 and keeps resident mode free of a visible window).
- **HID:** `HidSharp` — `DeviceList.Local.GetHidDevices(0x1532, …)`, the `0xFF00` filter, `HidStream.SetFeature/GetFeature`, 91-byte feature buffer (incl. report-id byte; `GetMaxFeatureReportLength()` includes it, expect 91). CRC computed by us.
- **Fluent UI:** `wpf-ui` (`Wpf.Ui.Controls.FluentWindow`, Mica).
- **Toasts:** `CommunityToolkit.WinUI.Notifications` (`ToastContentBuilder`, `ToastNotificationManagerCompat`).
- **Startup/single-instance/power events:** BCL only — `Microsoft.Win32.Registry`, `System.Threading.Mutex`, `Microsoft.Win32.SystemEvents`.

## 10. Build & distribution

- `dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=true`.
- **Verify the trimmed published exe early** (see §4.3 trim caveat); fall back to `TrimMode=partial`/roots or `PublishTrimmed=false` if WPF/wpf-ui resources fail at runtime.
- Output: one `.exe`, no installer in v1 (drop it anywhere; tray menu toggles run-at-login).
- No code signing in v1 (SmartScreen may warn on first run — acceptable for personal use).

## 11. Empirical unknowns — confirm at first run (build a tiny probe first)

Confirmed on the real device early (a throwaway/diagnostic console probe before the UI — see §13 for its lifecycle):

1. Which PID answers (expect `0x00A8`); the `0xFF00` collection accepts Set/GetFeature; **confirm the feature report id is `0x00`** — if the vendor collection reports a non-zero report id, `buffer[0]` must match it.
2. transaction id `0x1f` returns `status==0x02`, valid reply CRC, plausible 0–100 value.
3. Battery scaling: raw byte `[9]` is 0–255 (×100/255), not already 0–100 (avoid a 2.55× error).
4. The charging command (`0x07/0x84`) returns a meaningful flag (only openrazer implements it — no independent client confirmation; if unreliable, fall back to battery-only per §13).
5. Minimum reliable SET→GET delay (start ~400 ms, tune toward the ~150 ms floor).
6. Battery still reads from `0x00A8` while docked/charging.

## 12. Testing & verification

- **Protocol unit tests:** report-builder byte layout; **CRC values (battery `0x85`, charging `0x81`)**; reply parsing/scaling; reply-CRC validator over `buffer[3..88]`. Pure functions, no device.
- **State-machine tests** (faked `RazerDevice`): low-battery edge logic (fire at `percent <= threshold`; **boundary: fires at exactly 15**; charging suppresses; re-arm only on recovery above threshold); Unknown/offline transitions; **staleness ceiling** (>3 missed intervals → Unknown); charging-driven interval switch.
- **Concurrency test:** overlapping "Refresh now" + timer tick never run two reads at once (semaphore).
- **Manual device checks (the §11 probe):** real reads while charging / discharging / undocked / asleep / resume-from-sleep.
- **Footprint check:** idle CPU ~0% and RAM in range after the popup has been opened once.

## 13. Risks & decisions

- **Transaction-id / PID drift across firmware** → runtime probe + list-based enumeration. **Probe lifecycle:** keep the probe as a permanent hidden diagnostic subcommand (`NagaBatteryTray.exe --probe`) for re-diagnosing this risk, not throwaway code.
- **Charging flag unverified by a 2nd client** → confirm at first run; if unreliable, show battery only (still meets the core criterion).
- **WPF footprint / trim fragility** → accepted trade for the expandable GUI base; trim fallback in §4.3/§10.
- **Toast on unpackaged app** quirks (generic name on older Win10) → set display name (`Naga Battery Tray`); user is on Win11.

---

*Approved via brainstorming session 2026-06-19; hardened via adversarial spec review the same day. Next: implementation plan (writing-plans).*
