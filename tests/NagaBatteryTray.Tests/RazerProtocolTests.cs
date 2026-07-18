using NagaBatteryTray.Hid;
using Xunit;

public class RazerProtocolTests
{
    [Fact]
    public void Battery_query_buffer_has_correct_layout_and_crc()
    {
        byte[] buf = RazerProtocol.BuildFeatureBuffer(0x1f, RazerProtocol.CommandIdBattery);

        Assert.Equal(91, buf.Length);
        Assert.Equal(0x00, buf[0]);  // report id
        Assert.Equal(0x00, buf[1]);  // status (report[0])
        Assert.Equal(0x1f, buf[2]);  // transaction_id (report[1])
        Assert.Equal(0x02, buf[6]);  // data_size (report[5])
        Assert.Equal(0x07, buf[7]);  // command_class (report[6])
        Assert.Equal(0x80, buf[8]);  // command_id (report[7])
        Assert.Equal(0x85, buf[89]); // crc (report[88])
        Assert.Equal(0x00, buf[90]); // reserved (report[89])
    }

    [Fact]
    public void Charging_query_crc_is_0x81()
    {
        byte[] buf = RazerProtocol.BuildFeatureBuffer(0x1f, RazerProtocol.CommandIdCharging);
        Assert.Equal(0x84, buf[8]);   // command_id
        Assert.Equal(0x81, buf[89]);  // crc
    }

    [Fact]
    public void Crc_is_xor_of_bytes_2_to_87()
    {
        var report = new byte[90];
        report[5] = 0x02; report[6] = 0x07; report[7] = 0x80;
        Assert.Equal((byte)0x85, RazerProtocol.ComputeCrc(report));
    }

