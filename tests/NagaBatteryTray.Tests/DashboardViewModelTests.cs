using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public class DashboardViewModelTests
{
    private static Task<bool> NoWrite(int p, ButtonActionKind k, byte m, byte u) => Task.FromResult(true);

    private static AppSettings Seeded()
    {
        var s = new AppSettings { OnboardSlot = 3 };
        s.ButtonBindings[1] = new ButtonBindingSetting { Kind = ButtonActionKind.Key, Modifiers = 0x01, HidUsage = 0x06 };
        return s;
    }

    [Fact]
    public void Callouts_seed_from_the_table()
    {
        var vm = new DashboardViewModel(Seeded(), runAtStartup: false, NoWrite);
        Assert.Equal(12, vm.Callouts.Count);
        Assert.Equal("Ctrl+C", vm.Callout(1).BindingText);
        Assert.Equal("2", vm.Callout(2).BindingText); // untouched → factory digit
    }

    [Fact]
    public void Preset_checkmark_follows_current_dpi()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        vm.SetCurrentDpi(new DpiSetting(1600, 1600));
        Assert.True(vm.Presets.Single(p => p.Value == 1600).IsActive);
        Assert.False(vm.Presets.Single(p => p.Value == 800).IsActive);
        vm.Dpi = 800; // slider move re-evaluates
        Assert.True(vm.Presets.Single(p => p.Value == 800).IsActive);
    }

    [Fact]
    public void AddPreset_sorts_dedupes_and_RemovePreset_removes()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        vm.AddPreset(1200);
        vm.AddPreset(1200); // dupe ignored
        Assert.Equal(new[] { 800, 1200, 1600, 3200 }, vm.Presets.Select(p => p.Value));
        vm.RemovePreset(vm.Presets.Single(p => p.Value == 3200));
        Assert.Equal(new[] { 800, 1200, 1600 }, vm.Presets.Select(p => p.Value));
    }

    [Theory]
    [InlineData(ProfileLivenessState.NotAdopted, "No app profile yet")]
    [InlineData(ProfileLivenessState.Live, "bindings live")]
    [InlineData(ProfileLivenessState.NotLive, "another profile")]
    [InlineData(ProfileLivenessState.Unchecked, "Slot 3")]
    [InlineData(ProfileLivenessState.Unknown, "unknown")]
    public void Profile_card_text_tracks_state(ProfileLivenessState state, string fragment)
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        vm.SetLiveness(state);
        Assert.Contains(fragment, vm.ProfileTitle + " " + vm.ProfileDetail);
    }

    [Fact]
    public void ApplyTo_clamps_cadences_and_threshold()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite)
        { PollSeconds = 3, PollChargingSeconds = 1, LowBatteryThreshold = 0 };
        var target = new AppSettings();
        vm.ApplyTo(target);
        Assert.Equal(15, target.PollIntervalSeconds);
        Assert.Equal(15, target.PollIntervalChargingSeconds);
        Assert.Equal(1, target.LowBatteryThreshold);
    }

    [Fact]
    public void ApplyState_maps_online_and_offline()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        vm.ApplyState(DeviceState.Online(87, true, false));
        Assert.True(vm.DeviceOnline);
        Assert.Contains("87", vm.BatteryChipText);
        vm.ApplyState(DeviceState.Unknown);
        Assert.False(vm.DeviceOnline);
    }
}
