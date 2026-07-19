using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Startup;
using NagaBatteryTray.Ui;
using NagaBatteryTray.Ui.Dashboard;
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
    private DashboardWindow? _dashboard;
    private DashboardViewModel? _dashboardVm; // live only while _dashboard is; adoption notifications
    private DeviceChangeWatcher? _deviceWatcher;
    private CancellationTokenSource? _deviceDebounce;
    private bool _activating;

    public AppHost(Application app) => _app = app;

    public void Start()
    {
        // WPF-UI controls need its theme + controls dictionaries merged into Application.Resources
        // (no App.xaml here), or FluentWindow / ui:Button render unstyled.
        _app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        _app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        _app.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri("pack://application:,,,/Ui/Themes/DesignSystem.xaml") });
        // ApplicationThemeManager.Apply's default updateAccent:true pulls the OS accent color,
        // which would stomp the preset accent ThemeManager.Apply just pushed — so it must run first.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        ThemeManager.Apply(_app, _settings.Settings.Theme);

        _device = new RazerDevice(_settings);
        _monitor = new BatteryMonitor(_device, _settings, Dispatch);
        _tray = new TrayIconController(_monitor);

        _monitor.LowBatteryCrossed += (_, pct) => Notifications.LowBattery(pct);
        _tray.LeftClicked += TogglePopup;
        _tray.RefreshRequested += () => _ = _monitor.RefreshNowAsync();
        _tray.StartupToggled += SetStartup;
        _tray.SettingsRequested += OpenDashboard;
        _tray.QuitRequested += Quit;

        _tray.SetStartupChecked(_startup.IsEnabled());
        _tray.SetGaugeStyle(_settings.Settings.TrayIconStyle != "Text");
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
        p.SetProfile(_settings.Settings.OnboardSlot);
        p.RefreshRequested += () => _ = _monitor.RefreshNowAsync();
        p.SettingsRequested += OpenDashboard;
        _monitor.StateChanged += (_, state) => p.ApplyState(state); // live-update the popup while it's open
        return p;
    }

    private void OpenDashboard()
    {
        if (_dashboard is { IsVisible: true }) { _dashboard.Activate(); return; }

        var vm = new DashboardViewModel(_settings.Settings, _startup.IsEnabled(), WriteBindingAsync);
        var win = new DashboardWindow(vm);
        win.ApplyDpiRequested += dpi => _ = ApplyDpiAsync(vm, dpi);
        win.ProfileRefreshRequested += () => _ = RefreshProfileAsync(vm);
        vm.SwitchRequested += slot => _ = SwitchProfileAsync(vm, slot); // dropdown pick (spec §13.1)
        win.SettingsOverlayRequested += () => ShowSettingsOverlay(win, vm);
        EventHandler<DeviceState> onState = (_, state) => Dispatch(() => vm.ApplyState(state));
        _monitor.StateChanged += onState;
        win.Closed += (_, _) =>
        {
            _monitor.StateChanged -= onState; // release-on-close: don't leave the VM rooted by the app-lifetime monitor
            Interlocked.Increment(ref _gridSweep); // supersede any in-flight grid sweep promptly
            SaveDashboardSettings(vm);
            _dashboard = null; // release-on-close: idle memory returns to baseline
            _dashboardVm = null;
            _ = TrimAfterCloseAsync();
        };
        vm.ApplyState(_monitor.State);
        _dashboard = win;
        _dashboardVm = vm;
        win.Show();
        _ = SeedDashboardAsync(vm);
    }

    // Task.Run keeps the blocking HidD_*Feature calls off the UI thread (no UI freeze; supports the
    // lightweight + zero-latency invariant). Results marshal back via Dispatch.
    private async Task SeedDashboardAsync(DashboardViewModel vm)
    {
        var dpi = await Task.Run(() => _monitor.GetDpiAsync());
        Dispatch(() => vm.SetCurrentDpi(dpi));
        await RefreshProfileAsync(vm);
    }

    /// <summary>Profile card refresh (spec §13.1): the full slot inventory (0x05/0x81) plus the
    /// active-slot read (0x05/0x84), superseding the old effective-action inference. Both reads run
    /// inside one Task.Run — the sequential awaits still serialize on the monitor's lock either way.
    /// Only called on dashboard open / explicit refresh — never polled (also runs after a switch and
    /// after slot adoption). A known active slot then kicks the grid sweep, fire-and-forget so a
    /// switch in progress isn't blocked behind ~12 reads.</summary>
    private async Task RefreshProfileAsync(DashboardViewModel vm)
    {
        var (list, active) = await Task.Run(async () =>
        {
            var l = await _monitor.GetProfileListAsync();
            var a = await _monitor.GetActiveProfileAsync();
            return (l, a);
        });
        _lastActive = active;
        Dispatch(() => vm.SetProfileInventory(list?.Slots, active));
        if (active is { } a2) _ = ReadGridAsync(vm, a2);
    }

    private int _gridSweep; // sweep generation: a new sweep or dashboard close supersedes any in flight
    private byte? _lastActive; // last active-slot read result; null = unknown (see WriteBindingAsync)

    /// <summary>Grid sweep (spec §13.1): read the ACTIVE profile's 12 grid buttons (0x02/0x8c,
    /// hardware-verified) sequentially in position order, updating each chip as its read lands —
    /// hardware truth for whatever slot the mouse is on, including Synapse-configured user slots.
    /// ~0.5 s per button at the default SetReadDelayMs, so the fill is visibly progressive; each
    /// read serializes on the monitor's lock like every other pass-through (the battery poll skips
    /// while busy). Event-driven only — runs solely off RefreshProfileAsync's triggers.</summary>
    private async Task ReadGridAsync(DashboardViewModel vm, byte slot)
    {
        // eligibility BEFORE taking a generation: a straggler sweep for a closed dashboard must
        // not burn the token a just-reopened dashboard's live sweep is running under (review find)
        if (!ReferenceEquals(_dashboardVm, vm)) return;
        int gen = Interlocked.Increment(ref _gridSweep);
        bool appSlot = _settings.Settings.OnboardSlot is { } os && os == slot;
        Dispatch(() =>
        {
            for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++) vm.Callout(pos).SetPending();
        });
        for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
        {
            if (gen != Volatile.Read(ref _gridSweep) || !ReferenceEquals(_dashboardVm, vm)) return;
            int p = pos;
            var raw = await Task.Run(() => _monitor.GetButtonAsync(slot, NagaV2ProButtons.IdForPosition(p)));
            if (gen != Volatile.Read(ref _gridSweep) || !ReferenceEquals(_dashboardVm, vm)) return;
            Dispatch(() => vm.Callout(p).SetFromDevice(raw, appSlot));
        }
    }

    /// <summary>Dropdown pick: switch the mouse to ANY listed onboard slot (0x05/0x04,
    /// write-on-action) — no adopted-slot gate, since every entry comes from the device's own list —
    /// then re-read to confirm (which also sweeps the newly active slot's grid). Failure is visible
    /// on the card and snaps the dropdown back to the real active slot, never silent.</summary>
    private async Task SwitchProfileAsync(DashboardViewModel vm, byte slot)
    {
        if (_activating)
        {
            // a pick during an in-flight switch is dropped — snap the dropdown back so it
            // doesn't display a slot nobody asked the mouse to move to (review find)
            Dispatch(() => vm.ResyncSelection());
            return;
        }
        _activating = true;
        try
        {
            Dispatch(() => vm.SetProfileNote("Switching…"));
            bool ok = await Task.Run(() => _monitor.SetActiveProfileAsync(slot));
            if (!ok)
            {
                Dispatch(() =>
                {
                    vm.SetProfileNote("Couldn't switch — wiggle the mouse and retry");
                    vm.ResyncSelection();
                });
                return;
            }
            await RefreshProfileAsync(vm);
        }
        finally
        {
            _activating = false;
        }
    }

    private void ShowSettingsOverlay(DashboardWindow win, DashboardViewModel vm)
    {
        var view = new SettingsView { DataContext = vm };
        view.SetTrayIconStyle(_settings.Settings.TrayIconStyle);
        // one delegate serves both dismiss paths (✕ button and scrim click-away). CloseRequested
        // may use += because the view is new per gear click; OverlayDismissRequested lives on the
        // per-dashboard window, so it's a single-subscriber ASSIGNMENT — a += here would stack a
        // handler per gear click and save settings once per stacked handler on every dismiss.
        Action dismiss = () => { SaveDashboardSettings(vm); win.HideOverlay(); };
        view.CloseRequested += dismiss;
        win.OverlayDismissRequested = dismiss;
        view.StartupToggled += enable => { SetStartup(enable); _tray.SetStartupChecked(enable); };
        view.ThemeChanged += name =>
        { ThemeManager.Apply(_app, name); _settings.Settings.Theme = ThemeManager.Resolve(name); _settings.Save(); };
        view.TrayIconStyleChanged += style =>
        { _settings.Settings.TrayIconStyle = style; _settings.Save(); _tray.SetGaugeStyle(style != "Text"); };
        view.ResetAllRequested += () =>
        {
            // view mode (spec §13.1): the grid is displaying a FOREIGN slot's hardware truth —
            // a reset would write the app slot while the chips repaint with its factory values,
            // desyncing the display from the mouse (review find). Same rule as the chips.
            if (!vm.IsGridEditable) { view.SetResetNote("switch to the app profile to reset"); return; }
            view.SetResetNote("Resetting…");
            _ = RunResetAllAsync(view, vm);
        };
        win.ShowOverlay(view);
    }

    /// <summary>The one dashboard persist step — overlay dismiss and window close both run it,
    /// so additions (clamps, new settings) can't diverge between the two paths.</summary>
    private void SaveDashboardSettings(DashboardViewModel vm)
    {
        vm.ApplyTo(_settings.Settings);
        _settings.Save();
    }

    private async Task ApplyDpiAsync(DashboardViewModel vm, int dpi)
    {
        bool ok = await Task.Run(() => _monitor.SetDpiAsync(dpi, dpi));
        DpiSetting? readBack = ok ? await Task.Run(() => _monitor.GetDpiAsync()) : null;
        if (readBack is { } v && v.X == dpi)
        {
            Dispatch(() => vm.SetCurrentDpi(v));
        }
        else
        {
            // keep simple: on failure just re-read the current DPI
            var current = await Task.Run(() => _monitor.GetDpiAsync());
            Dispatch(() => vm.SetCurrentDpi(current));
        }
    }

    /// <summary>Returns the app-owned onboard slot (plus whether this call created it), adopting one
    /// on first use: re-creates the recorded slot if the mouse lost it (e.g. factory reset), else
    /// creates the first FREE slot — a user's existing slots are never taken or written. Every
    /// created slot is seeded with the factory map and then SWITCHED TO (spec §13.1: the grid shows
    /// the ACTIVE profile, so without the switch the write that triggered adoption would land
    /// invisibly; best-effort — a slot switch only, bottom-button parity, never a content write).
    /// Null = mouse unreachable, create refused, or no free slot.</summary>
    private async Task<(byte Slot, bool Created)?> EnsureOnboardSlotAsync()
    {
        if (await Task.Run(() => _monitor.GetProfileListAsync()) is not { } list) return null;

        if (_settings.Settings.OnboardSlot is { } recorded)
        {
            byte r = (byte)recorded;
            if (Array.IndexOf(list.Slots, r) >= 0) return (r, false);
            if (!await Task.Run(() => _monitor.CreateProfileAsync(r))) return null;
            await SeedFactoryMapAsync(r);
            await Task.Run(() => _monitor.SetActiveProfileAsync(r));
            return (r, true);
        }

        byte free = 0;
        for (byte n = 1; n <= list.Capacity; n++)
            if (Array.IndexOf(list.Slots, n) < 0) { free = n; break; }
        if (free == 0) return null; // every slot holds someone else's profile — never overwrite
        if (!await Task.Run(() => _monitor.CreateProfileAsync(free))) return null;
        await SeedFactoryMapAsync(free);
        await Task.Run(() => _monitor.SetActiveProfileAsync(free));
        _settings.Settings.OnboardSlot = free;
        _settings.Save();
        return (free, true);
    }

    /// <summary>The dashboard's instant-apply write: one binding into the app-owned slot
    /// (ensure slot → write → read-back verify → persist). Kind=Default writes the factory action
    /// and drops the table entry. Returns false on any failure (nothing persisted). When the ensure
    /// step just created (and switched to) the slot, the card + grid refresh runs AFTER the write —
    /// queuing it first would park this write behind the sweep's ~14 serialized reads.</summary>
    private async Task<bool> WriteBindingAsync(int position, ButtonActionKind kind, byte modifiers, byte usage)
    {
        if (await EnsureOnboardSlotAsync() is not { } ensured) return false;
        var (slot, created) = ensured;
        if (created)
            Dispatch(() => _dashboardVm?.SetAdoptedSlot(slot)); // the open VM was built before the slot existed

        var binding = kind == ButtonActionKind.Default
            ? NagaV2ProButtons.FactoryBindingForPosition(position)
            : new ButtonBinding(NagaV2ProButtons.IdForPosition(position), kind, modifiers, usage);
        var (category, data) = binding.ToWire();

        bool ok = await Task.Run(() => _monitor.SetButtonAsync(slot, binding.ButtonId, category, data));
        if (ok)
        {
            var readBack = await Task.Run(() => _monitor.GetButtonAsync(slot, binding.ButtonId));
            ok = readBack is { } r && r.Category == category && r.Data.AsSpan().SequenceEqual(data);
        }
        // let the card + grid catch up (event-driven, no polling) when adoption happened
        // mid-write, or when the active slot is UNKNOWN — a write that verified against an
        // existing app slot while the card says "state unknown" may have landed on an inactive
        // slot; the refresh shows where the mouse really is and view mode engages if it's
        // foreign (review find)
        if ((created || (ok && _lastActive is null)) && _dashboardVm is { } dvm)
            _ = RefreshProfileAsync(dvm);
        if (!ok) return false;

        if (kind == ButtonActionKind.Default) _settings.Settings.ButtonBindings.Remove(position);
        else _settings.Settings.ButtonBindings[position] = new ButtonBindingSetting
             { Kind = kind, Modifiers = modifiers, HidUsage = usage };
        _settings.Save();
        Dispatch(() => _popup?.SetProfile(_settings.Settings.OnboardSlot));
        return true;
    }

    /// <summary>Settings-overlay "Reset all to factory": runs the counted reset (each chip shows its
    /// own verified result too) and reports the outcome via SetResetNote, since the overlay/scrim
    /// hides the chips for the several seconds of HID I/O this takes.</summary>
    private static async Task RunResetAllAsync(SettingsView view, DashboardViewModel vm)
    {
        var (ok, failed) = await vm.ResetAllAsync();
        view.SetResetNote(failed == 0 ? $"All {ok} buttons reset" : $"{failed} failed — retry from the chips");
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

    private void SetStartup(bool enable)
    {
        if (enable) _startup.Enable(); else _startup.Disable();
    }

    private void Quit()
    {
        _deviceDebounce?.Cancel();
        _deviceWatcher?.Dispose();
        _dashboard?.Close();
        _monitor.Dispose();
        _device.Dispose();
        _tray.Dispose();
        _app.Shutdown();
    }

    /// <summary>The dashboard is heavy for a tray app: WPF holds ~60 MB of native/GC memory after the
    /// window closes, and an idle process never reclaims it (no allocation pressure → no GC, no
    /// decommit — measured steady at ~85 MB private WS vs the ~23 MB §3.1 gate). One-shot, event-driven
    /// trim: two GCs (the window is finalizable, so the first collection only queues it; the second
    /// frees it) then a working-set trim so idle RAM returns to baseline. The 2 s delay lets WPF's
    /// render thread finish teardown; skipped if the dashboard was reopened meanwhile (trimming under
    /// a live window causes a visible hiccup). The app sits in no input path, so paging cold pages
    /// out cannot add mouse latency.</summary>
    private async Task TrimAfterCloseAsync()
    {
        await Task.Delay(2000);
        if (_dashboard is not null) return; // reopened — don't trim under a live window
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
