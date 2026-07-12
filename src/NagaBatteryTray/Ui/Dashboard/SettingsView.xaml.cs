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

    // Two-step inline confirm for "Reset all to factory" (replaces a MessageBox): first click arms
    // it and starts a one-shot 5 s auto-revert, same undo-pattern token idiom as CalloutViewModel's
    // OpenUndoWindowAsync; a second click within the window fires ResetAllRequested.
    private bool _confirmingResetAll;
    private int _resetConfirmVersion;

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
    private void OnResetAll(object s, RoutedEventArgs e)
    {
        if (_confirmingResetAll)
        {
            _resetConfirmVersion++; // invalidate the pending auto-revert
            SetConfirmingResetAll(false);
            ResetAllRequested?.Invoke();
            return;
        }
        SetConfirmingResetAll(true);
        _ = RevertConfirmAfterDelayAsync();
    }

    private async Task RevertConfirmAfterDelayAsync()
    {
        int version = ++_resetConfirmVersion;
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (version == _resetConfirmVersion) SetConfirmingResetAll(false);
    }

    private void SetConfirmingResetAll(bool confirming)
    {
        _confirmingResetAll = confirming;
        ResetAllButton.Content = confirming ? "Press again to confirm" : "Reset all to factory";
        if (confirming)
        {
            ResetAllButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "App.AccentSoft");
            ResetAllButton.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "App.Accent");
        }
        else
        {
            ResetAllButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            ResetAllButton.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        }
    }
}
