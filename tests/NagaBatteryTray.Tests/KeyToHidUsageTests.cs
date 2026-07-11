using System.Windows.Input;
using NagaBatteryTray.Ui;
using Xunit;

public class KeyToHidUsageTests
{
    [Theory]
    [InlineData(Key.A, 0x04)]
    [InlineData(Key.Z, 0x1d)]
    [InlineData(Key.D1, 0x1e)]
    [InlineData(Key.D0, 0x27)]
    [InlineData(Key.F1, 0x3a)]
    [InlineData(Key.F12, 0x45)]
    [InlineData(Key.F13, 0x68)]
    [InlineData(Key.F24, 0x73)]
    [InlineData(Key.Enter, 0x28)]
    [InlineData(Key.Space, 0x2c)]
    [InlineData(Key.OemMinus, 0x2d)]
    [InlineData(Key.Home, 0x4a)]
    [InlineData(Key.Up, 0x52)]
    public void TryGetUsage_maps_supported_keys(Key key, byte expected)
    {
        Assert.True(KeyToHidUsage.TryGetUsage(key, out byte usage));
        Assert.Equal(expected, usage);
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.LWin)]
    [InlineData(Key.ImeConvert)]
    public void TryGetUsage_rejects_unsupported_keys(Key key)
    {
        Assert.False(KeyToHidUsage.TryGetUsage(key, out _));
    }

    [Fact]
    public void ToModifierBits_maps_wpf_modifiers_to_hid_bits()
    {
        Assert.Equal(0x00, KeyToHidUsage.ToModifierBits(ModifierKeys.None));
        Assert.Equal(0x01, KeyToHidUsage.ToModifierBits(ModifierKeys.Control));
        Assert.Equal(0x07, KeyToHidUsage.ToModifierBits(
            ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt));
        Assert.Equal(0x08, KeyToHidUsage.ToModifierBits(ModifierKeys.Windows));
    }

    [Theory]
    [InlineData(0x00, 0x06, "C")]
    [InlineData(0x01, 0x06, "Ctrl+C")]
    [InlineData(0x07, 0x3e, "Ctrl+Shift+Alt+F5")]
    [InlineData(0x08, 0x2c, "Win+Space")]
    public void Describe_formats_modifiers_and_key_name(byte mods, byte usage, string expected)
    {
        Assert.Equal(expected, KeyToHidUsage.Describe(mods, usage));
    }

    [Fact]
    public void Describe_unknown_usage_renders_hex()
    {
        Assert.Equal("0x74", KeyToHidUsage.Describe(0x00, 0x74));
    }
}
