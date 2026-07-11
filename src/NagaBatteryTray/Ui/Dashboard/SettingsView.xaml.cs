using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl; // WinForms' implicit using also exports one

namespace NagaBatteryTray.Ui.Dashboard;

public partial class SettingsView : UserControl
{
    public event Action? CloseRequested;
    public event Action<bool>? StartupToggled;
    public event Action<string>? ThemeChanged;
    public event Action? ResetAllRequested;

    public SettingsView() => InitializeComponent();

    public void SetResetNote(string text) => ResetNote.Text = text;

    private void OnClose(object s, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void OnStartupToggled(object s, RoutedEventArgs e) =>
        StartupToggled?.Invoke(((DashboardViewModel)DataContext).RunAtStartup);
    private void OnThemeChanged(object s, SelectionChangedEventArgs e)
    { if (ThemeList.SelectedItem is string name) ThemeChanged?.Invoke(name); }
    private void OnResetAll(object s, RoutedEventArgs e) => ResetAllRequested?.Invoke();
}
