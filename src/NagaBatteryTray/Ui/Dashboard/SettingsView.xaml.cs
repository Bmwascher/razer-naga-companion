using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl; // WinForms' implicit using also exports one

namespace NagaBatteryTray.Ui.Dashboard;

public partial class SettingsView : UserControl
{
    public event Action? CloseRequested;
    public event Action<bool>? StartupToggled;
    public event Action<string>? ThemeChanged;
    public event Action<string>? TrayIconStyleChanged;
    public event Action? ResetAllRequested;

    public SettingsView()
    {
        InitializeComponent();
        // Labels differ from the stored setting values ("Text only" vs. "Text"), so this list is
        // seeded here rather than bound to a settings-shaped VM property, unlike THEME.
        TrayIconStyleList.Items.Add("Gauge");
        TrayIconStyleList.Items.Add("Text only");
    }

    public void SetResetNote(string text) => ResetNote.Text = text;

    /// <summary>Seeds the current tray icon style without firing TrayIconStyleChanged (mirrors
    /// TrayIconController.SetStartupChecked's unsubscribe/set/resubscribe guard).</summary>
    public void SetTrayIconStyle(string style)
    {
        TrayIconStyleList.SelectionChanged -= OnTrayIconStyleChanged;
        TrayIconStyleList.SelectedItem = style == "Text" ? "Text only" : "Gauge";
        TrayIconStyleList.SelectionChanged += OnTrayIconStyleChanged;
    }

    private void OnClose(object s, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void OnStartupToggled(object s, RoutedEventArgs e) =>
        StartupToggled?.Invoke(((DashboardViewModel)DataContext).RunAtStartup);
    private void OnThemeChanged(object s, SelectionChangedEventArgs e)
    { if (ThemeList.SelectedItem is string name) ThemeChanged?.Invoke(name); }
    private void OnTrayIconStyleChanged(object s, SelectionChangedEventArgs e)
    { if (TrayIconStyleList.SelectedItem is string label) TrayIconStyleChanged?.Invoke(label == "Text only" ? "Text" : "Gauge"); }
    private void OnResetAll(object s, RoutedEventArgs e) => ResetAllRequested?.Invoke();
}
