using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NagaBatteryTray.Monitoring;
using Forms = System.Windows.Forms;

namespace NagaBatteryTray.Ui;

public partial class PopupWindow : Window
{
    private readonly PopupViewModel _vm = new();
    public event Action? RefreshRequested;

    public PopupWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Left = -32000; // park off-screen so the first frame never flashes on a monitor
        Top = -32000;
        Deactivated += (_, _) => Hide();
    }

    public void ShowAt(DeviceState state)
    {
        _vm.Apply(state);
        Show();           // realize + lay out so SizeToContent sets the real size
        UpdateLayout();
        PositionNearCursor();
        Activate();
    }

    public void ApplyState(DeviceState state) => _vm.Apply(state);

    /// <summary>Place the popup just above the taskbar on the monitor the user clicked, in physical pixels
    /// (avoids WPF per-monitor DIP confusion that put it off-screen across mixed-DPI displays).</summary>
    private void PositionNearCursor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!GetWindowRect(hwnd, out var r)) return;

        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        var cursor = Forms.Cursor.Position;                  // physical px, on the clicked monitor
        var wa = Forms.Screen.FromPoint(cursor).WorkingArea;  // that monitor's work area (physical px)
        const int margin = 4;

        int x = Math.Clamp(cursor.X - w / 2, wa.Left, Math.Max(wa.Left, wa.Right - w));
        int y = Math.Max(wa.Top, wa.Bottom - h - margin);     // just above the taskbar
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
