using NagaBatteryTray.Diagnostics;
using NagaBatteryTray.Hid;
using Xunit;

/// <summary>Unit tests for the pure evidence gates in ProfileProbeCommand: the analyzable-sample
/// predicate and the before/after integrity classifier. Both are byte-level logic worth locking
/// down independent of the interactive --probe-profile flow (which stays hardware-exercised).</summary>
public class ProfileProbeCommandTests
{
    // ---- SampleAnalyzable ----

    /// <summary>A well-formed 91-byte reply: status success at buffer[1], correct XOR CRC (over
    /// buffer[3..88]) at buffer[89].</summary>
    private static byte[] ValidReply(byte status = 0x02)
    {
        var buf = new byte[91];
        buf[1] = status;
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        buf[89] = crc;
        return buf;
    }

    [Fact]
    public void SampleAnalyzable_true_for_valid_success_reply_with_correct_crc()
    {
        Assert.True(ProfileProbeCommand.SampleAnalyzable(ValidReply()));
    }

    [Fact]
    public void SampleAnalyzable_false_for_wrong_length()
    {
        Assert.False(ProfileProbeCommand.SampleAnalyzable(new byte[90]));
    }

    [Fact]
    public void SampleAnalyzable_false_for_non_success_status()
    {
        Assert.False(ProfileProbeCommand.SampleAnalyzable(ValidReply(status: 0x03)));
    }

    [Fact]
    public void SampleAnalyzable_false_for_corrupted_crc_byte()
    {
        var r = ValidReply();
        r[89] ^= 0xff; // corrupt the CRC byte only
        Assert.False(ProfileProbeCommand.SampleAnalyzable(r));
    }

    // ---- CompareInventories ----

    private static RawButtonAction Key(byte usage) => new(0x02, new byte[] { 0x00, usage });

    /// <summary>A 12-entry factory-like row (same shape as ProfileProbeAnalysisTests.Row);
    /// overrides replace individual positions (1-based).</summary>
    private static RawButtonAction?[] Row(params (int Pos, RawButtonAction? A)[] overrides)
    {
        var row = new RawButtonAction?[NagaV2ProButtons.Count];
        for (int p = 1; p <= NagaV2ProButtons.Count; p++) row[p - 1] = Key((byte)(0x1e + p - 1));
        foreach (var (pos, a) in overrides) row[pos - 1] = a;
        return row;
    }

    /// <summary>Effective (profile-0 read-through) is excluded from CompareInventories by design —
    /// always filled with a dummy row so it can never influence a test's assertions.</summary>
    private static ProfileProbeCommand.InventorySnapshot Snap(
        bool listOk, byte capacity, byte[] slots, bool modeOk, byte mode, IReadOnlyList<SlotActions> rows) =>
        new(listOk, capacity, slots, modeOk, mode, rows, Row());

    private static readonly byte[] OneSlot = { 1 };
    private static readonly List<SlotActions> OneRowEqual = new() { new(1, Row()) };

