namespace NagaBatteryTray.Monitoring;

public enum DeviceStatus { Unknown, Online }

public readonly record struct DeviceState(DeviceStatus Status, int Percent, bool Charging)
{
    public static DeviceState Unknown { get; } = new(DeviceStatus.Unknown, 0, false);
    public static DeviceState Online(int percent, bool charging) => new(DeviceStatus.Online, percent, charging);
}
