using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Ui;
using Xunit;

public class IconRendererTests
{
    [Theory]
    [InlineData(96, 100, false)]   // 16 px
    [InlineData(120, 87, false)]   // 20 px
    [InlineData(144, 38, true)]    // 24 px, charging
    [InlineData(192, 12, false)]   // 32 px, low
    public void Render_with_ring_smokes_at_all_sizes(int dpi, int percent, bool charging)
    {
        var icon = IconRenderer.Render(DeviceState.Online(percent, charging, false), dpi);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
        IconRenderer.Destroy(icon);
    }

    [Fact]
    public void Render_unknown_state_smokes()
    {
        var icon = IconRenderer.Render(DeviceState.Unknown, 96);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
        IconRenderer.Destroy(icon);
    }
}
