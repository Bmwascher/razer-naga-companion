using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using NagaBatteryTray.Monitoring;

namespace NagaBatteryTray.Ui;

public static class IconRenderer
{
    private static readonly Color Green = Color.FromArgb(0x44, 0xD6, 0x2C);
    private static readonly Color Amber = Color.FromArgb(0xE0, 0xA2, 0x3E);
    private static readonly Color Red = Color.FromArgb(0xE0, 0x47, 0x3E);
    private static readonly FontFamily Family = ResolveFamily();

    private static FontFamily ResolveFamily()
    {
        try { return new FontFamily("Segoe UI"); }
        catch { return FontFamily.GenericSansSerif; }
    }

    public static Color ColorForLevel(int percent, bool charging)
    {
        if (charging) return Green;
        if (percent <= 20) return Red;
        if (percent <= 50) return Amber;
        return Green;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static void Destroy(Icon? icon)
    {
        if (icon is not null) DestroyIcon(icon.Handle);
    }

    public static Icon Render(DeviceState state, int dpi)
    {
        int size = Math.Max(16, dpi * 16 / 96); // SM_CXSMICON scales with DPI
        int render = size * 2;                  // supersample 2x; Windows downscales -> crisper digits
        string text = state.Status == DeviceStatus.Unknown ? "-" : state.Percent.ToString();
        Color color = state.Status == DeviceStatus.Unknown
            ? Color.Gray
            : ColorForLevel(state.Percent, state.Charging);

        using var bmp = new Bitmap(render, render);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Battery ring, drawn BEHIND the digits (digits-win rule: digit layout is untouched).
            // Depletes clockwise from 12 o'clock; track is a faint full circle. ~1 px at 16 px tray size.
            float ringW = Math.Max(2f, render * 0.07f);
            var ringRect = new RectangleF(ringW / 2f, ringW / 2f, render - ringW, render - ringW);
            using (var track = new Pen(Color.FromArgb(45, 255, 255, 255), ringW))
                g.DrawEllipse(track, ringRect);
            int pct = state.Status == DeviceStatus.Unknown ? 0 : Math.Clamp(state.Percent, 0, 100);
            if (pct > 0)
            {
                if (state.Status != DeviceStatus.Unknown && state.Charging)
                    using (var glow = new Pen(Color.FromArgb(70, color), ringW * 2f))
                        g.DrawArc(glow, ringRect, -90f, 360f * pct / 100f);
                using var arc = new Pen(color, ringW) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, ringRect, -90f, 360f * pct / 100f);
            }

            // Lay the digits out as a path so we can size them by their true ink bounds. Digits have no
            // descenders, so MeasureString leaves dead vertical space; the path's bounds don't. Fill the
            // icon HEIGHT, then compress horizontally only when too wide — this keeps 3-digit "100" tall
            // and legible instead of width-shrinking it into a tiny strip (the old FitEm behaviour).
            using var fmt = new StringFormat(StringFormat.GenericTypographic);
            using var path = new GraphicsPath();
            path.AddString(text, Family, (int)FontStyle.Bold, 100f, PointF.Empty, fmt);
            var ink = path.GetBounds();
            if (ink.Width > 0 && ink.Height > 0)
            {
                float margin = render * 0.04f; // hug the icon edges so the digits read as large as possible
                float box = render - 2f * margin;

                float scaleY = box / ink.Height;                          // fill the height
                float scaleX = ink.Width * scaleY > box                   // overflow horizontally?
                    ? box / ink.Width                                     //   compress width to fit
                    : scaleY;                                             //   else stay uniform (no stretch)

                float offX = (render - ink.Width * scaleX) / 2f - ink.Left * scaleX;
                float offY = (render - ink.Height * scaleY) / 2f - ink.Top * scaleY;

                using var m = new Matrix(scaleX, 0f, 0f, scaleY, offX, offY);
                path.Transform(m);

                // Halo knockout: erase a transparent clearance band around the digit ink so the ring
                // passes visibly BEHIND the digits with a clean gap, instead of antialiasing smearing
                // the arc into the digit strokes at small tray sizes. Round joins/caps avoid spike
                // artifacts on the digits' sharp corners.
                float haloWidth = ringW * 2f;
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var knockout = new Pen(Color.Transparent, haloWidth)
                       { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawPath(knockout, path);
                g.CompositingMode = CompositingMode.SourceOver;

                using var brush = new SolidBrush(color);
                g.FillPath(brush, path);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
