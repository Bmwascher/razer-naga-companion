using System.Drawing;
using NagaBatteryTray.Ui;
using Xunit;

public class PopupPlacementTests
{
    private static readonly Rectangle WorkArea = new(0, 0, 1920, 1040); // taskbar 40px at bottom

    [Fact]
    public void Places_above_the_tray_anchor()
    {
        var anchor = new Rectangle(1850, 1042, 24, 24); // tray icon near bottom-right
        var pos = PopupPlacement.Compute(anchor, WorkArea, new Size(260, 180));
        Assert.True(pos.Y + 180 <= WorkArea.Bottom); // fully above the taskbar
        Assert.True(pos.X + 260 <= WorkArea.Right);  // not off the right edge
        Assert.True(pos.X >= WorkArea.Left);
    }

    [Fact]
    public void Clamps_to_left_edge_when_anchor_is_far_left()
    {
        var anchor = new Rectangle(2, 1042, 24, 24);
        var pos = PopupPlacement.Compute(anchor, WorkArea, new Size(260, 180));
        Assert.Equal(WorkArea.Left, pos.X);
    }
}