    [Fact]
    public void Both_readable_and_equal_yields_no_findings()
    {
        var before = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00, rows: OneRowEqual);
        var after = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00, rows: OneRowEqual);

        var (changed, inconclusive) = ProfileProbeCommand.CompareInventories(before, after);

        Assert.Empty(changed);
        Assert.Empty(inconclusive);
    }

    [Fact]
    public void List_both_readable_and_value_differs_is_Changed()
    {
        var before = Snap(listOk: true, capacity: 5, slots: new byte[] { 1, 2 }, modeOk: true, mode: 0x00, rows: OneRowEqual);
        var after = Snap(listOk: true, capacity: 5, slots: new byte[] { 1, 2, 3 }, modeOk: true, mode: 0x00, rows: OneRowEqual);

        var (changed, inconclusive) = ProfileProbeCommand.CompareInventories(before, after);

        Assert.Contains("profile list changed", changed);
        Assert.DoesNotContain(inconclusive, s => s.StartsWith("profile list"));
    }

    [Fact]
    public void List_readable_to_unreadable_is_Inconclusive_only()
    {
        var before = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00, rows: OneRowEqual);
        var after = Snap(listOk: false, capacity: 0, slots: Array.Empty<byte>(), modeOk: true, mode: 0x00, rows: new List<SlotActions>());

        var (changed, inconclusive) = ProfileProbeCommand.CompareInventories(before, after);

        Assert.Contains("profile list unreadable after", inconclusive);
        Assert.DoesNotContain(changed, s => s.StartsWith("profile list"));
    }

    [Fact]
    public void List_unreadable_to_readable_is_Inconclusive_only()
    {
        var before = Snap(listOk: false, capacity: 0, slots: Array.Empty<byte>(), modeOk: true, mode: 0x00, rows: new List<SlotActions>());
        var after = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00, rows: OneRowEqual);

        var (changed, inconclusive) = ProfileProbeCommand.CompareInventories(before, after);

        Assert.Contains("profile list readable only after", inconclusive);
        Assert.DoesNotContain(changed, s => s.StartsWith("profile list"));
    }

    [Fact]
    public void Mode_both_readable_and_value_differs_is_Changed()
    {
        var before = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00, rows: OneRowEqual);
        var after = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x03, rows: OneRowEqual);

        var (changed, inconclusive) = ProfileProbeCommand.CompareInventories(before, after);

        Assert.Contains("device mode changed", changed);
        Assert.DoesNotContain(inconclusive, s => s.StartsWith("device mode"));
    }

    [Fact]
    public void Mode_readable_to_unreadable_is_Inconclusive()
    {
        var before = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00, rows: OneRowEqual);
        var after = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: false, mode: 0xff, rows: OneRowEqual);

        var (changed, inconclusive) = ProfileProbeCommand.CompareInventories(before, after);

        Assert.Contains("device mode unreadable after", inconclusive);
        Assert.DoesNotContain(changed, s => s.StartsWith("device mode"));
    }

    [Fact]
    public void Mode_unreadable_to_readable_is_Inconclusive()
    {
        var before = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: false, mode: 0xff, rows: OneRowEqual);
        var after = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00, rows: OneRowEqual);

        var (changed, inconclusive) = ProfileProbeCommand.CompareInventories(before, after);

        Assert.Contains("device mode readable only after", inconclusive);
        Assert.DoesNotContain(changed, s => s.StartsWith("device mode"));
    }

    [Fact]
    public void Button_both_readable_and_differs_is_Changed()
    {
        var before = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00,
            rows: new List<SlotActions> { new(1, Row()) });
        var after = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00,
            rows: new List<SlotActions> { new(1, Row((3, Key(0x68)))) });

        var (changed, inconclusive) = ProfileProbeCommand.CompareInventories(before, after);

        Assert.Contains("slot 1 pos 3 changed", changed);
        Assert.DoesNotContain(inconclusive, s => s.Contains("pos 3"));
    }

    [Fact]
    public void Button_readable_to_null_is_Inconclusive()
    {
        var before = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00,
            rows: new List<SlotActions> { new(1, Row()) });
        var after = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00,
            rows: new List<SlotActions> { new(1, Row((3, null))) });

        var (changed, inconclusive) = ProfileProbeCommand.CompareInventories(before, after);

        Assert.Contains("slot 1 pos 3 unreadable after", inconclusive);
        Assert.DoesNotContain(changed, s => s.Contains("pos 3"));
    }

    [Fact]
    public void Button_null_to_readable_is_Inconclusive()
    {
        var before = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00,
            rows: new List<SlotActions> { new(1, Row((3, null))) });
        var after = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00,
            rows: new List<SlotActions> { new(1, Row()) });

        var (changed, inconclusive) = ProfileProbeCommand.CompareInventories(before, after);

        Assert.Contains("slot 1 pos 3 readable only after", inconclusive);
        Assert.DoesNotContain(changed, s => s.Contains("pos 3"));
    }

    [Fact]
    public void After_list_unreadable_skips_per_slot_comparisons_entirely()
    {
        // Buttons differ at every position between before and after, but the after-list itself is
        // unreadable — there is no basis to say a slot went missing/changed, so no per-slot strings
        // (of any flavor) may appear at all.
        var before = Snap(listOk: true, capacity: 5, slots: OneSlot, modeOk: true, mode: 0x00,
            rows: new List<SlotActions> { new(1, Row()) });
        var after = Snap(listOk: false, capacity: 0, slots: Array.Empty<byte>(), modeOk: true, mode: 0x00,
            rows: new List<SlotActions> { new(1, Row((3, Key(0x68)))) });

        var (changed, inconclusive) = ProfileProbeCommand.CompareInventories(before, after);

        Assert.DoesNotContain(changed, s => s.Contains("slot"));
        Assert.DoesNotContain(inconclusive, s => s.Contains("slot"));
    }
}
