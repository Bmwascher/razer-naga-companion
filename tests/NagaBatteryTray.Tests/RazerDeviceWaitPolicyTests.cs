using System.Diagnostics;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;
using Xunit;

/// <summary>The wait policy as ordinary tested code. RazerDevice sits ABOVE the IControlCollection
/// seam, so hard rule 6 puts it inside the test suite - the transport exemption covers only the two
/// Win32 wrappers. These exist because the first version of the fast-read change shipped without
/// them and a real defect escaped: patience on a BUSY mouse silently fell from ~1000 ms to 400 ms,
/// which the gated benches could not catch because the emulator had no 0x01 path at all
/// (Fable diff r1, F1/F2).
/// <para>They assert on BEHAVIOUR (did the read succeed, how many round trips) and on generous
/// wall-clock bounds, never on exact timings: Task.Delay rounds up to the ~15.6 ms timer tick, so a
/// tight assertion here would be a flaky test rather than a stronger one.</para></summary>
public sealed class RazerDeviceWaitPolicyTests
{
    private static ISettingsStore Store(int setReadDelayMs = 400)
    {
        var s = new FakeSettingsStore();
        s.SetCachedTransactionId(0x1f);        // steady state: the id is resolved once, then cached
        s.Settings.SetReadDelayMs = setReadDelayMs;
        return s;
    }

    private static BenchControlCollectionSource Source(int readyAfterMs, int busyUntilMs = 0)
    {
        var src = new BenchControlCollectionSource { ReadyAfterMs = readyAfterMs, BusyUntilMs = busyUntilMs };
        src.Present.Add(RazerProtocol.MousePidV3Wireless);
        src.Answering.Add(RazerProtocol.MousePidV3Wireless);
        return src;
    }

