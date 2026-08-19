using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Ui;
using Xunit;

public class PopupViewModelTests
{
    [Fact]
    public void Apply_maps_online_state()
    {
        var vm = new PopupViewModel();
        vm.Apply(DeviceState.Online(64, charging: false, wired: true));
        Assert.Equal("64%", vm.PercentText);
        Assert.Equal("Wired", vm.Status);
        Assert.Equal(0.64, vm.BarFraction, 3);
    }

    [Fact]
    public void Apply_unknown_resets_to_no_response()
    {
        var vm = new PopupViewModel();
        vm.Apply(DeviceState.Online(64, charging: false, wired: false));
        vm.Apply(DeviceState.Unknown);
        Assert.Equal("-", vm.PercentText);
        Assert.Equal("no response", vm.Status);
        Assert.Equal(0.0, vm.BarFraction);
    }

    [Fact]
    public void Apply_shows_detected_model()
    {
        var vm = new PopupViewModel();
        vm.Apply(DeviceState.Online(64, charging: false, wired: true, model: "Naga V3 Pro"));
        Assert.Equal("Naga V3 Pro", vm.ModelName);
    }

    [Fact]
    public void Apply_unknown_resets_model_to_fallback()
    {
        var vm = new PopupViewModel();
        vm.Apply(DeviceState.Online(64, charging: false, wired: true, model: "Naga V3 Pro"));
        vm.Apply(DeviceState.Unknown);
        Assert.Equal("Razer Naga", vm.ModelName);
    }
}
