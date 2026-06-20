using System.Threading;
using HidSharp;
using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Diagnostics;

public static class ProbeCommand
{
    public static int Run()
    {
        Console.WriteLine("Naga Battery Tray - HID probe");
        Console.WriteLine("Enumerating VID 0x1532 devices:");
        foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId))
        {
            int maxFeature = -1;
            try { maxFeature = dev.GetMaxFeatureReportLength(); } catch { }
            Console.WriteLine($"  PID 0x{dev.ProductID:x4}  maxFeature={maxFeature}  {SafeName(dev)}");
        }

        foreach (int pid in new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidWired })
        {
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                if (!dev.TryOpen(out var stream)) continue;
                using (stream)
                {
                    foreach (byte tid in RazerProtocol.TransactionIdProbeSet)
                    {
                        var battery = OneShot(stream, tid, RazerProtocol.CommandIdBattery);
                        if (battery is null) continue;
                        var charging = OneShot(stream, tid, RazerProtocol.CommandIdCharging);
                        Console.WriteLine(
                            $"PID 0x{pid:x4} transaction 0x{tid:x2} => battery raw {battery} " +
                            $"({RazerProtocol.ScaleBattery(battery.Value)}%), charging={(charging is null ? "?" : (charging != 0).ToString())}");
                    }
                }
            }
        }
        Console.WriteLine("Done. The transaction id whose battery % is plausible is the right one.");
        return 0;
    }

    private static string SafeName(HidDevice dev)
    {
        try { return dev.GetFriendlyName() ?? "?"; } catch { return "?"; }
    }

    private static byte? OneShot(HidStream stream, byte tid, byte commandId)
    {
        try
        {
            stream.SetFeature(RazerProtocol.BuildFeatureBuffer(tid, commandId));
            Thread.Sleep(400);
            var reply = new byte[RazerProtocol.BufferLength];
            stream.GetFeature(reply);
            return RazerProtocol.ParseReply(reply, out byte value) == ReplyResult.Success ? value : null;
        }
        catch { return null; }
    }
}
