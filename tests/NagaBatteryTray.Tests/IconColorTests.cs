using NagaBatteryTray.Ui;
using Xunit;

public class IconColorTests
{
    [Theory]
    [InlineData(87, false, 0x44, 0xD6, 0x2C)] // healthy green
    [InlineData(40, false, 0xE0, 0xA2, 0x3E)] // amber
    [InlineData(10, false, 0xE0, 0x47, 0x3E)] // red
    public void ColorForLevel_maps_bands(int pct, bool charging, int r, int g, int b)
    {
        var c = IconRenderer.ColorForLevel(pct, charging);
        Assert.Equal((r, g, b), ((int)c.R, (int)c.G, (int)c.B));
    }

    [Fact]
    public void Charging_is_always_razer_green()
    {
        var c = IconRenderer.ColorForLevel(10, true); // low but charging
        Assert.Equal((0x44, 0xD6, 0x2C), ((int)c.R, (int)c.G, (int)c.B));
    }
}
