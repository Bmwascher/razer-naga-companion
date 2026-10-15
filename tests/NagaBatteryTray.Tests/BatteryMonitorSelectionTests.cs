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

    /// <summary>"Use automatic" in the settings overlay (spec 2026-10-15) clears the preference
    /// through this same pass-through. ConnectionPolicyTests.NoPreferenceNeverProbes is the other
    /// half: with null arriving at the device, the battery path stops probing for the mouse that
    /// is gone.</summary>
    [Fact]
    public void ClearingThePreferenceReachesTheDevice()
    {
        var device = new FakeRazerDevice();
        var monitor = Build(device);
        monitor.PreferredMouse = "Naga V2 Pro";

        monitor.PreferredMouse = null;

        Assert.Null(device.PreferredModel);
        Assert.Null(monitor.PreferredMouse);
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
    public async Task RefreshNowWaitingActuallyWaitsForABusyLockThenAppliesItsOwnReading()
    {
        var device = new FakeRazerDevice
        {
            ReadEntered = new TaskCompletionSource<bool>(),
            ReadGate = new TaskCompletionSource<bool>(),
        };
        device.Enqueue(new BatteryReading(40, false, true, DateTimeOffset.Now, false, "Naga V2 Pro"));
        device.Enqueue(new BatteryReading(60, false, true, DateTimeOffset.Now, false, "Naga V3 Pro"));
        var monitor = Build(device);

        // Get a read in flight and hold it open, so the read lock is genuinely busy.
        var inFlight = monitor.RefreshNowAsync();
        await device.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var waiting = monitor.RefreshNowWaitingAsync();
        var early = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(waiting, early); // still blocked behind the busy lock, not skipped

        device.ReadGate.SetResult(true); // let the in-flight read finish; waiting can now proceed
        var settled = await Task.WhenAny(Task.WhenAll(inFlight, waiting), Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(inFlight.IsCompletedSuccessfully);
        Assert.True(waiting.IsCompletedSuccessfully);

        Assert.Equal("Naga V3 Pro", monitor.State.Model); // waiting's own read landed, not the stale first one
    }

    [Fact]
    public async Task TimerStyleRefreshStillSkipsInsteadOfWaitingOnABusyLock()
    {
        var device = new FakeRazerDevice
        {
            ReadEntered = new TaskCompletionSource<bool>(),
            ReadGate = new TaskCompletionSource<bool>(),
        };
        device.Enqueue(new BatteryReading(40, false, true, DateTimeOffset.Now, false, "Naga V2 Pro"));
        device.Enqueue(new BatteryReading(60, false, true, DateTimeOffset.Now, false, "Naga V3 Pro"));
        var monitor = Build(device);

        var inFlight = monitor.RefreshNowAsync();
        await device.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var skip = monitor.RefreshNowAsync(); // timer-style call: WaitAsync(0), no waitForLock
        var raced = await Task.WhenAny(skip, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(skip, raced); // returned immediately instead of waiting on the busy lock

        device.ReadGate.SetResult(true);
        await inFlight.WaitAsync(TimeSpan.FromSeconds(5));

        // The skip never consumed a reading: the in-flight read still got the FIRST queued value.
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
