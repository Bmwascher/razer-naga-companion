using NagaBatteryTray.Startup;
using Xunit;

public class StartupRegistrationTests
{
    private static StartupRegistration NewReg() =>
        new($"NagaTest-{Guid.NewGuid():N}", @"Software\NagaBatteryTrayTests\Run");

    [Fact]
    public void Enable_then_IsEnabled_is_true_then_Disable_is_false()
    {
        var reg = NewReg();
        Assert.False(reg.IsEnabled());
        reg.Enable();
        Assert.True(reg.IsEnabled());
        reg.Disable();
        Assert.False(reg.IsEnabled());
    }
}
