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

    [Fact]
    public async Task TheWriteWaitsBehindAConcurrentBatteryReadHoldingTheLock()
    {
        var device = new FakeRazerDevice
        {
            ActiveProfile = 2,
            ReadEntered = new TaskCompletionSource<bool>(),
            ReadGate = new TaskCompletionSource<bool>(),
        };
        device.ButtonActions[(2, 0x4b)] = new RawButtonAction(RazerProtocol.FnKeyboard, F1);
        var monitor = Build(device);

        // Park a battery read inside the gate so the one read lock is genuinely held by it.
        var read = monitor.RefreshNowAsync();
        await device.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var write = monitor.WriteButtonVerifiedAsync(null, 0x4b, RazerProtocol.FnKeyboard, F1);
        var early = await Task.WhenAny(write, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(write, early); // still blocked behind the read holding the lock, not interleaved

        device.ReadGate.SetResult(true); // let the read finish; the write can now proceed
        var settled = await Task.WhenAny(Task.WhenAll(read, write), Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(read.IsCompletedSuccessfully);
        Assert.True(write.IsCompletedSuccessfully);

        var r = await write;
        Assert.True(r.Verified);
        Assert.Single(device.ButtonWrites);   // the resolve-write-verify sequence all ran, once the lock freed
        Assert.Equal(1, device.GetButtonCount);
    }
}
