using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NagaBatteryTray.Ui;
using UserControl = System.Windows.Controls.UserControl; // WinForms' implicit using also exports one

namespace NagaBatteryTray.Ui.Dashboard;

public partial class MouseStageView : UserControl
{
    public event Action<CalloutViewModel>? CaptureRequested; // window owns the keyboard hook
    public event Action<int>? ApplyDpiRequested;
    public event Action? LivenessRefreshRequested;
    public event Action<byte>? SwitchProfileRequested;

    private const int StaggerStepMs = 30;
    private static readonly Duration StaggerDuration = new(TimeSpan.FromMilliseconds(200));
    private const double StaggerRiseY = 4;

    private bool _staggered; // Loaded can fire more than once per instance (visual-tree reparenting) - run once

    public MouseStageView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Chip stagger-in (spec: the one delight moment on dashboard open). Runs once per
    /// view instance - AppHost creates a fresh DashboardWindow (and so a fresh MouseStageView) per
    /// open, so this naturally fires once per dashboard open. Deferred to Loaded-priority dispatch
    /// because the ItemsControl containers may not be realized yet when Loaded itself fires; if
    /// they still aren't realized by then, skip silently rather than throw.</summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_staggered) return;
        _staggered = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RunChipStagger));
    }

    private void RunChipStagger()
    {
        var containers = new List<ContentPresenter>();
        CollectContainers(LeftChips, containers);
        CollectContainers(RightChips, containers);
        if (containers.Count == 0) return; // not realized - skip silently, nothing to animate

        bool reduced = Motion.Reduced;
        for (int i = 0; i < containers.Count; i++)
        {
            var cp = containers[i];
            var begin = TimeSpan.FromMilliseconds(i * StaggerStepMs);

            cp.Opacity = 0;
            var fade = new DoubleAnimation
            { From = 0, To = 1, BeginTime = begin,
              Duration = reduced ? Motion.Micro : StaggerDuration, EasingFunction = Motion.EaseOut };
            cp.BeginAnimation(OpacityProperty, fade);

            if (reduced) continue; // reduced motion: fade only, no rise
            var translate = new TranslateTransform(0, StaggerRiseY);
            cp.RenderTransform = translate;
            var rise = new DoubleAnimation
            { From = StaggerRiseY, To = 0, BeginTime = begin, Duration = StaggerDuration, EasingFunction = Motion.EaseOut };
            translate.BeginAnimation(TranslateTransform.YProperty, rise);
        }
    }

    private static void CollectContainers(ItemsControl items, List<ContentPresenter> into)
    {
        for (int i = 0; i < items.Items.Count; i++)
            if (items.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter cp)
                into.Add(cp);
    }

    /// <summary>Split the 12 callouts 6 left / 6 right and seed the grid keys.</summary>
    public void Bind(DashboardViewModel vm)
    {
        DataContext = vm;
        LeftChips.ItemsSource = vm.Callouts.Take(6).ToList();
        RightChips.ItemsSource = vm.Callouts.Skip(6).ToList();
        // face-on thumb-panel view: natural order matches the hardware labels exactly
        // (rows 1 2 3 / 4 5 6 / 7 8 9 / 10 11 12, per the user's reference photo)
        GridKeys.ItemsSource = vm.Callouts;
    }

    private static CalloutViewModel Vm(object sender) =>
        (CalloutViewModel)((FrameworkElement)sender).DataContext;

    // shared by the callout rows AND the stage's grid keys — both hover-pair via IsHighlighted
    private void OnChipEnter(object s, RoutedEventArgs e) => Vm(s).IsHighlighted = true;
    private void OnChipLeave(object s, RoutedEventArgs e) { Vm(s).IsHighlighted = false; ReleasePress(s); }

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
        var fe = (FrameworkElement)el;
        // The template's <ScaleTransform/> is shared across all stamped chips/keys and FROZEN
        // by WPF's compiled-template optimization — animating it throws (crashed the app on
        // the first real pointer interaction). Swap in a per-element transform before animating.
        if (fe.RenderTransform is not ScaleTransform st || st.IsFrozen)
            fe.RenderTransform = st = new ScaleTransform(1, 1);
        Motion.Animate(st, ScaleTransform.ScaleXProperty, to, Motion.Press, Motion.EaseOut);
        Motion.Animate(st, ScaleTransform.ScaleYProperty, to, Motion.Press, Motion.EaseOut);
    }

    private void OnUndo(object s, RoutedEventArgs e) => _ = Vm(s).UndoAsync();
    // Disable/Default are also offered inside the capture card - resolve the capture first
    // so the row doesn't stay armed after the write
    private void OnDisable(object s, RoutedEventArgs e)
    { var vm = Vm(s); vm.CancelCapture(); _ = vm.DisableAsync(); }
    private void OnDefault(object s, RoutedEventArgs e)
    { var vm = Vm(s); vm.CancelCapture(); _ = vm.DefaultAsync(); }

    // every pointer interaction with the slider (thumb drag, track click, page-jump hold)
    // ends in a mouse-up — apply once there
    private void OnDpiPointerUp(object s, System.Windows.Input.MouseButtonEventArgs e) =>
        ApplyDpiRequested?.Invoke(((DashboardViewModel)DataContext).Dpi);
    private void OnDpiSliderKeyUp(object s, System.Windows.Input.KeyEventArgs e)
    { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Left
          or System.Windows.Input.Key.Right or System.Windows.Input.Key.Up
          or System.Windows.Input.Key.Down or System.Windows.Input.Key.PageUp
          or System.Windows.Input.Key.PageDown or System.Windows.Input.Key.Home
          or System.Windows.Input.Key.End)
        ApplyDpiRequested?.Invoke(((DashboardViewModel)DataContext).Dpi); }

    private void OnApplyPreset(object s, RoutedEventArgs e)
    {
        var item = (DpiPresetItem)((FrameworkElement)s).DataContext;
        ((DashboardViewModel)DataContext).Dpi = item.Value;
        ApplyDpiRequested?.Invoke(item.Value);
    }
    private void OnRemovePreset(object s, RoutedEventArgs e) =>
        ((DashboardViewModel)DataContext).RemovePreset((DpiPresetItem)((FrameworkElement)s).DataContext);
    private void OnSavePreset(object s, RoutedEventArgs e)
    { var vm = (DashboardViewModel)DataContext; vm.AddPreset(vm.Dpi); }

    private void OnRefreshLiveness(object s, RoutedEventArgs e) => LivenessRefreshRequested?.Invoke();

    private void OnSwitchProfile(object s, RoutedEventArgs e)
    {
        var item = (ProfileSlotItem)((FrameworkElement)s).DataContext;
        if (item.IsActive) return; // already there - the pill isn't hit-test visible, but guard anyway
        SwitchProfileRequested?.Invoke(item.Number);
    }
}
