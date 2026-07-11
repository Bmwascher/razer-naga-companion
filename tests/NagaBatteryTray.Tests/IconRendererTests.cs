using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Ui;
using Xunit;

public class IconRendererTests
{
    [Theory]
    [InlineData(96, 100, false)]   // 16 px
    [InlineData(120, 87, false)]   // 20 px
    [InlineData(144, 38, true)]    // 24 px, charging
    [InlineData(192, 12, false)]   // 32 px, low
    public void Render_with_ring_smokes_at_all_sizes(int dpi, int percent, bool charging)
    {
        var icon = IconRenderer.Render(DeviceState.Online(percent, charging, false), dpi);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
        IconRenderer.Destroy(icon);
    }

    [Fact]
    public void Render_unknown_state_smokes()
    {
        var icon = IconRenderer.Render(DeviceState.Unknown, 96);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
        IconRenderer.Destroy(icon);
    }

    [Fact]
    public void Render_at_full_charge_leaves_a_transparent_halo_between_ring_and_digits()
    {
        // 100% + not charging draws a full-circle arc, guaranteeing ring pixels on the exact
        // horizontal midline (the arc's leftmost point) — a stable place to probe for the gap.
        var icon = IconRenderer.Render(DeviceState.Online(100, false, false), 96);
        using var bmp = icon.ToBitmap();
        IconRenderer.Destroy(icon);

        int row = bmp.Height / 2;
        int x = 0;
        while (x < bmp.Width && bmp.GetPixel(x, row).A == 0) x++;
        Assert.True(x < bmp.Width, "expected ring pixels on the full-circle midline");

        while (x < bmp.Width && bmp.GetPixel(x, row).A > 0) x++; // walk through the ring's left arc
        int gapStart = x;
        Assert.True(gapStart < bmp.Width, "expected the ring arc to end before the digit ink");

        while (x < bmp.Width && bmp.GetPixel(x, row).A == 0) x++; // walk the transparent halo
        int digitStart = x;

        Assert.True(digitStart > gapStart, "expected a fully transparent halo between the ring and the digit ink");
        Assert.True(digitStart < bmp.Width, "expected digit ink after the halo");
    }
}
