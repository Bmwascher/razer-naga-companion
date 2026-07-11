using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using HidSharp;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Diagnostics;

public static class ProbeCommand
{
    public static int Run()
    {
        Console.WriteLine("Naga Battery Tray - HID probe (raw HidD_*, zero-access open)\n");

        foreach (int pid in new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidWired })
        {
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                int max = -1;
                try { max = dev.GetMaxFeatureReportLength(); } catch { }
                if (max != RazerProtocol.BufferLength) continue; // only collections with the 90+1 feature report

                Console.WriteLine($"PID 0x{pid:x4} {Mi(dev.DevicePath)} max={max} -> raw zero-access open");
                using var h = CreateFile(dev.DevicePath, 0, 0x3, IntPtr.Zero, 0x3, 0, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    Console.WriteLine($"  CreateFile failed err={Marshal.GetLastWin32Error()}");
                    continue;
                }
                Console.WriteLine("  opened OK (zero-access)");
                foreach (byte tid in RazerProtocol.TransactionIdProbeSet)
                    Console.WriteLine($"  tid 0x{tid:x2}: {OneShot(h, tid)}");
            }
        }
        Console.WriteLine("\n--- RazerDevice.ReadAsync (production class) ---");
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"naga-probe-{Guid.NewGuid():N}.json"));
        using (var device = new RazerDevice(store))
        {
            var reading = device.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"  present={reading.IsPresent} percent={reading.Percent}% charging={reading.IsCharging} cachedTid={store.Settings.CachedTransactionId}");
        }

        Console.WriteLine("\nLegend: status 0x02=success, 0x01=busy(asleep), other=fail.");
        return 0;
    }

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

    public static int RunDock()
    {
        Console.WriteLine("Naga Battery Tray - Mouse Dock Pro probe (PID 0x00A4, battery + charging)\n");

        bool any = false;
        foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, RazerProtocol.DockPid))
        {
            int max = -1;
            try { max = dev.GetMaxFeatureReportLength(); } catch { }
            if (max != RazerProtocol.BufferLength) continue; // only the 90+1 feature-report collection
            any = true;

            Console.WriteLine($"DOCK 0x{RazerProtocol.DockPid:x4} {Mi(dev.DevicePath)} max={max} -> raw zero-access open");
            using var h = CreateFile(dev.DevicePath, 0, 0x3, IntPtr.Zero, 0x3, 0, IntPtr.Zero);
            if (h.IsInvalid) { Console.WriteLine($"  CreateFile failed err={Marshal.GetLastWin32Error()}"); continue; }
            Console.WriteLine("  opened OK (zero-access)");

            foreach (byte tid in new byte[] { 0x1f, 0xff })
            {
                Console.WriteLine($"  tid 0x{tid:x2} battery : {DockOneShot(h, tid, RazerProtocol.CommandIdBattery)}");
                Console.WriteLine($"  tid 0x{tid:x2} charging: {DockOneShot(h, tid, RazerProtocol.CommandIdCharging)}");
            }
        }

        if (!any)
            Console.WriteLine($"No dock collection found (VID 0x{RazerProtocol.VendorId:x4} PID 0x{RazerProtocol.DockPid:x4}, feature len {RazerProtocol.BufferLength}).");
        Console.WriteLine("\nRun in each state: mouse off-dock / docked+charging / docked+asleep / dock present not charging.");
        Console.WriteLine("Legend: status 0x02=success, 0x01=busy(asleep/no relay), other=fail. battery raw 0..255; charging 0/1 at byte[10].");
        return 0;
    }

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
        if (!RunAcceptanceProbe(s, capture)) { capture.Save(); return 1; }
        capture.Save();
        RunGridDiscovery(s, capture);
        capture.Save();
        Console.WriteLine("(Steps 4-5 land in the next task.)");
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
                    foreach (var (_, id) in batch)
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

    private static string OneShot(SafeFileHandle h, byte tid)
    {
        try
        {
            var buf = RazerProtocol.BuildFeatureBuffer(tid, RazerProtocol.CommandIdBattery);
            if (!HidD_SetFeature(h, buf, buf.Length))
                return $"SetFeature failed err={Marshal.GetLastWin32Error()}";
            Thread.Sleep(400);
            var reply = new byte[RazerProtocol.BufferLength];
            reply[0] = 0;
            if (!HidD_GetFeature(h, reply, reply.Length))
                return $"GetFeature failed err={Marshal.GetLastWin32Error()}";
            var r = RazerProtocol.ParseReply(reply, out byte v);
            return $"status=0x{reply[1]:x2} {r} raw={v} ({RazerProtocol.ScaleBattery(v)}%)";
        }
        catch (Exception ex) { return $"EXC {ex.Message}"; }
    }

    private static string DockOneShot(SafeFileHandle h, byte tid, byte commandId)
    {
        try
        {
            var buf = RazerProtocol.BuildFeatureBuffer(tid, commandId);
            if (!HidD_SetFeature(h, buf, buf.Length))
                return $"SetFeature failed err={Marshal.GetLastWin32Error()}";

            // The dock relays over RF; a sleeping/charging mouse can answer 0x01 busy for a while.
            // Poll the reply until it stops being busy (relay completes) or we give up (~2.2 s).
            byte[] reply = new byte[RazerProtocol.BufferLength];
            int tries = 0;
            for (; tries < 10; tries++)
            {
                Thread.Sleep(tries == 0 ? 400 : 200);
                reply = new byte[RazerProtocol.BufferLength];
                reply[0] = 0;
                if (!HidD_GetFeature(h, reply, reply.Length))
                    return $"GetFeature failed err={Marshal.GetLastWin32Error()}";
                if (reply[1] != 0x01) break; // not busy: success / fail / timeout
            }

            var hex = string.Join(" ", reply.Take(16).Select(b => b.ToString("x2")));
            var r = RazerProtocol.ParseReply(reply, out byte v);
            string decoded = commandId == RazerProtocol.CommandIdBattery
                ? $"raw={v} ({RazerProtocol.ScaleBattery(v)}%)"
                : $"charging={v}";
            return $"status=0x{reply[1]:x2} {r} {decoded} (tries={tries + 1})  [{hex}]";
        }
        catch (Exception ex) { return $"EXC {ex.Message}"; }
    }

    private static string Mi(string p)
    {
        int i = p.IndexOf("mi_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && i + 5 <= p.Length ? p.Substring(i, 5) : "mi_??";
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
}
