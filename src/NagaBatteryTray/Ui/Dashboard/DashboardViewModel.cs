using System.Collections.ObjectModel;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Ui.Dashboard;

public enum ProfileLivenessState { NotAdopted, Unchecked, Unknown, Live, NotLive }

public sealed class DpiPresetItem : ObservableObject
{
    private bool _isActive;
    public DpiPresetItem(int value) { Value = value; }
    public int Value { get; }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
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
        ApplyProfileState(_slot is null ? ProfileLivenessState.NotAdopted : ProfileLivenessState.Unchecked);
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

    // ---- profile card (direct active-slot read, spec §13) ----
    private byte? _activeSlot;
    public string ProfileTitle { get => _profileTitle; private set => Set(ref _profileTitle, value); }
    public string ProfileDetail { get => _profileDetail; private set => Set(ref _profileDetail, value); }

    /// <summary>Activate is offered only when we KNOW the mouse is on another slot — adopted slot
    /// present, active slot read successfully, and they differ.</summary>
    public bool CanActivate { get => _canActivate; private set => Set(ref _canActivate, value); }
    private bool _canActivate;

    /// <summary>First remap on a fresh install adopts a slot AFTER this VM was built — update the
    /// identity the Profile card renders. Liveness deliberately resets to Unchecked (identity only,
    /// no claim): the mouse isn't necessarily ON the new slot until the user selects it.</summary>
    public void SetAdoptedSlot(int slot)
    {
        if (_slot == slot) return;
        _slot = slot;
        ApplyProfileState(ProfileLivenessState.Unchecked);
    }

    /// <summary>Feed the card a fresh 0x05/0x84 read (null = unreachable). Drives the whole
    /// state machine — Live/NotLive are slot equality now, not byte inference.</summary>
    public void SetActiveSlot(byte? active)
    {
        _activeSlot = active;
        ApplyProfileState(_slot is null ? ProfileLivenessState.NotAdopted
            : active is null ? ProfileLivenessState.Unknown
            : active == _slot ? ProfileLivenessState.Live
            : ProfileLivenessState.NotLive);
    }

    /// <summary>Transient card status ("Switching…", failure text) — detail line only.</summary>
    public void SetProfileNote(string note) => ProfileDetail = note;

    private void ApplyProfileState(ProfileLivenessState state)
    {
        string identity = _slot is { } n ? $"Slot {n} · {SlotColour(n)}" : "";
        (ProfileTitle, ProfileDetail) = state switch
        {
            ProfileLivenessState.NotAdopted => ("No app profile yet", "Remap any button to create one."),
            ProfileLivenessState.Live => (identity, "● live — active on the mouse"),
            ProfileLivenessState.NotLive => (identity, _activeSlot is { } m
                ? $"○ Mouse is on Slot {m} · {SlotColour(m)}" : "○ Mouse is on another profile"),
            ProfileLivenessState.Unknown => (identity, "state unknown — mouse unreachable"),
            _ => (identity, ""), // Unchecked: identity only, no claim
        };
        CanActivate = state == ProfileLivenessState.NotLive;
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
