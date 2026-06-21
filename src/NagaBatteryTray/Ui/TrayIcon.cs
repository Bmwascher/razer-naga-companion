using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NagaBatteryTray.Ui;

/// <summary>
/// Tray icon backed by Shell_NotifyIcon with a stable, GUID-based identity. WinForms NotifyIcon has no
/// GUID, so Windows keys its position/promotion on the (per-launch) window handle and forgets where the
/// user placed it after any app or Explorer restart (bug #12). A GUID gives the icon one identity for
/// the life of the install, so Windows 11 persists its taskbar position.
///
/// The GUID is derived from the executable path: stable across restarts for a given binary (position
/// sticks), yet distinct between the installed exe and a dev-host run — so they never collide on the
/// shell's one-GUID-per-executable rule (which otherwise makes the icon silently fail to register).
/// </summary>
internal sealed class TrayIcon : NativeWindow, IDisposable
{
    private const int WM_TRAYICON = 0x0400 + 1; // WM_USER + 1 (private callback id)
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_GUID = 0x20, NIF_SHOWTIP = 0x80;
    private const int NOTIFYICON_VERSION_4 = 4;

    private static readonly int WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

    private readonly Guid _guid = IconGuid();
    private IntPtr _hIcon;
    private string _tip = "";
    private bool _added;

    public event Action? LeftClick;
    public event Action<Point>? ContextMenuRequested;

    public TrayIcon() => CreateHandle(new CreateParams());

    /// <summary>Set the displayed icon + tooltip, registering the icon on first call.</summary>
    public void Update(Icon icon, string tip)
    {
        _hIcon = icon.Handle;
        _tip = tip;
        if (!_added) { Add(); return; }
        var d = BaseData(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    private void Add()
    {
        var d = BaseData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        bool ok = Shell_NotifyIcon(NIM_ADD, ref d);
        if (!ok)
        {
            // The GUID is still registered (e.g. a previous instance crashed without deleting it). Clear
            // it and retry once.
            var stale = BaseData(0);
            Shell_NotifyIcon(NIM_DELETE, ref stale);
            ok = Shell_NotifyIcon(NIM_ADD, ref d);
        }
        if (!ok) return;
        var ver = BaseData(0);
        ver.uVersionOrTimeout = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref ver);
        _added = true;
    }

    private NOTIFYICONDATA BaseData(int extraFlags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = Handle,
        uFlags = NIF_GUID | extraFlags,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _hIcon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
        guidItem = _guid,
    };

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_TRAYICON)
        {
            // Under NOTIFYICON_VERSION_4 the shell sends the high-level NIN_*/WM_CONTEXTMENU notifications
            // AND the raw mouse messages (WM_LBUTTONUP, WM_RBUTTONUP) for the same click. Handle only the
            // former, or each click fires twice (toggling the popup open then shut).
            int evt = (int)(m.LParam.ToInt64() & 0xFFFF);
            if (evt is NIN_SELECT or NIN_KEYSELECT)
            {
                LeftClick?.Invoke();
            }
            else if (evt == WM_CONTEXTMENU)
            {
                GetCursorPos(out POINT p);
                SetForegroundWindow(Handle); // so the menu dismisses when focus moves away
                ContextMenuRequested?.Invoke(new Point(p.X, p.Y));
            }
            return;
        }
        if (m.Msg == WM_TASKBARCREATED) { _added = false; Add(); return; } // Explorer restarted -> re-add
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_added) { var d = BaseData(0); Shell_NotifyIcon(NIM_DELETE, ref d); _added = false; }
        DestroyHandle();
    }

    private static Guid IconGuid()
    {
        string path = (Environment.ProcessPath ?? "NagaBatteryTray").ToLowerInvariant();
        byte[] hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.Unicode.GetBytes(path));
        return new Guid(hash);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
}
