using System.Text.Json;
using NagaBatteryTray.Settings;
using Xunit;

public sealed class SettingsMigrationTests
{
    private static string WriteTemp(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"naga-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void LegacyProfileNamesMigrateToTheV2()
    {
        string path = WriteTemp("""{"ProfileNames":{"1":"World of Warcraft"}}""");
        var store = new JsonSettingsStore(path);

        Assert.Equal("World of Warcraft", store.Settings.ProfileNamesByMouse["Naga V2 Pro"][1]);
        Assert.Empty(store.Settings.ProfileNames);
    }

    [Fact]
    public void MigrationDoesNotOverwriteExistingPerMouseNames()
    {
        string path = WriteTemp("""
            {"ProfileNames":{"1":"Legacy"},"ProfileNamesByMouse":{"Naga V3 Pro":{"1":"Kept"}}}
            """);
        var store = new JsonSettingsStore(path);

        Assert.Equal("Kept", store.Settings.ProfileNamesByMouse["Naga V3 Pro"][1]);
        Assert.False(store.Settings.ProfileNamesByMouse.ContainsKey("Naga V2 Pro"));
    }

    /// <summary>The migration must be one-time in the FILE, not just in the run: a legacy dictionary
    /// left behind re-migrates on every later load, so deleting all per-mouse names brings the old
    /// ones back under the V2 as if they had never been deleted.</summary>
    [Fact]
    public void MigratedLegacyNamesCannotComeBackAfterTheUserDeletesThem()
    {
        string path = WriteTemp("""
            {"ProfileNames":{"1":"Legacy"},"ProfileNamesByMouse":{"Naga V3 Pro":{"1":"Kept"}}}
            """);
        var store = new JsonSettingsStore(path);

        // the user renames every slot back to its default: ApplyTo writes an empty per-mouse map
        store.Settings.ProfileNamesByMouse = new Dictionary<string, Dictionary<byte, string>>();
        store.Save();

        var reloaded = new JsonSettingsStore(path);
        Assert.Empty(reloaded.Settings.ProfileNamesByMouse);
    }

    [Fact]
    public void PreferredMouseDefaultsToNullAndRoundTrips()
    {
        string path = WriteTemp("{}");
        var store = new JsonSettingsStore(path);
        Assert.Null(store.Settings.PreferredMouse);

        store.Settings.PreferredMouse = "Naga V3 Pro";
        store.Save();

        var reloaded = new JsonSettingsStore(path);
        Assert.Equal("Naga V3 Pro", reloaded.Settings.PreferredMouse);
    }

    [Fact]
    public void PerMouseNamesRoundTrip()
    {
        string path = WriteTemp("{}");
        var store = new JsonSettingsStore(path);
        store.Settings.ProfileNamesByMouse["Naga V3 Pro"] = new Dictionary<byte, string> { [2] = "Raid" };
        store.Save();

        var reloaded = new JsonSettingsStore(path);
        Assert.Equal("Raid", reloaded.Settings.ProfileNamesByMouse["Naga V3 Pro"][2]);
    }
}