    [Fact]
    public async Task A_prompt_mouse_is_read_on_the_first_rung()
    {
        var src = Source(readyAfterMs: 20);
        using var device = new RazerDevice(Store(), src);

        var sw = Stopwatch.StartNew();
        var reading = await device.ReadAsync(CancellationToken.None);
        sw.Stop();

        Assert.True(reading.IsPresent);
        // verify + battery + charging, one SET and one GET each: nothing is retried
        Assert.Equal(3, src.Exchanges);
        Assert.Equal(6, src.FeatureCalls);
        // three reads that each used to pay a flat 400 ms must now come in far under that
        Assert.True(sw.ElapsedMilliseconds < 400, $"took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>The F1 regression. A mouse answering 0x01 for 600 ms is inside the patience the flat
    /// path gave (SetReadDelayMs, then 200 ms, then SetReadDelayMs again = 1000 ms) and outside a
    /// budget of SetReadDelayMs alone. It must still read.</summary>
    [Fact]
    public async Task A_busy_mouse_is_still_read_within_the_patience_the_flat_path_gave()
    {
        var src = Source(readyAfterMs: 20, busyUntilMs: 600);
        using var device = new RazerDevice(Store(), src);

        var reading = await device.ReadAsync(CancellationToken.None);

        Assert.True(reading.IsPresent);                 // 400 ms of patience would have returned Absent
        Assert.Equal(50, reading.Percent);              // and the value still parses
    }

    [Fact]
    public async Task A_busy_mouse_is_still_read_on_the_dpi_path()
    {
        var src = Source(readyAfterMs: 20, busyUntilMs: 600);
        using var device = new RazerDevice(Store(), src);

        var dpi = await device.GetDpiAsync(CancellationToken.None);

        Assert.NotNull(dpi);
        Assert.Equal(1600, dpi!.Value.X);
    }

    [Fact]
    public async Task A_mouse_that_never_answers_gives_up_and_reports_absent()
    {
        // busy for far longer than any budget: the read must end, not hang
        var src = Source(readyAfterMs: 20, busyUntilMs: 60_000);
        using var device = new RazerDevice(Store(), src);

        var sw = Stopwatch.StartNew();
        var reading = await device.ReadAsync(CancellationToken.None);
        sw.Stop();

        Assert.False(reading.IsPresent);
        // one converted read's full schedule is ~1000 ms; the verify failing ends ReadAsync there,
        // so a generous ceiling still proves the loop terminates rather than spinning
        Assert.True(sw.ElapsedMilliseconds < 4000, $"took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>The converse of the busy case, and the reason patience takes TWO numbers. A
    /// collection that answers nothing at all leaves its status byte at 0x00, which ExchangeAsync
    /// took as the final answer after one SetReadDelayMs wait - so handing every converted read the
    /// full busy-case patience made a dead receiver on the dashboard's discovery path cost 1000 ms
    /// where it used to cost 400. The bench row discover_mice_silent_peer measured exactly that
    /// (521 -> 1108 ms) before the budget was split.</summary>
    [Fact]
    public async Task A_collection_that_answers_nothing_gives_up_within_the_flat_wait()
    {
        var src = new BenchControlCollectionSource { ReadyAfterMs = 20 };
        src.Present.Add(RazerProtocol.MousePidV3Wireless);   // present, never answers
        using var device = new RazerDevice(Store(), src);

        var sw = Stopwatch.StartNew();
        var reading = await device.ReadAsync(CancellationToken.None);
        sw.Stop();

        Assert.False(reading.IsPresent);
        // SetReadDelayMs of rungs plus timer-tick rounding; the busy-case budget would spend ~1000
        Assert.True(sw.ElapsedMilliseconds < 800, $"took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>Nothing validates SetReadDelayMs between settings.json and the transport, so a
    /// hand-edited 0 - or a nonsense negative - reaches the wait policy as written. ExchangeAsync
    /// did Task.Delay(it) and then a GET regardless, so a budget it drives to 0 or below must still
    /// poll once rather than skip the read entirely; that is what the loop's i > 0 guard is for
    /// (Fable diff r1, F5).</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public async Task A_nonsense_wait_setting_still_performs_one_read(int setReadDelayMs)
    {
        var src = Source(readyAfterMs: 0);
        using var device = new RazerDevice(Store(setReadDelayMs), src);

        var reading = await device.ReadAsync(CancellationToken.None);

        Assert.True(reading.IsPresent);
        Assert.Equal(6, src.FeatureCalls);   // three SETs, three GETs - one poll each, none skipped
    }

    /// <summary>The grid sweep's reads pass no budget, so they keep the plain schedule. Twelve of
    /// them is the dashboard's button grid, and each must be a single round trip on a prompt mouse.</summary>
    [Fact]
    public async Task Button_reads_are_one_round_trip_each_on_a_prompt_mouse()
    {
        var src = Source(readyAfterMs: 20);
        using var device = new RazerDevice(Store(), src);
        await device.ReadAsync(CancellationToken.None);   // connect first
        src.ResetCounters();

        for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
            Assert.NotNull(await device.GetButtonAsync(1, NagaV2ProButtons.IdForPosition(pos), CancellationToken.None));

        Assert.Equal(NagaV2ProButtons.Count, src.Exchanges);
        Assert.Equal(NagaV2ProButtons.Count * 2, src.FeatureCalls);
    }

    [Fact]
    public void A_premature_reply_is_never_mistaken_for_data()
    {
        var request = RazerProtocol.BuildFeatureBuffer(0x1f, RazerProtocol.CommandIdBattery);

        var echo = (byte[])request.Clone();          // our own request still sitting in the buffer
        Assert.False(RazerProtocol.IsReplyReady(request, echo));

        var busy = (byte[])request.Clone();
        busy[1] = 0x01;
        Assert.False(RazerProtocol.IsReplyReady(request, busy));

        // the previous command's FINISHED reply: completed status, but the wrong cmd. This is the
        // case that stops a stale battery reply being read as the charging flag.
        var stale = (byte[])request.Clone();
        stale[1] = 0x02;
        stale[8] = RazerProtocol.CommandIdCharging;
        Assert.False(RazerProtocol.IsReplyReady(request, stale));

        var ready = (byte[])request.Clone();
        ready[1] = 0x02;
        Assert.True(RazerProtocol.IsReplyReady(request, ready));
    }

    [Fact]
    public void The_arg_echo_catches_the_previous_buttons_reply()
    {
        var request = RazerProtocol.BuildGetButtonBuffer(0x1f, profile: 1, buttonId: 0x41, hypershift: 0x00);

        // same class and cmd, completed - but it answers the button BEFORE this one
        var previous = (byte[])request.Clone();
        previous[1] = 0x02;
        previous[10] = 0x40;
        Assert.False(RazerProtocol.IsReplyReady(request, previous, echoArgBytes: 3));
        Assert.True(RazerProtocol.IsReplyReady(request, previous));   // and 0 args cannot see it

        var ours = (byte[])request.Clone();
        ours[1] = 0x02;
        Assert.True(RazerProtocol.IsReplyReady(request, ours, echoArgBytes: 3));
    }
}
