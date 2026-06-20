using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using NagaBatteryTray.Monitoring;

namespace NagaBatteryTray.Ui;

public static class IconRenderer
{
    private static readonly Color Green = Color.FromArgb(0x44, 0xD6, 0x2C);
    private static readonly Color Amber = Color.FromArgb(0xE0, 0xA2, 0x3E);
    private static readonly Color Red = Color.FromArgb(0xE0, 0x47, 0x3E);

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
        int render = size * 2;                  // supersample 2x; Windows downscales -> crisper small digits
        string text = state.Status == DeviceStatus.Unknown ? "-" : state.Percent.ToString();
        Color color = state.Status == DeviceStatus.Unknown
            ? Color.Gray
            : ColorForLevel(state.Percent, state.Charging);

        using var bmp = new Bitmap(render, render);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var fmt = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            using var font = new Font("Segoe UI", FitEm(g, text, fmt, render - 2f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, new RectangleF(0, 0, render, render), fmt);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Largest bold Segoe UI em size whose rendered text fits within <paramref name="max"/> px square.</summary>
    private static float FitEm(Graphics g, string text, StringFormat fmt, float max)
    {
        for (float em = max; em > 4f; em -= 0.5f)
        {
            using var f = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, f, new PointF(0, 0), fmt);
            if (sz.Width <= max && sz.Height <= max) return em;
        }
        return 5f;
    }
}
