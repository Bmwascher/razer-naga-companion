using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Ui;
using Xunit;

public class SettingsViewModelTests
{
    private static AppSettings Sample() => new()
    {
        LowBatteryThreshold = 20,
        PollIntervalSeconds = 60,
        PollIntervalChargingSeconds = 15,
        CachedTransactionId = "0x1f",
    };

    [Fact]
    public void Ctor_copies_editable_fields()
    {
        var vm = new SettingsViewModel(Sample(), runAtStartup: true);
        Assert.Equal(20, vm.LowBatteryThreshold);
        Assert.Equal(60, vm.PollSeconds);
        Assert.Equal(15, vm.PollChargingSeconds);
        Assert.True(vm.RunAtStartup);
    }

    [Fact]
    public void ApplyTo_clamps_and_preserves_unedited_fields()
    {
        var vm = new SettingsViewModel(Sample(), false)
        {
            LowBatteryThreshold = 150, // -> 100
            PollSeconds = 2,           // -> 15 (floor)
            PollChargingSeconds = 9,   // -> 15 (floor)
        };
        var target = Sample();
        vm.ApplyTo(target);
        Assert.Equal(100, target.LowBatteryThreshold);
        Assert.Equal(15, target.PollIntervalSeconds);
        Assert.Equal(15, target.PollIntervalChargingSeconds);
        Assert.Equal("0x1f", target.CachedTransactionId); // untouched
    }

    [Fact]
    public void Dpi_setter_clamps_100_to_30000()
    {
        var vm = new SettingsViewModel(Sample(), false);
        vm.Dpi = 50;     Assert.Equal(100, vm.Dpi);
        vm.Dpi = 99999;  Assert.Equal(30000, vm.Dpi);
        vm.Dpi = 1600;   Assert.Equal(1600, vm.Dpi);
    }

    [Fact]
    public void SetCurrentDpi_seeds_value_and_marks_present()
    {
        var vm = new SettingsViewModel(Sample(), false);
        vm.SetCurrentDpi(new DpiSetting(1600, 1600));
        Assert.Equal(1600, vm.Dpi);
        Assert.Contains("1600", vm.CurrentDpiText);
        Assert.True(vm.DevicePresent);
    }

    [Fact]
    public void SetCurrentDpi_null_marks_unknown_and_absent()
    {
        var vm = new SettingsViewModel(Sample(), false);
        vm.SetCurrentDpi(null);
        Assert.Equal("Current: unknown", vm.CurrentDpiText);
        Assert.False(vm.DevicePresent);
    }

    [Fact]
    public void Buttons_are_seeded_from_the_settings_table()
    {
        var settings = Sample();
        settings.ButtonBindings[2] = new ButtonBindingSetting
        {
            Kind = ButtonActionKind.Key, Modifiers = 0x01, HidUsage = 0x06,
        };
        settings.ButtonBindings[7] = new ButtonBindingSetting { Kind = ButtonActionKind.Disabled };
        var vm = new SettingsViewModel(settings, false);

        Assert.Equal(12, vm.Buttons.Count);
        Assert.Equal("Ctrl+C", vm.Row(2).CurrentText);
        Assert.Equal("Disabled", vm.Row(7).CurrentText);
        Assert.Equal("Default", vm.Row(1).CurrentText);
        Assert.Empty(vm.GetPendingButtonOps()); // freshly seeded — nothing pending
    }

    [Fact]
    public void GetPendingButtonOps_collects_only_changed_rows()
    {
        var vm = new SettingsViewModel(Sample(), false);
        vm.Row(1).StageKey(0x00, 0x04);  // A
        vm.Row(12).StageDisabled();
        var ops = vm.GetPendingButtonOps();
        Assert.Equal(2, ops.Count);
        Assert.Equal(1, ops[0].Position);
        Assert.Equal(12, ops[1].Position);
    }
}
