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

    /// <summary>Last known run-at-login state. StartupRegistration.IsEnabled shells out to
    /// schtasks.exe — a process spawn, ~25 ms measured — and it used to be awaited on the UI thread
    /// both before the tray icon appeared and before the dashboard window was constructed. It is
    /// display-only (the scheduled task itself is the source of truth, and nothing writes it back
    /// from here), so seeding from this and correcting when the real query lands is as correct as
    /// blocking for it, and two UI-thread stalls cheaper.</summary>
    private bool _startupEnabled;

    /// <summary>Bumped by every toggle, so an in-flight query can tell that its answer is already
    /// stale. Without it a refresh issued BEFORE a toggle publishes the old value on top of the new
    /// one - a ~25 ms window, longer when schtasks.exe spawns cold at logon - and the tray check and
    /// the overlay switch both show the state the user just changed away from until the next
    /// dashboard open (Fable diff r1, F6).</summary>
    private int _startupGeneration;

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

        _tray.SetGaugeStyle(_settings.Settings.TrayIconStyle != "Text");
        _tray.Show();
        RefreshStartupStateAsync();   // the menu is closed, so nothing is on screen to be wrong yet

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

        var vm = new DashboardViewModel(_settings.Settings, _startupEnabled, WriteBindingAsync, RestoreRawAsync);
        var win = new DashboardWindow(vm);
        win.ApplyDpiRequested += dpi => _ = ApplyDpiAsync(vm, dpi);
        win.ProfileRefreshRequested += () => _ = RefreshProfileAsync(vm);
        vm.SwitchRequested += slot => _ = SwitchProfileAsync(vm, slot); // dropdown pick (spec §13.1)
        vm.MouseSelectRequested += name => _ = SelectMouseAsync(vm, name);
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
        RefreshStartupStateAsync();   // picks up a task someone changed outside the app
    }

    // Task.Run keeps the blocking HidD_*Feature calls off the UI thread (no UI freeze; supports the
    // lightweight + zero-latency invariant). Results marshal back via Dispatch.
    private async Task SeedDashboardAsync(DashboardViewModel vm)
    {
        // Task.Run is load-bearing, not decoration: DiscoverMiceAsync takes the read lock, and when
        // the lock is free the wait completes synchronously, so enumeration, CreateFile and the first
        // feature reports would run on the UI thread this was called from (hard rule 1)
        //
        // Discovery ends in a battery read, so it can publish a model change from inside itself, and
        // OnDeviceStateChanged then resets the UI and starts its own reseed. The sweep generation is
        // the record of that: OnDeviceStateChanged bumps it in the same breath as the reseed it
        // starts, so an unmoved generation means nothing reseeded underneath us and this open owes
        // the device its one seed. Reseeding anyway would run two DPI reads, two profile refreshes
        // and two twelve-read sweeps against a mouse in the user's hand (hard rule 1). No new flag:
        // the counter that supersedes duplicated sweeps already says exactly this.
        int sweep = Volatile.Read(ref _gridSweep);
        var mice = await Task.Run(() => _monitor.DiscoverMiceAsync());
        if (!ReferenceEquals(_dashboardVm, vm)) return; // closed while the sweep ran — nothing to paint
        Dispatch(() => vm.SetMice(mice, _monitor.State.Model));
        if (Volatile.Read(ref _gridSweep) == sweep) await ReseedDeviceAsync(vm);
    }

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
    // and each Reset()s under the other. No timer, no thread, no backoff, and AT MOST ONE follow-up
    // pass per acquisition — never an unbounded retry loop (hard rule 1).
    private int _recovering;

    // A recovery that arrives while one is already running is COALESCED, not dropped: it sets this
    // bit and the holder runs at most ONE more pass for it. Dropping it re-opened the very mid-state
    // the hand-off exists to prevent — recovery calls ReseedDeviceAsync while holding the flag, so
    // that nested reseed's own guard could fire and lose its request with the UI already reset to
    // blank and nothing left to retry (review round 2). Many requests collapse into that one
    // follow-up, never a queue: it is a single bit, cleared at the TOP of each pass.
    //
    // The follow-up is HARD-CAPPED at one because the pass generates its own requests: it calls
    // ReseedDeviceAsync, whose connection-id guard calls straight back in here and re-sets the bit
    // after the clear. Under a flapping link (V3 cable pulled — the acceptance scenario) an
    // unbounded loop would re-run refresh + reseed + the twelve-read sweep with no cap and no delay,
    // which is continuous HID traffic against a mouse in the user's hand (hard rule 1).
    //
    // Two residuals are ACCEPTED, not fixed. (1) A request landing between the loop's last check and
    // the flag release is DROPPED: the bit stays set, but the next holder clears it at the top of its
    // own pass before reading it, so that request contributes nothing on its own. (2) An exception
    // escaping the pass releases the flag with the bit possibly set and no follow-up, and every call
    // site is `_ = Recover…`, so the exception is swallowed into an unobserved Task. In both cases
    // the dashboard can sit mid-state — blank, via the sweep-abort path — until the next device
    // change, battery reselect, or dashboard reopen recovers it. Closing them needs a post-release
    // re-check plus its own re-entry guard: more moving parts than these failures justify in a file
    // whose first rule is to stay small and quiet.
    private int _recoveryRequested;

    /// <summary>Recover from a reconnect that nothing announced. A lock-waiting battery read
    /// republishes the connection's identity FIRST; only then is it safe to reload anything, because
    /// State.Model is what keys the scoped cache and the profile names. If the model did change,
    /// OnDeviceStateChanged has already reset and reseeded, so this does not do it twice.</summary>
    private async Task RecoverFromReconnectAsync(DashboardViewModel vm)
    {
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0)
        {
            Volatile.Write(ref _recoveryRequested, 1); // the holder will run at most one more pass
            return;
        }
        try
        {
            // pass 0 = this acquisition's own work; pass 1 = the single follow-up a request earns.
            // If the bit is STILL set after the follow-up it is left set and we exit — no third pass.
            for (int pass = 0; pass < 2; pass++)
            {
                // cleared BEFORE the pass runs, so this pass answers every request made so far and a
                // request made DURING it survives into the check below (no spin, no loss)
                Volatile.Write(ref _recoveryRequested, 0);
                string? before = _monitor.State.Model;
                await Task.Run(() => _monitor.RefreshNowWaitingAsync());
                if (!ReferenceEquals(_dashboardVm, vm)) return;
                if (string.Equals(_monitor.State.Model, before, StringComparison.Ordinal)) await ReseedDeviceAsync(vm);
                if (Volatile.Read(ref _recoveryRequested) == 0) return;
            }
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

    private bool _selecting; // one pick at a time — same shape as _activating, and for the same reason

    /// <summary>Header dropdown pick (spec §4.5): remember the choice, reconnect, and re-seed. The
    /// new mouse holds entirely different DPI, profiles and bindings, so nothing from the old one
    /// survives — OnDeviceStateChanged clears the cache as soon as the model change lands.</summary>
    private async Task SelectMouseAsync(DashboardViewModel vm, string name)
    {
        if (_selecting)
        {
            // a pick during an in-flight pick is DROPPED, never queued: two picks would run two
            // waiting refreshes, two discovery sweeps and two full seeds, and the loser would then
            // judge reachability by a State.Model the winner had already moved — reporting a mouse
            // that IS connected as unreachable. Snap the box back so it doesn't display a mouse
            // nobody asked the app to move to (the SwitchProfileAsync idiom).
            Dispatch(() => vm.ResyncMouse());
            return;
        }
        _selecting = true;
        try
        {
            _settings.Settings.PreferredMouse = name;
            _settings.Save();
            _monitor.PreferredMouse = name;

            // WAITS for the read lock: the ordinary refresh skips when a read is in flight, which would
            // report the mouse the user just picked as unreachable (spec §4.5). Task.Run for the same
            // reason as in SeedDashboardAsync - an uncontended lock keeps the HID work on this thread.
            await Task.Run(() => _monitor.RefreshNowWaitingAsync());
            // the dashboard can close while the lock wait runs; everything below paints a view model
            // and sweeps the device for it, so a dead one must not get a verdict or a seed
            if (!ReferenceEquals(_dashboardVm, vm)) return;

            if (!string.Equals(_monitor.State.Model, name, StringComparison.Ordinal))
            {
                Dispatch(() =>
                {
                    vm.SetProfileNote("Couldn't reach that mouse — wiggle it and retry");
                    vm.ResyncMouse();
                });
                return;
            }
            await SeedDashboardAsync(vm);
        }
        finally
        {
            _selecting = false;
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
    /// firmware holds every binding — nothing is persisted app-side (spec §13.2). Returns the
    /// outcome, so a refusal can be told from a failure.</summary>
    private async Task<WriteOutcome> WriteBindingAsync(int position, ButtonActionKind kind, byte modifiers, byte usage)
    {
        var binding = kind == ButtonActionKind.Default
            ? NagaV2ProButtons.FactoryBindingForPosition(position)
            : new ButtonBinding(NagaV2ProButtons.IdForPosition(position), kind, modifiers, usage);
        var (category, data) = binding.ToWire();
        return await WriteVerifiedAsync(binding.ButtonId, category, data);
    }

    /// <summary>The undo path (spec §13.2): restore a pre-overwrite snapshot byte-for-byte —
    /// including Synapse actions the app can't model. Same verified pipeline as any write.</summary>
    private Task<WriteOutcome> RestoreRawAsync(int position, RawButtonAction raw) =>
        WriteVerifiedAsync(NagaV2ProButtons.IdForPosition(position), raw.Category, raw.Data);

    private async Task<WriteOutcome> WriteVerifiedAsync(byte buttonId, byte category, byte[] data)
    {
        bool resolvedOnDemand = _scoped.ActiveSlot is null;
        // SeededModel goes DOWN with the write: it is the mouse the cache and every chip's undo
        // snapshot describe, and the monitor refuses the write outright if that is not the mouse
        // answering inside the lock. That refusal is what PREVENTS the wrong bytes landing on the
        // wrong mouse (hard rule 2); the staleness test below only REPORTS.
        var r = await Task.Run(() => _monitor.WriteButtonVerifiedAsync(
            _scoped.CreatedSlots, _scoped.SeededModel, buttonId, category, data));

        if (r.Slot is { } used) _scoped.ActiveSlot = used;
        // Two ways the write's result can be untrustworthy, and both land in the same branch. This
        // is the REPORTING half — by the time it runs the device work is over, so its job is to
        // clean up: drop the cache, void the undo snapshots, and hand off to recovery.
        // r.Refused: the monitor compared the expected model against the mouse answering — on BOTH
        // sides of its slot read — and issued nothing. Read from the flag rather than inferred from
        // r.Model, because the model-change handler can null SeededModel between the write and this
        // line, and the arm below would then miss it and report a refusal as a plain failure.
        // r.ConnectionChanged: the mouse was adopted DURING the write, inside the lock. An adoption
        // caught before any byte leaves is refused above instead; what reaches here is one that
        // landed once the write was already on the wire, which no pre-write check can undo. But the
        // id rises on a SAME-MODEL reconnect too — any USB blip drops the handle and the next call
        // re-adopts the very same mouse — and that is not a mouse change: the cache and every undo
        // snapshot still describe the mouse in front of the user, and a bulk reset may safely carry
        // on. All it means there is that the write could not be confirmed, which the monitor already
        // reports as Verified=false, i.e. an ordinary Failed whose "Not applied" copy is truthful.
        // So the adoption counts as a CHANGE only when the mouse it landed on is a different model —
        // asked of the MODEL, against the one connected now, because r.Model is the ENTRY model and
        // says nothing about what displaced it (ReadGridAsync applies the same rule mid-sweep).
        // r.Model != SeededModel: a hop onto the OTHER mouse landed BEFORE the lock, between the
        // grid sweep that armed the undo snapshots and this click. The monitor refuses that write
        // rather than issuing it, so this arm is the same verdict read from the outside — and it
        // still has to run, because it is what clears the stale cache and the snapshots. This is
        // the ACROSS-TIME question, so it is asked of the MODEL: comparing ids here failed landed
        // writes whenever any unrelated USB event bumped the id. A null SeededModel means nothing
        // was seeded, so there is no snapshot to be wrong about.
        bool hopped = r.ConnectionChanged
            && !string.Equals(_monitor.ConnectedModel, r.Model, StringComparison.Ordinal);
        bool stale = r.Refused || hopped
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
                Dispatch(() =>
                {
                    changedVm.ResetForNewMouse();
                    // ResetForNewMouse blanks the card; this says WHY, on the one status line the
                    // compact chip row does not have. Nothing failed and nothing was written, so the
                    // user must not be told to wiggle a mouse that is working. The reseed's own
                    // inventory read clears it again.
                    changedVm.SetProfileNote(CalloutViewModel.MouseChangedText);
                });
                _ = RecoverFromReconnectAsync(changedVm);
            }
            return WriteOutcome.MouseChanged;
        }
        // the known inventory does not list the active slot, so the monitor issued nothing. Same
        // class as the refusal above — nothing failed and nothing landed — but it is the mouse the
        // user is looking at, so it gets its own words instead of borrowing "Mouse changed", and
        // nothing is cleared or reseeded: the cache and every undo snapshot are still true. The
        // words go on the profile card for the same reason the mouse-change ones do — the chip row
        // shows Status only on a red chip, and this is deliberately not one.
        if (r.SlotMissing)
        {
            if (_dashboardVm is { } missingVm)
                Dispatch(() => missingVm.SetProfileNote(CalloutViewModel.SlotMissingText));
            return WriteOutcome.SlotMissing;
        }
        // the slot was only just learned (the card said "state unknown"): let the card + grid
        // catch up so the other chips stop showing placeholders and gain undo snapshots
        if (r.Verified && resolvedOnDemand && _dashboardVm is { } dvm) _ = RefreshProfileAsync(dvm);
        return r.Verified ? WriteOutcome.Applied : WriteOutcome.Failed;
    }

    /// <summary>Settings-overlay "Reset all to factory": runs the counted reset (each chip shows its
    /// own verified result too) and reports the outcome via SetResetNote, since the overlay/scrim
    /// hides the chips for the several seconds of HID I/O this takes.
    /// <para>An aborted run gets its own note: the mouse changed partway, so the run STOPPED, and the
    /// user is owed what did and did not change rather than a tally that reads like a full pass.</para></summary>
    private static async Task RunResetAllAsync(SettingsView view, DashboardViewModel vm)
    {
        var (ok, failed, skipped) = await vm.ResetAllAsync();
        view.SetResetNote(
            skipped > 0 ? $"Mouse changed — stopped: {ok} reset, {failed + skipped} not applied"
            : failed == 0 ? $"All {ok} buttons reset"
            : $"{failed} failed — retry from the chips");
    }

    private void SetStartup(bool enable)
    {
        if (enable) _startup.Enable(); else _startup.Disable();
        _startupEnabled = enable;
        Interlocked.Increment(ref _startupGeneration); // supersede any query already in flight
    }

    /// <summary>Ask Windows whether the logon task exists, off the UI thread, and publish the answer
    /// to whatever is currently showing it. Fire-and-forget and event-driven — one query per startup
    /// and per dashboard open, never a timer or a poll (hard rule 1). A failure leaves the last known
    /// value in place, which is what IsEnabled's own false-on-error would have shown anyway.</summary>
    private void RefreshStartupStateAsync() => _ = Task.Run(() =>
    {
        int generation = Volatile.Read(ref _startupGeneration);
        bool on;
        try { on = _startup.IsEnabled(); } catch { return; }
        // Quit disposes the tray and shuts the dispatcher down, and this query can still be in
        // flight when it does. A one-shot display refresh must not raise on the way out.
        try
        {
            Dispatch(() =>
            {
                // a toggle landed while this query ran: the user's own action is the newer truth,
                // and this answer describes the task before it changed
                if (Volatile.Read(ref _startupGeneration) != generation) return;
                _startupEnabled = on;
                _tray.SetStartupChecked(on);
                if (_dashboardVm is { } vm) vm.RunAtStartup = on;
            });
        }
        catch { /* shutting down; there is nothing left to tell */ }
    });

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
