using System.Globalization;
using System.Windows;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public class GridKeyMarginConverterTests
{
    private static Thickness For(int position) =>
        (Thickness)new GridKeyMarginConverter().Convert(position, typeof(Thickness), null!, CultureInfo.InvariantCulture);

    [Fact]
    public void Unnudged_key_gets_the_base_margin()
    {
        var t = For(8);
        Assert.Equal(2.15, t.Left, 3);
        Assert.Equal(5.75, t.Top, 3);
        Assert.Equal(2.15, t.Right, 3);
        Assert.Equal(5.75, t.Bottom, 3);
    }

    [Fact]
    public void Nudge_shifts_the_key_by_moving_margin_between_opposite_sides()
    {
        var t = For(1); // calibrated: left 2, down 3
        Assert.Equal(2.15 - 2, t.Left, 3);
        Assert.Equal(5.75 + 3, t.Top, 3);
        Assert.Equal(2.15 + 2, t.Right, 3);
        Assert.Equal(5.75 - 3, t.Bottom, 3);
    }

    [Fact]
    public void Every_position_presents_the_same_total_margin_so_grid_layout_never_shifts()
    {
        for (int pos = 1; pos <= 12; pos++)
        {
            var t = For(pos);
            Assert.Equal(2.15 * 2, t.Left + t.Right, 3);
            Assert.Equal(5.75 * 2, t.Top + t.Bottom, 3);
        }
    }
}
