using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NagaBatteryTray.Ui.Dashboard;

/// <summary>Per-key margin for the stage's 12 grid keys: the shared base margin plus a
/// hand-tuned (dx, dy) nudge aligning each hit ring to its keycap in the render (the caps
/// aren't on a perfectly uniform pitch). Opposite sides compensate, so every cell presents
/// the same total margin to the UniformGrid and only the child shifts — layout never moves.</summary>
public sealed class GridKeyMarginConverter : IValueConverter
{
    private const double BaseX = 2.15, BaseY = 5.75;

    /// <summary>position (1-12) -> (dx right+, dy down+), user-calibrated on the live render.</summary>
    internal static (double Dx, double Dy) NudgeFor(int position) => position switch
    {
        1 or 2 => (-2, 3),
        3 => (-2, 2),
        4 => (-1, 2),
        5 => (0, 2),
        6 => (0, 1),
        7 => (-1, 1),
        9 => (1, 0),
        12 => (0, -1),
        _ => (0, 0),
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var (dx, dy) = NudgeFor((int)value);
        return new Thickness(BaseX + dx, BaseY + dy, BaseX - dx, BaseY - dy);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
