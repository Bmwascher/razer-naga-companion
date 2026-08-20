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
}
