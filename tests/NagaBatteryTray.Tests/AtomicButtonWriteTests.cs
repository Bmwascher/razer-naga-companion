using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using Xunit;

public sealed class AtomicButtonWriteTests
{
    private static BatteryMonitor Build(FakeRazerDevice device) =>
        new(device, new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"naga-{Guid.NewGuid():N}.json")),
            a => a());

    private static readonly byte[] F1 = { 0x00, 0x3a };

    [Fact]
    public async Task TheSlotComesFromTheMouseBeingWrittenTo()
    {
        var device = new FakeRazerDevice { ActiveProfile = 2 };
        device.ButtonActions[(2, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, F1);
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.True(r.Verified);
        Assert.Equal((byte)2, r.Slot);
        Assert.Single(device.ButtonWrites);
        Assert.Equal((byte)2, device.ButtonWrites[0].Profile);
    }

    [Fact]
    public async Task AnUnreadableActiveSlotWritesNothing()
    {
        var device = new FakeRazerDevice { ActiveProfile = null };
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.False(r.Verified);
        Assert.Empty(device.ButtonWrites);   // never guess a slot we could not read
    }

    [Fact]
    public async Task ASlotTheMouseDoesNotHaveIsNeverWritten()
    {
        var device = new FakeRazerDevice { ActiveProfile = 3 };
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(new byte[] { 1 }, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.False(r.Verified);
        Assert.Equal((byte)3, r.Slot);
        Assert.Empty(device.ButtonWrites);   // refused before any write
    }

    [Fact]
    public async Task AReadBackThatDoesNotMatchFailsTheWrite()
    {
        var device = new FakeRazerDevice { ActiveProfile = 1 };
        device.ButtonActions[(1, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, new byte[] { 0x00 });
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.False(r.Verified);
        Assert.Single(device.ButtonWrites);   // the write WAS issued; only the read-back rejected it
    }

    [Fact]
    public async Task AMouseChangeDuringTheOperationIsReported()
    {
        var device = new FakeRazerDevice { ActiveProfile = 1 };
        device.ButtonActions[(1, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, F1);
        device.BeforeGetButton = () => device.ConnectionId++;   // reconnect landed mid-operation
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.True(r.ConnectionChanged);
        Assert.False(r.Verified);   // a changed connection is never a verified write
    }

    /// <summary>The across-time identity the caller compares its undo snapshots against: the write
    /// must report the MODEL it ran under, not nothing.</summary>
    [Fact]
    public async Task TheWriteReportsTheModelItRanUnder()
    {
        var device = new FakeRazerDevice { ActiveProfile = 2, ConnectedModel = "Naga V3 Pro" };
        device.ButtonActions[(2, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, F1);
        var monitor = Build(device);

        var r = await monitor.WriteButtonVerifiedAsync(null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.True(r.Verified);
        Assert.Equal("Naga V3 Pro", r.Model);
    }

    /// <summary>And it reports the mouse actually there, not a constant: hop the device onto the
    /// other model between two writes and the reported identity follows it — which is what lets the
    /// caller tell a snapshot taken on one mouse from a write that landed on another.</summary>
    [Fact]
    public async Task TheReportedModelFollowsTheDevice()
    {
        var device = new FakeRazerDevice { ActiveProfile = 2, ConnectedModel = "Naga V2 Pro" };
        device.ButtonActions[(2, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, F1);
        var monitor = Build(device);

        var first = await monitor.WriteButtonVerifiedAsync(null, 0x4b, RazerProtocol.FnKeyboard, F1);
        device.ConnectedModel = "Naga V3 Pro";   // the other mouse is what answers now
        var second = await monitor.WriteButtonVerifiedAsync(null, 0x4b, RazerProtocol.FnKeyboard, F1);

        Assert.Equal("Naga V2 Pro", first.Model);
        Assert.Equal("Naga V3 Pro", second.Model);
    }

    /// <summary>The point of the single lock acquisition: nothing can run between the write's own
    /// steps. Two battery polls are queued on the read lock — one before the write's first step
    /// (covering the resolve-to-write boundary) and one from inside the write itself (covering the
    /// write-to-verify boundary) — and the fake is loaded with a plainly different reading, so a
    /// poll that got in would leave a bigger read count and a different State behind it.
    /// <para>Split the write back into three Wait/Release pairs and this goes red: a queued waiter
    /// takes the freed lock at the very next release, which is exactly what one acquisition
    /// prevents.</para></summary>
    [Fact]
    public async Task NoBatteryPollCanRunBetweenTheWritesOwnSteps()
    {
        var device = new FakeRazerDevice
        {
            ActiveProfile = 2,
            ReadEntered = new TaskCompletionSource<bool>(),
            ReadGate = new TaskCompletionSource<bool>(),
        };
        device.ButtonActions[(2, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, F1);
        device.Enqueue(new BatteryReading(50, false, true, DateTimeOffset.Now, false, "Naga V2 Pro"));
        device.Enqueue(new BatteryReading(11, false, true, DateTimeOffset.Now, false, "Naga V3 Pro"));
        var baseline = DeviceState.Online(50, false, false, "Naga V2 Pro");
        var monitor = Build(device);

        // What the world looked like at each interior boundary of the write.
        int readsAtWrite = -1, readsAtVerify = -1;
        DeviceState stateAtWrite = default, stateAtVerify = default;
        Task? fromInsideTheWrite = null;

        device.BeforeSetButton = () =>
        {
            readsAtWrite = device.ReadCount;
            stateAtWrite = monitor.State;
            // queue a poll on the lock: under three acquisitions it would take the lock the moment
            // the write step releases, i.e. before the verify read.
            fromInsideTheWrite = monitor.RefreshNowWaitingAsync();
        };
        device.BeforeGetButton = () =>
        {
            readsAtVerify = device.ReadCount;
            stateAtVerify = monitor.State;
        };

        // Park a battery read inside the gate so the one read lock is genuinely held by it.
        var parked = monitor.RefreshNowAsync();
        await device.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var write = monitor.WriteButtonVerifiedAsync(null, 0x4b, RazerProtocol.FnKeyboard, F1);
        // queued after the write, so it is first in line the moment the write frees the lock —
        // under three acquisitions that is the gap between resolving the slot and writing it.
        var beforeTheWrite = monitor.RefreshNowWaitingAsync();

        var early = await Task.WhenAny(write, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(write, early); // still blocked behind the read holding the lock, not interleaved

        device.ReadGate.SetResult(true); // let the parked read finish; the write can now proceed
        await write.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.WhenAll(parked, beforeTheWrite).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(fromInsideTheWrite);
        await fromInsideTheWrite.WaitAsync(TimeSpan.FromSeconds(30));

        var r = await write;
        Assert.True(r.Verified);
        Assert.Single(device.ButtonWrites);   // the resolve-write-verify sequence all ran, once the lock freed
        Assert.Equal(1, device.GetButtonCount);

        // Neither queued poll got in: one read served (the parked one) at both interior boundaries,
        // and the second reading was still unserved, so State never moved off the first mouse.
        Assert.Equal(1, readsAtWrite);
        Assert.Equal(1, readsAtVerify);
        Assert.Equal(baseline, stateAtWrite);
        Assert.Equal(baseline, stateAtVerify);
    }
}
