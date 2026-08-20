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
