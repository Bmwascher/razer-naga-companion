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

/// <summary>One entry in the Profile card's slot dropdown (spec §13.2 v2.3 — no slot is special
/// anymore, so an entry is just number + colour). Name is the app-side label (dashboard-polish
/// §5.4; the firmware stores no names) — default "Slot N", user-renameable. IsActive and Name are
/// INPC-mutable: the same item instance gets re-flagged/renamed without rebuilding the list.</summary>
public sealed class ProfileSlotItem : ObservableObject
{
    public ProfileSlotItem(byte number, string? name = null) { Number = number; _name = name ?? DefaultName(number); }
    public byte Number { get; }
    public string Colour => DashboardViewModel.SlotColour(Number);
    public static string DefaultName(byte number) => $"Slot {number}";
    public string Name { get => _name; set => Set(ref _name, value); }
    private string _name;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    private bool _isActive;
}

public sealed class DashboardViewModel : ObservableObject
{
    private readonly int _seededPollSeconds, _seededPollChargingSeconds;
    private int _dpi = RazerProtocol.DpiMin;
    private bool _devicePresent, _deviceOnline, _runAtStartup, _lowBatteryNotify;
    private int _lowBatteryThreshold, _pollSeconds, _pollChargingSeconds;
    private string _headerSubtitle = "", _batteryChipText = "—", _statusDotBrushKey = "Status.Critical";
    private string _profileDetail = "", _theme = "Porcelain";

