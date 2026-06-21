using System.Globalization;
using System.Windows.Data;

namespace NagaBatteryTray.Ui;

/// <summary>Bridges WPF-UI NumberBox (double?) / Slider (double) to int view-model properties.
/// A null/blank entry is ignored (Binding.DoNothing) so a transient empty box never zeroes the value.</summary>
public sealed class DoubleIntConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i ? (double)i : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? (int)Math.Round(d) : System.Windows.Data.Binding.DoNothing;
}
