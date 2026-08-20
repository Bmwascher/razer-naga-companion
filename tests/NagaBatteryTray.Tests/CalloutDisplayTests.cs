using NagaBatteryTray.Hid;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public sealed class CalloutDisplayTests
{
    private static CalloutViewModel Build() =>
        new(1, (_, _, _, _) => Task.FromResult(true), (_, _) => Task.FromResult(true));

    [Fact]
    public void TheOneByteStubReadsAsNotSet()
    {
        var c = Build();
        c.SetFromDevice(new RawButtonAction(RazerProtocol.FnKeyboard, new byte[] { 0x00 }));

        Assert.Equal("Not set", c.BindingText);
        Assert.False(c.Failed);
    }

    [Fact]
    public void ARealKeyStillDecodes()
    {
        var c = Build();
        c.SetFromDevice(new RawButtonAction(RazerProtocol.FnKeyboard, new byte[] { 0x00, 0x3a }));

        Assert.NotEqual("Not set", c.BindingText);
        Assert.False(c.Failed);
    }

    [Fact]
    public void AGenuinelyForeignCategoryStillSaysSynapse()
    {
        var c = Build();
        c.SetFromDevice(new RawButtonAction(0x05, new byte[] { 0x01, 0x02 }));

        Assert.Contains("Synapse", c.BindingText);
    }

    [Fact]
    public void AnOverlongKeyboardReplyIsNotCalledUnset()
    {
        var c = Build();
        c.SetFromDevice(new RawButtonAction(RazerProtocol.FnKeyboard, new byte[] { 0x00, 0x3a, 0x01 }));

        // never measured on either mouse; saying "Not set" about an assigned button invites an overwrite
        Assert.Contains("Synapse", c.BindingText);
    }
}
