namespace NagaBatteryTray.Settings;

public interface ISettingsStore
{
    AppSettings Settings { get; }
    void Save();
    byte? GetCachedTransactionId();
    void SetCachedTransactionId(byte id);
}
