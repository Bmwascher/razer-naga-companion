using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace NagaBatteryTray.Ui;

/// <summary>Runtime theme preset switching: a preset is one brush ResourceDictionary in Ui/Themes
/// carrying the marker key "App.ThemeName". Apply() swaps the marked dictionary in place.</summary>
public static class ThemeManager
{
    public static readonly string[] PresetNames = { "Porcelain", "Razer", "Ice", "Ultraviolet", "Ember" };

    // Touching PackUriHelper forces its static constructor, which registers the "pack" URI scheme.
    // A running WPF Application does this as a side effect of startup; a bare test host (no
    // Application instance) never does, so an unguarded `new Uri("pack://...")` throws
    // "Invalid port specified" the first time this runs in-process.
    static ThemeManager() => _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

    public static string Resolve(string? name) =>
        Array.IndexOf(PresetNames, name) >= 0 ? name! : "Porcelain";

    public static Uri DictionaryUri(string? name) =>
        new($"pack://application:,,,/Ui/Themes/{Resolve(name)}.xaml");

    /// <summary>Pulls the preset's own "App.Accent" brush color out of its ResourceDictionary, or
    /// null if the dictionary doesn't carry one. Pure/testable without a live Application.</summary>
    public static Color? AccentOf(ResourceDictionary dict) =>
        dict.Contains("App.Accent") && dict["App.Accent"] is SolidColorBrush brush ? brush.Color : null;

    public static void Apply(Application app, string? name)
    {
        var next = new ResourceDictionary { Source = DictionaryUri(name) };
        var dicts = app.Resources.MergedDictionaries;
        bool replaced = false;
        for (int i = 0; i < dicts.Count; i++)
            if (dicts[i].Contains("App.ThemeName")) { dicts[i] = next; replaced = true; break; }
        if (!replaced) dicts.Add(next);

        // WPF-UI's own control chrome (Slider thumb, ToggleSwitch, ListBox selection, NumberBox)
        // colors from ApplicationAccentColorManager's SystemAccentColor* resources, which the
        // dictionary swap above never touches — push the preset's accent into WPF-UI so that
        // chrome follows the theme instead of the OS accent color. Guarded: a bare xunit test host
        // has no live Application/resources for WPF-UI to update.
        if (AccentOf(next) is { } accent)
        {
            try
            {
                ApplicationAccentColorManager.Apply(accent, ApplicationTheme.Dark);
                // Pushing the accent resources alone doesn't repaint chrome that already baked
                // them at template time — a live theme switch left toggles/selection in the
                // previous theme's accent until the dashboard was rebuilt. Re-applying the theme
                // dictionary makes WPF-UI re-derive its control brushes from the accent above;
                // updateAccent must stay false or the OS accent stomps that push (the startup bug).
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, updateAccent: false);
            }
            catch { /* no-op: no live Application (e.g. bare test host) */ }
        }
    }
}
