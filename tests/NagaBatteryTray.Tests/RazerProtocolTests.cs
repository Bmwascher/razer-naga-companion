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
}
