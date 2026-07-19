using System.Globalization;
using System.Windows;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public class RailTopMarginConverterTests
{
    private static Thickness Convert(params object[] values) =>
        (Thickness)new RailTopMarginConverter().Convert(values, typeof(Thickness), null!, CultureInfo.InvariantCulture);

    [Fact]
    public void Top_is_half_the_height_difference_where_the_centered_chips_start()
    {
        Assert.Equal(new Thickness(8, 180, 0, 0), Convert(600.0, 240.0));
    }

    [Fact]
    public void Top_floors_at_zero_when_the_rail_column_is_shorter_than_the_chips()
    {
        Assert.Equal(new Thickness(8, 0, 0, 0), Convert(200.0, 240.0));
    }

    [Fact]
    public void Unset_bindings_during_the_first_layout_pass_yield_the_resting_margin()
    {
        Assert.Equal(new Thickness(8, 0, 0, 0),
            Convert(DependencyProperty.UnsetValue, DependencyProperty.UnsetValue));
    }
}
