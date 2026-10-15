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
/// <para>They assert on BEHAVIOUR first - did the read succeed, and how many round trips did it
/// take. ROUND-TRIP COUNTS are the primary evidence, because the rung schedule decides how many
/// GETs a read issues by arithmetic on nominal waits: a count reads the same on an idle machine and
/// on a loaded CI runner. Wall clock is asserted only where nothing else can see the behaviour, and
/// then with headroom measured rather than guessed. Task.Delay rounds up to the ~15.6 ms timer tick
/// and the thread has to be scheduled afterwards, so under load these reads overshoot badly: at
/// 100% CPU (16 spinners on 16 cores, 2026-10-15) a 90 ms read measured 737 ms and a 400 ms read
/// measured 1086 ms. A bound that merely sits between two nominal outcomes is therefore a flaky
/// test rather than a stronger one - each one below says which two outcomes its number separates
/// and why the gap survives that overshoot.</para></summary>
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

    /// <summary>The per-read wait is the 30 ms first rung and NOT the configured flat wait. Counts
    /// cannot show that - the flat path also took one SET and one GET per read - so wall clock is the
    /// only evidence, and the fixture is what makes it robust: SetReadDelayMs is set far above the
    /// whole schedule, which leaves the two outcomes 90 ms and 12 s apart instead of 90 ms and
    /// 1200 ms. A budget that large never binds for a prompt mouse, so it does not distort the
    /// ladder; it only moves the failing outcome out of reach of scheduler overshoot.</summary>
    [Fact]
    public async Task A_prompt_mouse_is_read_on_the_first_rung()
    {
        var src = Source(readyAfterMs: 20);
        using var device = new RazerDevice(Store(setReadDelayMs: 4000), src);

        var sw = Stopwatch.StartNew();
        var reading = await device.ReadAsync(CancellationToken.None);
        sw.Stop();

        Assert.True(reading.IsPresent);
        // verify + battery + charging, one SET and one GET each: nothing is retried
        Assert.Equal(3, src.Exchanges);
        Assert.Equal(6, src.FeatureCalls);
        // 2000 separates three rung-1 reads (90 ms nominal, 737 ms measured at 100% CPU) from three
        // reads that pay the configured wait instead (12 s): 2.7x the worst measurement, 6x under
        // the outcome it rules out.
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
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
        // The give-up in round trips: one SET and the whole six-rung converted schedule, then stop.
        // That is arithmetic on nominal waits, so load cannot move it, and a seventh feature call is
        // what a retry the policy does not have would look like. The verify failing ends ReadAsync
        // there, so this is the only read in the call.
        Assert.Equal(1, src.Exchanges);
        Assert.Equal(7, src.FeatureCalls);
        // The ceiling only has to separate "ended" from "spinning", which is unbounded: the schedule
        // is ~1000 ms nominal and measured 2001 ms at 100% CPU, so 10 s proves termination with 5x
        // headroom instead of measuring the schedule.
        Assert.True(sw.ElapsedMilliseconds < 10_000, $"took {sw.ElapsedMilliseconds} ms");
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

        var reading = await device.ReadAsync(CancellationToken.None);

        Assert.False(reading.IsPresent);
        // The budget split, counted rather than timed: BaseMs of 400 clamps the ladder to five GETs
        // (30+20+100+200+50), where the busy-case budget would run all six. Wall clock cannot tell
        // those apart on a loaded machine - the outcomes are 400 and 1000 ms nominal, and these five
        // awaits measured 1086 ms at 100% CPU, i.e. past the outcome the assertion would have to
        // rule out. The count is 6 against 7 whatever the scheduler does.
        Assert.Equal(1, src.Exchanges);
        Assert.Equal(6, src.FeatureCalls);
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

    /// <summary>The sixth rung was added so a CONVERTED read can reach 1000 ms. The button and
    /// profile reads pass no patience, so without a bound they would run it too - 250 ms added to a
    /// give-up their own old four-rung ladder set at 750, twelve times over for a sweep whose mouse
    /// sleeps part way through (Fable diff r2, F12).</summary>
    [Fact]
    public async Task An_unbudgeted_read_keeps_the_five_rungs_its_own_ladder_gave_up_at()
    {
        var src = Source(readyAfterMs: 20);
        src.SilentForClasses.Add(RazerProtocol.CommandClassButton);
        using var device = new RazerDevice(Store(), src);
        await device.ReadAsync(CancellationToken.None);   // connect on the power class, which answers
        src.ResetCounters();

        var raw = await device.GetButtonAsync(1, NagaV2ProButtons.IdForPosition(1), CancellationToken.None);

        Assert.Null(raw);
        // One SET and five GETs: spending the sixth rung is exactly a seventh feature call, so the
        // count proves the whole thing and nothing is left for wall clock to add. It used to assert
        // < 900 ms against a 750 ms nominal and flaked once under a concurrent headless-Edge run
        // (2026-10-15); these same five awaits measured 937 ms at 100% CPU.
        Assert.Equal(1, src.Exchanges);
        Assert.Equal(6, src.FeatureCalls);
    }

    /// <summary>The busy tail. The budget is raised on the iteration AFTER a 0x01 is seen, so a 0x01
    /// first seen at the last rung has no iteration left to spend the second half of the patience
    /// in - which is every read whose BaseMs alone reaches the end of the schedule, i.e. a
    /// SetReadDelayMs above 750. Nothing clamps that setting, so the guarantee has to hold there
    /// too (Fable diff r2, F9).</summary>
    [Fact]
    public async Task A_busy_reply_at_the_last_rung_still_gets_the_second_half_of_the_patience()
    {
        var src = Source(readyAfterMs: 20);
        var store = Store();
        using var device = new RazerDevice(store, src);
        await device.ReadAsync(CancellationToken.None);   // connect while the mouse is prompt

        store.Settings.SetReadDelayMs = 1000;   // hand-edited above the 750 the rungs reach
        src.ReadyAfterMs = 900;                 // 0x00 through every rung but the last
        src.BusyUntilMs = 1200;                 // and that last GET is the FIRST to see busy

        // without the tail the loop ends at 1000 ms with null; ExchangeAsync would have reached 2200
        Assert.NotNull(await device.GetDpiAsync(CancellationToken.None));
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
