namespace NagaBatteryTray.Hid;

public enum ReplyResult { Success, Busy, Failed }

public static class RazerProtocol
{
    public const int VendorId = 0x1532;
    public const int MousePidWireless = 0x00A8;
    public const int MousePidWired = 0x00A7;
    public const int UsagePageVendor = 0xFF00;

    public const int ReportLength = 90;
    public const int BufferLength = 91; // report id + 90

    public const byte CommandClassPower = 0x07;
    public const byte CommandIdBattery = 0x80;
    public const byte CommandIdCharging = 0x84;
    public const byte DataSize = 0x02;

    public static readonly byte[] TransactionIdProbeSet =
        { 0x1f, 0x3f, 0x00, 0xff, 0x08, 0x88, 0x1d, 0x9f };

    /// <summary>XOR of report bytes [2..87] inclusive.</summary>
    public static byte ComputeCrc(byte[] report90)
    {
        byte crc = 0;
        for (int i = 2; i <= 87; i++) crc ^= report90[i];
        return crc;
    }

    /// <summary>Builds the 91-byte HID feature buffer (report id 0x00 + 90-byte report) for a power-class query.</summary>
    public static byte[] BuildFeatureBuffer(byte transactionId, byte commandId)
    {
        var report = new byte[ReportLength];
        report[0] = 0x00;               // status
        report[1] = transactionId;      // transaction_id
        report[5] = DataSize;           // data_size
        report[6] = CommandClassPower;  // command_class
        report[7] = commandId;          // command_id
        report[88] = ComputeCrc(report);
        report[89] = 0x00;              // reserved

        var buffer = new byte[BufferLength];
        buffer[0] = 0x00;               // HID report id
        Array.Copy(report, 0, buffer, 1, ReportLength);
        return buffer;
    }

    /// <summary>Validates a 91-byte feature reply. value = report byte[9] (buffer[10]) on success.</summary>
    public static ReplyResult ParseReply(byte[] buffer91, out byte value)
    {
        value = 0;
        byte status = buffer91[1];
        if (status == 0x01) return ReplyResult.Busy;
        if (status != 0x02) return ReplyResult.Failed;

        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buffer91[i]; // reply report[2..87]
        if (crc != buffer91[89]) return ReplyResult.Failed;

        value = buffer91[10]; // report byte[9]
        return ReplyResult.Success;
    }

    public static int ScaleBattery(byte raw) => (int)Math.Round(raw * 100.0 / 255.0);
}
