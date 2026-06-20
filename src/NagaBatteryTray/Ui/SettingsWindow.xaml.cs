using System.Windows;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;
using Wpf.Ui.Controls;

namespace NagaBatteryTray.Ui;

public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsViewModel _vm;

    public event Action? SaveRequested;        // raised on close — persist threshold/cadence
    public event Action<bool>? StartupToggled;  // raised immediately on the toggle
    public event Action<int>? ApplyDpiRequested; // raised on the Apply button

    public SettingsWindow(AppSettings source, bool runAtStartup)
    {
        InitializeComponent();
        _vm = new SettingsViewModel(source, runAtStartup);
        DataContext = _vm;
        Closed += (_, _) => SaveRequested?.Invoke();
    }

    public void ApplyTo(AppSettings target) => _vm.ApplyTo(target);
    public void SetCurrentDpi(DpiSetting? dpi) => _vm.SetCurrentDpi(dpi);
    public void SetDpiStatus(string text) => _vm.DpiStatus = text;
    public void SetDevicePresent(bool present) => _vm.DevicePresent = present;

    private void OnStartupToggled(object sender, RoutedEventArgs e) => StartupToggled?.Invoke(_vm.RunAtStartup);
    private void OnApplyDpi(object sender, RoutedEventArgs e) => ApplyDpiRequested?.Invoke(_vm.Dpi);
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
