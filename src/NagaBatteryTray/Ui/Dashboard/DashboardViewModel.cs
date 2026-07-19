using System.Collections.ObjectModel;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Ui.Dashboard;

public sealed class DpiPresetItem : ObservableObject
{
    private bool _isActive;
    public DpiPresetItem(int value) { Value = value; }
    public int Value { get; }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
}

/// <summary>One pill in the Profile card's slot selector (spec §13 v2.1). IsApp is fixed at
/// construction — re-marking it after an adopted-slot change means replacing the item, not
/// mutating it (see DashboardViewModel.RemarkApp). IsActive is INPC-mutable: the same pill instance
/// gets re-flagged as the mouse moves between slots without rebuilding the whole list.</summary>
public sealed class ProfileSlotItem : ObservableObject
{
    public ProfileSlotItem(byte number, bool isApp) { Number = number; IsApp = isApp; }
    public byte Number { get; }
    public bool IsApp { get; }
    public string Colour => DashboardViewModel.SlotColour(Number);
    public string Label => IsApp ? $"{Number} · {Colour} ·app" : $"{Number} · {Colour}";
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    private bool _isActive;
}

public sealed class DashboardViewModel : ObservableObject
{
    private int? _slot; // mutable: a fresh install adopts its slot mid-session (SetAdoptedSlot)
    private readonly int _seededPollSeconds, _seededPollChargingSeconds;
    private int _dpi = RazerProtocol.DpiMin;
    private bool _devicePresent, _deviceOnline, _runAtStartup, _lowBatteryNotify;
    private int _lowBatteryThreshold, _pollSeconds, _pollChargingSeconds;
    private string _headerSubtitle = "", _batteryChipText = "—", _statusDotBrushKey = "Status.Critical";
    private string _profileTitle = "", _profileDetail = "", _theme = "Porcelain";