    public DashboardViewModel(AppSettings source, bool runAtStartup,
        CalloutViewModel.WriteBinding write, CalloutViewModel.WriteRaw writeRaw)
    {
        _runAtStartup = runAtStartup;
        _lowBatteryThreshold = source.LowBatteryThreshold;
        _lowBatteryNotify = source.LowBatteryNotify;
        _pollSeconds = _seededPollSeconds = source.PollIntervalSeconds;
        _pollChargingSeconds = _seededPollChargingSeconds = source.PollIntervalChargingSeconds;
        _theme = source.Theme;
        _profileNames = new Dictionary<byte, string>(source.ProfileNames);

        // chips start on the factory display and adopt hardware truth when the first sweep
        // lands (~1 s; spec §13.2 — the settings table no longer seeds or records bindings)
        var callouts = new List<CalloutViewModel>(NagaV2ProButtons.Count);
        for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
            callouts.Add(new CalloutViewModel(pos, write, writeRaw));
        Callouts = callouts;
        foreach (var c in callouts)
            c.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CalloutViewModel.IsCapturing))
                    AnyCapturing = Callouts.Any(x => x.IsCapturing);
            };

        Presets = new ObservableCollection<DpiPresetItem>();
        foreach (int v in source.DpiPresets.Distinct().OrderBy(v => v)) Presets.Add(new DpiPresetItem(v));
        RefreshProfileDetail();
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

    /// <summary>DPI card status line (dashboard-polish §4.2): "" = hidden; AppHost sets the
    /// couldn't-confirm text on a failed apply and clears it at the start of the next one — the
    /// surface the old Settings window had and the card lost (recorded gripe).</summary>
    public string DpiStatus { get => _dpiStatus; private set => Set(ref _dpiStatus, value); }
    private string _dpiStatus = "";
    public void SetDpiStatus(string status) => DpiStatus = status;

    private void RefreshActive()
    {
        foreach (var p in Presets) p.IsActive = DevicePresent && p.Value == Dpi;
        CanSavePreset = DevicePresent && !Presets.Any(p => p.Value == Dpi);
    }

    // ---- profile card: slot dropdown (spec §13.2 v2.3) ----
    private byte? _activeSlot; // last active-slot read (0x05/0x84); null = never known / currently unreachable
    private bool _profileChecked; // true once SetProfileInventory has run at least once
    private ProfileSlotItem? _selectedSlot;
    private bool _syncingSelection; // guards programmatic selection writes from raising SwitchRequested
    public string ProfileDetail
    {
        get => _profileDetail;
        private set { if (Set(ref _profileDetail, value)) Notify(nameof(ShowLedCaption)); }
    }

    /// <summary>User picked a slot in the dropdown — AppHost switches the mouse (0x05/0x04).</summary>
    public event Action<byte>? SwitchRequested;

    /// <summary>Every existing onboard slot (from the profile list, 0x05/0x81), ascending, each
    /// flagging whether it's the currently active slot (0x05/0x84). Any slot is pick-to-switch
    /// (0x05/0x04) — switching persists across power-cycles (spec §12) and every slot is equally
    /// editable in place (spec §13.2), so nothing is special-cased.</summary>
    public ObservableCollection<ProfileSlotItem> ProfileSlots { get; } = new();

    /// <summary>Two-way ComboBox binding. The selection ALWAYS mirrors the mouse's actual active
    /// slot: programmatic sync (inventory reads, failed-switch resync) runs under a guard, so only
    /// a USER pick raises SwitchRequested. Re-picking the active slot is a no-op.</summary>
    public ProfileSlotItem? SelectedProfileSlot
    {
        get => _selectedSlot;
        set
        {
            if (!Set(ref _selectedSlot, value)) return;
            Notify(nameof(ShowLedCaption));
            if (_syncingSelection || value is null) return;
            if (_activeSlot == value.Number) return;
            SwitchRequested?.Invoke(value.Number);
        }
    }

    /// <summary>Steady-state caption row under the dropdown (dashboard-polish §5.5): the active
    /// slot's LED colour dot + name. A transient note (Switching…/failure/unreachable) or an empty
    /// selection takes the line instead.</summary>
    public bool ShowLedCaption => _profileDetail.Length == 0 && _selectedSlot is not null;

    /// <summary>Feed the card a fresh profile-list (0x05/0x81) + active-slot (0x05/0x84) read (either
    /// null = unreachable). Drives the dropdown items, which one is selected, and the detail text.</summary>
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
            // have items from a prior read — keep them and just re-flag which one is active, rather
            // than forcing the card back to "state unknown" over one failed half of the pair (review find)
            _activeSlot = active;
            foreach (var p in ProfileSlots) p.IsActive = p.Number == active;
        }
        else
        {
            _activeSlot = null;
            foreach (var p in ProfileSlots) p.IsActive = false; // keep last-known items, unmark active
        }
        SyncSelection();
        RefreshProfileDetail();
    }

    /// <summary>Transient card status ("Switching…", failure text) — detail line only.</summary>
    public void SetProfileNote(string note) => ProfileDetail = note;

    /// <summary>Snap the dropdown back to the last-known active slot after a failed switch —
    /// the dropdown never lies about where the mouse is.</summary>
    public void ResyncSelection() => SyncSelection();

    private void SyncSelection()
    {
        CancelRename(); // the selection is retargeting — a draft must never land on a different slot
        _syncingSelection = true;
        try { SelectedProfileSlot = ProfileSlots.FirstOrDefault(p => p.Number == _activeSlot); }
        finally { _syncingSelection = false; }
    }

    private void RebuildSlots(byte[] slots, byte? active)
    {
        ProfileSlots.Clear();
        foreach (byte n in slots.OrderBy(n => n))
            ProfileSlots.Add(new ProfileSlotItem(n, _profileNames.GetValueOrDefault(n)) { IsActive = active == n });
    }

    // ---- profile card: rename (dashboard-polish §5.4 — app-side label, the firmware has no names) ----
    private readonly Dictionary<byte, string> _profileNames;
    internal const int MaxProfileNameLength = 24;
    public bool IsRenamingProfile { get => _isRenaming; private set => Set(ref _isRenaming, value); }
    private bool _isRenaming;
    public string ProfileNameDraft { get => _nameDraft; set => Set(ref _nameDraft, value); }
    private string _nameDraft = "";

    public void BeginRename()
    {
        if (SelectedProfileSlot is not { } slot) return;
        ProfileNameDraft = slot.Name;
        IsRenamingProfile = true;
    }

    /// <summary>Commit the draft to the selected slot: trimmed, clamped to 24 chars; empty (or the
    /// literal default "Slot N") resets to default and drops the map entry. Persisted app-side via
    /// ApplyTo on the dashboard's save-on-close path, like DpiPresets.</summary>
    public void CommitRename()
    {
        if (!IsRenamingProfile) return;
        IsRenamingProfile = false;
        if (SelectedProfileSlot is not { } slot) return;
        var name = ProfileNameDraft.Trim();
        if (name.Length > MaxProfileNameLength) name = name[..MaxProfileNameLength];
        if (name.Length == 0 || name == ProfileSlotItem.DefaultName(slot.Number))
        {
            _profileNames.Remove(slot.Number);
            slot.Name = ProfileSlotItem.DefaultName(slot.Number);
        }
        else
        {
            _profileNames[slot.Number] = name;
            slot.Name = name;
        }
    }

    public void CancelRename() => IsRenamingProfile = false;

    /// <summary>The dropdown carries the active-slot identity and every slot is equal (v2.3), so
    /// the detail line is purely transient status — steady state is empty.</summary>
    private void RefreshProfileDetail() =>
        ProfileDetail = _activeSlot is null && _profileChecked ? "state unknown — mouse unreachable" : "";

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
        target.ProfileNames = new Dictionary<byte, string>(_profileNames);
    }

}
