using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NagaBatteryTray.Ui;
using Wpf.Ui.Controls;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brush = System.Windows.Media.Brush; // WinForms' implicit using also exports one

namespace NagaBatteryTray.Ui.Dashboard;

public partial class DashboardWindow : FluentWindow
{
    private readonly DashboardViewModel _vm;
    private CalloutViewModel? _capturing;
    private int _overlayVersion; // undo-pattern token: guards the hide animation's Completed collapse
                                  // against a ShowOverlay that arrives before it fires

    public event Action? SettingsOverlayRequested;
    public event Action<int>? ApplyDpiRequested;
    public event Action? LivenessRefreshRequested;

    /// <summary>Raised when the scrim is clicked (click-away dismiss). Deliberately an
    /// assignment-style single-subscriber delegate, NOT an event: AppHost re-wires it on every
    /// gear click (ShowSettingsOverlay runs per click, the window lives per dashboard-open), and
    /// `+=` on an event would stack a handler per click — each dismiss would then save settings
    /// once per stacked handler. Assignment replaces the previous subscriber instead.</summary>
    public Action? OverlayDismissRequested { get; set; }

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
        // click-away cancels a live capture (spec §4.3); fires before a chip's MouseLeftButtonUp,
        // so clicking another chip cancels this capture first, then starts its own. Clicks
        // INSIDE the capturing row are exempt: its capture card carries full-size
        // Disable/Default buttons, and cancelling on preview-down would collapse the card
        // before the button's own Click could fire.
        PreviewMouseDown += (_, e) =>
        {
            if (_capturing is not { } c || !c.IsCapturing) { _capturing = null; return; }
            for (var d = e.OriginalSource as DependencyObject; d is not null; d = ParentOf(d))
                if (d is FrameworkElement { DataContext: CalloutViewModel dc } && ReferenceEquals(dc, c))
                    return;
            c.CancelCapture();
            _capturing = null;
        };
    }

    /// <summary>Visual-tree parent that survives content elements (e.g. a Run inside a button).</summary>
    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d)
            : (d as FrameworkContentElement)?.Parent;

    private void UpdateDot() =>
        StatusDot.Fill = (Brush)FindResource(_vm.StatusDotBrushKey);

    /// <summary>Opens the drawer: content slides in from off-screen right (Motion.Drawer curve)
    /// while the scrim fades in on its own (Motion.EaseOut) - decoupled so the slide never rides
    /// on a fading parent. Reduced motion: no slide, both fade in together instead.</summary>
    public void ShowOverlay(UIElement content)
    {
        _overlayVersion++; // cancels any pending hide-collapse
        OverlayContent.Content = content;
        OverlayHost.Visibility = Visibility.Visible;
        Scrim.IsHitTestVisible = true; // restore click-to-close (a mid-hide reopen left it off)

        Motion.Animate(Scrim, OpacityProperty, 1, Motion.Fade, Motion.EaseOut);
        if (Motion.Reduced)
        {
            OverlayTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            OverlayTranslate.X = 0; // positional motion is skipped outright, not slowed down
            OverlayContent.BeginAnimation(OpacityProperty, null);
            OverlayContent.Opacity = 0;
            Motion.Animate(OverlayContent, OpacityProperty, 1, Motion.Fade, Motion.EaseOut);
        }
        else
        {
            Motion.Animate(OverlayTranslate, TranslateTransform.XProperty, 0, Motion.DrawerIn, Motion.Drawer);
        }
    }

    /// <summary>Closes the drawer: slides content back out (or fades it, reduced motion) while the
    /// scrim fades out; the host is only collapsed in the scrim fade's Completed callback, guarded
    /// by a version token so a ShowOverlay arriving mid-hide cancels the pending collapse instead
    /// of the drawer popping back open underneath it.</summary>
    public void HideOverlay()
    {
        int token = ++_overlayVersion;
        // the host stays Visible until the fade's Completed collapse below — without this, the
        // still-hit-testable scrim would swallow every dashboard click for the whole 220 ms hide
        Scrim.IsHitTestVisible = false;
        var scrimFade = new DoubleAnimation(0, Motion.Fade) { EasingFunction = Motion.EaseOut };
        scrimFade.Completed += (_, _) =>
        {
            if (token != _overlayVersion) return; // superseded by a ShowOverlay mid-hide
            OverlayHost.Visibility = Visibility.Collapsed;
        };
        Scrim.BeginAnimation(OpacityProperty, scrimFade, HandoffBehavior.Compose);

        if (Motion.Reduced)
            Motion.Animate(OverlayContent, OpacityProperty, 0, Motion.Fade, Motion.EaseOut);
        else
            Motion.Animate(OverlayTranslate, TranslateTransform.XProperty, 340, Motion.DrawerOut, Motion.Drawer);
    }

    private void OnGear(object s, RoutedEventArgs e) => SettingsOverlayRequested?.Invoke();

    // only fires for clicks on the scrim itself: OverlayContent is a sibling on top of it, so
    // clicks on the drawer content never route through the scrim
    private void OnScrimClick(object s, MouseButtonEventArgs e) => OverlayDismissRequested?.Invoke();

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
