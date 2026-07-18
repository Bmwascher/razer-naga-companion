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
