using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NagaBatteryTray.Ui;
using UserControl = System.Windows.Controls.UserControl; // WinForms' implicit using also exports one

namespace NagaBatteryTray.Ui.Dashboard;

public partial class MouseStageView : UserControl
{
    public event Action<CalloutViewModel>? CaptureRequested; // window owns the keyboard hook
    public event Action<int>? ApplyDpiRequested;
    public event Action? LivenessRefreshRequested;

    public MouseStageView() => InitializeComponent();

    /// <summary>Split the 12 callouts 6 left / 6 right and seed the grid keys.</summary>
    public void Bind(DashboardViewModel vm)
    {
        DataContext = vm;
        LeftChips.ItemsSource = vm.Callouts.Take(6).ToList();
        RightChips.ItemsSource = vm.Callouts.Skip(6).ToList();
        GridKeys.ItemsSource = vm.Callouts;
    }

    private static CalloutViewModel Vm(object sender) =>
        (CalloutViewModel)((FrameworkElement)sender).DataContext;

    private void OnChipEnter(object s, RoutedEventArgs e) => Vm(s).IsHighlighted = true;
    private void OnChipLeave(object s, RoutedEventArgs e) { Vm(s).IsHighlighted = false; ReleasePress(s); }
    private void OnKeyEnter(object s, RoutedEventArgs e) => Vm(s).IsHighlighted = true;
    private void OnKeyLeave(object s, RoutedEventArgs e) { Vm(s).IsHighlighted = false; ReleasePress(s); }

    private void OnChipClick(object s, RoutedEventArgs e) => CaptureRequested?.Invoke(Vm(s));
    private void OnChipKeyUp(object s, System.Windows.Input.KeyEventArgs e)
    { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
        CaptureRequested?.Invoke(Vm(s)); }

    // Press feedback (spec: chips + grid keys scale to 0.97 on pointer-down, back to 1.0 on
    // release/leave). Code-behind rather than XAML EventTriggers so Motion.Reduced can gate it -
    // reduced motion is positional (a scale), so it's skipped entirely, not just slowed down.
    private void OnChipPressDown(object s, RoutedEventArgs e) => PressScale((UIElement)s, 0.97);
    private void OnChipPressUp(object s, RoutedEventArgs e) => PressScale((UIElement)s, 1.0);
    private void ReleasePress(object s) => PressScale((UIElement)s, 1.0);

    private static void PressScale(UIElement el, double to)
    {
        if (Motion.Reduced) return;
        if (((FrameworkElement)el).RenderTransform is not ScaleTransform st) return;
        Motion.Animate(st, ScaleTransform.ScaleXProperty, to, Motion.Press, Motion.EaseOut);
        Motion.Animate(st, ScaleTransform.ScaleYProperty, to, Motion.Press, Motion.EaseOut);
    }

    private void OnUndo(object s, RoutedEventArgs e) => _ = Vm(s).UndoAsync();
    private void OnDisable(object s, RoutedEventArgs e) => _ = Vm(s).DisableAsync();
    private void OnDefault(object s, RoutedEventArgs e) => _ = Vm(s).DefaultAsync();

    private void OnDpiDragCompleted(object s, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        ApplyDpiRequested?.Invoke(((DashboardViewModel)DataContext).Dpi);
    private void OnDpiSliderKeyUp(object s, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == System.Windows.Input.Key.Enter)
        ApplyDpiRequested?.Invoke(((DashboardViewModel)DataContext).Dpi); }

    private void OnApplyPreset(object s, RoutedEventArgs e)
    {
        var item = (DpiPresetItem)((FrameworkElement)s).DataContext;
        ((DashboardViewModel)DataContext).Dpi = item.Value;
        ApplyDpiRequested?.Invoke(item.Value);
    }
    private void OnRemovePreset(object s, RoutedEventArgs e) =>
        ((DashboardViewModel)DataContext).RemovePreset((DpiPresetItem)((FrameworkElement)s).DataContext);
    private void OnAddPreset(object s, RoutedEventArgs e)
    { if (NewPresetBox.Value is { } v) ((DashboardViewModel)DataContext).AddPreset((int)v); }

    private void OnRefreshLiveness(object s, RoutedEventArgs e) => LivenessRefreshRequested?.Invoke();
}
