using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Hid;

public sealed class RazerDevice : IRazerDevice
{
    private readonly ISettingsStore _settings;
    private HidStream? _stream;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            CloseStream();
            LogOnce(ex);
            return BatteryReading.Absent(now);
        }
    }

    private bool EnsureOpen()
    {
        if (_stream is not null) return true;
        var device = FindControlDevice();
        if (device is null) return false;
        if (!device.TryOpen(out _stream)) { _stream = null; return false; }
        _stream.ReadTimeout = 1000;
        _stream.WriteTimeout = 1000;
        return true;
    }

    /// <summary>Enumerate VID 0x1532 mouse PIDs and pick the vendor 0xFF00 control collection.</summary>
    private static HidDevice? FindControlDevice()
    {
        foreach (int pid in new[] { RazerProtocol.MousePidWireless, RazerProtocol.MousePidWired })
        {
            foreach (var dev in DeviceList.Local.GetHidDevices(RazerProtocol.VendorId, pid))
            {
                if (HasVendorUsagePage(dev)) return dev;
            }
        }
        return null;
    }

    private static bool HasVendorUsagePage(HidDevice dev)
    {
        try
        {
            var descriptor = dev.GetReportDescriptor();
            foreach (var item in descriptor.DeviceItems)
                foreach (uint usage in item.Usages.GetAllValues())
                    if ((usage >> 16) == RazerProtocol.UsagePageVendor) return true;
        }
        catch { /* some collections refuse descriptor read */ }
        return false;
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
        if (_stream is null) return null;
        var buffer = RazerProtocol.BuildFeatureBuffer(transactionId, commandId);
        _stream.SetFeature(buffer);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(_settings.Settings.SetReadDelayMs, ct);
            var reply = new byte[RazerProtocol.BufferLength];
            reply[0] = 0x00;
            _stream.GetFeature(reply);

            var result = RazerProtocol.ParseReply(reply, out byte value);
            if (result == ReplyResult.Success) return value;
            if (result == ReplyResult.Failed) return null;
            await Task.Delay(200, ct); // Busy: wait a bit more, then retry the GET once
        }
        return null;
    }

    private void CloseStream() { _stream?.Dispose(); _stream = null; }

    private void LogOnce(Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        Console.Error.WriteLine($"[RazerDevice] {ex.GetType().Name}: {ex.Message}");
    }

    public void Dispose() => CloseStream();
}
