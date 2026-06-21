using System.Drawing;
using System.Windows.Forms;
using NagaBatteryTray.Monitoring;

namespace NagaBatteryTray.Ui;

public sealed class TrayIconController : IDisposable
{
    private readonly BatteryMonitor _monitor;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _startupItem;
    private Icon? _current;

    public event Action? LeftClicked;
    public event Action? RefreshRequested;
    public event Action<bool>? StartupToggled;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public TrayIconController(BatteryMonitor monitor)
    {
        _monitor = monitor;
        _icon = new NotifyIcon { Visible = false, Text = "Naga V2 Pro" };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, (_, _) => RefreshRequested?.Invoke());
        _startupItem = new ToolStripMenuItem("Run at startup") { CheckOnClick = true };
        _startupItem.CheckedChanged += OnStartupChanged;
        menu.Items.Add(_startupItem);
        menu.Items.Add("Settings", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) LeftClicked?.Invoke(); };
        _monitor.StateChanged += (_, state) => Update(state);
    }

    public void Show()
    {
        Update(_monitor.State);
        _icon.Visible = true;
    }

    public void SetStartupChecked(bool value)
    {
        _startupItem.CheckedChanged -= OnStartupChanged; // avoid firing the toggle on a programmatic set
        _startupItem.Checked = value;
        _startupItem.CheckedChanged += OnStartupChanged;
    }

    private void OnStartupChanged(object? sender, EventArgs e) => StartupToggled?.Invoke(_startupItem.Checked);

    private void Update(DeviceState state)
    {
        int dpi = (int)Math.Round(96 * GetDpiScale());
        var next = IconRenderer.Render(state, dpi);
        _icon.Icon = next;
        IconRenderer.Destroy(_current);
        _current = next;
        _icon.Text = Tooltip(state);
    }

    private static string Tooltip(DeviceState s) => s.Status == DeviceStatus.Unknown
        ? "Naga V2 Pro - no response"
        : $"Naga V2 Pro - {s.Percent}%{(s.Charging ? " (charging)" : "")}";

    private static double GetDpiScale()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96.0;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        IconRenderer.Destroy(_current);
    }
}
