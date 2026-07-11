using System.Windows;
using Application = System.Windows.Application;

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

    public static void Apply(Application app, string? name)
    {
        var next = new ResourceDictionary { Source = DictionaryUri(name) };
        var dicts = app.Resources.MergedDictionaries;
        for (int i = 0; i < dicts.Count; i++)
            if (dicts[i].Contains("App.ThemeName")) { dicts[i] = next; return; }
        dicts.Add(next);
    }
}
