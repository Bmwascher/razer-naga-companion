using NagaBatteryTray.Hid;
using Xunit;

public class ButtonBindingTests
{
    [Fact]
    public void ToWire_key_binding_yields_keyboard_category_and_payload()
    {
        var b = new ButtonBinding(0x40, ButtonActionKind.Key, Modifiers: 0x01, HidUsage: 0x06); // Ctrl+C
        var (category, data) = b.ToWire();
        Assert.Equal(RazerProtocol.FnKeyboard, category);
        Assert.Equal(new byte[] { 0x01, 0x06 }, data);
    }

    [Fact]
    public void ToWire_disabled_yields_disabled_category_and_empty_payload()
    {
        var b = new ButtonBinding(0x41, ButtonActionKind.Disabled, 0, 0);
        var (category, data) = b.ToWire();
        Assert.Equal(RazerProtocol.FnDisabled, category);
        Assert.Empty(data);
    }

    [Fact]
    public void ToWire_default_throws()
    {
        // a Default binding is a marker (drop from the table) and must never reach the device
        var b = new ButtonBinding(0x40, ButtonActionKind.Default, 0, 0);
        Assert.Throws<InvalidOperationException>(() => b.ToWire());
    }

    [Theory]
    [InlineData(1, 0x40)]
    [InlineData(12, 0x4b)]
    public void IdForPosition_maps_grid_position_to_firmware_id(int position, byte expected)
    {
        Assert.Equal(expected, NagaV2ProButtons.IdForPosition(position));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void IdForPosition_rejects_out_of_range(int position)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NagaV2ProButtons.IdForPosition(position));
    }

    [Theory]
    [InlineData(1, 0x1e)]   // "1"
    [InlineData(9, 0x26)]   // "9"
    [InlineData(10, 0x27)]  // "0"
    [InlineData(11, 0x2d)]  // "-"
    [InlineData(12, 0x2e)]  // "="
    public void FactoryBindingForPosition_is_the_unmodified_digits_row(int position, byte usage)
    {
        var b = NagaV2ProButtons.FactoryBindingForPosition(position);
        Assert.Equal(NagaV2ProButtons.IdForPosition(position), b.ButtonId);
        var (category, data) = b.ToWire();
        Assert.Equal(RazerProtocol.FnKeyboard, category);
        Assert.Equal(new byte[] { 0x00, usage }, data);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void FactoryBindingForPosition_rejects_out_of_range(int position)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NagaV2ProButtons.FactoryBindingForPosition(position));
    }
}
