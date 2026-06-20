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
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            string text = state.Status == DeviceStatus.Unknown ? "-" : state.Percent.ToString();
            Color color = state.Status == DeviceStatus.Unknown
                ? Color.Gray
                : ColorForLevel(state.Percent, state.Charging);

            float emSize = text.Length >= 3 ? size * 0.5f : size * 0.72f;
            using var font = new Font("Segoe UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, brush, new RectangleF(0, 0, size, size), fmt);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
