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
}
