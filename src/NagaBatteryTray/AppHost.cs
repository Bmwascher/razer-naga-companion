using System.Drawing;
using System.Windows;
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
        _app.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri("pack://application:,,,/Ui/Themes/DesignSystem.xaml") });
        ThemeManager.Apply(_app, _settings.Settings.Theme);
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
            await _monitor.RefreshNowAsync();
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

    /// <summary>Slot LED colour on the V2 Pro's bottom profile button (spec §6).</summary>
    private static string SlotColour(byte slot) => slot switch
    {
        1 => "white", 2 => "red", 3 => "green", 4 => "blue", 5 => "cyan", _ => $"slot {slot}",
    };

    /// <summary>Returns the app-owned onboard slot, adopting one on first use: re-creates the recorded
    /// slot if the mouse lost it (e.g. factory reset), else creates the first FREE slot — a user's
    /// existing slots are never taken or written. Every created slot is seeded with the factory map.
    /// Null = mouse unreachable, create refused, or no free slot.</summary>
    private async Task<byte?> EnsureOnboardSlotAsync()
    {
        if (await Task.Run(() => _monitor.GetProfileListAsync()) is not { } list) return null;

        if (_settings.Settings.OnboardSlot is { } recorded)
        {
            byte r = (byte)recorded;
            if (Array.IndexOf(list.Slots, r) >= 0) return r;
            if (!await Task.Run(() => _monitor.CreateProfileAsync(r))) return null;
            await SeedFactoryMapAsync(r);
            return r;
        }

        byte free = 0;
        for (byte n = 1; n <= list.Capacity; n++)
            if (Array.IndexOf(list.Slots, n) < 0) { free = n; break; }
        if (free == 0) return null; // every slot holds someone else's profile — never overwrite
        if (!await Task.Run(() => _monitor.CreateProfileAsync(free))) return null;
        await SeedFactoryMapAsync(free);
        _settings.Settings.OnboardSlot = free;
        _settings.Save();
        return free;
    }

    /// <summary>A just-created slot starts EMPTY — its grid buttons read back as no action (this
    /// surfaced in acceptance: "Default" restored nothingness). Seed the factory digits row once at
    /// creation so the app's profile behaves like a factory mouse before any user binding lands.
    /// Best-effort: a failed write here shows up later as that button doing nothing, and an explicit
    /// per-row Default rewrites it.</summary>
    private async Task SeedFactoryMapAsync(byte slot)
    {
        for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
        {
            var factory = NagaV2ProButtons.FactoryBindingForPosition(pos);
            var (category, data) = factory.ToWire();
            await Task.Run(() => _monitor.SetButtonAsync(slot, factory.ButtonId, category, data));
        }
    }

    /// <summary>Apply staged button ops into the app-owned ONBOARD slot (spec §5.3): adopt the slot on
    /// first use, write the binding, read-back verify, persist. "Default" writes the baked-in factory
    /// action (a fresh slot reads back empty, so there is no snapshot to restore). The mouse itself
    /// holds the bindings — they survive power-cycles with no software involvement. Every HID call
    /// runs off the UI thread via the monitor's shared lock.</summary>
    private async Task ApplyButtonsAsync(SettingsWindow win, IReadOnlyList<ButtonOp> ops)
    {
        Dispatch(() => win.SetButtonsStatus("Applying…"));

        if (await EnsureOnboardSlotAsync() is not { } slot)
        {
            // rows keep their pending state so a retry re-sends the same ops
            Dispatch(() => win.SetButtonsStatus("No onboard slot — wiggle the mouse and retry"));
            return;
        }

        var table = _settings.Settings.ButtonBindings;
        int okCount = 0;

        foreach (var op in ops)
        {
            var row = win.ButtonRow(op.Position);
            var binding = op.OpKind == ButtonOpKind.RestoreDefault
                ? NagaV2ProButtons.FactoryBindingForPosition(op.Position)
                : new ButtonBinding(NagaV2ProButtons.IdForPosition(op.Position), op.Kind, op.Modifiers, op.HidUsage);
            var (category, data) = binding.ToWire();

            bool ok = await Task.Run(() => _monitor.SetButtonAsync(slot, binding.ButtonId, category, data));
            if (ok)
            {
                var readBack = await Task.Run(() => _monitor.GetButtonAsync(slot, binding.ButtonId));
                ok = readBack is { } r && r.Category == category && r.Data.AsSpan().SequenceEqual(data);
            }
            if (ok)
            {
                if (op.OpKind == ButtonOpKind.RestoreDefault) table.Remove(op.Position);
                else table[op.Position] = new ButtonBindingSetting
                {
                    Kind = op.Kind, Modifiers = op.Modifiers, HidUsage = op.HidUsage,
                };
                Dispatch(row.MarkApplied);
                okCount++;
            }
            else Dispatch(() => row.MarkFailed("Not applied — wiggle the mouse and retry"));
        }

        _settings.Save();
        Dispatch(() => win.SetButtonsStatus(okCount == ops.Count
            ? $"Saved to onboard profile {slot} ({SlotColour(slot)}) — select it with the bottom button"
            : $"{okCount}/{ops.Count} applied"));
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
