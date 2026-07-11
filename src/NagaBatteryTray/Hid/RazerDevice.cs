using System.Collections.Generic;
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
    private byte _tid; // transaction id confirmed working on the currently-open collection (0 = not connected)
    private bool _wired; // true when the live collection is the wired interface (0x00A7), false for wireless (0x00A8)
    private bool _loggedError;

    public RazerDevice(ISettingsStore settings) => _settings = settings;

    public async Task<BatteryReading> ReadAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        try
        {
            byte tid = await EnsureConnectedAsync(ct);
            if (tid == 0) return BatteryReading.Absent(now);

            var battery = await QueryAsync(tid, RazerProtocol.CommandIdBattery, ct);
            // A live handle that stops answering (e.g. the wireless receiver after the mouse switches to
            // wired) replies with a timeout status, not a HID error — so drop it here to force the next
            // poll to re-select whichever collection is now live.
            if (battery is null) { CloseHandle(); return BatteryReading.Absent(now); }

            var charging = await QueryAsync(tid, RazerProtocol.CommandIdCharging, ct);
            int percent = RazerProtocol.ScaleBattery(battery.Value);
            bool isCharging = charging is not null && charging.Value != 0;
            _loggedError = false;
            return new BatteryReading(percent, isCharging, true, now, _wired);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseHandle();
            LogOnce(ex);
            return BatteryReading.Absent(now);
        }
    }

    /// <summary>Ensures <see cref="_handle"/> points at a collection that actually answers, and returns its
    /// working transaction id (0 = none reachable). Reuses a live connection cheaply; on (re)connect it tries
    /// each candidate collection — wired first — and keeps the first that replies to a battery query. The
    /// verify step is essential: the wireless receiver stays enumerated when the mouse switches to wired, so
    /// picking by enumeration order alone would lock onto a collection that can no longer reach the mouse.</summary>
    private async Task<byte> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_handle is { IsInvalid: false } && _tid != 0) return _tid;
        CloseHandle();

        foreach (var (pid, path) in FindControlPaths())
        {
            var h = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); continue; }
            _handle = h;

            byte tid = await ResolveTransactionIdAsync(ct);
            // ResolveTransactionIdAsync short-circuits on a cached id without touching this handle, so confirm
            // this collection truly answers before committing to it; otherwise fall through to the next.
            if (tid != 0 && await QueryAsync(tid, RazerProtocol.CommandIdBattery, ct) is not null)
            {
                _tid = tid;
                _wired = pid == RazerProtocol.MousePidWired;
                return tid;
            }
            CloseHandle();
        }
        return 0;
    }

    /// <summary>Mouse HID collections exposing the 90+1 byte feature report (with their PID), wired (0x00A7)
    /// before wireless (0x00A8): a cabled mouse must win over a still-plugged-in receiver that can no longer
    /// reach it. The PID is returned so the caller can report the active link (wired vs wireless).</summary>
    private static IEnumerable<(int Pid, string Path)> FindControlPaths()
    {
        foreach (int pid in new[] { RazerProtocol.MousePidWired, RazerProtocol.MousePidWireless })
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                int max;
                try { max = dev.GetMaxFeatureReportLength(); } catch { continue; }
                if (max == RazerProtocol.BufferLength) yield return (pid, dev.DevicePath);
            }
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

    /// <summary>One SET->wait->GET round-trip with one busy retry. Returns the raw 91-byte reply or null on failure.
    /// On a failed HID call the handle is closed so the next EnsureOpen re-acquires.</summary>
    private async Task<byte[]?> ExchangeAsync(byte[] request, CancellationToken ct)
    {
        if (_handle is null || _handle.IsInvalid) return null;
        if (!HidD_SetFeature(_handle, request, request.Length)) { CloseHandle(); return null; }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(_settings.Settings.SetReadDelayMs, ct);
            var reply = new byte[RazerProtocol.BufferLength];
            reply[0] = 0x00;
            if (!HidD_GetFeature(_handle, reply, reply.Length)) { CloseHandle(); return null; }
            if (reply[1] == 0x01) { await Task.Delay(200, ct); continue; } // Busy: wait, retry GET once
            return reply;
        }
        return null; // still busy after retries
    }

    /// <summary>SET->GET a power-class query and return the data byte, or null on failure.</summary>
    private async Task<byte?> QueryAsync(byte transactionId, byte commandId, CancellationToken ct)
    {
        var reply = await ExchangeAsync(RazerProtocol.BuildFeatureBuffer(transactionId, commandId), ct);
        if (reply is null) return null;
        return RazerProtocol.ParseReply(reply, out byte value) == ReplyResult.Success ? value : null;
    }

    public async Task<DpiSetting?> GetDpiAsync(CancellationToken ct)
    {
        try
        {
            byte tid = await EnsureConnectedAsync(ct);
            if (tid == 0) return null;
            var reply = await ExchangeAsync(RazerProtocol.BuildGetDpiBuffer(tid), ct);
            if (reply is null) return null;
            if (RazerProtocol.ParseDpiReply(reply, out int x, out int y) != ReplyResult.Success) return null;
            return new DpiSetting(x, y);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseHandle();
            LogOnce(ex);
            return null;
        }
    }

    public async Task<bool> SetDpiAsync(int dpiX, int dpiY, CancellationToken ct)
    {
        try
        {
            byte tid = await EnsureConnectedAsync(ct);
            if (tid == 0) return false;
            var reply = await ExchangeAsync(RazerProtocol.BuildSetDpiBuffer(tid, dpiX, dpiY), ct);
            if (reply is null) return false;
            return RazerProtocol.ParseDpiReply(reply, out _, out _) == ReplyResult.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseHandle();
            LogOnce(ex);
            return false;
        }
    }

    public async Task<bool> SetButtonAsync(byte buttonId, byte category, byte[] data, CancellationToken ct)
    {
        try
        {
            byte tid = await EnsureConnectedAsync(ct);
            if (tid == 0) return false;
            var reply = await ExchangeAsync(RazerProtocol.BuildSetButtonBuffer(
                tid, RazerProtocol.ButtonProfileDirect, buttonId, 0x00, category, data), ct);
            // SET ack: status-only (the spike-proven check); correctness is covered by read-back verify.
            return reply is not null && reply[1] == 0x02;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseHandle();
            LogOnce(ex);
            return false;
        }
    }

    public async Task<RawButtonAction?> GetButtonAsync(byte buttonId, CancellationToken ct)
    {
        try
        {
            byte tid = await EnsureConnectedAsync(ct);
            if (tid == 0) return null;
            var reply = await ExchangeAsync(RazerProtocol.BuildGetButtonBuffer(
                tid, RazerProtocol.ButtonProfileDirect, buttonId, 0x00), ct);
            if (reply is null) return null;
            if (RazerProtocol.ParseButtonReply(reply, RazerProtocol.ButtonProfileDirect, buttonId, 0x00,
                    out byte category, out byte[] data) != ReplyResult.Success)
                return null;
            return new RawButtonAction(category, data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseHandle();
            LogOnce(ex);
            return null;
        }
    }

    private void CloseHandle()
    {
        _handle?.Dispose();
        _handle = null;
        _tid = 0;
    }

    private void LogOnce(Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        Console.Error.WriteLine($"[RazerDevice] {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>Drop the cached handle so the next read re-selects whichever interface is now live. Cheap;
    /// the next <see cref="EnsureConnectedAsync"/> re-acquires (wired-first, verifying it answers).</summary>
    public void Reset() => CloseHandle();

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
