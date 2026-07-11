using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;
using Wpf.Ui.Controls;
using KeyEventArgs = System.Windows.Input.KeyEventArgs; // WinForms' implicit using also exports one

namespace NagaBatteryTray.Ui;

public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsViewModel _vm;
    private ButtonRowViewModel? _capturingRow;

    public event Action? SaveRequested;        // raised on close — persist threshold/cadence
    public event Action<bool>? StartupToggled;  // raised immediately on the toggle
    public event Action<int>? ApplyDpiRequested; // raised on the Apply button
    public event Action<IReadOnlyList<ButtonOp>>? ApplyButtonsRequested; // raised on "Apply buttons"

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

    public ButtonRowViewModel ButtonRow(int position) => _vm.Row(position);
    public void SetButtonsStatus(string text) => _vm.ButtonsStatus = text;

    private void OnStartupToggled(object sender, RoutedEventArgs e) => StartupToggled?.Invoke(_vm.RunAtStartup);
    private void OnApplyDpi(object sender, RoutedEventArgs e) => ApplyDpiRequested?.Invoke(_vm.Dpi);
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnRebindButton(object sender, RoutedEventArgs e)
    {
        if (_capturingRow is { } prev) prev.IsCapturing = false;
        var row = (ButtonRowViewModel)((FrameworkElement)sender).DataContext;
        _capturingRow = row;
        row.IsCapturing = true;
        Focus(); // take focus off the clicked button so the next key lands in the window
    }

    private void OnDisableButton(object sender, RoutedEventArgs e) =>
        ((ButtonRowViewModel)((FrameworkElement)sender).DataContext).StageDisabled();

    private void OnDefaultButton(object sender, RoutedEventArgs e) =>
        ((ButtonRowViewModel)((FrameworkElement)sender).DataContext).StageDefault();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturingRow is not { } row) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt-chords arrive as Key.System
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return; // a bare modifier — keep capturing until the real key arrives
        _capturingRow = null;
        if (key == Key.Escape) { row.IsCapturing = false; return; } // cancel (Esc is not bindable)
        if (!KeyToHidUsage.TryGetUsage(key, out byte usage))
        {
            row.IsCapturing = false;
            row.Status = $"{key} can't be bound";
            return;
        }
        row.StageKey(KeyToHidUsage.ToModifierBits(Keyboard.Modifiers), usage);
    }

    private void OnApplyButtons(object sender, RoutedEventArgs e)
    {
        var ops = _vm.GetPendingButtonOps();
        if (ops.Count == 0) { _vm.ButtonsStatus = "No changes"; return; }
        ApplyButtonsRequested?.Invoke(ops);
    }
}
