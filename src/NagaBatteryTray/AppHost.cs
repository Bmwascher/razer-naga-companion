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
    private DashboardViewModel? _dashboardVm; // live only while _dashboard is; write-triggered card refreshes
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
        _device.PreferredModel = _settings.Settings.PreferredMouse;
        _monitor = new BatteryMonitor(_device, _settings, Dispatch);
        _tray = new TrayIconController(_monitor);

        _monitor.LowBatteryCrossed += (_, pct) => Notifications.LowBattery(pct, _monitor.State.Model);
        _monitor.StateChanged += (_, state) => OnDeviceStateChanged(state);
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

    /// <summary>Every battery state change passes the connected model through the scoped cache. A
    /// swap (either direction, including offline) drops everything the app knew about the previous
    /// mouse, supersedes any grid sweep still running for it, and re-seeds an open dashboard from
    /// the new one. Without this the app writes the old mouse's slot number to the new mouse and
    /// reports every write as failed (spec §4.6).</summary>
    private void OnDeviceStateChanged(DeviceState state)
    {
        if (!_scoped.Observe(state.Model)) return;
        Interlocked.Increment(ref _gridSweep);
        if (_dashboardVm is not { } vm) return;
        Dispatch(() => vm.ResetForNewMouse());
        _ = ReseedDeviceAsync(vm);
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
        // no profile line: v2.3 retired the app-owned-slot concept the line advertised (§13.2)
        p.RefreshRequested += () => _ = _monitor.RefreshNowAsync();
        p.SettingsRequested += OpenDashboard;
        _monitor.StateChanged += (_, state) => p.ApplyState(state); // live-update the popup while it's open
        return p;
    }

    private void OpenDashboard()
    {
        if (_dashboard is { IsVisible: true }) { _dashboard.Activate(); return; }

        var vm = new DashboardViewModel(_settings.Settings, _startup.IsEnabled(), WriteBindingAsync, RestoreRawAsync);
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
    // Task 7 puts the reachability probe in front of this call; the reload itself is ReseedDeviceAsync.
    private Task SeedDashboardAsync(DashboardViewModel vm) => ReseedDeviceAsync(vm);

    /// <summary>Reload everything that describes the CONNECTED mouse. No reachability probe: the
    /// probe reconnects when it finishes, which would re-enter OnDeviceStateChanged and loop.
    /// Both reads release the lock and can outlive the mouse they were issued against, so each
    /// carries the connection it ran under and is dropped if that connection is gone.</summary>
    private async Task ReseedDeviceAsync(DashboardViewModel vm)
    {
        int connection = _monitor.ConnectionId;
        var dpi = await Task.Run(() => _monitor.GetDpiAsync());
        if (!ReferenceEquals(_dashboardVm, vm)) return;
        // WITHIN one operation the id is the right question: this result came back after an
        // adoption, so it is not attributable and must not be painted on.
        if (_monitor.ConnectionId != connection)
        {
            // A guard that fires HANDS OFF; it never just returns. On dashboard open with a napping
            // mouse the DPI read itself re-adopts, and returning here left no DPI, no profile card
            // and no sweep, with nothing to retry until the model changed (review round 1).
            _ = RecoverFromReconnectAsync(vm);
            return;
        }
        Dispatch(() => vm.SetCurrentDpi(dpi));
        await RefreshProfileAsync(vm);
    }

    // 0 = idle. One recovery at a time: a write-stale hand-off and a sweep abort can fire together,
    // and each Reset()s under the other. No timer, no thread, no retry loop, no backoff (hard rule 1).
    private int _recovering;

    // A recovery that arrives while one is already running is COALESCED, not dropped: it sets this
    // bit and the holder runs one more pass for it. Dropping it re-opened the very mid-state the
    // hand-off exists to prevent — recovery calls ReseedDeviceAsync while holding the flag, so that
    // nested reseed's own guard could fire and lose its request with the UI already reset to blank
    // and nothing left to retry (review round 2). Many drops collapse into ONE follow-up pass, never
    // a queue: it is a single bit, cleared at the TOP of each pass, so a request arriving during a
    // pass earns one further round instead of being swallowed by that pass's own clear. It is not
    // cleared on exit, so a request landing in the release window is picked up by the next holder.
    private int _recoveryRequested;

    /// <summary>Recover from a reconnect that nothing announced. A lock-waiting battery read
    /// republishes the connection's identity FIRST; only then is it safe to reload anything, because
    /// State.Model is what keys the scoped cache and the profile names. If the model did change,
    /// OnDeviceStateChanged has already reset and reseeded, so this does not do it twice.</summary>
    private async Task RecoverFromReconnectAsync(DashboardViewModel vm)
    {
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0)
        {
            Volatile.Write(ref _recoveryRequested, 1); // the holder will run one more pass for this
            return;
        }
        try
        {
            do
            {
                // cleared BEFORE the pass runs, so this pass answers every request made so far and a
                // request made DURING it survives into the loop check below (no spin, no loss)
                Volatile.Write(ref _recoveryRequested, 0);
                string? before = _monitor.State.Model;
                await Task.Run(() => _monitor.RefreshNowWaitingAsync());
                if (!ReferenceEquals(_dashboardVm, vm)) return;
                if (string.Equals(_monitor.State.Model, before, StringComparison.Ordinal)) await ReseedDeviceAsync(vm);
            }
            while (Volatile.Read(ref _recoveryRequested) == 1);
        }
        finally { Volatile.Write(ref _recovering, 0); }
    }

    /// <summary>Profile card refresh (spec §13.1): the full slot inventory (0x05/0x81) plus the
    /// active-slot read (0x05/0x84), superseding the old effective-action inference. Both reads run
    /// inside one Task.Run — the sequential awaits still serialize on the monitor's lock either way.
    /// Only called on dashboard open / explicit refresh — never polled (also runs after a switch and
    /// after a write that resolved the slot on demand). A known active slot then kicks the grid
    /// sweep, fire-and-forget so a switch in progress isn't blocked behind ~12 reads.
    /// <para>False = it bailed on a connection change and handed off to recovery, so the caller must
    /// not leave a "working…" note on screen for a refresh that never landed.</para></summary>
    private async Task<bool> RefreshProfileAsync(DashboardViewModel vm)
    {
        int connection = _monitor.ConnectionId;
        string? model = _monitor.ConnectedModel;
        var (list, active) = await Task.Run(async () =>
        {
            var l = await _monitor.GetProfileListAsync();
            var a = await _monitor.GetActiveProfileAsync();
            return (l, a);
        });
        // a hop while the two reads were in flight: what came back describes a mouse that may no
        // longer be here, so it must not be cached or shown. Hand off to recovery rather than
        // returning — nothing else retries, and the card would sit half-built (review round 1).
        if (_monitor.ConnectionId != connection)
        {
            _ = RecoverFromReconnectAsync(vm);
            return false;
        }
        _scoped.ActiveSlot = active;
        _scoped.CreatedSlots = list?.Slots;
        // stamp the seed identity only when the seed actually landed: an empty cache carrying one
        // would tell the write path "the snapshots are current" when there are none (review round 1)
        if (list?.Slots is not null || active is not null) _scoped.SeededModel = model;
        Dispatch(() => vm.SetProfileInventory(list?.Slots, active));
        if (active is { } a2) _ = ReadGridAsync(vm, a2);
        return true;
    }

    private int _gridSweep; // sweep generation: a new sweep or dashboard close supersedes any in flight

    /// <summary>Everything cached about the CONNECTED mouse. Cleared automatically when the model
    /// changes, which is what stops the V2's slot number being written to a V3 (spec §4.6).</summary>
    private readonly DeviceScopedState _scoped = new();

    /// <summary>Grid sweep (spec §13.1): read the ACTIVE profile's 12 grid buttons (0x02/0x8c,
    /// hardware-verified) sequentially in position order, updating each chip as its read lands —
    /// hardware truth for whatever slot the mouse is on, including Synapse-configured user slots.
    /// ~50-100 ms per button via the transport's fast-read ladder (full sweep ≈ 1 s); each read
    /// serializes on the monitor's lock like every other pass-through (the battery poll skips
    /// while busy). Event-driven only — runs solely off RefreshProfileAsync's triggers.</summary>
    private async Task ReadGridAsync(DashboardViewModel vm, byte slot)
    {
        // eligibility BEFORE taking a generation: a straggler sweep for a closed dashboard must
        // not burn the token a just-reopened dashboard's live sweep is running under (review find)
        if (!ReferenceEquals(_dashboardVm, vm)) return;
        int gen = Interlocked.Increment(ref _gridSweep);
        Dispatch(() =>
        {
            for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++) vm.Callout(pos).SetPending();
        });
        // the generation counter is not a substitute: _gridSweep moves on a PUBLISHED model change, a
        // dashboard close, or a new sweep, and a mid-sweep reconnect onto another mouse publishes nothing.
        // The MODEL is what is compared here, not the id: a same-mouse reconnect (any USB event resets
        // the handle) leaves every byte read so far still true, and throwing the sweep away for it
        // would red the grid over a plugged-in headset (spec §4.10 point 3).
        string? model = _monitor.ConnectedModel;
        bool landed = false; // did ANY read come back — i.e. does a chip hold a snapshot to be wrong about
        for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
        {
            if (gen != Volatile.Read(ref _gridSweep) || !ReferenceEquals(_dashboardVm, vm)) return;
            int p = pos;
            var raw = await Task.Run(() => _monitor.GetButtonAsync(slot, NagaV2ProButtons.IdForPosition(p)));
            landed |= raw is not null;
            if (gen != Volatile.Read(ref _gridSweep) || !ReferenceEquals(_dashboardVm, vm)) return;
            if (!string.Equals(_monitor.ConnectedModel, model, StringComparison.Ordinal))
            {
                // hopped onto the other mouse mid-sweep: abandon the half-read grid
                // rather than mixing two mice in it, drop the snapshots, and start over
                _scoped.Clear();
                Dispatch(() => vm.ResetForNewMouse());
                _ = RecoverFromReconnectAsync(vm);
                return;
            }
            Dispatch(() => vm.Callout(p).SetFromDevice(raw));
        }
        // the whole grid was read off one mouse: stamp it, so a write that arrives after a later hop
        // can tell that these snapshots belong to a mouse that is no longer here. Only a sweep that
        // actually LANDED may stamp: twelve null reads leave no snapshot anywhere, and stamping for
        // them would tell the write path "the snapshots are current" when there are none — the same
        // rule RefreshProfileAsync applies to its own seed (review round 2).
        if (landed) _scoped.SeededModel = model;
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
            if (!await RefreshProfileAsync(vm))
            {
                // the confirm read bailed on a connection change and handed off to recovery. The
                // switch itself landed, but "Switching…" would sit there forever and the dropdown
                // would never resync — a guard that fires must not leave the UI mid-state.
                Dispatch(() =>
                {
                    vm.SetProfileNote("");
                    vm.ResyncSelection();
                });
            }
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
        Dispatch(() => vm.SetDpiStatus("")); // a new attempt clears the previous verdict
        bool ok = await Task.Run(() => _monitor.SetDpiAsync(dpi, dpi));
        DpiSetting? readBack = ok ? await Task.Run(() => _monitor.GetDpiAsync()) : null;
        if (readBack is { } v && v.X == dpi)
        {
            Dispatch(() =>
            {
                vm.SetCurrentDpi(v);
                vm.SetDpiStatus(""); // a verified success clears even a warning an interleaved
                                     // earlier apply posted after this one's opening clear (review find)
            });
        }
        else
        {
            // on failure re-read the current DPI so the card shows truth, and say so —
            // the card's status line (dashboard-polish §4.2); silence was a recorded gripe
            var current = await Task.Run(() => _monitor.GetDpiAsync());
            Dispatch(() =>
            {
                vm.SetCurrentDpi(current);
                vm.SetDpiStatus("Couldn't confirm — wiggle the mouse and retry");
            });
        }
    }

    /// <summary>The dashboard's instant-apply write: one modeled binding into the ACTIVE slot
    /// (resolve slot → write → read-back verify). Kind=Default writes the factory action. The
    /// firmware holds every binding — nothing is persisted app-side (spec §13.2). Returns false
    /// on any failure.</summary>
    private async Task<bool> WriteBindingAsync(int position, ButtonActionKind kind, byte modifiers, byte usage)
    {
        var binding = kind == ButtonActionKind.Default
            ? NagaV2ProButtons.FactoryBindingForPosition(position)
            : new ButtonBinding(NagaV2ProButtons.IdForPosition(position), kind, modifiers, usage);
        var (category, data) = binding.ToWire();
        return await WriteVerifiedAsync(binding.ButtonId, category, data);
    }

    /// <summary>The undo path (spec §13.2): restore a pre-overwrite snapshot byte-for-byte —
    /// including Synapse actions the app can't model. Same verified pipeline as any write.</summary>
    private Task<bool> RestoreRawAsync(int position, RawButtonAction raw) =>
        WriteVerifiedAsync(NagaV2ProButtons.IdForPosition(position), raw.Category, raw.Data);

    private async Task<bool> WriteVerifiedAsync(byte buttonId, byte category, byte[] data)
    {
        bool resolvedOnDemand = _scoped.ActiveSlot is null;
        var r = await Task.Run(() => _monitor.WriteButtonVerifiedAsync(
            _scoped.CreatedSlots, buttonId, category, data));

        if (r.Slot is { } used) _scoped.ActiveSlot = used;
        // Two ways the cache can be stale, and both land in the same branch.
        // r.ConnectionChanged: the mouse was adopted DURING the write, inside the lock — the
        // WITHIN-operation question, where the connection id is the right answer.
        // r.Model != SeededModel: a hop onto the OTHER mouse landed BEFORE the lock, between the
        // grid sweep that armed the undo snapshots and this click. The write itself is fine — it
        // went to the slot it read from the mouse in front of it — but every chip snapshot
        // describes a different mouse, and offering undo against one would restore the wrong
        // binding onto this one (hard rule 2, spec §4.10). This is the ACROSS-TIME question, so it
        // is asked of the MODEL: comparing ids here failed landed writes whenever any unrelated USB
        // event bumped the id. A null SeededModel means nothing was seeded, so there is no
        // snapshot to be wrong about.
        bool stale = r.ConnectionChanged
            || (_scoped.SeededModel is { } seeded && !string.Equals(r.Model, seeded, StringComparison.Ordinal));
        if (stale)
        {
            _scoped.Clear();
            Interlocked.Increment(ref _gridSweep);
            // void the chips' live undo snapshots in the same breath as the cache. Without this a
            // chip still inside its 5 s undo window would write the OLD mouse's raw bytes into the
            // NEW mouse's active slot and report it applied (hard rule 2, review round 1).
            if (_dashboardVm is { } changedVm)
            {
                Dispatch(() => changedVm.ResetForNewMouse());
                _ = RecoverFromReconnectAsync(changedVm);
            }
            return false;
        }
        // the slot was only just learned (the card said "state unknown"): let the card + grid
        // catch up so the other chips stop showing placeholders and gain undo snapshots
        if (r.Verified && resolvedOnDemand && _dashboardVm is { } dvm) _ = RefreshProfileAsync(dvm);
        return r.Verified;
    }

    /// <summary>Settings-overlay "Reset all to factory": runs the counted reset (each chip shows its
    /// own verified result too) and reports the outcome via SetResetNote, since the overlay/scrim
    /// hides the chips for the several seconds of HID I/O this takes.</summary>
    private static async Task RunResetAllAsync(SettingsView view, DashboardViewModel vm)
    {
        var (ok, failed) = await vm.ResetAllAsync();
        view.SetResetNote(failed == 0 ? $"All {ok} buttons reset" : $"{failed} failed — retry from the chips");
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
