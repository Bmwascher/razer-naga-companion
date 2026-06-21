namespace NagaBatteryTray.Monitoring;

public enum DeviceStatus { Unknown, Online }

public readonly record struct DeviceState(DeviceStatus Status, int Percent, bool Charging, bool Wired = false)
{
    public static DeviceState Unknown { get; } = new(DeviceStatus.Unknown, 0, false);
    public static DeviceState Online(int percent, bool charging, bool wired = false) => new(DeviceStatus.Online, percent, charging, wired);
}
