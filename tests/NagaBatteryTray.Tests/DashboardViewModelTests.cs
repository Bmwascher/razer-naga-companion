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

    private static DashboardViewModel NewVm(int? onboardSlot) =>
        new DashboardViewModel(new AppSettings { OnboardSlot = onboardSlot }, runAtStartup: false, NoWrite);

    [Fact]
    public void Callouts_seed_from_the_table()
    {
        var vm = new DashboardViewModel(Seeded(), runAtStartup: false, NoWrite);
        Assert.Equal(12, vm.Callouts.Count);
        Assert.Equal("Ctrl+C", vm.Callout(1).BindingText);
        Assert.Equal("2", vm.Callout(2).BindingText); // untouched → factory digit
    }

    [Fact]
    public void AnyCapturing_tracks_callout_capture_state()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        Assert.False(vm.AnyCapturing);

        vm.Callout(3).BeginCapture();
        Assert.True(vm.AnyCapturing);

        vm.Callout(5).BeginCapture();
        vm.Callout(3).CancelCapture();
        Assert.True(vm.AnyCapturing); // one still armed

        vm.Callout(5).CancelCapture();
        Assert.False(vm.AnyCapturing);
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
    public void CanSavePreset_tracks_current_dpi_against_the_preset_list()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        Assert.False(vm.CanSavePreset);            // no device yet

        vm.SetCurrentDpi(new DpiSetting(1600, 1600));
        Assert.False(vm.CanSavePreset);            // 1600 is already a preset

        vm.Dpi = 1000;
        Assert.True(vm.CanSavePreset);

        vm.AddPreset(1000);
        Assert.False(vm.CanSavePreset);

        vm.RemovePreset(vm.Presets.Single(p => p.Value == 1000));
        Assert.True(vm.CanSavePreset);
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

    [Fact]
    public void SetActiveSlot_maps_states_from_slot_equality()
    {
        var vm = NewVm(onboardSlot: 2);
        vm.SetActiveSlot(2);
        Assert.Contains("Slot 2", vm.ProfileTitle);
        Assert.Contains("live", vm.ProfileDetail); // Live: "● active on the mouse"-style line contains "live"
        Assert.False(vm.CanActivate);

        vm.SetActiveSlot(3);
        Assert.Contains("Slot 3", vm.ProfileDetail); // NotLive: names where the mouse actually is
        Assert.True(vm.CanActivate);

        vm.SetActiveSlot(null);                    // Unknown: unreachable
        Assert.Contains("unknown", vm.ProfileDetail);
        Assert.False(vm.CanActivate);
    }

    [Fact]
    public void SetActiveSlot_without_adopted_slot_stays_NotAdopted()
    {
        var vm = NewVm(onboardSlot: null);
        vm.SetActiveSlot(1);
        Assert.Contains("No app profile", vm.ProfileTitle);
        Assert.False(vm.CanActivate);
    }

    [Fact]
    public void SetProfileNote_overwrites_detail_only()
    {
        var vm = NewVm(onboardSlot: 2);
        vm.SetActiveSlot(3);
        vm.SetProfileNote("Couldn't switch — wiggle the mouse and retry");
        Assert.Contains("wiggle", vm.ProfileDetail);
        Assert.Contains("Slot 2", vm.ProfileTitle); // title untouched
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

    [Fact]
    public void Header_subtitle_is_link_state_only_the_profile_card_owns_slot_identity()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        vm.ApplyState(DeviceState.Online(87, false, false));
        Assert.Equal("Wireless", vm.HeaderSubtitle);
        vm.ApplyState(DeviceState.Online(87, false, true));
        Assert.Equal("Wired", vm.HeaderSubtitle);
        vm.ApplyState(DeviceState.Unknown);
        Assert.Equal("offline", vm.HeaderSubtitle);
    }

    [Fact]
    public async Task ResetAllAsync_counts_all_12_as_ok_when_every_write_succeeds()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        var (ok, failed) = await vm.ResetAllAsync();
        Assert.Equal(12, ok);
        Assert.Equal(0, failed);
    }

    [Fact]
    public async Task ResetAllAsync_counts_failures_for_positions_whose_write_fails()
    {
        Task<bool> FailTwo(int p, ButtonActionKind k, byte m, byte u) => Task.FromResult(p != 3 && p != 7);
        var vm = new DashboardViewModel(Seeded(), false, FailTwo);
        var (ok, failed) = await vm.ResetAllAsync();
        Assert.Equal(10, ok);
        Assert.Equal(2, failed);
    }

    [Fact]
    public async Task ResetAllAsync_counts_a_busy_chip_as_failed_not_reset()
    {
        var pending = new TaskCompletionSource<bool>();
        bool first = true;
        Task<bool> Write(int p, ButtonActionKind k, byte m, byte u)
        {
            if (first) { first = false; return pending.Task; }
            return Task.FromResult(true);
        }
        var vm = new DashboardViewModel(Seeded(), false, Write);
        var inFlight = vm.Callout(3).DisableAsync(); // occupies chip 3

        var (ok, failed) = await vm.ResetAllAsync();
        Assert.Equal(11, ok);
        Assert.Equal(1, failed);

        pending.SetResult(true);
        await inFlight;
    }

    [Fact]
    public void SetAdoptedSlot_updates_the_profile_card_identity_mid_session()
    {
        var vm = new DashboardViewModel(new AppSettings(), false, NoWrite); // fresh install: no slot
        Assert.Contains("No app profile yet", vm.ProfileTitle);

        vm.SetAdoptedSlot(3);
        Assert.Contains("Slot 3 · green", vm.ProfileTitle);
        Assert.DoesNotContain("live", vm.ProfileDetail); // Unchecked: identity only, no claim
    }

    [Fact]
    public void ApplyTo_preserves_an_untouched_sub15_cadence_but_clamps_a_user_change()
    {
        var src = new AppSettings { PollIntervalSeconds = 10 }; // documented hand-edit bypass
        var vm = new DashboardViewModel(src, false, NoWrite);
        var target = new AppSettings();
        vm.ApplyTo(target);
        Assert.Equal(10, target.PollIntervalSeconds);           // untouched → passes through

        vm.PollSeconds = 3;
        vm.ApplyTo(target);
        Assert.Equal(15, target.PollIntervalSeconds);           // user change → floor applies
    }

    [Fact]
    public void DpiSliderPos_endpoints_map_to_dpi_range()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        vm.DpiSliderPos = 0.0;
        Assert.Equal(100, vm.Dpi);
        vm.DpiSliderPos = 1.0;
        Assert.Equal(30000, vm.Dpi);
    }

    [Fact]
    public void DpiSliderPos_round_trips_through_dpi()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite) { Dpi = 1600 };
        double pos = vm.DpiSliderPos;
        vm.DpiSliderPos = pos;
        Assert.Equal(1600, vm.Dpi);
    }

    [Fact]
    public void DpiSliderPos_midpoint_is_sqrt300_times_100_rounded_to_50()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        vm.DpiSliderPos = 0.5;
        Assert.Equal(1750, vm.Dpi);
    }

    [Fact]
    public void DpiSliderPos_setter_always_snaps_dpi_to_a_multiple_of_50()
    {
        var vm = new DashboardViewModel(Seeded(), false, NoWrite);
        vm.DpiSliderPos = 0.37;
        Assert.Equal(0, vm.Dpi % 50);
    }
}
