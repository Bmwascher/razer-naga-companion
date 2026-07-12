using System.Windows;
using System.Windows.Media;
using NagaBatteryTray.Ui;
using Xunit;

public class ThemeManagerTests
{
    [Theory]
    [InlineData("Porcelain")] [InlineData("Razer")] [InlineData("Ice")]
    [InlineData("Ultraviolet")] [InlineData("Ember")]
    public void Known_names_resolve_to_themselves(string name) =>
        Assert.Equal(name, ThemeManager.Resolve(name));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("Neon")] [InlineData("porcelain")]
    public void Unknown_names_fall_back_to_porcelain(string? name) =>
        Assert.Equal("Porcelain", ThemeManager.Resolve(name));

    [Fact]
    public void DictionaryUri_points_into_ui_themes() =>
        Assert.Equal("pack://application:,,,/Ui/Themes/Ember.xaml",
            ThemeManager.DictionaryUri("Ember").ToString());

    [Fact]
    public void AccentOf_reads_the_App_Accent_brush_color()
    {
        var dict = new ResourceDictionary { { "App.Accent", new SolidColorBrush(Colors.HotPink) } };
        Assert.Equal(Colors.HotPink, ThemeManager.AccentOf(dict));
    }

    [Fact]
    public void AccentOf_is_null_when_the_dictionary_carries_no_accent() =>
        Assert.Null(ThemeManager.AccentOf(new ResourceDictionary()));
}
