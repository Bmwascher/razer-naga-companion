using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Startup;
using NagaBatteryTray.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Application = System.Windows.Application;

namespace NagaBatteryTray;

public sealed class AppHost
{
    private readonly Application _app;
    private readonly ISettingsStore _settings = new JsonSettingsStore(JsonSettingsStore.DefaultPath());
    private readonly StartupRegistration _startup = new();

    private RazerDevice _device = null!;
    private BatteryMonitor _monitor = null!;
    private TrayIconController _tray = null!;
    private PopupWindow? _popup;
    private SettingsWindow? _settingsWindow;
    private DeviceChangeWatcher? _deviceWatcher;
    private CancellationTokenSource? _deviceDebounce;

    public AppHost(Application app) => _app = app;

    public void Start()
    {
        // WPF-UI controls need its theme + controls dictionaries merged into Application.Resources
        // (no App.xaml here), or FluentWindow / ui:Button render unstyled.
        _app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        _app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        _device = new RazerDevice(_settings);
        _monitor = new BatteryMonitor(_device, _settings, Dispatch);
        _tray = new TrayIconController(_monitor);

        _monitor.LowBatteryCrossed += (_, pct) => Notifications.LowBattery(pct);
        _tray.LeftClicked += TogglePopup;
        _tray.RefreshRequested += () => _ = _monitor.RefreshNowAsync();
        _tray.StartupToggled += SetStartup;
        _tray.SettingsRequested += OpenSettings;
        _tray.QuitRequested += Quit;

        _tray.SetStartupChecked(_startup.IsEnabled());
        _tray.Show();

        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) _ = _monitor.RefreshNowAsync(); };
        SystemEvents.SessionSwitch += (_, e) => { if (e.Reason == SessionSwitchReason.SessionUnlock) _ = _monitor.RefreshNowAsync(); };

        _deviceWatcher = new DeviceChangeWatcher();
        _deviceWatcher.DeviceChanged += OnDeviceChanged;

        _monitor.SetRemaps(CurrentBindings()); // first poll verifies + applies them (empty table = no-op)
        _monitor.Start();
    }

    private void Dispatch(Action action) => _app.Dispatcher.Invoke(action);

    private const int DeviceSettleMs = 750;

    // A single plug/unplug emits a burst of WM_DEVICECHANGE messages; coalesce them into one refresh
    // fired DeviceSettleMs after the last, so the link has settled (and re-enumerated) before we read.
    // Task.Run keeps the settle delay and the blocking HID read off the UI thread that WndProc runs on.
    private void OnDeviceChanged()
    {
        _deviceDebounce?.Cancel(); // supersede any pending refresh (old CTS is collected once its task unwinds)
        var cts = new CancellationTokenSource();
        _deviceDebounce = cts;
        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(DeviceSettleMs, ct); }
            catch (OperationCanceledException) { return; }
            await _monitor.RefreshNowAsync(); // the refresh also sentinel-verifies + re-asserts remaps
        }, ct);
    }

    private void TogglePopup()
    {
        _popup ??= CreatePopup();
        if (_popup.IsVisible) { _popup.Hide(); return; }
        _popup.ShowAt(_monitor.State);
    }

    private PopupWindow CreatePopup()
    {
        var p = new PopupWindow();
        p.RefreshRequested += () => _ = _monitor.RefreshNowAsync();
        p.SettingsRequested += OpenSettings;
        _monitor.StateChanged += (_, state) => p.ApplyState(state); // live-update the popup while it's open
        return p;
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }

        var win = new SettingsWindow(_settings.Settings, _startup.IsEnabled());
        win.SaveRequested += () => { win.ApplyTo(_settings.Settings); _settings.Save(); };
        win.StartupToggled += enable => { SetStartup(enable); _tray.SetStartupChecked(enable); };
        win.ApplyDpiRequested += dpi => _ = ApplyDpiAsync(win, dpi);
        win.ApplyButtonsRequested += ops => _ = ApplyButtonsAsync(win, ops);
        win.Closed += (_, _) => _settingsWindow = null;
        win.SetDevicePresent(_monitor.State.Status == DeviceStatus.Online);
        _settingsWindow = win;
        win.Show();
        _ = LoadDpiAsync(win); // read current DPI off the UI thread, then seed the UI
    }

    // Task.Run keeps the blocking HidD_*Feature calls off the UI thread (no UI freeze; supports the
    // lightweight + zero-latency invariant). Results marshal back via Dispatch.
    private async Task LoadDpiAsync(SettingsWindow win)
    {
        var dpi = await Task.Run(() => _monitor.GetDpiAsync());
        Dispatch(() => { win.SetCurrentDpi(dpi); win.SetDevicePresent(dpi is not null); });
    }

    private async Task ApplyDpiAsync(SettingsWindow win, int dpi)
    {
        Dispatch(() => win.SetDpiStatus("Applying…"));
        bool ok = await Task.Run(() => _monitor.SetDpiAsync(dpi, dpi));
        DpiSetting? readBack = ok ? await Task.Run(() => _monitor.GetDpiAsync()) : null;
        Dispatch(() =>
        {
            if (readBack is { } v && v.X == dpi)
            {
                win.SetCurrentDpi(v);
                win.SetDpiStatus($"Applied ({v.X} DPI)");
            }
            else
            {
                win.SetDpiStatus("Couldn't confirm — wiggle the mouse and retry");
            }
        });
    }

    /// <summary>The persisted remap table as wire-ready bindings — the monitor's sentinel-verify set.</summary>
    private IEnumerable<ButtonBinding> CurrentBindings()
    {
        foreach (var (pos, b) in _settings.Settings.ButtonBindings)
            yield return new ButtonBinding(NagaV2ProButtons.IdForPosition(pos), b.Kind, b.Modifiers, b.HidUsage);
    }

    /// <summary>Apply staged button ops: snapshot the stock action at a button's first-ever remap,
    /// write the binding (volatile profile), read-back verify, persist. Default restores the snapshot
    /// and drops the entry. Every HID call runs off the UI thread via the monitor's shared lock.</summary>
    private async Task ApplyButtonsAsync(SettingsWindow win, IReadOnlyList<ButtonOp> ops)
    {
        Dispatch(() => win.SetButtonsStatus("Applying…"));
        var table = _settings.Settings.ButtonBindings;
        int okCount = 0;

        foreach (var op in ops)
        {
            var row = win.ButtonRow(op.Position);
            byte id = NagaV2ProButtons.IdForPosition(op.Position);
            bool ok;

            if (op.OpKind == ButtonOpKind.RestoreDefault)
            {
                table.TryGetValue(op.Position, out var entry);
                bool deferred = entry is not null && !entry.HasStock;
                ok = entry is null || !entry.HasStock
                     || await Task.Run(() => _monitor.SetButtonAsync(id, entry.StockCategory, entry.StockData));
                if (ok)
                {
                    table.Remove(op.Position);
                    Dispatch(() =>
                    {
                        row.MarkApplied();
                        if (deferred) row.Status = "Default after next reconnect";
                    });
                }
                else Dispatch(() => row.MarkFailed("Restore failed — retry"));
            }
            else
            {
                if (!table.TryGetValue(op.Position, out var entry))
                {
                    entry = new ButtonBindingSetting();
                    // first-ever remap of this button: it has never been written, so the direct-profile
                    // read returns its stock action — snapshot it for instant Default later
                    var stock = await Task.Run(() => _monitor.GetButtonAsync(id));
                    if (stock is { } s)
                    {
                        entry.StockCategory = s.Category;
                        entry.StockData = s.Data;
                        entry.HasStock = true;
                    }
                }
                var binding = new ButtonBinding(id, op.Kind, op.Modifiers, op.HidUsage);
                var (category, data) = binding.ToWire();
                ok = await Task.Run(() => _monitor.SetButtonAsync(id, category, data));
                if (ok)
                {
                    var readBack = await Task.Run(() => _monitor.GetButtonAsync(id));
                    ok = readBack is { } r && r.Category == category && r.Data.AsSpan().SequenceEqual(data);
                }
                if (ok)
                {
                    entry.Kind = op.Kind; entry.Modifiers = op.Modifiers; entry.HidUsage = op.HidUsage;
                    table[op.Position] = entry;
                    Dispatch(row.MarkApplied);
                }
                else Dispatch(() => row.MarkFailed("Not applied — wiggle the mouse and retry"));
            }
            if (ok) okCount++;
        }

        _settings.Save();
        _monitor.SetRemaps(CurrentBindings()); // keep the poll's sentinel-verify set in sync
        Dispatch(() => win.SetButtonsStatus(okCount == ops.Count ? "Applied" : $"{okCount}/{ops.Count} applied"));
    }

    private void SetStartup(bool enable)
    {
        if (enable) _startup.Enable(); else _startup.Disable();
    }

    private void Quit()
    {
        _deviceDebounce?.Cancel();
        _deviceWatcher?.Dispose();
        _settingsWindow?.Close();
        _monitor.Dispose();
        _device.Dispose();
        _tray.Dispose();
        _app.Shutdown();
    }
}
