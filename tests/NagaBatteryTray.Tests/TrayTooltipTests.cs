using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Ui;
using Xunit;

public class TrayTooltipTests
{
    [Fact]
    public void Tooltip_uses_detected_model_when_online()
    {
        var s = DeviceState.Online(72, charging: true, wired: false, model: "Naga V3 Pro");
        Assert.Equal("Naga V3 Pro - 72% (charging)", TrayIconController.Tooltip(s));
    }

    [Fact]
    public void Tooltip_falls_back_when_offline()
    {
        Assert.Equal("Razer Naga - no response", TrayIconController.Tooltip(DeviceState.Unknown));
    }

    [Fact]
    public void Tooltip_falls_back_when_online_without_model()
    {
        var s = DeviceState.Online(50, charging: false);
        Assert.Equal("Razer Naga - 50%", TrayIconController.Tooltip(s));
    }
}
