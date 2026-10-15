using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;
using Xunit;

public sealed class RazerDeviceTransportTests
{
    private static JsonSettingsStore FreshStore() =>   // no cached transaction id: probing must run
        new(Path.Combine(Path.GetTempPath(), $"naga-{Guid.NewGuid():N}.json"));

    [Fact]
    public async Task ADeadCollectionIsNotProbedFurther()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(RazerProtocol.MousePidV3Wireless);
        source.FeatureFailing.Add(RazerProtocol.MousePidV3Wireless);
        using var device = new RazerDevice(FreshStore(), source);

        var reading = await device.ReadAsync(CancellationToken.None);

        Assert.False(reading.IsPresent);
        var dead = source.Handed.Single();
        Assert.Equal(1, dead.FeatureCalls);   // one failed call, then no more Win32 on a dead handle
        Assert.True(dead.Disposed);
    }

    [Fact]
    public async Task AWorkingCollectionIsReportedWithItsModelAndLink()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(RazerProtocol.MousePidV3Wired);
        source.Answering.Add(RazerProtocol.MousePidV3Wired);
        using var device = new RazerDevice(FreshStore(), source);

        var reading = await device.ReadAsync(CancellationToken.None);

        Assert.True(reading.IsPresent);
        Assert.Equal("Naga V3 Pro", reading.Model);
        Assert.True(reading.IsWired);
        Assert.Equal(1, device.ConnectionId);
    }

    [Fact]
    public async Task ThePreferredMouseIsAdoptedThroughTheRealDevice()
    {
        var source = new FakeControlCollectionSource();
        foreach (int pid in new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidV3Wireless })
        {
            source.Present.Add(pid);
            source.Answering.Add(pid);
        }
        using var device = new RazerDevice(FreshStore(), source) { PreferredModel = "Naga V2 Pro" };

        var reading = await device.ReadAsync(CancellationToken.None);

        Assert.Equal("Naga V2 Pro", reading.Model);
    }
}
