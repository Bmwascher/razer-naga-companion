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

        Console.WriteLine("[1/3] Inventory (verified commands only)");
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

        if (inv.Slots.Length < 2)
        {
            string why = $"only {inv.Slots.Length} existing slot(s): diff-across-states cannot discriminate (spec §5.1) - inventory-only run";
            Console.WriteLine($"  {why}");
            string verdictLine = inv.ListOk
                ? "INVENTORY ONLY - create a second onboard slot (e.g. via the dashboard) and re-run to hunt."
                : "profile list UNREADABLE — cannot enumerate slots; re-run when the list answers";
            capture.Add($"- {why}\n\n## Verdict\n{verdictLine}");
            return 0;
        }

        Console.WriteLine("[2/3] Pass 1 - sourced shortlist");
        var corpus1 = Shortlist();
        capture.Add(RenderCorpus(s, corpus1, "pass 1 (sourced shortlist)"));
        var visits1 = RunTour(s, capture, inv, fingerprint, corpus1, "pass 1");
        string? abortedDuring = visits1 is null ? "pass 1" : null;
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
                if (visits2 is null) abortedDuring = "pass 2";
                else hits = AnalyzeAndRecord(capture, corpus2, visits2, "pass 2");
            }
            else capture.Add("- pass 2: declined by user (recorded)");
        }

        Console.WriteLine("\n[3/3] Integrity re-check (spec §4.5)");
        if (!s.Alive())
        {
            capture.Add("## Integrity re-check\nIMPOSSIBLE — device not answering (no evidence of change; re-run when it reconnects).");
            Console.WriteLine("  IMPOSSIBLE - device not answering (no evidence of change; re-run when it reconnects).");
        }
        else
        {
            var after = ReadInventory(s);
            var diffs = CompareInventories(inv, after);
            capture.Add(RenderInventory(after, "Inventory (after)"));
            capture.Add(diffs.Count == 0
                ? "## Integrity re-check\nUNCHANGED - every observable profile surface byte-identical to the pre-run inventory."
                : "## Integrity re-check\n**CHANGED:** " + string.Join("; ", diffs));
            Console.WriteLine(diffs.Count == 0 ? "  UNCHANGED - all profile surfaces byte-identical." : $"  **CHANGED:** {string.Join("; ", diffs)}");
        }

        InputFeelPrompt(capture, "final");

        string verdict = abortedDuring is not null
            ? $"## Verdict\n**ABORTED** during {abortedDuring} — partial evidence only; no verdict on the corpus."
            : hits.Count > 0
                ? "## Verdict\n**HIT:** " + string.Join("; ", hits.Select(h =>
                      $"{h.Cand.Key} report[{h.Hit.ReportOffset}] ({string.Join(", ", h.Hit.SlotToValue.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}->0x{kv.Value:x2}"))})"))
                  + "\nFollow-up: teach RazerDevice/BatteryMonitor the read; event-driven only (no polling)."
                : "## Verdict\nNO HIT in the enumerated corpus (see corpus tables above for the exact shapes tried). " +
                  "The Profile card keeps the effective-action inference.";
        capture.Add(verdict);
        Console.WriteLine($"\n{verdict.Replace("## Verdict\n", "Verdict: ")}");
        Console.WriteLine($"\nCapture saved: {capture.StampedPath} (+ probe-profile-latest.md)");
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

    // ---- hunt: candidates, state tour, analysis ----

    /// <summary>A fully specified probe request (spec §5.3): shape + provenance. Status is
    /// hardware-verified | documented | speculative.</summary>
    private sealed record ProbeCandidate(string Key, byte CommandId, byte DataSize, byte[] Args, string Source, string Status);

    /// <summary>Pass-1 corpus. 0x05/0x81 rides as the hardware-verified control — it must answer in
    /// every state and must NOT track the slot (the existing-slot set doesn't change when the active
    /// slot does). Candidates below the control were added by the plan's research step, each with a
    /// readable source; if research found none, the control stands alone and pass 2 carries discovery.
    /// Research (spec §5.3): openrazer/openrazer driver/razerchromacommon.c has ZERO class-0x05
    /// (profile) commands of any kind (grepped every get_razer_report(0x05, ...) call site — none
    /// exist; the file only builds classes 0x00/0x02/0x03/0x04/0x07/0x0E/0x0F), so that source
    /// contributes nothing beyond the existing control. geezmolycos/razerqdhid documents three more
    /// class-0x05 GETs: docs/cmd_profile-en.md plus the byte-exact reference implementation in
    /// public/py/qdrazer/device.py + protocol.py (Report.new mirrors our own BuildReport/ComputeCrc
    /// field-for-field: transaction id, data_size, command_class, command_id, XOR CRC over [2..87]).</summary>
    private static List<ProbeCandidate> Shortlist() => new()
    {
        new("0x05/0x81 ds06 (control)", 0x81, 0x06, new byte[6], "Phase B spike 2026-07-11 (hardware)", "hardware-verified"),
        new("0x05/0x80 ds01 (available-count control)", 0x80, 0x01, new byte[1],
            "geezmolycos/razerqdhid public/py/qdrazer/device.py get_profile_available_count -> " +
            "sr_with(0x0580, '>B') [class 0x05, id 0x80, data_size 1]; protocol.py Report.new", "documented"),
        new("0x05/0x8a ds01 (total-count)", 0x8a, 0x01, new byte[1],
            "geezmolycos/razerqdhid public/py/qdrazer/device.py get_profile_total_count -> " +
            "sr_with(0x058a, '>B') [class 0x05, id 0x8a, data_size 1]; protocol.py Report.new", "documented"),
        new("0x05/0x88 ds45 (profile-info: profile=0 direct, start=0)", 0x88, 0x45, new byte[] { 0x00, 0x00, 0x00 },
            "geezmolycos/razerqdhid public/py/qdrazer/device.py get_profile_info -> " +
            "sr_with(0x0588, '>BHH64s', profile.value, len(data)) [class 0x05, id 0x88, " +
            "data_size = calcsize('>BHH64s') = 69 = 0x45; args[0]=profile, args[1..2]=start offset " +
            "(big-endian u16), rest reply-shaped padding]; protocol.py Report.new", "documented"),
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
            for (int idx = 0; idx < corpus.Count; idx++)
            {
                var cand = corpus[idx];
                Console.WriteLine($"    {cand.Key} ({idx + 1}/{corpus.Count})...");
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
                    : $"- {cand.Key} sample {i + 1}: status=0x{r[1]:x2} crc={(CrcOk(r) ? "ok" : "BAD")} [{ProbeCommand.Hex(r, 91)}]");
            }
        return sb.ToString();
    }

    /// <summary>Same CRC check RazerProtocol.ValidateReply applies to a reply (private there): XOR of
    /// buffer[3..88] inclusive vs buffer[89]. Reimplemented here since this file only sees the public
    /// surface (spec §5.3 read-only boundary).</summary>
    private static bool CrcOk(byte[] r)
    {
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= r[i];
        return crc == r[89];
    }

    private static void InputFeelPrompt(ProfileCapture capture, string when)
    {
        Console.Write($"\n  INPUT-FEEL CHECK ({when}, hard gate): move the mouse around now - any stutter/lag? [y/N] ");
        bool lag = Console.ReadKey(intercept: true).Key == ConsoleKey.Y;
        Console.WriteLine();
        capture.Add($"- input-feel ({when}): {(lag ? "**LAG REPORTED**" : "clean")}");
    }

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
            if (!complete)
            {
                sb.AppendLine($"- {cand.Key}: not analyzable (missing/non-success replies — see visit tables)");
                if (cand.CommandId == RazerProtocol.CommandIdGetProfileList && cand.Status == "hardware-verified")
                    sb.AppendLine("  - **WARNING: the hardware-verified control did not answer cleanly — treat this run as suspect.**");
                continue;
            }

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
        if (before.ListOk && !after.ListOk)
            diffs.Add("profile list unreadable after (inconclusive)");
        else if (before.ListOk != after.ListOk || before.Capacity != after.Capacity || !before.Slots.SequenceEqual(after.Slots))
            diffs.Add("profile list changed");

        if (before.ModeOk && !after.ModeOk)
            diffs.Add("device mode unreadable after (inconclusive)");
        else if (before.ModeOk != after.ModeOk || before.Mode != after.Mode)
            diffs.Add("device mode changed");

        foreach (var b in before.Rows)
        {
            var a = after.Rows.FirstOrDefault(r => r.Slot == b.Slot);
            if (a.Actions is null) { diffs.Add($"slot {b.Slot} missing after"); continue; }
            for (int p = 1; p <= NagaV2ProButtons.Count; p++)
            {
                var x = b.Actions[p - 1]; var y = a.Actions[p - 1];
                bool same = (x is null && y is null) ||
                    (x is { } xa && y is { } ya && xa.Category == ya.Category && xa.Data.SequenceEqual(ya.Data));
                if (same) continue;
                diffs.Add(x is not null && y is null
                    ? $"slot {b.Slot} pos {p} unreadable after (inconclusive)"
                    : $"slot {b.Slot} pos {p} changed");
            }
        }
        return diffs;
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
            string ver = (Attribute.GetCustomAttribute(typeof(ProfileProbeCommand).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute)) as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion ?? typeof(ProfileProbeCommand).Assembly.GetName().Version?.ToString() ?? "?";
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
