namespace NagaBatteryTray.Settings;

public sealed class AppSettings
{
    public int PollIntervalSeconds { get; set; } = 60;
    public int PollIntervalChargingSeconds { get; set; } = 15;
    public int LowBatteryThreshold { get; set; } = 15;
    public bool LowBatteryNotify { get; set; } = true;
    public string? CachedTransactionId { get; set; } = null; // e.g. "0x1f"; null = unprobed
    public int SetReadDelayMs { get; set; } = 400;
}
