using NagaBatteryTray.Diagnostics;
using NagaBatteryTray.Hid;
using Xunit;

public class ProfileProbeAnalysisTests
{
    private static RawButtonAction Key(byte usage) => new(0x02, new byte[] { 0x00, usage });

    /// <summary>A 12-entry factory-like row; overrides replace individual positions (1-based).</summary>
    private static RawButtonAction?[] Row(params (int Pos, RawButtonAction? A)[] overrides)
    {
        var row = new RawButtonAction?[12];
        for (int p = 1; p <= 12; p++) row[p - 1] = Key((byte)(0x1e + p - 1));
        foreach (var (pos, a) in overrides) row[pos - 1] = a;
        return row;
    }

    [Fact]
    public void Fingerprint_single_differing_position_suffices()
    {
        var inv = new List<SlotActions>
        {
            new(1, Row()),
            new(2, Row((3, Key(0x68)))), // slot 2 differs only at position 3
        };
        Assert.Equal(new[] { 3 }, ProfileProbeAnalysis.SelectFingerprint(inv));
    }

    [Fact]
    public void Fingerprint_needs_two_positions_when_no_single_one_splits_all()
    {
        // 1 vs 2 differ only at pos 2; 2 vs 3 differ only at pos 7; 1 vs 3 differ at both
        var inv = new List<SlotActions>
        {
            new(1, Row((2, Key(0x68)), (7, Key(0x69)))),
            new(2, Row((7, Key(0x69)))),
            new(3, Row((2, Key(0x68)))),
        };
        var fp = ProfileProbeAnalysis.SelectFingerprint(inv);
        Assert.NotNull(fp);
        Assert.Equal(2, fp!.Length);
        Assert.Contains(2, fp);
        Assert.Contains(7, fp);
    }

    [Fact]
    public void Fingerprint_null_when_slots_identical()
    {
        var inv = new List<SlotActions> { new(1, Row()), new(2, Row()) };
        Assert.Null(ProfileProbeAnalysis.SelectFingerprint(inv));
    }

    [Fact]
    public void Fingerprint_null_when_fewer_than_two_slots()
    {
        Assert.Null(ProfileProbeAnalysis.SelectFingerprint(new List<SlotActions> { new(1, Row()) }));
    }

    [Fact]
    public void Fingerprint_ignores_positions_with_a_failed_read()
    {
        // the only differing position (3) failed to read on slot 1 -> ineligible -> no fingerprint
        var inv = new List<SlotActions>
        {
            new(1, Row((3, null))),
            new(2, Row((3, Key(0x68)))),
        };
        Assert.Null(ProfileProbeAnalysis.SelectFingerprint(inv));
    }

    [Fact]
    public void Fingerprint_equality_is_by_value_not_array_reference()
    {
        // identical bindings in DISTINCT arrays must compare equal (no false fingerprint)
        var inv = new List<SlotActions>
        {
            new(1, Row((5, new RawButtonAction(0x02, new byte[] { 0x00, 0x22 })))),
            new(2, Row((5, new RawButtonAction(0x02, new byte[] { 0x00, 0x22 })))),
        };
        Assert.Null(ProfileProbeAnalysis.SelectFingerprint(inv));
    }

    [Fact]
    public void Match_returns_the_unique_slot()
    {
        var inv = new List<SlotActions> { new(1, Row()), new(2, Row((3, Key(0x68)))) };
        var observed = Row((3, Key(0x68)));
        Assert.Equal((byte)2, ProfileProbeAnalysis.MatchFingerprint(inv, new[] { 3 }, observed));
    }

    [Fact]
    public void Match_null_when_observed_read_failed_or_ambiguous()
    {
        var inv = new List<SlotActions> { new(1, Row()), new(2, Row((3, Key(0x68)))) };
        Assert.Null(ProfileProbeAnalysis.MatchFingerprint(inv, new[] { 3 }, Row((3, null))));
        var ambiguous = new List<SlotActions> { new(1, Row()), new(2, Row()) };
        Assert.Null(ProfileProbeAnalysis.MatchFingerprint(ambiguous, new[] { 3 }, Row()));
    }
}
