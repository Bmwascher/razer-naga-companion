using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Diagnostics;

/// <summary>Slot inventory row for --probe-profile analysis: the 12 grid actions read from one
/// onboard slot. Index = grid position - 1; null = that button's read failed.</summary>
internal readonly record struct SlotActions(byte Slot, RawButtonAction?[] Actions);

/// <summary>One candidate's replies (full 91-byte buffers) collected during one state visit
/// (spec §5.1). The tour's last visit re-visits the first slot; AnalyzeCandidate enforces
/// reproduction through the duplicated slot number.</summary>
internal sealed record StateSamples(byte Slot, IReadOnlyList<byte[]> Replies);

internal enum OffsetClass { Hit, Noise }

/// <summary>A reply byte that varies across states. ReportOffset uses 90-byte report indexing
/// (args live at report [8..87]); SlotToValue is the observed mapping (any encoding accepted —
/// bijectivity is required, literal slot numbers are not).</summary>
internal sealed record OffsetFinding(int ReportOffset, OffsetClass Class, IReadOnlyDictionary<byte, byte> SlotToValue);

/// <summary>Pure analysis for --probe-profile (spec 2026-07-18 §4.3/§5.2): fingerprint selection
/// and diff-across-states hit detection. No I/O — an analyzer bug must never force recollecting
/// hardware evidence.</summary>
internal static class ProfileProbeAnalysis
{
    // RawButtonAction's generated equality compares Data by reference — always compare by value.
    private static bool SameAction(RawButtonAction? a, RawButtonAction? b) =>
        a is { } x && b is { } y && x.Category == y.Category && x.Data.AsSpan().SequenceEqual(y.Data);

    /// <summary>Smallest set of grid positions (1-based) whose action tuples uniquely distinguish
    /// every slot (spec §4.3 — the LED-independent oracle). A position is eligible only if it read
    /// successfully on every slot. Null when fewer than 2 slots or no discriminating set exists.</summary>
    internal static int[]? SelectFingerprint(IReadOnlyList<SlotActions> inventory)
    {
        if (inventory.Count < 2) return null;
        var eligible = new List<int>();
        for (int p = 1; p <= NagaV2ProButtons.Count; p++)
            if (inventory.All(s => s.Actions[p - 1] is not null)) eligible.Add(p);

        for (int k = 1; k <= eligible.Count; k++)
            foreach (var combo in Combinations(eligible, k))
                if (Discriminates(inventory, combo)) return combo;
        return null;
    }

    private static bool Discriminates(IReadOnlyList<SlotActions> inventory, int[] positions)
    {
        for (int i = 0; i < inventory.Count; i++)
            for (int j = i + 1; j < inventory.Count; j++)
                if (positions.All(p => SameAction(inventory[i].Actions[p - 1], inventory[j].Actions[p - 1])))
                    return false;
        return true;
    }

    private static IEnumerable<int[]> Combinations(List<int> items, int k)
    {
        if (k == 0 || k > items.Count) yield break;
        var idx = new int[k];
        for (int i = 0; i < k; i++) idx[i] = i;
        while (true)
        {
            yield return idx.Select(i => items[i]).ToArray();
            int pos = k - 1;
            while (pos >= 0 && idx[pos] == items.Count - k + pos) pos--;
            if (pos < 0) yield break;
            idx[pos]++;
            for (int i = pos + 1; i < k; i++) idx[i] = idx[i - 1] + 1;
        }
    }

    /// <summary>The slot whose inventory matches the observed effective actions at the fingerprint
    /// positions. Null unless exactly one slot matches (a failed observed read can never match).</summary>
    internal static byte? MatchFingerprint(IReadOnlyList<SlotActions> inventory, int[] positions, RawButtonAction?[] observed)
    {
        var matches = inventory.Where(s => positions.All(p => SameAction(s.Actions[p - 1], observed[p - 1]))).ToList();
        return matches.Count == 1 ? matches[0].Slot : null;
    }

    /// <summary>Diff-across-states hit detection (spec §5.2). Hit = stable within every visit,
    /// bijective slot→value over ≥ 2 slots, reproduced on the revisit. Varies-but-fails = Noise
    /// (still reported — counters/echoes are worth seeing). Constant offsets are omitted.</summary>
    internal static IReadOnlyList<OffsetFinding> AnalyzeCandidate(IReadOnlyList<StateSamples> visits)
    {
        var findings = new List<OffsetFinding>();
        if (visits.Count == 0 ||
            visits.Any(v => v.Replies.Count == 0 || v.Replies.Any(r => r.Length != RazerProtocol.BufferLength)))
            return findings;

        for (int off = 8; off <= 87; off++)
        {
            int b = off + 1; // buffer prepends the report id
            bool stable = visits.All(v => v.Replies.All(r => r[b] == v.Replies[0][b]));
            var visitValues = visits.Select(v => (v.Slot, Value: v.Replies[0][b])).ToList();
            if (stable && visitValues.Select(x => x.Value).Distinct().Count() == 1) continue; // constant

            var slotToValue = new Dictionary<byte, byte>();
            bool reproduced = true;
            foreach (var (slot, value) in visitValues)
            {
                if (slotToValue.TryGetValue(slot, out byte prior)) { if (prior != value) reproduced = false; }
                else slotToValue[slot] = value;
            }
            bool bijective = slotToValue.Values.Distinct().Count() == slotToValue.Count && slotToValue.Count >= 2;
            var cls = stable && reproduced && bijective ? OffsetClass.Hit : OffsetClass.Noise;
            findings.Add(new OffsetFinding(off, cls, slotToValue));
        }
        return findings;
    }
}
