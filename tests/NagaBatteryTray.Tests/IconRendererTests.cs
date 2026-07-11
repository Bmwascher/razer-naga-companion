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
    public void Render_coin_leaves_the_canvas_corners_fully_transparent()
    {
        // The coin is a filled circle inscribed in the canvas square; a circle never reaches
        // the square's corners, so they stay empty regardless of level/charging/text.
        var icon = IconRenderer.Render(DeviceState.Online(60, false, false), 96);
        using var bmp = icon.ToBitmap();
        IconRenderer.Destroy(icon);

        Assert.Equal(0, bmp.GetPixel(0, 0).A);
        Assert.Equal(0, bmp.GetPixel(bmp.Width - 1, 0).A);
        Assert.Equal(0, bmp.GetPixel(0, bmp.Height - 1).A);
        Assert.Equal(0, bmp.GetPixel(bmp.Width - 1, bmp.Height - 1).A);
    }

    [Fact]
    public void Render_at_full_charge_has_rim_ring_pixels_and_an_opaque_coin_interior()
    {
        // 100% + not charging draws a full-circle arc, guaranteeing ring pixels on the exact
        // horizontal midline (the arc's leftmost point) — a stable place to probe. The coin
        // fill guarantees the center is opaque too, regardless of what the digit glyph looks
        // like there (e.g. the hole in "0") — the old ring-behind-digits composition had no
        // such guarantee since only digit ink or ring stroke were ever opaque.
        var icon = IconRenderer.Render(DeviceState.Online(100, false, false), 96);
        using var bmp = icon.ToBitmap();
        IconRenderer.Destroy(icon);

        int row = bmp.Height / 2;
        int x = 0;
        while (x < bmp.Width && bmp.GetPixel(x, row).A == 0) x++;
        Assert.True(x < bmp.Width, "expected rim-ring pixels on the full-circle midline");

        int center = bmp.Width / 2;
        Assert.True(bmp.GetPixel(center, row).A > 0, "expected the coin interior to be opaque at the icon center");
    }

    [Fact]
    public void Render_draws_white_digit_ink_inside_the_coin()
    {
        // Digits are always white now (previously level-colored). Scan a central window
        // rather than a single pixel/row, since exact glyph placement shouldn't be load-bearing.
        var icon = IconRenderer.Render(DeviceState.Online(50, false, false), 96);
        using var bmp = icon.ToBitmap();
        IconRenderer.Destroy(icon);

        bool foundWhite = false;
        int cx = bmp.Width / 2;
        int cy = bmp.Height / 2;
        int probe = Math.Max(1, bmp.Width / 4);
        for (int y = Math.Max(0, cy - probe); y <= Math.Min(bmp.Height - 1, cy + probe) && !foundWhite; y++)
        {
            for (int x = Math.Max(0, cx - probe); x <= Math.Min(bmp.Width - 1, cx + probe); x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.A > 0 && p.R >= 200 && p.G >= 200 && p.B >= 200)
                {
                    foundWhite = true;
                    break;
                }
            }
        }
        Assert.True(foundWhite, "expected white digit ink somewhere in the coin's central region");
    }
}
