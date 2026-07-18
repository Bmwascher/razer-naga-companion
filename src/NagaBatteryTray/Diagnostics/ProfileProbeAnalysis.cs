using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Diagnostics;

/// <summary>Slot inventory row for --probe-profile analysis: the 12 grid actions read from one
/// onboard slot. Index = grid position - 1; null = that button's read failed.</summary>
internal readonly record struct SlotActions(byte Slot, RawButtonAction?[] Actions);

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
}
