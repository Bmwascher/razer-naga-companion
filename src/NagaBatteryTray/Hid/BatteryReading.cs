namespace NagaBatteryTray.Hid;

public readonly record struct BatteryReading(int Percent, bool IsCharging, bool IsPresent, DateTimeOffset Timestamp)
{
    public static BatteryReading Absent(DateTimeOffset now) => new(0, false, false, now);
}
