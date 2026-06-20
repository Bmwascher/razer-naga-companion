using System.Drawing;

namespace NagaBatteryTray.Ui;

public static class PopupPlacement
{
    private const int Margin = 8;

    /// <summary>Position (in physical px) for a popup anchored above a tray icon, clamped to the work area.</summary>
    public static Point Compute(Rectangle anchorPx, Rectangle workAreaPx, Size popupPx)
    {
        int x = anchorPx.Right - popupPx.Width;            // right-align to the icon
        int y = anchorPx.Top - popupPx.Height - Margin;    // above the icon

        x = Math.Clamp(x, workAreaPx.Left, Math.Max(workAreaPx.Left, workAreaPx.Right - popupPx.Width));
        if (y < workAreaPx.Top) y = anchorPx.Bottom + Margin; // fall below if no room above
        y = Math.Clamp(y, workAreaPx.Top, Math.Max(workAreaPx.Top, workAreaPx.Bottom - popupPx.Height));
        return new Point(x, y);
    }
}
