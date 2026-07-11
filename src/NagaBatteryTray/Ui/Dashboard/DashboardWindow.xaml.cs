using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brush = System.Windows.Media.Brush; // WinForms' implicit using also exports one

namespace NagaBatteryTray.Ui.Dashboard;

public partial class DashboardWindow : FluentWindow
{
    private readonly DashboardViewModel _vm;
    private CalloutViewModel? _capturing;

    public event Action? SettingsOverlayRequested;
    public event Action<int>? ApplyDpiRequested;
    public event Action? LivenessRefreshRequested;

    public DashboardWindow(DashboardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Stage.Bind(vm);
        Stage.CaptureRequested += BeginCapture;
        Stage.ApplyDpiRequested += dpi => ApplyDpiRequested?.Invoke(dpi);
        Stage.LivenessRefreshRequested += () => LivenessRefreshRequested?.Invoke();
        vm.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(vm.StatusDotBrushKey)) UpdateDot(); };
        UpdateDot();
        PreviewMouseDown += (_, _) => { if (_capturing is { } c && !c.IsCapturing) _capturing = null; };
    }

    private void UpdateDot() =>
        StatusDot.Fill = (Brush)FindResource(_vm.StatusDotBrushKey);

    public void ShowOverlay(UIElement content)
    {
        OverlayHost.Children.Clear();
        OverlayHost.Children.Add(content);
        OverlayHost.Visibility = Visibility.Visible;
    }
    public void HideOverlay() => OverlayHost.Visibility = Visibility.Collapsed;

    private void OnGear(object s, RoutedEventArgs e) => SettingsOverlayRequested?.Invoke();

    private void BeginCapture(CalloutViewModel target)
    {
        if (_capturing is { } prev) prev.CancelCapture();
        _capturing = target;
        target.BeginCapture();
        Focus(); // pull focus off the clicked chip so the next key lands on the window
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturing is not { } chip || !chip.IsCapturing) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt-chords arrive as Key.System
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return; // bare modifier — keep capturing
        _capturing = null;
        if (key == Key.Escape) { chip.CancelCapture(); return; }
        if (!KeyToHidUsage.TryGetUsage(key, out byte usage))
        { chip.CancelCapture(); chip.Status = $"{key} can't be bound"; return; }
        _ = chip.CaptureAsync(KeyToHidUsage.ToModifierBits(Keyboard.Modifiers), usage);
    }
}
