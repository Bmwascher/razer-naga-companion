using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using Xunit;

public sealed class BatteryMonitorSelectionTests
{
    private static BatteryMonitor Build(FakeRazerDevice device) =>
        new(device, new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"naga-{Guid.NewGuid():N}.json")),
            a => a());

    [Fact]
    public void PreferredMouseReachesTheDevice()
    {
        var device = new FakeRazerDevice();
        var monitor = Build(device);

        monitor.PreferredMouse = "Naga V3 Pro";

        Assert.Equal("Naga V3 Pro", device.PreferredModel);
        Assert.Equal("Naga V3 Pro", monitor.PreferredMouse);
    }

    [Fact]
    public async Task DiscoverMiceReturnsWhatTheDeviceFound()
    {
        var device = new FakeRazerDevice();
        device.Reachable.Add("Naga V3 Pro");
        var monitor = Build(device);

        var found = await monitor.DiscoverMiceAsync();

        Assert.Equal(new[] { "Naga V3 Pro" }, found);
        Assert.Equal(1, device.DiscoverCount);
    }

    [Fact]
    public async Task DiscoverMiceRepublishesTheConnectedModel()
    {
        var device = new FakeRazerDevice();
        device.Reachable.Add("Naga V3 Pro");
        device.Enqueue(new BatteryReading(70, false, true, DateTimeOffset.Now, false, "Naga V3 Pro"));
        var monitor = Build(device);

        await monitor.DiscoverMiceAsync();

        Assert.Equal("Naga V3 Pro", monitor.State.Model);
    }

    [Fact]
    public async Task AnExplicitRefreshWaitsInsteadOfSkipping()
    {
        var device = new FakeRazerDevice();
        device.Enqueue(new BatteryReading(55, false, true, DateTimeOffset.Now, false, "Naga V2 Pro"));
        var monitor = Build(device);

        await monitor.RefreshNowWaitingAsync();

        Assert.Equal("Naga V2 Pro", monitor.State.Model);
    }

    [Fact]
    public async Task DiscoverMiceReturnsEmptyAfterDispose()
    {
        var device = new FakeRazerDevice();
        device.Reachable.Add("Naga V3 Pro");
        var monitor = Build(device);
        monitor.Dispose();

        Assert.Empty(await monitor.DiscoverMiceAsync());
    }
}
