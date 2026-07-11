using NagaBatteryTray.Startup;
using Xunit;

public class StartupRegistrationTests
{
    // Round-trips a real per-user scheduled task (named uniquely, never fires, deleted
    // at the end). Exercises the schtasks Create/Query/Delete path on Windows.
    [Fact]
    public void Enable_then_IsEnabled_is_true_then_Disable_is_false()
    {
        var reg = new StartupRegistration($"NagaTest-{Guid.NewGuid():N}");
        try
        {
            Assert.False(reg.IsEnabled());
            reg.Enable();
            Assert.True(reg.IsEnabled());
            reg.Disable();
            Assert.False(reg.IsEnabled());
        }
        finally
        {
            reg.Disable(); // no leaked task if an assertion above failed
        }
    }
}
