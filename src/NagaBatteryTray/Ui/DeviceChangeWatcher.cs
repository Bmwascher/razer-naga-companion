using System.Windows.Forms;

namespace NagaBatteryTray.Ui;

/// <summary>
/// Hidden top-level window that raises <see cref="DeviceChanged"/> when Windows reports a device-tree
/// change (WM_DEVICECHANGE / DBT_DEVNODES_CHANGED) — e.g. the mouse cable being plugged or unplugged.
/// Lets the app refresh charge status immediately instead of waiting for the next battery poll.
///
/// Event-driven and idle-free: nothing runs until the OS broadcasts a change, so it adds no background
/// work and no extra device I/O at rest. DBT_DEVNODES_CHANGED is broadcast to all top-level windows
/// without RegisterDeviceNotification, so a plain (never-shown) window is all that's needed.
/// </summary>
internal sealed class DeviceChangeWatcher : NativeWindow, IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;

    public event Action? DeviceChanged;

    public DeviceChangeWatcher() => CreateHandle(new CreateParams());

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE && (int)m.WParam == DBT_DEVNODES_CHANGED)
            DeviceChanged?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}
