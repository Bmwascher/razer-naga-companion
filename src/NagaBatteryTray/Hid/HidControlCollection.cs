using System.Collections.Generic;
using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace NagaBatteryTray.Hid;

/// <summary>The real collection: a Windows HID handle. The 90-byte Razer feature report lives on the
/// mouse HID collection (mi_00 on the V2, mi_03 on the V3 — never key on interface number;
/// GetMaxFeatureReportLength == 91 is the selector), which Windows owns, so it must be opened with
/// CreateFile(dwDesiredAccess = 0) and exchanged via HidD_SetFeature/HidD_GetFeature.</summary>
internal sealed class HidControlCollection : IControlCollection
{
    private readonly SafeFileHandle _handle;

    internal HidControlCollection(int pid, SafeFileHandle handle) { Pid = pid; _handle = handle; }

    public int Pid { get; }
    public bool IsOpen { get; private set; } = true;

    public bool SetFeature(byte[] buffer) => Feature(HidD_SetFeature(_handle, buffer, buffer.Length));
    public bool GetFeature(byte[] buffer) => Feature(HidD_GetFeature(_handle, buffer, buffer.Length));

    private bool Feature(bool ok)
    {
        if (!ok) IsOpen = false;
        return ok;
    }

    public void Dispose() { IsOpen = false; _handle.Dispose(); }

    // the marshalling attributes are load-bearing: they define the native success/failure boundary
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
}

/// <summary>The real source: HidSharp enumeration plus CreateFile.</summary>
internal sealed class HidControlCollectionSource : IControlCollectionSource
{
    public IEnumerable<IControlCollection> Open(int[] pids)
    {
        foreach (int pid in pids)
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                int max;
                try { max = dev.GetMaxFeatureReportLength(); } catch { continue; }
                if (max != RazerProtocol.BufferLength) continue;

                var h = CreateFile(dev.DevicePath, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); continue; }
                yield return new HidControlCollection(pid, h);
            }
    }

    private const uint FileShareReadWrite = 0x3; // FILE_SHARE_READ | FILE_SHARE_WRITE
    private const uint OpenExisting = 0x3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
}
