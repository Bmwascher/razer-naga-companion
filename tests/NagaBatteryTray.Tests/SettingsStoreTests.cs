using NagaBatteryTray.Settings;
using Xunit;

public class SettingsStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"naga-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void Missing_file_yields_defaults_and_writes_them()
    {
        var path = TempFile();
        var store = new JsonSettingsStore(path);

        Assert.Equal(60, store.Settings.PollIntervalSeconds);
        Assert.Equal(15, store.Settings.PollIntervalChargingSeconds);
        Assert.Equal(15, store.Settings.LowBatteryThreshold);
        Assert.True(store.Settings.LowBatteryNotify);
        Assert.Null(store.Settings.CachedTransactionId);
        Assert.Equal(400, store.Settings.SetReadDelayMs);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void GetCachedTransactionId_is_null_when_unprobed()
    {
        var store = new JsonSettingsStore(TempFile());
        Assert.Null(store.GetCachedTransactionId());
    }

    [Fact]
    public void SetCachedTransactionId_persists_and_parses_hex()
    {
        var path = TempFile();
        new JsonSettingsStore(path).SetCachedTransactionId(0x1f);

        var reloaded = new JsonSettingsStore(path);
        Assert.Equal((byte)0x1f, reloaded.GetCachedTransactionId());
        Assert.Equal("0x1f", reloaded.Settings.CachedTransactionId);
    }

    [Fact]
    public void OnboardSlot_defaults_null_and_round_trips()
    {
        var path = TempFile();
        var store = new JsonSettingsStore(path);
        Assert.Null(store.Settings.OnboardSlot);
        store.Settings.OnboardSlot = 3;
        store.Save();
        Assert.Equal(3, new JsonSettingsStore(path).Settings.OnboardSlot);
    }

    [Fact]
    public void ButtonBindings_default_is_empty_and_old_files_load_without_the_field()
    {
        var path = TempFile();
        File.WriteAllText(path, """{ "PollIntervalSeconds": 60 }"""); // pre-Stage-2 settings file
        var store = new JsonSettingsStore(path);
        Assert.NotNull(store.Settings.ButtonBindings);
        Assert.Empty(store.Settings.ButtonBindings);
    }

    [Fact]
    public void ButtonBindings_round_trip_through_save_and_reload()
    {
        var path = TempFile();
        var store = new JsonSettingsStore(path);
        store.Settings.ButtonBindings[1] = new ButtonBindingSetting
        {
            Kind = NagaBatteryTray.Hid.ButtonActionKind.Key,
            Modifiers = 0x01,
            HidUsage = 0x06, // Ctrl+C
        };
        store.Settings.ButtonBindings[5] = new ButtonBindingSetting
        {
            Kind = NagaBatteryTray.Hid.ButtonActionKind.Disabled,
        };
        store.Save();

        var reloaded = new JsonSettingsStore(path);
        Assert.Equal(2, reloaded.Settings.ButtonBindings.Count);
        var b1 = reloaded.Settings.ButtonBindings[1];
        Assert.Equal(NagaBatteryTray.Hid.ButtonActionKind.Key, b1.Kind);
        Assert.Equal(0x01, b1.Modifiers);
        Assert.Equal(0x06, b1.HidUsage);
        Assert.Equal(NagaBatteryTray.Hid.ButtonActionKind.Disabled, reloaded.Settings.ButtonBindings[5].Kind);
        Assert.Contains("\"Kind\": \"Key\"", File.ReadAllText(path)); // enum stored as a readable string
    }

    [Fact]
    public void Theme_defaults_porcelain_and_round_trips()
    {
        var path = TempFile();
        var store = new JsonSettingsStore(path);
        Assert.Equal("Porcelain", store.Settings.Theme);
        store.Settings.Theme = "Ember";
        store.Save();
        Assert.Equal("Ember", new JsonSettingsStore(path).Settings.Theme);
    }

    [Fact]
    public void DpiPresets_default_and_round_trip()
    {
        var path = TempFile();
        var store = new JsonSettingsStore(path);
        Assert.Equal(new[] { 800, 1600, 3200 }, store.Settings.DpiPresets);
        store.Settings.DpiPresets = new List<int> { 400, 12000 };
        store.Save();
        Assert.Equal(new[] { 400, 12000 }, new JsonSettingsStore(path).Settings.DpiPresets);
    }

    [Fact]
    public void Theme_and_DpiPresets_default_when_old_files_load_without_those_fields()
    {
        var path = TempFile();
        File.WriteAllText(path, """{ "PollIntervalSeconds": 60 }"""); // pre-theme/DPI-presets settings file
        var store = new JsonSettingsStore(path);
        Assert.Equal("Porcelain", store.Settings.Theme);
        Assert.Equal(new[] { 800, 1600, 3200 }, store.Settings.DpiPresets);
        Assert.Equal("Gauge", store.Settings.TrayIconStyle);
    }

    [Fact]
    public void TrayIconStyle_defaults_gauge_and_round_trips()
    {
        var path = TempFile();
        var store = new JsonSettingsStore(path);
        Assert.Equal("Gauge", store.Settings.TrayIconStyle);
        store.Settings.TrayIconStyle = "Text";
        store.Save();
        Assert.Equal("Text", new JsonSettingsStore(path).Settings.TrayIconStyle);
    }
}
