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
    private static readonly Color CoinFill = Color.FromArgb(170, 16, 18, 22);
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
        Color ringColor = state.Status == DeviceStatus.Unknown
            ? Color.Gray
            : ColorForLevel(state.Percent, state.Charging);

        using var bmp = new Bitmap(render, render);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Coin: a filled dark disc is the whole gauge body (digits + ring both sit on it).
            // Inset ~1.5% so the antialiased circle edge doesn't clip against the canvas bounds.
            float coinMargin = render * 0.015f;
            var coinRect = new RectangleF(coinMargin, coinMargin, render - 2f * coinMargin, render - 2f * coinMargin);
            using (var coinBrush = new SolidBrush(CoinFill))
                g.FillEllipse(coinBrush, coinRect);

            // Ring at the rim: track is a faint full circle; the arc depletes clockwise from 12
            // o'clock and is colored by battery level (green/amber/red, green while charging).
            // Inset by half its own width beyond the coin margin so the ring reads as the coin's
            // rim rather than floating separately from it.
            float ringW = render * 0.14f;
            float ringInset = coinMargin + ringW / 2f;
            var ringRect = new RectangleF(ringInset, ringInset, render - 2f * ringInset, render - 2f * ringInset);
            using (var track = new Pen(Color.FromArgb(45, 255, 255, 255), ringW))
                g.DrawEllipse(track, ringRect);
            int pct = state.Status == DeviceStatus.Unknown ? 0 : Math.Clamp(state.Percent, 0, 100);
            if (pct > 0)
            {
                using var arc = new Pen(ringColor, ringW) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, ringRect, -90f, 360f * pct / 100f);
            }

            // Digits sit INSIDE the coin, always white. Lay them out as a path so we can size by
            // true ink bounds (digits have no descenders, so MeasureString would leave dead
            // vertical space). Target ~52% of render tall; cap width to the coin's interior
            // (inside the ring) so 3-digit "100" condenses horizontally instead of overflowing
            // into the ring — same condense-when-too-wide approach as before, tighter box.
            using var fmt = new StringFormat(StringFormat.GenericTypographic);
            using var path = new GraphicsPath();
            path.AddString(text, Family, (int)FontStyle.Bold, 100f, PointF.Empty, fmt);
            var ink = path.GetBounds();
            if (ink.Width > 0 && ink.Height > 0)
            {
                float pad = render * 0.03f;
                // 3-digit "100" gets a shorter target so its condensed width still clears the
                // rim (the reference drops 56% -> 42% for 3 digits; same idea).
                float targetHeight = render * (text.Length >= 3 ? 0.44f : 0.56f);
                float maxWidth = render - 2f * ringW - 2f * pad;

                float scaleX, scaleY;
                if (state.Status == DeviceStatus.Unknown)
                {
                    // The "-" glyph's ink is far wider than tall; the fill-height rule below
                    // would stretch the thin hyphen into a slab filling the coin. Scale it
                    // uniformly by width instead: target ink width ~35% of render.
                    scaleX = scaleY = render * 0.35f / ink.Width;
                }
                else
                {
                    scaleY = targetHeight / ink.Height;                   // fill the target height
                    scaleX = ink.Width * scaleY > maxWidth                // overflow horizontally?
                        ? maxWidth / ink.Width                            //   compress width to fit
                        : scaleY;                                         //   else stay uniform (no stretch)
                }

                float offX = (render - ink.Width * scaleX) / 2f - ink.Left * scaleX;
                float offY = (render - ink.Height * scaleY) / 2f - ink.Top * scaleY;

                using var m = new Matrix(scaleX, 0f, 0f, scaleY, offX, offY);
                path.Transform(m);

                using var brush = new SolidBrush(Color.White);
                g.FillPath(brush, path);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
