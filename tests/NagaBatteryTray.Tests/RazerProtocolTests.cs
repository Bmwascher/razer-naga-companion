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
}
