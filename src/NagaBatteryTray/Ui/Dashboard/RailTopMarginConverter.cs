using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NagaBatteryTray.Ui.Dashboard;

/// <summary>Pins the right rail's top edge to the chip columns' top edge (dashboard-polish §6):
/// converts (column ActualHeight, chips ActualHeight) into a top margin of (H − h) / 2 — the
/// offset a centered chips column sits at. The rail itself stays Top-aligned and unconstrained,
/// so content taller than the chips band grows downward instead of being layout-clipped (the
/// first cut of §6 bound the rail wrapper's Height, and WPF clips a child arranged smaller than
/// its desired size — the Profile card's body vanished).</summary>
public sealed class RailTopMarginConverter : IMultiValueConverter
{
    /// <summary>Left inset the rail always carries (was the wrapper's Margin="8,0,0,0").</summary>
    public double Left { get; set; } = 8;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double columnH = values is [double c, ..] ? c : 0;
        double chipsH = values is [_, double k, ..] ? k : 0;
        return new Thickness(Left, Math.Max(0, (columnH - chipsH) / 2), 0, 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
