using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Ui.Dashboard;

public sealed class DpiPresetItem : INotifyPropertyChanged
{
    private bool _isActive;
    public DpiPresetItem(int value, string colorHex) { Value = value; ColorHex = colorHex; }
    public int Value { get; }
    public string ColorHex { get; }
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value;
              PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DashboardViewModel : INotifyPropertyChanged
{
    // Fixed identification palette for preset dots, assigned by list index (theme-independent, §4.1).
    private static readonly string[] DotPalette =
        { "#E6A23C", "#5AA9FF", "#43D675", "#E5484D", "#B678FF", "#4DD0C7" };

    private readonly int? _slot;
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
        _pollSeconds = source.PollIntervalSeconds;
        _pollChargingSeconds = source.PollIntervalChargingSeconds;
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

        Presets = new ObservableCollection<DpiPresetItem>();
        foreach (int v in source.DpiPresets.Distinct().OrderBy(v => v)) Presets.Add(NewItem(v));
        RefreshDots();
        SetLiveness(_slot is null ? ProfileLivenessState.NotAdopted : ProfileLivenessState.Unchecked);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- callouts ----
    public IReadOnlyList<CalloutViewModel> Callouts { get; }
    public CalloutViewModel Callout(int position) => Callouts[position - 1];

    // ---- header ----
    public bool DeviceOnline { get => _deviceOnline; private set => Set(ref _deviceOnline, value); }
    public string StatusDotBrushKey { get => _statusDotBrushKey; private set => Set(ref _statusDotBrushKey, value); }
    public string HeaderSubtitle { get => _headerSubtitle; private set => Set(ref _headerSubtitle, value); }
    public string BatteryChipText { get => _batteryChipText; private set => Set(ref _batteryChipText, value); }

    public void ApplyState(DeviceState s)
    {
        DeviceOnline = s.Status == DeviceStatus.Online;
        StatusDotBrushKey = DeviceOnline ? "Status.Positive" : "Status.Critical";
        string link = !DeviceOnline ? "offline" : s.Wired ? "Wired" : "Wireless";
        string slot = _slot is { } n ? $" · Profile {n} · {SlotColour(n)}" : "";
        HeaderSubtitle = $"{link}{slot}";
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
              { Notify(nameof(DpiText)); RefreshActive(); } }
    }
    public string DpiText => DevicePresent ? Dpi.ToString() : "—";

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
        Presets.Insert(at, NewItem(value));
        RefreshDots();
        RefreshActive();
    }

    public void RemovePreset(DpiPresetItem item)
    {
        Presets.Remove(item);
        RefreshDots();
    }

    private DpiPresetItem NewItem(int v) => new(v, DotPalette[0]); // dot fixed in RefreshDots
    private void RefreshDots()
    {
        // ColorHex is by index — rebuild wrappers cheaply by replacing items whose color changed
        for (int i = 0; i < Presets.Count; i++)
        {
            string want = DotPalette[i % DotPalette.Length];
            if (Presets[i].ColorHex != want)
                Presets[i] = new DpiPresetItem(Presets[i].Value, want) { IsActive = Presets[i].IsActive };
        }
    }
    private void RefreshActive()
    { foreach (var p in Presets) p.IsActive = DevicePresent && p.Value == Dpi; }

    // ---- profile card ----
    public string ProfileTitle { get => _profileTitle; private set => Set(ref _profileTitle, value); }
    public string ProfileDetail { get => _profileDetail; private set => Set(ref _profileDetail, value); }

    public void SetLiveness(ProfileLivenessState state)
    {
        string identity = _slot is { } n ? $"Slot {n} · {SlotColour(n)}" : "";
        (ProfileTitle, ProfileDetail) = state switch
        {
            ProfileLivenessState.NotAdopted => ("No app profile yet", "Remap any button to create one."),
            ProfileLivenessState.Live => (identity, "● bindings live"),
            ProfileLivenessState.NotLive => (identity, "○ Mouse is on another profile — press the bottom button until the LED is " + (_slot is { } m ? SlotColour(m) : "right") + "."),
            ProfileLivenessState.Unknown => (identity, "state unknown — mouse unreachable"),
            _ => (identity, ""), // Unchecked: identity only, no liveness claim
        };
    }

    // ---- settings (overlay) ----
    public bool RunAtStartup { get => _runAtStartup; set => Set(ref _runAtStartup, value); }
    public bool LowBatteryNotify { get => _lowBatteryNotify; set => Set(ref _lowBatteryNotify, value); }
    public int LowBatteryThreshold { get => _lowBatteryThreshold; set => Set(ref _lowBatteryThreshold, value); }
    public int PollSeconds { get => _pollSeconds; set => Set(ref _pollSeconds, value); }
    public int PollChargingSeconds { get => _pollChargingSeconds; set => Set(ref _pollChargingSeconds, value); }
    public string Theme { get => _theme; set => Set(ref _theme, value); }
    public IReadOnlyList<string> ThemeNames => Ui.ThemeManager.PresetNames;

    /// <summary>Ports the old SettingsViewModel.ApplyTo clamps: cadence floor 15 s, threshold 1..100.</summary>
    public void ApplyTo(AppSettings target)
    {
        target.LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 1, 100);
        target.LowBatteryNotify = LowBatteryNotify;
        target.PollIntervalSeconds = Math.Max(15, PollSeconds);
        target.PollIntervalChargingSeconds = Math.Max(15, PollChargingSeconds);
        target.Theme = Ui.ThemeManager.Resolve(Theme);
        target.DpiPresets = Presets.Select(p => p.Value).ToList();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
    private void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
