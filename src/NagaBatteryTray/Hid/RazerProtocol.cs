namespace NagaBatteryTray.Hid;

public enum ReplyResult { Success, Busy, Failed }

public static class RazerProtocol
{
    public const int VendorId = 0x1532;
    public const int MousePidWireless = 0x00A8;
    public const int MousePidWired = 0x00A7;
    public const int DockPid = 0x00A4;       // Razer Mouse Dock Pro (separate USB device)
    public const int UsagePageVendor = 0xFF00;

    public const int ReportLength = 90;
    public const int BufferLength = 91; // report id + 90

    public const byte CommandClassPower = 0x07;
    public const byte CommandIdBattery = 0x80;
    public const byte CommandIdCharging = 0x84;
    public const byte DataSize = 0x02;

    public const byte CommandClassDpi = 0x04;
    public const byte CommandIdGetDpi = 0x85;
    public const byte CommandIdSetDpi = 0x05;
    public const byte DataSizeDpi = 0x07;
    public const int DpiMin = 100;
    public const int DpiMax = 30000;

    public static readonly byte[] TransactionIdProbeSet =
        { 0x1f, 0x3f, 0x00, 0xff, 0x08, 0x88, 0x1d, 0x9f };

    /// <summary>XOR of report bytes [2..87] inclusive.</summary>
    public static byte ComputeCrc(byte[] report90)
    {
        byte crc = 0;
        for (int i = 2; i <= 87; i++) crc ^= report90[i];
        return crc;
    }

    /// <summary>Assembles a 90-byte report (payload args start at report[8]) into the 91-byte feature buffer.
    /// report[0] (status) and report[89] (reserved) and buffer[0] (report id) stay 0x00.</summary>
    private static byte[] BuildReport(byte transactionId, byte dataSize, byte commandClass, byte commandId, ReadOnlySpan<byte> payload)
    {
        var report = new byte[ReportLength];
        report[1] = transactionId;
        report[5] = dataSize;
        report[6] = commandClass;
        report[7] = commandId;
        for (int i = 0; i < payload.Length; i++) report[8 + i] = payload[i];
        report[88] = ComputeCrc(report);

        var buffer = new byte[BufferLength];
        Array.Copy(report, 0, buffer, 1, ReportLength);
        return buffer;
    }

    /// <summary>Builds the 91-byte HID feature buffer (report id 0x00 + 90-byte report) for a power-class query.</summary>
    public static byte[] BuildFeatureBuffer(byte transactionId, byte commandId) =>
        BuildReport(transactionId, DataSize, CommandClassPower, commandId, ReadOnlySpan<byte>.Empty);

    /// <summary>GET active DPI (X/Y). Request arg[0]=0x00 (NOSTORE).</summary>
    public static byte[] BuildGetDpiBuffer(byte transactionId)
    {
        Span<byte> args = stackalloc byte[7]; // all zero
        return BuildReport(transactionId, DataSizeDpi, CommandClassDpi, CommandIdGetDpi, args);
    }

    /// <summary>SET active DPI (X/Y), persisted to onboard memory (VARSTORE). Values clamped 100..30000.</summary>
    public static byte[] BuildSetDpiBuffer(byte transactionId, int dpiX, int dpiY)
    {
        dpiX = Math.Clamp(dpiX, DpiMin, DpiMax);
        dpiY = Math.Clamp(dpiY, DpiMin, DpiMax);
        Span<byte> args = stackalloc byte[7];
        args[0] = 0x01;                                     // VARSTORE = persist
        args[1] = (byte)(dpiX >> 8); args[2] = (byte)dpiX;  // X big-endian
        args[3] = (byte)(dpiY >> 8); args[4] = (byte)dpiY;  // Y big-endian
        return BuildReport(transactionId, DataSizeDpi, CommandClassDpi, CommandIdSetDpi, args);
    }

    /// <summary>Validates a 91-byte reply: status byte then XOR CRC over buffer[3..88] vs buffer[89].</summary>
    private static ReplyResult ValidateReply(byte[] buffer91)
    {
        byte status = buffer91[1];
        if (status == 0x01) return ReplyResult.Busy;
        if (status != 0x02) return ReplyResult.Failed;
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buffer91[i];
        if (crc != buffer91[89]) return ReplyResult.Failed;
        return ReplyResult.Success;
    }

    /// <summary>Validates a feature reply. value = report byte[9] (buffer[10]) on success.</summary>
    public static ReplyResult ParseReply(byte[] buffer91, out byte value)
    {
        value = 0;
        var r = ValidateReply(buffer91);
        if (r != ReplyResult.Success) return r;
        value = buffer91[10];
        return ReplyResult.Success;
    }

    /// <summary>Validates a DPI reply and decodes X=buffer[10..11], Y=buffer[12..13] (big-endian).
    /// A decoded value outside 100..30000 is treated as Failed (defends against wrong-layout replies).</summary>
    public static ReplyResult ParseDpiReply(byte[] buffer91, out int dpiX, out int dpiY)
    {
        dpiX = 0; dpiY = 0;
        var r = ValidateReply(buffer91);
        if (r != ReplyResult.Success) return r;
        int x = (buffer91[10] << 8) | buffer91[11];
        int y = (buffer91[12] << 8) | buffer91[13];
        if (x < DpiMin || x > DpiMax || y < DpiMin || y > DpiMax) return ReplyResult.Failed;
        dpiX = x; dpiY = y;
        return ReplyResult.Success;
    }

    public static int ScaleBattery(byte raw) => (int)Math.Round(raw * 100.0 / 255.0);
}
