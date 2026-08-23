using NagaBatteryTray.Startup;
using Xunit;

[CollectionDefinition("startup-registration", DisableParallelization = true)]
public sealed class StartupRegistrationCollection;

[Collection("startup-registration")]
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

    [Fact]
    public void Enable_survives_a_missing_inherited_temp_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"naga-missing-temp-{Guid.NewGuid():N}");
        string missing = Path.Combine(root, "gone");
        string? originalTemp = Environment.GetEnvironmentVariable("TEMP");
        string? originalTmp = Environment.GetEnvironmentVariable("TMP");
        var reg = new StartupRegistration($"NagaTest-{Guid.NewGuid():N}");

        try
        {
            Environment.SetEnvironmentVariable("TEMP", missing);
            Environment.SetEnvironmentVariable("TMP", missing);
            Assert.False(Directory.Exists(missing));

            reg.Enable();

            Assert.True(reg.IsEnabled());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", originalTemp);
            Environment.SetEnvironmentVariable("TMP", originalTmp);
            reg.Disable();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
