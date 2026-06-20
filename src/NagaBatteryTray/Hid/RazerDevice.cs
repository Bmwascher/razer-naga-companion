using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;
using Microsoft.Win32.SafeHandles;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Hid;

/// <summary>
/// Reads battery/charging from a Razer Naga V2 Pro. The 90-byte Razer feature report lives on the
/// mouse HID collection (mi_00, GetMaxFeatureReportLength == 91), which Windows owns — so it must be
/// opened with CreateFile(dwDesiredAccess = 0) and exchanged via HidD_SetFeature/HidD_GetFeature.
/// (No collection exposes usage page 0xFF00 on this device; verified empirically.)
/// </summary>
public sealed class RazerDevice : IRazerDevice
{
    private readonly ISettingsStore _settings;
    private SafeFileHandle? _handle;
    private bool _loggedError;

    public RazerDevice(ISettingsStore settings) => _settings = settings;

    public async Task<BatteryReading> ReadAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        try
        {
            if (!EnsureOpen()) return BatteryReading.Absent(now);

            byte tid = await ResolveTransactionIdAsync(ct);
            if (tid == 0) return BatteryReading.Absent(now);

            var battery = await QueryAsync(tid, RazerProtocol.CommandIdBattery, ct);
            if (battery is null) return BatteryReading.Absent(now);

            var charging = await QueryAsync(tid, RazerProtocol.CommandIdCharging, ct);
            int percent = RazerProtocol.ScaleBattery(battery.Value);
            bool isCharging = charging is not null && charging.Value != 0;
            _loggedError = false;
            return new BatteryReading(percent, isCharging, true, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseHandle();
            LogOnce(ex);
            return BatteryReading.Absent(now);
        }
    }

    private bool EnsureOpen()
    {
        if (_handle is { IsInvalid: false }) return true;
        CloseHandle();

        var path = FindControlPath();
        if (path is null) return false;

        var h = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return false; }
        _handle = h;
        return true;
    }

    /// <summary>The control collection is the mouse's HID device that exposes the 90+1 byte feature report.</summary>
    private static string? FindControlPath()
    {
        foreach (int pid in new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidWired })
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                int max;
                try { max = dev.GetMaxFeatureReportLength(); } catch { continue; }
                if (max == RazerProtocol.BufferLength) return dev.DevicePath;
            }
        return null;
    }

    /// <summary>Returns cached id, else probes the set and caches the winner. 0 = could not resolve.</summary>
    private async Task<byte> ResolveTransactionIdAsync(CancellationToken ct)
    {
        var cached = _settings.GetCachedTransactionId();
        if (cached is not null) return cached.Value;

        foreach (byte tid in RazerProtocol.TransactionIdProbeSet)
        {
            var value = await QueryAsync(tid, RazerProtocol.CommandIdBattery, ct);
            if (value is not null && RazerProtocol.ScaleBattery(value.Value) is >= 0 and <= 100)
            {
                _settings.SetCachedTransactionId(tid);
                return tid;
            }
        }
        return 0;
    }

    /// <summary>One SET->wait->GET round-trip with one busy retry. Returns the data byte or null on failure.</summary>
    private async Task<byte?> QueryAsync(byte transactionId, byte commandId, CancellationToken ct)
    {
        if (_handle is null || _handle.IsInvalid) return null;
        var buffer = RazerProtocol.BuildFeatureBuffer(transactionId, commandId);
        if (!HidD_SetFeature(_handle, buffer, buffer.Length)) return null;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(_settings.Settings.SetReadDelayMs, ct);
            var reply = new byte[RazerProtocol.BufferLength];
            reply[0] = 0x00;
            if (!HidD_GetFeature(_handle, reply, reply.Length)) return null;

            var result = RazerProtocol.ParseReply(reply, out byte value);
            if (result == ReplyResult.Success) return value;
            if (result == ReplyResult.Failed) return null;
            await Task.Delay(200, ct); // Busy: wait a bit more, then retry the GET once
        }
        return null;
    }

    private void CloseHandle()
    {
        _handle?.Dispose();
        _handle = null;
    }

    private void LogOnce(Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        Console.Error.WriteLine($"[RazerDevice] {ex.GetType().Name}: {ex.Message}");
    }

    public void Dispose() => CloseHandle();

    private const uint FileShareReadWrite = 0x3; // FILE_SHARE_READ | FILE_SHARE_WRITE
    private const uint OpenExisting = 0x3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
}
