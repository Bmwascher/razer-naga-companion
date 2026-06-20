using System.Windows;
using NagaBatteryTray.Monitoring;
using Wpf.Ui.Controls;
using Drawing = System.Drawing;

namespace NagaBatteryTray.Ui;

public partial class PopupWindow : FluentWindow
{
    private readonly PopupViewModel _vm = new();
    public event Action? RefreshRequested;

    public PopupWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Deactivated += (_, _) => Hide();
    }

    public void ShowAt(DeviceState state, Drawing.Rectangle anchorPx, Drawing.Rectangle workAreaPx)
    {
        _vm.Apply(state);
        double dpi = DpiScale();
        var sizePx = new Drawing.Size(
            (int)(Width * dpi),
            (int)((ActualHeight > 0 ? ActualHeight : 180) * dpi));
        var pt = PopupPlacement.Compute(anchorPx, workAreaPx, sizePx);
        Left = pt.X / dpi;
        Top = pt.Y / dpi;
        Show();
        Activate();
    }

    public void ApplyState(DeviceState state) => _vm.Apply(state);

    private double DpiScale()
    {
        var src = PresentationSource.FromVisual(this);
        return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
}