    private static byte[] MakeReply(byte status, byte value)
    {
        var buf = new byte[91];
        buf[1] = status;        // report[0]
        buf[2] = 0x1f;          // transaction_id
        buf[6] = 0x02;          // data_size
        buf[7] = 0x07;          // class
        buf[8] = 0x80;          // id
        buf[10] = value;        // report[9] data byte
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i]; // reply crc over buffer[3..88]
        buf[89] = crc;
        return buf;
    }

    [Fact]
    public void ParseReply_success_returns_value()
    {
        var reply = MakeReply(0x02, 220);
        var result = RazerProtocol.ParseReply(reply, out byte value);
        Assert.Equal(ReplyResult.Success, result);
        Assert.Equal((byte)220, value);
    }

    [Fact]
    public void ParseReply_busy_status_is_busy()
    {
        Assert.Equal(ReplyResult.Busy, RazerProtocol.ParseReply(MakeReply(0x01, 0), out _));
    }

    [Fact]
    public void ParseReply_bad_crc_is_failed()
    {
        var reply = MakeReply(0x02, 100);
        reply[89] ^= 0xFF; // corrupt crc
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseReply(reply, out _));
    }

    [Theory]
    [InlineData(255, 100)]
    [InlineData(0, 0)]
    [InlineData(220, 86)] // 220*100/255 = 86.27 -> 86
    public void ScaleBattery_maps_0_255_to_percent(byte raw, int expected)
    {
        Assert.Equal(expected, RazerProtocol.ScaleBattery(raw));
    }

    private static byte[] MakeDpiReply(byte status, int x, int y)
    {
        var buf = new byte[91];
        buf[1] = status;          // report[0]
        buf[2] = 0x1f;            // transaction_id
        buf[6] = 0x07;            // data_size
        buf[7] = 0x04;            // command_class
        buf[8] = 0x85;            // command_id (GET DPI)
        buf[9] = 0x00;            // arg[0] varstore echo
        buf[10] = (byte)(x >> 8); buf[11] = (byte)x;   // X big-endian
        buf[12] = (byte)(y >> 8); buf[13] = (byte)y;   // Y big-endian
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        buf[89] = crc;
        return buf;
    }

    [Fact]
    public void GetDpi_buffer_has_correct_layout_and_crc()
    {
        byte[] buf = RazerProtocol.BuildGetDpiBuffer(0x1f);
        Assert.Equal(91, buf.Length);
        Assert.Equal(0x1f, buf[2]);  // transaction_id
        Assert.Equal(0x07, buf[6]);  // data_size
        Assert.Equal(0x04, buf[7]);  // command_class
        Assert.Equal(0x85, buf[8]);  // command_id (GET)
        Assert.Equal(0x00, buf[9]);  // arg[0] NOSTORE
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        Assert.Equal(crc, buf[89]);
    }

    [Fact]
    public void SetDpi_buffer_encodes_xy_big_endian_with_varstore()
    {
        byte[] buf = RazerProtocol.BuildSetDpiBuffer(0x1f, 1600, 1600); // 1600 = 0x0640
        Assert.Equal(0x07, buf[6]);  // data_size
        Assert.Equal(0x04, buf[7]);  // command_class
        Assert.Equal(0x05, buf[8]);  // command_id (SET)
        Assert.Equal(0x01, buf[9]);  // VARSTORE
        Assert.Equal(0x06, buf[10]); Assert.Equal(0x40, buf[11]); // X
        Assert.Equal(0x06, buf[12]); Assert.Equal(0x40, buf[13]); // Y
        Assert.Equal(0x00, buf[14]); Assert.Equal(0x00, buf[15]);
    }

    [Fact]
    public void SetDpi_buffer_clamps_to_100_30000()
    {
        byte[] lo = RazerProtocol.BuildSetDpiBuffer(0x1f, 50, 50);    // -> 100 = 0x0064
        Assert.Equal(0x00, lo[10]); Assert.Equal(0x64, lo[11]);
        byte[] hi = RazerProtocol.BuildSetDpiBuffer(0x1f, 99999, 99999); // -> 30000 = 0x7530
        Assert.Equal(0x75, hi[10]); Assert.Equal(0x30, hi[11]);
    }

    [Fact]
    public void ParseDpiReply_success_decodes_xy()
    {
        var r = RazerProtocol.ParseDpiReply(MakeDpiReply(0x02, 1600, 800), out int x, out int y);
        Assert.Equal(ReplyResult.Success, r);
        Assert.Equal(1600, x);
        Assert.Equal(800, y);
    }

    [Fact]
    public void ParseDpiReply_busy_and_bad_crc_and_out_of_range_fail()
    {
        Assert.Equal(ReplyResult.Busy, RazerProtocol.ParseDpiReply(MakeDpiReply(0x01, 1600, 1600), out _, out _));
        var bad = MakeDpiReply(0x02, 1600, 1600); bad[89] ^= 0xFF;
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseDpiReply(bad, out _, out _));
        // valid CRC but decoded X below DpiMin -> Failed (guards against wrong-layout firmware)
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseDpiReply(MakeDpiReply(0x02, 50, 1600), out _, out _));
    }

    // --- Phase B: button remap (Basilisk V3 oracle vectors; spec §5.1/§6) ---

    [Fact]
    public void SetButton_reproduces_basilisk_ctrl_c_vector()
    {
        // tilt-wheel-left (0x34) -> Ctrl+C on profile 1: args = 01 34 00 02 02 01 06 00 00 00
        byte[] buf = RazerProtocol.BuildSetButtonBuffer(0x1f, 0x01, 0x34, 0x00,
            RazerProtocol.FnKeyboard, new byte[] { 0x01, 0x06 });
        Assert.Equal(91, buf.Length);
        Assert.Equal(0x1f, buf[2]);  // transaction_id
        Assert.Equal(0x0a, buf[6]);  // data_size
        Assert.Equal(0x02, buf[7]);  // command_class
        Assert.Equal(0x0c, buf[8]);  // command_id (SET)
        byte[] expectedArgs = { 0x01, 0x34, 0x00, 0x02, 0x02, 0x01, 0x06, 0x00, 0x00, 0x00 };
        for (int i = 0; i < 10; i++) Assert.Equal(expectedArgs[i], buf[9 + i]);
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        Assert.Equal(crc, buf[89]);
    }

    [Fact]
    public void SetButton_reproduces_basilisk_ctrl_v_vector()
    {
        // tilt-wheel-right (0x35) -> Ctrl+V on profile 1: args = 01 35 00 02 02 01 19 00 00 00
        byte[] buf = RazerProtocol.BuildSetButtonBuffer(0x1f, 0x01, 0x35, 0x00,
            RazerProtocol.FnKeyboard, new byte[] { 0x01, 0x19 });
        byte[] expectedArgs = { 0x01, 0x35, 0x00, 0x02, 0x02, 0x01, 0x19, 0x00, 0x00, 0x00 };
        for (int i = 0; i < 10; i++) Assert.Equal(expectedArgs[i], buf[9 + i]);
    }

    [Fact]
    public void SetButton_disabled_has_zero_length_data()
    {
        byte[] buf = RazerProtocol.BuildSetButtonBuffer(0x1f, RazerProtocol.ButtonProfileDirect,
            0x03, 0x00, RazerProtocol.FnDisabled, ReadOnlySpan<byte>.Empty);
        Assert.Equal(0x00, buf[12]); // category = disabled
        Assert.Equal(0x00, buf[13]); // dataLen = 0
        for (int i = 14; i <= 18; i++) Assert.Equal(0x00, buf[i]);
    }

    [Fact]
    public void SetButton_throws_on_data_longer_than_5()
    {
        // a truncated binding must never reach the device
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RazerProtocol.BuildSetButtonBuffer(0x1f, 0x00, 0x03, 0x00, RazerProtocol.FnKeyboard, new byte[6]));
    }

    private static byte[] MakeButtonReply(byte status, byte profile, byte buttonId, byte hypershift,
                                          byte category, byte[] data)
    {
        var buf = new byte[91];
        buf[1] = status;              // report[0]
        buf[2] = 0x1f;                // transaction_id
        buf[6] = 0x0a;                // data_size
        buf[7] = 0x02;                // command_class
        buf[8] = 0x8c;                // command_id (GET)
        buf[9] = profile; buf[10] = buttonId; buf[11] = hypershift; // echoed request args
        buf[12] = category; buf[13] = (byte)data.Length;
        for (int i = 0; i < data.Length; i++) buf[14 + i] = data[i];
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i]; // reply crc over buffer[3..88]
        buf[89] = crc;
        return buf;
    }

    [Fact]
    public void GetButton_buffer_has_correct_layout_and_crc()
    {
        byte[] buf = RazerProtocol.BuildGetButtonBuffer(0x1f, 0x01, 0x34, 0x00);
        Assert.Equal(0x0a, buf[6]);  // data_size (same 10-byte frame as SET)
        Assert.Equal(0x02, buf[7]);  // command_class
        Assert.Equal(0x8c, buf[8]);  // command_id (GET)
        Assert.Equal(0x01, buf[9]);  // profile
        Assert.Equal(0x34, buf[10]); // buttonId
        Assert.Equal(0x00, buf[11]); // hypershift
        for (int i = 12; i <= 18; i++) Assert.Equal(0x00, buf[i]); // zero-padded
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        Assert.Equal(crc, buf[89]);
    }

    [Fact]
    public void ParseButtonReply_success_decodes_category_and_data()
    {
        var reply = MakeButtonReply(0x02, 0x00, 0x34, 0x00, 0x02, new byte[] { 0x01, 0x06 });
        var r = RazerProtocol.ParseButtonReply(reply, 0x00, 0x34, 0x00, out byte category, out byte[] data);
        Assert.Equal(ReplyResult.Success, r);
        Assert.Equal(0x02, category);
        Assert.Equal(new byte[] { 0x01, 0x06 }, data);
    }

    [Fact]
    public void ParseButtonReply_busy_is_busy()
    {
        var reply = MakeButtonReply(0x01, 0x00, 0x34, 0x00, 0x02, new byte[] { 0x01, 0x06 });
        Assert.Equal(ReplyResult.Busy, RazerProtocol.ParseButtonReply(reply, 0x00, 0x34, 0x00, out _, out _));
    }

    [Fact]
    public void ParseButtonReply_echo_mismatch_is_failed()
    {
        // reply echoes buttonId 0x35 but we asked about 0x34 -> wrong-layout guard trips
        var reply = MakeButtonReply(0x02, 0x00, 0x35, 0x00, 0x02, new byte[] { 0x01, 0x06 });
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseButtonReply(reply, 0x00, 0x34, 0x00, out _, out _));
    }

    [Fact]
    public void ParseButtonReply_datalen_over_5_is_failed()
    {
        var reply = MakeButtonReply(0x02, 0x00, 0x34, 0x00, 0x02, new byte[] { 0x01, 0x06 });
        reply[13] = 6;                                    // corrupt dataLen
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= reply[i];    // re-seal crc so only the guard trips
        reply[89] = crc;
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseButtonReply(reply, 0x00, 0x34, 0x00, out _, out _));
    }

    [Fact]
    public void ParseButtonReply_bad_crc_is_failed()
    {
        var reply = MakeButtonReply(0x02, 0x00, 0x34, 0x00, 0x02, new byte[] { 0x01, 0x06 });
        reply[89] ^= 0xFF;
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseButtonReply(reply, 0x00, 0x34, 0x00, out _, out _));
    }

    [Fact]
    public void DeviceMode_get_and_set_buffers_have_correct_layout()
    {
        byte[] get = RazerProtocol.BuildGetDeviceModeBuffer(0x1f);
        Assert.Equal(0x02, get[6]); // data_size
        Assert.Equal(0x00, get[7]); // class (info)
        Assert.Equal(0x84, get[8]); // id (GET)

        byte[] set = RazerProtocol.BuildSetDeviceModeBuffer(0x1f, RazerProtocol.DeviceModeNormal);
        Assert.Equal(0x02, set[6]);
        Assert.Equal(0x00, set[7]);
        Assert.Equal(0x04, set[8]); // id (SET)
        Assert.Equal(0x00, set[9]);  // mode
        Assert.Equal(0x00, set[10]); // param
    }

    [Fact]
    public void ParseDeviceModeReply_decodes_mode()
    {
        var buf = new byte[91];
        buf[1] = 0x02; buf[2] = 0x1f; buf[6] = 0x02; buf[7] = 0x00; buf[8] = 0x84;
        buf[9] = 0x03; // driver mode
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        buf[89] = crc;
        Assert.Equal(ReplyResult.Success, RazerProtocol.ParseDeviceModeReply(buf, out byte mode));
        Assert.Equal(RazerProtocol.DeviceModeDriver, mode);
    }

    [Fact]
    public void ProfileList_buffer_and_parse_roundtrip()
    {
        byte[] req = RazerProtocol.BuildGetProfileListBuffer(0x1f);
        Assert.Equal(0x06, req[6]); // data_size = 1 capacity byte + up to 5 slot bytes
        Assert.Equal(0x05, req[7]); // class (profile)
        Assert.Equal(0x81, req[8]); // id (list)

        var reply = new byte[91];
        reply[1] = 0x02; reply[2] = 0x1f; reply[6] = 0x06; reply[7] = 0x05; reply[8] = 0x81;
        reply[9] = 5;                     // capacity
        reply[10] = 1; reply[11] = 3;     // slots 1 and 3 exist; rest zero
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= reply[i];
        reply[89] = crc;
        Assert.Equal(ReplyResult.Success, RazerProtocol.ParseProfileListReply(reply, out byte cap, out byte[] slots));
        Assert.Equal(5, cap);
        Assert.Equal(new byte[] { 1, 3 }, slots);
    }

    [Fact]
    public void ParseProfileListReply_capacity_over_5_is_failed()
    {
        var reply = new byte[91];
        reply[1] = 0x02; reply[2] = 0x1f; reply[6] = 0x06; reply[7] = 0x05; reply[8] = 0x81;
        reply[9] = 200; // nonsense capacity -> wrong-layout guard
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= reply[i];
        reply[89] = crc;
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseProfileListReply(reply, out _, out _));
    }

    [Fact]
    public void Profile_new_and_delete_buffers_have_correct_layout()
    {
        byte[] create = RazerProtocol.BuildNewProfileBuffer(0x1f, 0x01);
        Assert.Equal(0x01, create[6]); // data_size
        Assert.Equal(0x05, create[7]); // class
        Assert.Equal(0x02, create[8]); // id (new)
        Assert.Equal(0x01, create[9]); // slot

        byte[] del = RazerProtocol.BuildDeleteProfileBuffer(0x1f, 0x01);
        Assert.Equal(0x03, del[8]);    // id (delete)
        Assert.Equal(0x01, del[9]);    // slot
    }

    [Fact]
    public void ProfileGetProbe_places_class_id_size_args_and_crc()
    {
        var buf = RazerProtocol.BuildProfileGetProbeBuffer(0x1f, 0x84, 0x06, new byte[] { 0x01, 0x02 });
        Assert.Equal(91, buf.Length);
        Assert.Equal(0x00, buf[0]); // report id
        Assert.Equal(0x1f, buf[2]); // tid at report[1]
        Assert.Equal(0x06, buf[6]); // data_size at report[5]
        Assert.Equal(0x05, buf[7]); // class at report[6]
        Assert.Equal(0x84, buf[8]); // command id at report[7]
        Assert.Equal(0x01, buf[9]); // args at report[8..]
        Assert.Equal(0x02, buf[10]);
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i]; // XOR over report[2..87]
        Assert.Equal(crc, buf[89]);
    }

    [Fact]
    public void ProfileGetProbe_throws_on_set_half_command_ids()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RazerProtocol.BuildProfileGetProbeBuffer(0x1f, 0x02, 0x01, new byte[] { 0x01 }));
    }

    // --- active-profile get (hardware-verified 2026-07-18) + set spike (undocumented, unverified) ---

    private static byte[] MakeActiveProfileReply(byte status, byte slot)
    {
        var buf = new byte[91];
        buf[1] = status;      // report[0]
        buf[2] = 0x1f;        // transaction_id
        buf[6] = 0x06;        // data_size
        buf[7] = 0x05;        // command_class
        buf[8] = 0x84;        // command_id (GET active profile)
        buf[9] = slot;        // arg[0] active slot
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        buf[89] = crc;
        return buf;
    }

    [Fact]
    public void GetActiveProfile_buffer_has_correct_layout_and_crc()
    {
        byte[] buf = RazerProtocol.BuildGetActiveProfileBuffer(0x1f);
        Assert.Equal(0x06, buf[6]); // data_size
        Assert.Equal(0x05, buf[7]); // command_class
        Assert.Equal(0x84, buf[8]); // command_id (GET)
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        Assert.Equal(crc, buf[89]);
    }

    [Fact]
    public void ParseActiveProfileReply_success_decodes_slot()
    {
        var r = RazerProtocol.ParseActiveProfileReply(MakeActiveProfileReply(0x02, 3), out byte slot);
        Assert.Equal(ReplyResult.Success, r);
        Assert.Equal((byte)3, slot);
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x06)]
    public void ParseActiveProfileReply_slot_out_of_range_is_failed(byte slot)
    {
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseActiveProfileReply(MakeActiveProfileReply(0x02, slot), out _));
    }

    [Fact]
    public void ParseActiveProfileReply_set_echo_is_failed()
    {
        // an accepted SET's reply echoes class 0x05 / id 0x04 (not the GET id 0x84) at the same
        // arg[0]=slot offset — must not be misread as a successful read-back (echo-check guard)
        var buf = new byte[91];
        buf[1] = 0x02;  // status accepted
        buf[2] = 0x1f;  // transaction_id
        buf[6] = 0x01;  // data_size (ds01 SET shape)
        buf[7] = 0x05;  // command_class (profile)
        buf[8] = 0x04;  // command_id (SET, not GET 0x84)
        buf[9] = 3;     // arg[0] echoed slot
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        buf[89] = crc;
        Assert.Equal(ReplyResult.Failed, RazerProtocol.ParseActiveProfileReply(buf, out _));
    }

    [Fact]
    public void SetActiveProfile_buffer_has_correct_layout_and_crc()
    {
        byte[] buf = RazerProtocol.BuildSetActiveProfileBuffer(0x1f, 0x03);
        Assert.Equal(0x01, buf[6]); // data_size
        Assert.Equal(0x05, buf[7]); // command_class
        Assert.Equal(0x04, buf[8]); // command_id (SET, undocumented)
        Assert.Equal(0x03, buf[9]); // slot
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        Assert.Equal(crc, buf[89]);
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x06)]
    public void SetActiveProfile_throws_on_out_of_range_slot(byte slot)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerProtocol.BuildSetActiveProfileBuffer(0x1f, slot));
    }

    [Fact]
    public void SetActiveProfile_ds06_overload_uses_six_byte_frame()
    {
        // fallback shape the --set-test spike tries when ds 0x01 is rejected
        byte[] buf = RazerProtocol.BuildSetActiveProfileBuffer(0x1f, 0x02, RazerProtocol.DataSizeProfileList);
        Assert.Equal(0x06, buf[6]);  // data_size
        Assert.Equal(0x05, buf[7]);  // command_class
        Assert.Equal(0x04, buf[8]);  // command_id (SET)
        Assert.Equal(0x02, buf[9]);  // slot
        Assert.Equal(0x00, buf[10]); // remaining args zero
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        Assert.Equal(crc, buf[89]);
    }

    [Fact]
    public void SetActiveProfile_ds06_overload_throws_on_unrecognized_data_size()
    {
        // only the ds01 (single-slot-byte) and ds06 (mirroring get-active-profile) shapes are wired;
        // anything else must never reach the device
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RazerProtocol.BuildSetActiveProfileBuffer(0x1f, 0x02, 0x0a));
    }
}
