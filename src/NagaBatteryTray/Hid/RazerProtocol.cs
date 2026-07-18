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

    public const byte CommandClassButton = 0x02;
    public const byte CommandIdSetButton = 0x0c;   // write a button's onboard function
    public const byte CommandIdGetButton = 0x8c;   // read it back
    public const byte DataSizeButton = 0x0a;       // 10 arg bytes
    public const byte ButtonProfileDirect = 0x00;  // volatile "direct" profile; 0x01..0x05 = onboard slots

    // MVP function categories only (deferred mouse/DPI/media categories are spec §6 prose)
    public const byte FnDisabled = 0x00;
    public const byte FnKeyboard = 0x02;           // data = [modifierBitmask, hidUsage]

    // Spike aux commands (spec §5.2 steps 1 & 4). Device mode: openrazer razerchromacommon.c.
    // Profile lifecycle: razerqdhid cmd_profile (no active-profile get/set is documented).
    public const byte CommandClassInfo = 0x00;
    public const byte CommandIdSetDeviceMode = 0x04;
    public const byte CommandIdGetDeviceMode = 0x84;
    public const byte DataSizeDeviceMode = 0x02;
    public const byte DeviceModeNormal = 0x00;
    public const byte DeviceModeDriver = 0x03;

    public const byte CommandClassProfile = 0x05;
    public const byte CommandIdGetProfileList = 0x81;
    public const byte CommandIdGetActiveProfile = 0x84;  // hardware-verified 2026-07-18 (--probe-profile)
    public const byte CommandIdSetActiveProfile = 0x04;  // undocumented; hardware-UNVERIFIED until --set-test passes
    public const byte CommandIdNewProfile = 0x02;
    public const byte CommandIdDeleteProfile = 0x03;
    public const byte DataSizeProfileList = 0x06;  // 1 capacity byte + up to 5 slot numbers
    public const byte DataSizeProfileEdit = 0x01;

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

    /// <summary>SET a button's onboard function. args = [profile, buttonId, hypershift, category, dataLen, d0..d4].
    /// hypershift is a fixed wire-format byte (0x00 this phase). Throws if data exceeds 5 bytes —
    /// a truncated binding must never reach the device.</summary>
    public static byte[] BuildSetButtonBuffer(byte transactionId, byte profile, byte buttonId, byte hypershift,
                                              byte category, ReadOnlySpan<byte> data)
    {
        if (data.Length > 5) throw new ArgumentOutOfRangeException(nameof(data));
        Span<byte> args = stackalloc byte[10];
        args[0] = profile; args[1] = buttonId; args[2] = hypershift;
        args[3] = category; args[4] = (byte)data.Length;
        for (int i = 0; i < data.Length; i++) args[5 + i] = data[i];
        return BuildReport(transactionId, DataSizeButton, CommandClassButton, CommandIdSetButton, args);
    }

    /// <summary>GET a button's onboard function. Request carries [profile, buttonId, hypershift] in a
    /// 10-byte zero-padded frame (same data_size as SET); the reply fills category + data.</summary>
    public static byte[] BuildGetButtonBuffer(byte transactionId, byte profile, byte buttonId, byte hypershift)
    {
        Span<byte> args = stackalloc byte[10];
        args[0] = profile; args[1] = buttonId; args[2] = hypershift;
        return BuildReport(transactionId, DataSizeButton, CommandClassButton, CommandIdGetButton, args);
    }

    /// <summary>Validates a get-button reply. Echo check: reply args [0..2] must match the requested
    /// profile/buttonId/hypershift and dataLen must be ≤ 5, else Failed (buttons have no DPI-style
    /// numeric range to validate against).</summary>
    public static ReplyResult ParseButtonReply(byte[] buffer91, byte profile, byte buttonId, byte hypershift,
                                               out byte category, out byte[] data)
    {
        category = 0; data = Array.Empty<byte>();
        var r = ValidateReply(buffer91);
        if (r != ReplyResult.Success) return r;
        if (buffer91[9] != profile || buffer91[10] != buttonId || buffer91[11] != hypershift)
            return ReplyResult.Failed;
        int len = buffer91[13];
        if (len > 5) return ReplyResult.Failed;
        category = buffer91[12];
        data = new byte[len];
        Array.Copy(buffer91, 14, data, 0, len);
        return ReplyResult.Success;
    }

    public static byte[] BuildGetDeviceModeBuffer(byte transactionId)
    {
        Span<byte> args = stackalloc byte[2]; // zeroed
        return BuildReport(transactionId, DataSizeDeviceMode, CommandClassInfo, CommandIdGetDeviceMode, args);
    }

    public static byte[] BuildSetDeviceModeBuffer(byte transactionId, byte mode)
    {
        Span<byte> args = stackalloc byte[2];
        args[0] = mode; // args[1] = 0x00 param
        return BuildReport(transactionId, DataSizeDeviceMode, CommandClassInfo, CommandIdSetDeviceMode, args);
    }

    /// <summary>Decodes a get-device-mode reply; mode is report arg[0] (buffer[9]).</summary>
    public static ReplyResult ParseDeviceModeReply(byte[] buffer91, out byte mode)
    {
        mode = 0;
        var r = ValidateReply(buffer91);
        if (r != ReplyResult.Success) return r;
        mode = buffer91[9];
        return ReplyResult.Success;
    }

    public static byte[] BuildGetProfileListBuffer(byte transactionId)
    {
        Span<byte> args = stackalloc byte[6]; // zeroed
        return BuildReport(transactionId, DataSizeProfileList, CommandClassProfile, CommandIdGetProfileList, args);
    }

    /// <summary>Decodes a profile-list reply: capacity at buffer[9], then the existing (non-zero)
    /// slot numbers. Capacity beyond the 5 the frame can carry is treated as wrong-layout → Failed
    /// (the probe still prints raw hex, so nothing is lost on a surprise).</summary>
    public static ReplyResult ParseProfileListReply(byte[] buffer91, out byte capacity, out byte[] slots)
    {
        capacity = 0; slots = Array.Empty<byte>();
        var r = ValidateReply(buffer91);
        if (r != ReplyResult.Success) return r;
        capacity = buffer91[9];
        if (capacity > 5) return ReplyResult.Failed;
        var list = new List<byte>();
        for (int i = 0; i < capacity; i++)
            if (buffer91[10 + i] != 0) list.Add(buffer91[10 + i]);
        slots = list.ToArray();
        return ReplyResult.Success;
    }

    /// <summary>GET the active profile slot number (hardware-verified 2026-07-18, --probe-profile):
    /// class 0x05, id 0x84, data_size 0x06, six zero args. Reply arg[0] (buffer[9]) is the slot.</summary>
    public static byte[] BuildGetActiveProfileBuffer(byte transactionId)
    {
        Span<byte> args = stackalloc byte[6]; // zeroed
        return BuildReport(transactionId, DataSizeProfileList, CommandClassProfile, CommandIdGetActiveProfile, args);
    }

    /// <summary>Decodes a get-active-profile reply: slot = buffer91[9] (report arg[0]). A decoded
    /// value outside 1..5 is Failed (wrong-layout guard, same idiom as ParseDpiReply/ParseProfileListReply).</summary>
    public static ReplyResult ParseActiveProfileReply(byte[] buffer91, out byte slot)
    {
        slot = 0;
        var r = ValidateReply(buffer91);
        if (r != ReplyResult.Success) return r;
        byte s = buffer91[9];
        if (s < 1 || s > 5) return ReplyResult.Failed;
        slot = s;
        return ReplyResult.Success;
    }

    /// <summary>SET the active profile to an EXISTING onboard slot (1..5). Undocumented command
    /// (class 0x05, id 0x04) — hardware-UNVERIFIED until the --set-test spike passes; probe-only
    /// caller for now. Mirrors BuildNewProfileBuffer/BuildDeleteProfileBuffer's single-arg shape
    /// (data_size DataSizeProfileEdit).</summary>
    public static byte[] BuildSetActiveProfileBuffer(byte transactionId, byte slot) =>
        BuildSetActiveProfileBuffer(transactionId, slot, DataSizeProfileEdit);

    /// <summary>Overload taking an explicit data_size, for the --set-test spike's fallback shape
    /// (ds 0x06, mirroring BuildGetActiveProfileBuffer's frame) when the ds-0x01 shape is rejected.
    /// Undocumented command — hardware-UNVERIFIED until the --set-test spike passes; probe-only
    /// caller for now. Throws unless slot is 1..5 and dataSize is DataSizeProfileEdit (0x01) or
    /// DataSizeProfileList (0x06).</summary>
    public static byte[] BuildSetActiveProfileBuffer(byte transactionId, byte slot, byte dataSize)
    {
        if (slot < 1 || slot > 5) throw new ArgumentOutOfRangeException(nameof(slot));
        if (dataSize != DataSizeProfileEdit && dataSize != DataSizeProfileList)
            throw new ArgumentOutOfRangeException(nameof(dataSize));
        Span<byte> args = stackalloc byte[dataSize]; // zeroed
        args[0] = slot;
        return BuildReport(transactionId, dataSize, CommandClassProfile, CommandIdSetActiveProfile, args);
    }

    public static byte[] BuildNewProfileBuffer(byte transactionId, byte slot)
    {
        Span<byte> args = stackalloc byte[1];
        args[0] = slot;
        return BuildReport(transactionId, DataSizeProfileEdit, CommandClassProfile, CommandIdNewProfile, args);
    }

    public static byte[] BuildDeleteProfileBuffer(byte transactionId, byte slot)
    {
        Span<byte> args = stackalloc byte[1];
        args[0] = slot;
        return BuildReport(transactionId, DataSizeProfileEdit, CommandClassProfile, CommandIdDeleteProfile, args);
    }

    /// <summary>Read-only class-0x05 probe GET (--probe-profile, spec 2026-07-18 §5.3/§7). Throws
    /// unless commandId has the get bit (>= 0x80) — the profile probe must be UNABLE to compose a
    /// write. dataSize/args are caller-specified because candidates carry their own documented shapes.</summary>
    public static byte[] BuildProfileGetProbeBuffer(byte transactionId, byte commandId, byte dataSize, ReadOnlySpan<byte> args)
    {
        if (commandId < 0x80)
            throw new ArgumentOutOfRangeException(nameof(commandId), "get-half ids only (>= 0x80): the profile probe is read-only by construction");
        if (args.Length > 80) throw new ArgumentOutOfRangeException(nameof(args));
        return BuildReport(transactionId, dataSize, CommandClassProfile, commandId, args);
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
