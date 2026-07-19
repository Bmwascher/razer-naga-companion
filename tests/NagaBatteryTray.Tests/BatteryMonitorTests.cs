using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using Xunit;

public class BatteryMonitorTests
{
    private static BatteryMonitor NewMonitor(out List<int> lowFires, ISettingsStore? store = null)
    {
        store ??= new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"naga-{Guid.NewGuid():N}.json"));
        var monitor = new BatteryMonitor(new FakeRazerDevice(), store, a => a()); // synchronous dispatch
        var fires = new List<int>();
        monitor.LowBatteryCrossed += (_, pct) => fires.Add(pct);
        lowFires = fires;
        return monitor;
    }

    private static BatteryReading Online(int pct, bool charging) =>
        new(pct, charging, true, DateTimeOffset.Now);

    [Fact]
    public void Online_reading_sets_online_state()
    {
        var m = NewMonitor(out _);
        m.ProcessReading(Online(87, true));
        Assert.Equal(DeviceStatus.Online, m.State.Status);
        Assert.Equal(87, m.State.Percent);
        Assert.True(m.State.Charging);
    }

    [Fact]
    public void Low_battery_fires_once_at_or_below_threshold_while_discharging()
    {
        var m = NewMonitor(out var fires);
        m.ProcessReading(Online(80, false)); // armed
        m.ProcessReading(Online(15, false)); // fire (inclusive)
        m.ProcessReading(Online(10, false)); // no second fire
        Assert.Equal(new[] { 15 }, fires);
    }

    [Fact]
    public void Charging_suppresses_and_does_not_rearm()
    {
        var m = NewMonitor(out var fires);
        m.ProcessReading(Online(12, true));  // plugged in below threshold: no fire
        m.ProcessReading(Online(12, false)); // unplugged still below: no fire (never recovered)
        Assert.Empty(fires);
    }

    [Fact]
    public void Rearms_only_after_recovering_above_threshold()
    {
        var m = NewMonitor(out var fires);
        m.ProcessReading(Online(80, false)); // armed
        m.ProcessReading(Online(15, false)); // fire
        m.ProcessReading(Online(50, false)); // re-arm (>threshold)
        m.ProcessReading(Online(14, false)); // fire again
        Assert.Equal(new[] { 15, 14 }, fires);
    }

    [Fact]
    public void Staleness_goes_unknown_after_more_than_three_misses()
    {
        var m = NewMonitor(out _);
        m.ProcessReading(Online(50, false));
        var absent = BatteryReading.Absent(DateTimeOffset.Now);
        m.ProcessReading(absent); // miss 1 — keep last
        Assert.Equal(DeviceStatus.Online, m.State.Status);
        m.ProcessReading(absent); // 2
        m.ProcessReading(absent); // 3
        m.ProcessReading(absent); // 4 (>3) -> Unknown
        Assert.Equal(DeviceStatus.Unknown, m.State.Status);
    }

    private static ISettingsStore TempStore() =>
        new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"naga-{Guid.NewGuid():N}.json"));

    [Fact]
    public async Task SetDpiAsync_routes_to_device_and_returns_result()
    {
        var fake = new FakeRazerDevice { SetDpiResult = true };
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        bool ok = await m.SetDpiAsync(1600, 1600);
        Assert.True(ok);
        Assert.Equal(1600, fake.LastSetX);
        Assert.Equal(1600, fake.LastSetY);
    }

    [Fact]
    public async Task GetDpiAsync_returns_device_value()
    {
        var fake = new FakeRazerDevice { Dpi = new DpiSetting(800, 800) };
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        Assert.Equal(new DpiSetting(800, 800), await m.GetDpiAsync());
    }

    [Fact]
    public async Task RefreshNowAsync_resets_the_device_to_re_select_the_active_link()
    {
        // An explicit refresh must drop the cached HID handle so the read re-selects whichever interface is
        // now live (e.g. wired after a USB-C plug) instead of reusing a stale wireless handle.
        var fake = new FakeRazerDevice();
        fake.Enqueue(Online(50, true));
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        await m.RefreshNowAsync();
        Assert.Equal(1, fake.ResetCount);
        Assert.True(m.State.Charging);
    }

    [Fact]
    public async Task SetButtonAsync_routes_profile_and_raw_bytes_to_device_and_returns_result()
    {
        var fake = new FakeRazerDevice { SetButtonResult = true };
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        bool ok = await m.SetButtonAsync(0x03, 0x40, 0x02, new byte[] { 0x01, 0x06 });
        Assert.True(ok);
        var w = Assert.Single(fake.ButtonWrites);
        Assert.Equal(0x03, w.Profile);
        Assert.Equal(0x40, w.ButtonId);
        Assert.Equal(0x02, w.Category);
        Assert.Equal(new byte[] { 0x01, 0x06 }, w.Data);
    }

    [Fact]
    public async Task GetButtonAsync_returns_device_value_or_null()
    {
        var fake = new FakeRazerDevice();
        fake.ButtonActions[(0x03, 0x40)] = new RawButtonAction(0x02, new byte[] { 0x00, 0x3d });
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        var hit = await m.GetButtonAsync(0x03, 0x40);
        Assert.Equal(new RawButtonAction(0x02, fake.ButtonActions[(0x03, 0x40)].Data), hit);
        Assert.Null(await m.GetButtonAsync(0x03, 0x41));
        Assert.Null(await m.GetButtonAsync(0x00, 0x40)); // same button, different profile
    }

    [Fact]
    public async Task GetProfileListAsync_returns_device_value_or_null()
    {
        var fake = new FakeRazerDevice { Profiles = new ProfileList(5, new byte[] { 1, 2 }) };
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        var list = await m.GetProfileListAsync();
        Assert.Equal(new ProfileList(5, fake.Profiles!.Value.Slots), list);

        using var m2 = new BatteryMonitor(new FakeRazerDevice(), TempStore(), a => a());
        Assert.Null(await m2.GetProfileListAsync());
    }

    [Fact]
    public async Task CreateProfileAsync_routes_slot_and_returns_result()
    {
        var fake = new FakeRazerDevice { CreateProfileResult = true };
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        Assert.True(await m.CreateProfileAsync(3));
        Assert.Equal(new byte[] { 3 }, fake.CreatedSlots.ToArray());
    }

    [Fact]
    public async Task GetActiveProfile_passes_through_to_the_device()
    {
        var fake = new FakeRazerDevice { ActiveProfile = 3 };
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        Assert.Equal((byte)3, await m.GetActiveProfileAsync());
    }

    [Fact]
    public async Task SetActiveProfile_forwards_slot_and_result()
    {
        var fake = new FakeRazerDevice { SetActiveProfileResult = true };
        using var m = new BatteryMonitor(fake, TempStore(), a => a());
        Assert.True(await m.SetActiveProfileAsync(2));
        Assert.Equal((byte)2, fake.ActiveProfileSets.Single());
    }
}