    public DashboardViewModel(AppSettings source, bool runAtStartup, CalloutViewModel.WriteBinding write)
    {
        _slot = source.OnboardSlot;
        _runAtStartup = runAtStartup;
        _lowBatteryThreshold = source.LowBatteryThreshold;
        _lowBatteryNotify = source.LowBatteryNotify;
        _pollSeconds = _seededPollSeconds = source.PollIntervalSeconds;
        _pollChargingSeconds = _seededPollChargingSeconds = source.PollIntervalChargingSeconds;
        _theme = source.Theme;

        var callouts = new List<CalloutViewModel>(NagaV2ProButtons.Count);
        for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
        {
            var c = new CalloutViewModel(pos, write);
            if (source.ButtonBindings.TryGetValue(pos, out var b))
                c.SetApplied(b.Kind, b.Modifiers, b.HidUsage);
            callouts.Add(c);
        }
        Callouts = callouts;
        foreach (var c in callouts)
            c.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CalloutViewModel.IsCapturing))
                    AnyCapturing = Callouts.Any(x => x.IsCapturing);
            };

        Presets = new ObservableCollection<DpiPresetItem>();
        foreach (int v in source.DpiPresets.Distinct().OrderBy(v => v)) Presets.Add(new DpiPresetItem(v));
        RefreshProfileText();
    }

    // ---- callouts ----
    public IReadOnlyList<CalloutViewModel> Callouts { get; }
    public CalloutViewModel Callout(int position) => Callouts[position - 1];

    /// <summary>True while any grid button is capturing a key — drives the stage's
    /// "arming" glow boost.</summary>
    public bool AnyCapturing { get => _anyCapturing; private set => Set(ref _anyCapturing, value); }
    private bool _anyCapturing;

    /// <summary>Settings-overlay "Reset all to factory": Default-write every grid button in position
    /// order via each chip's own instant-apply pipeline, counting verified successes (Status ==
    /// "Applied") vs failures — so the caller can report a countable outcome instead of silence
    /// while the several seconds of HID I/O run behind the overlay/scrim.</summary>
    public async Task<(int Ok, int Failed)> ResetAllAsync()
    {
        int ok = 0, failed = 0;
        foreach (var c in Callouts)
        {
            // count the write's own verdict, not the display Status string — a busy chip's
            // in-flight write would otherwise leave a stale "Applied"/"Writing…" miscount
            if (await c.DefaultAsync()) ok++; else failed++;
        }
        return (ok, failed);
    }

    // ---- header ----
    public bool DeviceOnline { get => _deviceOnline; private set => Set(ref _deviceOnline, value); }
    public string StatusDotBrushKey { get => _statusDotBrushKey; private set => Set(ref _statusDotBrushKey, value); }
    public string HeaderSubtitle { get => _headerSubtitle; private set => Set(ref _headerSubtitle, value); }
    public string BatteryChipText { get => _batteryChipText; private set => Set(ref _batteryChipText, value); }

    public void ApplyState(DeviceState s)
    {
        DeviceOnline = s.Status == DeviceStatus.Online;
        StatusDotBrushKey = DeviceOnline ? "Status.Positive" : "Status.Critical";
        // link state only - slot identity lives solely in the Profile card (it was shown twice)
        HeaderSubtitle = !DeviceOnline ? "offline" : s.Wired ? "Wired" : "Wireless";
        BatteryChipText = DeviceOnline ? $"{s.Percent}%{(s.Charging ? " ⚡" : "")}" : "—";
    }

    internal static string SlotColour(int slot) => slot switch
    { 1 => "white", 2 => "red", 3 => "green", 4 => "blue", 5 => "cyan", _ => $"#{slot}" };

    // ---- DPI ----
    public bool DevicePresent { get => _devicePresent; set => Set(ref _devicePresent, value); }
    public int Dpi
    {
        get => _dpi;
        set { if (Set(ref _dpi, Math.Clamp(value, RazerProtocol.DpiMin, RazerProtocol.DpiMax)))
              { Notify(nameof(DpiText)); Notify(nameof(DpiSliderPos)); RefreshActive(); } }
    }
    public string DpiText => DevicePresent ? Dpi.ToString() : "—";

    /// <summary>Perceptual log-scale surface for the DPI slider: 0.0 at 100 DPI, 1.0 at 30000 DPI
    /// (a 300x range), so equal slider travel feels like equal proportional DPI change instead of
    /// the low end being unusably cramped on a linear scale. The setter snaps to the nearest 50,
    /// matching the granularity DPI is normally adjusted in.</summary>
    public double DpiSliderPos
    {
        get => Math.Log(Dpi / 100.0) / Math.Log(300.0);
        set => Dpi = (int)Math.Round(100.0 * Math.Pow(300.0, value) / 50.0) * 50;
    }

    public void SetCurrentDpi(DpiSetting? dpi)
    {
        DevicePresent = dpi is not null;
        if (dpi is { } d) Dpi = d.X;
        Notify(nameof(DpiText));
        RefreshActive();
    }

    public ObservableCollection<DpiPresetItem> Presets { get; }

    public void AddPreset(int value)
    {
        value = Math.Clamp(value, RazerProtocol.DpiMin, RazerProtocol.DpiMax);
        if (Presets.Any(p => p.Value == value)) return;
        int at = 0;
        while (at < Presets.Count && Presets[at].Value < value) at++;
        Presets.Insert(at, new DpiPresetItem(value));
        RefreshActive();
    }

    public void RemovePreset(DpiPresetItem item)
    {
        Presets.Remove(item);
        RefreshActive(); // removing the current value's preset re-enables "save"
    }

    /// <summary>Enables the "+ Save N" button: the current DPI is real and not already a preset.</summary>
    public bool CanSavePreset { get => _canSavePreset; private set => Set(ref _canSavePreset, value); }
    private bool _canSavePreset;

    private void RefreshActive()
    {
        foreach (var p in Presets) p.IsActive = DevicePresent && p.Value == Dpi;
        CanSavePreset = DevicePresent && !Presets.Any(p => p.Value == Dpi);
    }

    // ---- profile card: slot selector (spec §13 v2.1) ----
    private byte? _activeSlot; // last active-slot read (0x05/0x84); null = never known / currently unreachable
    private bool _profileChecked; // true once SetProfileInventory has run at least once
    public string ProfileTitle { get => _profileTitle; private set => Set(ref _profileTitle, value); }
    public string ProfileDetail { get => _profileDetail; private set => Set(ref _profileDetail, value); }

    /// <summary>Every existing onboard slot (from the profile list, 0x05/0x81), ascending, each
    /// flagging whether it's the currently active slot (0x05/0x84) and/or the app's adopted slot.
    /// Any pill is click-to-switch (0x05/0x04) — switching persists across power-cycles (spec §12),
    /// so it's safe to offer freely, not just when we've confirmed the mouse is elsewhere.</summary>
    public ObservableCollection<ProfileSlotItem> ProfileSlots { get; } = new();

    /// <summary>First remap on a fresh install adopts a slot AFTER this VM was built — update the
    /// identity the Profile card renders and re-mark which pill carries the "app" badge. Detail is
    /// recomputed from whatever active-slot knowledge we already have: the mouse's actual active
    /// slot doesn't change just because the app adopted a new one, so there's no need to discard it.</summary>
    public void SetAdoptedSlot(int slot)
    {
        if (_slot == slot) return;
        _slot = slot;
        RemarkApp();
        RefreshProfileText();
    }

    /// <summary>Feed the card a fresh profile-list (0x05/0x81) + active-slot (0x05/0x84) read (either
    /// null = unreachable). Drives the whole card: the pill list, which pill is active, and the
    /// title/detail text.</summary>
    public void SetProfileInventory(byte[]? slots, byte? active)
    {
        _profileChecked = true;
        if (slots is not null)
        {
            _activeSlot = active;
            RebuildSlots(slots, active);
        }
        else if (active is not null && ProfileSlots.Count > 0)
        {
            // partial read: the list call failed but the active-slot call succeeded and we already
            // have pills from a prior read — keep them and just re-flag which one is active, rather
            // than forcing the card back to "state unknown" over one failed half of the pair (review find)
            _activeSlot = active;
            foreach (var p in ProfileSlots) p.IsActive = p.Number == active;
        }
        else
        {
            _activeSlot = null;
            foreach (var p in ProfileSlots) p.IsActive = false; // keep last-known pills, unmark active
        }
        RefreshProfileText();
    }

    /// <summary>Transient card status ("Switching…", failure text) — detail line only.</summary>
    public void SetProfileNote(string note) => ProfileDetail = note;

    private void RebuildSlots(byte[] slots, byte? active)
    {
        ProfileSlots.Clear();
        foreach (byte n in slots.OrderBy(n => n))
            ProfileSlots.Add(new ProfileSlotItem(n, isApp: _slot == n) { IsActive = active == n });
    }

    /// <summary>IsApp is fixed at construction (see ProfileSlotItem), so re-marking after an adopted-
    /// slot change means replacing the affected pill instances rather than mutating them in place.</summary>
    private void RemarkApp()
    {
        for (int i = 0; i < ProfileSlots.Count; i++)
        {
            var old = ProfileSlots[i];
            bool isApp = old.Number == _slot;
            if (isApp != old.IsApp)
                ProfileSlots[i] = new ProfileSlotItem(old.Number, isApp) { IsActive = old.IsActive };
        }
    }

    /// <summary>Title always names the currently-known active slot once we have one — that's real
    /// information whether or not the app has adopted a slot. Detail states the app's relationship
    /// to it (live / elsewhere / n/a); falls back to identity-only text before the first check or
    /// once a check comes back unreachable.</summary>
    private void RefreshProfileText()
    {
        if (_activeSlot is not { } a)
        {
            (ProfileTitle, ProfileDetail) = _slot is { } n
                ? ($"Slot {n} · {SlotColour(n)}", _profileChecked ? "state unknown — mouse unreachable" : "")
                : ("No app profile yet", "Remap any button to create one.");
            return;
        }
        ProfileTitle = $"Slot {a} · {SlotColour(a)}";
        ProfileDetail = _slot is not { } appSlot ? ""
            : a == appSlot ? "● app profile active"
            : $"○ remaps live on Slot {appSlot} · {SlotColour(appSlot)}";
    }

    // ---- settings (overlay) ----
    public bool RunAtStartup { get => _runAtStartup; set => Set(ref _runAtStartup, value); }
    public bool LowBatteryNotify { get => _lowBatteryNotify; set => Set(ref _lowBatteryNotify, value); }
    public int LowBatteryThreshold { get => _lowBatteryThreshold; set => Set(ref _lowBatteryThreshold, value); }
    public int PollSeconds { get => _pollSeconds; set => Set(ref _pollSeconds, value); }
    public int PollChargingSeconds { get => _pollChargingSeconds; set => Set(ref _pollChargingSeconds, value); }
    public string Theme { get => _theme; set => Set(ref _theme, value); }
    public IReadOnlyList<string> ThemeNames => Ui.ThemeManager.PresetNames;

    /// <summary>Ports the old SettingsViewModel.ApplyTo clamps: cadence floor 15 s, threshold 1..100.
    /// The floor only clamps values the USER changed — an untouched cadence passes through as
    /// seeded, so the documented hand-edited-settings.json sub-15 bypass survives the dashboard's
    /// unconditional save-on-close (review find).</summary>
    public void ApplyTo(AppSettings target)
    {
        target.LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 1, 100);
        target.LowBatteryNotify = LowBatteryNotify;
        target.PollIntervalSeconds =
            PollSeconds == _seededPollSeconds ? _seededPollSeconds : Math.Max(15, PollSeconds);
        target.PollIntervalChargingSeconds =
            PollChargingSeconds == _seededPollChargingSeconds ? _seededPollChargingSeconds : Math.Max(15, PollChargingSeconds);
        target.Theme = Ui.ThemeManager.Resolve(Theme);
        target.DpiPresets = Presets.Select(p => p.Value).ToList();
    }

}
