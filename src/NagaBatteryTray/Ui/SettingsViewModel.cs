using System.ComponentModel;
using System.Runtime.CompilerServices;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Ui;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private int _lowBatteryThreshold;
    private int _pollSeconds;
    private int _pollChargingSeconds;
    private bool _runAtStartup;
    private int _dpi = RazerProtocol.DpiMin;
    private string _currentDpiText = "Current: unknown";
    private string _dpiStatus = "";
    private bool _devicePresent;

    public SettingsViewModel(AppSettings source, bool runAtStartup)
    {
        _lowBatteryThreshold = source.LowBatteryThreshold;
        _pollSeconds = source.PollIntervalSeconds;
        _pollChargingSeconds = source.PollIntervalChargingSeconds;
        _runAtStartup = runAtStartup;
    }

    public int LowBatteryThreshold { get => _lowBatteryThreshold; set => Set(ref _lowBatteryThreshold, value); }
    public int PollSeconds { get => _pollSeconds; set => Set(ref _pollSeconds, value); }
    public int PollChargingSeconds { get => _pollChargingSeconds; set => Set(ref _pollChargingSeconds, value); }
    public bool RunAtStartup { get => _runAtStartup; set => Set(ref _runAtStartup, value); }

    public int Dpi
    {
        get => _dpi;
        set => Set(ref _dpi, Math.Clamp(value, RazerProtocol.DpiMin, RazerProtocol.DpiMax));
    }

    public string CurrentDpiText { get => _currentDpiText; set => Set(ref _currentDpiText, value); }
    public string DpiStatus { get => _dpiStatus; set => Set(ref _dpiStatus, value); }
    public bool DevicePresent { get => _devicePresent; set => Set(ref _devicePresent, value); }

    /// <summary>Writes clamped edited values into the live settings instance (cadence floor 15 s, threshold 1..100).
    /// Leaves unedited fields (CachedTransactionId, SetReadDelayMs, LowBatteryNotify) untouched.</summary>
    public void ApplyTo(AppSettings target)
    {
        target.LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 1, 100);
        target.PollIntervalSeconds = Math.Max(15, PollSeconds);
        target.PollIntervalChargingSeconds = Math.Max(15, PollChargingSeconds);
    }

    public void SetCurrentDpi(DpiSetting? dpi)
    {
        if (dpi is { } d)
        {
            Dpi = d.X;
            CurrentDpiText = $"Current: {d.X} DPI";
            DevicePresent = true;
        }
        else
        {
            CurrentDpiText = "Current: unknown";
            DevicePresent = false;
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
