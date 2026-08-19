namespace NagaBatteryTray.Monitoring;

public enum DeviceStatus { Unknown, Online }

public readonly record struct DeviceState(DeviceStatus Status, int Percent, bool Charging, bool Wired = false, string? Model = null)
{
    public static DeviceState Unknown { get; } = new(DeviceStatus.Unknown, 0, false);
    public static DeviceState Online(int percent, bool charging, bool wired = false, string? model = null) => new(DeviceStatus.Online, percent, charging, wired, model);
}
