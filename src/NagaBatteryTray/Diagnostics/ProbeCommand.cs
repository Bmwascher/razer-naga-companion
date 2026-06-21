using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using HidSharp;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Diagnostics;

public static class ProbeCommand
{
    public static int Run()
    {
        Console.WriteLine("Naga Battery Tray - HID probe (raw HidD_*, zero-access open)\n");

        foreach (int pid in new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidWired })
        {
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                int max = -1;
                try { max = dev.GetMaxFeatureReportLength(); } catch { }
                if (max != RazerProtocol.BufferLength) continue; // only collections with the 90+1 feature report

                Console.WriteLine($"PID 0x{pid:x4} {Mi(dev.DevicePath)} max={max} -> raw zero-access open");
                using var h = CreateFile(dev.DevicePath, 0, 0x3, IntPtr.Zero, 0x3, 0, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    Console.WriteLine($"  CreateFile failed err={Marshal.GetLastWin32Error()}");
                    continue;
                }
                Console.WriteLine("  opened OK (zero-access)");
                foreach (byte tid in RazerProtocol.TransactionIdProbeSet)
                    Console.WriteLine($"  tid 0x{tid:x2}: {OneShot(h, tid)}");
            }
        }
        Console.WriteLine("\n--- RazerDevice.ReadAsync (production class) ---");
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"naga-probe-{Guid.NewGuid():N}.json"));
        using (var device = new RazerDevice(store))
        {
            var reading = device.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"  present={reading.IsPresent} percent={reading.Percent}% charging={reading.IsCharging} cachedTid={store.Settings.CachedTransactionId}");
        }

        Console.WriteLine("\nLegend: status 0x02=success, 0x01=busy(asleep), other=fail.");
        return 0;
    }

    public static int RunDpi()
    {
        Console.WriteLine("Naga Battery Tray - GET DPI probe (raw hex for offset verification)\n");
        foreach (int pid in new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidWired })
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                int max = -1;
                try { max = dev.GetMaxFeatureReportLength(); } catch { }
                if (max != RazerProtocol.BufferLength) continue;

                using var h = CreateFile(dev.DevicePath, 0, 0x3, IntPtr.Zero, 0x3, 0, IntPtr.Zero);
                if (h.IsInvalid) { Console.WriteLine($"CreateFile failed err={Marshal.GetLastWin32Error()}"); continue; }

                byte tid = 0x1f;
                var buf = RazerProtocol.BuildGetDpiBuffer(tid);
                if (!HidD_SetFeature(h, buf, buf.Length)) { Console.WriteLine($"SetFeature failed err={Marshal.GetLastWin32Error()}"); continue; }
                Thread.Sleep(400);
                var reply = new byte[RazerProtocol.BufferLength];
                reply[0] = 0;
                if (!HidD_GetFeature(h, reply, reply.Length)) { Console.WriteLine($"GetFeature failed err={Marshal.GetLastWin32Error()}"); continue; }

                Console.WriteLine($"PID 0x{pid:x4} status=0x{reply[1]:x2}");
                Console.WriteLine("  reply[0..15]: " + string.Join(" ", reply.Take(16).Select(b => b.ToString("x2"))));
                int x = (reply[10] << 8) | reply[11];
                int y = (reply[12] << 8) | reply[13];
                Console.WriteLine($"  decoded @offsets[10..13]: X={x} Y={y}");
            }
        Console.WriteLine("\nIf you set DPI to 1600 in another tool first, expect reply[10..11] = 06 40 and X=1600.");
        return 0;
    }

    public static int RunDock()
    {
        Console.WriteLine("Naga Battery Tray - Mouse Dock Pro probe (PID 0x00A4, battery + charging)\n");

        bool any = false;
        foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, RazerProtocol.DockPid))
        {
            int max = -1;
            try { max = dev.GetMaxFeatureReportLength(); } catch { }
            if (max != RazerProtocol.BufferLength) continue; // only the 90+1 feature-report collection
            any = true;

            Console.WriteLine($"DOCK 0x{RazerProtocol.DockPid:x4} {Mi(dev.DevicePath)} max={max} -> raw zero-access open");
            using var h = CreateFile(dev.DevicePath, 0, 0x3, IntPtr.Zero, 0x3, 0, IntPtr.Zero);
            if (h.IsInvalid) { Console.WriteLine($"  CreateFile failed err={Marshal.GetLastWin32Error()}"); continue; }
            Console.WriteLine("  opened OK (zero-access)");

            foreach (byte tid in new byte[] { 0x1f, 0xff })
            {
                Console.WriteLine($"  tid 0x{tid:x2} battery : {DockOneShot(h, tid, RazerProtocol.CommandIdBattery)}");
                Console.WriteLine($"  tid 0x{tid:x2} charging: {DockOneShot(h, tid, RazerProtocol.CommandIdCharging)}");
            }
        }

        if (!any)
            Console.WriteLine($"No dock collection found (VID 0x{RazerProtocol.VendorId:x4} PID 0x{RazerProtocol.DockPid:x4}, feature len {RazerProtocol.BufferLength}).");
        Console.WriteLine("\nRun in each state: mouse off-dock / docked+charging / docked+asleep / dock present not charging.");
        Console.WriteLine("Legend: status 0x02=success, 0x01=busy(asleep/no relay), other=fail. battery raw 0..255; charging 0/1 at byte[10].");
        return 0;
    }

    private static string OneShot(SafeFileHandle h, byte tid)
    {
        try
        {
            var buf = RazerProtocol.BuildFeatureBuffer(tid, RazerProtocol.CommandIdBattery);
            if (!HidD_SetFeature(h, buf, buf.Length))
                return $"SetFeature failed err={Marshal.GetLastWin32Error()}";
            Thread.Sleep(400);
            var reply = new byte[RazerProtocol.BufferLength];
            reply[0] = 0;
            if (!HidD_GetFeature(h, reply, reply.Length))
                return $"GetFeature failed err={Marshal.GetLastWin32Error()}";
            var r = RazerProtocol.ParseReply(reply, out byte v);
            return $"status=0x{reply[1]:x2} {r} raw={v} ({RazerProtocol.ScaleBattery(v)}%)";
        }
        catch (Exception ex) { return $"EXC {ex.Message}"; }
    }

    private static string DockOneShot(SafeFileHandle h, byte tid, byte commandId)
    {
        try
        {
            var buf = RazerProtocol.BuildFeatureBuffer(tid, commandId);
            if (!HidD_SetFeature(h, buf, buf.Length))
                return $"SetFeature failed err={Marshal.GetLastWin32Error()}";
            Thread.Sleep(400);
            var reply = new byte[RazerProtocol.BufferLength];
            reply[0] = 0;
            if (!HidD_GetFeature(h, reply, reply.Length))
                return $"GetFeature failed err={Marshal.GetLastWin32Error()}";
            var hex = string.Join(" ", reply.Take(16).Select(b => b.ToString("x2")));
            var r = RazerProtocol.ParseReply(reply, out byte v);
            string decoded = commandId == RazerProtocol.CommandIdBattery
                ? $"raw={v} ({RazerProtocol.ScaleBattery(v)}%)"
                : $"charging={v}";
            return $"status=0x{reply[1]:x2} {r} {decoded}  [{hex}]";
        }
        catch (Exception ex) { return $"EXC {ex.Message}"; }
    }

    private static string Mi(string p)
    {
        int i = p.IndexOf("mi_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && i + 5 <= p.Length ? p.Substring(i, 5) : "mi_??";
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
}
