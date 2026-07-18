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

    /// <summary>91-byte reply with given (reportOffset, value) pairs set; buffer[i] = report[i-1].</summary>
    private static byte[] ReplyAt(params (int ReportOffset, byte Value)[] set)
    {
        var buf = new byte[91];
        buf[1] = 0x02; // status success at report[0]
        foreach (var (off, v) in set) buf[off + 1] = v;
        return buf;
    }

    private static StateSamples Visit(byte slot, byte value, byte value2nd = 0xff) =>
        new(slot, new List<byte[]>
        {
            ReplyAt((20, value)),
            ReplyAt((20, value2nd == 0xff ? value : value2nd)),
        });

    /// <summary>A state visit with two independent report offsets set (report offsets 20 and 21),
    /// both samples identical (stable within the visit) — for adjacent-pair-span tests.</summary>
    private static StateSamples VisitPair(byte slot, byte v20, byte v21) =>
        new(slot, new List<byte[]> { ReplyAt((20, v20), (21, v21)), ReplyAt((20, v20), (21, v21)) });

    [Fact]
    public void Analyze_finds_a_zero_based_hit_at_the_varying_offset()
    {
        var visits = new List<StateSamples> { Visit(1, 0x00), Visit(2, 0x01), Visit(3, 0x02), Visit(1, 0x00) };
        var f = Assert.Single(ProfileProbeAnalysis.AnalyzeCandidate(visits));
        Assert.Equal(20, f.ReportOffset);
        Assert.Equal(1, f.Length);
        Assert.Equal(OffsetClass.Hit, f.Class);
        Assert.Equal(0x01, f.SlotToValue[2]); // encoding is 0-based — accepted as-is
    }

    [Fact]
    public void Analyze_accepts_bitmask_encodings()
    {
        var visits = new List<StateSamples> { Visit(1, 0x02), Visit(2, 0x04), Visit(3, 0x08), Visit(1, 0x02) };
        Assert.Equal(OffsetClass.Hit, Assert.Single(ProfileProbeAnalysis.AnalyzeCandidate(visits)).Class);
    }

    [Fact]
    public void Analyze_marks_revisit_mismatch_as_noise()
    {
        var visits = new List<StateSamples> { Visit(1, 0x10), Visit(2, 0x20), Visit(1, 0x30) };
        Assert.Equal(OffsetClass.Noise, Assert.Single(ProfileProbeAnalysis.AnalyzeCandidate(visits)).Class);
    }

    [Fact]
    public void Analyze_marks_non_bijective_mapping_as_noise()
    {
        var visits = new List<StateSamples> { Visit(1, 0x07), Visit(2, 0x07), Visit(3, 0x08), Visit(1, 0x07) };
        Assert.Equal(OffsetClass.Noise, Assert.Single(ProfileProbeAnalysis.AnalyzeCandidate(visits)).Class);
    }

    [Fact]
    public void Analyze_marks_instability_within_a_visit_as_noise()
    {
        var visits = new List<StateSamples> { Visit(1, 0x10, 0x11), Visit(2, 0x20), Visit(1, 0x10) };
        Assert.Equal(OffsetClass.Noise, Assert.Single(ProfileProbeAnalysis.AnalyzeCandidate(visits)).Class);
    }

    [Fact]
    public void Analyze_does_not_report_constant_offsets()
    {
        var visits = new List<StateSamples> { Visit(1, 0x42), Visit(2, 0x42), Visit(1, 0x42) };
        Assert.Empty(ProfileProbeAnalysis.AnalyzeCandidate(visits));
    }

    [Fact]
    public void Analyze_finds_an_adjacent_pair_hit_when_neither_byte_alone_is_bijective()
    {
        // slot1 (0x00,0x00), slot2 (0x00,0x01), slot3 (0x01,0x00), revisit slot1 (0x00,0x00):
        // report[20] alone is {1:0x00, 2:0x00, 3:0x01} (slots 1&2 collide) and report[21] alone is
        // {1:0x00, 2:0x01, 3:0x00} (slots 1&3 collide) — neither byte is bijective on its own, but
        // the (20,21) tuple is: {1:0x0000, 2:0x0001, 3:0x0100}.
        var visits = new List<StateSamples>
        {
            VisitPair(1, 0x00, 0x00), VisitPair(2, 0x00, 0x01), VisitPair(3, 0x01, 0x00), VisitPair(1, 0x00, 0x00),
        };
        var findings = ProfileProbeAnalysis.AnalyzeCandidate(visits);

        Assert.DoesNotContain(findings, f => f.Length == 1 && f.Class == OffsetClass.Hit);
        var hit = Assert.Single(findings, f => f.Class == OffsetClass.Hit);
        Assert.Equal(20, hit.ReportOffset);
        Assert.Equal(2, hit.Length);
        Assert.Equal(3, hit.SlotToValue.Count);
        Assert.Equal(0x0000, hit.SlotToValue[1]);
        Assert.Equal(0x0001, hit.SlotToValue[2]);
        Assert.Equal(0x0100, hit.SlotToValue[3]);

        // the two per-byte findings still surface, just as Noise (filtered by Class).
        Assert.Equal(2, findings.Count(f => f.Length == 1 && f.Class == OffsetClass.Noise));
    }

    [Fact]
    public void Analyze_pair_span_is_not_a_hit_when_the_tuple_is_not_bijective()
    {
        // same shape as the positive test, but slot3 (0x00,0x01) duplicates slot2's tuple -> no
        // bijective pair, so no hit of any length (only per-byte noise).
        var visits = new List<StateSamples>
        {
            VisitPair(1, 0x00, 0x00), VisitPair(2, 0x00, 0x01), VisitPair(3, 0x00, 0x01), VisitPair(1, 0x00, 0x00),
        };
        var findings = ProfileProbeAnalysis.AnalyzeCandidate(visits);

        Assert.DoesNotContain(findings, f => f.Class == OffsetClass.Hit);
    }
}
